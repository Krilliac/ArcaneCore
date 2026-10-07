# ArcaneCore documentation

Start with the [project README](../README.md) for what ArcaneCore is and how to build it, and with the
[charter](../ARCANECORE_CHARTER.md) for the rules every change follows (retail 1.12.1 fidelity, no invented API surface,
unfinished parts documented as limits).

## For operators

| Page | What it is |
|---|---|
| [Installation and first run](guide/installation.md) | Build, databases, realm list, first account, content import, `check-config`, starting both daemons, connecting a client, the development runner. |
| [Operating a realm](guide/operations.md) | Start, stop and restart, backups and upgrades, bans, monitoring, and the release caveats (which defaults are not retail, what is not delivered). |
| [Configuration reference](reference/configuration.md) | Every configuration key with its type, default, `.reload config` behaviour and meaning. **Generated from the code**; a test fails when the page is stale. |
| [GM command reference](reference/gm-commands.md) | Every chat command with the account level it needs and which stored account reaches it. **Generated.** |
| [Exit codes](reference/exit-codes.md) | The process exit codes of the daemons and the content importer (supervisors must not restart on 78). **Generated.** |
| [Database schema reference](reference/schema.md) | Every schema version of the auth, characters and world databases, which module owns it, and which modules clean up on character delete. **Generated.** |
| [Logging](ops/logging.md) | The `Logging:ArcaneCore` provider: colour or plain console, rolling text file, JSON lines, what reloads live, and the fail-closed checks. |
| [Database upgrade tooling](ops/database-upgrade.md) | The `arcane-db` runbook: status, plan, upgrade, backups, exit codes and the `Database:Upgrade` policy. |
| [Invariants and crash handling](ops/invariants.md) | The `Diagnostics` section: invariant policy and counters, what ends the process on an unhandled exception (exit 70 or abort), the crash report, and every invariant the code asserts. |
| [Resilience](ops/resilience.md) | Circuit breakers, retries, bulkheads and timeouts: what the daemons do when a database is slow or gone, the `Resilience` options, exit code 6, and how a write queue adopts the primitives. |
| [Runtime health watchdogs](ops/watchdog.md) | The `Ops:Watchdog` monitors: world-tick overruns and hangs, memory pressure, thread-pool starvation, the systemd/file heartbeat and the counters registry. |
| [Security hardening](security/hardening.md) | The opt-in hardening switches and their defaults, and what is not delivered. |
| [Network and packet protections](ops/netguard.md) | The `Net:Protection` limits (per-address caps, rates and failure budgets, frame deadlines), the bounds-checked packet readers and the fuzz harness. |
| [Live bans](security/live-bans.md) | Ban tables, IP bans and the live enforcement options. |
| [Operations and performance](areas/ops-perf.md) | `check-config`, exit codes, the performance log and shutdown commands. |
| [Live reload](areas/hot-reload.md) | The `.reload` command tree and which configuration is applied live. |
| [Code hot reload](areas/code-hot-reload.md) | The development runner and the `.hotcode` / `.hotmodule` commands. |

## Acceptance procedures (real client or mock client)

[M1](M1_ACCEPTANCE.md), [M2](M2_ACCEPTANCE.md), [M3](M3_ACCEPTANCE.md), [M4](M4_ACCEPTANCE.md),
[M5](M5_ACCEPTANCE.md), [M6](M6_ACCEPTANCE.md), [M13a](M13A_ACCEPTANCE.md), [M13b](M13B_ACCEPTANCE.md),
[quest rewards](QUEST_REWARD_ACCEPTANCE.md) and the [mock client](MOCK_CLIENT_ACCEPTANCE.md). No area has a
recorded real-client run unless its acceptance page says so.

## For developers: how to contribute

[Contributing](guide/contributing.md): the charter rules in practice, build and CI commands, the seams a feature plugs into, schema module rules, the
hosted-CI database rule, the flake rule, repository hygiene, and how the generated documentation is regenerated.

## For developers: areas

Each area page has a Delivered section, its limits, and the references it follows. This list is maintained by hand.

| Area | Page |
|---|---|
| Spell system core | [spells](areas/spells.md) |
| Spell combat rules, crowd control and diminishing returns | [spell-rules](areas/spell-rules.md) |
| Mage, priest and warlock | [casters](areas/casters.md) |
| Shaman and paladin (totems) | [class-shaman-paladin](areas/class-shaman-paladin.md) |
| Rogue | [rogue](areas/rogue.md) |
| Druid | [druid](areas/druid.md) |
| Hunter | [hunter](areas/hunter.md) |
| Talents | [talents](areas/talents.md) |
| Skills and professions | [skills](areas/skills.md) |
| Player stats and combat formulas | [stats](areas/stats.md) |
| Rested experience | [rested-xp](areas/rested-xp.md) |
| Character rename | [character-rename](areas/character-rename.md) |
| Melee combat | [combat](areas/combat.md) |
| Duels | [duels](areas/duels.md) |
| Creatures and world spawns | [creatures](areas/creatures.md) |
| Creature AI and movement | [creature-ai](areas/creature-ai.md) |
| Grids, maps and terrain | [grid-terrain](areas/grid-terrain.md) |
| Collision and pathing | [collision-pathing](areas/collision-pathing.md) |
| Locomotion, falls and environment | [locomotion](areas/locomotion.md) |
| World state and exploration | [world-state](areas/world-state.md) |
| Instances and bosses | [instances](areas/instances.md) |
| Items and inventory | [items](areas/items.md) |
| Group loot and group experience | [group-loot-xp](areas/group-loot-xp.md) |
| Fishing and special loot | [fishing-special-loot](areas/fishing-special-loot.md) |
| Quest and NPC adapters | [quests-npc](areas/quests-npc.md) (NPC services: [npc-services](integration/npc-services.md)) |
| Social: friends, groups, guilds, channels | [social](areas/social.md) |
| Chat languages, gates and channels | [chat](areas/chat.md) |
| Character creation and deletion rules | [character-creation](areas/character-creation.md) |
| Content import | [content-import](areas/content-import.md) |
| Documentation tooling (generated pages, fact checks) | [docs-wiki](areas/docs-wiki.md) |

## For developers: planning and integration records

- [Roadmap](ROADMAP.md) and the [clustering design](CLUSTERING_DESIGN.md).
- The integration records in [integration/](integration/) (per-lane notes, release reviews, handoffs). Start from the
  [seam table](integration/seams.md) when adding a feature, and from the
  [latest handoff](integration/claude-handoff-20261003.md) for the state of the tree.
- The security review notes in [security/](security/): [net/auth findings](security/codex-net-auth.md),
  [game logic findings](security/codex-game-logic.md), [persistence and I/O findings](security/codex-findings.md).

## Keeping the generated pages current

`docs/reference/*.md` are produced by `tests/ArcaneCore.World.Tests/Docs`. When an option, command or schema step is added
the matching test fails and names the page; regenerate and review the diff with:

```
ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs
```

A new options class must also be reachable by the catalog (a `SectionName` constant, or a row in the docs exception table);
a bound section the catalog does not know fails the source-scan test with the file and key.
