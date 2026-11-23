using FileHoster.Services;
using Microsoft.EntityFrameworkCore;
using FileHoster.Data;
using System.Net;

namespace FileHoster.Configuration;

public class HmacAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HmacAuthenticationMiddleware> _logger;

    public HmacAuthenticationMiddleware(RequestDelegate next, ILogger<HmacAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IApplicationService appService, ISignatureService signatureService, FileHosterContext database, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip authentication for public download endpoint
        if (path.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Skip authentication for health check and Swagger
        if (path == "/" || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) || path == "/health")
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (environment.IsProduction() && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp?.IsIPv4MappedToIPv6 == true)
                remoteIp = remoteIp.MapToIPv4();
            var allowed = configuration["TrustedApiNetworks"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(IPNetwork.Parse).Any(network => remoteIp is not null && network.Contains(remoteIp)) ?? false;
            if (!allowed)
            {
                _logger.LogWarning("Denied administrative API request from {RemoteIP}", remoteIp);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        // Validate HMAC for protected endpoints
        var appId = context.Request.Headers["X-App-Id"].ToString();
        var signature = context.Request.Headers["X-Signature"].ToString();

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(signature) ||
            string.IsNullOrEmpty(context.Request.Headers["X-Timestamp"]) ||
            string.IsNullOrEmpty(context.Request.Headers["X-Nonce"]))
        {
            _logger.LogWarning("Missing authentication headers from {RemoteIP} for {Path}", 
                context.Connection.RemoteIpAddress, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-App-Id or X-Signature header" }).ConfigureAwait(false);
            return;
        }

        var app = await appService.GetApplicationAsync(appId).ConfigureAwait(false);
        if (app == null)
        {
            _logger.LogWarning("Invalid application ID '{AppId}' from {RemoteIP} for {Path}", 
                appId, context.Connection.RemoteIpAddress, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid application ID" }).ConfigureAwait(false);
            return;
        }

        // Multipart bodies are not buffered for signing: the signed manifest is checked after the file streams to disk.
        var isValidSignature = await signatureService.VerifyRequestSignature(context.Request, signature, app.SecretKey).ConfigureAwait(false);
        if (!isValidSignature)
        {
            _logger.LogWarning("Invalid signature from application '{AppId}' ({RemoteIP}) for {Path} {Method}", 
                appId, context.Connection.RemoteIpAddress, path, context.Request.Method);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid signature" }).ConfigureAwait(false);
            return;
        }

        var nonce = context.Request.Headers["X-Nonce"].ToString();
        // A timestamp up to five minutes in the future remains valid for another five minutes.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(
            long.Parse(context.Request.Headers["X-Timestamp"]!)).AddMinutes(5).UtcDateTime;
        var inserted = await database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"RequestNonces\" (\"AppId\", \"Nonce\", \"ExpiresAt\") VALUES ({appId}, {nonce}, {expiresAt}) ON CONFLICT DO NOTHING",
            context.RequestAborted).ConfigureAwait(false);
        if (inserted != 1)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Request already used" }).ConfigureAwait(false);
            return;
        }
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"RequestNonces\" WHERE \"ExpiresAt\" < {DateTime.UtcNow}",
            context.RequestAborted).ConfigureAwait(false);

        _logger.LogInformation("Successfully authenticated application '{AppId}' for {Method} {Path}", 
            appId, context.Request.Method, path);

        context.Items["AppId"] = appId;
        context.Items["Application"] = app;

        await _next(context).ConfigureAwait(false);
    }
}
