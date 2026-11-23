using FileHoster.Models;
using FileHoster.Models.Dtos;
using FileHoster.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace FileHoster.Controllers;

[ApiController]
[Route("api/{appId}/files")]
public class FilesController : ControllerBase
{
    private readonly IFileService _fileService;
    private readonly IStorageService _storageService;
    private readonly IApplicationService _appService;
    private readonly ISignatureService _signatureService;
    private readonly Configuration.StorageOptions _storageOptions;
    private readonly Configuration.SignatureOptions _signatureOptions;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        IFileService fileService,
        IStorageService storageService,
        IApplicationService appService,
        ISignatureService signatureService,
        Microsoft.Extensions.Options.IOptions<Configuration.StorageOptions> storageOptions,
        Microsoft.Extensions.Options.IOptions<Configuration.SignatureOptions> signatureOptions,
        ILogger<FilesController> logger)
    {
        _fileService = fileService;
        _storageService = storageService;
        _appService = appService;
        _signatureService = signatureService;
        _storageOptions = storageOptions.Value;
        _signatureOptions = signatureOptions.Value;
        _logger = logger;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<FileResponseDto>> UploadFile(string appId, [FromForm] UploadFileRequest request)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId (prevents authorization bypass)
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        // Check upload permission
        if (!app.CanUpload)
            return Forbid("This application does not have permission to upload files");

        // Validate file size
        if (!_fileService.ValidateFileSize(request.File.Length, _storageOptions.MaxFileSizeBytes))
            return BadRequest($"File size exceeds maximum of {_storageOptions.MaxFileSizeBytes / (1024 * 1024)}MB");

        // Validate file extension
        if (!_fileService.ValidateFileExtension(request.File.FileName, _storageOptions.AllowedExtensions))
            return BadRequest($"File extension not allowed. Allowed: {string.Join(", ", _storageOptions.AllowedExtensions)}");

        // Validate metadata JSON
        try
        {
            JsonSerializer.Deserialize<FileMetadataDto>(request.Metadata);
        }
        catch
        {
            return BadRequest("Invalid metadata JSON");
        }

        try
        {
            if (!MatchesHash($"{request.Metadata}\n{request.File.FileName}", Request.Headers["X-Metadata-SHA256"].ToString()))
                return Unauthorized("Upload metadata does not match signed manifest");
            var response = await SaveVerifiedFileAsync(appId, request.File, request.Metadata, Request.Headers["X-Content-SHA256"].ToString());
            _logger.LogInformation("File uploaded successfully by app '{AppId}': {FileId}", appId, response.FileId);
            return CreatedAtAction(nameof(GetFile), new { appId, fileId = response.FileId }, response);
        }
        catch (InvalidDataException)
        {
            return Unauthorized("Uploaded content does not match signed manifest");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file for app '{AppId}'", appId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }

    [HttpPost("upload-bulk")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<BulkUploadResponseDto>> BulkUploadFiles(string appId, [FromForm] BulkUploadFileRequest request)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        // Check upload permission
        if (!app.CanUpload)
            return Forbid("This application does not have permission to upload files");

        var response = new BulkUploadResponseDto();

        // Validate that file and metadata counts match
        if (request.Files.Count != request.Metadata.Count)
        {
            return BadRequest("Number of files must match number of metadata entries");
        }

        var fileHashes = new List<string>();
        var metadataHashes = new List<string>();
        for (var i = 0; i < request.Files.Count; i++)
        {
            await using var stream = request.Files[i].OpenReadStream();
            fileHashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, HttpContext.RequestAborted)).ToLowerInvariant());
            metadataHashes.Add(Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{request.Metadata[i]}\n{request.Files[i].FileName}"))).ToLowerInvariant());
        }
        if (!MatchesHash(string.Join("\n", fileHashes), Request.Headers["X-Content-SHA256"].ToString()) ||
            !MatchesHash(string.Join("\n", metadataHashes), Request.Headers["X-Metadata-SHA256"].ToString()))
            return Unauthorized("Bulk contents do not match signed manifest");

        // Process each file
        for (int i = 0; i < request.Files.Count; i++)
        {
            var file = request.Files[i];
            var metadata = request.Metadata[i];

            try
            {
                // Validate file size
                if (!_fileService.ValidateFileSize(file.Length, _storageOptions.MaxFileSizeBytes))
                {
                    response.FailureCount++;
                    response.Errors.Add($"{file.FileName}: File size exceeds maximum of {_storageOptions.MaxFileSizeBytes / (1024 * 1024)}MB");
                    continue;
                }

                // Validate file extension
                if (!_fileService.ValidateFileExtension(file.FileName, _storageOptions.AllowedExtensions))
                {
                    response.FailureCount++;
                    response.Errors.Add($"{file.FileName}: File extension not allowed");
                    continue;
                }

                // Validate metadata JSON
                try
                {
                    JsonSerializer.Deserialize<FileMetadataDto>(metadata);
                }
                catch
                {
                    response.FailureCount++;
                    response.Errors.Add($"{file.FileName}: Invalid metadata JSON");
                    continue;
                }

                var fileResponse = await SaveVerifiedFileAsync(appId, file, metadata, fileHashes[i]);
                response.UploadedFiles.Add(fileResponse);
                response.SuccessCount++;
                _logger.LogInformation("File uploaded in bulk by app '{AppId}': {FileId}", appId, fileResponse.FileId);
            }
            catch (Exception ex)
            {
                response.FailureCount++;
                response.Errors.Add($"{file.FileName}: upload failed");
                _logger.LogError(ex, "Error uploading file '{FileName}' in bulk for app '{AppId}'", file.FileName, appId);
            }
        }

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedFilesResponse>> QueryFiles(string appId, [FromQuery] QueryFilesRequest? query)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId (prevents authorization bypass)
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        try
        {
            var pageNumber = query?.PageNumber ?? 1;
            var pageSize = query?.PageSize ?? 100;
            
            var (files, total) = await _fileService.GetFilesByAppIdPaginatedAsync(
                appId, pageNumber, pageSize, query?.Version, query?.Filename);

            var items = files.Select(f => new FileResponseDto
            {
                FileId = f.Id,
                AppId = f.AppId,
                Filename = f.Filename,
                FileSize = f.FileSize,
                ContentHash = f.ContentHash,
                MetadataJsonHash = f.MetadataJsonHash,
                CreatedAt = f.CreatedAt,
                Metadata = JsonSerializer.Deserialize<object>(f.Metadata)
            }).ToList();

            var totalPages = (total + pageSize - 1) / pageSize;
            
            var response = new PaginatedFilesResponse
            {
                Total = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalPages = totalPages,
                Items = items
            };

            _logger.LogInformation("Query files for app '{AppId}': Found {Total} files (page {Page}/{TotalPages})", 
                appId, total, pageNumber, totalPages);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying files for app '{AppId}'", appId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }

    [HttpGet("{fileId}")]
    public async Task<ActionResult<FileResponseDto>> GetFile(string appId, string fileId)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId (prevents authorization bypass)
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        try
        {
            var file = await _fileService.GetFileByIdAsync(fileId, appId);
            if (file == null)
                return NotFound("File not found");

            var response = new FileResponseDto
            {
                FileId = file.Id,
                AppId = file.AppId,
                Filename = file.Filename,
                FileSize = file.FileSize,
                ContentHash = file.ContentHash,
                MetadataJsonHash = file.MetadataJsonHash,
                CreatedAt = file.CreatedAt,
                Metadata = JsonSerializer.Deserialize<object>(file.Metadata)
            };

            _logger.LogInformation("Retrieved file '{FileId}' for app '{AppId}'", fileId, appId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving file '{FileId}' for app '{AppId}'", fileId, appId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }

    [HttpPost("{fileId}/download-url")]
    public async Task<ActionResult<DownloadUrlResponseDto>> GenerateDownloadUrl(string appId, string fileId)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId (prevents authorization bypass)
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        try
        {
            var file = await _fileService.GetFileByIdAsync(fileId, appId);
            if (file == null)
                return NotFound("File not found");

            var token = _signatureService.GenerateDownloadToken(fileId, appId, app.SecretKey);
            var expiresAt = DateTime.UtcNow.AddMinutes(_signatureOptions.ExpirationMinutes);

            var downloadUrl = $"{Request.Scheme}://{Request.Host}/download/{Uri.EscapeDataString(token)}";

            var response = new DownloadUrlResponseDto
            {
                DownloadUrl = downloadUrl,
                ExpiresAt = expiresAt,
                ExpirationMinutes = _signatureOptions.ExpirationMinutes
            };

            _logger.LogInformation("Generated download URL for file '{FileId}' in app '{AppId}'", fileId, appId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download URL for file '{FileId}' in app '{AppId}'", fileId, appId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }

    [HttpDelete("{fileId}")]
    public async Task<IActionResult> DeleteFile(string appId, string fileId)
    {
        var app = HttpContext.Items["Application"] as Application;
        if (app == null)
            return Unauthorized("Application not found");

        // Validate that authenticated app matches the requested appId (prevents authorization bypass)
        if (app.Id != appId)
            return Forbid($"Access denied: authenticated as '{app.Id}', cannot access '{appId}'");

        // Check delete permission
        if (!app.CanDelete)
            return Forbid("This application does not have permission to delete files");

        try
        {
            var file = await _fileService.GetFileByIdAsync(fileId, appId);
            if (file == null)
                return NotFound("File not found");

            var quarantinedPath = _storageService.QuarantineFile(file.FilePath);
            try
            {
                await _fileService.DeleteFileAsync(fileId, appId);
            }
            catch
            {
                _storageService.RestoreFile(quarantinedPath, file.FilePath);
                throw;
            }
            try
            {
                _storageService.DeleteFile(quarantinedPath);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Database delete committed, quarantined file needs cleanup: {Path}", quarantinedPath);
            }

            _logger.LogInformation("Deleted file '{FileId}' for app '{AppId}'", fileId, appId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file '{FileId}' for app '{AppId}'", fileId, appId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }

    private static bool MatchesHash(string value, string expected)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            return false;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
    }

    private async Task<FileResponseDto> SaveVerifiedFileAsync(string appId, IFormFile upload, string metadata, string expectedHash)
    {
            await using var input = upload.OpenReadStream();
            var path = await _storageService.SaveFileAsync(input, appId, upload.FileName);
            HostedFile saved;
            string contentHash;
            try
            {
                await using (var stored = _storageService.GetFileStream(path))
                    contentHash = "sha256:" + Convert.ToHexString(
                        await SHA256.HashDataAsync(stored, HttpContext.RequestAborted)).ToLowerInvariant();
                if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit) ||
                    !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(contentHash["sha256:".Length..]),
                        Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException("Uploaded file hash mismatch");

                saved = await _fileService.SaveFileAsync(
                    appId, upload.FileName, path, upload.Length, metadata, contentHash);
            }
            catch
            {
                _storageService.DeleteFile(path);
                throw;
            }
            return new FileResponseDto
            {
                FileId = saved.Id,
                AppId = saved.AppId,
                Filename = saved.Filename,
                FileSize = saved.FileSize,
                ContentHash = saved.ContentHash,
                MetadataJsonHash = saved.MetadataJsonHash,
                CreatedAt = saved.CreatedAt,
                Metadata = JsonSerializer.Deserialize<object>(saved.Metadata)
            };
    }
}
