using ArcaneCore.Data.Content.Import;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Backlog item 43: content produced by the dump importer (not hand-built rows) drives kill reputation. Killing a creature that
/// rewards Booty Bay spills to Stormwind at the imported template rate and scales by the imported creature rate.
/// </summary>
public sealed class ReputationTemplatesImportedSpilloverTests
{
    private const uint Boar = 7201;

    // Synthetic classic-db layout: Booty Bay (21) spills 0.25 into Stormwind (72) up to rank 7; Booty Bay creature_rate is 2.
    private const string Dump = """
        INSERT INTO `reputation_spillover_template` (`faction`,`faction1`,`rate_1`,`rank_1`,`faction2`,`rate_2`,`rank_2`,`faction3`,`rate_3`,`rank_3`,`faction4`,`rate_4`,`rank_4`) VALUES (21,72,0.25,7,0,0,0,0,0,0,0,0,0);
        INSERT INTO `reputation_reward_rate` (`faction`,`quest_rate`,`creature_rate`,`spell_rate`) VALUES (21,1,2,1);
        """;

    [Fact]
    public void KillingACreature_SpillsToTheRelatedFaction_AtTheImportedTemplateRate()
    {
        var importer = new ReputationTemplatesDumpImporter();
        importer.Read(new StringReader(Dump));
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        var service = new ReputationService(Factions, [new(Boar, BootyBay, 0, 7, false, 100, 0, false, 0, TeamDependent: false)],
            sink: new RecordingReputationSink(), roll: () => 0.0)
        {
            Content = ReputationContent.Create(importer.ToContentRows(), Factions),
        };
        service.Track(player, service.Create(player, new CharacterReputationData([], -1)));
        service.BuildInitializeFactions(player);

        var template = new CreatureTemplate { Entry = Boar, Name = "Synthetic boar", Faction = 3, MinLevel = 1, MaxLevel = 1 };
        var spawn = new CreatureSpawn { Guid = 900300, Entry = Boar, MapId = 0, X = 0, Y = 0, Z = 0 };
        service.RewardKill(player, new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1)));

        Assert.Equal(200, service.GetReputation(player, BootyBay)); // 100 * creature_rate 2
        Assert.Equal(50, service.GetReputation(player, Stormwind)); // (int)(200 * 0.25)
    }
}
