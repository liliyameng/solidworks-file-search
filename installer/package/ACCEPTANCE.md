# SOLIDWORKS File Search 1.3.13 RC88

Static candidate scope:

- x64 per-machine MSI with one embedded CAB.
- ProductVersion 1.3.13; Assembly/FileVersion 1.3.13.0.
- Stable UpgradeCode and new RC88 ProductCode `{E5A97259-595F-4C23-8F9D-B5F423E7B69A}`.
- RC88 verified behavior: persistent one-hour cooling with queue due/autonomous lease for cloud-placeholder retries, bypassed by real changes or interaction; pending retries and recovery of prior failure rows are preserved. Read/write governor gates are separate to avoid read throttling holding a write lock. Existing rates/concurrency are unchanged; 750ms is the slow-extraction threshold, pressure cooldown is 5 seconds, and no on-machine measurement was performed.
- Framework-dependent .NET 8 entry points; no .NET runtime is bundled.
- WebView2 SDK loader is included; WebView2 Evergreen Runtime is a system prerequisite.
- SOLIDWORKS 2026 x64 CLR4 task pane is built against the official PIA with interop types embedded.
- Task-pane results are native Windows Shell items, not a custom draggable list.
- No Setup.exe, Burn bundle, ZIP, 7z, test helper, PDB, or installer coordinator
  executable is installed as product payload.
- Engineering drawings are not supported. The major upgrade removes only the
  legacy SwSearch drawing property verb, without touching other drawing verbs.

The build workflow performs compilation plus MSI table, CAB, payload, version,
COM-registration, and task-pane static checks. It does not install the MSI,
start SOLIDWORKS, or claim that real in-process drag/drop has been exercised.
