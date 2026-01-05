using System.Diagnostics;

namespace SSIP.Gateway.Middleware;

/// <summary>
/// Middleware for structured request/response logging.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        // Add correlation ID to log context
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path.Value ?? "/",
            ["RequestMethod"] = context.Request.Method,
            ["ClientIp"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
        });

        var stopwatch = Stopwatch.StartNew();

        // Log request
        LogRequest(context);

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            
            // Log response
            LogResponse(context, stopwatch.Elapsed);
        }
    }

    private void LogRequest(HttpContext context)
    {
        var logLevel = GetRequestLogLevel(context.Request.Path);

        _logger.Log(logLevel,
            "HTTP {Method} {Path} started | User: {User} | ContentLength: {ContentLength}",
            context.Request.Method,
            context.Request.Path.Value,
            context.User.Identity?.Name ?? "anonymous",
            context.Request.ContentLength ?? 0);
    }

    private void LogResponse(HttpContext context, TimeSpan duration)
    {
        var statusCode = context.Response.StatusCode;
        var logLevel = GetResponseLogLevel(statusCode);

        _logger.Log(logLevel,
            "HTTP {Method} {Path} completed | Status: {StatusCode} | Duration: {Duration}ms",
            context.Request.Method,
            context.Request.Path.Value,
            statusCode,
            duration.TotalMilliseconds);

        // Log warnings for slow requests
        if (duration.TotalMilliseconds > 5000)
        {
            _logger.LogWarning(
                "Slow request detected: {Method} {Path} took {Duration}ms",
                context.Request.Method,
                context.Request.Path.Value,
                duration.TotalMilliseconds);
        }
    }

    private static LogLevel GetRequestLogLevel(PathString path)
    {
        // Lower log level for health checks and metrics
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics"))
        {
            return LogLevel.Trace;
        }

        return LogLevel.Information;
    }

    private static LogLevel GetResponseLogLevel(int statusCode)
    {
        return statusCode switch
        {
            >= 500 => LogLevel.Error,
            >= 400 => LogLevel.Warning,
            _ => LogLevel.Information
        };
    }
}

/// <summary>
/// Extension methods for registering request logging.
/// </summary>
public static class RequestLoggingExtensions
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestLoggingMiddleware>();
    }
}

