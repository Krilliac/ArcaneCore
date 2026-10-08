# Aura engine

Status: wave 4, lane `aura-engine-completeness`. Reference: D:\refs\vmangos (primary). Everything defaults to retail 1.12.1;
every deliberate difference sits behind the `Auras` configuration section (class `AuraOptions`, bound by
`SpellRulesAuraEngineFeature`).

## Delivered

| Slice | What it does | Where | vmangos |
|---|---|---|---|
| Polarity and cancel | Per-effect `IsPositiveEffect`, `IsPositiveSpell`, `IsPositiveTarget` ported exactly; a holder is positive only when all of its AURA effects are (`SpellAuraHolder.IsPositive`, recomputed in `AddAuraHolder` once every effect exists, with the spell store for the periodic-trigger check). Stealth (speed effect), Prowl, Tree Form and Slice and Dice now take buff slots and carry the CANCELABLE flag. `CMSG_CANCEL_AURA` gained the NO_AURA_ICON (needs `ActiveIconId`), possessed and foreign-area-aura checks and removes with `AuraRemoveMode.Cancel`. | `SpellInfo.Polarity.cs`, `SpellAuraHolder.cs`, `SpellSystem.CancelAura.cs` | `SpellEntry.cpp:795-1018`, `SpellEntry.h:218-240`, `SpellAuras.cpp:276, 7475-7482`, `SpellHandler.cpp:333-405` |
| Lifecycle | `AuraRemoveMode` on every holder (`SpellAuraHolder.RemoveMode`, set before the remove handlers and `HolderRemoved`); produced: Stack (replacement), Cancel, Dispel, Death, Expire. Same-caster recast refreshes the SAME holder in place (slot, duration, periodic timer, changed amounts re-applied; no `HolderRemoved`/`HolderAdded`, so diminishing returns do not see the aura end). One `ModStackAmount`/`SetStackAmount` shared by the cast path and dispel stack removal, using the one-stack `SpellAura.UnitAmount` and re-applying through the handler (dispel used to rewrite the amount without calling the handler). | `SpellSystem.Auras.cs`, `SpellSystem.Dispel.cs`, `AuraRemoveMode.cs` | `Unit.cpp:3138-3154, 3817-3873`, `SpellAuras.cpp:311-394, 6942-6999` |
| Food and drink | The player regen tick reads `ModRegen` (food: amount * 2000 / period, period 5000 ms when the spell has no amplitude), `ModPowerRegen` (mp5 for mana), `ModHealthRegenPercent`, `ModHealthRegenInCombat`, `ModRegenDuringCombat`, `ModManaRegenInterrupt` (percentage of the spirit regen inside the five-second rule) and `ModPowerRegenPercent` on the spirit part. `STANDING_CANCELS` auras sit the target down on apply and are removed when the target stands up. | `Combat/Power/RegenModifiers.cs` (the warlock-mage lane's pure regen functions; this lane's duplicate RegenAuraRules was removed at integration), `MapCombat.Regen.cs`, `IPowerAuraSource.GetAuras` | `Player.cpp:2269-2400`, `StatSystem.cpp:642-660`, `SpellAuras.cpp:4795-4866, 6803-6806`, `Unit.cpp:9302-9310` |
| Periodic timing | At most one tick per update with the vmangos drift clamp; `ObsModMana` without an amplitude ticks every 1000 ms; the totem passives, Immolation Trap Effect, Mark of Frost/Nature and the Stoneclaw rule tick at the first update; a channel pushback re-aligns the tick timer to the remaining duration. | `Auras/Periodic/PeriodicTiming.cs` | `SpellAuras.cpp:528-572, 8053-8110` |
| Damage break | The damage break goes through `RemoveAurasWithInterruptFlags(DAMAGE_CANCELS, damaging spell, checkProcFlags)`: the aura of the spell that dealt the damage survives (for a spell a proc is casting, the spell whose hit made it proc), and auras whose spell has procFlags are left to the proc engine (`Auras:ProcEngineBreaksDamageAuras`, default on: their charges, break chances and `Auras:DamageProcCancelsAura`, docs/areas/procs.md). The build 5875 Spell.dbc gives Polymorph, Sap, Gouge, Freezing Trap and druid Prowl no procFlags, so they break here; Wyvern Sting has no charges and keeps its sleep through the damage, as in vmangos (`Auras:DamageProcCancelsAura` opts into ending it on its damage proc). The id exemption that spared Wyvern Sting and Prowl before the proc engine existed is gone. | `SpellSystem.Combat.cs`, `AuraInterrupt/SpellSystem.AuraInterrupt.cs`, `Procs/SpellSystem.Procs.cs` | `Unit.cpp:3735-3751, 735-745, 895-906` |
| Persistence | Offline time is subtracted only for `ATTR_EX4_AURA_EXPIRES_OFFLINE` spells; auras cancelled by leaving or entering the world and bind sight, possess, charm, far sight and AoE charm are never saved; charges are zeroed for a spell without `procCharges`. | `SpellSystem.Persistence.cs`, `SpellAuraHolder.IsNeverSaved` | `Player.cpp:15356-15425, 16618-16675`, `SpellEntry.h:1092` |
| Support matrix | One row for each of the 193 aura types (see below), with a totality test and a test against the live registrations of a composed world host. | `Auras/Support/*` | `SpellAuras.cpp:63-258` |

## Configuration (`Auras` section, all defaults are retail)

| Key | Default | Effect |
|---|---|---|
| `Auras:PeriodicCatchUp` | `false` | `true` delivers every missed periodic tick in one update (the engine's earlier behaviour). |
| `Auras:HarmfulAurasExpireOffline` | `false` | `true` restores the cmangos rule that harmful auras keep counting down while the player is offline. |
| `Auras:ProcEngineBreaksDamageAuras` | `true` | `false` lets the damage break remove procFlags auras too (the engine before the proc engine). |
| `Auras:DamageProcCancelsAura` | `false` | `true` ends Wyvern Sting's sleep on its damage proc, as its tooltip says; the default keeps it through the damage, as vmangos does (docs/areas/procs.md). |

## Deviations from vmangos (documented, deliberate)

- An in-place refresh re-applies an aura amount only when it changed (vmangos un-applies and re-applies unconditionally). Handlers with
  side effects therefore do not fire twice on a plain recast; the 1.7 stat-refresh rule (no health/mana reset) belongs to the stat lane.
- A periodic aura without an amplitude is not periodic here (vmangos would tick an amplitude-less DoT or HoT at every update), except
  `ObsModMana` (1000 ms). No retail data relies on it and a data slip would hit a target every 50 ms.
- Aura polarity ignores the dispel caster/victim friendliness in the `IsPositive` property (`IsPositiveSpell(lookup, friendlyDispel)` takes both).
- `HealthIncreaseRate` (`Rate.Health`) is not an option in this engine, so food/health regen use rate 1.

## Limits (not delivered, with the owner)

- Stacking between different casters, rank/spell-specific/group replacement (`RemoveNoStackAurasDueToAuraHolder`), the exclusive stat aura
  model, `SPELL_FAILED_AURA_BOUNCED`, single-target auras: need spell chain/spell group/custom flag data that no reference tree contains
  (`spell_group` rows and `spell_template.custom` are vmangos world-DB data); the simplified rule in `docs/areas/spells.md` stands. `SpellInfo.CustomFlags`
  (POSITIVE/NEGATIVE bits) is the data seam and is empty until a data source fills it.
- Visible-slot overflow eviction (16 debuffs), `IsNeedVisibleSlot` special cases, `UpdateAuraForGroup` party aura slots, area aura rank selection and
  pet/owner areas, channel aura rules (per-second cost, range), heartbeat resist of crowd control,
  interrupt sources (`Moving`, `Turning`, `Interacting`, ... have no trigger), holder permanence rules for passive-with-visual spells.
- Polymorph health regeneration (the transform aura exists now, `transform-and-charge.md`), spell modifier auras, percent stat auras,
  skill auras, creature spawn addon auras: owned by other wave-4 lanes or unscheduled; each row in the matrix names the owner.

## Support matrix

Levels: `Handler` = a handler is registered with the spell system; `Referenced` = no handler, but code outside the aura files names the type (this does
not mean every vmangos consumer exists); `Unsupported` = nothing acts on it. The column "consumers" lists up to three source files that mention the type.
Counts: Handler 139, Referenced 24, Unsupported 29, NotAnAura 1 (193 types). The table is `AuraSupportBaseline.cs`; `AuraSupportWorldTests` fails when a row
disagrees with the live registrations of a composed world host.

| Value | Aura type | Level | vmangos handler | Consumers | Owner of the gap |
|---|---|---|---|---|---|
| 0 | None | NotAnAura | `HandleNULL` (SpellAuras.cpp:65) | ImmunityRules.cs |  |
| 1 | BindSight | Unsupported | `HandleBindSight` (SpellAuras.cpp:66) |  |  |
| 2 | ModPossess | Handler | `HandleModPossess` (SpellAuras.cpp:67) | CharmService.Possess.cs, CharmCastCheck.cs, SpellSystem.Dispel.cs |  |
| 3 | PeriodicDamage | Handler | `HandlePeriodicDamage` (SpellAuras.cpp:68) | SpellCoefficients.cs, SpellInfoRuleExtensions.cs, SpellPackets.cs |  |
| 4 | Dummy | Handler | `HandleAuraDummy` (SpellAuras.cpp:69) | SpellCoefficients.cs, SpellSystem.Auras.cs |  |
| 5 | ModConfuse | Handler | `HandleModConfuse` (SpellAuras.cpp:70) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 6 | ModCharm | Handler | `HandleModCharm` (SpellAuras.cpp:71) | CharmService.Charm.cs, CharmCastCheck.cs, SpellSystem.Dispel.cs |  |
| 7 | ModFear | Handler | `HandleModFear` (SpellAuras.cpp:72) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 8 | PeriodicHeal | Handler | `HandlePeriodicHeal` (SpellAuras.cpp:73) | SpellCoefficients.cs, SpellInfoRuleExtensions.cs, SpellPackets.cs |  |
| 9 | ModAttackspeed | Handler | `HandleModAttackSpeed` (SpellAuras.cpp:74) | AttackSpeedAuras.cs |  |
| 10 | ModThreat | Handler | `HandleModThreat` (SpellAuras.cpp:75) | SpellSystem.Auras.cs, SpellThreatModifiers.cs, SpellThreat.cs | threat-and-aggro |
| 11 | ModTaunt | Handler | `HandleModTaunt` (SpellAuras.cpp:76) | ThreatAuras.cs |  |
| 12 | ModStun | Handler | `HandleAuraModStun` (SpellAuras.cpp:77) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 13 | ModDamageDone | Referenced | `HandleModDamageDone` (SpellAuras.cpp:78) | SpellBonusModule.cs |  |
| 14 | ModDamageTaken | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:79) | SpellBinary.cs, SpellBonusModule.cs |  |
| 15 | DamageShield | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:80) | SpellSystem.Auras.cs, SpellSystem.CombatProcs.cs |  |
| 16 | ModStealth | Handler | `HandleModStealth` (SpellAuras.cpp:81) | GeneralCastChecks.cs, SpellSystem.AuraInterrupt.cs, StealthAuras.cs |  |
| 17 | ModStealthDetect | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:82) | StealthDetection.cs, StealthFeature.cs |  |
| 18 | ModInvisibility | Handler | `HandleInvisibility` (SpellAuras.cpp:83) | ../../Stealth/InvisibilityAuras.cs |  |
| 19 | ModInvisibilityDetection | Handler | `HandleInvisibilityDetect` (SpellAuras.cpp:84) | ../../Stealth/InvisibilityAuras.cs |  |
| 20 | ObsModHealth | Handler | `HandleAuraModTotalHealthPercentRegen` (SpellAuras.cpp:85) | SpellPackets.cs, SpellSystem.Auras.cs |  |
| 21 | ObsModMana | Handler | `HandleAuraModTotalManaPercentRegen` (SpellAuras.cpp:86) | PeriodicTiming.cs, SpellPackets.cs, SpellSystem.Auras.cs |  |
| 22 | ModResistance | Handler | `HandleAuraModResistance` (SpellAuras.cpp:87) | SpellBinary.cs, StatAuras.cs |  |
| 23 | PeriodicTriggerSpell | Handler | `HandlePeriodicTriggerSpell` (SpellAuras.cpp:88) | QuestRewardEffects.cs, SpellSystem.Auras.cs |  |
| 24 | PeriodicEnergize | Handler | `HandlePeriodicEnergize` (SpellAuras.cpp:89) | SpellPackets.cs, SpellSystem.Auras.cs |  |
| 25 | ModPacify | Handler | `HandleAuraModPacify` (SpellAuras.cpp:90) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 26 | ModRoot | Handler | `HandleAuraModRoot` (SpellAuras.cpp:91) | CcAuraHandlers.cs, SpellBinary.cs, SpellCoefficients.cs |  |
| 27 | ModSilence | Handler | `HandleAuraModSilence` (SpellAuras.cpp:92) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 28 | ReflectSpells | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:93) | SpellSystem.Auras.cs, SpellSystem.Reflect.cs |  |
| 29 | ModStat | Handler | `HandleAuraModStat` (SpellAuras.cpp:94) | StatAuras.cs |  |
| 30 | ModSkill | Handler | `HandleAuraModSkill` (SpellAuras.cpp:95) | SkillAuras.cs |  |
| 31 | ModIncreaseSpeed | Handler | `HandleAuraModIncreaseSpeed` (SpellAuras.cpp:96) | SpeedAuras.cs, UnitSpeed.cs |  |
| 32 | ModIncreaseMountedSpeed | Handler | `HandleAuraModIncreaseMountedSpeed` (SpellAuras.cpp:97) | SpeedAuras.cs, UnitSpeed.cs |  |
| 33 | ModDecreaseSpeed | Handler | `HandleAuraModDecreaseSpeed` (SpellAuras.cpp:98) | SpeedAuras.cs, SpellBinary.cs, SpellCoefficients.cs |  |
| 34 | ModIncreaseHealth | Handler | `HandleAuraModIncreaseHealth` (SpellAuras.cpp:99) | PercentStatAuras.cs |  |
| 35 | ModIncreaseEnergy | Handler | `HandleAuraModIncreaseEnergy` (SpellAuras.cpp:100) | PercentStatAuras.cs |  |
| 36 | ModShapeshift | Handler | `HandleAuraModShapeshift` (SpellAuras.cpp:101) | ShapeshiftService.cs | druid-forms |
| 37 | EffectImmunity | Handler | `HandleAuraModEffectImmunity` (SpellAuras.cpp:102) | ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 38 | StateImmunity | Handler | `HandleAuraModStateImmunity` (SpellAuras.cpp:103) | ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 39 | SchoolImmunity | Handler | `HandleAuraModSchoolImmunity` (SpellAuras.cpp:104) | CasterAuraGate.cs, ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 40 | DamageImmunity | Handler | `HandleAuraModDmgImmunity` (SpellAuras.cpp:105) | ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 41 | DispelImmunity | Handler | `HandleAuraModDispelImmunity` (SpellAuras.cpp:106) | CasterAuraGate.cs, ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 42 | ProcTriggerSpell | Handler | `HandleAuraProcTriggerSpell` (SpellAuras.cpp:107) | BuiltInProcHandlers.cs, SpellSystem.Auras.cs, SpellSystem.Procs.cs |  |
| 43 | ProcTriggerDamage | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:108) | BuiltInProcHandlers.cs, SpellSystem.Auras.cs |  |
| 44 | TrackCreatures | Handler | `HandleAuraTrackCreatures` (SpellAuras.cpp:109) | RangedHandlers.cs, SpellInfo.cs, TrackingAuras.cs |  |
| 45 | TrackResources | Handler | `HandleAuraTrackResources` (SpellAuras.cpp:110) | RangedHandlers.cs, SpellInfo.cs, TrackingAuras.cs |  |
| 46 | ModParrySkill | Unsupported | `HandleUnused` (SpellAuras.cpp:111) |  |  |
| 47 | ModParryPercent | Handler | `HandleAuraModParryPercent` (SpellAuras.cpp:112) | PercentStatAuras.cs |  |
| 48 | ModDodgeSkill | Unsupported | `HandleUnused` (SpellAuras.cpp:113) |  |  |
| 49 | ModDodgePercent | Handler | `HandleAuraModDodgePercent` (SpellAuras.cpp:114) | PercentStatAuras.cs |  |
| 50 | ModBlockSkill | Unsupported | `HandleUnused` (SpellAuras.cpp:115) |  |  |
| 51 | ModBlockPercent | Handler | `HandleAuraModBlockPercent` (SpellAuras.cpp:116) | PercentStatAuras.cs |  |
| 52 | ModCritPercent | Referenced | `HandleAuraModCritPercent` (SpellAuras.cpp:117) | SpellCombatRules.cs |  |
| 53 | PeriodicLeech | Handler | `HandlePeriodicLeech` (SpellAuras.cpp:118) | DrainAuras.cs, SpellCoefficients.cs, SpellInfoRuleExtensions.cs |  |
| 54 | ModHitChance | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:119) |  |  |
| 55 | ModSpellHitChance | Referenced | `HandleModSpellHitChance` (SpellAuras.cpp:120) | SpellCombatRules.cs, SpellSystem.Feign.cs |  |
| 56 | Transform | Handler | `HandleAuraTransform` (SpellAuras.cpp:121) | TransformAuras.cs, ShapeshiftService.cs, CombatOptions.cs |  |
| 57 | ModSpellCritChance | Referenced | `HandleModSpellCritChance` (SpellAuras.cpp:122) | SpellCombatRules.cs |  |
| 58 | ModIncreaseSwimSpeed | Handler | `HandleAuraModIncreaseSwimSpeed` (SpellAuras.cpp:123) | SpeedAuras.cs, UnitSpeed.cs |  |
| 59 | ModDamageDoneCreature | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:124) |  |  |
| 60 | ModPacifySilence | Handler | `HandleAuraModPacifyAndSilence` (SpellAuras.cpp:125) | CasterAuraGate.cs, CcAuraHandlers.cs, CcState.cs |  |
| 61 | ModScale | Handler | `HandleAuraModScale` (SpellAuras.cpp:126) | VisualAuras.cs |  |
| 62 | PeriodicHealthFunnel | Handler | `HandlePeriodicHealthFunnel` (SpellAuras.cpp:127) | SpellSystem.PowerBurn.cs, DrainAuras.cs, ProcFlagRules.cs |  |
| 63 | PeriodicManaFunnel | Unsupported | `HandleUnused` (SpellAuras.cpp:128) |  | obsolete in 1.12 (vmangos HandleUnused; only zzOLDMana Funnel 1941) |
| 64 | PeriodicManaLeech | Handler | `HandlePeriodicManaLeech` (SpellAuras.cpp:129) | CasterPeriodicPackets.cs, DrainAuras.cs |  |
| 65 | ModCastingSpeedNotStack | Unsupported | `HandleModCastingSpeed` (SpellAuras.cpp:130) |  | aura-transform (not scheduled) |
| 66 | FeignDeath | Handler | `HandleFeignDeath` (SpellAuras.cpp:131) | RangedHandlers.cs, SpellSystem.Feign.cs |  |
| 67 | ModDisarm | Handler | `HandleAuraModDisarm` (SpellAuras.cpp:132) | CcAuraHandlers.cs, CcState.cs, SpellBinary.cs |  |
| 68 | ModStalked | Handler | `HandleAuraModStalked` (SpellAuras.cpp:133) | RangedHandlers.cs, SpellSystem.Stealth.cs, SpellSystem.Tracking.cs |  |
| 69 | SchoolAbsorb | Referenced | `HandleSchoolAbsorb` (SpellAuras.cpp:134) | SpellSystem.Mitigation.cs |  |
| 70 | ExtraAttacks | Unsupported | `HandleUnused` (SpellAuras.cpp:135) |  |  |
| 71 | ModSpellCritChanceSchool | Referenced | `HandleModSpellCritChanceSchool` (SpellAuras.cpp:136) | SpellCombatRules.cs |  |
| 72 | ModPowerCostSchoolPct | Handler | `HandleModPowerCostPCT` (SpellAuras.cpp:137) | PowerCostAuras.cs |  |
| 73 | ModPowerCostSchool | Handler | `HandleModPowerCost` (SpellAuras.cpp:138) | PowerCostAuras.cs |  |
| 74 | ReflectSpellsSchool | Handler | `HandleReflectSpellsSchool` (SpellAuras.cpp:139) | BuiltInProcHandlers.cs, SpellSystem.Auras.cs, SpellSystem.Reflect.cs |  |
| 75 | ModLanguage | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:140) | ChatLanguageQueries.cs |  |
| 76 | FarSight | Unsupported | `HandleFarSight` (SpellAuras.cpp:141) |  |  |
| 77 | MechanicImmunity | Handler | `HandleModMechanicImmunity` (SpellAuras.cpp:142) | CasterAuraGate.cs, ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 78 | Mounted | Handler | `HandleAuraMounted` (SpellAuras.cpp:143) | MountAura.cs, MountService.cs |  |
| 79 | ModDamagePercentDone | Referenced | `HandleModDamagePercentDone` (SpellAuras.cpp:144) | SpellBonusModule.cs |  |
| 80 | ModPercentStat | Handler | `HandleModPercentStat` (SpellAuras.cpp:145) | PercentStatAuras.cs |  |
| 81 | SplitDamagePct | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:146) | SpellSystem.Mitigation.cs |  |
| 82 | WaterBreathing | Handler | `HandleWaterBreathing` (SpellAuras.cpp:147) | WaterBreathingAuras.cs |  |
| 83 | ModBaseResistance | Handler | `HandleModBaseResistance` (SpellAuras.cpp:148) | PercentStatAuras.cs |  |
| 84 | ModRegen | Handler | `HandleModRegen` (SpellAuras.cpp:149) | FoodDrinkAuras.cs, RegenModifiers.cs |  |
| 85 | ModPowerRegen | Handler | `HandleModPowerRegen` (SpellAuras.cpp:150) | SpellSystem.PowerRegenAuras.cs, RegenModifiers.cs |  |
| 86 | ChannelDeathItem | Handler | `HandleChannelDeathItem` (SpellAuras.cpp:151) | ../Warlock/ChannelDeathItemAura.cs |  |
| 87 | ModDamagePercentTaken | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:152) | SpellBonusModule.cs |  |
| 88 | ModHealthRegenPercent | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:153) | HealthRegenPercentAuras.cs, MapCombat.Regen.cs |  |
| 89 | PeriodicDamagePercent | Handler | `HandlePeriodicDamagePCT` (SpellAuras.cpp:154) | SpellInfoRuleExtensions.cs, SpellPackets.cs, SpellSystem.Auras.cs |  |
| 90 | ModResistChance | Unsupported | `HandleUnused` (SpellAuras.cpp:155) |  |  |
| 91 | ModDetectRange | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:156) |  |  |
| 92 | PreventsFleeing | Referenced | `HandlePreventFleeing` (SpellAuras.cpp:157) | CcAuraHandlers.cs, CcState.cs |  |
| 93 | ModUnattackable | Unsupported | `HandleModUnattackable` (SpellAuras.cpp:158) |  |  |
| 94 | InterruptRegen | Referenced | `HandleInterruptRegen` (SpellAuras.cpp:159) | MapCombat.Regen.cs |  |
| 95 | Ghost | Handler | `HandleAuraGhost` (SpellAuras.cpp:160) | GhostAuras.cs |  |
| 96 | SpellMagnet | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:161) | ../Magnet/SpellMagnetAuras.cs |  |
| 97 | ManaShield | Referenced | `HandleManaShield` (SpellAuras.cpp:162) | SpellSystem.Mitigation.cs |  |
| 98 | ModSkillTalent | Handler | `HandleAuraModSkill` (SpellAuras.cpp:163) | SkillAuras.cs |  |
| 99 | ModAttackPower | Handler | `HandleAuraModAttackPower` (SpellAuras.cpp:164) | StatAuras.cs |  |
| 100 | AurasVisible | Unsupported | `HandleAurasVisible` (SpellAuras.cpp:165) |  |  |
| 101 | ModResistancePct | Handler | `HandleModResistancePercent` (SpellAuras.cpp:166) | PercentStatAuras.cs |  |
| 102 | ModMeleeAttackPowerVersus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:167) |  |  |
| 103 | ModTotalThreat | Handler | `HandleAuraModTotalThreat` (SpellAuras.cpp:168) | ThreatAuras.cs |  |
| 104 | WaterWalk | Handler | `HandleAuraWaterWalk` (SpellAuras.cpp:169) | MovementFlagAuras.cs |  |
| 105 | FeatherFall | Handler | `HandleAuraFeatherFall` (SpellAuras.cpp:170) | FallObserver.cs, MovementFlagAuras.cs |  |
| 106 | Hover | Handler | `HandleAuraHover` (SpellAuras.cpp:171) | FallObserver.cs, MovementFlagAuras.cs |  |
| 107 | AddFlatModifier | Handler | `HandleAddModifier` (SpellAuras.cpp:172) | ../Mods/SpellModModule.cs |  |
| 108 | AddPctModifier | Handler | `HandleAddModifier` (SpellAuras.cpp:173) | ../Mods/SpellModModule.cs |  |
| 109 | AddTargetTrigger | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:174) | BuiltInProcHandlers.cs, SpellSystem.Auras.cs, SpellSystem.SpellProcs.cs |  |
| 110 | ModPowerRegenPercent | Referenced | `HandleModPowerRegenPCT` (SpellAuras.cpp:175) | CombatOptions.cs |  |
| 111 | AddCasterHitTrigger | Unsupported | `HandleUnused` (SpellAuras.cpp:176) |  |  |
| 112 | OverrideClassScripts | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:177) | ShapeshiftService.cs | druid-forms |
| 113 | ModRangedDamageTaken | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:178) |  |  |
| 114 | ModRangedDamageTakenPct | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:179) |  |  |
| 115 | ModHealing | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:180) | SpellBonusModule.cs |  |
| 116 | ModRegenDuringCombat | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:181) | CombatHealthRegenAuras.cs, MapCombat.Regen.cs |  |
| 117 | ModMechanicResistance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:182) | MechanicResistRule.cs, SpellCombatRules.cs |  |
| 118 | ModHealingPct | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:183) | DirectCombatEffects.cs, SpellBonusModule.cs |  |
| 119 | SharePetTracking | Unsupported | `HandleUnused` (SpellAuras.cpp:184) |  |  |
| 120 | Untrackable | Unsupported | `HandleAuraUntrackable` (SpellAuras.cpp:185) |  |  |
| 121 | Empathy | Unsupported | `HandleAuraEmpathy` (SpellAuras.cpp:186) |  |  |
| 122 | ModOffhandDamagePct | Unsupported | `HandleModOffhandDamagePercent` (SpellAuras.cpp:187) |  |  |
| 123 | ModTargetResistance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:188) | SpellCombatRules.cs |  |
| 124 | ModRangedAttackPower | Handler | `HandleAuraModRangedAttackPower` (SpellAuras.cpp:189) | StatAuras.cs |  |
| 125 | ModMeleeDamageTaken | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:190) |  |  |
| 126 | ModMeleeDamageTakenPct | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:191) |  |  |
| 127 | RangedAttackPowerAttackerBonus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:192) |  |  |
| 128 | ModPossessPet | Handler | `HandleModPossessPet` (SpellAuras.cpp:193) | CharmService.Possess.cs, CharmCastCheck.cs |  |
| 129 | ModSpeedAlways | Handler | `HandleAuraModIncreaseSpeed` (SpellAuras.cpp:194) | SpeedAuras.cs, UnitSpeed.cs |  |
| 130 | ModMountedSpeedAlways | Handler | `HandleAuraModIncreaseMountedSpeed` (SpellAuras.cpp:195) | SpeedAuras.cs, UnitSpeed.cs |  |
| 131 | ModRangedAttackPowerVersus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:196) |  |  |
| 132 | ModIncreaseEnergyPercent | Handler | `HandleAuraModIncreaseEnergyPercent` (SpellAuras.cpp:197) | PercentStatAuras.cs |  |
| 133 | ModIncreaseHealthPercent | Handler | `HandleAuraModIncreaseHealthPercent` (SpellAuras.cpp:198) | PercentStatAuras.cs |  |
| 134 | ModManaRegenInterrupt | Handler | `HandleAuraModRegenInterrupt` (SpellAuras.cpp:199) | ManaRegenInterruptAuras.cs, RegenModifiers.cs |  |
| 135 | ModHealingDone | Referenced | `HandleModHealingDone` (SpellAuras.cpp:200) | SpellBonusModule.cs |  |
| 136 | ModHealingDonePercent | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:201) | DirectCombatEffects.cs, SpellBonusModule.cs |  |
| 137 | ModTotalStatPercentage | Handler | `HandleModTotalPercentStat` (SpellAuras.cpp:202) | PercentStatAuras.cs |  |
| 138 | ModMeleeHaste | Handler | `HandleModMeleeSpeedPct` (SpellAuras.cpp:203) | AttackSpeedAuras.cs |  |
| 139 | ForceReaction | Handler | `HandleForceReaction` (SpellAuras.cpp:204) | ../ReputationSpellHandlers.cs |  |
| 140 | ModRangedHaste | Handler | `HandleAuraModRangedHaste` (SpellAuras.cpp:205) | AttackSpeedAuras.cs |  |
| 141 | ModRangedAmmoHaste | Handler | `HandleRangedAmmoHaste` (SpellAuras.cpp:206) | AttackSpeedAuras.cs |  |
| 142 | ModBaseResistancePct | Handler | `HandleAuraModBaseResistancePercent` (SpellAuras.cpp:207) | PercentStatAuras.cs |  |
| 143 | ModResistanceExclusive | Handler | `HandleAuraModResistanceExclusive` (SpellAuras.cpp:208) | ResistanceExclusiveAuras.cs |  |
| 144 | SafeFall | Handler | `HandleAuraSafeFall` (SpellAuras.cpp:209) | FallObserver.cs, MovementFlagAuras.cs |  |
| 145 | Charisma | Unsupported | `HandleUnused` (SpellAuras.cpp:210) |  |  |
| 146 | Persuaded | Unsupported | `HandleUnused` (SpellAuras.cpp:211) |  |  |
| 147 | MechanicImmunityMask | Handler | `HandleModMechanicImmunityMask` (SpellAuras.cpp:212) | CasterAuraGate.cs, ImmunityAuraHandlers.cs, ImmunityRules.cs |  |
| 148 | RetainComboPoints | Referenced | `HandleAuraRetainComboPoints` (SpellAuras.cpp:213) | ComboPointService.cs |  |
| 149 | ResistPushback | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:214) | SpellSystem.Pushback.cs |  |
| 150 | ModShieldBlockvaluePct | Handler | `HandleShieldBlockValue` (SpellAuras.cpp:215) | PercentStatAuras.cs |  |
| 151 | TrackStealthed | Handler | `HandleAuraTrackStealthed` (SpellAuras.cpp:216) | RangedHandlers.cs, SpellInfo.cs, TrackingAuras.cs |  |
| 152 | ModDetectedRange | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:217) |  |  |
| 153 | SplitDamageFlat | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:218) | SpellSystem.Mitigation.cs |  |
| 154 | ModStealthLevel | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:219) | StealthDetection.cs, StealthFeature.cs |  |
| 155 | ModWaterBreathing | Handler | `HandleModWaterBreathing` (SpellAuras.cpp:220) | WaterBreathingAuras.cs |  |
| 156 | ModReputationGain | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:221) | ../ReputationSpellHandlers.cs |  |
| 157 | PetDamageMulti | Unsupported | `HandleUnused` (SpellAuras.cpp:222) |  |  |
| 158 | ModShieldBlockvalue | Handler | `HandleShieldBlockValue` (SpellAuras.cpp:223) | PercentStatAuras.cs |  |
| 159 | NoPvpCredit | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:224) | ../../Honor/HonorSpellEffects.cs |  |
| 160 | ModAoeAvoidance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:225) | SpellCombatRules.cs |  |
| 161 | ModHealthRegenInCombat | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:226) | CombatFlatHealthRegenAuras.cs, MapCombat.Regen.cs |  |
| 162 | PowerBurnMana | Handler | `HandleAuraPowerBurn` (SpellAuras.cpp:227) | SpellSystem.PowerBurn.cs, ProcFlagRules.cs, PeriodicTiming.cs |  |
| 163 | ModCritDamageBonus | Unsupported | `HandleUnused` (SpellAuras.cpp:228) |  |  |
| 164 | Unk164 | Unsupported | `HandleUnused` (SpellAuras.cpp:229) |  |  |
| 165 | MeleeAttackPowerAttackerBonus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:230) |  |  |
| 166 | ModAttackPowerPct | Handler | `HandleAuraModAttackPowerPercent` (SpellAuras.cpp:231) | PercentStatAuras.cs |  |
| 167 | ModRangedAttackPowerPct | Handler | `HandleAuraModRangedAttackPowerPercent` (SpellAuras.cpp:232) | PercentStatAuras.cs |  |
| 168 | ModDamageDoneVersus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:233) |  |  |
| 169 | ModCritPercentVersus | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:234) | SpellCombatRules.cs |  |
| 170 | DetectAmore | Unsupported | `HandleDetectAmore` (SpellAuras.cpp:235) |  |  |
| 171 | ModSpeedNotStack | Handler | `HandleAuraModIncreaseSpeed` (SpellAuras.cpp:236) | SpeedAuras.cs, UnitSpeed.cs |  |
| 172 | ModMountedSpeedNotStack | Handler | `HandleAuraModIncreaseMountedSpeed` (SpellAuras.cpp:237) | SpeedAuras.cs, UnitSpeed.cs |  |
| 173 | AllowChampionSpells | Unsupported | `HandleUnused` (SpellAuras.cpp:238) |  |  |
| 174 | ModSpellDamageOfStatPercent | Referenced | `HandleModSpellDamagePercentFromStat` (SpellAuras.cpp:239) | SpellBonusModule.cs |  |
| 175 | ModSpellHealingOfStatPercent | Referenced | `HandleModSpellHealingPercentFromStat` (SpellAuras.cpp:240) | SpellBonusModule.cs |  |
| 176 | SpiritOfRedemption | Handler | `HandleSpiritOfRedemption` (SpellAuras.cpp:241) | SpellSystem.SpiritOfRedemption.cs, MapCombat.Melee.cs, Battleground.cs |  |
| 177 | AoeCharm | Handler | `HandleAuraAoeCharm` (SpellAuras.cpp:242) | CharmService.Charm.cs |  |
| 178 | ModDebuffResistance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:243) | SpellCombatRules.cs |  |
| 179 | ModAttackerSpellCritChance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:244) | SpellCombatRules.cs |  |
| 180 | ModFlatSpellDamageVersus | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:245) |  |  |
| 181 | ModFlatSpellCritDamageVersus | Unsupported | `HandleUnused` (SpellAuras.cpp:246) |  |  |
| 182 | ModResistanceOfStatPercent | Unsupported | `HandleAuraModResistenceOfStatPercent` (SpellAuras.cpp:247) |  |  |
| 183 | ModCriticalThreat | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:248) |  | threat-and-aggro |
| 184 | ModAttackerMeleeHitChance | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:249) |  |  |
| 185 | ModAttackerRangedHitChance | Unsupported | `HandleNoImmediateEffect` (SpellAuras.cpp:250) |  |  |
| 186 | ModAttackerSpellHitChance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:251) | SpellCombatRules.cs |  |
| 187 | ModAttackerMeleeCritChance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:252) | SpellCombatRules.cs |  |
| 188 | ModAttackerRangedCritChance | Referenced | `HandleNoImmediateEffect` (SpellAuras.cpp:253) | SpellCombatRules.cs |  |
| 189 | ModRating | Unsupported | `HandleUnused` (SpellAuras.cpp:254) |  |  |
| 190 | ModFactionReputationGain | Handler | `HandleNoImmediateEffect` (SpellAuras.cpp:255) | ../ReputationSpellHandlers.cs |  |
| 191 | UseNormalMovementSpeed | Handler | `HandleAuraModUseNormalSpeed` (SpellAuras.cpp:256) | SpeedAuras.cs, UnitSpeed.cs |  |
| 192 | AuraSpell | Unsupported | `HandleAuraAuraSpell` (SpellAuras.cpp:258) |  |  |

## Provenance

Spell.dbc data for the polarity and regen numbers (Stealth 1784 shape, Food 433 amount 17 with no amplitude, Drink 430 amount 42) was read from the
operator's 1.12.1 client (`patch-2.MPQ`, `DBFilesClient\Spell.dbc`, 22,357 records) with a throwaway reader kept outside the repository; no reference tree contains Spell.dbc.
No vmangos code or data was copied into the repository; every formula is re-expressed with a file:line citation.

## Tests

`tests/ArcaneCore.Game.Tests/Auras/*` (polarity, lifecycle, regen, periodic timing, interrupt, persistence, support matrix) on the virtual clock of `SpellTestKit`,
`tests/ArcaneCore.World.Tests/Spells/AuraOptionsBindingTests.cs` and `AuraSupportWorldTests.cs`. No Characters or World store changed in this lane, so there is no
provider-specific exposure beyond the unchanged `character_aura` theories, which run on hosted CI for MariaDB and PostgreSQL. Real-client acceptance (hybrid): right-click
cancel of Stealth, sitting down to eat and drink, food and drink numbers over 18 s.
