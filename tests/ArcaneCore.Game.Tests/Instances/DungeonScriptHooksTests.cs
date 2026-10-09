using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Instances.Scripts.RazorfenDowns;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The quest-accept, area-trigger and SEND_EVENT hooks the world's DungeonScriptFeature subscribes (<see cref="DungeonScriptHooks"/>), run
/// against real instance maps and script AIs.
/// </summary>
public sealed class DungeonScriptHooksTests
{
    private static (uint, uint, CreatureWaypoint)[] Path(uint entry)
        => [(entry, EscortAI.EscortPathId, new CreatureWaypoint(1, -10f, -383.07f, 61.78f, 0, 0)),
            (entry, EscortAI.EscortPathId, new CreatureWaypoint(2, -5f, -383.07f, 61.78f, 0, 0))];

    private static SpellCast Cast(Unit caster, uint spellId) => new(Spell(spellId), caster, SpellCastTargets.ForSelf(), false, 0, 0, 0);

    [Fact]
    public void QuestAccepted_StartsWillixsEscort_OnlyForHisQuest()
    {
        using DungeonScriptHarness run = new(map => new RazorfenKraulInstance(map), [WillixAi.Entry], [WillixAi.Entry], null, null,
            Path(WillixAi.Entry), null);
        Creature willix = run.Creature(WillixAi.Entry);
        var ai = Assert.IsType<WillixAi>(willix.AI);

        Assert.False(DungeonScriptHooks.OnQuestAccepted(run.Player, willix.Guid, 9999));
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(DungeonScriptHooks.OnQuestAccepted(run.Player, willix.Guid, WillixAi.Quest));
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
    }

    [Fact]
    public void QuestAccepted_StartsBelnistraszsEscort()
    {
        using DungeonScriptHarness run = new(map => new RazorfenDownsInstance(map), [BelnistraszAi.Entry], [BelnistraszAi.Entry], null, null,
            Path(BelnistraszAi.Entry), null);
        Creature belnistrasz = run.Creature(BelnistraszAi.Entry);

        Assert.True(DungeonScriptHooks.OnQuestAccepted(run.Player, belnistrasz.Guid, BelnistraszAi.Quest));

        Assert.True(Assert.IsType<BelnistraszAi>(belnistrasz.AI).HasEscortState(EscortAI.EscortState.Escorting));
    }

    [Fact]
    public void QuestAccepted_MakesKernobeeFollow_AndIgnoresOtherGivers()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [KernobeeAi.Entry, ThermapluggAi.Entry],
            [KernobeeAi.Entry, ThermapluggAi.Entry]);
        Creature kernobee = run.Creature(KernobeeAi.Entry);

        Assert.False(DungeonScriptHooks.OnQuestAccepted(run.Player, run.Creature(ThermapluggAi.Entry).Guid, KernobeeAi.Quest));
        Assert.True(DungeonScriptHooks.OnQuestAccepted(run.Player, kernobee.Guid, KernobeeAi.Quest));
        Assert.False(DungeonScriptHooks.OnQuestAccepted(run.Player, kernobee.Guid, KernobeeAi.Quest)); // already following
        Assert.Equal(MovementGeneratorType.Follow, kernobee.Motion.CurrentType);
    }

    [Fact]
    public void CathedralTrigger_NeedsTheCorruptedAshbringer_ALivingPlayer_AndNoGameMaster()
    {
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [3976], [3976]);
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Player player = run.Player;
        player.Inventory.Templates = new ItemTemplateStore(
            [new ItemTemplate { Entry = ScarletMonasteryInstance.CorruptedAshbringer, Class = 2, Name = "Corrupted Ashbringer", DisplayId = 1, Stackable = 1, Quality = 5 }], []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);

        Assert.False(DungeonScriptHooks.OnAreaTrigger(player, ScarletMonasteryInstance.CathedralTrigger));
        Assert.Equal(EncounterState.NotStarted, data.GetData(ScarletMonasteryInstance.TypeAshbringer));

        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ScarletMonasteryInstance.CorruptedAshbringer, 1, out _));
        player.Flags |= PlayerFlags.Gm;
        Assert.False(DungeonScriptHooks.OnAreaTrigger(player, ScarletMonasteryInstance.CathedralTrigger));
        player.Flags &= ~PlayerFlags.Gm;
        Assert.False(DungeonScriptHooks.OnAreaTrigger(player, ZulFarrakInstance.AntusulTrigger)); // another dungeon's trigger
        Assert.True(DungeonScriptHooks.OnAreaTrigger(player, ScarletMonasteryInstance.CathedralTrigger));

        Assert.Equal(EncounterState.InProgress, data.GetData(ScarletMonasteryInstance.TypeAshbringer));
        Assert.Equal(35u, run.Creature(3976).FactionTemplate);
    }

    [Fact]
    public void AntusulTrigger_SendsAntusulAtThePlayer()
    {
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [ZulFarrakInstance.Antusul], [ZulFarrakInstance.Antusul]);

        Assert.True(DungeonScriptHooks.OnAreaTrigger(run.Player, ZulFarrakInstance.AntusulTrigger));

        Assert.Same(run.Player, run.Creature(ZulFarrakInstance.Antusul).Combat.Victim);
    }

    [Fact]
    public void AltarSpells_StartTheKeeperAndArchaedasEvents_OnlyWhenTheCastCompleted()
    {
        using DungeonScriptHarness run = new(map => new UldamanInstance(map),
            [UldamanInstance.Archaedas, UldamanInstance.StoneKeeper], [UldamanInstance.Archaedas, UldamanInstance.StoneKeeper]);
        var data = Assert.IsType<UldamanInstance>(run.Data);

        Assert.False(DungeonScriptHooks.OnSpellFinished(Cast(run.Player, DungeonScriptHooks.AltarKeeperSpell), completed: false));
        Assert.Equal(EncounterState.NotStarted, data.GetData(UldamanInstance.TypeAltar));
        Assert.False(DungeonScriptHooks.OnSpellFinished(Cast(run.Player, DungeonScriptHooks.UnlockingSpell), completed: true));
        Assert.False(DungeonScriptHooks.OnSpellFinished(Cast(run.Creature(UldamanInstance.StoneKeeper), DungeonScriptHooks.AltarKeeperSpell), true));

        Assert.True(DungeonScriptHooks.OnSpellFinished(Cast(run.Player, DungeonScriptHooks.AltarKeeperSpell), completed: true));
        Assert.Equal(EncounterState.InProgress, data.GetData(UldamanInstance.TypeAltar));
        Assert.Equal(run.Player.Guid.Value, data.GetData64(UldamanInstance.DataEventStarter));
        Assert.True(DungeonScriptHooks.OnSpellFinished(Cast(run.Player, DungeonScriptHooks.AltarArchaedasSpell), completed: true));
        Assert.Equal(EncounterState.Special, data.GetData(UldamanInstance.TypeArchaedas));
    }

    [Fact]
    public void Unlocking_StartsThePyramidOnce_AndRunsEvent2609FromTheReservedRelayBlock()
    {
        uint relay = RelayScriptCatalog.EventRelayId(ZulFarrakInstance.PyramidEvent);
        Assert.Equal(2_000_002_609u, relay);
        var relays = new RelayScriptCatalog(
            [new RelayScriptStep(relay, 60_000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)], []);
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [7604], [7604], relays);
        var data = Assert.IsType<ZulFarrakInstance>(run.Data);

        // event_spell_unlocking is the instance's SEND_EVENT handler now, not a completed-cast hook (one owner of the wave).
        Assert.False(DungeonScriptHooks.OnSpellFinished(Cast(run.Player, DungeonScriptHooks.UnlockingSpell), completed: true));
        Assert.Equal(0, run.Creatures.PendingRelaySteps);

        Assert.True(data.OnSpellEvent(run.Player, ZulFarrakInstance.PyramidEvent));
        Assert.Equal(EncounterState.InProgress, data.GetData(ZulFarrakInstance.TypePyramid));
        Assert.Equal(1, run.Creatures.PendingRelaySteps);

        Assert.True(data.OnSpellEvent(run.Player, ZulFarrakInstance.PyramidEvent));
        Assert.Equal(1, run.Creatures.PendingRelaySteps); // event_spell_unlocking returns true: the DB script does not run again
        Assert.False(data.OnSpellEvent(run.Creature(7604), ZulFarrakInstance.PyramidEvent)); // a creature source gets the DB script
    }
}
