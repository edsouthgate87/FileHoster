using FileHoster.Models;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace FileHoster.Services;

public interface IFileService
{
    Task<HostedFile?> GetFileByIdAsync(string fileId, string appId);
    Task<IEnumerable<HostedFile>> GetFilesByAppIdAsync(string appId, string? version = null, string? filename = null);
    Task<(IEnumerable<HostedFile> Items, int Total)> GetFilesByAppIdPaginatedAsync(string appId, int pageNumber = 1, int pageSize = 100, string? version = null, string? filename = null);
    Task<HostedFile> SaveFileAsync(string appId, string filename, string filePath, long fileSize, string metadata, string? contentHash = null);
    Task<bool> DeleteFileAsync(string fileId, string appId);
    bool ValidateFileSize(long fileSize, long maxBytes);
    bool ValidateFileExtension(string filename, List<string> allowedExtensions);
}

public class FileService : IFileService
{
    private readonly FileHoster.Data.FileHosterContext _context;
    private readonly ILogger<FileService> _logger;
    private readonly ISignatureService _signatureService;
    
    // Maximum length for search parameters to prevent DoS attacks
    private const int MaxSearchLength = 256;

    public FileService(FileHoster.Data.FileHosterContext context, ILogger<FileService> logger, ISignatureService signatureService)
    {
        _context = context;
        _logger = logger;
        _signatureService = signatureService;
    }

    public async Task<HostedFile?> GetFileByIdAsync(string fileId, string appId)
    {
        return await _context.Files
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.AppId == appId);
    }

    public async Task<IEnumerable<HostedFile>> GetFilesByAppIdAsync(string appId, string? version = null, string? filename = null)
    {
        var query = _context.Files
            .AsNoTracking()
            .Where(f => f.AppId == appId);

        if (!string.IsNullOrEmpty(version))
        {
            // Validate input to prevent DoS attacks via complex queries
            if (version.Length > MaxSearchLength)
            {
                _logger.LogWarning("Version search parameter exceeds max length: {Length} > {Max}", 
                    version.Length, MaxSearchLength);
                return Enumerable.Empty<HostedFile>();
            }

            // Use parameterized query with EF's JSON operator for PostgreSQL
            // This searches for the exact version value in the metadata JSON
            var versionSearchPattern = $"\"version\":\"{EscapeJsonString(version)}\"";
            query = query.Where(f => f.Metadata.Contains(versionSearchPattern));
        }

        if (!string.IsNullOrEmpty(filename))
        {
            // Validate input length
            if (filename.Length > MaxSearchLength)
            {
                _logger.LogWarning("Filename search parameter exceeds max length: {Length} > {Max}", 
                    filename.Length, MaxSearchLength);
                return Enumerable.Empty<HostedFile>();
            }

            // Use parameterized Contains - EF Core will parameterize this at the database level
            query = query.Where(f => f.Filename.Contains(filename));
        }

        return await query.OrderByDescending(f => f.CreatedAt).ToListAsync();
    }

    public async Task<(IEnumerable<HostedFile> Items, int Total)> GetFilesByAppIdPaginatedAsync(string appId, int pageNumber = 1, int pageSize = 100, string? version = null, string? filename = null)
    {
        // Validate pagination parameters
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 1000) pageSize = 1000;  // Prevent abuse

        var query = _context.Files
            .AsNoTracking()
            .Where(f => f.AppId == appId);

        if (!string.IsNullOrEmpty(version))
        {
            if (version.Length > MaxSearchLength)
            {
                _logger.LogWarning("Version search parameter exceeds max length: {Length} > {Max}", 
                    version.Length, MaxSearchLength);
                return (Enumerable.Empty<HostedFile>(), 0);
            }
            query = query.Where(f => f.Metadata.Contains(version));
        }

        if (!string.IsNullOrEmpty(filename))
        {
            if (filename.Length > MaxSearchLength)
            {
                _logger.LogWarning("Filename search parameter exceeds max length: {Length} > {Max}", 
                    filename.Length, MaxSearchLength);
                return (Enumerable.Empty<HostedFile>(), 0);
            }
            query = query.Where(f => f.Filename.Contains(filename));
        }

        // Get total count before pagination
        var total = await query.CountAsync();

        // Apply pagination
        var skip = (pageNumber - 1) * pageSize;
        var items = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    public async Task<HostedFile> SaveFileAsync(string appId, string filename, string filePath, long fileSize, string metadata, string? contentHash = null)
    {
        // Compute hash of metadata JSON to verify integrity on client
        var metadataJsonHash = _signatureService.ComputeMetadataJsonHash(metadata);
        
        var file = new HostedFile
        {
            Id = Guid.NewGuid().ToString("N"),
            AppId = appId,
            Filename = filename,
            FilePath = filePath,
            FileSize = fileSize,
            ContentHash = contentHash,
            MetadataJsonHash = metadataJsonHash,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow
        };

        _context.Files.Add(file);
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("Saved file {FileId} with metadata hash {MetadataHash}", file.Id, metadataJsonHash);
        return file;
    }

    public async Task<bool> DeleteFileAsync(string fileId, string appId)
    {
        var file = await GetFileByIdAsync(fileId, appId);
        if (file == null)
            return false;

        _context.Files.Remove(file);
        await _context.SaveChangesAsync();
        return true;
    }

    public bool ValidateFileSize(long fileSize, long maxBytes)
    {
        return fileSize > 0 && fileSize <= maxBytes;
    }

    public bool ValidateFileExtension(string filename, List<string> allowedExtensions)
    {
        var ext = System.IO.Path.GetExtension(filename).ToLower();
        return allowedExtensions.Contains(ext);
    }

    /// <summary>
    /// Escapes special characters in a string to be safe for JSON searching.
    /// This prevents injection attacks when searching metadata.
    /// </summary>
    private static string EscapeJsonString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
