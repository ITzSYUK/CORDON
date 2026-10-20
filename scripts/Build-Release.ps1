param(
    [string]$Version,
    [switch]$IncludeExperimentalUsvfs
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\StalkerModLauncher\StalkerModLauncher.csproj"
$verificationScript = Join-Path $PSScriptRoot "Verify-Release.ps1"
$integrityScript = Join-Path $PSScriptRoot "ReleaseIntegrity.ps1"
$usvfsManifestPath = Join-Path $PSScriptRoot "UsvfsRuntimeManifest.psd1"

. $integrityScript
$usvfsManifest = Import-UsvfsRuntimeManifest -Path $usvfsManifestPath

$gitStatus = Invoke-RepositoryGit `
    -SourceRoot $repositoryRoot `
    -CommandArguments @("status", "--porcelain", "--untracked-files=all")
if ($gitStatus.ExitCode -ne 0 -or @($gitStatus.Output).Count -ne 0) {
    throw "Release packaging requires a clean working tree, including untracked files. Commit or remove the current changes first."
}

$revisionResult = Invoke-RepositoryGit `
    -SourceRoot $repositoryRoot `
    -CommandArguments @("rev-parse", "HEAD")
$sourceRevision = ($revisionResult.Output -join "").Trim()
if ($revisionResult.ExitCode -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') {
    throw "Failed to determine the source Git revision for the release."
}

[xml]$projectFile = Get-Content -LiteralPath $project -Raw
$versionPropertyGroup = @($projectFile.Project.PropertyGroup) |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Version) } |
    Select-Object -First 1
$projectVersion = [string]$versionPropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "The application version is missing from $project."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
} elseif ($Version -ne $projectVersion) {
    throw "Requested release version '$Version' does not match project version '$projectVersion'."
}

$releaseRoot = Join-Path $repositoryRoot "publish\release\v$Version"
$stagingRoot = Join-Path $repositoryRoot "publish\release\.staging-v$Version"
$usvfsRoot = Join-Path $repositoryRoot ".external\usvfs"
$x86Host = Join-Path $repositoryRoot "native\StalkerModLauncher.UsvfsX86Host\build32\StalkerModLauncher.UsvfsX86Host.exe"

$runtimeFiles = @{
    "usvfs_x64.dll" = Join-Path $usvfsRoot "lib\usvfs_x64.dll"
    "usvfs_proxy_x64.exe" = Join-Path $usvfsRoot "bin\usvfs_proxy_x64.exe"
    "usvfs_x86.dll" = Join-Path $usvfsRoot "lib\usvfs_x86.dll"
    "usvfs_proxy_x86.exe" = Join-Path $usvfsRoot "bin\usvfs_proxy_x86.exe"
    "StalkerModLauncher.UsvfsX86Host.exe" = $x86Host
}

foreach ($entry in $runtimeFiles.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Missing release dependency '$($entry.Key)': $($entry.Value)"
    }
}

$experimentalRuntime = $null
if ($IncludeExperimentalUsvfs) {
    $experimentalRuntime = Get-ExperimentalUsvfsRuntime -RepositoryRoot $repositoryRoot
}

& $verificationScript -ExpectedVersion $Version
if ($LASTEXITCODE -ne 0) {
    throw "Release verification failed."
}
if ($IncludeExperimentalUsvfs) {
    & (Join-Path $PSScriptRoot 'Test-UsvfsRuntime.ps1') -RuntimeRoot $experimentalRuntime.SourceRoot
}

foreach ($path in @($releaseRoot, $stagingRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $releaseRoot, $stagingRoot | Out-Null

function Publish-Package {
    param(
        [string]$PackageName,
        [bool]$SelfContained,
        [string]$ExecutableName
    )

    $publishDirectory = Join-Path $stagingRoot "$PackageName-publish"
    New-Item -ItemType Directory -Path $publishDirectory | Out-Null

    $projectLockFile = Join-Path (Split-Path -Parent $project) "packages.lock.json"
    $publishLockFile = Join-Path $publishDirectory "packages.lock.json"
    Copy-Item -LiteralPath $projectLockFile -Destination $publishLockFile

    dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained $SelfContained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$SelfContained `
        -p:PublishTrimmed=false `
        "-p:NuGetLockFilePath=$publishLockFile" `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $PackageName."
    }

    $variants = @(@{ Name = $PackageName; Files = $runtimeFiles; Manifest = $usvfsManifest; Hashes = @{} })
    if ($IncludeExperimentalUsvfs) {
        $experimentalFiles = $experimentalRuntime.Files.Clone()
        $experimentalFiles['StalkerModLauncher.UsvfsX86Host.exe'] = $x86Host
        $variants += @{ Name = "$PackageName-usvfs-experimental"; Files = $experimentalFiles;
            Manifest = $experimentalRuntime.Manifest; Hashes = $experimentalRuntime.Hashes }
    }
    foreach ($variant in $variants) {
        $packageDirectory = Join-Path $releaseRoot $variant.Name
        New-Item -ItemType Directory -Path $packageDirectory | Out-Null
        Copy-Item -LiteralPath (Join-Path $publishDirectory "CORDON.exe") -Destination (Join-Path $packageDirectory $ExecutableName)
        foreach ($entry in $variant.Files.GetEnumerator()) {
            Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $packageDirectory $entry.Key)
        }

        Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE.md") -Destination (Join-Path $packageDirectory "LICENSE.txt")
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD_PARTY_NOTICES.md") -Destination (Join-Path $packageDirectory "THIRD-PARTY-NOTICES.txt")

        $packageFiles = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File |
            Select-Object -ExpandProperty FullName
        Write-Sha256ChecksumFile `
            -Paths $packageFiles `
            -RelativeTo $packageDirectory `
            -OutputPath (Join-Path $packageDirectory "checksums.txt")

        $unexpected = Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Extension -in @('.pdb', '.json', '.md') }
        if ($unexpected) {
            throw "Unexpected debug/runtime files in ${PackageName}: $($unexpected.Name -join ', ')"
        }

        $archive = Join-Path $releaseRoot "$($variant.Name).zip"
        Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
        Test-ReleasePackage `
            -PackageDirectory $packageDirectory `
            -ArchivePath $archive `
            -ExecutableName $ExecutableName `
            -ExpectedVersion $Version `
            -ExpectedCommit $sourceRevision `
            -UsvfsManifest $variant.Manifest `
            -RuntimeHashes $variant.Hashes
    }
}

Publish-Package `
    -PackageName "CORDON-v$Version-win-x64" `
    -SelfContained $false `
    -ExecutableName "CORDON.exe"

Publish-Package `
    -PackageName "CORDON-v$Version-win-x64-standalone" `
    -SelfContained $true `
    -ExecutableName "CORDON-Standalone.exe"

$archives = Get-ChildItem -LiteralPath $releaseRoot -Filter '*.zip' -File |
    Select-Object -ExpandProperty FullName
Write-Sha256ChecksumFile `
    -Paths $archives `
    -RelativeTo $releaseRoot `
    -OutputPath (Join-Path $releaseRoot "checksums.txt")
Test-Sha256ChecksumFile `
    -Root $releaseRoot `
    -ChecksumPath (Join-Path $releaseRoot "checksums.txt") `
    -ExpectedPaths $archives

Remove-Item -LiteralPath $stagingRoot -Recurse -Force
Write-Host "Release packages created in $releaseRoot"
