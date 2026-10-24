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
        if ($args -contains (Join-Path $fixtureRoot '.external\usvfs')) { return $legacy.SourceRevision }
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

function Assert-DefaultStops([string]$Message) {
    $state.Publishes = 0
    foreach ($script in @('Build-Usvfs.ps1', 'Build-Release.ps1')) {
        $failed = $false
        try { & (Join-Path $fixtureRoot "scripts\$script") | Out-Null }
        catch {
            if ($_.Exception.Message -notlike "*$Message*") { throw }
            $failed = $true
        }
        if (-not $failed -or $state.Publishes -ne 0) { throw 'Invalid runtime reached publishing.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\keep.txt')) -ne 'main output' -or
        [IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\release\v1.0.0\keep.txt')) -ne 'release output') {
        throw 'Invalid runtime modified existing outputs.'
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts') -Force | Out-Null
    foreach ($name in @('Build-Usvfs.ps1', 'Build-Release.ps1', 'ReleaseIntegrity.ps1', 'UsvfsRuntimeManifest.psd1', 'UsvfsLegacyManifest.psd1')) {
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
    $legacy = Import-PowerShellDataFile (Join-Path $fixtureRoot 'scripts\UsvfsLegacyManifest.psd1')
    $primary = Import-PowerShellDataFile (Join-Path $fixtureRoot 'scripts\UsvfsRuntimeManifest.psd1')
    foreach ($file in $legacy.Files) {
        Write-FixtureFile ('.external\usvfs\' + $file.RelativePath) ('legacy-' + $file.Name)
        Write-FixtureFile ('.external\usvfs-pr99\' + $file.RelativePath) ('primary-' + $file.Name)
        Write-FixtureFile ('.external\usvfs-pr99\runtime\' + $file.Name) ('primary-' + $file.Name)
    }
    $patches = foreach ($path in $primary.SourcePatches) {
        Write-FixtureFile $path 'fixture patch'
        @{ Name = Split-Path -Leaf $path; Sha256 = (Get-FileHash (Join-Path $fixtureRoot $path)).Hash }
    }
    $records = foreach ($file in $primary.Files) {
        $path = Join-Path $fixtureRoot ('.external\usvfs-pr99\runtime\' + $file.Name)
        @{ Path = $path; Hash = (Get-FileHash $path).Hash }
    }
    $buildInfoPath = Join-Path $fixtureRoot '.external\usvfs-pr99\runtime\build-info.json'
    $buildInfo = @{ SourceRepository = $primary.SourceRepository; SourceRevision = $primary.SourceRevision; Patches = @($patches); Files = @($records) }
    Write-FixtureFile 'publish\usvfs\keep.txt' 'main output'
    Write-FixtureFile 'publish\release\v1.0.0\keep.txt' 'release output'
    Assert-DefaultStops 'build record is missing'
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    $buildInfo.SourceRevision = '2' * 40
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    Assert-DefaultStops 'source revision does not match'
    $buildInfo.SourceRevision = $primary.SourceRevision
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8
    $patch = Join-Path $fixtureRoot $primary.SourcePatches[0]
    [IO.File]::AppendAllText($patch, 'changed')
    Assert-DefaultStops 'patch is outdated'
    [IO.File]::WriteAllText($patch, 'fixture patch')
    $binary = Join-Path $fixtureRoot ('.external\usvfs-pr99\runtime\' + $primary.Files[0].Name)
    [IO.File]::AppendAllText($binary, 'changed')
    Assert-DefaultStops 'component is missing or changed'
    [IO.File]::WriteAllText($binary, 'primary-' + $primary.Files[0].Name)
    $library = Join-Path $fixtureRoot ('.external\usvfs-pr99\' + $primary.Files[0].RelativePath)
    [IO.File]::AppendAllText($library, 'changed')
    Assert-DefaultStops 'component is missing or changed'
    [IO.File]::WriteAllText($library, 'primary-' + $primary.Files[0].Name)

    foreach ($include in @($false, $true)) {
        $state.Publishes = 0
        $legacyLibrary = Join-Path $fixtureRoot '.external\usvfs\lib\usvfs_x64.dll'
        if (-not $include) { Remove-Item -LiteralPath $legacyLibrary }
        & (Join-Path $fixtureRoot 'scripts\Build-Release.ps1') -IncludeLegacyUsvfs:$include | Out-Null
        if ($state.Publishes -ne 2) { throw 'Launcher should only be published twice.' }
        $release = Join-Path $fixtureRoot 'publish\release\v1.0.0'
        $archives = @(Get-ChildItem -LiteralPath $release -Filter '*.zip')
        if ($archives.Count -ne $(if ($include) { 4 } else { 2 })) { throw 'Wrong release archive count.' }
        foreach ($standalone in @('', '-standalone')) {
            $name = "CORDON-v1.0.0-win-x64$standalone"
            $primaryBinary = Join-Path $release "$name\usvfs_x64.dll"
            if ([IO.File]::ReadAllText($primaryBinary) -ne 'primary-usvfs_x64.dll') { throw 'Default package must contain optimized USVFS.' }
            if ($include) {
                $candidate = Join-Path $release "$name-usvfs-legacy\usvfs_x64.dll"
                if ([IO.File]::ReadAllText($candidate) -ne 'legacy-usvfs_x64.dll') { throw 'Wrong legacy runtime.' }
            }
        }
        if (-not $include) { [IO.File]::WriteAllText($legacyLibrary, 'legacy-usvfs_x64.dll') }
    }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs\CORDON.exe'
    & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') -LegacyUsvfs | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\keep.txt')) -ne 'main output' -or
        [IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs-legacy\usvfs_x64.dll')) -ne 'legacy-usvfs_x64.dll' -or
        $state.Smokes[-1] -ne (Join-Path $fixtureRoot '.external\usvfs')) {
        throw 'Legacy publish did not preserve/select the expected output and smoke runtime.'
    }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs-legacy\CORDON.exe'
    $state.Publishes = 0
    $failed = $false
    try { & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') -LegacyUsvfs | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Close the launcher*') { throw }
        $failed = $true
    }
    if (-not $failed -or $state.Publishes -ne 0) { throw 'Running target launcher did not block publishing.' }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs-legacy\CORDON.exe'
    & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'publish\usvfs\usvfs_x64.dll')) -ne 'primary-usvfs_x64.dll' -or
        $state.Smokes[-1] -ne (Join-Path $fixtureRoot '.external\usvfs-pr99')) { throw 'Wrong default runtime.' }
    $state.Running = Join-Path $fixtureRoot 'publish\usvfs\CORDON.exe'
    $state.Publishes = 0
    $failed = $false
    try { & (Join-Path $fixtureRoot 'scripts\Build-Usvfs.ps1') | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Close the launcher*') { throw }
        $failed = $true
    }
    if (-not $failed -or $state.Publishes -ne 0) { throw 'Running main launcher did not block publishing.' }
    Write-Host 'USVFS packaging tests passed: 2/4 archives, shared launcher publishes, runtime isolation, missing/stale/tampered build guards.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^StalkerUsvfsPackaging-[0-9a-f]{32}$') { throw "Unsafe fixture cleanup: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
