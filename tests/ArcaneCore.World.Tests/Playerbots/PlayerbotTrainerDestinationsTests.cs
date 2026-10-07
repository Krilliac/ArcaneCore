using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using System.Buffers.Binary;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ArcaneCore.World.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotTrainerDestinationsTests
{
    [Fact]
    public async Task ConfiguredFactionFileIsTheActualRemoteTrainerCatalog()
    {
        string file = Path.Combine(Path.GetTempPath(), "arcane-faction-test-" + Guid.NewGuid().ToString("N") + ".dbc");
        uint[][] records = [[1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [900011, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]];
        byte[] image = new byte[20 + records.Length * 56 + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 14);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int row = 0; row < records.Length; row++)
            for (int field = 0; field < 14; field++)
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + row * 56 + field * 4), records[row][field]);
        File.WriteAllBytes(file, image);
        var fixture = new FarTrainerFixture();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.RemoveAll<FactionTemplateCatalog>();
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Quests:FactionTemplateDbcPath"] = file }).Build());
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Assert.Null(session.Services.GetService<FactionTemplateCatalog>());
                    Assert.NotNull(session.Services.GetRequiredService<QuestNpcFeature>().FactionTemplates.Find(900011));
                    var player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    session.ManagedBudget = new ManagedActionBudget(1);
                    var route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    Assert.True(route.HasCandidate(player));
                    float start = player.X;
                    Assert.True(route.Update(player, 500));
                    Assert.Equal(start, player.X);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.True(player.X > start);
                    Assert.Equal(FarTrainerFixture.Entry, route.TargetEntry);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; File.Delete(file); }
    }

    [Fact]
    public async Task BrainRetiresIdleTargetBeforeTravellingToUsefulTrainer()
    {
        var fixture = new FarTrainerFixture();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices:
                services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
            try
            {
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    var abandoned = new Creature(94490, new CreatureTemplate { Entry = 299, Name = "idle target" },
                        null, CreatureContent.Empty, new Random(1));
                    abandoned.SetPosition(player.X + 1, player.Y, player.Z, 0);
                    player.Map!.AddObject(abandoned);
                    typeof(PlayerbotBrain).GetField("_target", System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!.SetValue(brain, abandoned);
                    typeof(PlayerbotBrain).GetField("_attacking", System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!.SetValue(brain, true);
                    player.Map!.Combat.SetInCombatState(player, 10_000);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    // Linger now retires optional intent without opening another fight;
                    // the original trainer priority resumes when combat actually ends.
                    Assert.Null(brain.InspectionTarget);
                    Assert.Null(player.Combat.Victim);
                    Assert.Equal(1, session.ManagedBudget.Remaining);
                    player.Map!.Combat.CombatStop(player);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.Null(brain.InspectionTarget);
                    Assert.Equal(PlayerbotGoalKind.Train, brain.Goal);
                    float start = player.X;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.True(player.X > start);
                    Assert.Equal(FarTrainerFixture.Entry, brain.TargetEntry);
                    return true;
                });
            }
            finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task VisibleUsefulTrainer_IsLeftForTownGoals()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "trainer visible");
                bool moved = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    return new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true }).Update(player, 500);
                });
                Assert.False(moved);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task CombatAndGhostPlayersNeverRoute()
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "trainer visible");
                bool[] results = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var destinations = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    player.UnitFlags |= UnitFlags.InCombat;
                    bool combat = destinations.Update(player, 500);
                    player.UnitFlags &= ~UnitFlags.InCombat;
                    player.Flags |= PlayerFlags.Ghost;
                    bool ghost = destinations.Update(player, 500);
                    return new[] { combat, ghost };
                });
                Assert.All(results, Assert.False);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task MissingNpcMetadata_FailsClosedWithoutMovement()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            bool result = await host.OnWorldAsync(() =>
                new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true })
                    .Update(session.Player!, 500));
            Assert.False(result);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task RemoteTrainerHint_UsesNormalMovementWhenOutsideVisibility()
    {
        var fixture = new FarTrainerFixture();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                bool moved = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    session.ManagedBudget = new ManagedActionBudget(1);
                    var route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    Assert.DoesNotContain(player.VisibleObjects, guid => player.Map!.FindObject(guid) is Creature c && c.Entry == FarTrainerFixture.Entry);
                    bool result = route.Update(player, 500);
                    Assert.Equal(FarTrainerFixture.Entry, route.TargetEntry);
                    Assert.InRange(player.X, -8950.0f, -8949.9f);
                    Assert.Equal(0, session.ManagedBudget.Remaining);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.InRange(player.X, -8950.1f, -8944.0f);
                    float after = player.X;
                    player.Money = 0;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(route.Update(player, 500));
                    Assert.Equal(after, player.X);
                    Assert.Equal(0u, route.TargetEntry);
                    return result;
                });
                Assert.True(moved);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task UnaffordableTrainerSpell_IsNotAStaticRouteCandidate()
    {
        var fixture = new FarTrainerFixture();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                bool result = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 0;
                    return new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true }).Update(player, 500);
                });
                Assert.False(result);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task ReputationTrainerTravelRechecksReactionBeforeEachMovement()
    {
        var fixture = new FarTrainerFixture();
        var reactions = new TrainerReactions();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IPlayerReputation>(reactions);
                services.AddSingleton(new FactionTemplateCatalog([
                    new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                    new FactionTemplateRecord(900011, 72, 0, 8, 0, 0)]));
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    var player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    var route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    float position = player.X;
                    reactions.Rank = ReputationRank.Hostile;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(route.Update(player, 500));
                    Assert.Equal(position, player.X);
                    Assert.Equal(0u, route.TargetEntry);
                    Assert.Equal(1, session.ManagedBudget.Remaining);
                    reactions.Rank = ReputationRank.Friendly;
                    Assert.True(route.Update(player, 500));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task BlockedNearestTrainer_RotatesToReachableSameEntry()
    {
        var fixture = new FarTrainerFixture { TwoSpawns = true };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new EastBlockedPathfinder());
                    Player player = session.Player!;
                    player.Money = 100;
                    var route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(route.Update(player, 500));
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.Equal(-8949.95f, player.X, 2);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.True(player.X < -8949.95f);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }
}

internal sealed class FarTrainerServices : IWorldTestServices
{
    public static readonly AsyncLocal<FarTrainerFixture?> Current = new();
    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<INpcTemplateServiceMetadataSource>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}

internal sealed class FarTrainerWorldData : IWorldDataStore
{
    private readonly InMemoryWorldDataStore _inner = new();
    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<StartPosition?>(new(0, 1, -8949.95f, -132.49f, 83.53f, 0));
    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => _inner.GetRaceInfoAsync(race, gender, cancellationToken);
    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => _inner.GetClassInfoAsync(cls, cancellationToken);
    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default) => Task.FromResult(true);
}

internal sealed class FarTrainerFixture : ICreatureDataStore, INpcContentStore, INpcTemplateServiceMetadataSource, ISpellContentStore
{
    public const uint Entry = 944;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 94401);
    public bool TwoSpawns { get; init; }
    public Task<IReadOnlyList<NpcTemplateServiceMetadata>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<NpcTemplateServiceMetadata>>([new() { Entry = Entry, TrainerType = 0, TrainerClass = 1 }]);
    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with { TrainerSpells = [new TrainerSpell { Entry = Entry, Spell = 944010, SpellCost = 1 }] });
    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent([new CreatureTemplate { Entry = Entry, Name = "far trainer", Faction = 900011, NpcFlags = (uint)NpcFlags.Trainer, DisplayIds = [49] }], TwoSpawns ? [new CreatureSpawn { Guid = 94401, Entry = Entry, MapId = 0, X = -8649.95f, Y = -132.49f, Z = 83.53f }, new CreatureSpawn { Guid = 94402, Entry = Entry, MapId = 0, X = -9249.95f, Y = -132.49f, Z = 83.53f }] : [new CreatureSpawn { Guid = 94401, Entry = Entry, MapId = 0, X = -8649.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
    Task<SpellContent> ISpellContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SpellContent([new SpellTemplateRow { Id = 944010, SpellName = "teach", SpellLevel = 1, Effect1 = (uint)SpellEffectName.LearnSpell, EffectTriggerSpell1 = 944011, EffectImplicitTargetA1 = (uint)SpellImplicitTarget.UnitCaster }, new SpellTemplateRow { Id = 944011, SpellName = "learned", SpellLevel = 1 }], [], [], [], [], [], []));
    Task ISpellContentStore.ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FlatFloor : ILineOfSight
{
    public bool Enabled => true;
    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
}

internal sealed class OpenPathfinder : IPathfinder
{
    public bool Enabled => true;
    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.StraightLine(start, end, PathType.Normal);
}

internal sealed class EastBlockedPathfinder : IPathfinder
{
    public bool Enabled => true;
    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => end.X > start.X ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
}

internal sealed class TrainerReactions : IPlayerReputation, INpcReactionSource
{
    public ReputationRank Rank { get; set; } = ReputationRank.Friendly;
    public int GetReputation(Player player, uint factionId) => 0;
    public byte GetReputationRank(Player player, uint factionId) => (byte)Rank;
    public float GetPriceDiscount(Player player, NpcInfo npc) => 1;
    public bool TryGetNpcReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord self, out ReputationRank reaction)
    { reaction = Rank; return true; }
}
