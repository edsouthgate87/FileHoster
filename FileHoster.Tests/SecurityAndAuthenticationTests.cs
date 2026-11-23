using FileHoster.Services;
using Xunit;

namespace FileHoster.Tests;

public class SecurityAndAuthenticationTests
{
    private readonly ISignatureService _signatureService = new SignatureService();

    [Fact]
    public void Signature_DifferentSecrets_ProduceDifferentSignatures()
    {
        // Arrange
        var data = "test data";
        var secret1 = "secret1";
        var secret2 = "secret2";

        // Act
        var sig1 = _signatureService.GenerateSignature(data, secret1);
        var sig2 = _signatureService.GenerateSignature(data, secret2);

        // Assert
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void Signature_SmallDataChangeChangesSignature()
    {
        // Arrange
        var data1 = "test data";
        var data2 = "test datb";
        var secret = "secret";

        // Act
        var sig1 = _signatureService.GenerateSignature(data1, secret);
        var sig2 = _signatureService.GenerateSignature(data2, secret);

        // Assert
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void DownloadToken_ExtractionPrevent_AppIdInPlaintext()
    {
        // Arrange
        var fileId = "file123";
        var appId = "app456";
        var secret = "test-secret";

        // Act
        var token = _signatureService.GenerateDownloadToken(fileId, appId, secret);

        // Assert - Token contains appId in plaintext (allows efficient lookup)
        var parts = token.Split('|');
        Assert.Equal(appId, parts[1]);
    }

    [Fact]
    public void VerifySignature_CaseSensitive()
    {
        // Arrange
        var data = "TEST DATA";
        var secret = "secret";
        var signature = _signatureService.GenerateSignature(data, secret);

        // Act
        var validUpper = _signatureService.VerifySignature("TEST DATA", signature, secret);
        var validLower = _signatureService.VerifySignature("test data", signature, secret);

        // Assert
        Assert.True(validUpper);
        Assert.False(validLower);
    }

    [Fact]
    public void VerifySignature_WhitespaceMatters()
    {
        // Arrange
        var data1 = "test data";
        var data2 = "test  data"; // Extra space
        var secret = "secret";
        var signature = _signatureService.GenerateSignature(data1, secret);

        // Act
        var valid1 = _signatureService.VerifySignature(data1, signature, secret);
        var valid2 = _signatureService.VerifySignature(data2, signature, secret);

        // Assert
        Assert.True(valid1);
        Assert.False(valid2);
    }

    [Fact]
    public void TokenFormat_ContainsNoSecretKey()
    {
        // Arrange
        var fileId = "file123";
        var appId = "app456";
        var secretKey = "super-secret-key-12345678901234567890";

        // Act
        var token = _signatureService.GenerateDownloadToken(fileId, appId, secretKey);

        // Assert - Token should NOT contain the secret key
        Assert.DoesNotContain(secretKey, token);
        Assert.DoesNotContain("secret", token);
    }

    [Fact]
    public void Signature_LongSignature_HMAC256()
    {
        // Arrange
        var data = "test data";
        var secret = "secret";

        // Act
        var signature = _signatureService.GenerateSignature(data, secret);

        // Assert - HMAC-SHA256 produces 44-character Base64 string (256 bits / 6 bits per char)
        Assert.NotEmpty(signature);
        Assert.InRange(signature.Length, 40, 50); // Base64 encoding
    }

    [Fact]
    public void VerifySignature_EmptyData_AllowedButCanBeVerified()
    {
        // Arrange
        var emptyData = "";
        var secret = "secret";
        var signature = _signatureService.GenerateSignature(emptyData, secret);

        // Act
        var isValid = _signatureService.VerifySignature(emptyData, signature, secret);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void VerifySignature_MalformedSignature_ReturnsFalse()
    {
        // Arrange
        var data = "test data";
        var secret = "secret";
        var malformedSignature = "!!invalid!!!base64!!!";

        // Act
        var isValid = _signatureService.VerifySignature(data, malformedSignature, secret);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void VerifyDownloadToken_MissingSignaturePart_ReturnsFalse()
    {
        // Arrange
        var tokenWithoutSignature = "fileId|appId|12345";

        // Act
        var (isValid, _, _) = _signatureService.VerifyDownloadToken(tokenWithoutSignature, "secret");

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void VerifyDownloadToken_NonNumericTimestamp_ReturnsFalse()
    {
        // Arrange
        var invalidToken = "fileId|appId|notanumber|signature";

        // Act
        var (isValid, _, _) = _signatureService.VerifyDownloadToken(invalidToken, "secret");

        // Assert
        Assert.False(isValid);
    }
}
