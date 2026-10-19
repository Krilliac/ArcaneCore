using ArcaneCore.Game;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Resurrection sickness (spell 15007, SPELL_ID_PASSIVE_RESURRECTION_SICKNESS) as a managed bot sees it. vmangos
/// Player::ResurrectPlayer(applySickness) puts it on a player the spirit healer revives from <c>Death.SicknessLevel</c> (11) up, for one
/// minute at that level and one more per level above, ten at most (<see cref="SpiritHealerResurrection"/>); a corpse reclaim never
/// does (HandleReclaimCorpseOpcode, <c>ResurrectPlayer(0.5f)</c>). The 1.12.1 Spell.dbc row (client-dbc-5875) has three auras, each
/// -75% (base points -76, one die): MOD_PERCENT_STAT on every stat (80, misc -1: the level base of each stat, so the base stamina's
/// health with it), MOD_DAMAGE_PERCENT_DONE on every school (79, misc 127: every blow and spell deals a quarter) and
/// MOD_RESISTANCE_PCT on every school (101). It is temporary: the bot's own strength is what it has once it ends.
/// <para>
/// Live 2026-10-11 (analysis bot-xp-analysis.md, 4.1): 61% of the running level 11+ population bots were sick at any moment; judged
/// with the sickened health and damage, ordinary creatures round a body looked too strong, the bot took the healer again, and the
/// healer sickened it again.
/// </para>
/// </summary>
internal static class PlayerbotSickness
{
    /// <summary>SPELL_ID_PASSIVE_RESURRECTION_SICKNESS.</summary>
    internal const uint Spell = SpiritHealerResurrection.ResurrectionSicknessSpell;

    /// <summary>The stamina's index among the five stats (UNIT_FIELD_STAT0 + 2, STAT_STAMINA).</summary>
    private const int StaminaIndex = 2;

    /// <summary>The bot's resurrection sickness, or null (no spell system, or not sick).</summary>
    internal static SpellAuraHolder? Holder(WorldSession session, Player player)
        => session.Services.GetService<SpellFeature>()?.System is { } spells ? Holder(spells, player) : null;

    internal static SpellAuraHolder? Holder(SpellSystem spells, Player player)
    {
        foreach (SpellAuraHolder holder in spells.GetAuras(player))
            if (holder.Spell.Id == Spell && !holder.IsRemoved) return holder;
        return null;
    }

    /// <summary>Whether the bot carries resurrection sickness now.</summary>
    internal static bool IsSick(WorldSession session, Player player) => Holder(session, player) is not null;

    /// <summary>
    /// The level from which the spirit healer gives resurrection sickness (<see cref="DeathOptions.SicknessLevel"/>, vmangos
    /// <c>Death.SicknessLevel</c>, default 11).
    /// </summary>
    internal static int SicknessLevel(WorldSession session) => DeathHooks.For(session.World).Options.SicknessLevel;

    /// <summary>Whether a spirit-healer resurrection would sicken this bot (its level is at least <see cref="SicknessLevel"/>).</summary>
    internal static bool HealerSickens(WorldSession session, Player player) => player.Level >= SicknessLevel(session);

    /// <summary>
    /// The bot's maximum health without its resurrection sickness: what a corpse-run revival leaves it with once the sickness runs out
    /// (the reclaim itself adds none). The sickness's MOD_PERCENT_STAT share of the stamina's level base is taken back out of the
    /// player's stat ledger (<see cref="UnitModLedger"/>: <c>((base x BasePct) + (pre - base)) x TotalPct</c>, the reference's order,
    /// <see cref="PercentStatAuras"/>), and the health that stamina gives (<see cref="ExperienceFormulas.HealthBonusFromStamina"/>,
    /// Player::GetHealthBonusFromStamina) is added back. Everything else stays as it is: broken gear gives no stats with or without
    /// the sickness. <see cref="Unit.MaxHealth"/> itself when the bot is not sick.
    /// </summary>
    internal static uint UnsickenedMaxHealth(WorldSession session, Player player)
    {
        if (session.Services.GetService<SpellFeature>()?.System is not { } spells || Holder(spells, player) is not { } holder)
            return player.MaxHealth;
        float factor = 1f;
        foreach (SpellAura? aura in holder.Auras)
            if (aura is { Type: AuraType.ModPercentStat } && (aura.MiscValue < 0 || aura.MiscValue == StaminaIndex))
                factor *= 1f + (aura.Amount / 100f);
        if (factor <= 0.01f || factor >= 1f) return player.MaxHealth;

        const int stamina = StaminaIndex;
        UnitModLedger ledger = player.StatState.Mods;
        const UnitMods group = UnitMods.StatStamina;
        int current = player.GetInt32(UpdateFields.UnitFieldStat0 + stamina);
        int pre = current - ledger.Applied(group);
        int baseValue = Math.Max(0, pre - player.GetInt32(UpdateFields.PlayerFieldPosstat0 + stamina)
            - player.GetInt32(UpdateFields.PlayerFieldNegstat0 + stamina));
        float total = ledger.TotalPct(group);
        if (total <= 0) return player.MaxHealth;
        int well = Math.Max(0, (int)(((baseValue * (ledger.BasePct(group) / factor)) + (pre - baseValue)) * total));
        if (well <= current) return player.MaxHealth;
        long gained = ExperienceFormulas.HealthBonusFromStamina((uint)well) - (long)ExperienceFormulas.HealthBonusFromStamina((uint)Math.Max(0, current));
        return (uint)Math.Clamp(player.MaxHealth + gained, player.MaxHealth, uint.MaxValue);
    }
}
