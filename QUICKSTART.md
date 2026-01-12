# SSIP Gateway - Developer Quick Start

> **For the Dev Team** - Get up and running in under 5 minutes

---

## Prerequisites Checklist

Before you begin, ensure you have:

- [ ] **.NET 8 SDK** installed ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- [ ] **Docker Desktop** running ([Download](https://www.docker.com/products/docker-desktop))
- [ ] **Git** configured

Verify your setup:

```powershell
dotnet --version    # Should show 8.x
docker --version    # Should show 20.x or higher
```

---

## 🚀 3-Step Setup

### Step 1: Start Infrastructure (30 seconds)

```powershell
# From project root
docker compose up -d
```

Wait for healthy status:

```powershell
docker compose ps
# Both containers should show "running (healthy)"
```

### Step 2: Run Gateway (30 seconds)

```powershell
cd src/SSIP.Gateway
dotnet run
```

### Step 3: Verify (30 seconds)

Open in browser:
- **Gateway Info:** http://localhost:5000
- **Swagger UI:** http://localhost:5000/swagger
- **Health Check:** http://localhost:5000/health
- **RabbitMQ UI:** http://localhost:15672 (guest/guest)

---

## 🔧 Common Development Tasks

### Rebuild After Code Changes

```powershell
# From src/SSIP.Gateway
dotnet build
dotnet run
```

### Run Tests

```powershell
# From project root
dotnet test
```

### View Logs

```powershell
# RabbitMQ logs
docker compose logs -f rabbitmq

# Redis logs
docker compose logs -f redis
```

### Stop Everything

```powershell
# Stop gateway: Ctrl+C in terminal

# Stop infrastructure
docker compose down
```

---

## 📝 Key Configuration Files

| File | Purpose |
|------|---------|
| `src/SSIP.Gateway/appsettings.json` | Main configuration |
| `src/SSIP.Gateway/appsettings.Development.json` | Dev overrides |
| `docker-compose.yml` | Local infrastructure |
| `env.example.txt` | Environment variables template |

---

## 🐰 RabbitMQ Quick Reference

**Management UI:** http://localhost:15672

| Credentials | Value |
|-------------|-------|
| Username | `guest` |
| Password | `guest` |

**SSIP Exchanges:**
- `ssip.projectcreated` - Project events
- `ssip.workordercreated` - Work order events
- `ssip.apirequestprocessed` - API request events

**SSIP Queues:**
- `ssip.gateway.*` - Gateway subscriptions
- `ssip.deadletter.*` - Failed messages

---

## ⚠️ Troubleshooting

### "Connection refused" to RabbitMQ

```powershell
# Check if containers are running
docker compose ps

# If not running, start them
docker compose up -d

# Wait for healthy status (~30 seconds)
```

### Build errors after pull

```powershell
dotnet clean SSIP.Gateway.sln
dotnet restore SSIP.Gateway.sln
dotnet build SSIP.Gateway.sln
```

### Port already in use

```powershell
# Check what's using port 5000
netstat -ano | findstr :5000

# Or change ports in appsettings.Development.json
```

---

## 🔗 Useful Links

- [RabbitMQ Documentation](https://www.rabbitmq.com/documentation.html)
- [.NET 8 Documentation](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8)
- [Serilog Documentation](https://serilog.net/)

---

**Questions?** Contact the platform team.
