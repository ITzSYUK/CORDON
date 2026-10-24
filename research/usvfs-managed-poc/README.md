# Managed USVFS PoC

This research-only console app verifies the managed adapter in
`StalkerModLauncher.Services.UsvfsRuntime`.

It uses the official USVFS runtime through P/Invoke, applies a
`UsvfsMappingPlan` and checks that hooked processes see the overlaid files.
The x64 scenario starts a managed child; the x86 scenario uses the native x86
host, launcher and child executables.

Run from the repository root after building dependencies with
`scripts\Build-UsvfsDependencies.ps1`:

```powershell
.\scripts\Test-UsvfsRuntime.ps1
```

The script uses the main runtime in `.external\usvfs-pr99` by default, runs five sessions for each
architecture, and checks changed overlay contents, child lifetime and writable
directory behavior. See the [technical documentation](../../docs/TECHNICAL_EN.md)
for dependency preparation and release checks.

This project is not part of the normal launcher runtime. It remains an isolated
smoke-test harness for the stable production USVFS backend.
