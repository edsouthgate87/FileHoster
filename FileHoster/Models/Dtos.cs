using System.ComponentModel.DataAnnotations;

namespace FileHoster.Models.Dtos;

public class UploadFileRequest
{
    [Required]
    public IFormFile File { get; set; } = null!;

    [Required]
    public string Metadata { get; set; } = string.Empty;
}

public class BulkUploadFileRequest
{
    [Required]
    public List<IFormFile> Files { get; set; } = new();

    [Required]
    public List<string> Metadata { get; set; } = new();
}

public class FileMetadataDto
{
    public string? Version { get; set; }
    public string? Filename { get; set; }
    public string? Hash { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Changelog { get; set; }
    
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, object>? AdditionalProperties { get; set; }
}

public class FileResponseDto
{
    public string FileId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? ContentHash { get; set; }
    public string? MetadataJsonHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public object? Metadata { get; set; }
}

public class BulkUploadResponseDto
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<FileResponseDto> UploadedFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class DownloadUrlResponseDto
{
    public string DownloadUrl { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public int ExpirationMinutes { get; set; }
}

public class QueryFilesRequest
{
    public string? Version { get; set; }
    public string? Filename { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 100;
}

public class PaginatedFilesResponse
{
    public int Total { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public IEnumerable<FileResponseDto> Items { get; set; } = new List<FileResponseDto>();
}
