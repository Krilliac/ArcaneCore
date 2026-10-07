# Server gameplay continuation, 2026-10-04

Continues the local [wave-3 review fixes](wave3-followups-20261004.md) in
`/workspace/arcanecore-work`, branch `codex/wave3-escrow-loot-cleanup`, based on
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. The user requested continued server
development and agent fanout. Changes remain local and uncommitted.

## Gameplay and data paths

Active totems use the existing totem map updater and spell system to select a
victim and cast their configured spell. Passive totems keep their existing
summon-time aura behavior. The ordinary spell pipeline controls cast time,
costs, cooldowns and effects; unsummoning stops an in-flight totem cast. See
[shaman and paladin behavior](../areas/class-shaman-paladin.md).

`SkillAuras` supplies the discovered handlers for `MOD_SKILL` (30) and
`MOD_SKILL_TALENT` (98). Temporary and permanent bonuses occupy their existing
signed halves of the skill bonus word. Contributions wait for an unknown skill
to be learned, unapply before its fields are cleared, and reapply when it is
learned again. Trained skill values and maxima remain separate from bonuses.
See [skills](../areas/skills.md).

`StatsFeature` now gives the stat system `PlayerSkillStatSource`, which reads
effective weapon and defense skill values. Skill changes refresh crit and
avoidance fields; hosts without retail skill content keep the existing level
defaults. World session tests exercise initial passive application, live field
updates and saved temporary aura restoration through logout and login, checking
that bonuses never become stored trained values. See [stats](../areas/stats.md).

The content importer fills the existing `totem_spell` table from supported
totem source mappings, exposing the results through plan, import, dry-run and
verify. This connects imported content to `TotemFeature` at startup without a
schema change. vmangos direct `totem_spell_id` fields take precedence. cmangos
compatibility uses explicit lists first, selecting their lowest-position
positive spell, or the first nonzero legacy default-set spell. `--replace` is
deterministic; collisions without replacement roll back earlier importer writes.
Source assets remain operator supplied. See [content import](../areas/content-import.md)
for mapping and DBC validation limits.

## Ground truth and acceptance

Retail behavior is checked against vmangos core commit
`0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`: `AI/TotemAI.cpp:66-125`,
`Objects/Totem.cpp:220-222`, `Spells/SpellAuras.cpp:2807-2831`,
`Objects/Player.cpp:5549-5560,5627-5645`, and `StatSystem.cpp:514-640`.
The source references guide independent C# implementations; upstream code and
world dump assets are not vendored.
cmangos compatibility mapping is checked against core commit
`8ec338a1704e7dcb1c0213eb7ed58f9231ade40f`, `Entities/Totem.cpp:171-176`,
`Entities/Creature.cpp:609-612`, and `Globals/ObjectMgr.cpp:1032-1110,9757-9816`.

Tests use synthetic spell, creature and skill data. SQLite validates importer
persistence. Actual build-5875 client acceptance, MariaDB/PostgreSQL fixtures,
and proprietary DBC/world-dump verification remain pending.

## Validation

Focused checks passed: 184 importer/data cases (one real-dump skip), all 41 totem
cases (17 new active cases), and nine world skill/stat cases. Before implementation,
four CLI regressions showed the missing totem import path and 12 of the initial
13 active-totem cases failed. Review corrected stealth selection, default LOS
selection behavior, and owner-death cast completion on delayed ticks.

The combined Release solution build passed with **zero warnings and errors**.
The build-5875 mock client self-test passed **59/59 checks**, receiving 148 frames.
The full solution passed **14,050 tests**, with **eight fixture-dependent skips**
and no failures, including the production-world active-totem packet/damage test.
This adds 46 passing cases to the preceding review-fix baseline.

| Test project | Passed | Skipped |
|---|---:|---:|
| Cryptography | 8,017 | 0 |
| Data | 796 | 6 |
| Game | 3,689 | 1 |
| MockClient | 195 | 0 |
| Realm | 37 | 0 |
| World | 1,316 | 1 |

Commands, using .NET SDK 10.0.301 from `global.json`:

```sh
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1 --verbosity minimal
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

All commands exited successfully. Logs are retained outside the worktree in
`/workspace/scratch/server-gameplay-release-build.log`,
`server-gameplay-release-tests.log` and `server-gameplay-mock-self-test.json`.
The skipped tests require real DBC/dump data or the configured provider race
fixture. Provider matrix cases also require configured external services.
