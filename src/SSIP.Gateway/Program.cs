using Polly;
using Polly.Extensions.Http;
using Prometheus;
using Serilog;
using SSIP.Gateway.Authentication;
using SSIP.Gateway.EventBus;
using SSIP.Gateway.Middleware;
using SSIP.Gateway.RateLimiting;
using SSIP.Gateway.Routing;
using SSIP.Gateway.Transform;

// ═══════════════════════════════════════════════════════════════════════════════
//  SSIP API GATEWAY - Silver Star Integration Platform
//  Boundary Layer for connecting internal systems with external integrations
// ═══════════════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════════════════════════
//  LOGGING CONFIGURATION (Serilog)
// ═══════════════════════════════════════════════════════════════════════════════

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console(outputTemplate: 
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}" +
        "      {Message:lj}{NewLine}" +
        "      {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/ssip-gateway-.log", 
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// ═══════════════════════════════════════════════════════════════════════════════
//  CONFIGURATION BINDING
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<RoutingOptions>(builder.Configuration.GetSection(RoutingOptions.SectionName));
builder.Services.Configure<EventBusOptions>(builder.Configuration.GetSection(EventBusOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));

// ═══════════════════════════════════════════════════════════════════════════════
//  AUTHENTICATION & AUTHORIZATION
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddScoped<IAuthService, JwtAuthService>();

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
        options.Authority = jwtSettings?.Issuer;
        options.Audience = jwtSettings?.Audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddApiKeyAuthentication();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccess", policy =>
        policy.RequireAuthenticatedUser());
    
    options.AddPolicy("ErpAccess", policy =>
        policy.RequireClaim("scope", "erp.read", "erp.write"));
    
    options.AddPolicy("CrmAccess", policy =>
        policy.RequireClaim("scope", "crm.read", "crm.write"));
    
    options.AddPolicy("AiAccess", policy =>
        policy.RequireClaim("scope", "ai.inference"));
    
    options.AddPolicy("AdminAccess", policy =>
        policy.RequireRole("Admin", "SystemAdmin"));
});

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTING & SERVICE DISCOVERY
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<IServiceRegistry, ServiceRegistry>();
builder.Services.AddSingleton<IRouteResolver, DynamicRouter>();

// ═══════════════════════════════════════════════════════════════════════════════
//  DATA TRANSFORMATION
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<ISchemaMapper, SchemaMapper>();
builder.Services.AddScoped<IDataTransformer, JsonTransformer>();

// ═══════════════════════════════════════════════════════════════════════════════
//  EVENT BUS (Configurable: RabbitMQ or Azure Service Bus)
// ═══════════════════════════════════════════════════════════════════════════════

var messageBrokerType = builder.Configuration["EventBus:BrokerType"]?.ToLowerInvariant() ?? "rabbitmq";

if (messageBrokerType == "azureservicebus")
{
    // Azure Service Bus for cloud deployments
    builder.Services.AddSingleton<IEventBus, AzureServiceBusEventBus>();
    Log.Information("Using Azure Service Bus as message broker");
}
else
{
    // RabbitMQ for local/on-premises deployments (default)
    builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
    builder.Services.AddSingleton<IEventBus, RabbitMqEventBus>();
    Log.Information("Using RabbitMQ as message broker");
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RATE LIMITING
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<IRateLimiter, SlidingWindowLimiter>();

// ═══════════════════════════════════════════════════════════════════════════════
//  DISTRIBUTED CACHE (Redis)
// ═══════════════════════════════════════════════════════════════════════════════

var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "SSIP:";
    });
}
else
{
    // Fallback to in-memory cache for development
    builder.Services.AddDistributedMemoryCache();
}

// ═══════════════════════════════════════════════════════════════════════════════
//  HTTP CLIENT FACTORY with Resilience Policies
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddHttpClient("BackendServices")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(GetCircuitBreakerPolicy());

builder.Services.AddHttpClient("HealthCheck")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });

// ═══════════════════════════════════════════════════════════════════════════════
//  HEALTH CHECKS
// ═══════════════════════════════════════════════════════════════════════════════

var healthChecksBuilder = builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy())
    .AddRedis(redisConnection ?? "localhost", name: "redis", tags: ["infrastructure"]);

if (messageBrokerType == "azureservicebus")
{
    healthChecksBuilder.AddAzureServiceBusTopic(
        builder.Configuration["EventBus:ConnectionString"] ?? "",
        "ssip-health-check",
        name: "servicebus",
        tags: ["infrastructure"]);
}
else
{
    // RabbitMQ health check
    var rabbitMqConnection = builder.Configuration["RabbitMq:HostName"] ?? "localhost";
    var rabbitMqPort = builder.Configuration.GetValue<int>("RabbitMq:Port", 5672);
    var rabbitMqUser = builder.Configuration["RabbitMq:UserName"] ?? "guest";
    var rabbitMqPass = builder.Configuration["RabbitMq:Password"] ?? "guest";
    var rabbitMqVHost = builder.Configuration["RabbitMq:VirtualHost"] ?? "/";
    
    healthChecksBuilder.AddRabbitMQ(
        rabbitConnectionString: $"amqp://{rabbitMqUser}:{rabbitMqPass}@{rabbitMqConnection}:{rabbitMqPort}{rabbitMqVHost}",
        name: "rabbitmq",
        tags: ["infrastructure"]);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  SWAGGER / OPENAPI
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "SSIP API Gateway",
        Version = "v1",
        Description = "Silver Star Integration Platform - API Gateway for unified system access",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "Silver Star Industries",
            Email = "support@silverstarindustries.com"
        }
    });

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "API Key header",
        Name = "X-API-Key",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    });
});

// ═══════════════════════════════════════════════════════════════════════════════
//  CORS
// ═══════════════════════════════════════════════════════════════════════════════

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowConfigured", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
            ?? ["http://localhost:3000"];
        
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ═══════════════════════════════════════════════════════════════════════════════
//  BUILD APPLICATION
// ═══════════════════════════════════════════════════════════════════════════════

var app = builder.Build();

// ═══════════════════════════════════════════════════════════════════════════════
//  MIDDLEWARE PIPELINE
// ═══════════════════════════════════════════════════════════════════════════════

// Development tools
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "SSIP Gateway v1");
        c.RoutePrefix = "swagger";
    });
}

// Security
app.UseHttpsRedirection();
app.UseCors("AllowConfigured");

// Request logging
app.UseRequestLogging();

// Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// Prometheus metrics
app.UseHttpMetrics();

// SSIP Gateway middleware (handles routing, transformation, proxying)
app.UseMiddleware<GatewayMiddleware>();

// ═══════════════════════════════════════════════════════════════════════════════
//  ENDPOINTS
// ═══════════════════════════════════════════════════════════════════════════════

// Health check endpoints
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            timestamp = DateTime.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            })
        });
        await context.Response.WriteAsync(result);
    }
});

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("infrastructure")
});

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

// Metrics endpoint
app.MapMetrics();

// Gateway info endpoint
app.MapGet("/", () => Results.Ok(new
{
    name = "SSIP API Gateway",
    version = "1.0.0",
    description = "Silver Star Integration Platform - Boundary Layer",
    timestamp = DateTime.UtcNow,
    endpoints = new
    {
        health = "/health",
        metrics = "/metrics",
        swagger = "/swagger"
    }
})).AllowAnonymous();

// ═══════════════════════════════════════════════════════════════════════════════
//  STARTUP
// ═══════════════════════════════════════════════════════════════════════════════

// Load routes on startup
using (var scope = app.Services.CreateScope())
{
    var router = scope.ServiceProvider.GetRequiredService<IRouteResolver>();
    await router.ReloadRoutesAsync();
    Log.Information("Routes loaded successfully");
}

Log.Information("SSIP Gateway starting on {Urls}", string.Join(", ", app.Urls));

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SSIP Gateway terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ═══════════════════════════════════════════════════════════════════════════════
//  POLLY RESILIENCE POLICIES
// ═══════════════════════════════════════════════════════════════════════════════

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Log.Warning("Retry {RetryAttempt} after {Delay}s due to {StatusCode}",
                    retryAttempt, timespan.TotalSeconds, outcome.Result?.StatusCode);
            });
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (result, breakDelay) =>
            {
                Log.Warning("Circuit breaker opened for {BreakDelay}s", breakDelay.TotalSeconds);
            },
            onReset: () =>
            {
                Log.Information("Circuit breaker reset");
            });
}

