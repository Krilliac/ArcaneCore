using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>
/// Where SPELL_EFFECT_QUEST_COMPLETE reports to: the quest service installs a sink on the spell system it shares with the
/// world (the same shape as the duel service's lookup, no edit of <see cref="SpellSystem"/>).
/// </summary>
public static class QuestSpellEvents
{
    private static readonly ConditionalWeakTable<SpellSystem, Action<Player, uint>> s_sinks = new();

    /// <summary>Route SPELL_EFFECT_QUEST_COMPLETE of <paramref name="spells"/> to <paramref name="sink"/> (player, quest id).</summary>
    public static void Install(SpellSystem spells, Action<Player, uint> sink)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(sink);
        s_sinks.AddOrUpdate(spells, sink);
    }

    /// <summary>The sink installed on <paramref name="spells"/>, or null.</summary>
    public static Action<Player, uint>? For(SpellSystem spells) => s_sinks.TryGetValue(spells, out Action<Player, uint>? sink) ? sink : null;
}

/// <summary>
/// SPELL_EFFECT_QUEST_COMPLETE (16): vmangos Spell::EffectQuestComplete (SpellEffects.cpp:5324-5331) credits
/// <c>AreaExploredOrEventHappens(EffectMiscValue)</c> to a player target. Also the event-credit source of the quests those
/// spells name: an exploration/event quest with such a spell is no longer withheld (<see cref="QuestAdapter.EventCredit"/>).
/// Discovered by the spell system and by the quest service, each through its own interface.
/// </summary>
public sealed class QuestCompleteSpellEffect : ISpellHandlerModule, IQuestAdapterModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.QuestComplete, EffectQuestComplete);
    }

    private static void EffectQuestComplete(SpellEffectContext context)
    {
        if (context.Target is Player player && context.Effect.MiscValue > 0)
        {
            QuestSpellEvents.For(context.System)?.Invoke(player, (uint)context.Effect.MiscValue);
        }
    }

    public QuestAdapter Provides(QuestNpcServices services) => QuestAdapter.None;

    public IEnumerable<uint> EventQuestsCovered(QuestNpcServices services) => services.Deps.SpellCaster?.QuestsCompletedBySpells ?? [];
}
