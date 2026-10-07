using System.Diagnostics;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>Bounds and pacing of one scenario run.</summary>
public sealed class ScenarioRunOptions
{
    /// <summary>Default bound of one wait (game time on a manual clock, wall time otherwise).</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Wall-clock bound of the whole run.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Most game time a manual clock runs in one burst of a wait. The condition is checked after every tick either way;
    /// this only bounds how long the world runs between two looks at the run's cancellation.
    /// </summary>
    public TimeSpan ManualPollStep { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wall time a manual-clock wait keeps polling (without advancing) after its game budget, for async I/O.</summary>
    public TimeSpan ManualWallGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Real-clock poll interval.</summary>
    public TimeSpan RealPollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Packets per bot kept in a failure report.</summary>
    public int ReportPackets { get; set; } = 20;

    /// <summary>Stop (log out and save) the run's bots afterwards; otherwise they are returned to autonomous mode.</summary>
    public bool StopBotsAfterRun { get; set; } = true;
}

/// <summary>
/// The surface a scenario script drives: bot logins, world-thread setup helpers, bounded waits, steps and assertions.
/// The setup helpers (placement, items, money, spells, health) act directly on world state; they exist only on this
/// harness object, which only tests and the Administrator-only, config-gated <c>.playerbot scenario run</c> construct —
/// no opcode or player command reaches them.
/// </summary>
public sealed class ScenarioContext
{
    private readonly ManagedPlayerbotFeature _bots;
    private readonly List<ScenarioBot> _logins = [];
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private int _stepIndex;
    private string? _currentStep;

    internal ScenarioContext(ManagedPlayerbotFeature bots, WorldRuntime world, IServiceProvider services, IScenarioClock clock,
        ScenarioRunOptions options, ScenarioReport report, CancellationToken cancellationToken)
    {
        _bots = bots;
        World = world;
        Services = services;
        Clock = clock;
        Options = options;
        Report = report;
        CancellationToken = cancellationToken;
    }

    public WorldRuntime World { get; }

    public IServiceProvider Services { get; }

    public IScenarioClock Clock { get; }

    public ScenarioRunOptions Options { get; }

    public ScenarioReport Report { get; }

    public CancellationToken CancellationToken { get; }

    public IReadOnlyList<ScenarioBot> Bots => _logins;

    // --- steps ----------------------------------------------------------------------------------------------------

    /// <summary>Run one named step; its wall/game time and failure land in the report. A failure ends the run.</summary>
    public async Task StepAsync(string name, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        await StepAsync(name, async () =>
        {
            await body().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    public async Task<T> StepAsync<T>(string name, Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        CancellationToken.ThrowIfCancellationRequested();
        int index = ++_stepIndex;
        _currentStep = name;
        long wallStart = _wall.ElapsedTicks;
        TimeSpan gameStart = Clock.Advanced;
        try
        {
            T result = await body().ConfigureAwait(false);
            Report.Add(new ScenarioStepResult(index, name, true, Elapsed(wallStart), Clock.Advanced - gameStart, null));
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string message = error is ScenarioAssertionException or ScenarioTimeoutException
                ? error.Message
                : $"{error.GetType().Name}: {error.Message}";
            Report.Add(new ScenarioStepResult(index, name, false, Elapsed(wallStart), Clock.Advanced - gameStart, message));
            Report.Fail(name, message);
            throw;
        }
        finally
        {
            _currentStep = null;
        }
    }

    // --- bots -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Log a managed bot in, scripted: an existing bot of that name is reused (started, or switched from autonomous
    /// mode), otherwise one is created with <paramref name="race"/>/<paramref name="characterClass"/>.
    /// </summary>
    public async Task<ScenarioBot> LoginAsync(string name, byte race = 1, byte characterClass = 1)
    {
        var bot = new ScenarioBot(this, name);
        PlayerbotStatus? existing = _bots.Snapshot().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Guid id;
        if (existing is null)
        {
            PlayerbotOperationResult created = await _bots.CreateAsync(name, race, characterClass, CancellationToken).ConfigureAwait(false);
            if (!created.Success || created.BotId is not { } newId) throw new ScenarioAssertionException($"creating bot {name} failed: {created.Code}");
            id = newId;
        }
        else
        {
            id = existing.BotId;
        }

        bot.BotId = id;
        if (_bots.FindSession(id) is not null)
        {
            if (!await _bots.SetControllerAsync(id, bot).ConfigureAwait(false)) throw new ScenarioAssertionException($"bot {name} could not be scripted");
        }
        else
        {
            PlayerbotOperationResult started = await _bots.StartScriptedAsync(id.ToString(), bot, CancellationToken).ConfigureAwait(false);
            if (!started.Success) throw new ScenarioAssertionException($"starting bot {name} failed: {started.Code}");
        }

        WorldSessionAttach(bot, _bots.FindSession(id) ?? throw new ScenarioAssertionException($"bot {name} has no session"));
        _logins.Add(bot);
        await WaitUntilAsync($"{name} is in the world", () => bot.Session!.Player is { IsInWorld: true }).ConfigureAwait(false);
        bot.Guid = await World.InvokeAsync(() => bot.Session!.Player!.Guid).ConfigureAwait(false);
        return bot;
    }

    private void WorldSessionAttach(ScenarioBot bot, Net.WorldSession session)
    {
        bot.Session = session;
        session.ManagedPacketObserver = (opcode, payload) => bot.Log.Record(ScenarioPacketDirection.Received, World.NowMs, opcode, payload);
    }

    internal async Task ReleaseBotsAsync()
    {
        foreach (ScenarioBot bot in _logins)
        {
            try
            {
                if (Options.StopBotsAfterRun) await _bots.StopAsync(bot.BotId.ToString(), CancellationToken.None).ConfigureAwait(false);
                else await _bots.SetControllerAsync(bot.BotId, null).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                Report.Fail(null, $"releasing {bot.Name} failed: {error.Message}");
            }

            if (bot.Session is { } session) session.ManagedPacketObserver = null;
        }
    }

    // --- world-thread setup helpers (harness only) -----------------------------------------------------------------

    /// <summary>Teleport a bot through the ordinary teleport service and wait until it has acknowledged and arrived.</summary>
    public async Task PlaceAsync(ScenarioBot bot, uint mapId, float x, float y, float z, float orientation = 0f)
    {
        TeleportService teleports = Services.GetRequiredService<TeleportFeature>().Teleports;
        bool started = await World.InvokeAsync(() => teleports.TeleportTo(bot.RequirePlayer(), mapId, x, y, z, orientation)).ConfigureAwait(false);
        if (!started) throw new ScenarioAssertionException($"{bot.Name} cannot be teleported to map {mapId} ({x:F1}, {y:F1}, {z:F1})");
        await WaitUntilAsync($"{bot.Name} arrives on map {mapId}", () => bot.Session!.Player is { IsInWorld: true, Map: { } map } player
            && map.MapId == mapId && teleports.StageOf(player) is null
            && MathF.Abs(player.X - x) < 1f && MathF.Abs(player.Y - y) < 1f).ConfigureAwait(false);
    }

    /// <summary>
    /// Stand <paramref name="a"/> where it is, facing east, and <paramref name="b"/> <paramref name="distance"/> yards east of
    /// it facing west: face to face, in melee, trade and duel range.
    /// </summary>
    public async Task PlaceFacingAsync(ScenarioBot a, ScenarioBot b, float distance = 2f)
    {
        (uint map, float x, float y, float z) = await a.ReadAsync(p => (p.Map!.MapId, p.X, p.Y, p.Z)).ConfigureAwait(false);
        await PlaceAsync(a, map, x, y, z, 0f).ConfigureAwait(false);
        await PlaceAsync(b, map, x + distance, y, z, MathF.PI).ConfigureAwait(false);
    }

    /// <summary>Put <paramref name="count"/> of item <paramref name="entry"/> in the bot's bags; returns the item GUID.</summary>
    public Task<ObjectGuid> GiveItemAsync(ScenarioBot bot, uint entry, uint count = 1) => World.InvokeAsync(() =>
    {
        InventoryResult result = bot.RequirePlayer().Inventory.AddItem(entry, count, out Item? item);
        return result == InventoryResult.Ok && item is not null
            ? item.Guid
            : throw new ScenarioAssertionException($"giving {bot.Name} item {entry} x{count} failed: {result}");
    });

    public Task GiveMoneyAsync(ScenarioBot bot, uint copper) => World.InvokeAsync(() =>
    {
        Player player = bot.RequirePlayer();
        long money = (long)player.Money + copper;
        player.Money = money > EconomyOptions.MaxMoney ? throw new ScenarioAssertionException("money above the cap") : (uint)money;
        return true;
    });

    public Task LearnSpellAsync(ScenarioBot bot, uint spellId) => World.InvokeAsync(() =>
    {
        SpellFeature spells = Services.GetRequiredService<SpellFeature>();
        Player player = bot.RequirePlayer();
        return spells.System.LearnSpell(player, spellId) || spells.System.Spellbook?.HasSpell(player, spellId) == true
            ? true
            : throw new ScenarioAssertionException($"{bot.Name} could not learn spell {spellId}");
    });

    public Task SetHealthAsync(ScenarioBot bot, uint health) => World.InvokeAsync(() =>
    {
        Player player = bot.RequirePlayer();
        player.Health = Math.Clamp(health, 1u, player.MaxHealth);
        return true;
    });

    // --- waits and reads ------------------------------------------------------------------------------------------

    /// <summary>Evaluate <paramref name="read"/> on the world thread.</summary>
    public Task<T> ReadAsync<T>(Func<T> read) => World.InvokeAsync(read);

    /// <summary>
    /// Wait until <paramref name="condition"/> (evaluated on the world thread) holds. With a manual clock the world runs
    /// tick by tick with the condition checked after every tick, for at most <paramref name="timeout"/> of game time; then
    /// it keeps polling for <see cref="ScenarioRunOptions.ManualWallGrace"/> of wall time without advancing (async I/O such
    /// as a database commit). With the real clock it polls until <paramref name="timeout"/> of wall time. Always bounded.
    /// </summary>
    public async Task WaitUntilAsync(string what, Func<bool> condition, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        TimeSpan limit = timeout ?? Options.StepTimeout;
        var wall = Stopwatch.StartNew();
        TimeSpan game = TimeSpan.Zero;
        TimeSpan? graceStart = null;
        while (true)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (await World.InvokeAsync(condition).WaitAsync(CancellationToken).ConfigureAwait(false)) return;
            if (Clock.IsManual)
            {
                if (game < limit)
                {
                    TimeSpan step = Options.ManualPollStep < limit - game ? Options.ManualPollStep : limit - game;
                    TimeSpan before = Clock.Advanced;
                    if (await Clock.AdvanceUntilAsync(step, condition, CancellationToken).ConfigureAwait(false)) return;
                    TimeSpan advanced = Clock.Advanced - before;
                    game += advanced > TimeSpan.Zero ? advanced : step;
                    continue;
                }

                graceStart ??= wall.Elapsed;
                if (wall.Elapsed - graceStart.Value > Options.ManualWallGrace)
                    throw new ScenarioTimeoutException($"timed out waiting for: {what} ({game.TotalSeconds:F1}s game, {wall.Elapsed.TotalSeconds:F1}s wall)");
                await Task.Delay(10, CancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (wall.Elapsed > limit)
                    throw new ScenarioTimeoutException($"timed out waiting for: {what} ({wall.Elapsed.TotalSeconds:F1}s)");
                await Task.Delay(Options.RealPollInterval, CancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Let <paramref name="duration"/> pass (game time on a manual clock).</summary>
    public Task IdleAsync(TimeSpan duration) => Clock.AdvanceAsync(duration, CancellationToken);

    // --- assertions -----------------------------------------------------------------------------------------------

    public static void Expect(bool condition, string message)
    {
        if (!condition) throw new ScenarioAssertionException(message);
    }

    public static void ExpectEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new ScenarioAssertionException($"{what}: expected {expected}, got {actual}");
    }

    /// <summary>Assert a fact about a bot's player, read on the world thread.</summary>
    public async Task ExpectAsync(ScenarioBot bot, string what, Func<Player, bool> fact)
    {
        if (!await bot.ReadAsync(fact).ConfigureAwait(false)) throw new ScenarioAssertionException($"{bot.Name}: {what}");
    }

    public async Task ExpectMoneyAsync(ScenarioBot bot, uint copper)
        => ExpectEqual(copper, await bot.ReadAsync(p => p.Money).ConfigureAwait(false), $"{bot.Name} money");

    public async Task ExpectItemCountAsync(ScenarioBot bot, uint entry, uint count)
        => ExpectEqual(count, await bot.ReadAsync(p => p.Inventory.GetItemCount(entry)).ConfigureAwait(false), $"{bot.Name} count of item {entry}");

    /// <summary>The server-side group of <paramref name="leader"/> has exactly these members and that leader.</summary>
    public async Task ExpectGroupAsync(ScenarioBot leader, params ScenarioBot[] members)
    {
        GroupManager groups = Services.GetRequiredService<SocialFeature>().Context.Groups;
        (ObjectGuid Leader, ObjectGuid[] Members)? group = await World.InvokeAsync(() => groups.GetGroup(leader.Guid) is { } g
            ? (g.LeaderGuid, g.Members.Select(m => m.Guid).ToArray())
            : ((ObjectGuid, ObjectGuid[])?)null).ConfigureAwait(false);
        Expect(group is not null, $"{leader.Name} is not in a group");
        ExpectEqual(leader.Guid, group!.Value.Leader, "group leader");
        ObjectGuid[] expected = [leader.Guid, .. members.Select(m => m.Guid)];
        Expect(expected.Select(g => g.Value).Order().SequenceEqual(group.Value.Members.Select(g => g.Value).Order()),
            $"group roster: expected [{string.Join(", ", expected.Select(g => g.Value.ToString("X")))}], got [{string.Join(", ", group.Value.Members.Select(g => g.Value.ToString("X")))}]");
    }

    /// <summary>The bot's quest log state of <paramref name="quest"/> (null: not in the log).</summary>
    public Task<(QuestStatus Status, bool Rewarded)?> QuestStateAsync(ScenarioBot bot, uint quest) => World.InvokeAsync(() =>
    {
        QuestNpcFeature quests = Services.GetRequiredService<QuestNpcFeature>();
        return quests.Services.StateOf(bot.RequirePlayer())?.Quests.Get(quest) is { } entry
            ? (entry.Status, entry.Rewarded)
            : ((QuestStatus, bool)?)null;
    });

    internal void Finish()
    {
        Report.Wall = _wall.Elapsed;
        Report.Game = Clock.Advanced;
        if (!Report.Passed)
        {
            foreach (ScenarioBot bot in _logins) Report.AttachPackets(bot.Name, bot.Log.LastRelevant(Options.ReportPackets));
        }
    }

    internal string? CurrentStep => _currentStep;

    private TimeSpan Elapsed(long startTicks) => TimeSpan.FromTicks((_wall.ElapsedTicks - startTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
}
