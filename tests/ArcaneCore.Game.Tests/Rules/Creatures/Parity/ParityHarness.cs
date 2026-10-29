using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>Which executor a parity run uses.</summary>
internal enum ParitySide
{
    /// <summary>The EventAI executor on the scenario's rows (<see cref="CreatureEventAI"/>).</summary>
    Old,

    /// <summary>The rule engine on the rows the converter made from the same scenario (<see cref="CreatureRuleAI"/>).</summary>
    Engine,
}

/// <summary>One scripted action of a scenario. The same list of steps runs against both executors.</summary>
internal sealed record ParityStep(string Label, Action<ParityContext> Run);

/// <summary>
/// A clean-room fixture: a synthetic creature (entry <see cref="ParityScenario.Entry"/>) with hand-written EventAI rows, and the script of
/// stimuli applied to it. Nothing here is copied from a reference database.
/// </summary>
internal sealed class ParityScenario
{
    /// <summary>The fixture creature's entry.</summary>
    public const uint Entry = 990_001;

    /// <summary>A passive creature the fixture can summon.</summary>
    public const uint SummonEntry = 990_002;

    /// <summary>A second passive summon template.</summary>
    public const uint SummonEntryB = 990_003;

    /// <summary>A passive friendly creature standing next to the fixture (the target of the friendly-search events).</summary>
    public const uint FriendEntry = 990_004;

    public required string Name { get; init; }

    public required IReadOnlyList<CreatureAiEvent> Rows { get; init; }

    public IReadOnlyList<CreatureAiText> Texts { get; init; } = [];

    public IReadOnlyList<BroadcastText> Broadcast { get; init; } = [];

    public IReadOnlyList<CreatureAiSummon> Summons { get; init; } = [];

    public required IReadOnlyList<ParityStep> Steps { get; init; }

    /// <summary>The source row ids the converter is expected to refuse (the engine side then runs without them).</summary>
    public IReadOnlyList<uint> Unconvertible { get; init; } = [];

    /// <summary>The source row ids the converter is expected to write disabled.</summary>
    public IReadOnlyList<uint> Disabled { get; init; } = [];
}

/// <summary>The effect log of one run, with the evaluation cost of each world tick.</summary>
internal sealed record ParityRun(IReadOnlyList<string> Log, IReadOnlyList<double> TickMicroseconds, EventAiConversion? Conversion);

/// <summary>The world of one run and the scripted stimuli. Time advances only through <see cref="Wait"/> (100 ms ticks).</summary>
internal sealed class ParityContext : IDisposable
{
    private const uint TickMs = 100;

    private readonly List<string> _log = [];
    private readonly List<double> _tickMicroseconds = [];
    private readonly HashSet<ulong> _knownCreatures = [];
    private readonly FakeSession _session;
    private int _packets;
    private string _lastState = string.Empty;

    public ParityContext(WorldRuntime world, Map map, CreatureMapSystem system, Player player, FakeSession session, Creature wolf, ParityCaster caster, ParityUnitSpells unitSpells, ParityQuests quests)
    {
        World = world;
        Map = map;
        System = system;
        Player = player;
        _session = session;
        Wolf = wolf;
        Caster = caster;
        UnitSpells = unitSpells;
        Quests = quests;
        caster.Log = Log;
        caster.NameOf = Name;
        unitSpells.Log = Log;
        unitSpells.NameOf = Name;
        quests.Log = Log;
        quests.NameOf = Name;
        foreach (Creature creature in system.Creatures)
        {
            _knownCreatures.Add(creature.Guid.Value);
        }

        _lastState = State();
    }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public CreatureMapSystem System { get; }

    public Player Player { get; private set; }

    public Creature Wolf { get; }

    public ParityCaster Caster { get; }

    public ParityUnitSpells UnitSpells { get; }

    public ParityQuests Quests { get; }

    /// <summary>The creature's AI, which is a <see cref="CreatureEventAI"/> on the old side and a <see cref="CreatureRuleAI"/> on the engine side.</summary>
    public CreatureEventAI Ai => (CreatureEventAI)Wolf.AI!;

    public uint Now { get; private set; }

    public IReadOnlyList<string> Lines => _log;

    public IReadOnlyList<double> TickCosts => _tickMicroseconds;

    /// <summary>Adds a second player at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Player AddSecondPlayer(float x, float y)
    {
        (Player second, _) = AddPlayer(World, 2, x, y);
        return second;
    }

    public void Log(string line) => _log.Add($"t={Now} {line}");

    public string Name(Unit? unit) => unit switch
    {
        null => "none",
        Player p => $"player{p.Guid.Low}",
        Creature c => $"creature{c.Template.Entry}",
        _ => unit.GetType().Name,
    };

    /// <summary>The first player damages the fixture creature (this puts it in combat and fires the aggro hook).</summary>
    public void Pull(uint damage = 1) => Map.Combat.DealDamage(Player, Wolf, damage, direct: false);

    public void Damage(uint damage, Unit? by = null) => Map.Combat.DealDamage(by ?? Player, Wolf, damage, direct: false);

    /// <summary>Run <paramref name="ms"/> of world time in 100 ms ticks, observing the effects after each.</summary>
    public void Wait(uint ms)
    {
        for (uint done = 0; done < ms; done += TickMs)
        {
            uint step = Math.Min(TickMs, ms - done);
            long start = Stopwatch.GetTimestamp();
            World.RunTick(step);
            _tickMicroseconds.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            Now += step;
            Observe();
        }
    }

    /// <summary>Runs a stimulus that calls the creature's AI directly, then observes the effects.</summary>
    public void Stimulus(string label, Action<CreatureEventAI> call)
    {
        Log($"stimulus {label}");
        call(Ai);
        Observe();
    }

    public void Observe()
    {
        foreach (Creature creature in System.Creatures.OrderBy(c => c.Guid.Value))
        {
            if (_knownCreatures.Add(creature.Guid.Value))
            {
                Log($"appeared creature{creature.Template.Entry} at ({creature.X:0.##},{creature.Y:0.##},{creature.Z:0.##}) o={creature.Orientation:0.##}");
            }
        }

        foreach (ulong guid in _knownCreatures.ToArray())
        {
            if (!System.Creatures.Any(c => c.Guid.Value == guid))
            {
                _knownCreatures.Remove(guid);
                Log("disappeared creature");
            }
        }

        string state = State();
        if (state != _lastState)
        {
            Log($"state {state}");
            _lastState = state;
        }

        (WorldOpcode Opcode, byte[] Payload)[] sent = _session.Sent.ToArray();
        for (; _packets < sent.Length; _packets++)
        {
            string? line = Packet(sent[_packets].Opcode, sent[_packets].Payload);
            if (line is not null)
            {
                Log(line);
            }
        }
    }

    private string State()
    {
        Creature w = Wolf;
        int phase = Wolf.AI is CreatureRuleAI rule ? rule.Runner.Phase : Ai.Phase;
        return string.Create(CultureInfo.InvariantCulture,
            $"phase={phase} flags=0x{(uint)w.UnitFlags:X} faction={w.FactionTemplate} display={w.DisplayId} stand={w.StandState} melee={Ai.MeleeEnabled} ranged={Ai.RangedMode} alive={w.IsAlive} combat={w.Combat.IsInCombat}");
    }

    private static string? Packet(WorldOpcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case WorldOpcode.SmsgUpdateObject:
            case WorldOpcode.SmsgCompressedUpdateObject:
            case WorldOpcode.SmsgDestroyObject:
                return null;
            case WorldOpcode.SmsgMessagechat:
                {
                    MonsterChat chat = ParseMonsterChat(payload);
                    return $"chat {chat.Type} lang={chat.Language} '{chat.Message}'";
                }

            case WorldOpcode.SmsgMonsterMove:
                try
                {
                    PathMove move = ParsePathMove(payload);
                    string points = string.Join(';', move.Points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.#},{p.Y:0.#},{p.Z:0.#}")));
                    return $"move {move.Type} flags=0x{move.Flags:X} duration={move.Duration} to {points}";
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
                {
                    return $"move raw {Convert.ToHexString(payload)}";
                }

            default:
                return $"packet {opcode} {Convert.ToHexString(payload)}";
        }
    }

    public void Dispose() => World.Dispose();
}

/// <summary>A spell caster that logs what the creature asked of it and can refuse chosen spells (to fail a combat action).</summary>
internal sealed class ParityCaster : ICreatureSpellCaster
{
    public Action<string> Log { get; set; } = static _ => { };

    public Func<Unit?, string> NameOf { get; set; } = static _ => "?";

    /// <summary>Spell ids whose cast is refused.</summary>
    public HashSet<uint> Refuse { get; } = [];

    public bool Casting { get; set; }

    public HashSet<(Unit, uint)> Auras { get; } = [];

    public event Action<Unit, Unit, SpellInfo>? SpellHit;

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        bool refused = Refuse.Contains(spellId);
        Log($"cast {spellId} at {NameOf(target)} triggered={triggered}{(refused ? " refused" : string.Empty)}");
        return refused ? CreatureCastResult.Failed : CreatureCastResult.Ok;
    }

    public CreatureCastResult CastAtDestination(Creature caster, uint spellId, float x, float y, float z, bool triggered)
    {
        Log($"cast {spellId} at ({x:0.##},{y:0.##},{z:0.##}) triggered={triggered}");
        return CreatureCastResult.Ok;
    }

    public CreatureCastResult CastByUnit(Unit caster, uint spellId, Unit? target, bool triggered)
    {
        Log($"cast-by {NameOf(caster)} {spellId} at {NameOf(target)} triggered={triggered}");
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => Casting;

    public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

    public void RemoveAuras(Unit unit, uint spellId)
    {
        Log($"remove-aura {spellId} from {NameOf(unit)}");
        Auras.Remove((unit, spellId));
    }

    public CreatureCastResult AddAura(Unit unit, uint spellId, bool permanent)
    {
        Log($"add-aura {spellId} to {NameOf(unit)} permanent={permanent}");
        Auras.Add((unit, spellId));
        return CreatureCastResult.Ok;
    }

    public CreatureCastResult AddAuraFrom(Unit unit, uint spellId, Unit caster)
    {
        Log($"add-aura-from {NameOf(caster)} {spellId} to {NameOf(unit)}");
        Auras.Add((unit, spellId));
        return CreatureCastResult.Ok;
    }

    public void Interrupt(Creature caster) => Log("interrupt");

    public void OnCreatureRemoved(Creature creature)
    {
    }

    public void RaiseHit(Unit caster, Unit target, SpellInfo spell) => SpellHit?.Invoke(caster, target, spell);
}

/// <summary>Aura and casting queries the fixture controls.</summary>
internal sealed class ParityUnitSpells : IUnitSpellQueries
{
    public Action<string> Log { get; set; } = static _ => { };

    public Func<Unit?, string> NameOf { get; set; } = static _ => "?";

    public Dictionary<(Unit, uint), int> Stacks { get; } = [];

    public HashSet<Unit> Casters { get; } = [];

    public int GetAuraStacks(Unit unit, uint spellId) => Stacks.GetValueOrDefault((unit, spellId));

    public bool IsCasting(Unit unit) => Casters.Contains(unit);

    public bool RemoveAuras(Unit unit, uint spellId)
    {
        Log($"remove-auras {spellId} from {NameOf(unit)}");
        return Stacks.Remove((unit, spellId));
    }
}

/// <summary>Quest hooks that log the credit the creature gives.</summary>
internal sealed class ParityQuests : IEventAiQuestEvents
{
    public Action<string> Log { get; set; } = static _ => { };

    public Func<Unit?, string> NameOf { get; set; } = static _ => "?";

    public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        => Log($"quest-event {questId} for {NameOf(player)} group={rewardGroup}");

    public void KillCredit(Player player, uint creatureEntry, Creature source)
        => Log($"kill-credit {creatureEntry} for {NameOf(player)}");
}

/// <summary>Builds the two worlds of a scenario and runs its script on each.</summary>
internal static class ParityRunner
{
    /// <summary>The rule catalog the converter's output builds into, for the engine side.</summary>
    public static RuleCatalog BuildCatalog(EventAiConversion conversion)
    {
        RuleCatalog catalog = RuleCatalog.Build(conversion.Content, RuleVocabulary.Default);
        Assert.Empty(catalog.Rejected.Select(r => $"{r.Table} {r.Id}: {r.Reason}"));
        return catalog;
    }

    public static CreatureAiContent Content(ParityScenario scenario)
        => new(scenario.Rows, scenario.Texts, new BroadcastTextCatalog(scenario.Broadcast), scenario.Summons);

    public static EventAiConversion Convert(ParityScenario scenario) => EventAiRuleConverter.Convert(Content(scenario));

    public static ParityRun Run(ParityScenario scenario, ParitySide side)
    {
        CreatureAiContent ai = Content(scenario);
        EventAiConversion? conversion = null;
        var factory = new CreatureAiFactory();
        string aiName = CreatureAiFactory.EventAIName;
        if (side == ParitySide.Engine)
        {
            conversion = EventAiRuleConverter.Convert(ai);
            CreatureRuleAI.Register(factory, BuildCatalog(conversion));
            aiName = CreatureRuleAI.AiName;
        }

        var caster = new ParityCaster();
        var unitSpells = new ParityUnitSpells();
        var quests = new ParityQuests();
        var services = new CreatureAiServices { Spells = caster, UnitSpells = unitSpells, QuestEvents = quests, Factory = factory };
        var content = new CreatureContent(
            [
                Template(ParityScenario.Entry, t => t.AIName = aiName),
                Template(ParityScenario.SummonEntry, t => t.AIName = "NullAI"),
                Template(ParityScenario.SummonEntryB, t => t.AIName = "NullAI"),
                Template(ParityScenario.FriendEntry, t => t.AIName = "NullAI"),
            ],
            [Spawn(1, ParityScenario.Entry, 5, 0), Spawn(2, ParityScenario.FriendEntry, 6, 1)],
            [], [], [],
            ai);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, services);
        map.Combat.Random = new SeededCombatRandom();
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = system.Creatures.Single(c => c.Template.Entry == ParityScenario.Entry);
        Assert.IsAssignableFrom<CreatureEventAI>(wolf.AI);
        Assert.Equal(side == ParitySide.Engine, wolf.AI is CreatureRuleAI);
        using var context = new ParityContext(world, map, system, player, session, wolf, caster, unitSpells, quests);
        foreach (ParityStep step in scenario.Steps)
        {
            context.Log($"step {step.Label}");
            step.Run(context);
            context.Observe();
        }

        return new ParityRun(context.Lines.ToArray(), context.TickCosts.ToArray(), conversion);
    }
}

/// <summary>A combat roll source with a fixed seed: both sides of a scenario see the same hit table and damage rolls.</summary>
internal sealed class SeededCombatRandom : ICombatRandom
{
    private readonly Random _random = new(7);

    public int Next(int minInclusive, int maxInclusive) => _random.Next(minInclusive, maxInclusive + 1);

    public float NextFloat(float min, float max) => min + (float)(_random.NextDouble() * (max - min));
}
