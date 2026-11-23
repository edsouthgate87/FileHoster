using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FileHoster.Models;

public class HostedFile
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string AppId { get; set; } = string.Empty;

    [Required]
    public string Filename { get; set; } = string.Empty;

    [Required]
    public string FilePath { get; set; } = string.Empty;

    [Required]
    public long FileSize { get; set; }

    public string? ContentHash { get; set; }
    public string? MetadataJsonHash { get; set; }

    [Required]
    public string Metadata { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Foreign key
    [ForeignKey(nameof(AppId))]
    public Application? Application { get; set; }
}
