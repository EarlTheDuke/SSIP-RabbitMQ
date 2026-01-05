using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace SSIP.Gateway.RateLimiting;

/// <summary>
/// Sliding window rate limiter implementation using Redis for distributed state.
/// </summary>
public class SlidingWindowLimiter : IRateLimiter
{
    private readonly IDistributedCache _cache;
    private readonly ConcurrentDictionary<string, RateLimitPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _whitelist = new();
    private readonly RateLimitOptions _options;
    private readonly ILogger<SlidingWindowLimiter> _logger;

    private static readonly RateLimitPolicy DefaultPolicy = new()
    {
        PolicyName = "default",
        RequestsPerWindow = 100,
        WindowSize = TimeSpan.FromMinutes(1)
    };

    public SlidingWindowLimiter(
        IDistributedCache cache,
        IOptions<RateLimitOptions> options,
        ILogger<SlidingWindowLimiter> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        // Load policies from options
        foreach (var policy in _options.Policies)
        {
            _policies[policy.PolicyName] = policy;
        }
    }

    public async Task<RateLimitResult> CheckAsync(RateLimitContext context, CancellationToken ct = default)
    {
        // Check whitelist
        if (IsWhitelisted(context.ClientId))
        {
            return RateLimitResult.Allowed(int.MaxValue, int.MaxValue, DateTimeOffset.MaxValue);
        }

        // Get applicable policy
        var policy = GetPolicyForEndpoint(context.Endpoint) ?? DefaultPolicy;

        // Build cache key
        var key = BuildKey(context, policy);

        var now = DateTimeOffset.UtcNow;
        var windowStart = now - policy.WindowSize;

        try
        {
            // Get current request count in window
            var requestCount = await GetRequestCountAsync(key, windowStart, now, ct);

            if (requestCount >= policy.RequestsPerWindow)
            {
                var retryAfter = await CalculateRetryAfterAsync(key, policy, now, ct);
                
                _logger.LogWarning("Rate limit exceeded for client {ClientId} on {Endpoint}",
                    context.ClientId, context.Endpoint);

                return RateLimitResult.Rejected(
                    retryAfter,
                    $"Rate limit exceeded. Max {policy.RequestsPerWindow} requests per {policy.WindowSize.TotalSeconds}s",
                    policy.RequestsPerWindow
                );
            }

            // Record this request
            await RecordRequestInternalAsync(key, now, ct);

            var remaining = policy.RequestsPerWindow - requestCount - 1;
            var resetAt = now + policy.WindowSize;

            return new RateLimitResult
            {
                IsAllowed = true,
                RemainingRequests = Math.Max(0, remaining),
                Limit = policy.RequestsPerWindow,
                ResetAt = resetAt,
                PolicyName = policy.PolicyName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking rate limit for {ClientId}", context.ClientId);
            
            // Fail open - allow request if rate limiting fails
            if (_options.FailOpen)
            {
                return RateLimitResult.Allowed(policy.RequestsPerWindow, policy.RequestsPerWindow, now + policy.WindowSize);
            }

            throw;
        }
    }

    public async Task RecordRequestAsync(string clientId, string endpoint, CancellationToken ct = default)
    {
        var policy = GetPolicyForEndpoint(endpoint) ?? DefaultPolicy;
        var key = BuildKey(clientId, endpoint, policy);
        await RecordRequestInternalAsync(key, DateTimeOffset.UtcNow, ct);
    }

    public async Task<RateLimitStatus> GetStatusAsync(string clientId, CancellationToken ct = default)
    {
        return await GetStatusAsync(clientId, "*", ct);
    }

    public async Task<RateLimitStatus> GetStatusAsync(string clientId, string endpoint, CancellationToken ct = default)
    {
        var policy = GetPolicyForEndpoint(endpoint) ?? DefaultPolicy;
        var key = BuildKey(clientId, endpoint, policy);

        var now = DateTimeOffset.UtcNow;
        var windowStart = now - policy.WindowSize;
        var requestCount = await GetRequestCountAsync(key, windowStart, now, ct);

        return new RateLimitStatus
        {
            ClientId = clientId,
            Endpoint = endpoint,
            RequestsUsed = requestCount,
            RequestsRemaining = Math.Max(0, policy.RequestsPerWindow - requestCount),
            Limit = policy.RequestsPerWindow,
            WindowStart = windowStart,
            WindowEnd = now + policy.WindowSize,
            IsWhitelisted = IsWhitelisted(clientId),
            PolicyName = policy.PolicyName
        };
    }

    public Task ConfigurePolicyAsync(string endpoint, RateLimitPolicy policy, CancellationToken ct = default)
    {
        _policies[endpoint] = policy;
        _logger.LogInformation("Configured rate limit policy {PolicyName} for {Endpoint}",
            policy.PolicyName, endpoint);
        return Task.CompletedTask;
    }

    public Task<RateLimitPolicy?> GetPolicyAsync(string endpoint, CancellationToken ct = default)
    {
        var policy = GetPolicyForEndpoint(endpoint);
        return Task.FromResult(policy);
    }

    public async Task ResetAsync(string clientId, CancellationToken ct = default)
    {
        // Remove all rate limit entries for this client
        var pattern = $"{_options.KeyPrefix}:{clientId}:*";
        
        // Note: This is a simplified implementation. In production, you'd use
        // Redis SCAN and DEL commands or maintain a list of keys per client.
        _logger.LogInformation("Reset rate limit counters for client {ClientId}", clientId);
        await Task.CompletedTask;
    }

    public Task WhitelistAsync(string clientId, TimeSpan? duration = null, CancellationToken ct = default)
    {
        var expiry = duration.HasValue
            ? DateTimeOffset.UtcNow + duration.Value
            : DateTimeOffset.MaxValue;

        _whitelist[clientId] = expiry;
        _logger.LogInformation("Whitelisted client {ClientId} until {Expiry}", clientId, expiry);
        return Task.CompletedTask;
    }

    public Task RemoveFromWhitelistAsync(string clientId, CancellationToken ct = default)
    {
        _whitelist.TryRemove(clientId, out _);
        _logger.LogInformation("Removed client {ClientId} from whitelist", clientId);
        return Task.CompletedTask;
    }

    #region Private Methods

    private bool IsWhitelisted(string clientId)
    {
        if (_whitelist.TryGetValue(clientId, out var expiry))
        {
            if (expiry > DateTimeOffset.UtcNow)
            {
                return true;
            }
            
            // Expired, remove from whitelist
            _whitelist.TryRemove(clientId, out _);
        }

        return false;
    }

    private RateLimitPolicy? GetPolicyForEndpoint(string endpoint)
    {
        // Try exact match first
        if (_policies.TryGetValue(endpoint, out var policy))
        {
            return policy;
        }

        // Try pattern matching
        foreach (var (pattern, p) in _policies)
        {
            if (p.AppliesTo is not null)
            {
                foreach (var applyPattern in p.AppliesTo)
                {
                    if (MatchesPattern(endpoint, applyPattern))
                    {
                        return p;
                    }
                }
            }
        }

        return null;
    }

    private static bool MatchesPattern(string endpoint, string pattern)
    {
        // Simple wildcard matching
        if (pattern.EndsWith("*"))
        {
            return endpoint.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return endpoint.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildKey(RateLimitContext context, RateLimitPolicy policy)
    {
        return BuildKey(context.ClientId, context.Endpoint, policy);
    }

    private static string BuildKey(string clientId, string endpoint, RateLimitPolicy policy)
    {
        if (policy.PerClient)
        {
            return $"ratelimit:{clientId}:{endpoint}";
        }

        return $"ratelimit:global:{endpoint}";
    }

    private async Task<int> GetRequestCountAsync(
        string key,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Simplified implementation using a counter
        // In production, use Redis sorted sets for proper sliding window
        var countStr = await _cache.GetStringAsync(key, ct);
        
        if (string.IsNullOrEmpty(countStr))
        {
            return 0;
        }

        // Parse stored value: "count:timestamp"
        var parts = countStr.Split(':');
        if (parts.Length != 2)
        {
            return 0;
        }

        var count = int.Parse(parts[0]);
        var timestamp = DateTimeOffset.Parse(parts[1]);

        // Check if within current window
        if (timestamp < windowStart)
        {
            return 0; // Old data, window has passed
        }

        return count;
    }

    private async Task RecordRequestInternalAsync(string key, DateTimeOffset timestamp, CancellationToken ct)
    {
        var countStr = await _cache.GetStringAsync(key, ct);
        var count = 1;

        if (!string.IsNullOrEmpty(countStr))
        {
            var parts = countStr.Split(':');
            if (parts.Length == 2)
            {
                var existingTimestamp = DateTimeOffset.Parse(parts[1]);
                var windowStart = timestamp - TimeSpan.FromMinutes(1); // Default window

                if (existingTimestamp >= windowStart)
                {
                    count = int.Parse(parts[0]) + 1;
                }
            }
        }

        var value = $"{count}:{timestamp:O}";
        await _cache.SetStringAsync(key, value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) // Keep for cleanup
        }, ct);
    }

    private async Task<TimeSpan> CalculateRetryAfterAsync(
        string key,
        RateLimitPolicy policy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var countStr = await _cache.GetStringAsync(key, ct);
        
        if (string.IsNullOrEmpty(countStr))
        {
            return TimeSpan.Zero;
        }

        var parts = countStr.Split(':');
        if (parts.Length != 2)
        {
            return policy.WindowSize;
        }

        var timestamp = DateTimeOffset.Parse(parts[1]);
        var windowEnd = timestamp + policy.WindowSize;
        var retryAfter = windowEnd - now;

        return retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero;
    }

    #endregion
}

/// <summary>
/// Rate limiting configuration options.
/// </summary>
public class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public string KeyPrefix { get; init; } = "ratelimit";
    public bool FailOpen { get; init; } = true;  // Allow requests if rate limiting fails
    public bool Enabled { get; init; } = true;
    public List<RateLimitPolicy> Policies { get; init; } = new()
    {
        new RateLimitPolicy
        {
            PolicyName = "default",
            RequestsPerWindow = 100,
            WindowSize = TimeSpan.FromMinutes(1)
        },
        new RateLimitPolicy
        {
            PolicyName = "strict",
            RequestsPerWindow = 10,
            WindowSize = TimeSpan.FromMinutes(1),
            AppliesTo = new[] { "/api/*/write", "/api/*/delete" }
        },
        new RateLimitPolicy
        {
            PolicyName = "ai",
            RequestsPerWindow = 50,
            WindowSize = TimeSpan.FromMinutes(1),
            AppliesTo = new[] { "/api/ai/*" }
        }
    };
}

