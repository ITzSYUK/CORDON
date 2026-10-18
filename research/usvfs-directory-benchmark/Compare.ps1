param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineRoot,
    [Parameter(Mandatory = $true)]
    [string]$CandidateRoot
)

$ErrorActionPreference = 'Stop'
$BaselineRoot = [IO.Path]::GetFullPath($BaselineRoot)
$CandidateRoot = [IO.Path]::GetFullPath($CandidateRoot)
$project = Join-Path $PSScriptRoot 'StalkerUsvfsDirectoryBenchmark.csproj'
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
$executable = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\StalkerUsvfsDirectoryBenchmark.exe'
$results = Join-Path $PSScriptRoot ('obj\comparison-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results | Out-Null
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cordon-usvfs-enumeration-' + [Guid]::NewGuid().ToString('N'))
& $executable --fixture $fixture
if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }

function Get-SourceFingerprint {
    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'game') -Recurse -File)
    foreach ($mod in 0..39) {
        $sourceFiles += Get-ChildItem -LiteralPath (Join-Path $fixture ('mod-{0:D2}' -f $mod)) -Recurse -File
    }
    $fingerprint = foreach ($file in $sourceFiles | Sort-Object FullName) {
        $file.FullName + ':' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $fingerprint -join "`n"
}

$before = Get-SourceFingerprint
$measurements = @()
# Alternate the order to reduce systematic filesystem-cache and background-load bias.
foreach ($round in 0..2) {
    $order = if ($round % 2 -eq 0) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
    foreach ($variant in $order) {
        $runtime = if ($variant -eq 'baseline') { $BaselineRoot } else { $CandidateRoot }
        $result = Join-Path $results "$variant-$round.json"
        & $executable --host $runtime $fixture $result
        if ($LASTEXITCODE -ne 0) { throw "$variant round $round failed; evidence: $results" }
        $measurements += [pscustomobject]@{
            Variant = $variant
            Round = $round
            Result = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        }
    }
}
if ($before -cne (Get-SourceFingerprint)) { throw 'Fixture source files changed.' }

$hashes = foreach ($runtime in @($BaselineRoot, $CandidateRoot)) {
    foreach ($file in @('lib\usvfs_x64.dll', 'lib\usvfs_x86.dll', 'bin\usvfs_proxy_x64.exe', 'bin\usvfs_proxy_x86.exe')) {
        Get-FileHash -LiteralPath (Join-Path $runtime $file) -Algorithm SHA256 | Select-Object Path, Hash
    }
}
$summary = foreach ($variant in @('baseline', 'candidate')) {
    $runs = @($measurements | Where-Object Variant -eq $variant)
    $times = @($runs.Result.EnumerationMs | Sort-Object)
    $preparation = @($runs.Result.PreparationMs | Sort-Object)
    [pscustomobject]@{
        Variant = $variant
        FileCount = $runs[0].Result.FileCount
        Scans = $times.Count
        MedianEnumerationMs = $times[[int][Math]::Floor($times.Count / 2)]
        MedianPreparationMs = $preparation[1]
    }
}
[pscustomobject]@{ Summary = $summary; Measurements = $measurements; RuntimeHashes = $hashes; Fixture = $fixture; SourcesUnchanged = $true } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding UTF8
$summary | Format-Table
Write-Host "Evidence: $results"
