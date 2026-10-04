using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Accept side effects that retail attaches to a few quests: the source spell is cast on the player once the quest is in the
/// log (vmangos QuestHandler.cpp:202-203) and a PvP quest (type 41) flags the player (Player.cpp:12866-12867).
/// </summary>
public sealed class QuestAcceptEffectTests
{
    private const uint Id = 950001;
    private const uint Spell = 8326;

    private static QuestTemplate SpellQuest(byte minLevel = 1) => new()
    {
        Entry = Id, Method = 2, QuestLevel = 1, MinLevel = minLevel, Title = "Source spell", SrcSpell = Spell,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1,
    };

    [Fact]
    public void AcceptingAQuestWithASourceSpell_CastsItOnThePlayerAfterTheQuestIsLogged()
    {
        var caster = new RecordingCaster();
        using var kit = new QuestFlowKit([SpellQuest()], starters: [Id], spellCaster: caster);
        caster.OnCast = () => Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));
        Assert.True(kit.Accept(Id));
        Assert.Equal([(kit.Player, Spell)], caster.Casts);
    }

    [Fact]
    public void SourceSpellIsNotCast_WhenTheAcceptIsRefused()
    {
        var caster = new RecordingCaster();
        using var kit = new QuestFlowKit([SpellQuest(minLevel: 20)], starters: [Id], spellCaster: caster);
        Assert.False(kit.Accept(Id));
        Assert.Empty(caster.Casts);
    }

    [Fact]
    public void WithoutASpellCaster_TheSourceSpellQuestIsWithheld()
    {
        using var kit = new QuestFlowKit([SpellQuest()], starters: [Id]);
        Assert.Equal(QuestAdapter.SrcSpell, kit.Services.MissingAdapters(kit.Services.Quests.Get(Id)!));
        Assert.False(kit.Accept(Id));
        using var with = new QuestFlowKit([SpellQuest()], starters: [Id], spellCaster: new RecordingCaster());
        Assert.Equal(QuestAdapter.None, with.Services.MissingAdapters(with.Services.Quests.Get(Id)!));
    }

    [Fact]
    public void PvpQuest_SetsThePvpFlagAndItsTimer_AndAnOrdinaryQuestDoesNot()
    {
        using var kit = new QuestFlowKit([QuestFlowKit.Task(Id, type: 41), QuestFlowKit.Task(Id + 1)], starters: [Id, Id + 1]);
        Assert.True(kit.Accept(Id + 1));
        Assert.Equal(0u, (uint)(kit.Player.UnitFlags & UnitFlags.Pvp));
        Assert.True(kit.Accept(Id));
        Assert.NotEqual(0u, (uint)(kit.Player.UnitFlags & UnitFlags.Pvp));
        Assert.True(kit.Player.Combat.PvpFlagTimer > 0);
    }

    [Fact]
    public void LootSourceQuest_IsOfferedBecauseHasQuestForItemAlreadyImplementsItsOnlyRetailUse()
    {
        QuestTemplate quest = QuestFlowKit.Task(Id, reqSource: ItemTestData.ToughJerky, reqSourceCount: 2);
        using var kit = new QuestFlowKit([quest], starters: [Id]);
        Assert.True(kit.Services.Supported(kit.Services.Quests.Get(Id)!));
        Assert.True(kit.Accept(Id));
        // vmangos Player::HasQuestForItem (Player.cpp:14298-14316): the source item drops while fewer than ReqSourceCount are owned.
        Assert.True(kit.Services.HasQuestForItem(kit.Player, ItemTestData.ToughJerky, inRaidGroup: false));
    }

    private sealed class RecordingCaster : IQuestSpellCaster
    {
        public List<(Player Player, uint Spell)> Casts { get; } = [];

        public Action? OnCast { get; set; }

        public bool CastOnSelf(Player player, uint spellId)
        {
            OnCast?.Invoke();
            Casts.Add((player, spellId));
            return true;
        }
    }
}
