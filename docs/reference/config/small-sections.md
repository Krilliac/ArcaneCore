# Small sections options

40 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Auras`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DamageProcCancelsAura` | bool | `false` | true \| false | World | start | Opt-in deviation: an aura that breaks on damage (AuraInterruptFlags DAMAGE) and procs on TAKEN_ANY_DAMAGE (Wyvern Sting's sleep) ends when its damage proc fires, as the spell's tooltip says ("Any damage will cancel the effect"). Default false, the vmangos behaviour: MOD_STUN has no proc handler (HandleNULLProc, UnitAuraProcHandler.cpp:51) and the proc spends only real charges (Unit.cpp:4330-4335), so an uncharged aura such as Wyvern Sting keeps going through the damage. |
| `HarmfulAurasExpireOffline` | bool | `false` | true \| false | World | start | Harmful auras keep counting down while their owner is offline (the cmangos rule this engine used before). Retail (vmangos Player::LoadAura, Player.cpp:15363-15372) subtracts the offline time only from spells with SPELL_ATTR_EX4_AURA_EXPIRES_OFFLINE (Deserter), so every other aura resumes with the time it had at logout. Default false. |
| `PeriodicCatchUp` | bool | `false` | true \| false | World | start | Deliver every missed periodic tick in one update (the engine's behaviour before the periodic timing slice). Retail (vmangos Aura::Update, SpellAuras.cpp:553-572) delivers at most one tick per update, so a lag spike drops ticks instead of bursting them. Default false. |
| `ProcEngineBreaksDamageAuras` | bool | `true` | true \| false | World | start | The damage break skips every aura whose spell has procFlags (vmangos `checkProcFlags`, Unit.cpp:735-745, 895-906): such auras end through the proc engine instead (their charges, the root, pacify-silence and fear break chances, docs/areas/procs.md). Default true, the vmangos rule, now that the proc engine exists. False removes procFlags auras on the damage break too (the engine's behaviour before procs existed). Checked against the build 5875 Spell.dbc: Polymorph, Sap, Gouge, Freezing Trap and druid Prowl carry no procFlags and break on the interrupt path either way; Wyvern Sting (TAKEN_ANY_DAMAGE) and the hunter pet's Prowl (melee flags, one charge) are the damage-cancel auras that do. |

## `AutoBroadcast`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `false` | true \| false | World | start | Off by default: ClassicDB has no autobroadcast table, so the messages come from this section. |
| `IntervalMs` | uint | `1800000` | a whole number, 0 to 4294967295 | World | start | vmangos default 1800000 (30 minutes). |
| `Messages` | List<string> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is text | World | start | The announcements (vmangos autobroadcast.string_id rows; sql/custom/autobroadcast_example.sql has the classic tips). |

## `ClientData`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DbcDirectory` | string | `(empty)` | text | World | start | A directory holding the client's DBFilesClient *.dbc files (build 5875, extracted by the developer; none ships with the server). Set, every DBC consumer whose own path key (for example `Combat:ShapeshiftFormDbcPath`) is unset or empty reads the file of that name in this directory (for example SpellShapeshiftForm.dbc); a key that is set still wins. Each file is checked at start against the vmangos layout (field count and record size) and logged on one line: loaded, missing or format mismatch. A missing or mismatched file is not handed to its consumer, which keeps its built-in table or stays off, with a warning. Empty (the default): only the per-file keys are read, as before. |
| `Strict` | bool | `false` | true \| false | World | start | Make a client data problem fatal: a missing or mismatched DBC under `DbcDirectory`, a configured per-file path whose file is missing or has another layout, or a `DbcDirectory` that does not exist refuses start-up (exit code 78, like any configuration error) instead of a warning. Default false. |

## `Conditions`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ActiveGameEvents` | uint[] | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is a whole number, 0 to 4294967295 | World | start | Game event ids reported active to CONDITION_ACTIVE_GAME_EVENT (cmangos Conditions.cpp:245-248) IN ADDITION to the events the game-event service says are running (`GameEventFeature`, docs/areas/game-events-weather.md). Default none: an operator override for a world that runs no event service or wants an event forced on for its conditions only. |
| `ActiveHolidays` | uint[] | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is a whole number, 0 to 4294967295 | World | start | Holiday ids reported active to CONDITION_ACTIVE_HOLIDAY (Conditions.cpp:318-321) in addition to the running events' holidays; default none, same override. |

## `Crafting`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | World | start | `false` leaves crafting (reagent check, tool check and the CREATE_ITEM effect) and profession-specialization gossip unregistered (read once by `CraftingFeature`); unset or unparsable keeps crafting enabled, the retail behaviour. |

## `Enchanting`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | World | start | Master switch (default true): false leaves the enchantment engine and the enchant spell effects unregistered. |
| `GmAllowTrades` | bool | `true` | true \| false | World | start | vmangos `GM.AllowTrades` (default true, World.cpp:680): false keeps a game master's enchant spells from landing (SpellEffects.cpp:3029). |
| `SpellItemEnchantmentDbcPath` | string | `(empty)` | text | World | start | Build-5875 SpellItemEnchantment.dbc (24 fields). Without it the engine is inactive and says so in the log: no enchantment can be applied or resolved (a configured file that is unreadable or has another layout refuses startup). |

## `Forms`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ResetFistAttackTimeOnFormLoss` | bool | `false` | true \| false | World | start | `true` gives a weapon-less hand the 2.0 s base attack time when a shapeshift form ends (read once by `FormStatFeature`); `false` keeps the retail 1.0 / 2.5 s of an unarmed hand. |

## `ItemRandomProperties`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DbcPath` | string | `(empty)` | text | World | start | The build-5875 ItemRandomProperties.dbc (16 fields), supplied by the developer and never downloaded by the daemon. Empty: no item gets a random property. A configured file that is unreadable or has another layout refuses startup. |
| `EnchantmentTemplateDumpPath` | string | `(empty)` | text | World | start | A MySQL world dump (vmangos or cmangos classic-db, plain or .gz) holding `item_enchantment_template`; only that table is read. Both this and `DbcPath` are needed for random properties. A configured dump that cannot be read refuses startup. |

## `ItemSets`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DbcPath` | string | `(empty)` | text | World | start | The build-5875 ItemSet.dbc (45 fields), supplied by the developer and never downloaded by the daemon. Empty: no item set bonuses (the ON_EQUIP item spells still work). A configured file that is unreadable or has another layout refuses startup. |

## `Names`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `NamesProfanityDbcPath` | string | `(empty)` | text | World | start | Build-5875 NamesProfanity.dbc: the patterns a character or pet name may not contain (vmangos ObjectMgr::IsValidCharacterName). Set together with `NamesReservedDbcPath` or not at all; unset leaves only the SQL reserved names. |
| `NamesReservedDbcPath` | string | `(empty)` | text | World | start | Build-5875 NamesReserved.dbc: the reserved-name patterns; required with `NamesProfanityDbcPath`. |

## `PageText`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DumpPath` | string | `(empty)` | text | World | start | A MySQL world dump (vmangos or cmangos classic-db, plain or .gz) holding the `page_text` table; only that table is read. Empty: no page text, so every page a client asks for is answered "Item page missing." (the vmangos answer for an unknown page). A configured file that cannot be read refuses startup. |

## `Ranged`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Ammo:Mode` | AmmoMode | `Retail` | Retail \| Infinite | World | start | How ranged ammunition is treated. `Retail` (default): a ranged attack needs compatible ammunition (or a thrown weapon) and consumes one per shot. `Infinite` is a developer switch: no ammunition is required or consumed. Values: `Retail`, `Infinite`. |
| `Range:Leeway` | RangeLeewayMode | `Retail` | Retail \| None | World | start | How the spell range leeway of moving casters is treated. `Retail` (default) keeps the movement leeway; `None` is a deviation with only the fixed player allowance. Values: `Retail`, `None`. |
| `Traps:Hostility` | TrapHostilityRule | `Faction` | Faction \| AttackTarget | World | start | How a trap decides a unit is hostile. `Faction` (default) is the retail faction reaction; `AttackTarget` also accepts neutral creatures (a deviation). Values: `Faction`, `AttackTarget`. |
| `Traps:RadiusSource` | TrapRadiusSource | `Vmangos` | Vmangos \| Template | World | start | Where a hunter trap takes its trigger radius from. `Vmangos` (default) is the retail value as vmangos ships it; `Template` always uses the template radius (a deviation). Values: `Vmangos`, `Template`. |

## `Realm:Console`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | Realm | start | Accept typed console commands (help, info, realmlist, online, account ..., quit) when standard input is an interactive console. Never starts a reader when input is redirected (services, tests, deploy scripts). Default on. |

## `Realm:Liveness`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `HeartbeatStaleSeconds` | int | `60` | a whole number, -2147483648 to 2147483647 | Realm | start | Seconds without a heartbeat after which a world that claims to be running is shown offline. Default 60. Must be at least three times `World:Realm:HeartbeatIntervalSeconds` (a missed beat or two never flips the realm) and above five times `Ops:Watchdog:TickMonitor:HangMs` in seconds (a slow tick is a hang for the watchdog long before it is an outage here). 0 turns the heartbeat check off (the state and the lock level still apply). |
| `RestartingAs` | RestartingRealmDisplay | `Invalid` | Invalid \| Offline | Realm | start | What a realm shows while its world counts down to a restart and still serves: `Invalid` (red name, still selectable, the retail-emulator convention, unverified in the 1.12.1 client binary) or `Offline` (grey, not selectable). Default Invalid. Values: `Invalid`, `Offline`. |

## `Realms`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Seed` | List<RealmSeedEntry> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each entry is a group of keys | Realm | start | Realms inserted into an empty realm list on first start. Each entry has `Name`, `Address` (`ip:port` of the world server), `Type`, `Flags`, `Population` and `Category`. |

## `ServerMail`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | World | start | Send the mail_server_template letters at login (default on; with empty tables nothing is sent). |

## `SpecialLoot`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Fishing:FailGain` | bool | `false` | true \| false | World | start | `SkillFail.Gain.Fishing` (default false): a failed catch still raises the fishing skill. |
| `Fishing:FailLoot` | bool | `false` | true \| false | World | start | `SkillFail.Loot.Fishing` (default false): a failed catch still opens the junk loot (fishing_loot_template entry 0). Retail 1.12 shows "fish escaped" instead. |
| `Fishing:FailPossibleFishingPool` | bool | `true` | true \| false | World | start | `SkillFail.Possible.FishingPool` (default true). Counter-intuitively named: only when this is FALSE does vmangos search for a fishing hole after a failed roll and turn it into a success (GameObject.cpp:1672-1680); when true (default) a hole is only looked up after a successful roll. |
| `Items:ConsumeWholeStack` | bool | `true` | true \| false | World | start | A container item whose loot was taken is destroyed completely (default true: vmangos `DestroyItem` destroys the whole stack, LootHandler.cpp:556-563). With false only one of a stack is consumed. Three classic-db lootable items stack (Bloated Mackerel 6644, Bloated Albacore 6646, Bundle of Reports 16783); which behaviour retail had is not verifiable from the references. |

## `Startup`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Strict` | bool | `false` | true \| false | Both, Gateway | start | `true` makes a configuration warning fail start-up like an error (exit code 78); read by `ConfigReport.Run` in the world, realm and gateway daemons. |

## `Stats`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `RequireImportedData` | bool | `false` | true \| false | World | start | Refuse to log a player in unless the player base data (class health/mana, race/class stats, XP per level, crit and dodge per agility) is imported and complete, as vmangos refuses to start without it (ObjectMgr.cpp:4876-4882, 4988-4994, 5011-5029, 5117-5129). The default is false: a host that has not imported the data yet still runs, the agility terms of crit and dodge are left out and the level stats come from `Progression:LevelStatsPath` or are absent. This is the one deliberate difference from retail behaviour of the stat feature; set it to true on a host that has imported the data. |

## `Totems`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | World | start | false leaves the totem effects unregistered, so they report "not implemented" (fail closed). |
| `OwnerLeash` | bool | `true` | true \| false | World | start | vmangos Totem::Update unsummons a totem whose owner left its visibility distance (Objects/Totem.cpp:66-76). false (developer only) lets it persist. |
| `PlacementDistance` | float | `2` | a number, '.' is the decimal point | World | start | Base distance from the caster at which a totem is placed (vmangos Spell::EffectSummonTotem builds `CreatureCreatePos(caster, orientation, 2.0f, angle)`, SpellEffects.cpp:4952-4957). The caster's and the totem's bounding radii are added to it, as GetClosePoint does (Object.cpp:2728-2729, 2748). |
