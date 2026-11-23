using System.Security.Cryptography;
using System.Text;

namespace FileHoster.Services;

public interface ISignatureService
{
    string GenerateSignature(string data, string secretKey);
    bool VerifySignature(string data, string signature, string secretKey);
    string GenerateDownloadToken(string fileId, string appId, string secretKey);
    (bool Valid, string? FileId, string? AppId) VerifyDownloadToken(string token, string secretKey);
    Task<bool> VerifyRequestSignature(HttpRequest request, string signature, string secretKey);
    string SignRequest(string method, string pathAndQuery, string appId, string timestamp, string nonce, string contentHash, string metadataHash, string secretKey);
    string ComputeMetadataJsonHash(string metadataJson);
}

public class SignatureService : ISignatureService
{
    public string SignRequest(string method, string pathAndQuery, string appId, string timestamp, string nonce, string contentHash, string metadataHash, string secretKey)
        => GenerateSignature($"{method}\n{pathAndQuery}\n{appId}\n{timestamp}\n{nonce}\n{contentHash}\n{metadataHash}", secretKey);

    public string GenerateSignature(string data, string secretKey)
    {
        var key = Encoding.UTF8.GetBytes(secretKey);
        using (var hmac = new HMACSHA256(key))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }
    }

    public bool VerifySignature(string data, string signature, string secretKey)
    {
        try
        {
            var expectedSignature = GenerateSignature(data, secretKey);
            // Use constant-time comparison to prevent timing attacks
            var expectedBytes = Convert.FromBase64String(expectedSignature);
            var actualBytes = Convert.FromBase64String(signature);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch
        {
            return false;
        }
    }

    public string GenerateDownloadToken(string fileId, string appId, string secretKey)
    {
        var expirationTime = new DateTimeOffset(DateTime.UtcNow.AddHours(1)).ToUnixTimeSeconds();
        var tokenData = $"{fileId}|{appId}|{expirationTime}";
        var signature = GenerateUrlSafeSignature(tokenData, secretKey);
        return $"{tokenData}|{signature}";
    }

    public (bool Valid, string? FileId, string? AppId) VerifyDownloadToken(string token, string secretKey)
    {
        try
        {
            var parts = token.Split('|');
            if (parts.Length != 4)
                return (false, null, null);

            var fileId = parts[0];
            var appId = parts[1];
            var expirationTime = long.Parse(parts[2]);
            var signature = parts[3];

            // Check expiration
            if (expirationTime < new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds())
                return (false, null, null);

            // Verify signature
            var tokenData = $"{fileId}|{appId}|{expirationTime}";
            if (!VerifyUrlSafeSignature(tokenData, signature, secretKey))
                return (false, null, null);

            return (true, fileId, appId);
        }
        catch
        {
            return (false, null, null);
        }
    }

    /// <summary>
    /// Generates an HMAC-SHA256 signature encoded as Base64URL (RFC 4648 §5).
    ///
    /// Standard Base64 can contain '+', '/', and '=' characters. When such a signature is
    /// embedded in a URL path segment (as with download tokens), ASP.NET Core/Kestrel does
    /// NOT decode a percent-encoded slash ("%2F") back into a literal '/' by design (this is
    /// a built-in protection against path-traversal/request-smuggling ambiguity). That leaves
    /// the literal "%2F" sequence inside the value bound to the route parameter, which corrupts
    /// Base64 parsing and causes signature verification to fail intermittently (roughly half of
    /// all randomly generated signatures contain at least one '/').
    ///
    /// Base64URL avoids the problem entirely by using a URL-safe alphabet ('-' and '_' instead
    /// of '+' and '/') and omitting padding, so the token never contains characters that require
    /// path-segment percent-encoding.
    /// </summary>
    private static string GenerateUrlSafeSignature(string data, string secretKey)
    {
        var key = Encoding.UTF8.GetBytes(secretKey);
        using (var hmac = new HMACSHA256(key))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Base64UrlEncode(hash);
        }
    }

    private static bool VerifyUrlSafeSignature(string data, string signature, string secretKey)
    {
        try
        {
            var key = Encoding.UTF8.GetBytes(secretKey);
            using (var hmac = new HMACSHA256(key))
            {
                var expectedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
                var actualBytes = Base64UrlDecode(signature);
                // Use constant-time comparison to prevent timing attacks
                return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
            }
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }

    public async Task<bool> VerifyRequestSignature(HttpRequest request, string signature, string secretKey)
    {
        var appId = request.Headers["X-App-Id"].ToString();
        var timestamp = request.Headers["X-Timestamp"].ToString();
        var nonce = request.Headers["X-Nonce"].ToString();
        var contentHash = request.Headers["X-Content-SHA256"].ToString();
        var metadataHash = request.Headers["X-Metadata-SHA256"].ToString();
        if (!long.TryParse(timestamp, out var sentAt) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt) > 300 ||
            nonce.Length != 32 || !Guid.TryParseExact(nonce, "N", out _) ||
            contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit) ||
            (metadataHash.Length != 0 && (metadataHash.Length != 64 ||
                !metadataHash.All(Uri.IsHexDigit))))
            return false;

        if (request.HasFormContentType)
        {
            if (metadataHash.Length != 64)
                return false;
        }
        else
        {
            if (request.ContentLength > 1024 * 1024)
                return false;
            request.EnableBuffering();
            var hash = await SHA256.HashDataAsync(request.Body, request.HttpContext.RequestAborted);
            request.Body.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(contentHash)))
                return false;
        }

        return VerifySignature(
            $"{request.Method.ToUpperInvariant()}\n{request.Path}{request.QueryString}\n{appId}\n{timestamp}\n{nonce}\n{contentHash.ToLowerInvariant()}\n{metadataHash.ToLowerInvariant()}",
            signature, secretKey);
    }

    /// <summary>
    /// Computes SHA256 hash of metadata JSON content.
    /// Used to verify metadata integrity on client side.
    /// </summary>
    /// <param name="metadataJson">Raw JSON string from upload request</param>
    /// <returns>SHA256 hash with "sha256:" prefix</returns>
    public string ComputeMetadataJsonHash(string metadataJson)
    {
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(metadataJson));
            return "sha256:" + Convert.ToHexString(hash).ToLower();
        }
    }
}
