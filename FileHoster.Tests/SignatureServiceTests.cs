using FileHoster.Services;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FileHoster.Tests;

public class SignatureServiceTests
{
    private readonly ISignatureService _signatureService = new SignatureService();
    private readonly string _testSecretKey = "test-secret-key-12345678901234567890";

    [Fact]
    public void GenerateSignature_ShouldProduceDeterministicOutput()
    {
        // Arrange
        var data = "test data";

        // Act
        var sig1 = _signatureService.GenerateSignature(data, _testSecretKey);
        var sig2 = _signatureService.GenerateSignature(data, _testSecretKey);

        // Assert - Same input should always produce same signature
        Assert.Equal(sig1, sig2);
    }

    [Fact]
    public void VerifySignature_WithValidSignature_ReturnsTrue()
    {
        // Arrange
        var data = "test data";
        var signature = _signatureService.GenerateSignature(data, _testSecretKey);

        // Act
        var isValid = _signatureService.VerifySignature(data, signature, _testSecretKey);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void VerifySignature_WithInvalidSignature_ReturnsFalse()
    {
        // Arrange
        var data = "test data";
        var invalidSignature = "invalid-signature-data";

        // Act
        var isValid = _signatureService.VerifySignature(data, invalidSignature, _testSecretKey);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void VerifySignature_WithWrongSecretKey_ReturnsFalse()
    {
        // Arrange
        var data = "test data";
        var signature = _signatureService.GenerateSignature(data, _testSecretKey);
        var wrongSecretKey = "wrong-secret-key-98765432109876543210";

        // Act
        var isValid = _signatureService.VerifySignature(data, signature, wrongSecretKey);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void GenerateDownloadToken_ContainsAllExpectedParts()
    {
        // Arrange
        var fileId = "file123";
        var appId = "app456";

        // Act
        var token = _signatureService.GenerateDownloadToken(fileId, appId, _testSecretKey);

        // Assert
        var parts = token.Split('|');
        Assert.Equal(4, parts.Length);
        Assert.Equal(fileId, parts[0]);
        Assert.Equal(appId, parts[1]);
        Assert.True(long.TryParse(parts[2], out _)); // Expiration time
        Assert.NotEmpty(parts[3]); // Signature
    }

    [Fact]
    public void VerifyDownloadToken_WithValidToken_ReturnsTrue()
    {
        // Arrange
        var fileId = "file123";
        var appId = "app456";
        var token = _signatureService.GenerateDownloadToken(fileId, appId, _testSecretKey);

        // Act
        var (isValid, returnedFileId, returnedAppId) = _signatureService.VerifyDownloadToken(token, _testSecretKey);

        // Assert
        Assert.True(isValid);
        Assert.Equal(fileId, returnedFileId);
        Assert.Equal(appId, returnedAppId);
    }

    [Fact]
    public void VerifyDownloadToken_WithExpiredToken_ReturnsFalse()
    {
        // Arrange - Create token and wait for expiration (or manipulate token)
        var fileId = "file123";
        var appId = "app456";
        var token = _signatureService.GenerateDownloadToken(fileId, appId, _testSecretKey);

        // Manually create an expired token
        var expiredTime = new DateTimeOffset(DateTime.UtcNow.AddHours(-2)).ToUnixTimeSeconds();
        var expiredTokenData = $"{fileId}|{appId}|{expiredTime}";
        var signature = _signatureService.GenerateSignature(expiredTokenData, _testSecretKey);
        var expiredToken = $"{expiredTokenData}|{signature}";

        // Act
        var (isValid, _, _) = _signatureService.VerifyDownloadToken(expiredToken, _testSecretKey);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void VerifyDownloadToken_WithInvalidFormat_ReturnsFalse()
    {
        // Arrange
        var invalidToken = "invalid-token-format";

        // Act
        var (isValid, fileId, appId) = _signatureService.VerifyDownloadToken(invalidToken, _testSecretKey);

        // Assert
        Assert.False(isValid);
        Assert.Null(fileId);
        Assert.Null(appId);
    }

    [Fact]
    public async Task VerifyRequestSignature_ShouldValidateRequestData()
    {
        var request = CreateSignedRequest("POST", "/api/app1/files", "?page=1", "request body", _testSecretKey);
        Assert.True(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));
    }

    [Fact]
    public async Task VerifyRequestSignature_WithWrongMethod_ReturnsFalse()
    {
        var request = CreateSignedRequest("POST", "/api/app1/files", "", "request body", _testSecretKey);
        request.Method = "GET";
        Assert.False(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));
    }

    [Fact]
    public async Task VerifyRequestSignature_ChangedQueryOrBodyOrTimestamp_Fails()
    {
        var request = CreateSignedRequest("GET", "/api/app1/files", "?page=1", "", _testSecretKey);
        request.QueryString = new QueryString("?page=2");
        Assert.False(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));

        request = CreateSignedRequest("POST", "/api/app1/files", "", "abc", _testSecretKey);
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes("abd"));
        Assert.False(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));

        request = CreateSignedRequest("GET", "/api/app1/files", "", "", _testSecretKey);
        request.Headers["X-Timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        Assert.False(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));
    }

    [Fact]
    public async Task VerifyRequestSignature_SignedUploadManifest_DoesNotReadMultipartBody()
    {
        var request = CreateSignedRequest("POST", "/api/app1/files/upload", "", "", _testSecretKey);
        request.ContentType = "multipart/form-data; boundary=test";
        var fileHash = new string('a', 64);
        var metadataHash = new string('b', 64);
        request.Headers["X-Content-SHA256"] = fileHash;
        request.Headers["X-Metadata-SHA256"] = metadataHash;
        request.Headers["X-Signature"] = _signatureService.SignRequest(
            request.Method, request.Path, "app1", request.Headers["X-Timestamp"]!,
            request.Headers["X-Nonce"]!, fileHash, metadataHash, _testSecretKey);
        Assert.True(await _signatureService.VerifyRequestSignature(request, request.Headers["X-Signature"]!, _testSecretKey));
    }

    private HttpRequest CreateSignedRequest(string method, string path, string query, string body, string secret)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        request.QueryString = new QueryString(query);
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        request.Headers["X-App-Id"] = "app1";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        request.Headers["X-Timestamp"] = timestamp;
        request.Headers["X-Nonce"] = nonce;
        request.Headers["X-Content-SHA256"] = hash;
        request.Headers["X-Signature"] = _signatureService.SignRequest(
            method, path + query, "app1", timestamp, nonce, hash, "", secret);
        return request;
    }
}
