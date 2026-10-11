## Spell coverage

Computed 2026-10-11 16:55:41Z from the spell table and the live handler registries (27309 class trainer rows). Spell coverage: 1147 of 1178 spells bots can cast are fully handled (762 of 762 rotation and escape spells); 35 gaps in 31 spells (0 rotation spells).

Bots can cast the class spell books at trainer level: the class spells trainers teach, the start spells and the talent spells; the ones a class rotation or an escape names are the rotation spells. A gap is an effect with no handler, an aura type nothing implements, a DUMMY or OVERRIDE_CLASS_SCRIPTS aura nothing known reads (advisory: a rule may read it by spell id; never published), or a DUMMY / SCRIPT_EFFECT that is a spell's only behaviour with no script. Each gap is a `spell unsupported` finding; one is published only once a bot really casts it.

| Missing | Spells | Rotation spells |
|---|---|---|
| aura Dummy read by nothing | 18 | 0 |
| aura ModDetectRange not implemented | 6 | 0 |
| SCRIPT_EFFECT with no script | 5 | 0 |
| aura AurasVisible not implemented | 1 | 0 |
| OpenLock not implemented | 1 | 0 |

| Spell | Name | Classes | Rotation | Effect | Gap |
|---|---|---|---|---|---|
| 453 | Mind Soothe (Rank 1) | Priest | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 605 | Mind Control (Rank 1) | Priest | no | #1 ApplyAura | aura Dummy read by nothing |
| 1002 | Eyes of the Beast | Hunter | no | #1 ApplyAura | aura Dummy read by nothing |
| 1842 | Disarm Trap | Rogue | no | #0 OpenLock | OpenLock not implemented |
| 2096 | Mind Vision (Rank 1) | Priest | no | #1 ApplyAura | aura Dummy read by nothing |
| 2855 | Detect Magic | Mage | no | #0 ApplyAura | aura AurasVisible not implemented |
| 2908 | Soothe Animal (Rank 1) | Druid | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 5143 | Arcane Missiles (Rank 1) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 5144 | Arcane Missiles (Rank 2) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 5145 | Arcane Missiles (Rank 3) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 5699 | Create Healthstone | Warlock | no | #0 ScriptEffect | SCRIPT_EFFECT with no script |
| 6201 | Create Healthstone (Minor) | Warlock | no | #0 ScriptEffect | SCRIPT_EFFECT with no script |
| 6202 | Create Healthstone (Lesser) | Warlock | no | #0 ScriptEffect | SCRIPT_EFFECT with no script |
| 6495 | Sentry Totem | Shaman | no | #1 ApplyAura | aura Dummy read by nothing |
| 8192 | Mind Soothe (Rank 2) | Priest | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 8416 | Arcane Missiles (Rank 4) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 8417 | Arcane Missiles (Rank 5) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 8955 | Soothe Animal (Rank 2) | Druid | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 9901 | Soothe Animal (Rank 3) | Druid | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 10211 | Arcane Missiles (Rank 6) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 10212 | Arcane Missiles (Rank 7) | Mage | no | #1 ApplyAura | aura Dummy read by nothing |
| 10909 | Mind Vision (Rank 2) | Priest | no | #1 ApplyAura | aura Dummy read by nothing |
| 10911 | Mind Control (Rank 2) | Priest | no | #1 ApplyAura | aura Dummy read by nothing |
| 10912 | Mind Control (Rank 3) | Priest | no | #1 ApplyAura | aura Dummy read by nothing |
| 10953 | Mind Soothe (Rank 3) | Priest | no | #0 ApplyAura | aura ModDetectRange not implemented |
| 11729 | Create Healthstone (Greater) | Warlock | no | #0 ScriptEffect | SCRIPT_EFFECT with no script |
| 11730 | Create Healthstone (Major) | Warlock | no | #0 ScriptEffect | SCRIPT_EFFECT with no script |
| 19977 | Blessing of Light (Rank 1) | Paladin | no | #0 ApplyAura | aura Dummy read by nothing |
| 19977 | Blessing of Light (Rank 1) | Paladin | no | #1 ApplyAura | aura Dummy read by nothing |
| 19978 | Blessing of Light (Rank 2) | Paladin | no | #0 ApplyAura | aura Dummy read by nothing |
| 19978 | Blessing of Light (Rank 2) | Paladin | no | #1 ApplyAura | aura Dummy read by nothing |
| 19979 | Blessing of Light (Rank 3) | Paladin | no | #0 ApplyAura | aura Dummy read by nothing |
| 19979 | Blessing of Light (Rank 3) | Paladin | no | #1 ApplyAura | aura Dummy read by nothing |
| 25890 | Greater Blessing of Light (Rank 1) | Paladin | no | #0 ApplyAura | aura Dummy read by nothing |
| 25890 | Greater Blessing of Light (Rank 1) | Paladin | no | #1 ApplyAura | aura Dummy read by nothing |

