# USVFS directory enumeration experiment

Compares two already-built USVFS runtimes in real hooked x64 child processes.
This research project does not replace the launcher's runtime or read saved profiles.

Revisions used on 2026-10-18:

- baseline: `ModOrganizer2/usvfs@57f1ea5e6ad13f7435a7af184748e6c1312c5637`;
- candidate: [PR #99](https://github.com/ModOrganizer2/usvfs/pull/99),
  `Illustar0/usvfs@c5f7ef2085bd29a3cca2cd57f3aba84dae030ff5`.

Build both with identical Release toolchains and dependencies. Each runtime root
must contain `lib/usvfs_x64.dll`, `lib/usvfs_x86.dll`, and the corresponding
`bin/usvfs_proxy_*.exe`. Keep these experimental builds separate from `.external/usvfs`.
The optional candidate can now be built and validated with
`scripts/Build-UsvfsPr99.ps1`; use `.external/usvfs-pr99` as its benchmark runtime
root. Its `runtime` subdirectory contains the validated flat files and
`build-info.json` used by experimental launcher publishing and release packaging.

After building the native candidate, run `scripts/Build-Usvfs.ps1 -ExperimentalUsvfs`
to publish a test launcher to `publish/usvfs-experimental`. For release packaging,
`scripts/Build-Release.ps1 -IncludeExperimentalUsvfs` requires a clean working tree
and creates the two stable archives plus compact and standalone experimental
archives with the `-usvfs-experimental` suffix. Without these flags, both scripts
retain the stable runtime. They verify the experimental build record and hashes
and run x64/x86 smoke tests. The built-in updater still installs stable packages;
update experimental packages manually to retain that runtime.

From the repository root, restore/build this project and run:

```powershell
dotnet restore .\research\usvfs-directory-benchmark\StalkerUsvfsDirectoryBenchmark.csproj
.\research\usvfs-directory-benchmark\Compare.ps1 `
    -BaselineRoot <baseline-source-root> -CandidateRoot <candidate-source-root>
```

The comparison generates its own fixture in local Temp and stores results under
this project's ignored `obj/comparison-<ID>` directory. It retains the fixture and
evidence for inspection; it never deletes an existing directory. Run one comparison
at a time, without another USVFS session. Use an ASCII path to the checkout when
required by native tools.

The fixture has 40 overlapping mods, 20,000 numbered mod files, 1,000 numbered base
files, additional shared/Unicode files, and a profile overwrite. The final gamedata
tree has 10,253 files. Mapping and sampled content winners use the launcher's
`FileLayerPlan` and `UsvfsMappingPlanBuilder`, including an excluded-file fallback.

There are three alternating pairs of processes (AB, BA, AB). Each child performs
one warmup and seven measured recursive enumerations. Sorting, comparisons, sampled
content reads, wildcard checks, and write/replace/delete checks are outside the
timed region. The summary pools 21 scans per runtime and reports their median;
session preparation is measured separately. Source files are hashed before and
after the comparison, and runtime binary hashes are recorded.

The benchmark fails on missing/duplicate files, wrong sampled providers, failed
overwrite operations, escaped writes, modified sources, or a failed child process.
It measures warm x64 enumeration, not game startup, FPS, native heap usage, or
asynchronous NT API compatibility. Run `scripts/Test-UsvfsRuntime.ps1` and the
candidate's native correctness tests separately, sequentially.

For early source-scan completion, `--fixture-sparse <new-directory>` creates four
source folders with 10,002 files each: two selected names at the beginning and
10,000 unrelated names after them. Run the built executable with
`--host <runtime-root> <same-fixture-directory> <result.json>` for each runtime.
The timed Win32 search uses `FindFirstFileW`/`FindNextFileW` with `a-selected-*`
(eight files), passing the pattern to USVFS. The normal .NET enumeration filters
names after reading them and does not isolate this optimization. A complete untimed
recursive listing, sampled contents and overwrite checks still run. Compare medians on the
same fixture; this deliberately favorable case measures skipped tail work, not
expected seconds saved during game startup.

See [the release measurements in Russian](../../RELEASE_NOTES.md) for before-and-after
timings and [the technical documentation](../../docs/TECHNICAL_EN.md) for build
instructions and compatibility limits.
