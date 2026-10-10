using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Mouth of Kel'Thuzad per attacked zone and the persisted Stormwind/Undercity city attacks, including a restart.</summary>
public sealed class ScourgeInvasionChoreographyTests
{
    private sealed class StateStore : IScourgeInvasionStateStore
    {
        public ScourgeInvasionSnapshot State { get; set; } = ScourgeInvasionSnapshot.Disabled;
        public int Claims { get; private set; }
        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            if (State.State == ScourgeInvasionState.Enabled) return Task.FromResult(false);
            State = new ScourgeInvasionSnapshot(ScourgeInvasionState.Enabled, 0, 0,
                ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, z.Necropolises, 0)).ToArray());
            return Task.FromResult(true);
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = ScourgeInvasionSnapshot.Disabled;
            return Task.CompletedTask;
        }
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> ClaimCityAttackAsync(uint zoneId, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            Assert.InRange(nextAttackSeconds, ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds);
            if (!State.IsCityAttackDue(zoneId, nowUnix)) return Task.FromResult(false);
            Claims++;
            State = State with
            {
                Cities = State.Cities.Select(c => c.ZoneId == zoneId ? c with { NextAttackUnix = nowUnix + nextAttackSeconds } : c).ToArray(),
            };
            return Task.FromResult(true);
        }
        public List<uint> CityDefeats { get; } = [];
        public Task<bool> CityAttackDefeatedAsync(uint zoneId, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            Assert.InRange(nextAttackSeconds, ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds);
            if (State.State != ScourgeInvasionState.Enabled) return Task.FromResult(false);
            CityDefeats.Add(zoneId);
            State = State with
            {
                Cities = State.Cities.Select(c => c.ZoneId == zoneId ? c with { NextAttackUnix = nowUnix + nextAttackSeconds } : c).ToArray(),
            };
            return Task.FromResult(true);
        }
        public void ZoneDefeated(uint zoneId) => State = State with
        {
            Zones = State.Zones.Select(z => z.ZoneId == zoneId ? z with { Remaining = 0 } : z).ToArray(),
        };
    }

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(17, 0, 525600, 1, 0, 0, "Scourge Invasion"),
                 .. Enumerable.Range(90, 10).Select(id => new GameEventRecord((uint)id, 0, 525600, 1, 0, 0, $"Scourge {id}"))],
                [], [], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        public WorldRuntime World { get; }
        public ScourgeInvasionFeature Invasion { get; }
        public long Now { get; set; } = 1_800_000_000;

        public Rig(StateStore store, bool withMaps = true, ICreatureSpellCaster? spells = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddScoped<IScourgeInvasionStateStore>(sp => sp.GetRequiredService<StateStore>());
            services.AddScoped<IGameEventDataStore, Events>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new ScourgeInvasionFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<ScourgeInvasionFeature>.Instance));
            _provider = services.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            if (withMaps)
            {
                CreatureTemplate[] templates =
                [
                    Template(ScourgeInvasionCatalog.MouthOfKelThuzad, "Mouth of Kel'Thuzad"),
                    Template(ScourgeInvasionCatalog.PallidHorror, "Pallid Horror"),
                    Template(ScourgeInvasionCatalog.PatchworkTerror, "Patchwork Terror"),
                    Template(ScourgeInvasionCatalog.Flameshocker, "Flameshocker"),
                    Template(ScourgeInvasionCatalog.CultistEngineer, "Cultist Engineer"),
                    Template(ScourgeInvasionCatalog.ShadowOfDoom, "Shadow of Doom"),
                    Template(ScourgeInvasionCatalog.DamagedNecroticShard, "Damaged Necrotic Shard"),
                    Template(68, "Stormwind City Guard") with { Faction = 11 },
                ];
                int[] textIds = [.. ScourgeInvasionCatalog.PallidYells, .. ScourgeInvasionCatalog.MouthZoneStartYells,
                    .. ScourgeInvasionCatalog.MouthZoneEndYells, .. ScourgeInvasionCatalog.MouthRandomYells,
                    ScourgeInvasionCatalog.BolvarCastleDefended, ScourgeInvasionCatalog.SylvanasCourtDefended];
                var ai = new CreatureAiContent([], [], new BroadcastTextCatalog(textIds.Select(id =>
                    new BroadcastText((uint)id, $"text {id}", "", 1, 0, 0, [], []))));
                (uint, uint, CreatureWaypoint)[] paths =
                [
                    .. new[] { ScourgeInvasionCatalog.PallidHorror, ScourgeInvasionCatalog.PatchworkTerror }
                        .SelectMany(entry => Enumerable.Range(0, 4).SelectMany(path => Enumerable.Range(1, 3).Select(point =>
                            (entry, (uint)path, new CreatureWaypoint((uint)point, -8578f + point * 10, 886f, 87.3f, 0, 0))))),
                ];
                foreach (uint mapId in new uint[] { 0, 1 })
                {
                    Map map = World.GetMap(mapId);
                    map.AddUpdater(new CreatureMapSystem(map, new CreatureContent(templates, [], [], [], [], ai, paths),
                        aiServices: spells is null ? null : new CreatureAiServices { Spells = spells }));
                    map.AddUpdater(new GameObjectMapSystem(map, new GameObjectContent(
                    [
                        new GameObjectTemplate { Entry = ScourgeInvasionCatalog.SummonCircle, Type = 5, DisplayId = 1, Name = "Circle", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] },
                        new GameObjectTemplate { Entry = ScourgeInvasionCatalog.SummonerShield, Type = 5, DisplayId = 2, Name = "Summoner Shield", Size = 1f, Data = new uint[GameObjectTemplate.DataCount] },
                    ], [], [], [], [])));
                }
            }
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            Invasion = _provider.GetRequiredService<ScourgeInvasionFeature>();
            Invasion.Random = new Random(20261010);
            Invasion.NowUnix = () => Now;
            Invasion.Attach(World);
        }

        public RecordingSession AddPlayer(uint mapId, float x, float y, float z)
        {
            var session = new RecordingSession();
            var player = new Player(new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
                Gender = (byte)Gender.Male, Level = 60, MapId = mapId, X = x, Y = y, Z = z,
            }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
            World.AddPlayer(player);
            LastPlayer = player;
            return session;
        }

        public Player? LastPlayer { get; private set; }

        public GameEventFeature Events => _provider.GetRequiredService<GameEventFeature>();

        public IEnumerable<Creature> Living(uint entry) => World.Maps
            .Select(m => m.FindUpdater<CreatureMapSystem>()).OfType<CreatureMapSystem>()
            .SelectMany(s => s.Creatures).Where(c => c.Entry == entry && c.IsAlive && c.IsInWorld);

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }

        private static CreatureTemplate Template(uint entry, string name) => new()
        {
            Entry = entry, Name = name, MinLevel = 60, MaxLevel = 60, DisplayIds = [16000], Faction = 21,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        };
    }

    [Fact]
    public void MouthsStandInAttackedZonesAndCitiesAreAttackedOnTheirSavedTimerAcrossARestart()
    {
        var store = new StateStore();
        using (var rig = new Rig(store))
        {
            rig.World.RunTick(5_000);
            Assert.Empty(rig.Invasion.Mouths);
            Assert.Empty(rig.Invasion.CityAttackers);

            Assert.True(rig.Events.Service!.StartEvent(17));
            rig.World.RunTick(5_000);
            Assert.Equal(ScourgeInvasionCatalog.Zones.Select(z => z.ZoneId).Order(), rig.Invasion.Mouths.Keys.Order());
            Assert.Equal(6, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());
            Creature azsharaMouth = rig.Invasion.Mouths[16];
            Assert.Equal(1u, azsharaMouth.Map!.MapId);
            Assert.Equal(3273.75f, azsharaMouth.X, 0.01f);

            Assert.Equal([1497u, 1519u], rig.Invasion.CityAttackers.Keys.Order());
            Assert.All(rig.Invasion.CityAttackers.Values, c => Assert.Contains(c.Entry,
                new[] { ScourgeInvasionCatalog.PallidHorror, ScourgeInvasionCatalog.PatchworkTerror }));
            Assert.Equal(2, store.Claims);
            Creature stormwind = rig.Invasion.CityAttackers[1519];
            long next = store.State.NextCityAttack(1519);
            Assert.InRange(next, rig.Now + 2700, rig.Now + 3600);

            rig.World.RunTick(5_000); // not due again: no second summon, mouths not duplicated
            Assert.Equal(2, store.Claims);
            Assert.Same(stormwind, rig.Invasion.CityAttackers[1519]);
            Assert.Equal(6, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());

            store.ZoneDefeated(16);
            rig.World.RunTick(5_000);
            rig.World.RunTick(5_000);
            Assert.False(rig.Invasion.Mouths.ContainsKey(16));
            Assert.DoesNotContain(rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad), c => c.Map!.MapId == 1 && c.X > 3000 && c.X < 3500);
            Assert.Equal(5, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());

            rig.Now = next;
            int due = store.State.Cities.Count(c => c.NextAttackUnix <= next);
            rig.World.RunTick(5_000); // due: the old attacker is replaced, not doubled
            Assert.Equal(2 + due, store.Claims);
            Assert.NotSame(stormwind, rig.Invasion.CityAttackers[1519]);
            Assert.False(stormwind.IsInWorld && stormwind.IsAlive);
            Assert.Equal(2, rig.Living(ScourgeInvasionCatalog.PallidHorror).Count() + rig.Living(ScourgeInvasionCatalog.PatchworkTerror).Count());
        }

        // Restart: the saved state comes back, the five still-attacked zones get their Mouth again, and the capitals wait for their timers.
        int claims = store.Claims;
        using (var restarted = new Rig(store))
        {
            restarted.Now = 1_800_000_000 + 1;
            restarted.World.RunTick(5_000);
            Assert.Equal(5, restarted.Invasion.Mouths.Count);
            Assert.False(restarted.Invasion.Mouths.ContainsKey(16));
            Assert.Equal(claims, store.Claims);
            Assert.Empty(restarted.Invasion.CityAttackers);

            restarted.Now = store.State.Cities.Min(c => c.NextAttackUnix);
            restarted.World.RunTick(5_000);
            Assert.InRange(store.Claims, claims + 1, claims + 2);
            Assert.Equal(store.Claims - claims, restarted.Invasion.CityAttackers.Count);

            Assert.True(restarted.Events.Service!.StopEvent(17));
            restarted.World.RunTick(5_000);
            restarted.World.RunTick(5_000);
            Assert.Equal(ScourgeInvasionState.Disabled, store.State.State);
            Assert.Empty(restarted.Invasion.Mouths);
            Assert.Empty(restarted.Invasion.CityAttackers);
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.MouthOfKelThuzad));
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.PallidHorror));
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.PatchworkTerror));
        }
    }

    internal sealed class RecordingSession : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public int Chat { get; private set; }
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgMessagechat) Chat++;
        }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    [Fact]
    public void CityAttackerWalksItsEntryPathWithFlameshockersAndItsDeathSavesTheNextAttack()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(1_000);
        Creature attacker = rig.Invasion.CityAttackers[1519];
        Assert.Equal(MovementGeneratorType.Waypoint, attacker.Motion.CurrentType);
        var ai = Assert.IsType<PallidHorrorAi>(attacker.AI);
        Assert.Equal(1519u, ai.ZoneId);
        Assert.InRange(ai.Flameshockers.Count, 5, 9);
        Assert.All(ai.Flameshockers, f =>
        {
            Assert.True(f.IsAlive);
            Assert.Equal(MovementGeneratorType.Follow, f.Motion.CurrentType);
            Assert.IsType<FlameshockerAi>(f.AI);
        });
        Creature[] shockers = [.. ai.Flameshockers];

        rig.Now += 60;
        attacker.Map!.FindUpdater<CreatureMapSystem>()!.KillCreature(attacker);
        Assert.All(shockers, f => Assert.False(f.IsAlive));
        Assert.Equal([1519u], store.CityDefeats);
        Assert.InRange(store.State.NextCityAttack(1519), rig.Now + 2700, rig.Now + 3600);
        Assert.False(rig.Invasion.CityAttackers.ContainsKey(1519));
        int claims = store.Claims;
        rig.World.RunTick(5_000); // the defended capital waits for its saved timer
        Assert.Equal(claims, store.Claims);
        Assert.False(rig.Invasion.CityAttackers.ContainsKey(1519));
    }

    [Fact]
    public void MouthsYellTheirZoneStartAndEndToPlayersOfTheirZone()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        ScourgeInvasionPosition azshara = ScourgeInvasionCatalog.MouthPositions[16];
        RecordingSession watcher = rig.AddPlayer(1, azshara.X + 5, azshara.Y, azshara.Z);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(1_000);
        Assert.All(rig.Invasion.Mouths.Values, m => Assert.IsType<ScourgeMouthAi>(m.AI));
        // Without terrain every map-1 point reads zone 0, so the three Kalimdor Mouths all reach the watcher once.
        Assert.Equal(3, watcher.Chat);
        Creature mouth = rig.Invasion.Mouths[16];
        var mouthAi = Assert.IsType<ScourgeMouthAi>(mouth.AI);
        CreatureMapSystem kalimdor = mouth.Map!.FindUpdater<CreatureMapSystem>()!;
        MovementGeneratorType before = mouth.Motion.CurrentType;
        Assert.False(kalimdor.StartEntryWaypointPath(mouth, 0)); // the Mouth's entry has no path: nothing changes
        Assert.Equal(before, mouth.Motion.CurrentType);
        Assert.Equal(16u, mouthAi.ZoneId);

        store.ZoneDefeated(16);
        rig.World.RunTick(5_000);
        Assert.Equal(4, watcher.Chat); // one end yell
        Assert.True(mouthAi.Ended);
        Assert.False(mouth.IsInWorld && mouth.IsAlive);
        rig.World.RunTick(5_000);
        Assert.Equal(4, watcher.Chat);
    }

    [Fact]
    public void AnUnloadedCapitalDoesNotSpendItsTimer()
    {
        var store = new StateStore();
        using var rig = new Rig(store, withMaps: false);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(5_000);
        Assert.Equal(0, store.Claims);
        Assert.Empty(rig.Invasion.Mouths);
        Assert.All(store.State.Cities, c => Assert.Equal(0, c.NextAttackUnix));
    }

    [Fact]
    public void MilestoneOneHundredFiftyStopsTheMouthsButCitiesKeepTheirTimer()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        Assert.Equal(6, rig.Invasion.Mouths.Count);
        store.State = store.State with { BattlesWon = 150 };
        rig.World.RunTick(5_000);
        rig.World.RunTick(5_000);
        Assert.Empty(rig.Invasion.Mouths);
        Assert.Equal(2, rig.Invasion.CityAttackers.Count); // mangos-classic city attacks run while the state is enabled
    }

    private static (Creature Shard, CreatureMapSystem Creatures, GameObjectMapSystem Objects) Camp(Rig rig)
    {
        rig.World.RunTick(0); // the feature installs its AIs on the first world command
        Map map = rig.World.GetMap(0);
        CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
        GameObjectMapSystem objects = map.FindUpdater<GameObjectMapSystem>()!;
        Assert.NotNull(objects.Summon(ScourgeInvasionCatalog.SummonCircle, -8000f, 500f, 100f, 0f));
        Creature shard = creatures.SummonForInstance(ScourgeInvasionCatalog.DamagedNecroticShard, -8000f, 500f, 100f, 0f)!;
        return (shard, creatures, objects);
    }

    [Fact]
    public void DamagedShardButtressPlacesFourShieldedChannellingCultistsAndADeadCultistDropsItsShield()
    {
        using var rig = new Rig(new StateStore());
        (Creature shard, CreatureMapSystem creatures, GameObjectMapSystem objects) = Camp(rig);
        Assert.IsType<NecroticShardAi>(shard.AI);
        shard.Health = shard.MaxHealth / 2;
        rig.World.RunTick(5_000);
        Assert.Equal(shard.MaxHealth, shard.Health);
        Creature[] cultists = [.. rig.Living(ScourgeInvasionCatalog.CultistEngineer)];
        Assert.Equal(4, cultists.Length);
        Assert.All(cultists, c => Assert.InRange(MathF.Sqrt((c.X + 8000f) * (c.X + 8000f) + (c.Y - 500f) * (c.Y - 500f)), 6.7f, 7.0f));
        Assert.Equal(4, objects.GameObjects.Count(g => g.Entry == ScourgeInvasionCatalog.SummonerShield));
        rig.World.RunTick(1_000);
        Assert.All(cultists, c => Assert.True(Assert.IsType<CultistEngineerAi>(c.AI).Channelling));

        creatures.KillCreature(cultists[0]);
        Assert.Equal(3, objects.GameObjects.Count(g => g.Entry == ScourgeInvasionCatalog.SummonerShield));

        // The next hourly buttress replaces the cultists and shields instead of stacking them.
        Assert.Equal(4, ScourgeButtress.Run(shard, creatures));
        Assert.Equal(4, rig.Living(ScourgeInvasionCatalog.CultistEngineer).Count());
        Assert.Equal(4, objects.GameObjects.Count(g => g.Entry == ScourgeInvasionCatalog.SummonerShield));
    }

    [Fact]
    public void EightNecroticRunesDisruptACultistIntoAShadowOfDoomThatAttacksItsSummoner()
    {
        using var rig = new Rig(new StateStore());
        (Creature shard, _, GameObjectMapSystem objects) = Camp(rig);
        rig.World.RunTick(5_000);
        Creature cultist = rig.Living(ScourgeInvasionCatalog.CultistEngineer).First();
        rig.AddPlayer(0, cultist.X + 3f, cultist.Y, cultist.Z);
        Player player = rig.LastPlayer!;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Templates = new ItemTemplateStore(
            [new ItemTemplate { Entry = ScourgeInvasionCatalog.NecroticRune, Class = 12, Name = "Necrotic Rune", DisplayId = 1, Stackable = 250 }], []);

        var gossip = new CultistEngineerGossip(_ => null, new Random(1));
        Assert.Null(CultistEngineerGossip.Disrupt(player, cultist, new Random(1))); // no runes
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(ScourgeInvasionCatalog.NecroticRune, 7, out _));
        Assert.Null(CultistEngineerGossip.Disrupt(player, cultist, new Random(1))); // seven are not enough
        Assert.True(cultist.IsAlive);
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(ScourgeInvasionCatalog.NecroticRune, 3, out _));

        Assert.Equal(ScourgeInvasionCatalog.CultistGossipText, (int)gossip.Hello(player, NpcOf(cultist))!.NpcTextId);
        Assert.True(gossip.SelectReply(player, NpcOf(cultist), 1, CultistEngineerGossip.DisruptAction).Close);
        Creature shadow = Assert.Single(rig.Living(ScourgeInvasionCatalog.ShadowOfDoom));
        Assert.Equal(2u, player.Inventory.GetItemCount(ScourgeInvasionCatalog.NecroticRune));
        Assert.False(cultist.IsAlive);
        Assert.Equal(3, objects.GameObjects.Count(g => g.Entry == ScourgeInvasionCatalog.SummonerShield));
        var ai = Assert.IsType<ShadowOfDoomAi>(shadow.AI);
        Assert.Same(player, ai.Summoner);
        Assert.True(shadow.UnitFlags.HasFlag(UnitFlags.ImmuneToPlayer));
        rig.World.RunTick(5_100);
        Assert.False(shadow.UnitFlags.HasFlag(UnitFlags.ImmuneToPlayer));
        Assert.Same(player, shadow.Combat.Victim);

        _ = shard;
    }

    [Fact]
    public void CityGuardsJoinTheFightAgainstACityAttacker()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        Creature attacker = rig.Invasion.CityAttackers[1519];
        CreatureMapSystem creatures = attacker.Map!.FindUpdater<CreatureMapSystem>()!;
        Creature near = creatures.SummonForInstance(68, attacker.X + 10f, attacker.Y, attacker.Z, 0f)!;
        Creature far = creatures.SummonForInstance(68, attacker.X + 60f, attacker.Y, attacker.Z, 0f)!;
        Creature bystander = creatures.SummonForInstance(ScourgeInvasionCatalog.ShadowOfDoom, attacker.X + 5f, attacker.Y, attacker.Z, 0f)!;
        attacker.AI!.MoveInLineOfSight(near);
        attacker.AI.MoveInLineOfSight(far);
        attacker.AI.MoveInLineOfSight(bystander);
        Assert.Same(attacker, near.Combat.Victim);
        Assert.Null(far.Combat.Victim);
        Assert.Null(bystander.Combat.Victim);

        var shocker = Assert.IsType<PallidHorrorAi>(attacker.AI).Flameshockers.First();
        Creature second = creatures.SummonForInstance(68, shocker.X + 3f, shocker.Y, shocker.Z, 0f)!;
        shocker.AI!.MoveInLineOfSight(second);
        Assert.Same(shocker, second.Combat.Victim);
    }

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit;

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return CreatureCastResult.Ok;
        }

        public void RaiseHit(Unit caster, Unit target, uint spellId) => SpellHit?.Invoke(caster, target, new SpellInfo { Id = spellId });

        public IEnumerable<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Of(Creature caster, uint spell)
            => Casts.Where(c => ReferenceEquals(c.Caster, caster) && c.Spell == spell);

        public bool IsCasting(Creature caster) => false;
        public bool HasAura(Unit unit, uint spellId) => false;
        public void Interrupt(Creature caster) { }
        public void OnCreatureRemoved(Creature creature) { }
    }

    private static Creature Summon(Rig rig, uint entry, float x, float y = 500f)
        => rig.World.GetMap(0).FindUpdater<CreatureMapSystem>()!.SummonForInstance(entry, x, y, 100f, 0f)!;

    /// <summary>Runs up to <paramref name="ticks"/> one-second world ticks and returns the 1-based tick on which <paramref name="count"/> first exceeded 0 (0: never).</summary>
    private static int RunUntil(Rig rig, int ticks, Func<int> count)
    {
        for (int tick = 1; tick <= ticks; tick++)
        {
            rig.World.RunTick(1_000);
            if (count() > 0) return tick;
        }
        return 0;
    }

    [Fact]
    public void AnArmedFlameshockerCastsDespawnerSelfAfterSixtySecondsOutOfCombatAndAnUnarmedOneNever()
    {
        var spells = new RecordingCaster();
        using var rig = new Rig(new StateStore(), spells: spells);
        rig.World.RunTick(0);
        Creature armed = Summon(rig, ScourgeInvasionCatalog.Flameshocker, -8000f);
        Creature unarmed = Summon(rig, ScourgeInvasionCatalog.Flameshocker, -7900f);
        var armedAi = Assert.IsType<FlameshockerAi>(armed.AI);
        var unarmedAi = Assert.IsType<FlameshockerAi>(unarmed.AI);
        Assert.False(armedAi.DespawnArmed); // EVENT_MINION_FLAMESHOCKERS_DESPAWN starts disabled
        armedAi.ArmDespawn();
        Assert.True(armedAi.DespawnArmed);
        Assert.False(armed.Combat.IsInCombat);

        int firedOn = RunUntil(rig, 75, () => spells.Of(armed, ScourgeInvasionCatalog.DespawnerSelf).Count());

        Assert.Equal(60, firedOn);
        (Creature caster, _, Unit? target, bool triggered) = Assert.Single(spells.Of(armed, ScourgeInvasionCatalog.DespawnerSelf));
        Assert.Same(armed, caster);
        Assert.Same(armed, target);
        Assert.True(triggered);
        Assert.False(armedAi.DespawnArmed); // the action fired once and is spent
        Assert.Empty(spells.Of(unarmed, ScourgeInvasionCatalog.DespawnerSelf)); // 60+ s ran: an unarmed one never casts it
        Assert.False(unarmedAi.DespawnArmed);
    }

    [Fact]
    public void AnArmedFlameshockerInCombatRearmsInsteadOfCastingAndCastsOnlyOnceItIsOutOfCombatSixtySecondsLater()
    {
        var spells = new RecordingCaster();
        using var rig = new Rig(new StateStore(), spells: spells);
        rig.World.RunTick(0);
        Creature shocker = Summon(rig, ScourgeInvasionCatalog.Flameshocker, -8000f);
        Creature guard = Summon(rig, 68, -7998f);
        var ai = Assert.IsType<FlameshockerAi>(shocker.AI);
        ai.ArmDespawn();
        Assert.True(shocker.AI!.AttackStart(guard));
        Assert.True(shocker.Combat.IsInCombat);

        for (int tick = 1; tick <= 100; tick++)
        {
            rig.World.RunTick(1_000);
            Assert.True(shocker.Combat.IsInCombat, $"tick {tick}"); // otherwise the absence of a cast proves nothing
        }
        Assert.Empty(spells.Of(shocker, ScourgeInvasionCatalog.DespawnerSelf)); // 100 s of combat: re-armed at 60 s, never cast
        Assert.True(ai.DespawnArmed);

        rig.World.GetMap(0).FindUpdater<CreatureMapSystem>()!.KillCreature(guard);
        rig.World.RunTick(1_000);
        Assert.False(shocker.Combat.IsInCombat);
        Assert.Empty(spells.Of(shocker, ScourgeInvasionCatalog.DespawnerSelf));

        int firedOn = RunUntil(rig, 80, () => spells.Of(shocker, ScourgeInvasionCatalog.DespawnerSelf).Count());
        Assert.InRange(firedOn, 1, 80);
        Assert.Single(spells.Of(shocker, ScourgeInvasionCatalog.DespawnerSelf));
        Assert.False(ai.DespawnArmed);
    }

    [Fact]
    public void ThePallidHorrorArmsTheDespawnOfTheFlameshockerItSummonsBesideAnAttackerAndNotOfItsEscortRing()
    {
        var spells = new RecordingCaster();
        var store = new StateStore();
        using var rig = new Rig(store, spells: spells);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(1_000);
        Creature attacker = rig.Invasion.CityAttackers[1519];
        var ai = Assert.IsType<PallidHorrorAi>(attacker.AI);
        Creature[] escort = [.. ai.Flameshockers];
        Assert.InRange(escort.Length, 5, 9);
        Assert.All(escort, f => Assert.False(Assert.IsType<FlameshockerAi>(f.AI).DespawnArmed));

        Creature guard = Summon(rig, 68, attacker.X + 70f, attacker.Y); // far enough that no escort Flameshocker stands within 5 yd of it yet
        Assert.True(guard.AI!.AttackStart(attacker));
        Assert.Same(attacker, guard.Combat.Victim);
        attacker.Combat.Threat.AddThreat(guard, 100f); // a ranged hit lands: the guard is on the threat list while still far from the escort
        for (int tick = 0; tick < 6 && ai.Flameshockers.Count == escort.Length; tick++) rig.World.RunTick(1_000);

        Creature[] added = [.. ai.Flameshockers.Except(escort)];
        Assert.NotEmpty(added); // the summon timer put one beside the guard
        Assert.All(added, f => Assert.True(Assert.IsType<FlameshockerAi>(f.AI).DespawnArmed));
        Assert.All(escort, f => Assert.False(Assert.IsType<FlameshockerAi>(f.AI).DespawnArmed));
    }

    [Fact]
    public void SpiritSpawnOutGivesBothMinionsAThreeSecondForcedDespawn()
    {
        var spells = new RecordingCaster();
        using var rig = new Rig(new StateStore(), spells: spells);
        rig.World.RunTick(0);
        Creature shocker = Summon(rig, ScourgeInvasionCatalog.Flameshocker, -8000f);
        Creature shadow = Summon(rig, ScourgeInvasionCatalog.ShadowOfDoom, -7900f);
        Creature bystander = Summon(rig, ScourgeInvasionCatalog.Flameshocker, -7800f);
        Assert.IsType<FlameshockerAi>(shocker.AI);
        Assert.IsType<ShadowOfDoomAi>(shadow.AI);

        spells.RaiseHit(shocker, shocker, ScourgeInvasionCatalog.SpiritSpawnOut);
        spells.RaiseHit(shadow, shadow, ScourgeInvasionCatalog.SpiritSpawnOut);
        spells.RaiseHit(bystander, bystander, ScourgeInvasionCatalog.DespawnerSelf); // another spell: no despawn
        rig.World.RunTick(2_900);
        Assert.True(shocker.IsInWorld && shocker.IsAlive);
        Assert.True(shadow.IsInWorld && shadow.IsAlive);
        rig.World.RunTick(200);
        Assert.False(shocker.IsInWorld);
        Assert.False(shadow.IsInWorld);
        Assert.True(bystander.IsInWorld && bystander.IsAlive);
    }

    /// <summary>A Shadow of Doom set on <paramref name="victim"/>, one tick on: the Scourge Strike (28265) casts it made.</summary>
    private static List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> StrikeAt(Rig rig, RecordingCaster spells, Creature shadow, Unit victim)
    {
        Assert.True(shadow.AI!.AttackStart(victim));
        rig.World.RunTick(100);
        Assert.True(shadow.Combat.IsInCombat); // a refusal below is the gate's, not a shadow that never fought
        Assert.Same(victim, shadow.Combat.Victim);
        return [.. spells.Of(shadow, ScourgeInvasionCatalog.ScourgeStrike)];
    }

    [Fact]
    public void ShadowOfDoomScourgeStrikesACreatureVictimWithin30YardsButNeverAPlayerAPetOrADistantCreature()
    {
        var spells = new RecordingCaster();
        using var rig = new Rig(new StateStore(), spells: spells);
        rig.World.RunTick(0);

        // Positive control: an attackable creature 5 yd away is struck, triggered, at the victim.
        Creature near = Summon(rig, 68, -8000f, 505f);
        Creature shadow = Summon(rig, ScourgeInvasionCatalog.ShadowOfDoom, -8000f);
        var hits = StrikeAt(rig, spells, shadow, near);
        Assert.NotEmpty(hits);
        Assert.All(hits, h =>
        {
            Assert.Same(near, h.Target);
            Assert.True(h.Triggered);
        });

        // A creature victim beyond 30 yd is refused (same gate otherwise).
        Creature far = Summon(rig, 68, -8000f, 650f);
        Creature distantShadow = Summon(rig, ScourgeInvasionCatalog.ShadowOfDoom, -8000f, 400f);
        Assert.Empty(StrikeAt(rig, spells, distantShadow, far));
        Assert.True(MathF.Abs(far.Y - distantShadow.Y) > 30f); // still out of range after the tick

        // A pet (a creature whose owner is a player) is refused although it is in range and attackable.
        rig.AddPlayer(0, -7000f, 0f, 100f); // one player: the pet owner and the player victim
        Player owner = rig.LastPlayer!;
        Creature pet = Summon(rig, 68, -8100f, 505f);
        pet.SetOwnerGuid(owner.Guid);
        Assert.True(pet.IsCharmerOrOwnerPlayerOrPlayerItself);
        Creature petShadow = Summon(rig, ScourgeInvasionCatalog.ShadowOfDoom, -8100f);
        Assert.Empty(StrikeAt(rig, spells, petShadow, pet));

        // A player is refused although it is in range and attackable.
        Creature playerShadow = Summon(rig, ScourgeInvasionCatalog.ShadowOfDoom, -7000f, 5f);
        Assert.Empty(StrikeAt(rig, spells, playerShadow, owner));
    }

    private static NpcInfo NpcOf(Creature c)
        => new(c.Guid, c.Entry, 0, default, c.MapId, c.X, c.Y, c.Z, 0f, c.IsAlive, false, false, false, 0);
}
