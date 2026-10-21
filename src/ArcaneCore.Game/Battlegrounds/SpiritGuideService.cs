using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// Keeps the battleground spirit guide's <see cref="BattlegroundConstants.SpellSpiritHealChannel"/> (22011) aura active across
/// non-death removals and expiry (vmangos <c>npc_spirit_guideAI::UpdateAI</c>, battleground.cpp:58-71; GitHub #887).
/// </summary>
public sealed class SpiritGuideService : ISpellHandlerModule
{
    /// <summary>Spirit Heal (22012), the 20-yard area resurrection wave triggered by <see cref="BattlegroundConstants.SpellSpiritHealChannel"/>.</summary>
    public const uint SpellSpiritHeal = 22012;

    private readonly HashSet<ObjectGuid> _recasting = [];

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderRemoved += holder => HandleHolderRemoved(system, holder);
    }

    public static void OnAuraRemoved(SpellSystem system, SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.Spell.Id != BattlegroundConstants.SpellSpiritHealChannel
            || holder.RemoveMode == AuraRemoveMode.Death
            || holder.Target is not { IsInWorld: true, IsAlive: true } guide)
        {
            return;
        }

        ReapplyChannel(system, holder, guide);
    }

    private void HandleHolderRemoved(SpellSystem system, SpellAuraHolder holder)
    {
        if (holder.Spell.Id != BattlegroundConstants.SpellSpiritHealChannel
            || holder.RemoveMode == AuraRemoveMode.Death
            || holder.Target is not { IsInWorld: true, IsAlive: true } guide
            || !_recasting.Add(guide.Guid))
        {
            return;
        }

        try
        {
            ReapplyChannel(system, holder, guide);
        }
        finally
        {
            _recasting.Remove(guide.Guid);
        }
    }

    private static void ReapplyChannel(SpellSystem system, SpellAuraHolder holder, Unit guide)
    {
        if (holder.RemoveMode == AuraRemoveMode.Expire
            && !holder.Spell.HasAura(AuraType.PeriodicTriggerSpell)
            && system.Store.Get(SpellSpiritHeal) is not null)
        {
            system.CastSpell(guide, SpellSpiritHeal, SpellCastTargets.ForSelf(), triggered: true);
        }

        if (system.HasAura(guide, BattlegroundConstants.SpellSpiritHealChannel))
        {
            return;
        }

        SpellCastResult result = system.CastSpell(
            guide,
            BattlegroundConstants.SpellSpiritHealChannel,
            SpellCastTargets.ForSelf(),
            triggered: true);
        if (result != SpellCastResult.CastOk && !system.HasAura(guide, BattlegroundConstants.SpellSpiritHealChannel))
        {
            system.AddAura(guide, BattlegroundConstants.SpellSpiritHealChannel, permanent: false);
        }
    }
}
