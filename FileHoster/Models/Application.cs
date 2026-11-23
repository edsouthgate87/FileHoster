using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FileHoster.Models;

public class Application
{
    [Key]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    public string? Description { get; set; }

    // Permissions (deny by default, explicitly grant what's needed)
    public bool CanUpload { get; set; } = false;  // Must be explicitly granted
    public bool CanDownload { get; set; } = true; // Download enabled by default
    public bool CanQuery { get; set; } = true;    // Query enabled by default
    public bool CanDelete { get; set; } = false;  // Must be explicitly granted

    // Navigation
    public ICollection<HostedFile> Files { get; set; } = new List<HostedFile>();
}
