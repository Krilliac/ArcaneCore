using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// A real creature and object system for the Alterac Valley assault scripts: an Alterac Valley match (recording host and ports), the scripts
/// attached to the map, a recording spell caster, the broadcast texts the scripts speak and one player. Time is the manual world tick.
/// </summary>
internal sealed class AvScriptRig : IDisposable
{
    public const float Z = 83.5f;

    private AvScriptRig(WorldRuntime world, Map map, CreatureMapSystem creatures, GameObjectMapSystem objects, AlteracValley match,
        RecordingHost host, AlteracValleyScripts scripts, FakeCaster caster, Player player, FakeSession session, SpellTestKit? spells)
    {
        Spells = spells;
        World = world;
        Map = map;
        Creatures = creatures;
        Objects = objects;
        Match = match;
        Host = host;
        Scripts = scripts;
        Caster = caster;
        Player = player;
        Session = session;
    }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public CreatureMapSystem Creatures { get; }

    public GameObjectMapSystem Objects { get; }

    public AlteracValley Match { get; }

    public RecordingHost Host { get; }

    public AlteracValleyScripts Scripts { get; }

    public FakeCaster Caster { get; }

    public Player Player { get; }

    public FakeSession Session { get; }

    /// <summary>
    /// The real spell system the creatures cast through, when the rig was created with one (<see cref="Caster"/> then records nothing); it
    /// advances with every world tick.
    /// </summary>
    public SpellTestKit? Spells { get; }

    /// <summary>The broadcast texts the assault scripts speak (only their ids matter to the tests; the chat type is a yell for all).</summary>
    public static readonly int[] Texts =
    [
        AvEventAI.SayLokholarSpawned, AvEventAI.SayPrimalistThurloga, AvEventAI.SayArchdruidRenferal, AvEventAI.SayWolfRiderCommander,
        AvEventAI.SayWarcryHorde, AvEventAI.SayRamRiderCommander, AvEventAI.SayWarcryAlliance,
        AvWorldBossAI.SayLokholarSpawn1, AvWorldBossAI.SayLokholarSpawn2, AvWorldBossAI.SayLokholarKilledPlayer, AvWorldBossAI.SayLokholarReachedBase,
        AvWorldBossAI.SayIvusSpawned, AvWorldBossAI.SayIvusPastField, AvWorldBossAI.SayIvusReachedBase,
        AvCollectorGossip.SayPatrol, AvCollectorGossip.SayReavers, AvCollectorGossip.SayRamRider, AvCollectorGossip.SayGuse,
    ];

    public static CreatureTemplate AvNpc(uint entry, Action<CreatureTemplateBuilder>? configure = null) => Template(entry, b =>
    {
        b.Name = $"Npc {entry}";
        b.Faction = 35;
        b.MinLevel = 60;
        b.MaxLevel = 60;
        b.MinLevelHealth = 5000;
        b.MaxLevelHealth = 5000;
        configure?.Invoke(b);
    });

    /// <summary>A straight path of points 0..<paramref name="last"/> from (x, y) along +x, three yards apart.</summary>
    public static IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)> Path(uint entry, float x, float y, uint last, float step = 3f)
    {
        for (uint i = 0; i <= last; i++)
        {
            yield return (entry, EscortAI.EscortPathId, new CreatureWaypoint(i, x + ((i + 1) * step), y, Z, 0, 0));
        }
    }

    public static AvScriptRig Create(IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns, float playerX, float playerY,
        IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? paths = null, IEnumerable<GameObjectTemplate>? objectTemplates = null,
        IEnumerable<GameObjectSpawn>? objectSpawns = null, Team team = Team.Alliance, ICreatureHostility? hostility = null,
        SpellTestKit? spells = null)
    {
        var ai = new CreatureAiContent([], [], new BroadcastTextCatalog(Texts.Select(id =>
            new BroadcastText((uint)id, $"text {id}", string.Empty, 1, 0, 0, [0, 0, 0], [0, 0, 0]))));
        CreatureContent content = new(templates, spawns, [], [], [], ai, entryWaypoints: paths);
        var caster = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem creatures) = CreateAiSystem(content,
            new CreatureAiServices
            {
                Spells = spells is null ? caster : new SpellSystemCreatureCaster(spells.System),
                Hostility = hostility ?? new FactionHostility(),
            },
            world: spells?.World);
        var objects = new GameObjectMapSystem(map, new GameObjectContent(objectTemplates ?? [], objectSpawns ?? [], [], [], []));
        map.AddUpdater(objects);

        var host = new RecordingHost();
        var ports = new RecordingPorts();
        var match = new AlteracValley(AlteracValleyTests.AvTemplate(), bracket: 0, instanceId: 303, clientInstanceId: 1, new BattlegroundOptions(),
            ports.ToPorts(host, new Random(7)));
        (Player player, FakeSession session) = AddPlayer(world, 1, playerX, playerY);
        match.StartBattleground();
        match.IncreaseInvitedCount(team);
        Assert.True(match.AddPlayer(player.Guid, team));

        var scripts = new AlteracValleyScripts(match);
        scripts.Attach(creatures);
        scripts.Attach(objects);
        world.RunTick(0);
        session.Clear();
        return new AvScriptRig(world, map, creatures, objects, match, host, scripts, caster, player, session, spells);
    }

    public Creature Single(uint entry) => Assert.Single(Creatures.Creatures, c => c.Entry == entry);

    public IReadOnlyList<Creature> All(uint entry) => [.. Creatures.Creatures.Where(c => c.Entry == entry)];

    public void Run(uint ms)
    {
        for (uint done = 0; done < ms; done += 100)
        {
            Tick(Math.Min(100, ms - done));
        }
    }

    public void RunUntil(Func<bool> condition, string what, uint limitMs = 120_000, Func<string>? detail = null)
    {
        for (uint done = 0; done < limitMs && !condition(); done += 100)
        {
            Tick(100);
        }

        Assert.True(condition(), $"{what} did not happen within {limitMs} ms of simulated time {detail?.Invoke()}");
    }

    /// <summary>The ids of the broadcast texts the creatures said since the last clear (their text is "text {id}").</summary>
    public List<int> Said()
        => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(ParseMonsterChat).Select(c => int.Parse(c.Message["text ".Length..]))];

    public void Dispose() => World.Dispose();

    /// <summary>One world tick, and the same time for the real spell system when there is one.</summary>
    private void Tick(uint ms)
    {
        World.RunTick(ms);
        Spells?.Advance(ms, ms);
    }

    /// <summary>Hostile across the two player factions only (Alliance 1, Horde 2 and the creature factions below).</summary>
    private sealed class FactionHostility : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => false;

        public bool CanAssist(Creature helper, Creature caller) => false;
    }
}
