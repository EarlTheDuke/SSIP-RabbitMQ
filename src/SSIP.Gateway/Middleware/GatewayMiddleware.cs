using System.Diagnostics;
using System.Text.Json;
using SSIP.Gateway.Authentication;
using SSIP.Gateway.EventBus;
using SSIP.Gateway.EventBus.Events;
using SSIP.Gateway.RateLimiting;
using SSIP.Gateway.Routing;
using SSIP.Gateway.Transform;

namespace SSIP.Gateway.Middleware;

/// <summary>
/// Core gateway middleware that orchestrates the request pipeline.
/// Combines auth, routing, transformation, and proxying.
/// </summary>
public class GatewayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GatewayMiddleware> _logger;

    public GatewayMiddleware(RequestDelegate next, ILogger<GatewayMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IRouteResolver router,
        IDataTransformer transformer,
        IRateLimiter rateLimiter,
        IEventBus eventBus,
        IHttpClientFactory httpClientFactory)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();
        
        context.Response.Headers.Append("X-Correlation-Id", correlationId);
        context.Items["CorrelationId"] = correlationId;

        var stopwatch = Stopwatch.StartNew();
        var requestPath = context.Request.Path.Value ?? "/";

        try
        {
            // Skip gateway processing for internal endpoints
            if (IsInternalEndpoint(requestPath))
            {
                await _next(context);
                return;
            }

            _logger.LogDebug("Processing request {Method} {Path} [{CorrelationId}]",
                context.Request.Method, requestPath, correlationId);

            // 1. Rate Limiting Check
            var rateLimitResult = await CheckRateLimitAsync(context, rateLimiter);
            if (!rateLimitResult.IsAllowed)
            {
                await WriteRateLimitResponse(context, rateLimitResult);
                return;
            }

            // 2. Resolve Route
            var route = await router.ResolveAsync(context.Request);
            if (route is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteErrorResponse(context, "NOT_FOUND", "No route found for this endpoint");
                return;
            }

            _logger.LogDebug("Route resolved: {ServiceName} -> {TargetUri}",
                route.ServiceName, route.TargetUri);

            // 3. Transform Request (if applicable)
            var requestBody = await ReadRequestBodyAsync(context.Request);
            JsonDocument? transformedRequest = null;
            
            if (requestBody is not null)
            {
                var hasMapping = await transformer.HasMappingAsync(
                    "gateway.incoming", $"{route.ServiceName}.request");
                
                if (hasMapping)
                {
                    transformedRequest = await transformer.TransformRequestAsync(
                        requestBody,
                        "gateway.incoming",
                        $"{route.ServiceName}.request"
                    );
                }
                else
                {
                    transformedRequest = requestBody;
                }
            }

            // 4. Forward to Backend Service
            var httpClient = httpClientFactory.CreateClient("BackendServices");
            httpClient.Timeout = route.Timeout;

            using var proxyRequest = CreateProxyRequest(context, route, transformedRequest);
            using var proxyResponse = await httpClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);

            // 5. Transform Response (if applicable)
            var responseBody = await ReadResponseBodyAsync(proxyResponse);
            JsonDocument? transformedResponse = null;

            if (responseBody is not null)
            {
                var hasMapping = await transformer.HasMappingAsync(
                    $"{route.ServiceName}.response", "gateway.outgoing");

                if (hasMapping)
                {
                    transformedResponse = await transformer.TransformResponseAsync(
                        responseBody,
                        $"{route.ServiceName}.response",
                        "gateway.outgoing"
                    );
                }
                else
                {
                    transformedResponse = responseBody;
                }
            }

            // 6. Write Response
            await WriteProxyResponse(context, proxyResponse, transformedResponse);

            stopwatch.Stop();

            // 7. Publish Event (async, don't wait)
            _ = PublishRequestEventAsync(eventBus, new ApiRequestProcessed(
                correlationId,
                route.ServiceName,
                context.Response.StatusCode,
                stopwatch.Elapsed
            )
            {
                UserId = context.User.Identity?.Name,
                Endpoint = requestPath,
                HttpMethod = context.Request.Method
            });

            _logger.LogInformation(
                "Request completed: {Method} {Path} -> {StatusCode} in {Duration}ms [{CorrelationId}]",
                context.Request.Method, requestPath, context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds, correlationId);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Backend service error for {Path} [{CorrelationId}]",
                requestPath, correlationId);

            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await WriteErrorResponse(context, "BAD_GATEWAY", "Backend service unavailable");

            _ = PublishErrorEventAsync(eventBus, new GatewayErrorOccurred(
                correlationId, "BAD_GATEWAY", ex.Message
            ));
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Request timeout for {Path} [{CorrelationId}]",
                requestPath, correlationId);

            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await WriteErrorResponse(context, "GATEWAY_TIMEOUT", "Backend service timed out");

            _ = PublishErrorEventAsync(eventBus, new GatewayErrorOccurred(
                correlationId, "GATEWAY_TIMEOUT", "Request timed out"
            ));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Gateway error for {Path} [{CorrelationId}]",
                requestPath, correlationId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteErrorResponse(context, "INTERNAL_ERROR", "An unexpected error occurred");

            _ = PublishErrorEventAsync(eventBus, new GatewayErrorOccurred(
                correlationId, "INTERNAL_ERROR", ex.Message
            )
            {
                StackTrace = ex.StackTrace
            });
        }
    }

    #region Private Methods

    private static bool IsInternalEndpoint(string path)
    {
        return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RateLimitResult> CheckRateLimitAsync(HttpContext context, IRateLimiter rateLimiter)
    {
        var clientId = GetClientId(context);
        
        return await rateLimiter.CheckAsync(new RateLimitContext
        {
            ClientId = clientId,
            Endpoint = context.Request.Path,
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            TenantId = context.User.FindFirst("tenant_id")?.Value,
            HttpMethod = context.Request.Method
        });
    }

    private static string GetClientId(HttpContext context)
    {
        // Try to get client ID from various sources
        return context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst("client_id")?.Value
            ?? context.Request.Headers["X-API-Key"].FirstOrDefault()
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
    }

    private static async Task<JsonDocument?> ReadRequestBodyAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        request.EnableBuffering();
        
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        return JsonDocument.Parse(body);
    }

    private static async Task<JsonDocument?> ReadResponseBodyAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        return JsonDocument.Parse(body);
    }

    private static HttpRequestMessage CreateProxyRequest(
        HttpContext context,
        RouteMatch route,
        JsonDocument? body)
    {
        var request = new HttpRequestMessage
        {
            Method = new HttpMethod(context.Request.Method),
            RequestUri = route.TargetUri
        };

        // Copy headers (except Host)
        foreach (var header in context.Request.Headers)
        {
            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        // Add route-specific headers
        foreach (var (key, value) in route.AdditionalHeaders)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        // Forward correlation ID
        if (context.Items.TryGetValue("CorrelationId", out var correlationId))
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId?.ToString());
        }

        // Set body
        if (body is not null)
        {
            request.Content = new StringContent(
                body.RootElement.GetRawText(),
                System.Text.Encoding.UTF8,
                "application/json"
            );
        }
        else if (context.Request.ContentLength > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }

        return request;
    }

    private static async Task WriteProxyResponse(
        HttpContext context,
        HttpResponseMessage proxyResponse,
        JsonDocument? transformedBody)
    {
        context.Response.StatusCode = (int)proxyResponse.StatusCode;

        // Copy response headers
        foreach (var header in proxyResponse.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in proxyResponse.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Remove transfer-encoding if we're modifying the body
        context.Response.Headers.Remove("transfer-encoding");

        // Write body
        if (transformedBody is not null)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(transformedBody.RootElement.GetRawText());
        }
        else
        {
            await proxyResponse.Content.CopyToAsync(context.Response.Body);
        }
    }

    private static async Task WriteRateLimitResponse(HttpContext context, RateLimitResult result)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.Append("Retry-After", ((int)result.RetryAfter.TotalSeconds).ToString());
        context.Response.Headers.Append("X-RateLimit-Limit", result.Limit.ToString());
        context.Response.Headers.Append("X-RateLimit-Remaining", result.RemainingRequests.ToString());

        await WriteErrorResponse(context, "RATE_LIMITED", result.RejectReason ?? "Rate limit exceeded");
    }

    private static async Task WriteErrorResponse(HttpContext context, string code, string message)
    {
        context.Response.ContentType = "application/json";
        
        var error = new
        {
            error = new
            {
                code,
                message,
                timestamp = DateTimeOffset.UtcNow
            }
        };

        await context.Response.WriteAsJsonAsync(error);
    }

    private static async Task PublishRequestEventAsync(IEventBus eventBus, ApiRequestProcessed @event)
    {
        try
        {
            await eventBus.PublishAsync(@event);
        }
        catch
        {
            // Ignore event publishing errors
        }
    }

    private static async Task PublishErrorEventAsync(IEventBus eventBus, GatewayErrorOccurred @event)
    {
        try
        {
            await eventBus.PublishAsync(@event);
        }
        catch
        {
            // Ignore event publishing errors
        }
    }

    #endregion
}

/// <summary>
/// Extension method to check for JSON content type.
/// </summary>
public static class HttpRequestExtensions
{
    public static bool HasJsonContentType(this HttpRequest request)
    {
        return request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }
}

