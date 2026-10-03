# Reported build-5875 client run, 2026-10-03

The user-provided report records successful realm authentication, Human Warrior
creation, Northshire entry, an 80.5-second stability interval and basic movement
at exact server revision `f8ae6e8b5f805e94f145194a82f80e876fa2dec3`. Standard logout,
fresh login and restart/relog were stopped before testing. This advances bounded
real-client evidence; it does not establish general playability or complete the
remaining acceptance checks.

## Provenance and tested configuration

This document records the supplied report. The integration documentation lane
did not inspect screenshots, logs, databases, client files or the referenced run
directory, and did not independently reproduce the observations. Results below
are therefore **reported passes**, not additional verification by this lane.

| Field | Supplied value |
|---|---|
| Date/timezone | 2026-10-03, `America/Chicago` (CDT). |
| Displayed client | `1.12.1 (5875) Release`, with displayed date `Sep 19, 2006`. |
| Server checkout | Clean detached checkout `D:\ArcaneCore-client-acceptance-f8ae6e8`. |
| Exact tested server SHA | `f8ae6e8b5f805e94f145194a82f80e876fa2dec3`. |
| Build | .NET SDK 10.0.401; Release, zero warnings/errors. |
| Databases | Fresh SQLite; auth schema 2, characters schema 6, world schema 6. |
| Network | Loopback realm/world. |
| Account/character | `CLIENTA` / `Clienta`, Human male Warrior, level 1. |
| Content | Empty creature/item/spell/quest content and no server terrain; expected for this baseline. |

Later canonical publication and the separate quest greeting/fixture successor
do not change this tested pin or extend this run's observations. The preceding
automated qualification and setup commands remain in
[the original handoff](quest-client-acceptance.md). The
[quest UI follow-up](quest-ui-acceptance.md) is a separately selected future run.

The supplied run reference is
`C:\Users\Nathan\Documents\Codex\2026-10-03\t\work\ArcaneCore-client-f8ae6e8`.
It was outside this documentation lane's permitted inspection scope and was not
accessed or copied. Auth storage contains disposable authentication material;
exclude it from a shared evidence bundle. No client assets belong in the repository.

## Reported results and pending acceptance

| Check | Result | Supplied observation / boundary |
|---|---|---|
| `CLIENTA` authentication and realm list | REPORTED PASS | Login succeeded and the realm list appeared. |
| Human male Warrior creation and character list | REPORTED PASS | `Clienta` was created and listed with the intended identity. |
| Northshire world entry | REPORTED PASS | Character visibly entered the world. |
| Connected stability after visible entry | REPORTED PASS | An 80.5-second interval completed without observed disconnect. |
| Walk, jump and landing | REPORTED PASS | No observed snap-back or disconnect. |
| User-assisted held A/Q movement/turn | REPORTED PASS | Read-only database observations reflected changed XY/orientation; character identity remained unchanged. This is storage observation, not a fresh-login position test. |
| Standard 20-second logout and fresh login | NOT TESTED | Computer use stopped before these steps. |
| Saved position after fresh login | NOT TESTED | No fresh login followed the movement observations. |
| World restart and relog | NOT TESTED | Clean shutdown occurred; restarting and reconnecting were not exercised. |
| Two-player movement/visibility | NOT TESTED | This report covers one client/character. |
| Quests, combat and reward UI | NOT TESTED | Baseline content was empty. |
| Server terrain/collision | NOT TESTED | No server terrain data was configured. Jump/landing observations do not qualify this category. |
| Clustering/general playability | NOT TESTED | Outside this bounded run. |

The user's physical Escape stopped ComputerUse before logout/relog/restart
acceptance. The report states that no client input followed that stop. These
unexecuted checks remain pending; cleanup does not substitute for them.

## Timeline and read-only position observations

Times below are supplied CDT times on 2026-10-03; no additional timing was inferred.

| Event | Time |
|---|---|
| Run start | 01:42:07 |
| Visible world entry | 01:45:43 |
| Assisted movement snapshot | 01:51:12 |
| Shutdown | 01:51:55 |

Reported identity remained character ID **1**, race **1**, class **1**, gender
**0**, level **1**, map **0**, zone **12** throughout these observations.

| Observation | X | Y | Z | Orientation |
|---|---:|---:|---:|---:|
| Initial | -8949.950195 | -132.492996 | 83.531197 | 0 |
| Walk | -8927.527344 | -132.492996 | 81.981163 | Not supplied |
| Assisted turn | -8947.380859 | -138.532501 | 83.637680 | 1.866109 |
| Final | -8915.987305 | -120.113731 | 82.059952 | 4.626014 |

## Cleanup and protocol observations

The supplied report records normal daemon cleanup via console `CTRL_BREAK`
handlers: both daemons exited zero, the world reported saved/stopped, and the
listeners were gone. All three SQLite files passed `integrity_check`. This is
reported shutdown/integrity evidence; it does not qualify world restart/relog.

`realmlist.wtf` already pointed to `127.0.0.1`. A byte backup was taken, no edit or
restoration was needed, and the final hash matched. The report states no source,
protocol or client-asset modifications, no observed warning/error/critical log
entry and no new client crash.

Debug logs reported unhandled zero-byte `CMSG_BATTLEFIELD_STATUS` and
`CMSG_MEETINGSTONE_INFO`. These are separate protocol triage observations; the
report does not associate them with a crash or disconnect. No response behavior
is inferred from this client run.

## Supplied evidence inventory

Only these filenames were supplied to this lane; their contents were not opened:

- `01-client-build.png`
- `02-realm-list.png`
- `03-create-human-warrior.png`
- `04-character-list.png`
- `05-world-entry.png`
- `06-world-stable-60s.png`
- `07-jump.png`
- `08-movement-endpoint.png`
- `provenance.json`
- `supervisor.jsonl`
- `ui-observations.jsonl`
- `database-observations.jsonl`

## Remaining handoff

Continuation belongs only to a future file-access chat selected by the user;
this report does not resume input, start a daemon or switch the acceptance target.
The smallest remaining baseline procedure keeps the exact **f8ae6e8** pin:

1. Confirm the chosen disposable setup and the exact tested checkout, log in,
   move to a recorded endpoint, then complete the normal 20-second logout.
2. Fully authenticate and log in again; compare the restored identity and saved
   position with the recorded endpoint and durable rows.
3. Stop the world normally, restart the same revision/configuration, and relog;
   require the same saved state and clean daemon logs.

Two-client visibility is a separate extension. Ordinary quest UI uses the
separately qualified greeting/fixture revision only when the user selects that
follow-up; it must not be attributed to this empty-content baseline run.
