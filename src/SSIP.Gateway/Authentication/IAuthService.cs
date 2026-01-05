using System.Security.Claims;
using SSIP.Gateway.Authentication.Models;

namespace SSIP.Gateway.Authentication;

/// <summary>
/// Core authentication service for the SSIP API Gateway.
/// Handles JWT tokens, API keys, and OAuth 2.0 flows.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Validates JWT bearer token and returns authentication result with claims principal.
    /// </summary>
    /// <param name="token">The JWT bearer token to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication result with claims if valid</returns>
    Task<AuthResult> ValidateTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Validates API key for service-to-service authentication.
    /// </summary>
    /// <param name="apiKey">The API key to validate</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication result with service claims if valid</returns>
    Task<AuthResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>
    /// Generates new JWT token for authenticated user.
    /// </summary>
    /// <param name="credentials">User credentials for authentication</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Token response with access and refresh tokens</returns>
    Task<TokenResponse> GenerateTokenAsync(UserCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Refreshes expired token using refresh token.
    /// </summary>
    /// <param name="refreshToken">The refresh token</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>New token response</returns>
    Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>
    /// Checks if user has required permission for resource.
    /// </summary>
    /// <param name="user">The authenticated user's claims principal</param>
    /// <param name="resource">The resource being accessed</param>
    /// <param name="action">The action being performed (read, write, delete)</param>
    /// <returns>True if user has permission</returns>
    Task<bool> HasPermissionAsync(ClaimsPrincipal user, string resource, string action);

    /// <summary>
    /// Revokes a refresh token (logout).
    /// </summary>
    /// <param name="refreshToken">The refresh token to revoke</param>
    /// <param name="ct">Cancellation token</param>
    Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>
    /// Gets user information from claims principal.
    /// </summary>
    /// <param name="user">The claims principal</param>
    /// <returns>User information</returns>
    UserInfo GetUserInfo(ClaimsPrincipal user);
}

