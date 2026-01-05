# Contributing to SSIP Gateway

Thank you for your interest in contributing to the Silver Star Integration Platform!

## 🚀 Getting Started

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022 / VS Code / Rider
- Docker (optional, for Redis)
- Git

### Local Setup

1. **Clone the repository**
   ```bash
   git clone https://github.com/SilverStarIndustries/SSIP-Gateway.git
   cd SSIP-Gateway
   ```

2. **Copy environment file**
   ```bash
   cp .env.example .env
   # Edit .env with your local settings
   ```

3. **Restore and build**
   ```bash
   dotnet restore
   dotnet build
   ```

4. **Run tests**
   ```bash
   dotnet test
   ```

5. **Start the gateway**
   ```bash
   cd src/SSIP.Gateway
   dotnet run
   ```

## 📝 Development Workflow

### Branch Naming

- `feature/` - New features (e.g., `feature/add-oauth-support`)
- `bugfix/` - Bug fixes (e.g., `bugfix/fix-rate-limiter`)
- `hotfix/` - Urgent production fixes
- `refactor/` - Code improvements without changing behavior

### Commit Messages

Use conventional commits:
```
feat: add OAuth 2.0 support for external providers
fix: correct rate limiter window calculation
docs: update API documentation
test: add unit tests for SchemaMapper
refactor: simplify authentication middleware
```

### Pull Request Process

1. Create a feature branch from `main`
2. Make your changes
3. Ensure all tests pass: `dotnet test`
4. Update documentation if needed
5. Submit a PR with a clear description
6. Request review from at least one team member

## 🧪 Testing Guidelines

- Write unit tests for all new code
- Maintain >80% code coverage
- Use meaningful test names that describe behavior
- Follow AAA pattern (Arrange, Act, Assert)

Example:
```csharp
[Fact]
public async Task ValidateTokenAsync_WithExpiredToken_ShouldReturnFailure()
{
    // Arrange
    var expiredToken = GenerateExpiredToken();
    
    // Act
    var result = await _authService.ValidateTokenAsync(expiredToken);
    
    // Assert
    result.IsValid.Should().BeFalse();
    result.ErrorCode.Should().Be("TOKEN_EXPIRED");
}
```

## 📁 Project Structure

```
SSIP.Gateway/
├── src/SSIP.Gateway/      # Main application
│   ├── Authentication/    # Auth services
│   ├── Routing/          # Route resolution
│   ├── Transform/        # Data transformation
│   ├── EventBus/         # Event publishing
│   ├── RateLimiting/     # Request throttling
│   └── Middleware/       # Request pipeline
└── tests/                # Unit & integration tests
```

## 🔒 Security

- Never commit secrets or API keys
- Use environment variables for sensitive config
- Report security issues privately to the team

## 📞 Questions?

Contact the development team or open a GitHub issue.

---

*Silver Star Industries - "Fostering a culture that inspires everyone we serve to innovate and thrive"*

