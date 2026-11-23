[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$ApiUrl,

    [Parameter(Mandatory)]
    [string]$ProxyUrl,
    [string]$SshTarget,
    [string]$AppId = 'SampleApp',
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 is required. Run this script with pwsh.'
}
if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Package directory does not exist: $SourcePath"
}

$files = @(
    Get-ChildItem -LiteralPath $SourcePath -File |
        Where-Object { $_.Name -match '\.(zip|tar\.gz)$' } |
        Sort-Object Name
)
if ($files.Count -eq 0) {
    throw "No .zip or .tar.gz packages found in: $SourcePath"
}

$packages = foreach ($file in $files) {
    $baseName = $file.Name -replace '\.(zip|tar\.gz)$', ''
    $metadataPath = Join-Path $file.DirectoryName "$baseName.json"
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Missing metadata for $($file.Name): $metadataPath"
    }

    $metadataJson = Get-Content -LiteralPath $metadataPath -Raw
    $metadata = $metadataJson | ConvertFrom-Json
    Write-Host "Checking $($file.Name)..."
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $declaredHash = ([string]$metadata.Sha256 -replace '^sha256:', '').ToLowerInvariant()
    if ($file.Length -ne $metadata.SizeBytes -or $declaredHash -notmatch '^[0-9a-f]{64}$' -or
        $hash -ne $declaredHash) {
        throw "Sidecar size or SHA-256 does not match $($file.Name)"
    }

    [pscustomobject]@{
        Name = $file.Name
        Size = $file.Length
        ContentHash = "sha256:$hash"
        MetadataJsonHash = "sha256:" + [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($metadataJson))).ToLowerInvariant()
    }
}

$listing = Invoke-RestMethod -Uri "$($ProxyUrl.TrimEnd('/'))/api/v1/filehoster/files" -TimeoutSec 30
if ($null -eq $listing.Items -or $listing.Total -ne @($listing.Items).Count) {
    throw 'Proxy file list is incomplete; cannot safely determine which packages already exist'
}

$toUpload = @(
    foreach ($package in $packages) {
        $existing = @($listing.Items | Where-Object {
            $_.Filename -eq $package.Name -and
            $_.FileSize -eq $package.Size -and
            $_.MetadataJsonHash -eq $package.MetadataJsonHash -and
            ($_.ContentHash -eq $package.ContentHash -or
                (-not $_.ContentHash -and $_.Metadata.Sha256 -eq $package.ContentHash.Substring(7)))
        })
        if ($existing.Count -gt 0) {
            Write-Host "Already hosted: $($package.Name) ($($existing[0].FileId))"
        } else {
            $package
        }
    }
)

if ($toUpload.Count -eq 0) {
    Write-Host 'All packages are already hosted; nothing to upload.'
    return
}
Write-Host "Packages to upload: $($toUpload.Name -join ', ')"
if ($Preview) {
    Write-Host 'Preview only; no key requested and no files uploaded.'
    return
}

if ([string]::IsNullOrWhiteSpace($SshTarget)) {
    throw 'Pass -SshTarget to retrieve the application key over SSH before uploading.'
}

$secret = & ssh $SshTarget "sed -n 's/^FILEHOSTER_SECRET_KEY=//p' ~/filehoster/.env"
try {
    if ($LASTEXITCODE -ne 0 -or $secret -isnot [string] -or $secret -notmatch '^[0-9a-f]{64}$') {
        throw 'Could not retrieve the application key over SSH; no upload started'
    }
    & (Join-Path $PSScriptRoot 'upload-packages.ps1') `
        -SourcePath $SourcePath -ApiUrl $ApiUrl -AppId $AppId `
        -SecretKey $secret -FileNames $toUpload.Name
} finally {
    Remove-Variable secret -ErrorAction SilentlyContinue
}

$updated = Invoke-RestMethod -Uri "$($ProxyUrl.TrimEnd('/'))/api/v1/filehoster/files" -TimeoutSec 30
if ($null -eq $updated.Items -or $updated.Total -ne @($updated.Items).Count) {
    throw 'Upload completed, but proxy file list is incomplete; verify results before retrying'
}
foreach ($package in $toUpload) {
    if (-not @($updated.Items | Where-Object {
        $_.Filename -eq $package.Name -and
        $_.FileSize -eq $package.Size -and
        $_.ContentHash -eq $package.ContentHash -and
        $_.MetadataJsonHash -eq $package.MetadataJsonHash
    }).Count) {
        throw "Upload completed, but $($package.Name) was not verified; check the proxy before retrying"
    }
}
Write-Host 'All uploaded packages verified through the proxy.'
