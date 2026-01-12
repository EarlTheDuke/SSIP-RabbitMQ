using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SSIP.Gateway.Authentication;
using SSIP.Gateway.Authentication.Models;
using Xunit;

namespace SSIP.Gateway.Tests;

public class AuthServiceTests
{
    private readonly Mock<IDistributedCache> _cacheMock;
    private readonly Mock<ILogger<JwtAuthService>> _loggerMock;
    private readonly JwtSettings _settings;
    private readonly JwtAuthService _authService;

    public AuthServiceTests()
    {
        _cacheMock = new Mock<IDistributedCache>();
        _loggerMock = new Mock<ILogger<JwtAuthService>>();
        
        _settings = new JwtSettings
        {
            SecretKey = "TestSecretKeyThatMustBeAtLeast32Characters!",
            Issuer = "https://test.ssip.local",
            Audience = "ssip-test",
            AccessTokenExpirationMinutes = 60,
            RefreshTokenExpirationDays = 7
        };

        var optionsMock = new Mock<IOptions<JwtSettings>>();
        optionsMock.Setup(x => x.Value).Returns(_settings);

        _authService = new JwtAuthService(optionsMock.Object, _cacheMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GenerateTokenAsync_ShouldReturnValidToken()
    {
        // Arrange
        var credentials = new UserCredentials("testuser@ssip.local", "password");

        // Act
        var result = await _authService.GenerateTokenAsync(credentials);

        // Assert
        result.Should().NotBeNull();
        result.AccessToken.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        result.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task ValidateTokenAsync_WithValidToken_ShouldReturnSuccess()
    {
        // Arrange
        var credentials = new UserCredentials("testuser@ssip.local", "password");
        var tokenResponse = await _authService.GenerateTokenAsync(credentials);

        // Act
        var result = await _authService.ValidateTokenAsync(tokenResponse.AccessToken);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_WithInvalidToken_ShouldReturnFailure()
    {
        // Arrange
        var invalidToken = "invalid.jwt.token";

        // Act
        var result = await _authService.ValidateTokenAsync(invalidToken);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Principal.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateApiKeyAsync_WithMissingKey_ShouldReturnFailure()
    {
        // Arrange
        _cacheMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        // Act
        var result = await _authService.ValidateApiKeyAsync("nonexistent-api-key");

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_API_KEY");
    }

    [Fact]
    public void GetUserInfo_ShouldExtractClaimsCorrectly()
    {
        // Arrange
        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-123"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "testuser"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, "test@ssip.local"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
            new System.Security.Claims.Claim("permission", "erp:read"),
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "test");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        // Act
        var userInfo = _authService.GetUserInfo(principal);

        // Assert
        userInfo.UserId.Should().Be("user-123");
        userInfo.Username.Should().Be("testuser");
        userInfo.Email.Should().Be("test@ssip.local");
        userInfo.Roles.Should().Contain("Admin");
        userInfo.Permissions.Should().Contain("erp:read");
    }

    [Fact]
    public async Task HasPermissionAsync_WithDirectPermission_ShouldReturnTrue()
    {
        // Arrange
        var claims = new[]
        {
            new System.Security.Claims.Claim("permission", "erp:read"),
            new System.Security.Claims.Claim("permission", "erp:write"),
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "test");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        // Act
        var result = await _authService.HasPermissionAsync(principal, "erp", "read");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_WithWildcardPermission_ShouldReturnTrue()
    {
        // Arrange
        var claims = new[]
        {
            new System.Security.Claims.Claim("permission", "erp:*"),
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "test");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        // Act
        var result = await _authService.HasPermissionAsync(principal, "erp", "write");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermissionAsync_WithoutPermission_ShouldReturnFalse()
    {
        // Arrange
        var claims = new[]
        {
            new System.Security.Claims.Claim("permission", "crm:read"),
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "test");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        // Act
        var result = await _authService.HasPermissionAsync(principal, "erp", "write");

        // Assert
        result.Should().BeFalse();
    }
}

