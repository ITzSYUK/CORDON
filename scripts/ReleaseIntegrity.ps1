function Import-UsvfsRuntimeManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $manifest = Import-PowerShellDataFile -LiteralPath $Path
    if ($manifest.SchemaVersion -ne 2 -or
        [string]::IsNullOrWhiteSpace([string]$manifest.RuntimeVersion) -or
        [string]::IsNullOrWhiteSpace([string]$manifest.SourceRevision) -or
        @($manifest.Files).Count -eq 0) {
        throw "Invalid USVFS runtime manifest: $Path"
    }

    return $manifest
}

function Invoke-RepositoryGit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string[]]$CommandArguments
    )

    $resolvedSourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    $gitArguments = @("-c", "safe.directory=$resolvedSourceRoot", "-C", $resolvedSourceRoot)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& git @gitArguments @CommandArguments 2>$null)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return @{
        ExitCode = $exitCode
        Output = $output
    }
}

function Test-UsvfsRuntimeIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,
        [Parameter(Mandatory = $true)]
        [hashtable]$Manifest
    )

    foreach ($file in $Manifest.Files) {
        $path = Join-Path $RuntimeRoot $file.RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing pinned USVFS component '$($file.Name)': $path"
        }

        $version = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
        if ($version -ne $Manifest.RuntimeVersion) {
            throw "Unexpected USVFS version for '$($file.Name)': expected $($Manifest.RuntimeVersion), found '$version'."
        }

    }

    Write-Host "USVFS runtime components verified: v$($Manifest.RuntimeVersion), revision $($Manifest.SourceRevision)."
}

function Get-ExperimentalUsvfsRuntime {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $manifest = Import-UsvfsRuntimeManifest -Path (Join-Path $RepositoryRoot 'scripts\UsvfsPr99Manifest.psd1')
    $sourceRoot = Join-Path $RepositoryRoot '.external\usvfs-pr99'
    $runtimeRoot = Join-Path $sourceRoot 'runtime'
    $buildInfoPath = Join-Path $runtimeRoot 'build-info.json'
    $rebuildMessage = 'Run scripts\Build-UsvfsPr99.ps1 to rebuild the experimental runtime.'
    if (-not (Test-Path -LiteralPath $buildInfoPath -PathType Leaf)) {
        throw "Experimental USVFS build record is missing. $rebuildMessage"
    }
    $buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
    if ($buildInfo.SourceRevision -ne $manifest.SourceRevision -or
        $buildInfo.SourceRepository -ne $manifest.SourceRepository) {
        throw "Experimental USVFS source revision does not match the manifest. $rebuildMessage"
    }
    if (@($buildInfo.Patches).Count -ne @($manifest.SourcePatches).Count) {
        throw "Experimental USVFS patch list does not match the manifest. $rebuildMessage"
    }
    foreach ($patchPath in $manifest.SourcePatches) {
        $name = Split-Path -Leaf $patchPath
        $records = @($buildInfo.Patches | Where-Object Name -eq $name)
        $hash = (Get-FileHash -LiteralPath (Join-Path $RepositoryRoot $patchPath) -Algorithm SHA256).Hash
        if ($records.Count -ne 1 -or $records[0].Sha256 -ne $hash) {
            throw "Experimental USVFS patch is outdated: $name. $rebuildMessage"
        }
    }
    if (@($buildInfo.Files).Count -ne @($manifest.Files).Count) {
        throw "Experimental USVFS component list does not match the manifest. $rebuildMessage"
    }
    $files = @{}
    $hashes = @{}
    foreach ($file in $manifest.Files) {
        $records = @($buildInfo.Files | Where-Object { (Split-Path -Leaf $_.Path) -eq $file.Name })
        if ($records.Count -ne 1 -or $records[0].Hash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Invalid experimental USVFS component record: $($file.Name). $rebuildMessage"
        }
        $path = Join-Path $runtimeRoot $file.Name
        # The smoke test uses lib/bin, so both copies must be the validated build.
        foreach ($copy in @($path, (Join-Path $sourceRoot $file.RelativePath))) {
            if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
                (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $records[0].Hash) {
                throw "Experimental USVFS component is missing or changed: $copy. $rebuildMessage"
            }
        }
        $files[$file.Name] = $path
        $hashes[$file.Name] = $records[0].Hash
    }
    Test-UsvfsRuntimeIntegrity -RuntimeRoot $sourceRoot -Manifest $manifest
    return @{ SourceRoot = $sourceRoot; Manifest = $manifest; Files = $files; Hashes = $hashes }
}

function Test-UsvfsSourceProvenance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [hashtable]$Manifest,
        [Parameter(Mandatory = $true)]
        [string]$PatchPath
    )

    $resolvedSourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedSourceRoot ".git"))) {
        throw "USVFS source repository is missing: $resolvedSourceRoot"
    }

    if (-not (Test-Path -LiteralPath $PatchPath -PathType Leaf)) {
        throw "Tracked USVFS source patch is missing: $PatchPath"
    }

    $revisionResult = Invoke-RepositoryGit -SourceRoot $resolvedSourceRoot -CommandArguments @("rev-parse", "HEAD")
    $revision = ($revisionResult.Output -join "").Trim()
    if ($revisionResult.ExitCode -ne 0 -or $revision -ne $Manifest.SourceRevision) {
        throw "USVFS source revision must be $($Manifest.SourceRevision), found '$revision'."
    }

    $changesResult = Invoke-RepositoryGit `
        -SourceRoot $resolvedSourceRoot `
        -CommandArguments @("diff", "--name-only", "--ignore-space-at-eol")
    $changedFiles = @(
        @($changesResult.Output) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($changesResult.ExitCode -ne 0) {
        throw "Failed to inspect USVFS source changes."
    }

    $expectedChangedFile = "src/shared/CMakeLists.txt"
    if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $expectedChangedFile) {
        throw "USVFS source contains changes outside the tracked patch: $($changedFiles -join ', ')."
    }

    $patchResult = Invoke-RepositoryGit `
        -SourceRoot $resolvedSourceRoot `
        -CommandArguments @("apply", "--reverse", "--check", $PatchPath)
    if ($patchResult.ExitCode -ne 0) {
        throw "USVFS source does not match the tracked patch: $PatchPath"
    }

    Write-Host "USVFS source provenance verified: revision $revision plus $($Manifest.SourcePatch)."
}

function Write-Sha256ChecksumFile {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Paths,
        [Parameter(Mandatory = $true)]
        [string]$RelativeTo,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $lines = foreach ($path in $Paths | Sort-Object) {
        $fullPath = [System.IO.Path]::GetFullPath($path)
        $relativePath = Get-RelativePathWithinRoot -Root $RelativeTo -Path $fullPath
        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        "$hash  $relativePath"
    }

    Set-Content -LiteralPath $OutputPath -Value $lines -Encoding ascii
}

function Test-Sha256ChecksumFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$ChecksumPath,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedPaths
    )

    if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
        throw "Checksum file is missing: $ChecksumPath"
    }

    $entries = @{}
    foreach ($line in Get-Content -LiteralPath $ChecksumPath) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            throw "Invalid checksum line in ${ChecksumPath}: $line"
        }

        $relativePath = $Matches[2].Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if ([System.IO.Path]::IsPathRooted($relativePath)) {
            throw "Checksum contains an absolute path: $relativePath"
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        $normalizedRelativePath = Get-RelativePathWithinRoot -Root $Root -Path $fullPath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Checksummed file is missing: $fullPath"
        }

        if ($entries.ContainsKey($normalizedRelativePath)) {
            throw "Duplicate checksum entry: $normalizedRelativePath"
        }

        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if ($actualHash -ne $Matches[1]) {
            throw "Checksum mismatch for '$normalizedRelativePath': expected $($Matches[1]), found $actualHash."
        }

        $entries[$normalizedRelativePath] = $actualHash
    }

    $expectedEntries = @(
        $ExpectedPaths |
            ForEach-Object { Get-RelativePathWithinRoot -Root $Root -Path $_ } |
            Sort-Object -Unique
    )
    if ($entries.Count -ne $expectedEntries.Count) {
        throw "Checksum coverage mismatch in ${ChecksumPath}: expected $($expectedEntries.Count) files, found $($entries.Count)."
    }

    foreach ($relativePath in $expectedEntries) {
        if (-not $entries.ContainsKey($relativePath)) {
            throw "Checksum entry is missing for: $relativePath"
        }
    }

    Write-Host "Checksums verified: $ChecksumPath"
}

function Test-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$ExecutableName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedCommit,
        [Parameter(Mandatory = $true)]
        [hashtable]$UsvfsManifest,
        [hashtable]$RuntimeHashes = @{}
    )

    $requiredFiles = @(
        $ExecutableName,
        "StalkerModLauncher.UsvfsX86Host.exe",
        "usvfs_x64.dll",
        "usvfs_proxy_x64.exe",
        "usvfs_x86.dll",
        "usvfs_proxy_x86.exe",
        "LICENSE.txt",
        "THIRD-PARTY-NOTICES.txt",
        "checksums.txt"
    )
    foreach ($fileName in $requiredFiles) {
        $path = Join-Path $PackageDirectory $fileName
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release package is missing required file '$fileName': $PackageDirectory"
        }
    }

    $unexpected = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File |
        Where-Object { $_.Extension -in @('.pdb', '.json', '.md') }
    if ($unexpected) {
        throw "Unexpected files in release package: $($unexpected.FullName -join ', ')"
    }

    $packageFiles = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File |
        Select-Object -ExpandProperty FullName
    $checksummedFiles = $packageFiles |
        Where-Object { -not $_.Equals((Join-Path $PackageDirectory "checksums.txt"), [System.StringComparison]::OrdinalIgnoreCase) }
    Test-Sha256ChecksumFile `
        -Root $PackageDirectory `
        -ChecksumPath (Join-Path $PackageDirectory "checksums.txt") `
        -ExpectedPaths $checksummedFiles

    $executablePath = Join-Path $PackageDirectory $ExecutableName
    $versionInfo = (Get-Item -LiteralPath $executablePath).VersionInfo
    if ($versionInfo.FileVersion -ne "$ExpectedVersion.0" -or
        $versionInfo.ProductVersion -ne "$ExpectedVersion+$ExpectedCommit") {
        throw "Unexpected launcher version in '$ExecutableName': FileVersion=$($versionInfo.FileVersion), ProductVersion=$($versionInfo.ProductVersion)."
    }

    foreach ($runtimeFile in $UsvfsManifest.Files) {
        $runtimePath = Join-Path $PackageDirectory $runtimeFile.Name
        $version = (Get-Item -LiteralPath $runtimePath).VersionInfo.FileVersion
        if ($version -ne $UsvfsManifest.RuntimeVersion) {
            throw "Packaged USVFS component has an unexpected version: $($runtimeFile.Name)"
        }
    }

    foreach ($name in $RuntimeHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $PackageDirectory $name) -Algorithm SHA256).Hash -ne $RuntimeHashes[$name]) {
            throw "Packaged USVFS component differs from the selected build: $name"
        }
    }

    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "Release archive is missing: $ArchivePath"
    }

    $verificationRoot = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        ("StalkerModLauncherReleaseVerify-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $verificationRoot | Out-Null
    try {
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $verificationRoot
        $archiveFiles = Get-ChildItem -LiteralPath $verificationRoot -Recurse -File |
            Select-Object -ExpandProperty FullName
        $packageHashes = Get-FileHashMap -Root $PackageDirectory -Paths $packageFiles
        $archiveHashes = Get-FileHashMap -Root $verificationRoot -Paths $archiveFiles
        if ($packageHashes.Count -ne $archiveHashes.Count) {
            throw "Archive content count differs from the package directory: $ArchivePath"
        }

        foreach ($relativePath in $packageHashes.Keys) {
            if (-not $archiveHashes.ContainsKey($relativePath) -or
                $archiveHashes[$relativePath] -ne $packageHashes[$relativePath]) {
                throw "Archive content mismatch for '$relativePath': $ArchivePath"
            }
        }

        $archiveChecksums = Join-Path $verificationRoot "checksums.txt"
        $archiveChecksummedFiles = $archiveFiles |
            Where-Object { -not $_.Equals($archiveChecksums, [System.StringComparison]::OrdinalIgnoreCase) }
        Test-Sha256ChecksumFile `
            -Root $verificationRoot `
            -ChecksumPath $archiveChecksums `
            -ExpectedPaths $archiveChecksummedFiles
    } finally {
        $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolvedVerificationRoot = [System.IO.Path]::GetFullPath($verificationRoot)
        if ($resolvedVerificationRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedVerificationRoot).StartsWith("StalkerModLauncherReleaseVerify-")) {
            Remove-Item -LiteralPath $resolvedVerificationRoot -Recurse -Force
        }
    }

    Write-Host "Release package verified: $ArchivePath"
}

function Get-RelativePathWithinRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $normalizedRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $normalizedRoot += [System.IO.Path]::DirectorySeparatorChar
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: $fullPath"
    }

    return $fullPath.Substring($normalizedRoot.Length).Replace('\', '/')
}

function Get-FileHashMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string[]]$Paths
    )

    $hashes = @{}
    foreach ($path in $Paths) {
        $relativePath = Get-RelativePathWithinRoot -Root $Root -Path $path
        $hashes[$relativePath] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    return $hashes
}
