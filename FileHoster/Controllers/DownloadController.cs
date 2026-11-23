using FileHoster.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileHoster.Controllers;

[ApiController]
[Route("download")]
public class DownloadController : ControllerBase
{
    private readonly IFileService _fileService;
    private readonly IStorageService _storageService;
    private readonly IApplicationService _appService;
    private readonly ISignatureService _signatureService;
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(
        IFileService fileService,
        IStorageService storageService,
        IApplicationService appService,
        ISignatureService signatureService,
        ILogger<DownloadController> logger)
    {
        _fileService = fileService;
        _storageService = storageService;
        _appService = appService;
        _signatureService = signatureService;
        _logger = logger;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> DownloadFile(string token)
    {
        try
        {
            // Extract appId from token (format: fileId|appId|expirationTime|signature)
            var parts = token.Split('|');
            if (parts.Length != 4)
            {
                _logger.LogWarning("Invalid token format from {RemoteIP}", HttpContext.Connection.RemoteIpAddress);
                return Unauthorized("Invalid token format");
            }

            var appId = parts[1];
            
            // Fetch only the required application
            var app = await _appService.GetApplicationAsync(appId);
            if (app == null)
            {
                _logger.LogWarning("Invalid application ID '{AppId}' in token from {RemoteIP}", 
                    appId, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized("Invalid application");
            }

            // Verify token with the specific app's secret key
            var (isValid, fileId, _) = _signatureService.VerifyDownloadToken(token, app.SecretKey);
            if (!isValid || fileId == null)
            {
                _logger.LogWarning("Invalid or expired download token for app '{AppId}' from {RemoteIP}", 
                    appId, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized("Invalid or expired token");
            }

            var file = await _fileService.GetFileByIdAsync(fileId, appId);
            if (file == null || !_storageService.FileExists(file.FilePath))
            {
                _logger.LogWarning("Downloaded file not found: {FileId} for app '{AppId}'", fileId, appId);
                return NotFound("File not found");
            }

            // Verify app has download permission
            if (!app.CanDownload)
            {
                _logger.LogWarning("Download permission denied for app '{AppId}' file '{FileId}' from {RemoteIP}", 
                    appId, fileId, HttpContext.Connection.RemoteIpAddress);
                return Forbid("This application does not have permission to download files");
            }

            var stream = _storageService.GetFileStream(file.FilePath);
            if (stream == null)
            {
                _logger.LogWarning("Could not open file stream: {FileId} for app '{AppId}'", fileId, appId);
                return NotFound("File not found");
            }

            try
            {
                _logger.LogInformation("File download started: {FileId} ({FileSize} bytes) for app '{AppId}' from {RemoteIP}", 
                    fileId, file.FileSize, appId, HttpContext.Connection.RemoteIpAddress);
                return File(stream, "application/octet-stream", file.Filename, enableRangeProcessing: true);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing download request from {RemoteIP}", HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "An error occurred processing your request" });
        }
    }
}
