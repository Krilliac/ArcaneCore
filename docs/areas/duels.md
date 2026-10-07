# Duels

Player-versus-player duels after vmangos (primary reference, `D:\refs\vmangos`), with mangos-classic (`D:\refs\mangos-classic`)
used to record differences. Code: `src/ArcaneCore.Game/Combat/Duel/` (state, rules, service, spell effect, map tick),
`src/ArcaneCore.Game/Entities/Player.Duel.cs`, `src/ArcaneCore.World/Combat/Duel/` (feature and opcode handlers). Integration
points in shared files: [integration/duels.md](../integration/duels.md).

Evidence: automated tests against the references cited below and synthetic clients over loopback. **Not verified against a
real 1.12.1 client** (charter 1.3); the client checklist at the end is what a client pass has to confirm.

## Delivered scope

| Part | Where | Reference |
|---|---|---|
| Challenge spell 7266: `SPELL_EFFECT_DUEL` (83) | `DuelSpellEffects`, `DuelService.Challenge/CheckChallenge` | vmangos `Spells/SpellEffects.cpp:4650-4761`, `Spells/Spell.cpp:6187-6203` |
| Flag object (Duel Flag, gameobject 21680, type 16) at the midpoint, faction/level/creator, lifetime = spell duration | `DuelService.Challenge` | `SpellEffects.cpp:4704-4730` |
| `SMSG_DUEL_REQUESTED`, the two crossed `DuelInfo` halves, `PLAYER_DUEL_ARBITER` | `DuelService.Begin` | `SpellEffects.cpp:4732-4760` |
| `CMSG_DUEL_ACCEPTED` and the 3000 ms countdown; the flag turns on 3 whole seconds later, teams 1 and 2 | `DuelService.Accept`, `UpdateDuelFlag` | `Handlers/DuelHandler.cpp:30-47`, `Objects/Player.cpp:17248-17263` |
| `CMSG_DUEL_CANCELLED`: discard (interrupted) and `/forfeit` (combat stops, Grovel 7267, the opponent wins) | `DuelService.Cancel` | `DuelHandler.cpp:49-71` |
| Boundaries: 50 yd out, 40 yd back (retail; vmangos 75/70 is an option), 10 s grace, `SMSG_DUEL_OUTOFBOUNDS/INBOUNDS`, a missing flag ends it as fled | `DuelService.CheckDistance`, `MapDuel` | `Player.cpp:6671-6718`, `Objects/Object.cpp:1738-1752` |
| Completion: `SMSG_DUEL_COMPLETE`, `SMSG_DUEL_WINNER`, flag removal, hostile auras since the start removed, combo points cleared, arbiter/team reset, delayed delete | `DuelService.Complete` | `Player.cpp:6726-6822`, `Player.cpp:1126-1140` |
| Hostility: a started duel makes the two players hostile before any team rule and waives the PvP-flag gate; no PvP pulses between opponents | `DuelRules`, `CombatHooks.IsFriendly/CanAttack`, `MapCombat.TogglePlayerPvpFlagOnAttackVictim/SetInCombatWithAggressor` | `Object.cpp:3650-3652`, `3797-3800`; `Objects/Unit.cpp:5973`, `6047` |
| Duels end at 1 hp: clamp, win, Grovel; lethal damage from a third party kills and interrupts | `MapCombat.Duel.cs`, three call sites in `DealDamage` | `Unit.cpp:762-779`, `825-843`, `954-969` |
| Cleanup on logout/disconnect (interrupted) and on leaving the map (fled) | `DuelFeature`, `MapDuel.OnPlayerRemoved` | `Player.cpp:2228-2230`, `1878-1883` |
| Aura apply time (`SpellAuraHolder.AppliedAtUnixSeconds`) | `SpellSystem.AddAuraHolder` | `Spells/SpellAuras.cpp:6665`, `SpellAuras.h:473` |

The post-duel "immunity" some players remember is not a thing in either reference: what exists is the removal of the opposing hostile
auras applied since the start, the loser left at 1 hp, and the Grovel stun.

## Wire formats

Verified against vmangos `Server/Packets/Duel.cpp:20-65` and gtker `wow_messages` `world/duel/*.wowm`. `SMSG_DUEL_REQUESTED` is flag guid then
challenger guid (two full u64); `SMSG_DUEL_COMPLETE` one byte `started`; `SMSG_DUEL_WINNER` byte `fled`, winner name, loser name (wow_messages names the
fields `reason`, `opponent_name`, `initiator_name`: same bytes); `SMSG_DUEL_COUNTDOWN` u32 3000 (wow_messages types it as seconds, both servers send 3000);
`SMSG_DUEL_OUTOFBOUNDS/INBOUNDS` empty; `CMSG_DUEL_ACCEPTED/CANCELLED` one u64 guid that both references read and ignore.

## Configuration (`World:Duel`, restart-only)

Defaults are the vmangos values; a different value is a deliberate deviation.

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: the duel spell is refused with `SPELL_FAILED_NO_DUELING` |
| `StartDelaySeconds` | `3` | Accept to start (the countdown packet always says 3000 ms) |
| `OutOfBoundsYards` | `50` | Warning distance, original 1.12 (mangos-classic `Player.cpp:6917-6937`); `75` is the vmangos Nostalrius widening (`Player.cpp:6688-6716`, "50 -> 75m") |
| `ReturnInBoundsYards` | `40` | Distance that counts as back (mangos-classic `Player.cpp:6927`); vmangos uses 70 |
| `OutOfBoundsGraceSeconds` | `10` | Seconds out before the duel is lost as fled (both references) |
| `RequireKnownArea` | `false` | vmangos lets a duel start where the area has no AreaTable row; on, such areas refuse |
| `ExpiredRequestIsSilent` | `false` | Both references end an unaccepted request whose flag expired as fled (a winner is announced); on, it is interrupted |

## Deviations and decisions

* Time is whole Unix seconds on the world's death clock (vmangos `time(nullptr)`): the flag turns on when `now >= accept + 3`, so 2-3 real seconds pass.
  The same clock stamps aura holders (`SpellSystem.UnixSecondsClock`), so "applied since the start" compares like with like.
* A cancelled request that never started removes **every** hostile aura the other side cast on this player, at any time: vmangos compares
  `applyTime >= startTime` with `startTime = 0` (`Player.cpp:6757-6770`). Copied on purpose.
* Both vmangos quirks of `EffectDuel` that corrupt state are not copied: it deletes the target's duel object even when it belongs to a third
  player (unreachable on retail data because the cast check refuses a target in a duel), and a self challenge is not refused server-side
  (here: `BAD_TARGETS`).
* Team numbers: whichever participant the map tick reaches first gets team 1 (as in vmangos); tests only assert `{1, 2}`.
* mangos-classic differences recorded, not adopted: `ForceHealthAndPowerUpdate` around duels, `PLAYER_DUEL_TEAM`-based reactions, copying the duel team
  to charmed units, the 50/40 yd distances (selectable by option), and blocking the logout request while dueling (vmangos only blocks it while in combat,
  which a duel already is; a logout mid-duel completes interrupted either way).

* **Reflected spells and DoTs** (proc-engine lane, docs/areas/procs.md): a reflected spell that would kill its own caster is cut to 1 health and ends the duel
  with the caster as the loser (`pVictim == this && reflected`, Unit.cpp:770-776, `MapCombat.ApplyDuelClamp`), and completion removes reflected debuffs
  whoever cast them (`IsReflected` clause, Player.cpp:6762-6787). Playerbot scenario: `proc-reflect-duel`.

## Limits (documented, not stubbed)

* **Pets, guardians, totems, charmed units.** There is none on this base. The seam is `IPlayerControlledUnit`: the clamp, the hostility rule and the PvP-pulse
  exemption already read it, so the pets lane only has to implement it. Not done: `CombatStopWithPets` over controlled units, combo points aimed at the
  opponent's pet, pet hostility through the owner on the `CanAttack` path of creatures, copying `PLAYER_DUEL_TEAM` to a charmed player.
* **Transports** (`DuelInfo.transportGuid`, `SPELL_FAILED_NOT_ON_TRANSPORT`, leaving the transport ends the duel): no transport system.
* **Helpful spells on duelists** (vmangos `Spell::CheckTarget` drops a positive spell aimed at a started-duel player from a non-opponent, `IsValidHelpfulTarget`
  "cannot help others in duels", party area auras skipping dueling members, `Object.cpp:3846-3848`, `SpellAuras.cpp:621-623`): needs a hook in spell target
  selection that the wave-2 spell combat rules lane reworks; not delivered (slice skipped).
* **Area flags.** `AREA_FLAG_DUEL` (0x40) is read from the area table; the client AreaTable is not on the build machine and not in the references, so which zones allow duels is
  untested beyond synthetic rows. Unknown areas pass by default.
* **Quest settlement.** A player whose quest settlement is pending cannot accept, and a challenge involving one is dropped; completion on such a player still runs.
* **Flag template.** A world whose gameobject content lacks entry 21680 cannot host duels: the request is dropped and no state is left behind.
* **Flag lifetime.** It is the duration of spell 7266 (`SpellDuration` row 21). The row is client DBC data that is not available here, so the real value is unverified.
* **No Characters or World schema.** Duel state is runtime-only (vmangos clears it at load, `Player.cpp:14945-14947`): no table, no store, no data module, no
  provider-aware theory. The MariaDB/PostgreSQL rule has no surface in this lane.

## Open questions

* Whether the real 1.12 client agrees with 50/40 yd (the retail default, from mangos-classic; vmangos 75/70 is a Nostalrius change and an option): confirm in the client pass.
* The real duration of the flag object (SpellDuration 21) and with it how long an unaccepted request lives.
* Whether the 1.12 client needs the mangos-classic health/power percentage refresh for the opponent's bars.
* Whether retail refuses a challenge from an invisible or stealthed player (`SPELL_FAILED_CANT_DUEL_WHILE_INVISIBLE/STEALTHED` exist in the enum; neither server uses them).

## Client checklist (for the real-client pass)

1. `/duel` on a target: request dialog appears for the target; the flag stands between the players.
2. Accept: countdown 3-2-1, then the flag turns on; name plates turn hostile for same-team duelists.
3. Out-of-bounds warning past 50 yd, cleared by walking back; ten seconds out loses the duel.
4. Lethal hit leaves 1 hp, the loser is stunned (Grovel), the winner is announced, the flag disappears.
5. `/forfeit` mid-duel and discarding the request before the countdown ends.
6. Logout and a far teleport (hearthstone to another continent) mid-duel; a cast on a third player while dueling.
7. Re-challenging the same player mid-duel (`TARGET_DUELING`) and another player (old duel forfeits).
