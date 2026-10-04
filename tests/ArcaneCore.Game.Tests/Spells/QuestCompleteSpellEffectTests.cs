using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Quests.Adapters;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Quests;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_EFFECT_QUEST_COMPLETE (vmangos Spell::EffectQuestComplete, SpellEffects.cpp:5324-5331): the player target is credited
/// <c>AreaExploredOrEventHappens(EffectMiscValue)</c>. 42 event quests of the classic-db data have no other credit source.
/// </summary>
public sealed class QuestCompleteSpellEffectTests
{
    private const uint CreditSpell = 980001;
    private const uint QuestId = 980100;

    private static SpellTestKit Kit(int misc = (int)QuestId) => new(Spell(CreditSpell, Effect(SpellEffectName.QuestComplete, 0, misc: misc)));

    [Fact]
    public void TheModuleIsDiscovered_AndTheEffectHasAHandler()
    {
        using SpellTestKit kit = Kit();
        Assert.Contains(typeof(QuestCompleteSpellEffect), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.QuestComplete));
    }

    [Fact]
    public void CastingItOnAPlayer_ReportsTheMiscValueQuestToTheInstalledSink()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        var seen = new List<(Player, uint)>();
        QuestSpellEvents.Install(kit.System, (p, quest) => seen.Add((p, quest)));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, CreditSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal([(player, QuestId)], seen);
    }

    [Fact]
    public void WithoutASink_CastingItDoesNothing()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, CreditSpell, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void ANonPositiveMiscValue_CreditsNothing()
    {
        using SpellTestKit kit = Kit(misc: 0);
        (Player player, _) = kit.AddPlayer(1);
        var seen = new List<uint>();
        QuestSpellEvents.Install(kit.System, (_, quest) => seen.Add(quest));
        kit.System.CastSpell(player, CreditSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Empty(seen);
    }

    [Fact]
    public void TheSpellSystemCaster_ListsTheQuestsTheSpellStoreCredits()
    {
        using SpellTestKit kit = Kit();
        var caster = new SpellSystemQuestCaster(kit.System);
        Assert.Equal([QuestId], caster.QuestsCompletedBySpells);
    }

    [Fact]
    public void AnEventQuestWithAQuestCompleteSpell_IsNotWithheld_AndAnUncoveredOneIs()
    {
        QuestTemplate Event(uint id) => new()
        {
            Entry = id, Method = 2, QuestLevel = 1, Title = "Event", SpecialFlags = (byte)QuestSpecialFlags.ExplorationOrEvent,
        };
        QuestTemplate covered = Event(QuestId);
        QuestTemplate other = Event(QuestId + 1);
        using var kit = new QuestFlowKit([covered, other], starters: [QuestId, QuestId + 1], spellCaster: new CreditingCaster(QuestId));
        Assert.Equal(QuestAdapter.None, kit.Services.MissingAdapters(kit.Services.Quests.Get(QuestId)!));
        Assert.Equal(QuestAdapter.EventCredit, kit.Services.MissingAdapters(kit.Services.Quests.Get(QuestId + 1)!));
        Assert.True(kit.Accept(QuestId));
        Assert.False(kit.Accept(QuestId + 1));

        // The effect's credit then completes the quest exactly as an area trigger would.
        kit.Services.AreaExploredOrEventHappens(kit.Player, QuestId);
        Assert.Equal(QuestStatus.Complete, kit.State.Quests.GetStatus(QuestId));
    }

    private sealed class CreditingCaster(uint quest) : IQuestSpellCaster
    {
        public bool CastOnSelf(Player player, uint spellId) => true;

        public IReadOnlyCollection<uint> QuestsCompletedBySpells => [quest];
    }
}
