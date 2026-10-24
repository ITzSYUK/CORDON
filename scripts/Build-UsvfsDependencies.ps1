param(
    [switch]$BootstrapUsvfs,
    [switch]$SkipSmokeTest,
    [string]$VcpkgRoot,
    [switch]$CleanUsvfsBuild,
    [switch]$LegacyUsvfs
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hostSource = Join-Path $repositoryRoot "native\StalkerModLauncher.UsvfsX86Host"
$hostBuild = Join-Path $hostSource "build32"
$pocSource = Join-Path $repositoryRoot "research\usvfs-poc"
$pocBuild = Join-Path $pocSource "build32"
$manifestName = if ($LegacyUsvfs) { 'UsvfsLegacyManifest.psd1' } else { 'UsvfsRuntimeManifest.psd1' }
$manifestPath = Join-Path $PSScriptRoot $manifestName
$integrityScript = Join-Path $PSScriptRoot "ReleaseIntegrity.ps1"
$prepareScript = Join-Path $PSScriptRoot "Prepare-UsvfsSource.ps1"
$smokeScript = Join-Path $PSScriptRoot "Test-UsvfsRuntime.ps1"
$patchPath = Join-Path $repositoryRoot "scripts\patches\usvfs-msvc-pch.patch"

. $integrityScript
$manifest = Import-UsvfsRuntimeManifest -Path $manifestPath
$usvfsRoot = Join-Path $repositoryRoot $manifest.SourceDirectory
$usvfsParent = Split-Path -Parent $usvfsRoot

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Resolve-CmakeExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVcpkgRoot
    )

    $command = Get-Command "cmake" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Path
    }

    $installationPath = Split-Path -Parent (Split-Path -Parent $ResolvedVcpkgRoot)
    if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
        $candidate = Join-Path $installationPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw "CMake was not found. Install CMake or the Visual Studio C++ CMake tools."
}

function Resolve-VisualStudio2022Path {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe was not found. Install Visual Studio 2022 Build Tools with MSVC v143 for x64/x86."
    }

    $installationPath = & $vswhere `
        -latest `
        -products * `
        -version "[17.0,18.0)" `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio 2022 with MSVC v143 for x64/x86 was not found."
    }

    return $installationPath.Trim()
}

function New-AsciiRepositoryDrive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    foreach ($letter in [char[]](90..82)) {
        $drive = "${letter}:"
        if (Test-Path -LiteralPath "$drive\") {
            continue
        }

        & subst.exe $drive $TargetPath
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath "$drive\")) {
            return $drive
        }
    }

    throw "No free drive letter is available for an ASCII-only USVFS build path."
}

function Remove-AsciiRepositoryDrive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Drive
    )

    & subst.exe $Drive /D
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to remove temporary build drive $Drive. Remove it manually with: subst $Drive /D"
    }
}

function Clear-UsvfsBuildDirectories {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot
    )

    $resolvedSourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    foreach ($directoryName in @("vsbuild64", "vsbuild32")) {
        $buildDirectory = [System.IO.Path]::GetFullPath((Join-Path $resolvedSourceRoot $directoryName))
        if (-not $buildDirectory.StartsWith(
            $resolvedSourceRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean USVFS build directory outside the source root: $buildDirectory"
        }

        if (Test-Path -LiteralPath $buildDirectory -PathType Container) {
            Remove-Item -LiteralPath $buildDirectory -Recurse -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $usvfsRoot -PathType Container)) {
    if (-not $BootstrapUsvfs) {
        throw "USVFS source is missing: $usvfsRoot. Re-run with -BootstrapUsvfs to clone the pinned source."
    }

    New-Item -ItemType Directory -Path $usvfsParent -Force | Out-Null
    Invoke-Tool `
        -FileName "git" `
        -Arguments @("clone", "--no-checkout", $manifest.SourceRepository, $usvfsRoot) `
        -FailureMessage "Failed to clone the USVFS source repository."
    Invoke-Tool `
        -FileName "git" `
        -Arguments @("-C", $usvfsRoot, "fetch", "--depth", "1", "origin", $manifest.SourceRevision) `
        -FailureMessage "Failed to fetch the pinned USVFS revision."
    Invoke-Tool `
        -FileName "git" `
        -Arguments @("-C", $usvfsRoot, "checkout", "--detach", $manifest.SourceRevision) `
        -FailureMessage "Failed to checkout the pinned USVFS revision."
}

if (-not (Test-Path -LiteralPath (Join-Path $usvfsRoot ".git"))) {
    throw "USVFS source is not a Git repository: $usvfsRoot"
}

if ([string]::IsNullOrWhiteSpace($VcpkgRoot)) {
    $VcpkgRoot = $env:VCPKG_ROOT
}

if ([string]::IsNullOrWhiteSpace($VcpkgRoot) -or
    -not (Test-Path -LiteralPath (Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake") -PathType Leaf)) {
    throw "VCPKG_ROOT must point to a vcpkg checkout before building USVFS."
}

$VcpkgRoot = [System.IO.Path]::GetFullPath($VcpkgRoot)
$cmake = Resolve-CmakeExecutable -ResolvedVcpkgRoot $VcpkgRoot
$visualStudioPath = Resolve-VisualStudio2022Path

& $prepareScript -SourceRoot $usvfsRoot -LegacyUsvfs:$LegacyUsvfs

if ($CleanUsvfsBuild) {
    Clear-UsvfsBuildDirectories -SourceRoot $usvfsRoot
}

$previousVcpkgRoot = $env:VCPKG_ROOT
$previousVisualStudioPath = $env:VCPKG_VISUAL_STUDIO_PATH
$buildDrive = $null
try {
    $env:VCPKG_ROOT = $VcpkgRoot
    $env:VCPKG_VISUAL_STUDIO_PATH = $visualStudioPath
    $buildDrive = New-AsciiRepositoryDrive -TargetPath $repositoryRoot

    $buildRepositoryRoot = "$buildDrive\"
    $buildUsvfsRoot = Join-Path $buildRepositoryRoot $manifest.SourceDirectory
    $buildHostSource = Join-Path $buildRepositoryRoot "native\StalkerModLauncher.UsvfsX86Host"
    $buildHostOutput = Join-Path $buildHostSource "build32"
    $buildPocSource = Join-Path $buildRepositoryRoot "research\usvfs-poc"
    $buildPocOutput = Join-Path $buildPocSource "build32"

    Push-Location $buildUsvfsRoot
    try {
        Invoke-Tool `
            -FileName $cmake `
            -Arguments @("--fresh", "--preset", "vs2022-windows-x64") `
            -FailureMessage "USVFS x64 CMake configuration failed."
        Invoke-Tool `
            -FileName $cmake `
            -Arguments @("--build", "--preset", "vs2022-windows-x64", "--config", "Release") `
            -FailureMessage "USVFS x64 build failed."
        Invoke-Tool `
            -FileName $cmake `
            -Arguments @("--fresh", "--preset", "vs2022-windows-x86") `
            -FailureMessage "USVFS x86 CMake configuration failed."
        Invoke-Tool `
            -FileName $cmake `
            -Arguments @("--build", "--preset", "vs2022-windows-x86", "--config", "Release") `
            -FailureMessage "USVFS x86 build failed."
    }
    finally {
        Pop-Location
    }

    Test-UsvfsSourceProvenance -SourceRoot $usvfsRoot -Manifest $manifest -PatchPath $patchPath
    # Binaries compiled from the pinned source may differ between Windows SDK builds.
    # Their canonical SHA-256 values are checked only when assembling a release package.

    Invoke-Tool `
        -FileName $cmake `
        -Arguments @(
            "--fresh",
            "-S", $buildHostSource,
            "-B", $buildHostOutput,
            "-G", "Visual Studio 17 2022",
            "-A", "Win32",
            "-T", "v143",
            "-DUSVFS_SOURCE_DIR=$buildUsvfsRoot"
        ) `
        -FailureMessage "USVFS x86 host CMake configuration failed."
    Invoke-Tool `
        -FileName $cmake `
        -Arguments @("--build", $buildHostOutput, "--config", "Release") `
        -FailureMessage "USVFS x86 host build failed."

    Invoke-Tool `
        -FileName $cmake `
        -Arguments @(
            "--fresh",
            "-S", $buildPocSource,
            "-B", $buildPocOutput,
            "-G", "Visual Studio 17 2022",
            "-A", "Win32",
            "-T", "v143",
            "-DUSVFS_SOURCE_DIR=$buildUsvfsRoot",
            "-DUSVFS_BINARY_DIR=$buildUsvfsRoot"
        ) `
        -FailureMessage "USVFS x86 smoke-process CMake configuration failed."
    Invoke-Tool `
        -FileName $cmake `
        -Arguments @(
            "--build", $buildPocOutput,
            "--config", "Release",
            "--target", "usvfs_overlay_child_x86", "usvfs_overlay_launcher_x86"
        ) `
        -FailureMessage "USVFS x86 smoke-process build failed."
}
finally {
    if ($buildDrive) {
        Remove-AsciiRepositoryDrive -Drive $buildDrive
    }

    $env:VCPKG_ROOT = $previousVcpkgRoot
    $env:VCPKG_VISUAL_STUDIO_PATH = $previousVisualStudioPath
}

$requiredFiles = @(
    (Join-Path $hostBuild "StalkerModLauncher.UsvfsX86Host.exe"),
    (Join-Path $pocBuild "usvfs_overlay_child_x86.exe"),
    (Join-Path $pocBuild "usvfs_overlay_launcher_x86.exe")
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected USVFS build output was not created: $path"
    }
}

if (-not $LegacyUsvfs) {
    & (Join-Path $PSScriptRoot 'Test-UsvfsNative.ps1') -RuntimeRoot $usvfsRoot
}

if (-not $SkipSmokeTest) {
    & $smokeScript -RuntimeRoot $usvfsRoot
}

if (-not $LegacyUsvfs) {
    $patchPaths = @($manifest.SourcePatches | ForEach-Object { Join-Path $repositoryRoot $_ })
    $outputRoot = Join-Path $usvfsRoot 'runtime'
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $hashes = foreach ($file in @('lib\usvfs_x64.dll', 'lib\usvfs_x86.dll', 'bin\usvfs_proxy_x64.exe', 'bin\usvfs_proxy_x86.exe')) {
        $source = Join-Path $usvfsRoot $file
        Copy-Item -LiteralPath $source -Destination $outputRoot -Force
        Get-FileHash -LiteralPath (Join-Path $outputRoot (Split-Path -Leaf $file)) -Algorithm SHA256 | Select-Object Path, Hash
    }
    [ordered]@{
        SourceRepository = $manifest.SourceRepository
        SourceRevision = $manifest.SourceRevision
        Patches = @($patchPaths | ForEach-Object {
            [ordered]@{ Name = (Split-Path -Leaf $_); Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
        })
        Files = @($hashes)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'build-info.json') -Encoding UTF8
}

Write-Host "USVFS dependencies are ready for Build-Usvfs.ps1 (legacy: $LegacyUsvfs)."
