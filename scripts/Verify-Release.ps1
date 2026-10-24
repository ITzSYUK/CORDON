param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "StalkerModLauncher.sln"
$project = Join-Path $repositoryRoot "src\StalkerModLauncher\StalkerModLauncher.csproj"
$tests = Join-Path $repositoryRoot "tests\StalkerModLauncher.Tests\StalkerModLauncher.Tests.csproj"
$usvfsSmokeScript = Join-Path $PSScriptRoot "Test-UsvfsRuntime.ps1"
. (Join-Path $PSScriptRoot 'ReleaseIntegrity.ps1')
$runtime = Get-UsvfsRuntime -RepositoryRoot $repositoryRoot

[xml]$projectFile = Get-Content -LiteralPath $project -Raw
$versionPropertyGroup = @($projectFile.Project.PropertyGroup) |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Version) } |
    Select-Object -First 1
$projectVersion = [string]$versionPropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "The application version is missing from $project."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $ExpectedVersion -ne $projectVersion) {
    throw "Expected version '$ExpectedVersion' does not match project version '$projectVersion'."
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

Write-Host "Verifying CORDON v$projectVersion..."
Test-UsvfsSourceProvenance -SourceRoot $runtime.SourceRoot -Manifest $runtime.Manifest
Invoke-DotNet -Arguments @("restore", $solution) -FailureMessage "dotnet restore failed."
Invoke-DotNet `
    -Arguments @("format", $solution, "--verify-no-changes", "--no-restore") `
    -FailureMessage "dotnet format verification failed."
Invoke-DotNet `
    -Arguments @(
        "build", $solution,
        "-c", "Release",
        "--no-restore",
        "-p:TreatWarningsAsErrors=true",
        "-p:WarningsNotAsErrors=NU1900"
    ) `
    -FailureMessage "Release build failed."
Invoke-DotNet `
    -Arguments @("test", $tests, "-c", "Release", "--no-build", "--no-restore") `
    -FailureMessage "Release tests failed."

& $usvfsSmokeScript -RuntimeRoot $runtime.SourceRoot

Write-Host "Automated release verification passed."
