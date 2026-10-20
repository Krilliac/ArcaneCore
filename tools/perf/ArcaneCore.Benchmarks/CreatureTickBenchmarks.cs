using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Benchmarks;

/// <summary>
/// One 50 ms world tick of map 0 with <see cref="Creatures"/> synthetic creatures (a fixed seed scatters them over a 1600-yard square,
/// three in five wander) and <see cref="Players"/> players gathered eight to a hub over the same square, a quarter of them stepping each
/// tick: the shape of the live creature walk (CreatureMapSystem) and the visibility phase. No collision data: heights, paths and sight are
/// the map's defaults. Measured 2026-10-19 (perf-tick lane): CreatureMapSystem is about 95% of the tick and its per-creature cost is spread
/// over AI, spline, random-motion and motion selection with no single hot spot.
/// </summary>
[MemoryDiagnoser]
public class CreatureTickBenchmarks
{
    private const uint Entry = 299;
    private const float Origin = -400f;
    private const float Side = 1600f;

    private readonly List<Player> _players = [];
    private WorldRuntime _world = null!;
    private int _tick;

    [Params(8000)]
    public int Creatures { get; set; }

    [Params(64, 512)]
    public int Players { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(5875);
        var template = new CreatureTemplate
        {
            Entry = Entry, Name = "Bench Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32, UnitClass = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55, CreatureType = 1, Family = 1,
        };
        var spawns = new List<CreatureSpawn>(Creatures);
        for (int i = 0; i < Creatures; i++)
        {
            bool wanders = random.Next(5) < 3;
            spawns.Add(new CreatureSpawn
            {
                Guid = (uint)i + 1, Entry = Entry, MapId = 0, X = Origin + (random.NextSingle() * Side), Y = Origin + (random.NextSingle() * Side),
                Z = 83.5f, Orientation = 1.5f, SpawnTimeMinSeconds = 120, SpawnTimeMaxSeconds = 120, WanderDistance = wanders ? 8f : 0f,
                MovementType = wanders ? (byte)1 : (byte)0,
            });
        }

        _world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new NoSaves(), NullLogger<WorldRuntime>.Instance);
        _world.UseManualClock();
        Map map = _world.GetMap(0);
        map.AddUpdater(new CreatureMapSystem(map, new CreatureContent([template], spawns, [], [], []), random: new Random(1)));
        for (int p = 0; p < Players; p++)
        {
            // Bots gather: Players / 8 hubs over the square, eight players within ten yards of each hub.
            int hub = p / 8;
            float x = Origin + 100f + ((hub % 8) * 200f) + (random.NextSingle() * 10f);
            float y = Origin + 100f + ((hub / 8 % 8) * 200f) + (random.NextSingle() * 10f);
            Player player = CreatePlayer((uint)p + 1, x, y);
            _players.Add(player);
            _world.AddPlayer(player);
        }

        // Spawn, first visibility and the first wander legs are not the steady state being measured.
        for (int i = 0; i < 100; i++)
        {
            RunTick();
        }
    }

    /// <summary>A quarter of the players take a half-yard step each tick (moving bots), alternating direction.</summary>
    private void StepPlayers()
    {
        _tick++;
        float step = (_tick / 4 % 2 == 0) ? 0.5f : -0.5f;
        for (int i = _tick % 4; i < _players.Count; i += 4)
        {
            Player player = _players[i];
            player.Relocate(player.X + step, player.Y, player.Z, player.Orientation, 0);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void RunTick()
    {
        StepPlayers();
        _world.RunTick(50);
    }

    private static Player CreatePlayer(uint guid, float x, float y)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid, AccountId = (int)guid, Name = $"B{guid}", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 1, MapId = 0, ZoneId = 12, X = x, Y = y, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new DiscardSession((int)guid));
    }

    /// <summary>A session that builds what the world sends it (update objects included) and drops it.</summary>
    private sealed class DiscardSession(int accountId) : IPlayerSession
    {
        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private sealed class NoSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}
