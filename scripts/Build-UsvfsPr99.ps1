# Experimental PR #99 runtime only; the normal manifest, source and publish are untouched.
# Requires dependencies and x86 smoke tools prepared by Build-UsvfsDependencies.ps1.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot '.external\usvfs-pr99'
$revision = 'c5f7ef2085bd29a3cca2cd57f3aba84dae030ff5'
$sourceRepository = 'https://github.com/ModOrganizer2/usvfs'
$patchPaths = @(
    (Join-Path $PSScriptRoot 'patches\usvfs-pr99-tests.patch'),
    (Join-Path $PSScriptRoot 'patches\usvfs-pr99-runtime.patch')
)
$outputRoot = Join-Path $sourceRoot 'runtime'

function Invoke-PrTool {
    param([string]$FileName, [string[]]$Arguments)
    Get-Command $FileName -ErrorAction Stop | Out-Null
    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $FileName @Arguments
    }
    finally { $ErrorActionPreference = $previousErrorPreference }
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName failed with exit code $LASTEXITCODE."
    }
}

# Reuse the existing dependency installation, never the stable runtime binaries.
foreach ($arch in @('x64', 'x86')) {
    $buildName = if ($arch -eq 'x64') { 'vsbuild64' } else { 'vsbuild32' }
    $prefix = Join-Path $repositoryRoot ".external\usvfs\$buildName\vcpkg_installed\$arch-windows-static-md"
    if (-not (Test-Path -LiteralPath (Join-Path $prefix 'share\GTest\GTestConfig.cmake'))) {
        throw "Missing $arch dependencies. Run Build-UsvfsDependencies.ps1 first."
    }
}
foreach ($file in @(
    'native\StalkerModLauncher.UsvfsX86Host\build32\StalkerModLauncher.UsvfsX86Host.exe',
    'research\usvfs-poc\build32\usvfs_overlay_child_x86.exe',
    'research\usvfs-poc\build32\usvfs_overlay_launcher_x86.exe'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $file) -PathType Leaf)) {
        throw "Missing $file. Run Build-UsvfsDependencies.ps1 first."
    }
}
$cmake = (Get-Command cmake -ErrorAction Stop).Source
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installations = & $vswhere -products * -version '[17.0,19.0)' -format json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Visual Studio discovery failed.' }
$visualStudio = $installations | Where-Object {
    Test-Path -LiteralPath (Join-Path $_.installationPath 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.v143.default.txt')
} | Sort-Object { [version]$_.installationVersion } -Descending | Select-Object -First 1
if (-not $visualStudio) { throw 'Visual Studio 2022/2026 with MSVC v143 for x64/x86 is required.' }
$generator = if (([version]$visualStudio.installationVersion).Major -eq 18) {
    'Visual Studio 18 2026'
} else { 'Visual Studio 17 2022' }

if (-not (Test-Path -LiteralPath $sourceRoot)) {
    Invoke-PrTool git @('clone', '--no-checkout', $sourceRepository, $sourceRoot)
    Invoke-PrTool git @('-C', $sourceRoot, 'fetch', '--depth', '1', 'origin', $revision)
    Invoke-PrTool git @('-C', $sourceRoot, 'checkout', '--detach', $revision)
}
$actualRevision = & git -C $sourceRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actualRevision -ne $revision) {
    throw "Expected PR #99 revision $revision in $sourceRoot. No existing source was reset."
}
$extraFiles = & git -C $sourceRoot ls-files --others --exclude-standard -- . ':!runtime/'
if ($LASTEXITCODE -ne 0 -or $extraFiles) {
    throw 'PR source contains unexpected untracked files. No files were removed.'
}
$diffArguments = @('-C', $sourceRoot, '-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false', 'diff', '--unified=0', '--binary', '--ignore-space-at-eol', '--no-ext-diff', 'HEAD')
$allPatchFiles = @()
$pendingPatches = @()
foreach ($patchPath in $patchPaths) {
    $statistics = & git -C $sourceRoot apply --numstat $patchPath
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect patch $patchPath" }
    $patchFiles = @($statistics | ForEach-Object { ($_ -split "`t", 3)[2] })
    if ($patchFiles.Count -eq 0) { throw "Empty patch: $patchPath" }
    $allPatchFiles += $patchFiles
    $sourceDiff = & git @diffArguments -- @patchFiles
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect PR source changes.' }
    $expectedDiff = [IO.File]::ReadAllText($patchPath).Replace("`r`n", "`n").TrimEnd()
    if (-not $sourceDiff) {
        Invoke-PrTool git @('-C', $sourceRoot, 'apply', '--unidiff-zero', '--check', $patchPath)
        $pendingPatches += $patchPath
    } elseif (($sourceDiff -join "`n").TrimEnd() -cne $expectedDiff) {
        throw 'PR source contains changes other than the tracked patches. No changes were overwritten.'
    }
}
$changedFiles = & git -C $sourceRoot -c core.safecrlf=false diff --name-only HEAD
if ($LASTEXITCODE -ne 0 -or @($changedFiles | Where-Object { $_ -notin $allPatchFiles }).Count -gt 0) {
    throw 'PR source contains changes outside the tracked patches. No changes were overwritten.'
}
foreach ($patchPath in $pendingPatches) {
    Invoke-PrTool git @('-C', $sourceRoot, 'apply', '--unidiff-zero', '--index', $patchPath)
}

$buildDrive = $null
try {
    foreach ($letter in [char[]](90..82)) {
        if (Test-Path -LiteralPath "${letter}:\") { continue }
        & subst.exe "${letter}:" $repositoryRoot
        if ($LASTEXITCODE -eq 0) { $buildDrive = "${letter}:"; break }
    }
    if (-not $buildDrive) { throw 'No free drive letter for an ASCII build path.' }
    $buildSource = "$buildDrive\.external\usvfs-pr99"
    foreach ($arch in @('x64', 'x86')) {
        $buildName = if ($arch -eq 'x64') { 'vsbuild64' } else { 'vsbuild32' }
        $platform = if ($arch -eq 'x64') { 'x64' } else { 'Win32' }
        $prefix = "$buildDrive\.external\usvfs\$buildName\vcpkg_installed\$arch-windows-static-md"
        $buildRoot = Join-Path $buildSource $buildName
        Invoke-PrTool $cmake @(
            '--fresh', '-S', $buildSource, '-B', $buildRoot, '-G', $generator, '-A', $platform, '-T', 'v143',
            "-DCMAKE_GENERATOR_INSTANCE=$($visualStudio.installationPath)",
            '-DBUILD_TESTING=ON', "-DCMAKE_PREFIX_PATH=$prefix"
        )
        Invoke-PrTool $cmake @('--build', $buildRoot, '--config', 'Release', '--parallel', '4')
    }
}
finally {
    if ($buildDrive) {
        & subst.exe $buildDrive /D
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove temporary drive $buildDrive" }
    }
}

# Native fixtures spawn children and compare final paths; use a real ASCII path, not subst.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cordon-usvfs-pr99-' + [Guid]::NewGuid().ToString('N'))
if ($testRoot -match '[^\x00-\x7f]') { throw 'Native tests require TEMP to be an ASCII path.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
foreach ($folder in @('lib', 'bin', 'test\bin')) {
    $destination = Join-Path $testRoot $folder
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $sourceRoot $folder) -File |
        Where-Object Extension -In @('.dll', '.exe') |
        Copy-Item -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'test\fixtures') -Destination (Join-Path $testRoot 'test\fixtures') -Recurse
$failed = @()
Push-Location $testRoot
try {
    foreach ($arch in @('x64', 'x86')) {
        foreach ($test in @('shared_test', 'thooklib_test', 'tinjectlib_test', 'tvfs_test', 'usvfs_global_test_runner', 'usvfs_test_runner')) {
            $name = "${test}_$arch"
            $process = Start-Process -FilePath (Join-Path $testRoot "test\bin\$name.exe") `
                -ArgumentList ('"--gtest_output=xml:{0}\{1}.xml"' -f $testRoot, $name) `
                -WorkingDirectory $testRoot -WindowStyle Hidden -Wait -PassThru `
                -RedirectStandardOutput (Join-Path $testRoot "$name.log") `
                -RedirectStandardError (Join-Path $testRoot "$name.stderr.log")
            if ($process.ExitCode -ne 0) { $failed += $name }
        }
    }
}
finally { Pop-Location }
Write-Host "Native test evidence: $testRoot"
if ($failed.Count -gt 0) { throw "Native tests failed: $($failed -join ', '). See $testRoot" }
& (Join-Path $PSScriptRoot 'Test-UsvfsRuntime.ps1') -RuntimeRoot $sourceRoot

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$hashes = foreach ($file in @('lib\usvfs_x64.dll', 'lib\usvfs_x86.dll', 'bin\usvfs_proxy_x64.exe', 'bin\usvfs_proxy_x86.exe')) {
    $source = Join-Path $sourceRoot $file
    Copy-Item -LiteralPath $source -Destination $outputRoot -Force
    Get-FileHash -LiteralPath (Join-Path $outputRoot (Split-Path -Leaf $file)) -Algorithm SHA256 | Select-Object Path, Hash
}
[ordered]@{
    SourceRepository = $sourceRepository
    SourceRevision = $revision
    Patches = @($patchPaths | ForEach-Object {
        [ordered]@{ Name = (Split-Path -Leaf $_); Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    NativeTestEvidence = $testRoot
    Files = @($hashes)
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'build-info.json') -Encoding UTF8
Write-Host "Experimental PR #99 files: $outputRoot"
Write-Host 'The standard USVFS runtime and publish folders were not changed.'
