using FileHoster.Models;
using Microsoft.EntityFrameworkCore;

namespace FileHoster.Data;

public class FileHosterContext : DbContext
{
    public FileHosterContext(DbContextOptions<FileHosterContext> options) : base(options)
    {
    }

    public DbSet<Application> Applications { get; set; }

    public DbSet<HostedFile> Files { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Applications
        modelBuilder.Entity<Application>()
            .HasMany(a => a.Files)
            .WithOne(f => f.Application)
            .HasForeignKey(f => f.AppId)
            .OnDelete(DeleteBehavior.Cascade);

        // Application indexes
        modelBuilder.Entity<Application>()
            .HasIndex(a => a.IsActive)
            .HasDatabaseName("idx_applications_isactive");
        
        // Files indexes
        modelBuilder.Entity<HostedFile>()
            .HasIndex(f => f.AppId)
            .HasDatabaseName("idx_files_appid");

        modelBuilder.Entity<HostedFile>()
            .HasIndex(f => f.CreatedAt)
            .HasDatabaseName("idx_files_createdat");
        
        // Composite index for common queries
        modelBuilder.Entity<HostedFile>()
            .HasIndex(f => new { f.AppId, f.Filename })
            .HasDatabaseName("idx_files_appid_filename");
        
        // Index for sorting by creation time within app
        modelBuilder.Entity<HostedFile>()
            .HasIndex(f => new { f.AppId, f.CreatedAt })
            .HasDatabaseName("idx_files_appid_createdat")
            .IsDescending(false, true);  // Descending on CreatedAt for latest-first sorting
    }
}
