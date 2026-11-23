[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [string]$ApiUrl = "http://localhost:5011",

    [string]$AppId = "SampleApp",

    [string]$SecretKey = $env:FILEHOSTER_SECRET_KEY,

    [string[]]$FileNames = @()
)

$ErrorActionPreference = "Stop"

Write-Host "Checking package directory..."
if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Package directory does not exist: $SourcePath"
}
if ([string]::IsNullOrWhiteSpace($SecretKey)) {
    throw "Set FILEHOSTER_SECRET_KEY or pass -SecretKey for authenticated uploads."
}

$files = @(
    Get-ChildItem -LiteralPath $SourcePath -File |
        Where-Object { $_.Name -match '\.(zip|tar\.gz)$' } |
        Sort-Object Name
)

if ($files.Count -eq 0) {
    throw "No .zip or .tar.gz packages found in: $SourcePath"
}
if ($FileNames.Count -gt 0) {
    $missing = @($FileNames | Where-Object { $_ -notin $files.Name })
    if ($missing.Count -gt 0) {
        throw "Requested package(s) not found: $($missing -join ', ')"
    }
    $files = @($files | Where-Object { $_.Name -in $FileNames })
}

Write-Host "Found $($files.Count) archive(s)."
$uploadUri = "$($ApiUrl.TrimEnd('/'))/api/$([Uri]::EscapeDataString($AppId))/files/upload"
$successCount = 0

foreach ($file in $files) {
    $baseName = $file.Name -replace '\.(zip|tar\.gz)$', ''
    $metadataPath = Join-Path $file.DirectoryName "$baseName.json"

    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Missing metadata for $($file.Name): $metadataPath"
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw
    $null = $metadata | ConvertFrom-Json
    Write-Host "Hashing $($file.Name)..."
    $contentHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadataHash = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes("$metadata`n$($file.Name)")
        )).ToLowerInvariant()
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
    $nonce = [Guid]::NewGuid().ToString("N")
    $path = ([Uri]$uploadUri).PathAndQuery
    $signedData = "POST`n$path`n$AppId`n$timestamp`n$nonce`n$contentHash`n$metadataHash"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new(
        [System.Text.Encoding]::UTF8.GetBytes($SecretKey))
    try {
        $signature = [Convert]::ToBase64String(
            $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($signedData)))
    } finally {
        $hmac.Dispose()
    }

    Write-Host "Uploading $($file.Name) ($([math]::Round($file.Length / 1MB, 2)) MB)..."

    $response = Invoke-WebRequest `
        -Uri $uploadUri `
        -Method Post `
        -Headers @{
            "X-App-Id" = $AppId
            "X-Signature" = $signature
            "X-Timestamp" = $timestamp
            "X-Nonce" = $nonce
            "X-Content-SHA256" = $contentHash
            "X-Metadata-SHA256" = $metadataHash
        } `
        -Form @{
            file = $file
            metadata = $metadata
        }

    if ($response.StatusCode -notin 200, 201) {
        throw "Upload failed for $($file.Name): HTTP $($response.StatusCode)"
    }

    $result = $response.Content | ConvertFrom-Json
    Write-Host "Uploaded $($file.Name) as file ID $($result.fileId)"
    $successCount++
}

Write-Host "Uploaded $successCount package(s) successfully."
