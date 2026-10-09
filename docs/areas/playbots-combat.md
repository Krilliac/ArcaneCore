# Playerbot class combat

How a managed playerbot fights like its class. The rest of the bot (goals, movement, scripted mode, the scenario harness) is in
`docs/areas/playbots.md`. Reference: vmangos `src/game/PlayerBots/CombatBotBaseAI.cpp` and `PartyBotAI.cpp`.

## Pieces

| File (`src/ArcaneCore.World/Playerbots/`) | vmangos | What it does |
|---|---|---|
| `Combat/PlayerbotAbilities.cs` | `PopulateSpellData` (:136-1827) | Each class ability by its exact Spell.dbc name at the highest known rank ("Rank N", else the higher id); passive and hidden spells skipped; direct and periodic single-target heal lists, strongest first. |
| `Combat/PlayerbotRole.cs` | `AutoAssignRole` (:58-118) | Tank / melee / ranged / healer from the talent spell the bot knows: Shield Slam, Holy Shield, Sanctity Aura, Shadowform, Elemental Mastery, Stormstrike, Moonkin Form, Leader of the Pack. A later rank of the same spell counts. |
| `Combat/PlayerbotRotationState.cs` | | A plain snapshot of one decision: the bot, its victim (or pull target), its group, attackers, pet, totems, power, combo points, form and consumables, plus a `CanCast(spell, unit)` check. |
| `Combat/PlayerbotCombatView.cs` | `CanTryToCastSpell` (:2807-2860) | Builds the snapshot on the world thread. `CanCast`: spell and category cooldowns, global cooldown, caster/target aura state, power cost, shapeshift gate, reagents, equipped item class/subclass, ammunition for a bow/gun/crossbow, and range (the `SpellSystem.CheckRange` formula). It only avoids pointless requests; the ordinary handlers still decide. |
| `Combat/PlayerbotClassRotation.cs` | `CombatBotBaseAI` helpers | Shared steps: heal target (`SelectHealTarget`, `SelectPeriodicHealTarget`), most efficient heal, buff target (single vs group version), friendly dispel and purge, pet commands, wand, emergency potion/bandage, taunt condition. |
| `Combat/Playerbot<Class>Rotation.cs` (9) | `UpdateInCombatAI_<Class>` / `UpdateOutOfCombatAI_<Class>` (PartyBotAI.cpp:1163-3039) | The class priority lists. |
| `PlayerbotCombatSpells.cs` | `DoCastSpell`, `OnPacketReceived` (:544) | Runs a rotation and submits its action as an ordinary CMSG_CAST_SPELL, CMSG_USE_ITEM or CMSG_PET_ACTION; acceptance is read from SMSG_SPELL_START/GO or the cast, next-swing and auto-repeat slots. |

A rotation is a pure function of the snapshot (`InCombat` for a fight or pull, `OutOfCombat` for upkeep), so each class is unit
tested without a world (`tests/ArcaneCore.World.Tests/Playerbots/Combat/`).

## PlayerbotCombatSpells API

Kept from before (frozen): `PlayerbotCombatSpells(WorldSession)`, `bool Update(Player, Unit target, uint elapsedMs)` (true: an
action was accepted or a cast is in progress, the caller keeps still; false: chase or swing as usual) and `void Reset()`.

Added for the party AI (lane b1): `bool UpdateOutOfCombat(Player, uint elapsedMs)` (forms, auras, buffs on the bot and its group,
pet summon/revive/call, heals including group members, stealth before a pull), `float PreferredRange(Player)` and
`PlayerbotRole RoleOf(Player)`. `PullTarget` (internal) is the creature the bot is closing in on; the brain sets it before the
upkeep so a rogue stealths and a pet is sent ahead.

A spellbook with none of its class's ability names keeps the old behaviour: the first usable hostile unit-target spell
(the generic fallback the earlier tests rely on).

Spell data is rebuilt when SMSG_LEARNED_SPELL, SMSG_SUPERCEDED_SPELL or SMSG_REMOVED_SPELL is captured; only those opcodes are
drained from the bounded capture queue. `Reset` (death) also drops it. A spell the server refused is left out for 4 s
(`RefusalBackoffMs`) so the rest of the rotation goes on; the generic fallback keeps its 750 ms global backoff.

## Rotations

Each step is "known, castable at that unit now, not already on it (any rank of an aura spell), and its condition holds", in the
vmangos order. Common to every class: an emergency healing potion below 25% health in combat, or a bandage below 40% when
nothing is hitting the bot in melee and Recently Bandaged has worn off; friendly dispels first (not from a shapeshift form).

- **Warrior**: Battle Stance between fights (Defensive for a tank), Battle Shout (Bloodrage without rage); Charge to pull; Pummel /
  Shield Bash on a caster, Execute below 20%, Overpower, Revenge (tank), Last Stand, shield abilities in Defensive Stance with a
  shield, Thunder Clap / Sunder Armor (tank), Hamstring a runner, Rend (not on undead, elementals, mechanicals), the cooldowns,
  Mortal Strike / Bloodthirst, stance dance, Whirlwind, Cleave or Heroic Strike above 30 rage. Taunt / Mocking Blow (tank).
- **Rogue**: Stealth when closing in on a pull target; openers Garrote (caster) / Ambush / Cheap Shot; finishers at 5 points
  (Slice and Dice first if missing, else Eviscerate / Rupture / Kidney Shot / Expose Armor), from 3 points below 25%; Kick /
  Gouge; Evasion, Cold Blood, Blade Flurry; Backstab, Ghostly Strike, Hemorrhage, Sinister Strike; Sprint.
- **Hunter**: Aspect of the Hawk; Revive Pet / Call Pet / Mend Pet; Hunter's Mark to pull; pet sent at the target; from 8 yards
  out: Auto Shot (not while moving), Concussive, Aimed, Arcane, Serpent Sting, Multi-Shot; Scare Beast / Disengage / Monkey /
  Feign Death with an enemy close; in melee Wing Clip, Mongoose Bite, Raptor Strike. Ammo buying is lane b3's.
- **Mage**: Arcane Intellect / Brilliance, Ice Armor (Frost Armor until learned), Ice Barrier; Combustion, Pyroblast with Presence of
  Mind, Ice Block below 10%, Mana Shield and Frost Nova with an enemy close, Cone of Cold / Blast Wave / Arcane Explosion against
  two, Counterspell, Polymorph a second attacker, Arcane Power, Scorch below 20%, Frostbolt, Fire Blast, Fireball, Evocation,
  wand below 5% mana. Remove Lesser Curse.
- **Priest**: Fortitude, Divine Spirit, Shadow Protection (single or Prayer), Inner Fire; healer heals between fights; in a fight
  Power Word: Shield (not on Weakened Soul), Fade / Shackle Undead in a group, Inner Focus; as a healer (or out of Shadowform)
  shield a hurt group mate, direct heal below 60% (self) / 80% (group), Renew below 80% / 90%; otherwise Shadowform, Silence,
  Vampiric Embrace, Mind Blast, Shadow Word: Pain, Devouring Plague, Mana Burn on a player, Mind Flay, Holy Nova against three,
  Smite, wand below 10% mana. Dispel Magic, Abolish / Cure Disease.
- **Warlock**: Demon Armor (Demon Skin until learned); a demon by role when none is out: alone Voidwalker > Imp > Succubus >
  Felhunter, in a group Imp > Succubus > Felhunter > Voidwalker (vmangos picks a known one at random; the Imp needs no shard);
  Health Funnel, Life Tap; pet sent at the target; Death Coil, Shadowburn below 10%, Searing Pain below 20%, Banish a second
  demon or elemental, Immolate, Conflagrate, Corruption, Siphon Life, Drain Life below 30%, Fear and Howl of Terror only in a
  group, Curse of Agony, Shadow Bolt, Life Tap, wand below 5% mana.
- **Paladin**: the role's aura (Sanctity / Retribution for damage, Concentration for a grouped healer, else Devotion), Righteous
  Fury (tank), one blessing per friend (Kings if known, else Wisdom for a mana user, Might otherwise, Sanctuary on a tank
  itself); Divine Shield, Blessing of Protection / Sacrifice / Lay on Hands for a friend below 70%, Holy Shield, Turn Undead;
  healer heals (Holy Shock below 50%); seal (Command for damage, else Righteousness), Judgement above 30% mana, Hammer of
  Justice, Hammer of Wrath, Consecration against three, Holy Shock, Exorcism / Holy Wrath on undead and demons, Blessing of
  Freedom, self heal below 30%. Cleanse / Purify.
- **Shaman**: Lightning Shield, healer heals; Mana Tide; Elemental Mastery, Earth Shock on a caster, Frost Shock a runner,
  Stormstrike, Chain Lightning, Purge, Flame Shock, Lightning Bolt (always for elemental/restoration, out of reach for
  enhancement); one totem per element in the fight (Magma / Searing, Strength of Earth / Stoneskin, Healing Stream / Mana Spring,
  Windfury / Grace of Air for melee); self heal below 20%. Cure Disease / Poison.
- **Druid**: Mark / Gift of the Wild, Thorns, Nature's Grasp; the role's form (cat for damage, bear for a tank, moonkin for
  balance; a healer stays in caster form); Prowl when closing in as a cat; Barkskin; caster form heals (HoT, direct, Innervate),
  Hibernate in a group; cat: Pounce / Ravage / Tiger's Fury from stealth, Ferocious Bite / Rip at 5 points (3 below 25%), Faerie
  Fire (Feral), Dash, Shred, Rake, Claw; bear: Growl (tank), Feral Charge, Bash, Frenzied Regeneration, Faerie Fire (Feral),
  Demoralizing Roar / Swipe, Maul; caster: Entangling Roots in a group, Insect Swarm, Moonfire, Starfire above 50%, Wrath.
  Abolish / Cure Poison, Remove Curse.

Deliberate differences from the party bots, for a bot that also grinds alone: a healer with nobody to heal fights with its damage
spells instead of idling; tanks taunt a victim that turned to a group mate; rogues and cats finish early on a nearly dead victim;
Fear-type crowd control only in a group; hunters do not tame a random beast (vmangos summons one for a pet-less bot); no
ground-targeted area spells (Blizzard, Rain of Fire, Volley, Hurricane) and no weapon buffs or poisons yet.

## Positioning (PlayerbotBrain)

`PlayerbotBrain.DecidePosition` (vmangos PartyBotAI.cpp:755-766, `GetDistancingTarget` / `RunAwayFromTarget` :146-185, caster
chase distance 25): melee classes close to 4 yards and swing; casters and healers close to 25 yards and hold; a hunter with a
ranged weapon and Auto Shot closes to 30 and holds, and fights in melee (Wing Clip, Raptor Strike, white swings) inside its
8-yard dead zone. A ranged bot the enemy already reached in melee swings back; a ranged bot that found nothing to cast for 8 s at
a target not fighting it closes to melee. Nobody ranged otherwise runs in: an out-of-mana caster wands (the rotation) or, between
fights, drinks (the existing rest). Its victim is not `Combat.Victim` (no melee swing), so a living target that has the bot on its
threat list keeps being its fight through the combat linger.

"In melee" is the server's own swing reach (`MapCombat.CanReachWithMeleeAutoAttack`: 2D distance within the combat reach and
less than 6 yards of height), or the 4-yard 3D distance. Judged by the 3D distance alone, a target up a slope (2 yards away, 3.9 up)
stayed "out of melee" while the bot already stood at it: the chase had nowhere closer to go, no swing was sent, and the creature, which
the same rule lets hit, wore the bot down (Dawnrover against Kobold Laborers in the 2026-10-08 replays, `ttk=860s reason=behind`, a
retreat and once a death). The replay trace (`ARCANECORE_TEST_BOT_REPLAY_TRACE`) prints the melee geometry against the bot's target.

## Target etiquette (PlayerbotBrain.FindTarget)

Idle grinding skips a creature that is someone else's (vmangos `IsValidHostileTarget` and the client's grey name): tapped by a
player outside the bot's group (`UNIT_DYNFLAG_TAPPED` without `TAPPED_BY_PLAYER` as the bot sees it, LootService's viewer filter
over `Creature.LootTapPlayerGuid`), or fighting a player who is neither the bot nor a group mate (its victim or its threat list).
It also skips grey creatures (level at or below `ExperienceFormulas.GrayLevel`); a named quest objective may still be grey.

## Tests

- `Playerbots/Combat/PlayerbotClassRotationTests.cs`: per class, a synthetic book and a state give the expected next action.
- `Playerbots/Combat/PlayerbotCombatSupportTests.cs`: the role table, rank resolution, heal and buff target selection, the most
  efficient heal, the distancing decision.
- `Playerbots/Combat/PlayerbotTargetEtiquetteTests.cs`: the tap, engaged and grey filters (RED on 08ea5ff6).
- `Playerbots/PlayerbotCombatSpellsTests.cs`: the spell data rebuild on SMSG_LEARNED_SPELL and the role change; the stance taken
  through the ordinary cast handler.
- Scenarios `combat-warrior` .. `combat-druid` and `combat-tapped-wolf` (`Playerbots/Scenarios/ScenarioCombat.cs`, run by
  `ClassCombatScenarioTests`): a bot of each class (orc warrior, dwarf paladin, troll hunter, human rogue, undead priest, tauren
  shaman, gnome mage, human warlock, night elf druid) is created, taught its first spells by classic id, handed to its own brain
  (`ScenarioContext.AutonomousAsync`) 35 yards from the scenario wolf and must kill it: its class spells go off (SMSG_SPELL_GO),
  a ranged bot's first attack is from range and it does not walk in, the priest heals itself after being dropped to 40%, the
  hunter's first Auto Shot is from 8 yards or more, the warlock's Imp is out before its first Shadow Bolt and attacks the wolf;
  a wolf another player tagged is left alone for a 15 s watch. The test world uses `ScenarioClassContent` (every race/class pair
  creatable, synthetic class spells with real names and ids, the Imp, a bow and arrows), a flat floor
  (`ScenarioTestWorld.UseFlatFloorAsync`) and no scenario quest. Against live content the scenarios fail at "find the wolf".
