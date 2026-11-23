namespace FileHoster.Configuration;

public class SignatureOptions
{
    public int ExpirationMinutes { get; set; } = 60;
    public string SigningAlgorithm { get; set; } = "HMAC-SHA256";
}

public class StorageOptions
{
    public string BasePath { get; set; } = "/data/files";
    public long MaxFileSizeBytes { get; set; } = 500 * 1024 * 1024; // 500 MB
    public List<string> AllowedExtensions { get; set; } = new() { ".zip", ".tar", ".gz" };
}
