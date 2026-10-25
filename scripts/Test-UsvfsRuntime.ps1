param(
    [string]$RuntimeRoot,
    [ValidateRange(1, 100)]
    [int]$Iterations = 5
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ReleaseIntegrity.ps1')
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $manifest = Import-UsvfsRuntimeManifest -Path (Join-Path $PSScriptRoot 'UsvfsRuntimeManifest.psd1')
    $RuntimeRoot = Join-Path $repositoryRoot $manifest.SourceDirectory
}

$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$managedPoc = Join-Path $repositoryRoot "research\usvfs-managed-poc\StalkerUsvfsManagedPoc.csproj"
$x86Child = Join-Path $repositoryRoot "research\usvfs-poc\build32\usvfs_overlay_child_x86.exe"
$x86Launcher = Join-Path $repositoryRoot "research\usvfs-poc\build32\usvfs_overlay_launcher_x86.exe"
$x86Host = Join-Path $repositoryRoot "native\StalkerModLauncher.UsvfsX86Host\build32\StalkerModLauncher.UsvfsX86Host.exe"

$requiredFiles = @(
    (Join-Path $RuntimeRoot "lib\usvfs_x64.dll"),
    (Join-Path $RuntimeRoot "bin\usvfs_proxy_x64.exe"),
    (Join-Path $RuntimeRoot "lib\usvfs_x86.dll"),
    (Join-Path $RuntimeRoot "bin\usvfs_proxy_x86.exe"),
    $x86Host,
    $x86Child,
    $x86Launcher
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing USVFS smoke-test dependency: $path"
    }
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

$testLock = $null
Push-Location $repositoryRoot
try {
    $testLock = Enter-UsvfsTestLock
    Write-Host "Building managed USVFS smoke test..."
    Invoke-DotNet `
        -Arguments @("build", $managedPoc, "-c", "Release") `
        -FailureMessage "Managed USVFS smoke-test build failed."

    Write-Host "Running x64 USVFS overlay smoke test..."
    Invoke-DotNet `
        -Arguments @(
            "run", "--project", $managedPoc,
            "-c", "Release",
            "--no-build", "--no-restore",
            "--", $RuntimeRoot, "--iterations", $Iterations
        ) `
        -FailureMessage "x64 USVFS overlay smoke test failed."

    Write-Host "Running x86 USVFS launcher-child smoke test..."
    Invoke-DotNet `
        -Arguments @(
            "run", "--project", $managedPoc,
            "-c", "Release",
            "--no-build", "--no-restore",
            "--", $RuntimeRoot, $x86Child, $x86Launcher, "--iterations", $Iterations
        ) `
        -FailureMessage "x86 USVFS launcher-child smoke test failed."
}
finally {
    Pop-Location
    if ($testLock) {
        $testLock.ReleaseMutex()
        $testLock.Dispose()
    }
}

Write-Host "USVFS native smoke tests passed ($Iterations iteration(s) per architecture)."
