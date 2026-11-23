namespace FileHosterProxy.Models;

/// <summary>Represents a temporary public URL for downloading a hosted file.</summary>
public sealed record DownloadLinkResponse(
    string DownloadUrl,
    DateTimeOffset ExpiresAt,
    int ExpirationMinutes);

/// <summary>Represents the download-link response returned by FileHoster.</summary>
internal sealed record UpstreamDownloadLinkResponse(
    string DownloadUrl,
    DateTimeOffset ExpiresAt,
    int ExpirationMinutes);
