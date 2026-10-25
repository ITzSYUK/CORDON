$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('StalkerNativeDiagnostics-' + [Guid]::NewGuid().ToString('N'))
$previousGitHubEnv = $env:GITHUB_ENV
$testRoot = $null
try {
    foreach ($folder in @('lib', 'bin', 'test\bin', 'test\fixtures')) {
        New-Item -ItemType Directory -Path (Join-Path $fixtureRoot $folder) -Force | Out-Null
    }
    $env:GITHUB_ENV = Join-Path $fixtureRoot 'github-env.txt'
    $state = @{ Count = 0; Output = [Collections.Generic.List[string]]::new() }
    & {
        # Exercise the actual runner's failure reporting without injecting a process.
        function Start-Process {
            param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle,
                [switch]$Wait, [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
            $state.Count++
            $failed = [IO.Path]::GetFileNameWithoutExtension($FilePath) -eq 'tvfs_test_x64'
            [IO.File]::WriteAllText($RedirectStandardOutput, $(if ($failed) { '[ FAILED ] Fixture.NativeAssertion: expected 0, got 1' } else { 'passed' }))
            [IO.File]::WriteAllText($RedirectStandardError, $(if ($failed) { 'fixture stderr detail' } else { '' }))
            [IO.File]::WriteAllText([IO.Path]::ChangeExtension($RedirectStandardOutput, '.xml'), '<testsuites/>')
            return [pscustomobject]@{ ExitCode = [int]$failed }
        }
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'Test-UsvfsNative.ps1') -RuntimeRoot $fixtureRoot 6>&1 |
                ForEach-Object { $state.Output.Add($_.ToString()) }
        }
        catch {
            if ($_.Exception.Message -notlike 'Native tests failed: tvfs_test_x64.*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'A failed native test did not fail the runner.' }
    }
    $record = [IO.File]::ReadAllText($env:GITHUB_ENV).Trim()
    if ($record -notmatch '^USVFS_NATIVE_TEST_RESULTS=(.+)$') { throw 'Native artifact path was not exported.' }
    $testRoot = $Matches[1]
    $output = $state.Output -join "`n"
    if ($state.Count -ne 12 -or $output -notlike '*Fixture.NativeAssertion: expected 0, got 1*' -or
        $output -notlike '*fixture stderr detail*' -or
        -not (Test-Path -LiteralPath (Join-Path $testRoot 'tvfs_test_x64.xml')) -or
        -not (Test-Path -LiteralPath (Join-Path $testRoot 'tvfs_test_x64.log'))) {
        throw 'Native failure details, remaining suites or diagnostic files were lost.'
    }
    Write-Host 'Native diagnostics passed: assertions/stderr printed, all suites run, failure preserved, artifact path exported.'
}
finally {
    $env:GITHUB_ENV = $previousGitHubEnv
    foreach ($path in @($fixtureRoot, $testRoot)) {
        if (-not $path) { continue }
        $resolved = [IO.Path]::GetFullPath($path)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notmatch '^(StalkerNativeDiagnostics-|cordon-usvfs-native-)[0-9a-f]{32}$') {
            throw "Unsafe diagnostics fixture cleanup: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
