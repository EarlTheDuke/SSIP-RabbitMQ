# SSIP API Gateway

**Silver Star Integration Platform - Boundary Layer**

The SSIP API Gateway serves as the central entry point for all system integrations, providing a unified interface between Silver Star Industries' internal systems (ERP, TinyBox AI) and external services (Dynamics 365 CRM, Power Automate, Outlook).

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        SSIP API Gateway                         │
│                      (Boundary Layer)                           │
├─────────────────────────────────────────────────────────────────┤
│  🔐 Authentication  │  📋 Routing  │  🔄 Transform  │  ⚡ Rate   │
│     JWT/API Keys    │   Dynamic    │    JSON/XML    │   Limit   │
├─────────────────────────────────────────────────────────────────┤
│                      📡 Event Bus (Azure Service Bus)           │
└─────────────────────────────────────────────────────────────────┘
         ↓                    ↓                    ↓
   ┌─────────────┐     ┌─────────────┐     ┌─────────────┐
   │ ERP Service │     │  TinyBox AI │     │  CRM        │
   │  (.NET 8)   │     │  Inference  │     │  Connector  │
   └─────────────┘     └─────────────┘     └─────────────┘
```

## 🚀 Quick Start

### Prerequisites

- .NET 8 SDK
- Redis (optional, uses in-memory cache in development)
- Azure Service Bus (optional for event publishing)

### Running Locally

```bash
# Navigate to project
cd SSIP.Gateway/src/SSIP.Gateway

# Restore dependencies
dotnet restore

# Run the gateway
dotnet run

# Gateway starts at https://localhost:5001
```

### Building

```bash
# Build the solution
dotnet build SSIP.Gateway.sln

# Run tests
dotnet test SSIP.Gateway.sln

# Publish for deployment
dotnet publish src/SSIP.Gateway -c Release -o ./publish
```

## 📁 Project Structure

```
SSIP.Gateway/
├── src/
│   └── SSIP.Gateway/
│       ├── Authentication/        # JWT & API Key auth
│       │   ├── IAuthService.cs
│       │   ├── JwtAuthService.cs
│       │   ├── ApiKeyValidator.cs
│       │   └── Models/
│       ├── Routing/               # Dynamic route resolution
│       │   ├── IRouteResolver.cs
│       │   ├── DynamicRouter.cs
│       │   └── ServiceRegistry.cs
│       ├── Transform/             # Data transformation
│       │   ├── IDataTransformer.cs
│       │   ├── JsonTransformer.cs
│       │   └── SchemaMapper.cs
│       ├── EventBus/              # Azure Service Bus integration
│       │   ├── IEventBus.cs
│       │   ├── AzureServiceBusEventBus.cs
│       │   └── Events/
│       ├── RateLimiting/          # Request throttling
│       │   ├── IRateLimiter.cs
│       │   └── SlidingWindowLimiter.cs
│       ├── Middleware/            # Request pipeline
│       │   ├── GatewayMiddleware.cs
│       │   └── RequestLoggingMiddleware.cs
│       ├── Program.cs
│       └── appsettings.json
└── tests/
    └── SSIP.Gateway.Tests/
```

## 🔧 Configuration

### appsettings.json

```json
{
  "Jwt": {
    "SecretKey": "your-secret-key-32-chars-minimum",
    "Issuer": "https://ssip.silverstarindustries.com",
    "Audience": "ssip-api"
  },
  "Gateway": {
    "Routes": [
      {
        "RouteId": "erp-api",
        "Pattern": "/api/erp/{*path}",
        "ServiceName": "erp-service",
        "TargetBaseUrl": "http://localhost:5001"
      }
    ]
  }
}
```

## 🔑 Authentication

### JWT Bearer Token
```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
```

### API Key
```http
X-API-Key: your-api-key-here
```

## 📡 API Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /` | Gateway info |
| `GET /health` | Health check |
| `GET /health/ready` | Readiness probe |
| `GET /health/live` | Liveness probe |
| `GET /metrics` | Prometheus metrics |
| `GET /swagger` | API documentation |
| `* /api/erp/*` | Proxy to ERP service |
| `* /api/ai/*` | Proxy to TinyBox AI |
| `* /api/crm/*` | Proxy to CRM connector |

## 📊 Monitoring

### Health Checks
- Redis connectivity
- Azure Service Bus connectivity
- Backend service health

### Metrics (Prometheus)
- Request count by endpoint
- Request duration histograms
- Rate limit rejections
- Circuit breaker state

### Logging (Serilog)
- Structured JSON logging
- Correlation ID tracking
- Request/response logging

## 🛡️ Security Features

- **JWT validation** with configurable issuer/audience
- **API key authentication** for service-to-service
- **Rate limiting** with sliding window algorithm
- **CORS** configuration
- **HTTPS** enforcement in production

## 📦 Dependencies

- `Microsoft.AspNetCore.Authentication.JwtBearer` - JWT auth
- `Azure.Messaging.ServiceBus` - Event bus
- `StackExchange.Redis` - Distributed cache
- `Polly` - Resilience policies
- `Serilog` - Structured logging
- `prometheus-net` - Metrics
- `Swashbuckle` - OpenAPI/Swagger

## 🤝 Contributing

1. Create a feature branch
2. Make your changes
3. Run tests: `dotnet test`
4. Submit a pull request

## 📄 License

Copyright © 2026 Silver Star Industries. All rights reserved.

---

*"Fostering a culture that inspires everyone we serve to innovate and thrive"*

