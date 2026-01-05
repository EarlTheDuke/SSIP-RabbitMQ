using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SSIP.Gateway.Authentication.Models;

namespace SSIP.Gateway.Authentication;

/// <summary>
/// JWT-based authentication service implementation.
/// </summary>
public class JwtAuthService : IAuthService
{
    private readonly JwtSettings _settings;
    private readonly IDistributedCache _cache;
    private readonly ILogger<JwtAuthService> _logger;
    private readonly TokenValidationParameters _validationParameters;

    public JwtAuthService(
        IOptions<JwtSettings> settings,
        IDistributedCache cache,
        ILogger<JwtAuthService> logger)
    {
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _settings.Issuer,
            ValidAudience = _settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }

    public async Task<AuthResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(token, _validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return AuthResult.Failure("Invalid token format", "INVALID_TOKEN_FORMAT");
            }

            // Check if token is blacklisted (revoked)
            var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;
            if (!string.IsNullOrEmpty(jti))
            {
                var isBlacklisted = await _cache.GetStringAsync($"token:blacklist:{jti}", ct);
                if (!string.IsNullOrEmpty(isBlacklisted))
                {
                    return AuthResult.Failure("Token has been revoked", "TOKEN_REVOKED");
                }
            }

            _logger.LogDebug("Token validated successfully for user {UserId}",
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);

            return AuthResult.Success(principal);
        }
        catch (SecurityTokenExpiredException)
        {
            return AuthResult.Failure("Token has expired", "TOKEN_EXPIRED");
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return AuthResult.Failure("Invalid token", "INVALID_TOKEN");
        }
    }

    public async Task<AuthResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            // Hash the incoming API key for comparison
            var hashedKey = HashApiKey(apiKey);

            // Look up API key in cache or database
            var keyInfoJson = await _cache.GetStringAsync($"apikey:{hashedKey}", ct);
            if (string.IsNullOrEmpty(keyInfoJson))
            {
                return AuthResult.Failure("Invalid API key", "INVALID_API_KEY");
            }

            var keyInfo = System.Text.Json.JsonSerializer.Deserialize<ApiKeyInfo>(keyInfoJson);
            if (keyInfo is null || !keyInfo.IsActive)
            {
                return AuthResult.Failure("API key is inactive", "INACTIVE_API_KEY");
            }

            if (keyInfo.ExpiresAt.HasValue && keyInfo.ExpiresAt.Value < DateTime.UtcNow)
            {
                return AuthResult.Failure("API key has expired", "EXPIRED_API_KEY");
            }

            // Create claims for the service
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, keyInfo.KeyId),
                new(ClaimTypes.Name, keyInfo.ServiceName),
                new("auth_type", "api_key"),
                new("service_name", keyInfo.ServiceName)
            };

            foreach (var scope in keyInfo.AllowedScopes)
            {
                claims.Add(new Claim("scope", scope));
            }

            var identity = new ClaimsIdentity(claims, "ApiKey");
            var principal = new ClaimsPrincipal(identity);

            // Update last used timestamp (fire and forget)
            _ = UpdateApiKeyLastUsed(keyInfo.KeyId, ct);

            return AuthResult.Success(principal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating API key");
            return AuthResult.Failure("Error validating API key", "VALIDATION_ERROR");
        }
    }

    public async Task<TokenResponse> GenerateTokenAsync(UserCredentials credentials, CancellationToken ct = default)
    {
        // In production, validate credentials against user store
        // This is a simplified implementation
        var userId = await ValidateCredentialsAsync(credentials, ct);
        if (string.IsNullOrEmpty(userId))
        {
            throw new UnauthorizedAccessException("Invalid credentials");
        }

        var userRoles = await GetUserRolesAsync(userId, ct);
        var userPermissions = await GetUserPermissionsAsync(userId, ct);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(ClaimTypes.Name, credentials.Username),
            new(ClaimTypes.Email, credentials.Username), // Assuming username is email
        };

        if (!string.IsNullOrEmpty(credentials.TenantId))
        {
            claims.Add(new Claim("tenant_id", credentials.TenantId));
        }

        foreach (var role in userRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var permission in userPermissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        var scopes = credentials.RequestedScopes ?? _settings.DefaultScopes;
        foreach (var scope in scopes)
        {
            claims.Add(new Claim("scope", scope));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var accessTokenExpiry = DateTime.UtcNow.AddMinutes(_settings.AccessTokenExpirationMinutes);
        var refreshTokenExpiry = DateTime.UtcNow.AddDays(_settings.RefreshTokenExpirationDays);

        var accessToken = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: accessTokenExpiry,
            signingCredentials: creds
        );

        var refreshToken = GenerateRefreshToken();

        // Store refresh token
        var refreshTokenInfo = new RefreshTokenInfo
        {
            TokenId = Guid.NewGuid().ToString(),
            UserId = userId,
            HashedToken = HashRefreshToken(refreshToken),
            ExpiresAt = refreshTokenExpiry,
            CreatedAt = DateTime.UtcNow
        };

        await _cache.SetStringAsync(
            $"refresh:{refreshTokenInfo.HashedToken}",
            System.Text.Json.JsonSerializer.Serialize(refreshTokenInfo),
            new DistributedCacheEntryOptions { AbsoluteExpiration = refreshTokenExpiry },
            ct
        );

        _logger.LogInformation("Generated tokens for user {UserId}", userId);

        return new TokenResponse(
            AccessToken: new JwtSecurityTokenHandler().WriteToken(accessToken),
            RefreshToken: refreshToken,
            ExpiresAt: accessTokenExpiry,
            RefreshExpiresAt: refreshTokenExpiry,
            Scopes: scopes.ToList()
        );
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var hashedToken = HashRefreshToken(refreshToken);
        var tokenInfoJson = await _cache.GetStringAsync($"refresh:{hashedToken}", ct);

        if (string.IsNullOrEmpty(tokenInfoJson))
        {
            throw new UnauthorizedAccessException("Invalid refresh token");
        }

        var tokenInfo = System.Text.Json.JsonSerializer.Deserialize<RefreshTokenInfo>(tokenInfoJson);
        if (tokenInfo is null || tokenInfo.IsRevoked || tokenInfo.ExpiresAt < DateTime.UtcNow)
        {
            throw new UnauthorizedAccessException("Refresh token is invalid or expired");
        }

        // Revoke old refresh token
        await _cache.RemoveAsync($"refresh:{hashedToken}", ct);

        // Generate new tokens using stored user info
        return await GenerateTokenAsync(new UserCredentials(tokenInfo.UserId, ""), ct);
    }

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, string resource, string action)
    {
        var permissions = user.FindAll("permission").Select(c => c.Value).ToList();
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        var requiredPermission = $"{resource}:{action}";
        var wildcardPermission = $"{resource}:*";
        var adminPermission = "*:*";

        // Check direct permission
        if (permissions.Contains(requiredPermission) ||
            permissions.Contains(wildcardPermission) ||
            permissions.Contains(adminPermission))
        {
            return true;
        }

        // Check role-based permissions from cache/database
        foreach (var role in roles)
        {
            var rolePermissions = await GetRolePermissionsAsync(role);
            if (rolePermissions.Contains(requiredPermission) ||
                rolePermissions.Contains(wildcardPermission) ||
                rolePermissions.Contains(adminPermission))
            {
                return true;
            }
        }

        return false;
    }

    public async Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var hashedToken = HashRefreshToken(refreshToken);
        await _cache.RemoveAsync($"refresh:{hashedToken}", ct);
        _logger.LogInformation("Refresh token revoked");
    }

    public UserInfo GetUserInfo(ClaimsPrincipal user)
    {
        return new UserInfo
        {
            UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
            Username = user.FindFirst(ClaimTypes.Name)?.Value ?? "",
            Email = user.FindFirst(ClaimTypes.Email)?.Value,
            DisplayName = user.FindFirst("display_name")?.Value,
            TenantId = user.FindFirst("tenant_id")?.Value,
            Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
            Permissions = user.FindAll("permission").Select(c => c.Value).ToList(),
            Claims = user.Claims.ToDictionary(c => c.Type, c => c.Value)
        };
    }

    #region Private Methods

    private static string HashApiKey(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToBase64String(hashedBytes);
    }

    private static string HashRefreshToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hashedBytes);
    }

    private static string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    private Task<string?> ValidateCredentialsAsync(UserCredentials credentials, CancellationToken ct)
    {
        // TODO: Implement actual credential validation against user store
        // This is a placeholder implementation
        return Task.FromResult<string?>(Guid.NewGuid().ToString());
    }

    private Task<IReadOnlyList<string>> GetUserRolesAsync(string userId, CancellationToken ct)
    {
        // TODO: Implement actual role retrieval from database
        return Task.FromResult<IReadOnlyList<string>>(["User"]);
    }

    private Task<IReadOnlyList<string>> GetUserPermissionsAsync(string userId, CancellationToken ct)
    {
        // TODO: Implement actual permission retrieval from database
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    private Task<IReadOnlyList<string>> GetRolePermissionsAsync(string role)
    {
        // TODO: Implement actual role-permission mapping from database
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    private async Task UpdateApiKeyLastUsed(string keyId, CancellationToken ct)
    {
        // Update last used timestamp in background
        await Task.CompletedTask;
    }

    #endregion
}

/// <summary>
/// JWT configuration settings.
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    public required string SecretKey { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public int AccessTokenExpirationMinutes { get; init; } = 60;
    public int RefreshTokenExpirationDays { get; init; } = 7;
    public IReadOnlyList<string> DefaultScopes { get; init; } = ["api.read", "api.write"];
}

