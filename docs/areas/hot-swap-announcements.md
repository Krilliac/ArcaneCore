# Hot swap announcements (`World:Announce`)

Every live change the server takes without a restart is reported in one place: the data reloads (`.reload`), the hot
modules (`.hotmodule`) and the code hot reload (`dotnet watch`, Development / Staging only). The report is always logged
and counted; it is also sent to the staff who are online and, if you ask for it, to every player.

The service behind it is generic (`IOpsAnnouncer`, categories, per-category switches, severity floor and rate limit), so
other areas can announce through the same switches. Only the **HotSwap** category is wired today.

## What is sent

| Where | Always | Needs |
|---|---|---|
| Log | one line per report: `Announce [HotSwap/Info] ... (sent)` | nothing |
| Metric | `arcanecore.hotswap{kind,result}` (every report, whatever the filters say) and `arcanecore.announce{category,severity,outcome}` | `Ops:Metrics:Enabled` to export |
| Staff (every online account above Player) | an on-screen notification and a chat line | `GmNotify` (default **on**) |
| Players | one short system chat line | `WorldMessage` (default **off**) |

A staff member who receives the staff line is not sent the world line as well.

Staff line, with `IncludeDetails` on: what happened, then who triggered it (account and character, `console`, `startup` or
`dotnet watch`), the reason they typed, the duration and the lane's own facts (table summary, module SHA-256 prefix, the
opcodes / command roots / creature AI / script hooks it registered, the previous hash on a replace, creatures switched).

```
[HotSwap] Live data reload 'creature_template' applied (fix drop rates) | by acct (Alice); reason: fix drop rates; 250 ms; reloaded: 12345 creature templates
[HotSwap] Server module 'GmCommands' replace was REJECTED, nothing changed | by acct (Alice); 5 ms; refused by the module allowlist: sha256 ... is not in the allowlist
```

World line: `[Server] Live data reload 'creature_template' applied (fix drop rates)`. It never carries an account or character name,
a count, a hash or a duration. The reason is the one thing a staff member typed that players can see, so keep it
player-safe or turn `WorldMessageIncludeReason` off. A rejected or failed swap is staff-only unless `WorldMessageOnFailure`.
A reload that changed nothing (`KeptCurrent`) is staff-only.

Typed text is cleaned: control characters become spaces and `|` becomes `/`, so a reason cannot inject colours or links.
`.reload all` sends one summary, not one line per table.

## The reason argument

```
.reload creature_template                      # as before
.reload creature_template fixed the drop rates # the rest of the line is the reason
.reload all weekly content refresh
.hotmodule load GmCommands new GM helper
.hotmodule reload GmCommands v2: fix the typo  # reload = replace
.hotmodule unload GmCommands
```

The first word is still the table or module name; whatever follows is the reason (at most 120 characters). A reload name with
spaces never existed, so no old command changes meaning. The reason is also written to the hot-code audit log for modules.

## Configuration

All keys restart-only. `Enabled` and the category switches only decide delivery; the log line and the metrics stay.

| Key | Default | Meaning |
|---|---|---|
| `World:Announce:Enabled` | `true` | Master switch for delivery. |
| `World:Announce:HotSwap:GmNotify` | `true` | Staff notification + chat line. A server with no hot lane switched on never reports anything, so the default costs nothing there. |
| `World:Announce:HotSwap:WorldMessage` | `false` | Short line to all players. |
| `World:Announce:HotSwap:IncludeDetails` | `true` | Who / how long / counts / hashes on the staff line. |
| `World:Announce:HotSwap:MinimumSeverity` | `Info` | `Info` (applied), `Warning` (rejected), `Error` (failed). Below it: logged and counted only. |
| `World:Announce:HotSwap:Lanes` | empty = all | List of `Data`, `Module`, `Code`. |
| `World:Announce:HotSwap:WorldMessageOnFailure` | `false` | Tell players about a rejected / failed swap. |
| `World:Announce:HotSwap:WorldMessageIncludeReason` | `true` | Put the typed reason in the world line. |
| `World:Announce:HotSwap:AllowNamesInWorldMessage` | `false` | Generic switch; the hot swap texts never contain names, so it changes nothing here. |
| `World:Announce:HotSwap:RateBurst` / `RatePerMinute` | `10` / `20` | Per-category token bucket (0 burst = unlimited). Suppressed ones are counted (`outcome=rate_limited`) and the next delivered line says how many. |
| `World:Announce:HotSwap:DedupSeconds` | `0` | Suppress an identical text this many seconds after it was delivered. Hot swap events are compared by lane + action + area + result + actor (the same swap by the same person), not by the full text. |

`check-config` / the start-up report validate the section (`AnnounceConfigChecks`): booleans, ranges, severity names, lane names.

## Using it from another area

```csharp
// anywhere the container is available; any thread; never throws
announcer.Announce(
    AnnounceCategory.HotSwap,        // add a member, an options property and a case in OpsAnnouncer.OptionsFor
    AnnounceSeverity.Warning,
    shortText: "Anti-cheat raised its alert level",       // player-safe: it is the world line
    gmDetail: "account 42 flagged for speed hacking",     // staff only
    fields: [new("check", "speed")],                      // staff only
    shortTextWithNames: null,                             // used only if the category sets AllowNamesInWorldMessage
    gmOnly: false,
    dedupKey: "anticheat-alert");
```

New category checklist: `AnnounceCategory` member, a property on `AnnounceOptions` (type derived from `AnnounceCategoryOptions`),
the case in `OpsAnnouncer.OptionsFor`, the name in `AnnounceConfigChecks.Categories`, regenerate the docs.
