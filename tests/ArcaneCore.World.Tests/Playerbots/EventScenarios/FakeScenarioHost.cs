using System.Numerics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.EventScenarios;

namespace ArcaneCore.World.Tests.Playerbots.EventScenarios;

/// <summary>A bot of the fake server.</summary>
internal sealed class FakeBot(string name, byte team = 1)
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; } = name;

    public ManagedPlayerbotState State { get; set; } = ManagedPlayerbotState.Stopped;

    public bool Desired { get; set; }

    public bool Online { get; set; }

    public bool Alive { get; set; } = true;

    public bool InCombat { get; set; }

    public int Level { get; set; } = 1;

    public byte Team { get; set; } = team;

    public uint Map { get; set; } = 1;

    public Vector3 Position { get; set; } = new(5000, 5000, 0);

    public string? HeldBy { get; set; }

    /// <summary>A bot logged in and standing somewhere.</summary>
    public FakeBot OnlineAt(uint map, Vector3 position, int level)
    {
        State = ManagedPlayerbotState.Running;
        Desired = true;
        Online = true;
        Map = map;
        Position = position;
        Level = level;
        return this;
    }
}

/// <summary>
/// An in-memory server for the scenario engine: a roster of bots, the commands the engine sends recorded, and the answers a test sets.
/// Creations complete at once (a new bot is registered stopped) unless <see cref="HoldCreates"/> keeps them pending.
/// </summary>
internal sealed class FakeScenarioHost : IEventScenarioHost
{
    public uint Now { get; set; } = 1_000;

    public uint NowMs => Now;

    public List<FakeBot> Bots { get; } = [];

    public int Room { get; set; } = 10_000;

    public bool HoldCreates { get; set; }

    public List<(string Name, byte Race, byte Class, Action<PlayerbotOperationResult> Done)> PendingCreates { get; } = [];

    public List<string> Created { get; } = [];

    public Func<string, PlayerbotOperationResult?>? CreateAnswer { get; set; }

    public int RosterCalls { get; private set; }

    public List<string> Starts { get; } = [];

    public List<(string Bot, uint Map, Vector3 At)> Teleports { get; } = [];

    public string? TeleportRefusal { get; set; }

    public List<(string Bot, int Level)> Boosts { get; } = [];

    public string? BoostRefusal { get; set; }

    public Dictionary<Guid, PlayerbotDefendPost?> Posts { get; } = [];

    public string? DefendRefusal { get; set; }

    public int DefendCalls { get; private set; }

    public int? RunningSiegeId { get; set; }

    public List<Guid> SiegeBots { get; } = [];

    public IReadOnlyCollection<Guid> SiegeMembers() => RunningSiegeId is null ? [] : [.. SiegeBots];

    public string? SiegeOutcome { get; set; }

    public string? SiegeRefusal { get; set; }

    public List<(IReadOnlyList<Guid> Bots, EventScenarioSiege Siege)> SiegeStarts { get; } = [];

    public List<string> SiegeStops { get; } = [];

    private int _nextSiege = 1;

    public FakeBot Add(string name, byte team = 1) { var bot = new FakeBot(name, team); Bots.Add(bot); return bot; }

    public FakeBot Find(string name) => Bots.Single(b => b.Name == name);

    public IReadOnlyList<PlayerbotStatus> Roster()
    {
        RosterCalls++;
        return [.. Bots.Select(b => new PlayerbotStatus(b.Id, b.Name, b.State, b.Desired, PlayerbotGoalKind.Explore, 0, 0, b.Map, 100, null))];
    }

    public ScenarioBotProbe? Probe(Guid botId)
    {
        FakeBot? bot = Bots.FirstOrDefault(b => b.Id == botId);
        return bot is { Online: true } ? new ScenarioBotProbe(bot.Alive, bot.InCombat, bot.Level, bot.Team, bot.Map, bot.Position, bot.HeldBy, Posts.GetValueOrDefault(bot.Id)?.PostId) : null;
    }

    public int CreateRoom() => Room - PendingCreates.Count;

    public void BeginCreate(string name, byte race, byte characterClass, Action<PlayerbotOperationResult> done)
    {
        Created.Add(name);
        if (HoldCreates)
        {
            PendingCreates.Add((name, race, characterClass, done));
            return;
        }

        Complete(name, done);
    }

    /// <summary>Answer the held creations.</summary>
    public void CompleteCreates()
    {
        (string Name, byte Race, byte Class, Action<PlayerbotOperationResult> Done)[] held = [.. PendingCreates];
        PendingCreates.Clear();
        foreach (var create in held) Complete(create.Name, create.Done);
    }

    private void Complete(string name, Action<PlayerbotOperationResult> done)
    {
        if (CreateAnswer?.Invoke(name) is { } answer)
        {
            done(answer);
            return;
        }

        FakeBot bot = Add(name);
        done(new PlayerbotOperationResult(true, "created", bot.Id, name));
    }

    public void BeginStart(Guid botId, Action<PlayerbotOperationResult> done)
    {
        FakeBot bot = Bots.Single(b => b.Id == botId);
        Starts.Add(bot.Name);
        bot.State = ManagedPlayerbotState.Running;
        bot.Desired = true;
        bot.Online = true;
        done(new PlayerbotOperationResult(true, "started", botId, bot.Name));
    }

    public string? Boost(Guid botId, int level)
    {
        FakeBot bot = Bots.Single(b => b.Id == botId);
        if (BoostRefusal is { } refusal) return refusal;
        Boosts.Add((bot.Name, level));
        bot.Level = Math.Max(bot.Level, level);
        return null;
    }

    public IReadOnlyList<FleetSlot> Ring(uint map, Vector3 anchor, float orientation, int count)
        => [.. Enumerable.Range(0, count).Select(i => new FleetSlot(anchor.X + (i * 0.5f), anchor.Y, anchor.Z, orientation))];

    public string? Teleport(Guid botId, uint map, Vector3 position, float orientation)
    {
        FakeBot bot = Bots.Single(b => b.Id == botId);
        Teleports.Add((bot.Name, map, position));
        if (TeleportRefusal is { } refusal) return refusal;
        bot.Map = map;
        bot.Position = position;
        return null;
    }

    public string? SetDefend(Guid botId, PlayerbotDefendPost? post)
    {
        if (post is not null && DefendRefusal is { } refusal) return refusal;
        if (post is not null) DefendCalls++;
        Posts[botId] = post;
        return null;
    }

    public ScenarioSiegeResult StartSiege(IReadOnlyList<Guid> botIds, EventScenarioSiege siege)
    {
        if (SiegeRefusal is { } refusal) return new ScenarioSiegeResult(false, 0, refusal, 0, 0);
        if (RunningSiegeId is not null) return new ScenarioSiegeResult(false, 0, "siege-running", 0, 0);
        SiegeStarts.Add(([.. botIds], siege));
        SiegeBots.Clear();
        SiegeBots.AddRange(botIds);
        RunningSiegeId = _nextSiege++;
        SiegeOutcome = null;
        return new ScenarioSiegeResult(true, RunningSiegeId.Value, null, botIds.Count, 0);
    }

    public bool StopSiege(string reason)
    {
        if (RunningSiegeId is null) return false;
        SiegeStops.Add(reason);
        RunningSiegeId = null;
        SiegeOutcome = "stopped: " + reason;
        return true;
    }
}
