using FileHoster.Services;
using Xunit;

namespace FileHoster.Tests;

public class StorageServiceTests : IDisposable
{
    private readonly string _testStoragePath;
    private readonly IStorageService _storageService;

    public StorageServiceTests()
    {
        _testStoragePath = Path.Combine(Path.GetTempPath(), $"filehoster-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testStoragePath);
        _storageService = new StorageService(_testStoragePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testStoragePath))
        {
            Directory.Delete(_testStoragePath, true);
        }
    }

    [Fact]
    public async Task SaveFileAsync_CreatesFileInCorrectLocation()
    {
        // Arrange
        var appId = "testapp";
        var fileContent = "test file content";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent));

        // Act
        var filePath = await _storageService.SaveFileAsync(stream, appId, "test.txt");

        // Assert
        Assert.NotNull(filePath);
        Assert.True(System.IO.File.Exists(filePath));
        var appStoragePath = _storageService.GetAppStoragePath(appId);
        Assert.StartsWith(appStoragePath, filePath);
    }

    [Fact]
    public async Task SaveFileAsync_PreservesFileExtension()
    {
        // Arrange
        var appId = "testapp";
        var fileContent = "test file content";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent));

        // Act
        var filePath = await _storageService.SaveFileAsync(stream, appId, "test.zip");

        // Assert
        Assert.EndsWith(".zip", filePath);
    }

    [Fact]
    public void FileExists_WithExistingFile_ReturnsTrue()
    {
        // Arrange
        var testFile = Path.Combine(_testStoragePath, "test.txt");
        System.IO.File.WriteAllText(testFile, "test content");

        // Act
        var exists = _storageService.FileExists(testFile);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void FileExists_WithNonexistentFile_ReturnsFalse()
    {
        // Arrange
        var nonexistentFile = Path.Combine(_testStoragePath, "nonexistent.txt");

        // Act
        var exists = _storageService.FileExists(nonexistentFile);

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void GetFileStream_WithExistingFile_ReturnsStream()
    {
        // Arrange
        var testFile = Path.Combine(_testStoragePath, "test.txt");
        var content = "test file content";
        System.IO.File.WriteAllText(testFile, content);

        // Act
        using var stream = _storageService.GetFileStream(testFile);

        // Assert
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var readContent = reader.ReadToEnd();
        Assert.Equal(content, readContent);
    }

    [Fact]
    public void GetFileStream_WithNonexistentFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonexistentFile = Path.Combine(_testStoragePath, "nonexistent.txt");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => _storageService.GetFileStream(nonexistentFile));
    }

    [Fact]
    public void DeleteFile_WithExistingFile_DeletesFile()
    {
        // Arrange
        var testFile = Path.Combine(_testStoragePath, "test.txt");
        System.IO.File.WriteAllText(testFile, "test content");

        // Act
        var deleted = _storageService.DeleteFile(testFile);

        // Assert
        Assert.True(deleted);
        Assert.False(System.IO.File.Exists(testFile));
    }

    [Fact]
    public void DeleteFile_WithNonexistentFile_ReturnsFalse()
    {
        // Arrange
        var nonexistentFile = Path.Combine(_testStoragePath, "nonexistent.txt");

        // Act
        var deleted = _storageService.DeleteFile(nonexistentFile);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public void GetAppStoragePath_ReturnsCorrectPath()
    {
        // Arrange
        var appId = "myapp";

        // Act
        var path = _storageService.GetAppStoragePath(appId);

        // Assert
        Assert.Equal(Path.Combine(_testStoragePath, appId), path);
    }

    [Fact]
    public void GetAppStoragePath_DifferentAppsHaveDifferentPaths()
    {
        // Arrange
        var appId1 = "app1";
        var appId2 = "app2";

        // Act
        var path1 = _storageService.GetAppStoragePath(appId1);
        var path2 = _storageService.GetAppStoragePath(appId2);

        // Assert
        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public async Task SaveFileAsync_GeneratesUniqueFilenames()
    {
        // Arrange
        var appId = "testapp";
        var fileContent = "test file content";

        // Act
        using var stream1 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent));
        var filePath1 = await _storageService.SaveFileAsync(stream1, appId, "test.txt");

        using var stream2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent));
        var filePath2 = await _storageService.SaveFileAsync(stream2, appId, "test.txt");

        // Assert
        Assert.NotEqual(filePath1, filePath2);
    }

    [Fact]
    public async Task SaveFileAsync_WhenInputFails_RemovesPendingFile()
    {
        using var stream = new ThrowingStream();

        await Assert.ThrowsAsync<IOException>(() => _storageService.SaveFileAsync(stream, "testapp", "test.zip"));
        Assert.Empty(Directory.GetFiles(_storageService.GetAppStoragePath("testapp")));
    }

    [Fact]
    public async Task QuarantineFile_CanBeRestoredWhenDeletionFails()
    {
        using var stream = new MemoryStream("package"u8.ToArray());
        var path = await _storageService.SaveFileAsync(stream, "testapp", "test.zip");

        var quarantine = _storageService.QuarantineFile(path);
        Assert.False(System.IO.File.Exists(path));
        Assert.True(System.IO.File.Exists(quarantine));

        _storageService.RestoreFile(quarantine, path);
        Assert.True(System.IO.File.Exists(path));
        Assert.False(System.IO.File.Exists(quarantine));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync("partial"u8.ToArray(), cancellationToken);
            throw new IOException("Simulated source failure");
        }
    }
}
