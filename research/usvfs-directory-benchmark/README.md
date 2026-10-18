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
root (the `runtime` subdirectory contains flat files for manual testing only).

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

See [the experiment report](../../docs/USVFS_BATCHED_ENUMERATION_RU.md) for results,
the diagnosis of the original test failures, and the decision about adoption.
