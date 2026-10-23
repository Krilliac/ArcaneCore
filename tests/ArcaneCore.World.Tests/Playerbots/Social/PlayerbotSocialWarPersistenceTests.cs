using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Both layers of a relation (the normal one and the War Mode one) reach the relation store through the feature's own checkpoint and stop, and come
/// back after a restart; War Mode switched off at run time leaves decisions on the normal layer, and ClearOnDisable wipes the dormant layer at the
/// next checkpoint. The notes are made on the memory (the events themselves are covered by the trigger tests); what is under test is the feature's
/// save and load wiring.
/// </summary>
public sealed class PlayerbotSocialWarPersistenceTests
{
    private const ulong Rival = 0x5151;

    private static WorldTestHost Start(InMemoryAccountStore accounts, InMemoryCharacterStore characters, ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners,
        PlayerbotSocialMemoryPersistenceTests.MemoryRelationStore store, bool war)
        => ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: options => options.Social.WarMode.Enabled = war,
            extraServices: services => services.AddSingleton<IPlayerbotRelationStore>(store));

    /// <summary>A relation with a trade (normal +3, war +3) and two rude emotes (normal -5 each, war -15 each): the layers have diverged.</summary>
    private static void NoteBoth(ManagedPlayerbotFeature feature, Guid bot, int characterId)
    {
        feature.Social.Memory.Note(bot, characterId, Rival, false, PlayerbotSocialEvent.TradeCompleted);
        feature.Social.Memory.Note(bot, characterId, Rival, false, PlayerbotSocialEvent.RudeEmote);
        feature.Social.Memory.Note(bot, characterId, Rival, false, PlayerbotSocialEvent.RudeEmote);
    }

    [Fact]
    public async Task BothLayers_AreSavedWhenTheBotStops_AndLoadedAfterARestart_WhetherOrNotWarModeIsStillOn()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new PlayerbotSocialMemoryPersistenceTests.MemoryRelationStore();
        Guid id;
        PlayerbotRelation before;
        await using (WorldTestHost first = Start(accounts, characters, owners, store, war: true))
        {
            var feature = PartyTestHost.Feature(first);
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Warpersist", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            NoteBoth(feature, id, (await owners.FindAsync(id))!.CharacterId);
            before = feature.Social.Memory.Snapshot(id).Single();
            await feature.ShutdownBeforeWorldStopAsync();
        }

        // The normal layer follows the normal rules (3 - 5 - 5); the war layer holds the grudge three times as hard; they differ.
        Assert.Equal((short)-7, before.Affinity);
        Assert.True(before.WarAffinity < before.Affinity, $"war {before.WarAffinity} normal {before.Affinity}");
        Assert.True(before.WarFlags.HasFlag(PlayerbotRelationFlags.WarSeeded));
        PlayerbotRelation saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(before, saved);

        // A restart with War Mode still on: both layers are back, and the decisions read the war one.
        await using (WorldTestHost second = Start(accounts, characters, owners, store, war: true))
        {
            var restarted = PartyTestHost.Feature(second);
            await restarted.StartupAsync(default);
            Assert.True(restarted.Social.Memory.TryGet(id, Rival, out PlayerbotRelation loaded));
            Assert.Equal(before, restarted.Social.Memory.Snapshot(id).Single()); // the stored row, exact values and anchors included, comes back unchanged
            Assert.Equal(before.WarAffinity, restarted.Social.Memory.EffectiveAffinity(loaded));
        }

        // A restart with War Mode off: the same row, but the decisions read the normal layer; the war layer is still there for the next war.
        await using WorldTestHost third = Start(accounts, characters, owners, store, war: false);
        var calm = PartyTestHost.Feature(third);
        await calm.StartupAsync(default);
        Assert.True(calm.Social.Memory.TryGet(id, Rival, out PlayerbotRelation dormant));
        Assert.Equal(before, calm.Social.Memory.Snapshot(id).Single());
        Assert.Equal(before.Affinity, calm.Social.Memory.EffectiveAffinity(dormant));
        Assert.Equal(before.WarAffinity, dormant.WarAffinity);
    }

    [Fact]
    public async Task WarModeSwitchedOffAtRunTime_ReadsTheNormalLayerAtOnce_AndClearOnDisableWipesTheWarLayerAtTheNextCheckpoint()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new PlayerbotSocialMemoryPersistenceTests.MemoryRelationStore();
        await using WorldTestHost host = Start(accounts, characters, owners, store, war: true);
        var feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Warswitch", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        NoteBoth(feature, id, (await owners.FindAsync(id))!.CharacterId);
        PlayerbotRelation noted = feature.Social.Memory.Snapshot(id).Single();
        PlayerbotWarModeOptions war = PartyTestHost.PlayerbotOptionsOf(host).Social.WarMode;

        // The checkpoint writes both layers while the bot keeps running.
        await PlayerbotSocialTriggerWorldTests.UntilAsync(() => store.Replaces > 0, "the checkpoint to write the relation", seconds: 30);
        Assert.Equal(noted, Assert.Single(await store.LoadAllAsync()));
        Assert.Equal(noted.WarAffinity, feature.Social.Memory.EffectiveAffinity(noted));

        // Switched off live: the very next read is the normal layer, which was maintained all along; the war layer stays dormant.
        war.Enabled = false;
        Assert.Equal(noted.Affinity, feature.Social.Memory.EffectiveAffinity(feature.Social.Memory.Snapshot(id).Single()));
        Assert.Equal(noted.WarAffinity, feature.Social.Memory.Snapshot(id).Single().WarAffinity);
        war.Enabled = true;
        Assert.Equal(noted.WarAffinity, feature.Social.Memory.EffectiveAffinity(feature.Social.Memory.Snapshot(id).Single())); // and it comes back

        // Switched off for good with ClearOnDisable: the next checkpoint (which saw War Mode on, so the layer is live) wipes the war layer and saves that.
        war.ClearOnDisable = true;
        war.Enabled = false;
        int writes = store.Replaces;
        await PlayerbotSocialTriggerWorldTests.UntilAsync(() => store.Replaces > writes, "the checkpoint after War Mode went off", seconds: 30);
        PlayerbotRelation cleared = Assert.Single(await store.LoadAllAsync());
        Assert.Equal((short)0, cleared.WarAffinity);
        Assert.False(cleared.WarFlags.HasFlag(PlayerbotRelationFlags.WarSeeded));
        Assert.Equal(noted.Affinity, cleared.Affinity); // the normal layer is untouched by all of it
        Assert.Equal(noted.TrustPermille, cleared.TrustPermille);
        Assert.Equal(noted.Familiarity, cleared.Familiarity);
    }
}
