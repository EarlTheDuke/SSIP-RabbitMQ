namespace SSIP.Gateway.Routing;

/// <summary>
/// Resolves incoming requests to appropriate backend services.
/// Supports dynamic routing, load balancing, and service discovery.
/// </summary>
public interface IRouteResolver
{
    /// <summary>
    /// Resolves request path to backend service endpoint.
    /// </summary>
    /// <param name="request">The incoming HTTP request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Route match with target service info, or null if no match</returns>
    Task<RouteMatch?> ResolveAsync(HttpRequest request, CancellationToken ct = default);

    /// <summary>
    /// Registers a new route mapping.
    /// </summary>
    /// <param name="route">The route definition to register</param>
    /// <param name="ct">Cancellation token</param>
    Task RegisterRouteAsync(RouteDefinition route, CancellationToken ct = default);

    /// <summary>
    /// Removes a route mapping.
    /// </summary>
    /// <param name="routeId">The route ID to remove</param>
    /// <param name="ct">Cancellation token</param>
    Task UnregisterRouteAsync(string routeId, CancellationToken ct = default);

    /// <summary>
    /// Gets all registered routes.
    /// </summary>
    Task<IReadOnlyList<RouteDefinition>> GetAllRoutesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all registered routes for a specific service.
    /// </summary>
    /// <param name="serviceName">The service name</param>
    Task<IReadOnlyList<RouteDefinition>> GetRoutesForServiceAsync(string serviceName);

    /// <summary>
    /// Checks health of target service.
    /// </summary>
    /// <param name="serviceName">The service name</param>
    Task<ServiceHealth> CheckServiceHealthAsync(string serviceName);

    /// <summary>
    /// Reloads routes from configuration or database.
    /// </summary>
    Task ReloadRoutesAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of a route resolution.
/// </summary>
public record RouteMatch
{
    public required string RouteId { get; init; }
    public required string ServiceName { get; init; }
    public required Uri TargetUri { get; init; }
    public Dictionary<string, string> RouteParams { get; init; } = new();
    public Dictionary<string, string> QueryParams { get; init; } = new();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public RetryPolicy? RetryPolicy { get; init; }
    public bool PreserveHostHeader { get; init; }
    public Dictionary<string, string> AdditionalHeaders { get; init; } = new();
}

/// <summary>
/// Definition of a route mapping.
/// </summary>
public record RouteDefinition
{
    public required string RouteId { get; init; }
    public required string Pattern { get; init; }  // e.g., "/api/erp/{*path}"
    public required string ServiceName { get; init; }  // e.g., "erp-service"
    public required string TargetBaseUrl { get; init; }  // e.g., "http://erp-internal:5001"
    public string? TargetPathTemplate { get; init; }  // Transform path, e.g., "/v2/{path}"
    public string[] AllowedMethods { get; init; } = ["GET", "POST", "PUT", "DELETE", "PATCH"];
    public bool RequiresAuth { get; init; } = true;
    public string[]? RequiredScopes { get; init; }
    public string[]? RequiredRoles { get; init; }
    public int Priority { get; init; } = 100;  // Lower = higher priority
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public RetryPolicy? RetryPolicy { get; init; }
    public bool IsActive { get; init; } = true;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Retry policy for failed requests.
/// </summary>
public record RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public double BackoffMultiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int[] RetryOnStatusCodes { get; init; } = [502, 503, 504];
    public bool RetryOnTimeout { get; init; } = true;
}

/// <summary>
/// Health status of a service.
/// </summary>
public enum ServiceHealth
{
    Healthy,
    Degraded,
    Unhealthy,
    Unknown
}

/// <summary>
/// Detailed health check result.
/// </summary>
public record ServiceHealthResult
{
    public required string ServiceName { get; init; }
    public required ServiceHealth Status { get; init; }
    public string? Message { get; init; }
    public TimeSpan? ResponseTime { get; init; }
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Details { get; init; } = new();
}

