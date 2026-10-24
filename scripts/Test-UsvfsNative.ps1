param([Parameter(Mandatory = $true)][string]$RuntimeRoot)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($RuntimeRoot)
# Native fixtures spawn children and compare final paths; use a real ASCII path, not subst.
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('cordon-usvfs-native-' + [Guid]::NewGuid().ToString('N'))
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
