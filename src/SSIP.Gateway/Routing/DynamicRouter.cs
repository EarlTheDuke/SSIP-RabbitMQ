using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SSIP.Gateway.Routing;

/// <summary>
/// Dynamic route resolver with pattern matching and service discovery.
/// </summary>
public class DynamicRouter : IRouteResolver
{
    private readonly ConcurrentDictionary<string, RouteDefinition> _routes = new();
    private readonly ConcurrentDictionary<string, ServiceHealthResult> _healthCache = new();
    private readonly IServiceRegistry _serviceRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DynamicRouter> _logger;
    private readonly RoutingOptions _options;

    // Compiled regex patterns for route matching
    private readonly ConcurrentDictionary<string, Regex> _compiledPatterns = new();

    public DynamicRouter(
        IServiceRegistry serviceRegistry,
        IHttpClientFactory httpClientFactory,
        IOptions<RoutingOptions> options,
        ILogger<DynamicRouter> logger)
    {
        _serviceRegistry = serviceRegistry;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RouteMatch?> ResolveAsync(HttpRequest request, CancellationToken ct = default)
    {
        var path = request.Path.Value ?? "/";
        var method = request.Method;

        _logger.LogDebug("Resolving route for {Method} {Path}", method, path);

        // Get routes sorted by priority
        var activeRoutes = _routes.Values
            .Where(r => r.IsActive)
            .OrderBy(r => r.Priority)
            .ToList();

        foreach (var route in activeRoutes)
        {
            // Check HTTP method
            if (!route.AllowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Try to match pattern
            var match = TryMatchPattern(route.Pattern, path);
            if (match is null)
            {
                continue;
            }

            _logger.LogDebug("Route matched: {RouteId} -> {ServiceName}", route.RouteId, route.ServiceName);

            // Get target URL with load balancing
            var targetBaseUrl = await _serviceRegistry.GetServiceUrlAsync(route.ServiceName, ct)
                ?? route.TargetBaseUrl;

            // Build target URI
            var targetPath = BuildTargetPath(route, path, match);
            var targetUri = new Uri(new Uri(targetBaseUrl), targetPath);

            // Add query string if present
            if (request.QueryString.HasValue)
            {
                var uriBuilder = new UriBuilder(targetUri)
                {
                    Query = request.QueryString.Value
                };
                targetUri = uriBuilder.Uri;
            }

            return new RouteMatch
            {
                RouteId = route.RouteId,
                ServiceName = route.ServiceName,
                TargetUri = targetUri,
                RouteParams = match,
                Timeout = route.Timeout,
                RetryPolicy = route.RetryPolicy
            };
        }

        _logger.LogWarning("No route found for {Method} {Path}", method, path);
        return null;
    }

    public Task RegisterRouteAsync(RouteDefinition route, CancellationToken ct = default)
    {
        _routes[route.RouteId] = route;
        
        // Compile and cache the regex pattern
        var regex = BuildRouteRegex(route.Pattern);
        _compiledPatterns[route.RouteId] = regex;

        _logger.LogInformation("Registered route {RouteId}: {Pattern} -> {ServiceName}",
            route.RouteId, route.Pattern, route.ServiceName);

        return Task.CompletedTask;
    }

    public Task UnregisterRouteAsync(string routeId, CancellationToken ct = default)
    {
        _routes.TryRemove(routeId, out _);
        _compiledPatterns.TryRemove(routeId, out _);

        _logger.LogInformation("Unregistered route {RouteId}", routeId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RouteDefinition>> GetAllRoutesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<RouteDefinition>>(_routes.Values.ToList());
    }

    public Task<IReadOnlyList<RouteDefinition>> GetRoutesForServiceAsync(string serviceName)
    {
        var routes = _routes.Values
            .Where(r => r.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<RouteDefinition>>(routes);
    }

    public async Task<ServiceHealth> CheckServiceHealthAsync(string serviceName)
    {
        // Check cache first
        if (_healthCache.TryGetValue(serviceName, out var cached) &&
            cached.CheckedAt > DateTime.UtcNow.AddSeconds(-30))
        {
            return cached.Status;
        }

        try
        {
            var serviceUrl = await _serviceRegistry.GetServiceUrlAsync(serviceName);
            if (string.IsNullOrEmpty(serviceUrl))
            {
                return ServiceHealth.Unknown;
            }

            var client = _httpClientFactory.CreateClient("HealthCheck");
            client.Timeout = TimeSpan.FromSeconds(5);

            var healthUrl = new Uri(new Uri(serviceUrl), "/health");
            var startTime = DateTime.UtcNow;
            var response = await client.GetAsync(healthUrl);
            var responseTime = DateTime.UtcNow - startTime;

            var status = response.IsSuccessStatusCode ? ServiceHealth.Healthy : ServiceHealth.Degraded;

            _healthCache[serviceName] = new ServiceHealthResult
            {
                ServiceName = serviceName,
                Status = status,
                ResponseTime = responseTime,
                CheckedAt = DateTime.UtcNow
            };

            return status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for service {ServiceName}", serviceName);

            _healthCache[serviceName] = new ServiceHealthResult
            {
                ServiceName = serviceName,
                Status = ServiceHealth.Unhealthy,
                Message = ex.Message,
                CheckedAt = DateTime.UtcNow
            };

            return ServiceHealth.Unhealthy;
        }
    }

    public async Task ReloadRoutesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reloading routes from configuration");

        // Load routes from configuration or database
        var routes = await _serviceRegistry.GetRouteDefinitionsAsync(ct);

        _routes.Clear();
        _compiledPatterns.Clear();

        foreach (var route in routes)
        {
            await RegisterRouteAsync(route, ct);
        }

        _logger.LogInformation("Loaded {Count} routes", routes.Count);
    }

    #region Private Methods

    private Dictionary<string, string>? TryMatchPattern(string pattern, string path)
    {
        var regex = _compiledPatterns.GetOrAdd(pattern, BuildRouteRegex);
        var match = regex.Match(path);

        if (!match.Success)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>();
        foreach (var groupName in regex.GetGroupNames())
        {
            if (!int.TryParse(groupName, out _) && match.Groups[groupName].Success)
            {
                parameters[groupName] = match.Groups[groupName].Value;
            }
        }

        return parameters;
    }

    private static Regex BuildRouteRegex(string pattern)
    {
        // Convert route pattern to regex
        // /api/erp/{id} -> ^/api/erp/(?<id>[^/]+)$
        // /api/erp/{*path} -> ^/api/erp/(?<path>.*)$

        var regexPattern = pattern
            .Replace("/", "\\/")
            .Replace("{*", "(?<")
            .Replace("{", "(?<")
            .Replace("}", ">[^/]+)");

        // Fix catch-all parameter
        regexPattern = Regex.Replace(regexPattern, @"\(\?<(\w+)>\[\^/\]\+\)\.\*", "(?<$1>.*)");

        return new Regex($"^{regexPattern}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private static string BuildTargetPath(RouteDefinition route, string originalPath, Dictionary<string, string> routeParams)
    {
        if (string.IsNullOrEmpty(route.TargetPathTemplate))
        {
            // If catch-all parameter exists, use it
            if (routeParams.TryGetValue("path", out var catchAllPath))
            {
                return "/" + catchAllPath;
            }
            return originalPath;
        }

        // Apply template substitution
        var targetPath = route.TargetPathTemplate;
        foreach (var param in routeParams)
        {
            targetPath = targetPath.Replace($"{{{param.Key}}}", param.Value);
        }

        return targetPath;
    }

    #endregion
}

/// <summary>
/// Routing configuration options.
/// </summary>
public class RoutingOptions
{
    public const string SectionName = "Routing";

    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool EnableHealthChecks { get; init; } = true;
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    public bool EnableLoadBalancing { get; init; } = true;
}

