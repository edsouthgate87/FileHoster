using FileHoster.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace FileHoster.Services;

public interface IApplicationService
{
    Task<Models.Application?> GetApplicationAsync(string appId);
    Task<Models.Application> CreateApplicationAsync(string appId, string secretKey, string? description = null);
    Task<bool> ValidateApplicationAsync(string appId, string secretKey);
    Task<IEnumerable<Models.Application>> GetAllApplicationsAsync();
}

public class ApplicationService : IApplicationService
{
    private readonly FileHosterContext _context;

    public ApplicationService(FileHosterContext context)
    {
        _context = context;
    }

    public async Task<Models.Application?> GetApplicationAsync(string appId)
    {
        return await _context.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appId && a.IsActive);
    }

    public async Task<Models.Application> CreateApplicationAsync(string appId, string secretKey, string? description = null)
    {
        var app = new Models.Application
        {
            Id = appId,
            SecretKey = secretKey,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _context.Applications.Add(app);
        await _context.SaveChangesAsync();

        return app;
    }

    public async Task<bool> ValidateApplicationAsync(string appId, string secretKey)
    {
        var app = await GetApplicationAsync(appId);
        if (app == null)
            return false;
        
        // Use constant-time comparison to prevent timing attacks
        try
        {
            var expectedBytes = Encoding.UTF8.GetBytes(app.SecretKey);
            var actualBytes = Encoding.UTF8.GetBytes(secretKey);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IEnumerable<Models.Application>> GetAllApplicationsAsync()
    {
        return await _context.Applications
            .AsNoTracking()
            .Where(a => a.IsActive)
            .ToListAsync();
    }
}
