using FileHoster.Models;
using Xunit;

namespace FileHoster.Tests;

public class PermissionSystemTests
{
    [Fact]
    public void Application_CanUpload_DefaultIsFalse()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Assert
        Assert.False(app.CanUpload);
    }

    [Fact]
    public void Application_CanDownload_DefaultIsTrue()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Assert
        Assert.True(app.CanDownload);
    }

    [Fact]
    public void Application_CanQuery_DefaultIsTrue()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Assert
        Assert.True(app.CanQuery);
    }

    [Fact]
    public void Application_CanDelete_DefaultIsFalse()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Assert
        Assert.False(app.CanDelete);
    }

    [Fact]
    public void Application_CanExplicitlyEnableAllPermissions()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
            CanUpload = true,
            CanDownload = true,
            CanQuery = true,
            CanDelete = true
        };

        // Assert
        Assert.True(app.CanUpload);
        Assert.True(app.CanDownload);
        Assert.True(app.CanQuery);
        Assert.True(app.CanDelete);
    }

    [Fact]
    public void Application_DenyByDefaultPrinciple_UploadAndDeleteDisabled()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "TestApp",
            SecretKey = "secret",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Assert - Dangerous operations denied by default
        Assert.False(app.CanUpload, "Upload should be denied by default");
        Assert.False(app.CanDelete, "Delete should be denied by default");
        
        // Safe operations allowed by default
        Assert.True(app.CanDownload, "Download should be allowed by default");
        Assert.True(app.CanQuery, "Query should be allowed by default");
    }

    [Fact]
    public void Application_CanCreateDownloadOnlyApp()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "CustomerDownloadOnly",
            SecretKey = "customer-secret",
            Description = "Customer download-only access",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
            CanUpload = false,
            CanDownload = true,
            CanQuery = false,
            CanDelete = false
        };

        // Assert
        Assert.False(app.CanUpload);
        Assert.True(app.CanDownload);
        Assert.False(app.CanQuery);
        Assert.False(app.CanDelete);
    }

    [Fact]
    public void Application_CanCreateFullAccessApp()
    {
        // Arrange & Act
        var app = new Application
        {
            Id = "AdminApp",
            SecretKey = "admin-secret",
            Description = "Admin full access",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true,
            CanUpload = true,
            CanDownload = true,
            CanQuery = true,
            CanDelete = true
        };

        // Assert - All permissions enabled
        Assert.True(app.CanUpload);
        Assert.True(app.CanDownload);
        Assert.True(app.CanQuery);
        Assert.True(app.CanDelete);
    }
}
