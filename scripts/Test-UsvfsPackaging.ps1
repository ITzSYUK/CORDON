$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('StalkerUsvfsPackaging-' + [Guid]::NewGuid().ToString('N'))
$state = @{ Publishes = 0; Smokes = @() }

function Write-FixtureFile([string]$RelativePath, [string]$Content) {
    $path = Join-Path $fixtureRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content)
}

# Stub compilation and PE metadata; exercise copying, ZIPs, hashes and guards for real.
function dotnet {
    if ($args[0] -ne 'publish') { throw 'Unexpected dotnet command.' }
    $output = $args[[Array]::IndexOf($args, '-o') + 1]
    [IO.File]::WriteAllText((Join-Path $output 'CORDON.exe'), 'same launcher')
    $state.Publishes++
    $global:LASTEXITCODE = 0
}
function git {
    $global:LASTEXITCODE = 0
    if ($args -contains 'status' -or $args -contains 'apply') { return }
    if ($args -contains 'diff') { return 'src/shared/CMakeLists.txt' }
    if ($args -contains 'rev-parse') {
        if ($args -contains (Join-Path $fixtureRoot '.external\usvfs')) { return $stable.SourceRevision }
        return ('1' * 40)
    }
    throw 'Unexpected Git command.'
}
function Get-Item {
    param([string]$LiteralPath)
    if ($LiteralPath.StartsWith($fixtureRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetExtension($LiteralPath) -in @('.dll', '.exe')) {
        $launcher = [IO.Path]::GetFileName($LiteralPath) -in @('CORDON.exe', 'CORDON-Standalone.exe')
        return [pscustomobject]@{ VersionInfo = [pscustomobject]@{
            FileVersion = $(if ($launcher) { '1.0.0.0' } else { '0.5.7.2' })
            ProductVersion = '1.0.0+' + ('1' * 40)
        } }
    }
    Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath
}
function Get-Process {
    if ($state.Running) { return [pscustomobject]@{ Path = $state.Running; Id = 123 } }
    return @()
}

function Assert-ExperimentalStops([string]$Message) {
    $state.Publishes = 0
    foreach ($script in @('Build-Usvfs.ps1', 'Build-Release.ps1')) {
        $options = if ($script -eq 'Build-Usvfs.ps1') { @{ ExperimentalUsvfs = $true } } else { @{ IncludeExperimentalUsvfs = $true } }
        $failed = $false
        try { & (Join-Path $fixtureRoot "scripts\$script") @options | Out-Null }
        catch {
            if ($_.Exception.Message -notlike "*$Message*") { throw }
            $failed = $true
        }
        if (-not $failed -or $state.Publishes -ne 0) { throw 'Invalid runtime reached publishing.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\keep.txt')) -ne 'stable output' -or
        [IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\release\v1.0.0\keep.txt')) -ne 'release output') {
        throw 'Invalid runtime modified existing outputs.'
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts') -Force | Out-Null
    foreach ($name in @('Build-Usvfs.ps1', 'Build-Release.ps1', 'ReleaseIntegrity.ps1', 'UsvfsRuntimeManifest.psd1', 'UsvfsPr99Manifest.psd1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixtureRoot "scripts\$name")
    }
    Write-FixtureFile 'scripts\Verify-Release.ps1' 'param($ExpectedVersion) $global:LASTEXITCODE = 0'
    Write-FixtureFile 'scripts\Test-UsvfsRuntime.ps1' 'param($RuntimeRoot) $state.Smokes += $RuntimeRoot'
    Write-FixtureFile 'src\StalkerModLauncher\StalkerModLauncher.csproj' '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>'
    Write-FixtureFile 'src\StalkerModLauncher\packages.lock.json' '{}'
    Write-FixtureFile 'LICENSE.md' 'license'
    Write-FixtureFile 'THIRD_PARTY_NOTICES.md' 'notices'
    Write-FixtureFile 'scripts\patches\usvfs-msvc-pch.patch' 'stable fixture patch'
    Write-FixtureFile 'native\StalkerModLauncher.UsvfsX86Host\build32\StalkerModLauncher.UsvfsX86Host.exe' 'host'
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot '.external\usvfs\.git') -Force | Out-Null
    $stable = Import-PowerShellDataFile (Join-Path $fixtureRoot 'scripts\UsvfsRuntimeManifest.psd1')
    $experimental = Import-PowerShellDataFile (Join-Path $fixtureRoot 'scripts\UsvfsPr99Manifest.psd1')
    foreach ($file in $stable.Files) {
        Write-FixtureFile ('.external\usvfs\' + $file.RelativePath) ('stable-' + $file.Name)
        Write-FixtureFile ('.external\usvfs-pr99\' + $file.RelativePath) ('experimental-' + $file.Name)
        Write-FixtureFile ('.external\usvfs-pr99\runtime\' + $file.Name) ('experimental-' + $file.Name)
    }
    $patches = foreach ($path in $experimental.SourcePatches) {
        Write-FixtureFile $path 'fixture patch'
        @{ Name = Split-Path -Leaf $path; Sha256 = (Get-FileHash (Join-Path $fixtureRoot $path)).Hash }
    }
    $records = foreach ($file in $experimental.Files) {
        $path = Join-Path $fixtureRoot ('.external\usvfs-pr99\runtime\' + $file.Name)
        @{ Path = $path; Hash = (Get-FileHash $path).Hash }
    }
    $buildInfoPath = Join-Path $fixtureRoot '.external\usvfs-pr99\runtime\build-info.json'
    $buildInfo = @{ SourceRepository = $experimental.SourceRepository; SourceRevision = $experimental.SourceRevision; Patches = @($patches); Files = @($records) }
    Write-FixtureFile 'publish\usvfs\keep.txt' 'stable output'
    Write-FixtureFile 'publish\release\v1.0.0\keep.txt' 'release output'
    Assert-ExperimentalStops 'build record is missing'
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    $buildInfo.SourceRevision = '2' * 40
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    Assert-ExperimentalStops 'source revision does not match'
    $buildInfo.SourceRevision = $experimental.SourceRevision
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    $patch = Join-Path $fixtureRoot $experimental.SourcePatches[0]
    [IO.File]::AppendAllText($patch, 'changed')
    Assert-ExperimentalStops 'patch is outdated'
    [IO.File]::WriteAllText($patch, 'fixture patch')
    $binary = Join-Path $fixtureRoot ('.external\usvfs-pr99\runtime\' + $experimental.Files[0].Name)
    [IO.File]::AppendAllText($binary, 'changed')
    Assert-ExperimentalStops 'component is missing or changed'
    [IO.File]::WriteAllText($binary, 'experimental-' + $experimental.Files[0].Name)
    $library = Join-Path $fixtureRoot ('.external\usvfs-pr99\' + $experimental.Files[0].RelativePath)
    [IO.File]::AppendAllText($library, 'changed')
    Assert-ExperimentalStops 'component is missing or changed'
    [IO.File]::WriteAllText($library, 'experimental-' + $experimental.Files[0].Name)

    foreach ($include in @($false, $true)) {
        $state.Publishes = 0
        if (-not $include) { Remove-Item -LiteralPath $buildInfoPath }
        & (Join-Path $fixtureRoot 'scripts\Build-Release.ps1') -IncludeExperimentalUsvfs:$include | Out-Null
        if ($state.Publishes -ne 2) { throw 'Launcher should only be published twice.' }
        $release = Join-Path $fixtureRoot 'publish\release\v1.0.0'
        $archives = @(Get-ChildItem -LiteralPath $release -Filter '*.zip')
        if ($archives.Count -ne $(if ($include) { 4 } else { 2 })) { throw 'Wrong release archive count.' }
        foreach ($standalone in @('', '-standalone')) {
            $name = "CORDON-v1.0.0-win-x64$standalone"
            $stableBinary = Join-Path $release "$name\usvfs_x64.dll"
            if ([IO.File]::ReadAllText($stableBinary) -ne 'stable-usvfs_x64.dll') { throw 'Stable package was changed.' }
            if ($include) {
                $candidate = Join-Path $release "$name-usvfs-experimental\usvfs_x64.dll"
                if ([IO.File]::ReadAllText($candidate) -ne 'experimental-usvfs_x64.dll') { throw 'Wrong experimental runtime.' }
            }
        }
        if (-not $include) { $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8 }
    }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs\CORDON.exe'
    & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') -ExperimentalUsvfs | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\keep.txt')) -ne 'stable output' -or
        [IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs-experimental\usvfs_x64.dll')) -ne 'experimental-usvfs_x64.dll' -or
        $state.Smokes[-1] -ne (Join-Path $fixtureRoot '.external\usvfs-pr99')) {
        throw 'Experimental publish did not preserve/select the expected output and smoke runtime.'
    }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs-experimental\CORDON.exe'
    $state.Publishes = 0
    $failed = $false
    try { & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') -ExperimentalUsvfs | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Close the launcher*') { throw }
        $failed = $true
    }
    if (-not $failed -or $state.Publishes -ne 0) { throw 'Running target launcher did not block publishing.' }
    $state.Running = $null
    Remove-Item -LiteralPath $buildInfoPath
    & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\usvfs_x64.dll')) -ne 'stable-usvfs_x64.dll' -or
        $state.Smokes[-1] -ne (Join-Path $fixtureRoot '.external\usvfs')) { throw 'Default runtime changed.' }
    Write-Host 'USVFS packaging tests passed: 2/4 archives, shared launcher publishes, runtime isolation, missing/stale/tampered build guards.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^StalkerUsvfsPackaging-[0-9a-f]{32}$') { throw "Unsafe fixture cleanup: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
