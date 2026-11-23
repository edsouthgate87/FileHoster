namespace FileHoster.Services;

public interface IStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string appId, string filename);
    Stream GetFileStream(string filePath);
    bool DeleteFile(string filePath);
    bool FileExists(string filePath);
    string GetAppStoragePath(string appId);
    string QuarantineFile(string filePath);
    void RestoreFile(string quarantinedPath, string originalPath);
}

public class StorageService : IStorageService
{
    private readonly string _basePath;

    public StorageService(string basePath)
    {
        _basePath = basePath;
        EnsureStoragePathExists();
    }

    public async Task<string> SaveFileAsync(Stream fileStream, string appId, string filename)
    {
        var appPath = GetAppStoragePath(appId);
        Directory.CreateDirectory(appPath);

        var fileId = Guid.NewGuid().ToString("N");
        var fileExtension = Path.GetExtension(filename).ToLower();
        var storagePath = Path.Combine(appPath, $"{fileId}{fileExtension}");

        var pendingPath = storagePath + ".pending";
        try
        {
            await using (var fileFs = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await fileStream.CopyToAsync(fileFs);
            System.IO.File.Move(pendingPath, storagePath);
        }
        catch
        {
            System.IO.File.Delete(pendingPath);
            throw;
        }

        return storagePath;
    }

    public Stream GetFileStream(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        return System.IO.File.OpenRead(filePath);
    }

    public bool DeleteFile(string filePath)
    {
        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
            return true;
        }
        return false;
    }

    public string QuarantineFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new FileNotFoundException("Stored file is missing", filePath);
        var quarantinedPath = filePath + "." + Guid.NewGuid().ToString("N") + ".deleting";
        System.IO.File.Move(filePath, quarantinedPath);
        return quarantinedPath;
    }

    public void RestoreFile(string quarantinedPath, string originalPath)
        => System.IO.File.Move(quarantinedPath, originalPath);

    public bool FileExists(string filePath)
    {
        return System.IO.File.Exists(filePath);
    }

    public string GetAppStoragePath(string appId)
    {
        return Path.Combine(_basePath, appId);
    }

    private void EnsureStoragePathExists()
    {
        Directory.CreateDirectory(_basePath);
    }
}
