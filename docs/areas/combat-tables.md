# Combat table audit (1.12.1)

This audit covers the integrated white melee and magic spell tables. The primary
reference is the read-only vmangos clone at `D:/refs/vmangos`. Its GPL source and
resistance rows were not copied into ArcaneCore. The arithmetic tests are in
`tests/ArcaneCore.Game.Tests/CombatTableReferenceTests.cs`, alongside the older
`CombatHitTableTests`, `SpellRules/*`, and `CombatMechanics/PowerRulesTests`.

Full reference locations (line ranges are from this clone):

- `D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:336-412` — white miss and dual wield.
- `D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:446-725` — swing roll order, avoidance, glancing and crushing.
- `D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:795-925` — magic hit and resistance conversion.
- `D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:958-993` — spell critical damage.
- `D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:1121-1145` — armor reduction.
- `D:\refs\vmangos\src\game\Objects\Unit.cpp:1364-1565` — melee damage schools, block, absorb and hit flags.
- `D:\refs\vmangos\src\game\Objects\Unit.cpp:1571-1601` — glancing damage range.
- `D:\refs\vmangos\src\game\Objects\Unit.cpp:1875-1918` and `:2397-2458` — resistance buckets and interpolation.
- `D:\refs\vmangos\src\game\Objects\Unit.cpp:1920-2083` — school and mana shields.
- `D:\refs\vmangos\src\game\Objects\Unit.cpp:2552-2595` and `:5212-5327` — melee and spell critical chance.
- `D:\refs\vmangos\src\game\Objects\Player.cpp:2243-2267` — rage from damage.
- `D:\refs\wow_messages\wow_message_parser\wowm\world\combat\smsg_attackerstateupdate.wowm:1-52` — 1.12 hit flags and per-school damage/absorb/resist packet fields.

| Stage | Retail behavior and reference | ArcaneCore status |
| --- | --- | --- |
| White swing roll | One 0–9999 roll: miss, dodge, parry, glancing, block, crit, crushing, normal (`src/game/Objects/SpellCaster.cpp:446-725`). | `MeleeHitTable.Roll` matches the order. Ranged swings skip the avoidance, glancing, block and crushing entries (`:532-541`). |
| Skill and miss | Player victim: 0.04% per skill; creature victim: 0.1% per point until the deficit exceeds ten, then 0.2%; dual wield white swings add 19%; the first 1% positive hit bonus is lost at a deficit above ten (`SpellCaster.cpp:336-412`). | The pure `MeleeHitTable` matches. This lane fixes the live off-hand input: attached learned skill now takes precedence over the stat source's level maximum. Generic `MOD_HIT_CHANCE` auras now reach white swings through the spell system. Weapon-restricted aura filtering (`SpellCaster.cpp:389-390`) is not yet represented. |
| Avoidance and crit | Dodge/block use 4 basis points per skill versus players and 10 versus creatures; creature parry uses 20, or 60 past a ten-point deficit (`SpellCaster.cpp:455-468,585-679`). Crit uses 0.04% versus players or while ahead, otherwise 0.2% of the capped deficit (`Unit.cpp:2552-2595`). | The pure table implements these branches. The player crit field is supplied by the stats area; creature/victim crit auras are not in the white-swing input. |
| Glancing | Player-controlled attacker versus non-player-controlled victim: `clamp((10 + 2 × (defense − min(skill, level maximum))) × 100, 0, 4000)` roll slots (`SpellCaster.cpp:627-654`). Damage range is the Baeza low/high formula, with a caster penalty (`Unit.cpp:1571-1601`). | Pure table and damage range match. Pet ownership is not represented in `BuildRollInput`, so pet glancing eligibility remains limited. |
| Crushing | Creature attacker with eligible flags, when its level maximum exceeds the victim's capped defense by at least 15: `200 × difference − 1500` slots; damage is 150% (`SpellCaster.cpp:695-724`, `Unit.cpp:1496-1504`). | Formula matches for creatures represented by `ICombatCreature`. The reference's always-crush extra flag is not represented. |
| Armor and block | Armor reduction is `(0.1 × armor / (8.5 × attacker level + 40)) / (1 + that ratio)`, capped at 75%, with at least one damage left (`SpellCaster.cpp:1121-1145`). A block removes at most shield block value after armor and marks a full block when it consumes the hit (`Unit.cpp:1463-1479`). | Both formulas are present. This lane connects white physical swings to school and mana shields after block, reports absorbed damage in the hit packet, and preserves damage-interrupt behavior even for full absorbs (`Unit.cpp:1510-1565,1920-2083`). Non-physical weapon school slices and their partial resists remain unmodelled. |
| Magic hit | 96% at equal level, 95% at +1, 94% at +2, then 7 percentage points per further level against players or 11 against creatures, base floor 22%; final chance is truncated to integer slots out of 10000 and clamped to 1–99% (`SpellCaster.cpp:795-879`). | This lane fixes the fractional-slot truncation and rolls miss from that integer range. A scripted boundary test pins the 9556-hit/444-miss case. |
| Spell crit and schools | Magic and weapon-class spells use separate crit sources; creature magic spells do not crit by default (`Unit.cpp:5212-5327`). Melee/ranged spell crit adds 100% damage, magic adds 50% before modifiers (`SpellCaster.cpp:958-993`). School-specific damage and target-side modifiers are applied in the spell bonus pipeline (`SpellCaster.cpp:1243-1276`; `Unit.cpp:1364-1374`). | `VanillaSpellCombatRules` and `SpellBonusModule` cover the spell path. White swings do not yet apply the reference's melee damage done/taken school modifiers or non-physical weapon schools. |
| Partial resist | Innate creature resistance from level difference and the 0–75% average cap are in `SpellCaster.cpp:882-925`. vmangos interpolates 31 probability rows for 0/25/50/75/100% resist buckets and folds a 100% outcome to 75% (`Unit.cpp:1875-1918,2397-2458`). | `SpellResistance` matches the average formula and `ResistOutcomeTable` supports operator-supplied rows. Without `SpellRules:ResistTablePath`, the default two-step approximation has different bucket probabilities. The GPL reference rows are not shipped. |
| Power on damage | Rage uses Kalgan's level conversion and ×7.5 dealt / ×2.5 taken, with ×1.3 taken under Berserker Rage and the configured income rate (`Player.cpp:2243-2267`). Mana Shield spends mana for absorbed damage (`Unit.cpp:2040-2083`). | `PowerRules` and direct spell mana shields implement these. Ordinary damage taken has no general mana-generation rule in the referenced damage path (`Unit.cpp:800-876`); individual aura procs require separate coverage. |

## Verification boundary

The table-driven vectors cover levels 5, 10, 30, and 60–64; skill deficit 0,
5, 10, and 15; spell level differences from −2 through +20; armor and
resistance caps; glancing and crushing boundaries; and a fractional spell-hit
roll boundary. Existing tests cover rage, spell crit, and resistance outcomes.
The exact solution build command was blocked by NuGet restore's `NU1900`
vulnerability-feed error. A command-local `NuGetAudit=false` allowed a Release
solution build (zero warnings/errors), 3,703 Game tests, and 1,305 World tests
(one existing real-dump skip). A full World run initially caught a regression
in the direct `DealDamage` pushback event; the event was restored and the full
World suite passed on rerun. Real build-5875 client packets remain unverified
in this lane.
