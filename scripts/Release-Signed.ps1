#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v?\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputDirectory,

    [switch]$SkipTests,

    [switch]$CreateTag,

    [switch]$PublishRelease,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:Repository = 'vulcan365/OpenCaddis'
$script:SigningEndpoint = 'https://eus.codesigning.azure.net/'
$script:SigningAccount = 'vulcan365'
$script:CertificateProfile = 'vulcan365'
$script:TimestampUrl = 'http://timestamp.acs.microsoft.com'
$script:AssetName = 'OpenCaddis-win-x64.zip'

function Invoke-External {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Get-GitHubHeaders {
    $credentialRequest = "protocol=https`nhost=github.com`n`n"
    $credentialLines = @($credentialRequest | git credential fill)
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub credential lookup failed.'
    }

    $credentialValues = @{}
    foreach ($line in $credentialLines) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            $credentialValues[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
        }
    }

    $token = $credentialValues['password']
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'No GitHub token was available from Git Credential Manager.'
    }

    return @{
        Authorization = "Bearer $token"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'OpenCaddis-local-release'
    }
}

function Publish-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$ArtifactDirectory
    )

    $headers = Get-GitHubHeaders
    $apiRoot = "https://api.github.com/repos/$($script:Repository)"
    $release = $null

    try {
        $release = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "$apiRoot/releases/tags/$Tag"
    }
    catch {
        if ($null -eq $_.Exception.Response -or $_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }
    }

    if ($null -eq $release) {
        $previousTag = git tag --list 'v*.*.*' --sort=-version:refname |
            Where-Object { $_ -ne $Tag } |
            Select-Object -First 1

        $notesRequest = @{
            tag_name = $Tag
            target_commitish = $Commit
        }
        if ($previousTag) {
            $notesRequest.previous_tag_name = $previousTag
        }

        $notes = Invoke-RestMethod `
            -UseBasicParsing `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($notesRequest | ConvertTo-Json) `
            -Uri "$apiRoot/releases/generate-notes"

        $createRequest = @{
            tag_name = $Tag
            target_commitish = $Commit
            name = $Tag
            body = $notes.body
            draft = $false
            prerelease = $false
            make_latest = 'true'
        } | ConvertTo-Json

        $release = Invoke-RestMethod `
            -UseBasicParsing `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body $createRequest `
            -Uri "$apiRoot/releases"
    }

    $uploadBase = $release.upload_url -replace '\{\?name,label\}$', ''
    $assetNames = @($script:AssetName, "$($script:AssetName).sha256")

    foreach ($assetName in $assetNames) {
        $release = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "$apiRoot/releases/tags/$Tag"
        foreach ($existingAsset in @($release.assets | Where-Object { $_.name -eq $assetName })) {
            Invoke-RestMethod `
                -UseBasicParsing `
                -Method Delete `
                -Headers $headers `
                -Uri "$apiRoot/releases/assets/$($existingAsset.id)" | Out-Null
        }

        $assetPath = Join-Path $ArtifactDirectory $assetName
        $contentType = if ($assetName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            'application/zip'
        }
        else {
            'text/plain'
        }

        $uploadUri = "${uploadBase}?name=$([Uri]::EscapeDataString($assetName))"
        Invoke-RestMethod `
            -UseBasicParsing `
            -Method Post `
            -Headers $headers `
            -ContentType $contentType `
            -InFile $assetPath `
            -Uri $uploadUri | Out-Null
    }

    Write-Host "Published https://github.com/$($script:Repository)/releases/tag/$Tag" -ForegroundColor Green
}

if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Signed OpenCaddis releases must be produced on 64-bit Windows.'
}

foreach ($command in 'az', 'dotnet', 'git') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found."
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$numericVersion = $tag.Substring(1)
$versionParts = $numericVersion.Split('.')
$applicationVersion = ([int]$versionParts[0] * 1000000) + ([int]$versionParts[1] * 1000) + [int]$versionParts[2]
$headCommit = (git -C $repoRoot rev-parse HEAD).Trim()
$shortCommit = (git -C $repoRoot rev-parse --short=12 HEAD).Trim()

if ($PublishRelease) {
    $CreateTag = $true
}

if ($CreateTag) {
    $currentBranch = (git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($currentBranch -ne 'main') {
        throw "Tag creation requires branch 'main'; current branch is '$currentBranch'."
    }

    if (git -C $repoRoot status --porcelain) {
        throw 'The repository must be clean before creating a signed release tag.'
    }

    Invoke-External -FilePath git -Arguments @('-C', $repoRoot, 'fetch', 'origin', 'main', '--tags')
    $originMain = (git -C $repoRoot rev-parse origin/main).Trim()
    if ($headCommit -ne $originMain) {
        throw 'HEAD must exactly match origin/main before creating a signed release.'
    }
}

$existingTagCommit = git -C $repoRoot rev-list -n 1 $tag 2>$null
if ($existingTagCommit -and $existingTagCommit.Trim() -ne $headCommit) {
    throw "Tag '$tag' already points to another commit. Existing tags are never rewritten."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\releases\$tag"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $OutputDirectory.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be beneath '$artifactRoot'."
}

if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not $Force) {
        throw "Output directory '$OutputDirectory' already exists. Use -Force to replace it."
    }
    [IO.Directory]::Delete($OutputDirectory, $true)
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
    -Filter signtool.exe `
    -File `
    -Recurse `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool) {
    throw 'A supported x64 Windows SDK SignTool installation was not found.'
}

$dlibPath = Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll'
if (-not (Test-Path -LiteralPath $dlibPath -PathType Leaf)) {
    throw "Artifact Signing Client Tools were not found at '$dlibPath'."
}

$azureAccount = az account show --query user.name -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($azureAccount)) {
    throw "Azure CLI is not authenticated. Run 'az login' before releasing."
}
Write-Host "Signing as $azureAccount using $($script:SigningAccount)/$($script:CertificateProfile)." -ForegroundColor Cyan

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "OpenCaddis-$tag-$([Guid]::NewGuid().ToString('N'))"
$publishPath = Join-Path $tempRoot 'publish'
$metadataPath = Join-Path $tempRoot 'metadata.json'

try {
    New-Item -ItemType Directory -Force -Path $publishPath | Out-Null

    if (-not $SkipTests) {
        Invoke-External -FilePath dotnet -Arguments @(
            'test'
            (Join-Path $repoRoot 'src\OpenCaddis.slnx')
            '-c'
            'Release'
        )
    }

    Invoke-External -FilePath dotnet -Arguments @(
        'publish'
        (Join-Path $repoRoot 'src\OpenCaddis.App\OpenCaddis.App.csproj')
        '-c', 'Release'
        '-f', 'net10.0-windows10.0.19041.0'
        '-r', 'win-x64'
        '--self-contained', 'true'
        '-p:WindowsPackageType=None'
        '-p:WindowsAppSDKSelfContained=true'
        "-p:ApplicationDisplayVersion=$numericVersion"
        "-p:ApplicationVersion=$applicationVersion"
        "-p:Version=$numericVersion"
        "-p:InformationalVersion=$tag+$headCommit"
        '-o', $publishPath
    )

    $executable = Join-Path $publishPath 'OpenCaddis.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Expected executable was not published at '$executable'."
    }

    @{
        Endpoint = $script:SigningEndpoint
        CodeSigningAccountName = $script:SigningAccount
        CertificateProfileName = $script:CertificateProfile
        CorrelationId = "OpenCaddis-$tag-$shortCommit"
        ExcludeCredentials = @(
            'EnvironmentCredential'
            'ManagedIdentityCredential'
            'WorkloadIdentityCredential'
            'SharedTokenCacheCredential'
            'VisualStudioCredential'
            'VisualStudioCodeCredential'
            'AzurePowerShellCredential'
            'AzureDeveloperCliCredential'
            'InteractiveBrowserCredential'
        )
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

    $ownedExecutableFiles = @(Get-ChildItem -LiteralPath $publishPath -File -Recurse |
        Where-Object {
            $_.Name -eq 'OpenCaddis.App.exe' -or
            $_.Name -like 'OpenCaddis*.dll' -or
            $_.Name -like 'FabrCore*.dll'
        })
    if ($ownedExecutableFiles.Count -eq 0) {
        throw 'No OpenCaddis or FabrCore executable files were found to sign.'
    }

    $filesToSign = @($ownedExecutableFiles | Where-Object {
        (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid'
    })

    Write-Host "Signing $($filesToSign.Count) OpenCaddis/FabrCore executable files..." -ForegroundColor Cyan
    $batchSize = 20
    for ($offset = 0; $offset -lt $filesToSign.Count; $offset += $batchSize) {
        $lastIndex = [Math]::Min($offset + $batchSize - 1, $filesToSign.Count - 1)
        $batch = @($filesToSign[$offset..$lastIndex])
        $signArguments = @(
            'sign'
            '/fd', 'SHA256'
            '/tr', $script:TimestampUrl
            '/td', 'SHA256'
            '/dlib', $dlibPath
            '/dmdf', $metadataPath
        ) + @($batch.FullName)
        Invoke-External -FilePath $signTool.FullName -Arguments $signArguments
    }

    $invalidSignatures = @($ownedExecutableFiles | ForEach-Object {
        $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
        if ($signature.Status -ne 'Valid') {
            [pscustomobject]@{ Path = $_.FullName; Status = $signature.Status }
        }
    })
    if ($invalidSignatures.Count -gt 0) {
        $details = $invalidSignatures | ForEach-Object { "$($_.Status): $($_.Path)" }
        throw "Signature verification failed:`n$($details -join "`n")"
    }

    $applicationSignature = Get-AuthenticodeSignature -LiteralPath $executable
    Write-Host "Application signer: $($applicationSignature.SignerCertificate.Subject)" -ForegroundColor Green

    Set-Content -LiteralPath (Join-Path $publishPath 'version.txt') -Value $numericVersion -Encoding utf8NoBOM
    $archivePath = Join-Path $OutputDirectory $script:AssetName
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $archivePath -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$archivePath.sha256"
    Set-Content -LiteralPath $checksumPath -Value "$hash  $($script:AssetName)" -Encoding ascii

    if ($CreateTag -and -not $existingTagCommit) {
        Invoke-External -FilePath git -Arguments @('-C', $repoRoot, 'tag', $tag)
        Invoke-External -FilePath git -Arguments @('-C', $repoRoot, 'push', 'origin', $tag)
    }

    if ($PublishRelease) {
        Publish-GitHubRelease -Tag $tag -Commit $headCommit -ArtifactDirectory $OutputDirectory
    }

    [pscustomobject]@{
        Tag = $tag
        Commit = $headCommit
        Signer = $applicationSignature.SignerCertificate.Subject
        SignedFiles = $filesToSign.Count
        Archive = $archivePath
        ArchiveBytes = (Get-Item -LiteralPath $archivePath).Length
        Sha256 = $hash
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        [IO.Directory]::Delete($tempRoot, $true)
    }
}
