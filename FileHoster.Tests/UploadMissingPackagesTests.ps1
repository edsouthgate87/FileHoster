$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$uploader = Join-Path (Join-Path $repositoryRoot 'scripts') 'upload-missing-packages.ps1'
$parameters = (Get-Command $uploader).Parameters
foreach ($name in 'SourcePath', 'ApiUrl', 'ProxyUrl') {
    $mandatory = @($parameters[$name].Attributes | Where-Object {
        $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory
    })
    Assert-True ($mandatory.Count -gt 0) "$name must be supplied explicitly."
}

$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "filehoster-upload-$([guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $testDirectory 'package.zip'
$metadataPath = Join-Path $testDirectory 'package.json'
New-Item -ItemType Directory -Path $testDirectory | Out-Null

try {
    [System.IO.File]::WriteAllText($archivePath, 'test package')
    $archive = Get-Item -LiteralPath $archivePath
    $contentHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadata = @{ SizeBytes = $archive.Length; Sha256 = $contentHash } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($metadataPath, $metadata)
    $metadataHash = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($metadata))).ToLowerInvariant()

    $uploadTestState = @{
        SshCalls = @()
        ProxyUrls = @()
        UploadUrls = @()
        Uploaded = $false
        Item = [pscustomobject]@{
            Filename = $archive.Name
            FileSize = $archive.Length
            ContentHash = "sha256:$contentHash"
            MetadataJsonHash = "sha256:$metadataHash"
            FileId = 'test-file'
        }
    }

    function Invoke-RestMethod {
        param([string]$Uri, [int]$TimeoutSec)
        $uploadTestState.ProxyUrls += $Uri
        $items = @()
        if ($uploadTestState.Uploaded) {
            $items = @($uploadTestState.Item)
        }
        [pscustomobject]@{ Items = $items; Total = $items.Count }
    }

    function Invoke-WebRequest {
        param([string]$Uri, [string]$Method, [hashtable]$Headers, [hashtable]$Form)
        $uploadTestState.UploadUrls += $Uri
        $uploadTestState.Uploaded = $true
        [pscustomobject]@{ StatusCode = 201; Content = '{"fileId":"test-file"}' }
    }

    function ssh {
        param([string]$Target, [string]$Command)
        $uploadTestState.SshCalls += [pscustomobject]@{ Target = $Target; Command = $Command }
        $global:LASTEXITCODE = 0
        'a' * 64
    }

    $arguments = @{
        SourcePath = $testDirectory
        ApiUrl = 'https://api.example.invalid'
        ProxyUrl = 'https://proxy.example.invalid'
    }

    & $uploader @arguments -Preview
    Assert-True ($uploadTestState.SshCalls.Count -eq 0) 'Preview must not use SSH.'
    Assert-True ($uploadTestState.UploadUrls.Count -eq 0) 'Preview must not upload.'

    $missingTargetError = $null
    try {
        & $uploader @arguments
    } catch {
        $missingTargetError = $_.Exception.Message
    }
    Assert-True ($missingTargetError -match 'SshTarget') 'An upload without an SSH target must fail.'
    Assert-True ($uploadTestState.SshCalls.Count -eq 0) 'A missing SSH target must not invoke SSH.'
    Assert-True ($uploadTestState.UploadUrls.Count -eq 0) 'A missing SSH target must not upload.'

    & $uploader @arguments -SshTarget 'uploader@example.invalid'
    Assert-True ($uploadTestState.SshCalls.Count -eq 1) 'The upload must retrieve the key once.'
    Assert-True ($uploadTestState.SshCalls[0].Target -eq 'uploader@example.invalid') 'SSH must use the supplied target.'
    Assert-True ($uploadTestState.SshCalls[0].Command -match ' ~/filehoster/\.env$') 'SSH must use the remote home directory.'
    Assert-True ($uploadTestState.UploadUrls.Count -eq 1) 'The missing package must be uploaded.'
    Assert-True ($uploadTestState.UploadUrls[0] -eq 'https://api.example.invalid/api/SampleApp/files/upload') 'Upload must use the supplied API.'
    Assert-True (@($uploadTestState.ProxyUrls | Where-Object { $_ -ne 'https://proxy.example.invalid/api/v1/filehoster/files' }).Count -eq 0) 'Listing must use the supplied proxy.'

    & $uploader @arguments
    Assert-True ($uploadTestState.SshCalls.Count -eq 1) 'An already-hosted package must not use SSH.'
    Assert-True ($uploadTestState.UploadUrls.Count -eq 1) 'An already-hosted package must not be uploaded again.'
    Write-Host 'Package uploader tests passed.'
} finally {
    foreach ($path in $archivePath, $metadataPath) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    if (Test-Path -LiteralPath $testDirectory) {
        Remove-Item -LiteralPath $testDirectory -Force
    }
}
