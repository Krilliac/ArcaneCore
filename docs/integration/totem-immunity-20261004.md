# Totem effect immunity, 2026-10-04

Summoned totems now apply the intrinsic per-effect immunity rules from vmangos
core commit `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
[`Objects/Totem.cpp:180-217`](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/Totem.cpp#L180).
The implementation uses the existing `TotemQuery` identity and spell immunity
seam. It adds no persisted state or schema, and ordinary creatures keep their
existing immunity rules.

Foreign taunts (`ATTACK_ME`), direct healing, mechanical healing, maximum-health
healing and energize effects cannot affect a totem. Negative aura-applying
effects are removed independently of their accompanying damage. Positive
periodic heal, periodic energize and periodic health funnel effects are also
blocked. Other positive buffs and direct damage fall through to the existing
creature and live-aura immunity checks.

Self casts and Shaman spells whose family flags overlap `0x4006000` (Healing
Stream, Mana Spring, Mana Tide) bypass the per-effect immunity checks completely,
including the creature fallback. Whole-spell and damage immunities still use
their existing separate rules. These exceptions precede the intrinsic checks,
as they do in the reference. `NO_IMMUNITIES` and ignore-caster/target-restrictions
attributes do not bypass intrinsic foreign-cast totem immunities. The existing
developer switch `SpellRules:ImmunityEnforcement=false` disables enforcement.

The regeneration list and supported aura application shapes follow
[`Spells/SpellEntry.h:469-485,838-849`](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Spells/SpellEntry.h#L469).
`MOD_REGEN` is deliberately not added to that list. Aura negativity uses the
server's existing `SpellInfo.IsPositive` simplification; full upstream positivity
classification remains a broader spell-engine limitation.

Game tests exercise the actual immunity application rule and real mixed-effect,
foreign/self-heal and Shaman-family casts. A production world-host case checks
that the installed spell rules suppress foreign healing and negative auras,
preserve damage, and permit self and Healing Stream-shaped healing. Synthetic
spell and creature fixtures replace proprietary world data. Upstream C++ and
content assets are not vendored. Real build-5875 client acceptance remains
pending; combined automated validation is recorded in the tranche handoff.
