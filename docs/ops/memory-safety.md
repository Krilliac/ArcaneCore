# Memory and thread safety rules

C# already gives memory safety (GC, bounds checks, nullable references as errors, `AllowUnsafeBlocks` only in World and Playerbots, each baselined as an `assembly:` finding). What a game
server still needs from "Rust-like" safety is thread ownership, resource ownership, entity lifetime, exhaustiveness and audited
`unsafe`. This page is what is enforced, by what, and where the limits are.

## The analyzer (`src/ArcaneCore.Analyzers`)

A Roslyn analyzer referenced by every non-test project from `Directory.Build.props`. All rules are warnings, so warnings-as-errors
breaks the build. Tests: `tests/ArcaneCore.Analyzers.Tests` (positive and negative case per rule).

| Rule | Reports |
|---|---|
| ARC0001 | A `[WorldThreadOnly]` member used in a lambda or method group passed to `Task.Run`, `ContinueWith`, `TaskFactory.StartNew`, `ThreadPool.*`, `Parallel.*`, `new Thread`, `new Timer`, `CancellationToken.Register`. |
| ARC0002 | A `[WorldThreadOnly]` member used after an `await` in the same function. The world thread has no `SynchronizationContext`, so every continuation runs on the pool. |
| ARC0003 | `Unsafe`, `MemoryMarshal`, `NativeMemory`, `Marshal` (except last-error getters), P/Invoke, or `AllowUnsafeBlocks`, outside `[AuditedUnsafe("reason")]` on the member, type or assembly. |
| ARC0004 | `ArrayPool.Rent` with no `Return` in the same member; `MemoryPool.Rent` not in a `using`. |
| ARC0005 | A buffer read by a later statement of the same block after `ArrayPool.Return(buffer)`. |
| ARC0006 | `Task.Result`, `Task.Wait`, `Task.WaitAll/WaitAny`, `GetAwaiter().GetResult()`. |
| ARC9001 / ARC9002 | A baseline entry that allows more than the code has / a malformed baseline line. |

Attributes live in `ArcaneCore.Kernel.Safety`: `[WorldThreadOnly]` (on `WorldObject`, `Map`, `CreatureMapSystem`; inherited by derived
types), `[AnyThread]` (opt-out member, e.g. `WorldObject.Guid`), `[RunsOnWorldThread]` (on a method, or on a delegate parameter: the
callback runs on the world thread, e.g. `WorldRuntime.Post`/`InvokeAsync`), `[AuditedUnsafe]`.

### Baseline

`analyzers/safety-baseline.txt` lists the findings that predate a rule: `RULE|path|Type.Member|detail|count`. A finding inside its
count is silent, one beyond it fails the build, and a count above what the code has fails too (ARC9001), so the file only shrinks.
The diagnostic message prints the exact line. Fix new findings; do not add lines for new code. After fixing old ones, or when adding a rule,
run `powershell -NoProfile -File scripts/safety-baseline.ps1`.

### Suppressing a finding

When a report is a false positive or the object is genuinely not world-owned yet (for example a player being loaded, not in a map), suppress that one statement with a reason; do not add a baseline line for new code:

```csharp
#pragma warning disable ARC0002 // the player is still loading: no map, so no world thread owns it yet
player.Inventory.Templates = templates;
#pragma warning restore ARC0002
```

### Limits (what is not caught)

- Classification is by the call a lambda is an argument of. A lambda stored in a variable and passed later is "unknown" (not reported).
- A named method passed to `Task.Run` is checked only when the method group itself is `[WorldThreadOnly]`; its body is not followed.
- ARC0002 is decided on the compiler's control-flow graph, not on source order. A reference is reported when a suspension can run before it: in the same block the suspension must come first; across blocks the normal, conditional, loop (back edge), `break`, `continue`, `goto` and `switch` edges are followed, as are the `finally` regions a jump passes through. Suspension points counted: `await`, `await foreach` (before its body) and `await using (...)` statements (after them). Not counted: `await using var x = ...;` declarations (the disposal await is at scope end); awaits of tasks that happen to be complete are counted like any other (the analyzer cannot know). So use before the `await` in a loop body is reported, because the next iteration runs it after the await.
- Exceptions: a failed await is assumed to continue at the catch, filter or finally of every enclosing `try`. `try { await x; return; } catch { throw; } e.Tick();` and `catch { return; }` do not fall through, while `try { await x; return; } catch { } e.Tick();` is reported, and a `finally` entered by an exception propagates onward. An await inside a `finally` taints the code after it. Over-reporting: catch types and `when` filters are ignored (a rejected filter still reaches the outer catch), and every `try` statement is assumed able to throw. Suspensions in a callee, or an awaited local function called later, are not followed (false negatives).
- A reference inside a local function is judged only by suspensions inside that local function; where it is declared or called from is not followed.
- A call, member read or indexer is positioned where it runs (after its receiver and arguments), and an assignment target where it is written: `e.Set(await x)`, `(await t).Level` and `e.Level = await x` are after the await. `nameof(e.Level)` is not an access.
- ARC0005 follows straight-line statements in one block (and the statements after a `try` whose `finally` returns); conditional returns and loops are not followed.
- Dependency injection, interfaces and reflection hide the callee; only symbols the compiler resolves are checked.
- Test projects (`IsTestProject`) and vendored code are not analysed; `TestSupport`, `SupervisedChildFixture` and `ArcaneCore.Data.ProcessRunner` are, because they do not set it. `AllowUnsafeBlocks` in World and Playerbots is baselined as an `assembly:` entry, which keeps ARC0003 live inside those assemblies (an assembly-level `[AuditedUnsafe]` would switch it off).

## Exhaustiveness

`CS8509` (switch expression misses a case) and `CS8524` (misses an undefined enum value, reported even when every named member is covered)
are named in `WarningsAsErrors` in `Directory.Build.props`. The tree had no violations when they were pinned. Switch *statements* have no
compiler check; `IDE0010`/`IDE0072` would flag 549 existing sites (most have a deliberate `default`), so they are not enabled.

## Runtime backstop

`ThreadAffinity` (`ArcaneCore.Kernel.Safety`) is the runtime half of `[WorldThreadOnly]`. `WorldObject.MarkChanged` (every real change to
an update field of an object in a map) calls `Map.CheckWorldThread`; off the world thread that counts the violation and records the first 64 in
`FaultLedger.Shared` as an `InvariantViolationException` with site `thread-affinity:WorldObject.MarkChanged`. It never throws. The ledger signature is built from the stack at the detection point (the exception is never thrown, so `FaultLedger.Record(site, exception, StackTrace)` is given the captured stack; methods marked `[StackTraceHidden]` are skipped), so it names the code that made the change. The count is the `arcanecore_thread_affinity_violations_total` metric. The pass
path is one thread-id compare, or a `[ThreadStatic]` read on parallel map threads (`WorldRuntime.IsWorldThread`; a world that has no running thread, as in most tests, passes). `ThreadAffinity.Enabled = false`
turns the report off. `Map.OnObjectMoved` (every position change of an in-map object) makes the same check, site `thread-affinity:Map.OnObjectMoved`. Map add/remove and the other map mutators throw through `Map.EnsureWorldThread`.

## Stale entity references (design, not implemented)

The audit found 145 collection fields and 126 single fields outside the entity classes holding `Player`/`Creature`/`Unit`/`GameObject`
references across ticks (many in `Creatures/Scripts`), guarded by `IsInWorld` checks (259 uses). No concrete stale-reference bug was found in
the history that a handle would have prevented, and there is no contained adoption site, so none was added. Design if one appears:
`readonly record struct EntityRef<T>(ObjectGuid Guid, uint Generation) where T : WorldObject`, resolved each use through the owning map
(`map.TryGet(ref, out T)`), null after despawn because the map bumps the generation when it removes the object. First adoption would be a
script AI's `Summoned` lists.
