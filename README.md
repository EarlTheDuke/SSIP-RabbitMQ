# SSIP API Gateway

**Silver Star Integration Platform - Boundary Layer**

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-3.13-FF6600)](https://www.rabbitmq.com/)
[![License](https://img.shields.io/badge/License-Proprietary-blue)](LICENSE)

The SSIP API Gateway serves as the central entry point for all system integrations, providing a unified interface between Silver Star Industries' internal systems (ERP, TinyBox AI) and external services (Dynamics 365 CRM, Power Automate, Outlook).

---

## 🏗️ Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           SSIP API Gateway                                  │
│                          (Boundary Layer)                                   │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   🔐 Authentication    📋 Dynamic Routing    🔄 Data Transform    ⚡ Rate   │
│      JWT / API Keys       Path-based           JSON / XML          Limit   │
│                                                                             │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   📡 Event Bus                          🗄️ Distributed Cache                │
│      RabbitMQ (local) or                   Redis                           │
│      Azure Service Bus (cloud)                                             │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                    │                  │                  │
           ┌────────┴────────┐ ┌───────┴───────┐ ┌───────┴───────┐
           │   ERP Service   │ │  TinyBox AI   │ │  CRM Connector │
           │    (.NET 8)     │ │   Inference   │ │  (Dynamics)    │
           └─────────────────┘ └───────────────┘ └────────────────┘
```

---

## 🚀 Quick Start (5 Minutes)

### Prerequisites

| Tool | Version | Required |
|------|---------|----------|
| .NET SDK | 8.0+ | ✅ Yes |
| Docker Desktop | 20.10+ | ✅ Yes |
| Git | 2.x | ✅ Yes |

### Step 1: Start Infrastructure

```powershell
# Clone and navigate to the project
cd SSIP-RabbitMQ

# Start RabbitMQ and Redis containers
docker compose up -d

# Verify containers are running
docker compose ps
```

**Expected Output:**
```
NAME            STATUS                   PORTS
ssip-rabbitmq   running (healthy)        0.0.0.0:5672->5672/tcp, 0.0.0.0:15672->15672/tcp
ssip-redis      running (healthy)        0.0.0.0:6379->6379/tcp
```

### Step 2: Run the Gateway

```powershell
# Navigate to the gateway project
cd src/SSIP.Gateway

# Restore and run
dotnet run
```

**Expected Output:**
```
[12:00:00 INF] Using RabbitMQ as message broker
[12:00:00 INF] RabbitMQ EventBus connected to localhost:5672//
[12:00:00 INF] Routes loaded successfully
[12:00:00 INF] SSIP Gateway starting on https://localhost:5001, http://localhost:5000
```

### Step 3: Verify It Works

| Service | URL | Credentials |
|---------|-----|-------------|
| 🌐 Gateway | http://localhost:5000 | N/A |
| 📖 Swagger UI | http://localhost:5000/swagger | N/A |
| ❤️ Health Check | http://localhost:5000/health | N/A |
| 🐰 RabbitMQ UI | http://localhost:15672 | guest / guest |

---

## 📁 Project Structure

```
SSIP-RabbitMQ/
├── 📄 docker-compose.yml          # Local infrastructure (RabbitMQ, Redis)
├── 📄 env.example.txt             # Environment variables template
├── 📄 SSIP.Gateway.sln            # Solution file
│
├── 📂 src/
│   └── 📂 SSIP.Gateway/
│       ├── 📂 Authentication/     # JWT & API Key authentication
│       │   ├── IAuthService.cs
│       │   ├── JwtAuthService.cs
│       │   ├── ApiKeyValidator.cs
│       │   └── 📂 Models/
│       │
│       ├── 📂 EventBus/           # Message broker abstraction
│       │   ├── IEventBus.cs           # Interface
│       │   ├── RabbitMqEventBus.cs    # 🐰 Local/On-Premises (DEFAULT)
│       │   ├── AzureServiceBusEventBus.cs  # ☁️ Cloud option
│       │   └── 📂 Events/             # Integration event definitions
│       │
│       ├── 📂 Routing/            # Dynamic route resolution
│       ├── 📂 Transform/          # Data transformation
│       ├── 📂 RateLimiting/       # Request throttling
│       ├── 📂 Middleware/         # Request pipeline
│       ├── 📄 Program.cs          # Application entry point
│       └── 📄 appsettings.json    # Configuration
│
├── 📂 tests/
│   └── 📂 SSIP.Gateway.Tests/     # Unit tests
│
└── 📂 docs/                       # Documentation
```

---

## 🐰 Message Broker Configuration

The gateway supports **two message brokers** with seamless switching:

### RabbitMQ (Default - Local/On-Premises)

Best for: Local development, on-premises servers, self-hosted deployments

```json
{
  "EventBus": {
    "BrokerType": "RabbitMQ"
  },
  "RabbitMq": {
    "HostName": "localhost",
    "Port": 5672,
    "UserName": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "ExchangePrefix": "ssip.",
    "QueuePrefix": "ssip.",
    "PrefetchCount": 20,
    "MaxDeliveryCount": 5
  }
}
```

### Azure Service Bus (Cloud Option)

Best for: Azure cloud deployments, enterprise-scale

```json
{
  "EventBus": {
    "BrokerType": "AzureServiceBus",
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/..."
  }
}
```

### Feature Comparison

| Feature | RabbitMQ | Azure Service Bus |
|---------|----------|-------------------|
| Deployment | Self-hosted | Azure Cloud |
| Cost | Free (OSS) | Pay-per-use |
| Management UI | ✅ Built-in | Azure Portal |
| Dead Letter Queues | ✅ Yes | ✅ Yes |
| Scheduled Messages | ✅ Via TTL+DLX | ✅ Native |
| Message Batching | ✅ Yes | ✅ Yes |
| Health Checks | ✅ Yes | ✅ Yes |

---

## 🔧 Configuration Reference

### Environment Variables

Copy `env.example.txt` to `.env` and configure:

```bash
# ═══════════════════════════════════════════════════════════════
# MESSAGE BROKER
# ═══════════════════════════════════════════════════════════════
EVENTBUS_BROKERTYPE=RabbitMQ          # RabbitMQ or AzureServiceBus

# RabbitMQ Settings
RABBITMQ_HOST=localhost
RABBITMQ_PORT=5672
RABBITMQ_USER=guest
RABBITMQ_PASSWORD=guest

# ═══════════════════════════════════════════════════════════════
# CACHE
# ═══════════════════════════════════════════════════════════════
REDIS_CONNECTION_STRING=localhost:6379

# ═══════════════════════════════════════════════════════════════
# AUTHENTICATION
# ═══════════════════════════════════════════════════════════════
JWT_SECRET_KEY=your-secret-key-must-be-at-least-32-characters-long
JWT_ISSUER=https://ssip.silverstarindustries.com
JWT_AUDIENCE=ssip-api

# ═══════════════════════════════════════════════════════════════
# BACKEND SERVICES
# ═══════════════════════════════════════════════════════════════
ERP_SERVICE_URL=http://localhost:5001
TINYBOX_AI_URL=http://localhost:5002
CRM_CONNECTOR_URL=http://localhost:5003
```

---

## 📡 API Endpoints

### Gateway Endpoints

| Method | Endpoint | Description | Auth |
|--------|----------|-------------|------|
| GET | `/` | Gateway info & status | ❌ No |
| GET | `/health` | Full health check (JSON) | ❌ No |
| GET | `/health/ready` | Readiness probe | ❌ No |
| GET | `/health/live` | Liveness probe | ❌ No |
| GET | `/metrics` | Prometheus metrics | ❌ No |
| GET | `/swagger` | API documentation | ❌ No |

### Proxied Routes

| Pattern | Target Service | Required Scopes |
|---------|----------------|-----------------|
| `/api/erp/*` | ERP Service | `erp.read`, `erp.write` |
| `/api/ai/*` | TinyBox AI | `ai.inference` |
| `/api/crm/*` | CRM Connector | `crm.read`, `crm.write` |

---

## 🔑 Authentication

### JWT Bearer Token

```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
```

### API Key

```http
X-API-Key: your-api-key-here
```

---

## 🐳 Docker Commands

```powershell
# Start infrastructure
docker compose up -d

# View logs
docker compose logs -f rabbitmq
docker compose logs -f redis

# Stop infrastructure
docker compose down

# Stop and remove all data
docker compose down -v

# Restart a specific service
docker compose restart rabbitmq
```

---

## 📊 Monitoring

### RabbitMQ Management Console

Access at **http://localhost:15672** (guest/guest):

- 📈 View message rates and queue depths
- 🔍 Inspect messages in queues
- ⚙️ Manage exchanges and bindings
- 📊 Monitor connections and channels

### Health Check Response

```json
{
  "status": "Healthy",
  "timestamp": "2026-01-11T20:00:00Z",
  "checks": [
    { "name": "self", "status": "Healthy", "duration": 0.1 },
    { "name": "redis", "status": "Healthy", "duration": 2.5 },
    { "name": "rabbitmq", "status": "Healthy", "duration": 15.3 }
  ]
}
```

### Prometheus Metrics

Available at `/metrics`:
- `http_requests_received_total` - Request count by endpoint
- `http_request_duration_seconds` - Request latency histogram
- Custom SSIP metrics

---

## 🛡️ Security Features

| Feature | Description |
|---------|-------------|
| JWT Validation | RS256/HS256 token validation with configurable issuer/audience |
| API Key Auth | Service-to-service authentication |
| Rate Limiting | Sliding window algorithm with configurable policies |
| CORS | Configurable cross-origin resource sharing |
| HTTPS | Enforced in production environments |

---

## 📦 NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `RabbitMQ.Client` | 6.8.1 | RabbitMQ message broker |
| `Azure.Messaging.ServiceBus` | 7.18.2 | Azure Service Bus (cloud) |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 8.0.11 | JWT authentication |
| `StackExchange.Redis` | 2.8.16 | Distributed cache |
| `Polly` | 8.5.0 | Resilience policies |
| `Serilog.AspNetCore` | 8.0.3 | Structured logging |
| `prometheus-net.AspNetCore` | 8.2.1 | Metrics |
| `Swashbuckle.AspNetCore` | 6.9.0 | OpenAPI/Swagger |

---

## 🔄 Switching Message Brokers

To switch between RabbitMQ and Azure Service Bus:

1. **Update `appsettings.json`:**
   ```json
   {
     "EventBus": {
       "BrokerType": "AzureServiceBus"  // or "RabbitMQ"
     }
   }
   ```

2. **Restart the gateway:**
   ```powershell
   # Stop and restart
   dotnet run
   ```

**No code changes required** - the `IEventBus` interface abstracts the broker implementation.

---

## 🧪 Testing

```powershell
# Run all tests
dotnet test SSIP.Gateway.sln

# Run with verbose output
dotnet test SSIP.Gateway.sln --verbosity normal

# Run with coverage
dotnet test SSIP.Gateway.sln --collect:"XPlat Code Coverage"
```

---

## 📝 Changelog

### v1.1.0 (January 2026) - RabbitMQ Integration

**New Features:**
- ✅ Added RabbitMQ as local message broker (default)
- ✅ Configurable broker selection (RabbitMQ or Azure Service Bus)
- ✅ Docker Compose for local infrastructure
- ✅ RabbitMQ health checks
- ✅ Dead letter queue support
- ✅ Publisher confirms for reliable delivery
- ✅ Message batching support

**Files Added/Modified:**
- `src/SSIP.Gateway/EventBus/RabbitMqEventBus.cs` (NEW)
- `src/SSIP.Gateway/Program.cs` (Modified)
- `src/SSIP.Gateway/appsettings.json` (Modified)
- `docker-compose.yml` (NEW)
- `env.example.txt` (Modified)

### v1.0.0 (December 2025) - Initial Release

- Initial SSIP Gateway implementation
- Azure Service Bus integration
- JWT/API Key authentication
- Dynamic routing
- Rate limiting

---

## 🆘 Troubleshooting

### RabbitMQ Connection Failed

```
RabbitMQ.Client.Exceptions.BrokerUnreachableException
```

**Solution:**
1. Ensure Docker containers are running: `docker compose ps`
2. Check RabbitMQ logs: `docker compose logs rabbitmq`
3. Verify port 5672 is not blocked

### Health Check Shows Unhealthy

**Solution:**
1. Check `/health` endpoint for details
2. Verify Redis is running: `docker compose logs redis`
3. Verify RabbitMQ is running: `docker compose logs rabbitmq`

### Build Errors

**Solution:**
```powershell
# Clean and rebuild
dotnet clean SSIP.Gateway.sln
dotnet restore SSIP.Gateway.sln
dotnet build SSIP.Gateway.sln
```

---

## 🤝 Contributing

1. Create a feature branch from `main`
2. Make your changes
3. Run tests: `dotnet test`
4. Submit a pull request

---

## 📄 License

Copyright © 2026 Silver Star Industries. All rights reserved.

---

<div align="center">
<i>"Fostering a culture that inspires everyone we serve to innovate and thrive"</i>
</div>
