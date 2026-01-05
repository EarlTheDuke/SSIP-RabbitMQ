using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SSIP.Gateway.Routing;

/// <summary>
/// Service registry interface for service discovery.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>
    /// Gets the URL for a service, with load balancing.
    /// </summary>
    Task<string?> GetServiceUrlAsync(string serviceName, CancellationToken ct = default);

    /// <summary>
    /// Registers a service instance.
    /// </summary>
    Task RegisterServiceAsync(ServiceInstance instance, CancellationToken ct = default);

    /// <summary>
    /// Deregisters a service instance.
    /// </summary>
    Task DeregisterServiceAsync(string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Gets all instances for a service.
    /// </summary>
    Task<IReadOnlyList<ServiceInstance>> GetInstancesAsync(string serviceName, CancellationToken ct = default);

    /// <summary>
    /// Gets route definitions from configuration/database.
    /// </summary>
    Task<IReadOnlyList<RouteDefinition>> GetRouteDefinitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates service instance health.
    /// </summary>
    Task UpdateHealthAsync(string instanceId, ServiceHealth health, CancellationToken ct = default);
}

/// <summary>
/// In-memory service registry with round-robin load balancing.
/// </summary>
public class ServiceRegistry : IServiceRegistry
{
    private readonly ConcurrentDictionary<string, List<ServiceInstance>> _services = new();
    private readonly ConcurrentDictionary<string, int> _roundRobinCounters = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceRegistry> _logger;

    public ServiceRegistry(IConfiguration configuration, ILogger<ServiceRegistry> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Load initial services from configuration
        LoadServicesFromConfiguration();
    }

    public Task<string?> GetServiceUrlAsync(string serviceName, CancellationToken ct = default)
    {
        if (!_services.TryGetValue(serviceName, out var instances) || instances.Count == 0)
        {
            _logger.LogWarning("No instances found for service {ServiceName}", serviceName);
            return Task.FromResult<string?>(null);
        }

        // Get healthy instances
        var healthyInstances = instances.Where(i => i.IsHealthy).ToList();
        if (healthyInstances.Count == 0)
        {
            _logger.LogWarning("No healthy instances for service {ServiceName}", serviceName);
            // Fall back to any instance
            healthyInstances = instances;
        }

        // Round-robin selection
        var counter = _roundRobinCounters.AddOrUpdate(serviceName, 0, (_, c) => c + 1);
        var selectedInstance = healthyInstances[counter % healthyInstances.Count];

        _logger.LogDebug("Selected instance {InstanceId} for service {ServiceName}",
            selectedInstance.InstanceId, serviceName);

        return Task.FromResult<string?>(selectedInstance.BaseUrl);
    }

    public Task RegisterServiceAsync(ServiceInstance instance, CancellationToken ct = default)
    {
        var instances = _services.GetOrAdd(instance.ServiceName, _ => new List<ServiceInstance>());
        
        lock (instances)
        {
            // Remove existing instance with same ID
            instances.RemoveAll(i => i.InstanceId == instance.InstanceId);
            instances.Add(instance);
        }

        _logger.LogInformation("Registered service instance {InstanceId} for {ServiceName}",
            instance.InstanceId, instance.ServiceName);

        return Task.CompletedTask;
    }

    public Task DeregisterServiceAsync(string instanceId, CancellationToken ct = default)
    {
        foreach (var service in _services.Values)
        {
            lock (service)
            {
                service.RemoveAll(i => i.InstanceId == instanceId);
            }
        }

        _logger.LogInformation("Deregistered service instance {InstanceId}", instanceId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceInstance>> GetInstancesAsync(string serviceName, CancellationToken ct = default)
    {
        if (_services.TryGetValue(serviceName, out var instances))
        {
            return Task.FromResult<IReadOnlyList<ServiceInstance>>(instances.ToList());
        }

        return Task.FromResult<IReadOnlyList<ServiceInstance>>([]);
    }

    public Task<IReadOnlyList<RouteDefinition>> GetRouteDefinitionsAsync(CancellationToken ct = default)
    {
        var routes = new List<RouteDefinition>();

        // Load from configuration
        var routeConfigs = _configuration.GetSection("Gateway:Routes").GetChildren();
        foreach (var routeConfig in routeConfigs)
        {
            var route = new RouteDefinition
            {
                RouteId = routeConfig["RouteId"] ?? Guid.NewGuid().ToString(),
                Pattern = routeConfig["Pattern"] ?? "/",
                ServiceName = routeConfig["ServiceName"] ?? "unknown",
                TargetBaseUrl = routeConfig["TargetBaseUrl"] ?? "http://localhost",
                TargetPathTemplate = routeConfig["TargetPathTemplate"],
                AllowedMethods = routeConfig.GetSection("AllowedMethods").Get<string[]>() ?? ["GET", "POST", "PUT", "DELETE"],
                RequiresAuth = routeConfig.GetValue<bool>("RequiresAuth", true),
                RequiredScopes = routeConfig.GetSection("RequiredScopes").Get<string[]>(),
                Priority = routeConfig.GetValue<int>("Priority", 100),
                Timeout = TimeSpan.FromSeconds(routeConfig.GetValue<int>("TimeoutSeconds", 30)),
                IsActive = routeConfig.GetValue<bool>("IsActive", true)
            };

            routes.Add(route);
        }

        // Add default SSIP routes if none configured
        if (routes.Count == 0)
        {
            routes.AddRange(GetDefaultRoutes());
        }

        return Task.FromResult<IReadOnlyList<RouteDefinition>>(routes);
    }

    public Task UpdateHealthAsync(string instanceId, ServiceHealth health, CancellationToken ct = default)
    {
        foreach (var service in _services.Values)
        {
            lock (service)
            {
                var instance = service.FirstOrDefault(i => i.InstanceId == instanceId);
                if (instance != null)
                {
                    var index = service.IndexOf(instance);
                    service[index] = instance with { IsHealthy = health == ServiceHealth.Healthy };
                    break;
                }
            }
        }

        return Task.CompletedTask;
    }

    #region Private Methods

    private void LoadServicesFromConfiguration()
    {
        var serviceConfigs = _configuration.GetSection("Gateway:Services").GetChildren();
        foreach (var serviceConfig in serviceConfigs)
        {
            var instance = new ServiceInstance
            {
                InstanceId = Guid.NewGuid().ToString(),
                ServiceName = serviceConfig["Name"] ?? "unknown",
                BaseUrl = serviceConfig["BaseUrl"] ?? "http://localhost",
                IsHealthy = true,
                RegisteredAt = DateTime.UtcNow
            };

            _ = RegisterServiceAsync(instance);
        }

        // Add default services if none configured
        if (!_services.Any())
        {
            AddDefaultServices();
        }
    }

    private void AddDefaultServices()
    {
        // Default SSIP services
        var defaultServices = new[]
        {
            new ServiceInstance
            {
                InstanceId = "erp-1",
                ServiceName = "erp-service",
                BaseUrl = "http://localhost:5001",
                IsHealthy = true,
                RegisteredAt = DateTime.UtcNow,
                Metadata = new() { ["version"] = "1.0", ["environment"] = "development" }
            },
            new ServiceInstance
            {
                InstanceId = "tinybox-1",
                ServiceName = "tinybox-ai",
                BaseUrl = "http://localhost:5002",
                IsHealthy = true,
                RegisteredAt = DateTime.UtcNow
            },
            new ServiceInstance
            {
                InstanceId = "crm-connector-1",
                ServiceName = "crm-connector",
                BaseUrl = "http://localhost:5003",
                IsHealthy = true,
                RegisteredAt = DateTime.UtcNow
            }
        };

        foreach (var service in defaultServices)
        {
            _ = RegisterServiceAsync(service);
        }
    }

    private static IEnumerable<RouteDefinition> GetDefaultRoutes()
    {
        return
        [
            // ERP Routes
            new RouteDefinition
            {
                RouteId = "erp-api",
                Pattern = "/api/erp/{*path}",
                ServiceName = "erp-service",
                TargetBaseUrl = "http://localhost:5001",
                TargetPathTemplate = "/api/{path}",
                RequiresAuth = true,
                RequiredScopes = ["erp.read", "erp.write"],
                Priority = 100
            },
            // TinyBox AI Routes
            new RouteDefinition
            {
                RouteId = "ai-inference",
                Pattern = "/api/ai/{*path}",
                ServiceName = "tinybox-ai",
                TargetBaseUrl = "http://localhost:5002",
                TargetPathTemplate = "/{path}",
                RequiresAuth = true,
                RequiredScopes = ["ai.inference"],
                Priority = 100
            },
            // CRM Connector Routes
            new RouteDefinition
            {
                RouteId = "crm-api",
                Pattern = "/api/crm/{*path}",
                ServiceName = "crm-connector",
                TargetBaseUrl = "http://localhost:5003",
                TargetPathTemplate = "/api/{path}",
                RequiresAuth = true,
                RequiredScopes = ["crm.read", "crm.write"],
                Priority = 100
            },
            // Health Check (no auth)
            new RouteDefinition
            {
                RouteId = "health",
                Pattern = "/health",
                ServiceName = "gateway",
                TargetBaseUrl = "http://localhost:5000",
                RequiresAuth = false,
                Priority = 1
            }
        ];
    }

    #endregion
}

/// <summary>
/// Represents a service instance in the registry.
/// </summary>
public record ServiceInstance
{
    public required string InstanceId { get; init; }
    public required string ServiceName { get; init; }
    public required string BaseUrl { get; init; }
    public bool IsHealthy { get; init; } = true;
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastHealthCheck { get; init; }
    public int Weight { get; init; } = 100;  // For weighted load balancing
    public Dictionary<string, string> Metadata { get; init; } = new();
    public string[] Tags { get; init; } = [];
}

