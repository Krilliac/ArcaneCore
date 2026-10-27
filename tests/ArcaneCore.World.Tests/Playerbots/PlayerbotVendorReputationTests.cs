using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The vendors a bot walks to are ones it can use. Live 2026-10-25 (issues #1478 and #1479): bots walked to the Timbermaw Hold vendors
/// 11557 and 11555 and "found no usable service NPC in sight": an Unfriendly creature does not attack, so the bot's approach rule let it
/// in, but vmangos <c>Player::CanInteractWithNPC</c> refuses ranks up to Unfriendly.
/// </summary>
public sealed class PlayerbotVendorReputationTests
{
    [Fact]
    public async Task AnUnfriendlyBot_GetsNoTimbermawVendor_AndOneAtNeutralDoes_ThenLosesItAgainWhenItsRankFalls()
    {
        var fixture = new TimbermawVendorFixture();
        var reactions = new TrainerReactions { Rank = ReputationRank.Unfriendly };
        TimbermawVendorServices.Current.Value = (fixture, reactions);
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    QuestNpcServices services = session.Services.GetRequiredService<QuestNpcFeature>().Services;

                    goals.RefreshRepairSpawns(player, player.MapId, services, NpcFlags.Vendor);
                    Assert.DoesNotContain(goals.VendorServiceSpawns, v => v.Entry == TimbermawVendorFixture.TimbermawVendor);

                    // The rank rises to Neutral: the vendor can be used, and the index (keyed by the bot's rank) is built again.
                    reactions.Rank = ReputationRank.Neutral;
                    goals.RefreshRepairSpawns(player, player.MapId, services, NpcFlags.Vendor);
                    Assert.Contains(goals.VendorServiceSpawns, v => v.Entry == TimbermawVendorFixture.TimbermawVendor);

                    reactions.Rank = ReputationRank.Unfriendly;
                    goals.RefreshRepairSpawns(player, player.MapId, services, NpcFlags.Vendor);
                    Assert.DoesNotContain(goals.VendorServiceSpawns, v => v.Entry == TimbermawVendorFixture.TimbermawVendor);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TimbermawVendorServices.Current.Value = null; }
    }
}

/// <summary>Timbermaw Hold's vendor 11557 (faction 576).</summary>
internal sealed class TimbermawVendorFixture : ICreatureDataStore, INpcContentStore
{
    public const uint TimbermawVendor = 11557;

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with { VendorItems = [new VendorItem { Entry = TimbermawVendor, Item = 117, Slot = 0 }] });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [new CreatureTemplate { Entry = TimbermawVendor, Name = "Timbermaw vendor", Faction = 900021, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 39349, Entry = TimbermawVendor, MapId = 0, X = -8839.95f, Y = -132.49f, Z = 83.53f }],
        [], [], []));
}

internal sealed class TimbermawVendorServices : IWorldTestServices
{
    public static readonly AsyncLocal<(TimbermawVendorFixture Fixture, TrainerReactions Reactions)?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } current) return;
        services.AddSingleton<ICreatureDataStore>(current.Fixture);
        services.AddSingleton<INpcContentStore>(current.Fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton<IPlayerReputation>(current.Reactions);
        // Faction 576 is Timbermaw Hold, a reputation faction; the template ids are the test's own.
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900021, 576, 0, 8, 0, 0)]));
    }
}
