---
applyTo: "Benchmark/**/*.cs"
---

# Benchmark conventions

- Uses BenchmarkDotNet targeting `net10.0`.
- Benchmarks compare "old" (current/baseline) vs "new" (proposed) implementations side-by-side.
- Always use `[MemoryDiagnoser]` to track allocations.
- Use `[SimpleJob(RuntimeMoniker.Net10_0)]` for the runtime target.
- Name benchmarks with the pattern `{IssueId}_{Old|New}` (e.g., `High1_Old`, `High1_New`).
- `[GlobalSetup]` initializes shared state; benchmarks must not allocate setup objects in the measured path.
- XML doc comments are NOT required (CS1591 is suppressed via `.editorconfig`).
- This project has `InternalsVisibleTo` access to `Ofn.ServiceFabric.Cache` — test internal helpers directly.
