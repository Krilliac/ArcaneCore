using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.SunkenTemple;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Sunken Temple: at_shade_of_eranikus and npc_malfurion_stormrage (mangos-classic sunken_templeScripts.cpp:44-149), through the real
/// instance script and creature system.
/// </summary>
public sealed class SunkenTempleMalfurionTests
{
    private sealed class QuestSeam(HashSet<uint> rewarded) : IInstanceQuestRewards
    {
        public bool? IsRewarded(Player player, uint questId) => rewarded.Contains(questId);
        public void Update(Map map, uint diffMs) { }
        public void OnPlayerRemoved(Map map, Player player) { }
    }

    private static DungeonScriptTestKit NewKit(uint[] rewarded, bool tyrantComplete = false, bool seam = true)
    {
        var texts = new List<CreatureAiText>();
        for (int id = -1109004; id <= -1109000; id++) texts.Add(new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0));
        var kit = new DungeonScriptTestKit(map => new SunkenTempleInstance(map), [SunkenTempleInstance.NpcMalfurion], [], [],
            new CreatureAiContent([], texts),
            questReady: (_, q) => tyrantComplete && q == SunkenTempleInstance.QuestEranikusTyrantOfTheDream);
        if (seam) kit.Map.AddUpdater(new QuestSeam([.. rewarded]));
        return kit;
    }

    private static Creature[] Malfurions(DungeonScriptTestKit kit) => [.. kit.Creatures.Creatures.Where(c => c.Entry == SunkenTempleInstance.NpcMalfurion)];

    private static List<int> Said(DungeonScriptTestKit kit)
        => [.. kit.Sent(WorldOpcode.SmsgMessagechat).Select(p => int.Parse(ParseMonsterChat(p).Message, CultureInfo.InvariantCulture))];

    [Fact]
    public void TheTrigger_AfterTheChargeOfTheDragonflights_SummonsMalfurionOnce_AtHisSpot_AndMarksHimInProgress()
    {
        using DungeonScriptTestKit kit = NewKit([SunkenTempleInstance.QuestChargeOfTheDragonflights]);
        var temple = (SunkenTempleInstance)kit.Script;

        temple.OnAreaTrigger(kit.Player, 9999);
        Assert.Empty(Malfurions(kit));

        temple.OnAreaTrigger(kit.Player, SunkenTempleInstance.AreaTriggerShadeOfEranikus);
        Creature malfurion = Assert.Single(Malfurions(kit));
        Assert.Equal(SunkenTempleInstance.MalfurionSummon.X, malfurion.X, 2);
        Assert.Equal(SunkenTempleInstance.MalfurionSummon.Y, malfurion.Y, 2);
        Assert.Equal(EncounterState.InProgress, temple.GetData(SunkenTempleInstance.TypeMalfurion));
        Assert.IsType<MalfurionStormrageAI>(malfurion.AI);

        temple.OnAreaTrigger(kit.Player, SunkenTempleInstance.AreaTriggerShadeOfEranikus);
        Assert.Single(Malfurions(kit));
    }

    [Theory]
    [InlineData(new uint[0], false, true)] // the pre-quest is not turned in
    [InlineData(new uint[] { SunkenTempleInstance.QuestChargeOfTheDragonflights, SunkenTempleInstance.QuestEranikusTyrantOfTheDream }, false, true)]
    [InlineData(new uint[] { SunkenTempleInstance.QuestChargeOfTheDragonflights }, true, true)] // 8733 is already complete
    [InlineData(new uint[] { SunkenTempleInstance.QuestChargeOfTheDragonflights }, false, false)] // no quest seam on the map
    public void TheTrigger_SummonsNothing_WhenTheQuestStateDoesNotAllowIt(uint[] rewarded, bool tyrantComplete, bool seam)
    {
        using DungeonScriptTestKit kit = NewKit(rewarded, tyrantComplete, seam);
        var temple = (SunkenTempleInstance)kit.Script;
        temple.OnAreaTrigger(kit.Player, SunkenTempleInstance.AreaTriggerShadeOfEranikus);
        Assert.Empty(Malfurions(kit));
        Assert.NotEqual(EncounterState.InProgress, temple.GetData(SunkenTempleInstance.TypeMalfurion));
    }

    [Fact]
    public void Malfurion_ComesWithoutHisQuest_EmotesAndSpeaksInOrder_ThenOffersTheTyrantOfTheDream()
    {
        using DungeonScriptTestKit kit = NewKit([SunkenTempleInstance.QuestChargeOfTheDragonflights]);
        var temple = (SunkenTempleInstance)kit.Script;
        temple.OnAreaTrigger(kit.Player, SunkenTempleInstance.AreaTriggerShadeOfEranikus);
        Creature malfurion = Assert.Single(Malfurions(kit));
        kit.Player.Relocate(malfurion.X + 2f, malfurion.Y, malfurion.Z, 0, 0);
        kit.Tick(50);
        var ai = Assert.IsType<MalfurionStormrageAI>(malfurion.AI);
        uint questGiver = (uint)ArcaneCore.Game.Npc.NpcFlags.QuestGiver;
        Assert.Equal(0u, malfurion.NpcFlags & questGiver);

        for (int i = 0; i < 400 && ai.Speech < MalfurionStormrageAI.MaxTempleSpeeches; i++) kit.Tick(250); // under 100 s
        Assert.Equal(MalfurionStormrageAI.MaxTempleSpeeches, ai.Speech);
        Assert.NotEqual(0u, malfurion.NpcFlags & questGiver);
        Assert.Equal(
            [MalfurionStormrageAI.SayMalfurion1, MalfurionStormrageAI.SayMalfurion2, MalfurionStormrageAI.SayMalfurion3, MalfurionStormrageAI.SayMalfurion4],
            Said(kit).Where(id => id != MalfurionStormrageAI.EmoteMalfurion1));
        Assert.Equal(MalfurionStormrageAI.EmoteStateNone, malfurion.GetUInt32(UpdateFields.UnitNpcEmotestate));
    }
}
