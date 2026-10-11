$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "ReleaseIntegrity.ps1")

function Invoke-FixtureGit {
    param([string[]]$Arguments)

    $result = Invoke-RepositoryGit -SourceRoot $fixtureRoot -CommandArguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "Fixture Git command failed: $($Arguments -join ' ')"
    }
}

function Assert-ReleaseStops {
    param([string]$ExpectedMessage)

    try {
        & (Join-Path $fixtureRoot "scripts\Build-Release.ps1")
    } catch {
        if ($_.Exception.Message.StartsWith($ExpectedMessage, [StringComparison]::Ordinal)) {
            return
        }
        throw "Unexpected release failure: $($_.Exception.Message)"
    }
    throw "Release packaging did not stop."
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("StalkerRepositoryGuards-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path (Join-Path $fixtureRoot "scripts"), (Join-Path $fixtureRoot "src\StalkerModLauncher") | Out-Null
try {
    foreach ($name in @("Build-Release.ps1", "ReleaseIntegrity.ps1", "UsvfsRuntimeManifest.psd1")) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixtureRoot "scripts\$name")
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot ".gitignore") -Destination $fixtureRoot
    $project = Join-Path $fixtureRoot "src\StalkerModLauncher\StalkerModLauncher.csproj"
    $projectContent = '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>'
    [IO.File]::WriteAllText($project, $projectContent)
    Invoke-FixtureGit -Arguments @("init", "--quiet")
    Invoke-FixtureGit -Arguments @("add", ".")
    Invoke-FixtureGit -Arguments @("-c", "user.name=Guard Test", "-c", "user.email=guard@example.invalid", "commit", "--quiet", "-m", "fixture")

    # Missing native dependencies stop the clean fixture before any packaging can begin.
    $dependencyFailure = "Missing release dependency"
    Assert-ReleaseStops -ExpectedMessage $dependencyFailure
    foreach ($directory in @("bin", "obj", "publish")) {
        $path = Join-Path $fixtureRoot $directory
        New-Item -ItemType Directory -Path $path | Out-Null
        [IO.File]::WriteAllText((Join-Path $path "ignored.txt"), "keep")
    }
    Assert-ReleaseStops -ExpectedMessage $dependencyFailure

    $dirtyFailure = "Release packaging requires a clean working tree"
    foreach ($extension in @("cs", "xaml")) {
        $untracked = Join-Path $fixtureRoot "src\StalkerModLauncher\Untracked.$extension"
        [IO.File]::WriteAllText($untracked, "untracked source")
        Assert-ReleaseStops -ExpectedMessage $dirtyFailure
        Remove-Item -LiteralPath $untracked
    }
    [IO.File]::AppendAllText($project, " ")
    Assert-ReleaseStops -ExpectedMessage $dirtyFailure
    Invoke-FixtureGit -Arguments @("add", "src/StalkerModLauncher/StalkerModLauncher.csproj")
    Assert-ReleaseStops -ExpectedMessage $dirtyFailure
    foreach ($directory in @("bin", "obj", "publish")) {
        if ([IO.File]::ReadAllText((Join-Path $fixtureRoot "$directory\ignored.txt")) -ne "keep") {
            throw "Release guard changed an existing output file."
        }
    }
    Write-Host "Release guards passed: clean/ignored files accepted; untracked C#/XAML and tracked/staged changes rejected."
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notmatch '^StalkerRepositoryGuards-[0-9a-f]{32}$') {
        throw "Unsafe fixture cleanup path: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}

$workflow = [IO.File]::ReadAllText((Join-Path $repositoryRoot ".github\workflows\usvfs.yml"))
$runtimePaths = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\StalkerModLauncher\Services") -Filter '*Usvfs*.cs' -File |
        ForEach-Object { "src/StalkerModLauncher/Services/$($_.Name)" }
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src\StalkerModLauncher\Models") -Filter 'Usvfs*.cs' -File |
        ForEach-Object { "src/StalkerModLauncher/Models/$($_.Name)" }
    "research/usvfs-managed-poc/Program.cs"
    "research/usvfs-poc/child.cpp"
    "native/StalkerModLauncher.UsvfsX86Host/main.cpp"
    "scripts/Build-UsvfsDependencies.ps1"
    "scripts/Prepare-UsvfsSource.ps1"
    "scripts/Test-UsvfsRuntime.ps1"
    "scripts/UsvfsRuntimeManifest.psd1"
)
foreach ($path in $runtimePaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path) -PathType Leaf)) {
        throw "Missing CI guard fixture source: $path"
    }
}
foreach ($event in @("pull_request", "push")) {
    $section = [regex]::Match($workflow, '(?ms)^  ' + $event + ':\r?\n(?<body>.*?)(?=^  \w|^\S|\z)')
    if (-not $section.Success) { throw "Missing USVFS event: $event" }
    $patterns = @([regex]::Matches($section.Groups['body'].Value, "(?m)^      - '([^']+)'\r?$") |
        ForEach-Object { [Management.Automation.WildcardPattern]::new($_.Groups[1].Value) })
    foreach ($path in $runtimePaths) {
        if (-not ($patterns | Where-Object { $_.IsMatch($path) })) {
            throw "USVFS $event does not cover $path"
        }
    }
    foreach ($unrelated in @("README.md", "src/StalkerModLauncher/ViewModels/MainViewModel.Mods.cs")) {
        if ($patterns | Where-Object { $_.IsMatch($unrelated) }) {
            throw "USVFS $event includes unrelated path: $unrelated"
        }
    }
}
Write-Host "USVFS CI guards passed: both events cover runtime sources, models, smoke tests and build dependencies."
