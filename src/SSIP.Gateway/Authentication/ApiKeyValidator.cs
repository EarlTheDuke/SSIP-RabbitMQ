using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace SSIP.Gateway.Authentication;

/// <summary>
/// API Key authentication handler for service-to-service communication.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private const string ApiKeyQueryName = "api_key";
    
    private readonly IAuthService _authService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService)
        : base(options, logger, encoder)
    {
        _authService = authService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Try to get API key from header first, then query string
        string? apiKey = null;

        if (Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerValue))
        {
            apiKey = headerValue.FirstOrDefault();
        }
        else if (Request.Query.TryGetValue(ApiKeyQueryName, out var queryValue))
        {
            apiKey = queryValue.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        var result = await _authService.ValidateApiKeyAsync(apiKey);

        if (!result.IsValid || result.Principal is null)
        {
            return AuthenticateResult.Fail(result.ErrorMessage ?? "Invalid API key");
        }

        var ticket = new AuthenticationTicket(result.Principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Append("WWW-Authenticate", $"ApiKey realm=\"{Options.Realm}\"");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Options for API key authentication.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public string Realm { get; set; } = "SSIP Gateway";
}

/// <summary>
/// Extension methods for API key authentication registration.
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    public static AuthenticationBuilder AddApiKeyAuthentication(
        this AuthenticationBuilder builder,
        Action<ApiKeyAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationOptions.DefaultScheme,
            configure ?? (_ => { }));
    }
}

