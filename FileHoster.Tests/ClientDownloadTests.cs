using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AvaloniaApp.Services;

namespace FileHoster.Tests;

public sealed class ClientDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_OnlyReplacesExistingFileAfterVerifyingHash(bool correctHash)
    {
        var bytes = Encoding.UTF8.GetBytes("new package bytes");
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!correctHash)
            hash = "sha256:" + new string('0', 64);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "archive.zip");
        await System.IO.File.WriteAllTextAsync(target, "original");
        try
        {
            using var proxyClient = new HttpClient(new StubHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var json = path.Contains("/metadata/") ?
                    $$"""{"fileId":"abc","fileSize":{{bytes.Length}},"contentHash":"{{hash}}"}""" :
                    """{"downloadUrl":"https://download.example/download/abc","expiresAt":"2030-01-01T00:00:00Z"}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }));
            using var downloadClient = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            using var client = new SecureFileHosterClient(proxyClient, downloadClient, "https://proxy.example");
            if (correctHash)
            {
                await client.DownloadFileAsync("abc", target);
                Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(target));
            }

            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync("abc", target));
                Assert.Equal("original", await System.IO.File.ReadAllTextAsync(target));
            }
            Assert.Empty(Directory.GetFiles(root, "*.partial"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_WhenServerReturnsUnauthorized_PreservesExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "archive.zip");
        await System.IO.File.WriteAllTextAsync(target, "original");
        try
        {
            using var proxyClient = new HttpClient(new StubHandler(request =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/metadata/")
                        ? """{"fileId":"abc","fileSize":12}"""
                        : """{"downloadUrl":"https://download.example/download/abc"}""")
                }));
            using var downloadClient = new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            using var client = new SecureFileHosterClient(proxyClient, downloadClient, "https://proxy.example");

            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadFileAsync("abc", target));
            Assert.Equal("original", await System.IO.File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
