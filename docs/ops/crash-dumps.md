# Crash dumps

How a crashed daemon leaves a dump, and how the supervisors behave while one is written. Source of truth for the settings:
`Diagnostics:CrashDump` ([invariants.md](invariants.md#the-crash-dump)), the heartbeat protocol ([watchdog.md](watchdog.md)) and
`tools/ops/diag/register-crash-dumps.ps1`.

## Three writers

| Writer | Covers | Needs |
|---|---|---|
| In-process (`Diagnostics:CrashDump`) | Managed crashes, the tick watchdog's fatal path, invariant `FailFast` | Nothing; on by default (Mode) |
| Runtime createdump (`DOTNET_DbgEnableMiniDump=1`) | A managed `FailFast` | The variable in the environment at process start (`launch-daemon.ps1 -Diagnostics Max` sets it) |
| WER LocalDumps | Also a stack overflow, a fatal runtime error and a native access violation, which can end the process before a managed handler runs | Registry keys, set once by `register-crash-dumps.ps1` |

A dump is a copy of process memory: session keys, password-derived material and the gateway link secret are in it. Keep the folder
access-controlled and never attach a dump to an issue.

## register-crash-dumps.ps1 (WER LocalDumps)

Opt-in, run once from an elevated shell (it never elevates itself). The registry is HKLM; an absent `LocalDumps` parent key is refused without `-CreateParentKey`
.

```
pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Plan             # print the keys and values, change nothing
pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1                   # elevated: register
pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Status           # read-only
pwsh -NoProfile -File tools/ops/diag/register-crash-dumps.ps1 -Unregister       # elevated: remove what it created
```

- Keys: `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe>` for `ArcaneCore.World.exe`, `ArcaneCore.Realm.exe`
  and `ArcaneCore.Gateway.exe`, with `DumpFolder`, `DumpType` (`-DumpType Full|Mini`, default Full), `DumpCount` (default 5) and a marker
  value. Value names and types are in Microsoft's
  [Collecting User-Mode Dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps).
- The dump folder is `-DumpFolder`, else `<ARCANECORE_LIVE_HOME>/dumps/wer` (a folder of its own: the sidecar trims `*.dmp` directly in
  `<live home>/dumps` only). With neither, the script stops. No path or secret is stored in the script.
- A key it did not create is never changed; a failed write is rolled back; `-Unregister` removes only keys that carry the marker.
- The parent key switches WER dumps on for every program on the machine, so an absent one is only created with `-CreateParentKey`.

## The `dumping` heartbeat

While a daemon writes its own dump it announces `dumping` once and stops beating. A supervisor that kills a process for a stale heartbeat
would truncate the dump, so both supervisors grant a grace instead of the stale-heartbeat kill:

- stdout: `heartbeat <UTC> pid=<pid> dumping max=<s> token=<t>` (`max` is three times `Diagnostics:CrashDump:TimeoutSeconds`, for the
  whole Full, Heap, Mini chain). The cluster host supervisor honours it only with the right token.
- file: `<UTC> pid=<pid> dumping max=<s>` (the process's own file, no token).
- grace = `min(max + 60, cap)`. The cap is `Cluster:Supervisor:DumpGraceSeconds` (cluster host supervisor) and `-DumpGraceSeconds`
  (`server-status.ps1`), both default 420. A line without `max=` (an older writer) gets the cap.
- The cluster host supervisor also takes no hang capture of its own meanwhile, and does not drain or kill the child inside the grace.
- Raise the cap if you raise `TimeoutSeconds` above 120 (cap should stay above `3 * TimeoutSeconds + 60`).

## VERIFY IT ONCE (two dumps for one crash?)

Unverified: with LocalDumps registered and `DOTNET_DbgEnableMiniDump=1`, one managed crash may leave two dumps (the runtime's
`<name>` file and WER's `<exe>.<pid>.dmp`). The operator runs this once; no part of the repository starts a daemon. Use a scratch machine
or a throwaway program, never a live daemon.

1. Build a console program `Crash.exe` with three modes: `Environment.FailFast("x")`; unbounded recursion (stack overflow); an unhandled
   exception thrown on a new worker thread.
2. Check the plan (add `-CreateParentKey` to the next step only if it says the parent key is absent), then register it from an
   elevated shell:
   `register-crash-dumps.ps1 -Plan -Executables Crash.exe -DumpFolder <wer dir> -DumpType Mini`
   `register-crash-dumps.ps1 -Executables Crash.exe -DumpFolder <wer dir> -DumpType Mini`
3. For each of the three modes run it twice from a fresh shell: once with `DOTNET_DbgEnableMiniDump=0`, once with
   `DOTNET_DbgEnableMiniDump=1` and `DOTNET_DbgMiniDumpName=<other dir>\crash-%p.dmp`.
4. After each run count the files in `<wer dir>` and `<other dir>`. That shows which mechanism writes for which crash, and whether
   one managed crash gives two dumps.
5. `register-crash-dumps.ps1 -Unregister -Executables Crash.exe`, then delete the dump folders (they hold process memory).

Provisional advice until it is run: keep the runtime's file for a managed crash and WER's for a stack overflow, a native access violation
or any crash where createdump left nothing; delete the other. To get only WER's, start the daemons with `DOTNET_DbgEnableMiniDump=0`
in the launching environment. Record the result here once observed.
