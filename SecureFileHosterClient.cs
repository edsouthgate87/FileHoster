using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AvaloniaApp.Services;

/// <summary>
/// Secure FileHoster client for Avalonia apps.
/// 
/// IMPORTANT: No secrets are stored in the client app.
/// All requests go through the backend proxy which handles authentication.
/// 
/// Benefits:
/// - No secrets in client code
/// - Backend validates all requests
/// - Signed URLs generated securely
/// - Response data validated server-side
/// - Easy to audit and update authorization
/// </summary>
public class SecureFileHosterClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _proxyBaseUrl;
    private readonly ILogger _logger;
    private readonly HttpClient? _downloadClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Initialize with proxy URL (no secrets needed!)
    /// 
    /// Example:
    /// var client = new SecureFileHosterClient("https://yourapi.example.com");
    /// </summary>
    public SecureFileHosterClient(string proxyBaseUrl, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(proxyBaseUrl))
            throw new ArgumentNullException(nameof(proxyBaseUrl), "Proxy URL is required");

        _proxyBaseUrl = proxyBaseUrl.TrimEnd('/');
        _httpClient = new HttpClient();
        _ownsHttpClient = true;
        _logger = logger ?? new LoggerConfiguration().CreateLogger();
    }

    /// <summary>
    /// Initialize with custom HttpClient (useful for DI or testing)
    /// </summary>
    public SecureFileHosterClient(HttpClient httpClient, string proxyBaseUrl, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _proxyBaseUrl = proxyBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(proxyBaseUrl));
        _logger = logger ?? new LoggerConfiguration().CreateLogger();
    }

    internal SecureFileHosterClient(HttpClient proxyClient, HttpClient downloadClient, string proxyBaseUrl)
        : this(proxyClient, proxyBaseUrl)
    {
        _downloadClient = downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
    }

    /// <summary>
    /// Get list of available files.
    /// No authentication needed on client side.
    /// 
    /// Usage:
    /// var files = await client.GetFilesAsync();
    /// foreach (var file in files)
    /// {
    ///     Console.WriteLine($"{file.Filename} ({file.FileSize} bytes)");
    /// }
    /// </summary>
    public async Task<List<FileInfo>> GetFilesAsync()
    {
        try
        {
            _logger.Information("Client: Requesting file list from proxy");
            
            var url = $"{_proxyBaseUrl}/api/v1/filehoster/files";
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error("Client: Failed to get files (HTTP {StatusCode})", response.StatusCode);
                throw new HttpRequestException($"Proxy returned {response.StatusCode}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<FileListResponse>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new FileListResponse();
            var files = result.Items;

            _logger.Information("Client: Successfully retrieved {Count} files", files.Count);
            return files;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Client: Error retrieving file list");
            throw;
        }
    }

    /// <summary>
    /// Get metadata for a specific file.
    /// No authentication needed on client side.
    /// 
    /// Usage:
    /// var metadata = await client.GetFileMetadataAsync("your-file-id");
    /// Console.WriteLine($"File: {metadata.Filename}, Size: {metadata.FileSize}");
    /// </summary>
    public async Task<FileInfo?> GetFileMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentNullException(nameof(fileId), "File ID is required");
            
            _logger.Information("Client: Requesting metadata for fileId={FileId}", fileId);
            
            var url = $"{_proxyBaseUrl}/api/v1/filehoster/metadata/{Uri.EscapeDataString(fileId)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.Warning("Client: File not found: {FileId}", fileId);
                return null;
            }
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error("Client: Failed to get metadata (HTTP {StatusCode})", response.StatusCode);
                throw new HttpRequestException($"Proxy returned {response.StatusCode}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var metadata = JsonSerializer.Deserialize<FileInfo>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            _logger.Information("Client: Successfully retrieved metadata for fileId={FileId}", fileId);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Client: Error retrieving metadata for fileId={FileId}", fileId);
            throw;
        }
    }

    /// <summary>
    /// Generate a secure download link that expires in 60 minutes.
    /// The link is signed server-side and cannot be forged.
    /// 
    /// Usage:
    /// var downloadLink = await client.GetDownloadLinkAsync("your-file-id");
    /// // Open downloadLink.DownloadUrl in a browser or pass it to a download handler.
    /// Console.WriteLine($"Download: {downloadLink.DownloadUrl}");
    /// Console.WriteLine($"Expires at: {downloadLink.ExpiresAt}");
    /// </summary>
    public async Task<DownloadLink> GetDownloadLinkAsync(string fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentNullException(nameof(fileId), "File ID is required");
            
            _logger.Information("Client: Requesting download link for fileId={FileId}", fileId);
            
            var url = $"{_proxyBaseUrl}/api/v1/filehoster/download/{Uri.EscapeDataString(fileId)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.Warning("Client: File not found: {FileId}", fileId);
                throw new HttpRequestException("File not found");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error("Client: Failed to get download link (HTTP {StatusCode})", response.StatusCode);
                throw new HttpRequestException($"Proxy returned {response.StatusCode}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            var downloadLink = JsonSerializer.Deserialize<DownloadLink>(content, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Invalid download link response");
            
            _logger.Information("Client: Successfully generated download link for fileId={FileId}", fileId);
            return downloadLink;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Client: Error generating download link for fileId={FileId}", fileId);
            throw;
        }
    }

    /// <summary>
    /// Downloads a hosted file through a temporary signed URL.
    /// </summary>
    public async Task DownloadFileAsync(
        string fileId,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentNullException(nameof(destinationPath));

        var metadata = await GetFileMetadataAsync(fileId, cancellationToken)
            ?? throw new FileNotFoundException("File metadata not found", fileId);
        var downloadLink = await GetDownloadLinkAsync(fileId, cancellationToken);
        // Do not forward proxy HttpClient credentials to the signed download origin.
        using var ownedDownloadClient = _downloadClient is null ? new HttpClient() : null;
        using var response = await (_downloadClient ?? ownedDownloadClient!).GetAsync(
            downloadLink.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes.HasValue && totalBytes.Value != metadata.FileSize)
            throw new InvalidDataException("Download size does not match file metadata");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var fullPath = Path.GetFullPath(destinationPath);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            long downloadedBytes = 0;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hasher.AppendData(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;
                    if (totalBytes is > 0)
                        progress?.Report(downloadedBytes * 100d / totalBytes.Value);
                }
                await destination.FlushAsync(cancellationToken);
            }
            if (downloadedBytes != metadata.FileSize)
                throw new InvalidDataException("Downloaded file is incomplete");
            if (metadata.ContentHash is { } expectedHash)
            {
                if (!expectedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals("sha256:" + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
                        expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded file hash does not match server metadata");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Check if the proxy (and FileHoster) service is healthy.
    /// 
    /// Usage:
    /// if (await client.IsHealthyAsync())
    ///     Console.WriteLine("Service is ready");
    /// </summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var url = $"{_proxyBaseUrl}/api/v1/filehoster/health";
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Client: Health check failed");
            return false;
        }
    }

    /// <summary>
    /// Dispose HTTP client resources
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>
/// File metadata returned from the proxy.
/// All data is validated by the backend before sending to client.
/// </summary>
public class FileInfo
{
    public string FileId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ContentHash { get; set; }
    public string? MetadataJsonHash { get; set; }
    public JsonElement? Metadata { get; set; }
    public string? ContentType { get; set; }

    public string Id => FileId;
    public string? Hash => ContentHash;

    public override string ToString()
        => $"{Filename} ({FormatBytes(FileSize)}) - {CreatedAt:yyyy-MM-dd}";

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}

public class FileListResponse
{
    public int Total { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public List<FileInfo> Items { get; set; } = new();
}

/// <summary>
/// Secure download link generated by the backend.
/// The URL is signed and cannot be forged by the client.
/// The link expires after the specified time.
/// </summary>
public class DownloadLink
{
    public string DownloadUrl { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public int ExpirationMinutes { get; set; }
    public bool HasExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public override string ToString()
        => $"Download expires at {ExpiresAt:yyyy-MM-dd HH:mm:ss}";
}
