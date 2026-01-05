namespace SSIP.Gateway.RateLimiting;

/// <summary>
/// Rate limiting service to protect backend systems from overload.
/// Supports per-client, per-endpoint, and global rate limits.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Checks if request is allowed under rate limit.
    /// </summary>
    /// <param name="context">The rate limit context</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Rate limit result indicating if request is allowed</returns>
    Task<RateLimitResult> CheckAsync(RateLimitContext context, CancellationToken ct = default);

    /// <summary>
    /// Records a request for rate tracking (usually called after successful processing).
    /// </summary>
    Task RecordRequestAsync(string clientId, string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Gets current rate limit status for client.
    /// </summary>
    Task<RateLimitStatus> GetStatusAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Gets current rate limit status for client and endpoint.
    /// </summary>
    Task<RateLimitStatus> GetStatusAsync(string clientId, string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Configures rate limit policy for endpoint.
    /// </summary>
    Task ConfigurePolicyAsync(string endpoint, RateLimitPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Gets the configured policy for an endpoint.
    /// </summary>
    Task<RateLimitPolicy?> GetPolicyAsync(string endpoint, CancellationToken ct = default);

    /// <summary>
    /// Resets rate limit counters for a client.
    /// </summary>
    Task ResetAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// Whitelists a client (bypasses rate limiting).
    /// </summary>
    Task WhitelistAsync(string clientId, TimeSpan? duration = null, CancellationToken ct = default);

    /// <summary>
    /// Removes client from whitelist.
    /// </summary>
    Task RemoveFromWhitelistAsync(string clientId, CancellationToken ct = default);
}

/// <summary>
/// Context for rate limit checking.
/// </summary>
public record RateLimitContext
{
    /// <summary>User ID or API key identifying the client</summary>
    public required string ClientId { get; init; }
    
    /// <summary>Request endpoint/path</summary>
    public required string Endpoint { get; init; }
    
    /// <summary>Client IP address for IP-based limiting</summary>
    public string? ClientIp { get; init; }
    
    /// <summary>Tenant ID for multi-tenant scenarios</summary>
    public string? TenantId { get; init; }
    
    /// <summary>HTTP method (GET, POST, etc.)</summary>
    public string? HttpMethod { get; init; }
    
    /// <summary>Request weight (for weighted rate limiting)</summary>
    public int Weight { get; init; } = 1;
}

/// <summary>
/// Result of a rate limit check.
/// </summary>
public record RateLimitResult
{
    /// <summary>Whether the request is allowed</summary>
    public required bool IsAllowed { get; init; }
    
    /// <summary>Remaining requests in current window</summary>
    public int RemainingRequests { get; init; }
    
    /// <summary>Total requests allowed in window</summary>
    public int Limit { get; init; }
    
    /// <summary>When the rate limit resets</summary>
    public DateTimeOffset? ResetAt { get; init; }
    
    /// <summary>How long to wait before retrying</summary>
    public TimeSpan RetryAfter { get; init; }
    
    /// <summary>Reason for rejection (if not allowed)</summary>
    public string? RejectReason { get; init; }
    
    /// <summary>Policy that was applied</summary>
    public string? PolicyName { get; init; }

    public static RateLimitResult Allowed(int remaining, int limit, DateTimeOffset resetAt) =>
        new()
        {
            IsAllowed = true,
            RemainingRequests = remaining,
            Limit = limit,
            ResetAt = resetAt
        };

    public static RateLimitResult Rejected(TimeSpan retryAfter, string reason, int limit) =>
        new()
        {
            IsAllowed = false,
            RemainingRequests = 0,
            Limit = limit,
            RetryAfter = retryAfter,
            RejectReason = reason
        };
}

/// <summary>
/// Current rate limit status for a client.
/// </summary>
public record RateLimitStatus
{
    public required string ClientId { get; init; }
    public string? Endpoint { get; init; }
    public int RequestsUsed { get; init; }
    public int RequestsRemaining { get; init; }
    public int Limit { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public bool IsWhitelisted { get; init; }
    public string? PolicyName { get; init; }
}

/// <summary>
/// Rate limit policy configuration.
/// </summary>
public record RateLimitPolicy
{
    public required string PolicyName { get; init; }
    
    /// <summary>Requests allowed per window</summary>
    public required int RequestsPerWindow { get; init; }
    
    /// <summary>Size of the time window</summary>
    public required TimeSpan WindowSize { get; init; }
    
    /// <summary>Type of rate limiting algorithm</summary>
    public RateLimitType Type { get; init; } = RateLimitType.SlidingWindow;
    
    /// <summary>Allow burst above limit (for token bucket)</summary>
    public int? BurstLimit { get; init; }
    
    /// <summary>Endpoint patterns this policy applies to</summary>
    public string[]? AppliesTo { get; init; }
    
    /// <summary>Clients to exclude from this policy</summary>
    public string[]? ExcludeClients { get; init; }
    
    /// <summary>HTTP methods this policy applies to</summary>
    public string[]? Methods { get; init; }
    
    /// <summary>Whether to apply per-client or globally</summary>
    public bool PerClient { get; init; } = true;
}

/// <summary>
/// Type of rate limiting algorithm.
/// </summary>
public enum RateLimitType
{
    /// <summary>Sliding window counter</summary>
    SlidingWindow,
    
    /// <summary>Fixed window counter</summary>
    FixedWindow,
    
    /// <summary>Token bucket (allows bursting)</summary>
    TokenBucket,
    
    /// <summary>Leaky bucket (smooths traffic)</summary>
    LeakyBucket,
    
    /// <summary>Concurrent requests limit</summary>
    Concurrency
}

