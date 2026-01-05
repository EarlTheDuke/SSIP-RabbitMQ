using System.Security.Claims;

namespace SSIP.Gateway.Authentication.Models;

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public record AuthResult
{
    public bool IsValid { get; init; }
    public ClaimsPrincipal? Principal { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }

    public static AuthResult Success(ClaimsPrincipal principal) =>
        new() { IsValid = true, Principal = principal };

    public static AuthResult Failure(string message, string? errorCode = null) =>
        new() { IsValid = false, ErrorMessage = message, ErrorCode = errorCode };
}

/// <summary>
/// Response containing JWT tokens.
/// </summary>
public record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    DateTime RefreshExpiresAt,
    IReadOnlyList<string> Scopes,
    string TokenType = "Bearer"
);

/// <summary>
/// User credentials for authentication.
/// </summary>
public record UserCredentials(
    string Username,
    string Password,
    string? TenantId = null,
    IReadOnlyList<string>? RequestedScopes = null
);

/// <summary>
/// Information about an authenticated user.
/// </summary>
public record UserInfo
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// API key information for service-to-service auth.
/// </summary>
public record ApiKeyInfo
{
    public required string KeyId { get; init; }
    public required string ServiceName { get; init; }
    public required string HashedKey { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTime? ExpiresAt { get; init; }
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];
    public IReadOnlyList<string> AllowedEndpoints { get; init; } = [];
    public int? RateLimitPerMinute { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}

/// <summary>
/// Refresh token information stored in database/cache.
/// </summary>
public record RefreshTokenInfo
{
    public required string TokenId { get; init; }
    public required string UserId { get; init; }
    public required string HashedToken { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? DeviceInfo { get; init; }
    public string? IpAddress { get; init; }
    public bool IsRevoked { get; init; }
    public DateTime? RevokedAt { get; init; }
}

