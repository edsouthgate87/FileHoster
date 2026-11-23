using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileHosterProxy.Models;
using Microsoft.AspNetCore.Mvc;

namespace FileHosterProxy.Controllers;

/// <summary>
/// Proxy endpoint for Avalonia client apps to securely call FileHoster.
/// 
/// Architecture:
/// Avalonia Client → This Proxy (no secrets) → FileHoster API (uses HMAC signature)
/// 
/// Security Model:
/// - Client never sees the API secret
/// - All HMAC signatures computed server-side
/// - Signed download URLs generated server-side  
/// - Client makes simple HTTP GET requests (no auth needed)
/// </summary>
[ApiController]
[Route("api/v1/filehoster")]
public class FileHosterProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _fileHosterBaseUrl;
    private readonly string _fileHosterPublicBaseUrl;
    private readonly string _applicationId;
    private readonly string _secretKey;
    private readonly ILogger<FileHosterProxyController> _logger;

    public FileHosterProxyController(
        IHttpClientFactory httpClientFactory, 
        IConfiguration config,
        ILogger<FileHosterProxyController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        
        // Load from environment or appsettings.json
        _fileHosterBaseUrl = config["FileHoster:BaseUrl"] ?? "http://filehoster:5000";
        _fileHosterPublicBaseUrl = config["FileHoster:PublicBaseUrl"] ?? _fileHosterBaseUrl;
        _applicationId = config["FileHoster:ApplicationId"] ?? "SampleApp";
        _secretKey = config["FileHoster:SecretKey"] ?? throw new InvalidOperationException(
            "FileHoster:SecretKey not configured. Set via environment variable or appsettings.json");
    }

    /// <summary>
    /// Get list of all available files.
    /// 
    /// GET /api/v1/filehoster/files
    /// 
    /// Returns: [{ id, filename, fileSize, createdAt, hash, ... }]
    /// </summary>
    [HttpGet("files")]
    public async Task<IActionResult> GetFiles()
    {
        try
        {
            _logger.LogInformation("Proxy: Fetching file list for appId={AppId}", _applicationId);
            
            var client = _httpClientFactory.CreateClient();
            var url = $"{_fileHosterBaseUrl}/api/{_applicationId}/files";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHmacSignature(request, _applicationId, _secretKey);
            
            var response = await client.SendAsync(request);
            
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("FileHoster API error: {StatusCode} {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);
                return StatusCode((int)response.StatusCode, new { error = "Failed to retrieve files" });
            }
            
            _logger.LogInformation("Proxy: Successfully retrieved file list");
            return Ok(System.Text.Json.JsonSerializer.Deserialize<dynamic>(responseContent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching files");
            return StatusCode(500, new { error = "Internal server error", traceId = HttpContext.TraceIdentifier });
        }
    }

    /// <summary>
    /// Generate a secure download link for a file.
    /// Link is signed and expires in 60 minutes.
    /// 
    /// GET /api/v1/filehoster/download/{fileId}
    /// 
    /// Returns: { fileId, filename, downloadUrl, expiresIn, contentLength }
    /// </summary>
    [HttpGet("download/{fileId}")]
    [ProducesResponseType<DownloadLinkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<DownloadLinkResponse>> GenerateDownloadLink(
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Proxy: Generating download link for {FileId}", fileId);
            
            if (string.IsNullOrWhiteSpace(fileId) || fileId.Length != 32)
                return BadRequest(new { error = "Invalid file ID format" });
            
            var client = _httpClientFactory.CreateClient();
            var url = $"{_fileHosterBaseUrl}/api/{_applicationId}/files/{fileId}/download-url";
            
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            AddHmacSignature(request, _applicationId, _secretKey);
            var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return NotFound(new { error = "File not found" });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("FileHoster API error: {StatusCode}", response.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to generate download link" });
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var upstreamLink = JsonSerializer.Deserialize<UpstreamDownloadLinkResponse>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (upstreamLink is null || !Uri.TryCreate(upstreamLink.DownloadUrl, UriKind.Absolute, out var internalDownloadUri))
            {
                _logger.LogError("FileHoster returned an invalid download-link response");
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Invalid download link response" });
            }

            var publicBaseUri = new Uri(_fileHosterPublicBaseUrl, UriKind.Absolute);
            var publicDownloadUrl =
                publicBaseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') +
                internalDownloadUri.PathAndQuery;

            _logger.LogInformation("Proxy: Download link generated for {FileId}", fileId);
            return Ok(new DownloadLinkResponse(
                publicDownloadUrl,
                upstreamLink.ExpiresAt,
                upstreamLink.ExpirationMinutes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download link for {FileId}", fileId);
            return StatusCode(500, new { error = "Internal server error", traceId = HttpContext.TraceIdentifier });
        }
    }

    /// <summary>
    /// Get file metadata without downloading.
    /// 
    /// GET /api/v1/filehoster/metadata/{fileId}
    /// 
    /// Returns: { id, filename, fileSize, createdAt, contentType }
    /// </summary>
    [HttpGet("metadata/{fileId}")]
    public async Task<IActionResult> GetFileMetadata(string fileId)
    {
        try
        {
            _logger.LogInformation("Proxy: Fetching metadata for {FileId}", fileId);
            
            if (string.IsNullOrWhiteSpace(fileId) || fileId.Length != 32)
                return BadRequest(new { error = "Invalid file ID format" });
            
            var client = _httpClientFactory.CreateClient();
            var url = $"{_fileHosterBaseUrl}/api/{_applicationId}/files/{fileId}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHmacSignature(request, _applicationId, _secretKey);
            
            var response = await client.SendAsync(request);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return NotFound(new { error = "File not found" });
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("FileHoster API error: {StatusCode}", response.StatusCode);
                return StatusCode((int)response.StatusCode, new { error = "Failed to retrieve metadata" });
            }
            
            var content = await response.Content.ReadAsStringAsync();
            return Ok(System.Text.Json.JsonSerializer.Deserialize<dynamic>(content));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching metadata for {FileId}", fileId);
            return StatusCode(500, new { error = "Internal server error", traceId = HttpContext.TraceIdentifier });
        }
    }

    /// <summary>
    /// Health check endpoint.
    /// GET /api/v1/filehoster/health
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync($"{_fileHosterBaseUrl}/health");
            
            if (response.IsSuccessStatusCode)
                return Ok(new { status = "healthy", fileHosterUrl = _fileHosterBaseUrl });
            else
                return StatusCode(503, new { status = "unhealthy", error = "FileHoster not responding" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(503, new { status = "unhealthy", error = ex.Message });
        }
    }

    /// <summary>
    /// Adds HMAC-SHA256 signature to requests.
    /// This authenticates with FileHoster without exposing secrets to clients.
    /// 
    /// Signature format: METHOD|PATH|APPID|BODY
    /// </summary>
    private void AddHmacSignature(HttpRequestMessage request, string appId, string secretKey)
    {
        var content = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? Array.Empty<byte>();
        var method = request.Method.Method.ToUpperInvariant();
        var path = request.RequestUri?.PathAndQuery ?? "";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var signatureData = $"{method}\n{path}\n{appId}\n{timestamp}\n{nonce}\n{contentHash}\n";

        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureData));
            var signature = Convert.ToBase64String(hash);

            request.Headers.Add("X-App-Id", appId);
            request.Headers.Add("X-Signature", signature);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Nonce", nonce);
            request.Headers.Add("X-Content-SHA256", contentHash);

            _logger.LogDebug("HMAC signature added for appId={AppId} path={Path}", appId, path);
        }
    }
}
