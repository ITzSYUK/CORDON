param(
    [string]$SourceRoot,
    [switch]$LegacyUsvfs
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestName = if ($LegacyUsvfs) { 'UsvfsLegacyManifest.psd1' } else { 'UsvfsRuntimeManifest.psd1' }
. (Join-Path $PSScriptRoot 'ReleaseIntegrity.ps1')
$manifest = Import-UsvfsRuntimeManifest -Path (Join-Path $PSScriptRoot $manifestName)
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $repositoryRoot $manifest.SourceDirectory
}
if (-not $LegacyUsvfs) {
    $pending = @(Get-UsvfsPendingPatches -SourceRoot $SourceRoot -Manifest $manifest)
    foreach ($patch in $pending) {
        $result = Invoke-RepositoryGit -SourceRoot $SourceRoot -CommandArguments @('apply', '--unidiff-zero', '--index', $patch)
        if ($result.ExitCode -ne 0) { throw "Failed to apply tracked USVFS patch: $patch" }
    }
    Test-UsvfsSourceProvenance -SourceRoot $SourceRoot -Manifest $manifest
    Write-Host 'USVFS source is prepared reproducibly.'
    return
}
$patchPath = Join-Path $repositoryRoot $manifest.SourcePatch

$resolvedSourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
$revisionResult = Invoke-RepositoryGit -SourceRoot $resolvedSourceRoot -CommandArguments @("rev-parse", "HEAD")
$revision = ($revisionResult.Output -join "").Trim()
if ($revisionResult.ExitCode -ne 0 -or $revision -ne $manifest.SourceRevision) {
    throw "USVFS source revision must be $($manifest.SourceRevision), found '$revision'."
}

$reverseCheck = Invoke-RepositoryGit `
    -SourceRoot $resolvedSourceRoot `
    -CommandArguments @("apply", "--reverse", "--check", $patchPath)
if ($reverseCheck.ExitCode -ne 0) {
    $forwardCheck = Invoke-RepositoryGit `
        -SourceRoot $resolvedSourceRoot `
        -CommandArguments @("apply", "--check", $patchPath)
    if ($forwardCheck.ExitCode -ne 0) {
        throw "USVFS source has unrelated changes or the tracked patch cannot be applied."
    }

    $applyResult = Invoke-RepositoryGit `
        -SourceRoot $resolvedSourceRoot `
        -CommandArguments @("apply", $patchPath)
    if ($applyResult.ExitCode -ne 0) {
        throw "Failed to apply the tracked USVFS source patch."
    }
}

Test-UsvfsSourceProvenance `
    -SourceRoot $resolvedSourceRoot `
    -Manifest $manifest `
    -PatchPath $patchPath

Write-Host "USVFS source is prepared reproducibly."
