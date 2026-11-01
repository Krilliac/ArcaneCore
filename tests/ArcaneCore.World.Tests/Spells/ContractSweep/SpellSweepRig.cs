using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The world the contract sweep casts in: every world feature composed as <see cref="WorldTestHost"/> composes them (so every
/// effect and aura handler the daemon registers is registered here too), but on a manual clock and without the world thread or a
/// listener. The test thread drives <see cref="WorldRuntime.RunTick(uint)"/> itself, so a 30-minute aura costs ticks, not wall time.
/// The spell feature's store is replaced by the client's Spell.dbc after attach.
/// </summary>
internal sealed class SpellSweepRig : IDisposable
{
    public const uint MapId = 0;
    public const float BaseX = -8900f;
    public const float BaseY = -120f;
    public const float BaseZ = 82f;

    /// <summary>A hostile, attackable creature the sweep targets.</summary>
    public const uint HostileEntry = 990_801;

    /// <summary>
    /// FactionTemplate 16 (monster): hostile to both player teams and, unlike 14, friendly to itself, so one hostile creature
    /// can cast a friendly dispel on another (a harmful aura on a creature needs a dispeller the target counts as a friend).
    /// </summary>
    public const uint HostileFaction = 16;

    /// <summary>FactionTemplate 35: friendly to everyone.</summary>
    public const uint FriendlyFaction = 35;

    /// <summary>A friendly creature (faction 35: friendly to all).</summary>
    public const uint FriendlyEntry = 990_802;

    private readonly ServiceProvider _services;
    private readonly SweepFaultCapture _faults;
    private int _nextGuid = 50_000;

    public SpellSweepRig(SpellStore store, string? dbcDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _faults = new SweepFaultCapture();
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(_faults);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var accounts = new InMemoryAccountStore();
        var statusEvents = new AccountStatusEvents();
        accounts.Events = statusEvents;
        collection.AddSingleton<IAccountStore>(accounts);
        collection.AddSingleton<IAccountAdmin>(accounts);
        collection.AddSingleton<IAccountSessionClaimStore, InMemoryAccountSessionClaimStore>();
        collection.AddSingleton(sp => new SessionRegistry(sp.GetRequiredService<IAccountSessionClaimStore>()));
        collection.AddSingleton<ISessionClaims>(sp => sp.GetRequiredService<SessionRegistry>());
        collection.AddSingleton(statusEvents);
        collection.AddSingleton<IBanStore>(new ArcaneCore.World.Tests.Bans.InMemoryBanStore(statusEvents));
        var characters = new InMemoryCharacterStore();
        collection.AddSingleton<ICharacterStore>(characters);
        collection.AddSingleton<ICharacterLifeStore>(characters);
        collection.AddSingleton<IAccountDataStore>(new InMemoryAccountDataStore());
        collection.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        collection.AddSingleton(new CharacterDirectory());
        collection.AddSingleton(sp => ChatCommands.CreateTable(sp));
        collection.AddSingleton<CharacterSaveQueue>();
        collection.AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>());
        collection.AddWorldFeatures();
        WorldTestServices.RegisterAll(collection);
        // Registered after the shared test services so it replaces their (empty) configuration: the last registration wins.
        if (dbcDirectory is not null)
        {
            // The client tables the features read from configuration (faction relations, skills, talents, forms, displays), so
            // hostility, dispel polarity and shapeshift checks see the real data instead of the stand-ins.
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(DbcSettings(dbcDirectory)).Build());
        }
        _services = collection.BuildServiceProvider();

        World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 50, AutosaveIntervalMs = 0 },
            _services.GetRequiredService<CharacterSaveQueue>(), new Logger<WorldRuntime>(_faults));
        World.UseManualClockFrom(100_000);
        _services.AttachWorldFeatures(World);
        ArcaneCore.Game.WorldState.WorldStateHooks.For(World).WeatherSettings.Enabled = false;

        Spells = _services.GetRequiredService<SpellFeature>();
        Spells.System.Store = store;
        Opcodes = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Registry = _services.GetRequiredService<SessionRegistry>();
        Map = World.GetMap(MapId);
        Creatures = Map.FindUpdater<CreatureMapSystem>() ?? AddUpdater(new CreatureMapSystem(Map, new CreatureContent([], [], [], [], []), random: new Random(1)));
        Objects = Map.FindUpdater<GameObjectMapSystem>();
        World.RunTick(0); // runs the creature feature's posted (empty) content install first, so the install below is the last one
        _services.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(DisplayTemplates(store), [], [], [], []));
    }

    /// <summary>
    /// Creature entries a Transform or Mounted aura names that the retail world database (classic-db creature_template) has no row for, so
    /// the live server logs them too (vmangos Aura::HandleAuraTransform / HandleAuraMounted: "not found in database"). 6029: spell 8324.
    /// </summary>
    private static readonly HashSet<uint> s_entriesMissingInRetail = [6029];

    /// <summary>The display every stand-in template carries: any real CreatureDisplayInfo model serves, the sweep checks no model.</summary>
    public const uint StandInDisplay = ProductionSweepActions.MountDisplay;

    /// <summary>
    /// The world content the live server has and the sweep would otherwise lack: a creature template for every entry a Transform or Mounted
    /// aura names in its misc value. Both auras read only the template's display (vmangos SpellAuras.cpp, Aura::HandleAuraTransform and
    /// Aura::HandleAuraMounted), so a stand-in with one display is enough. No spawns: nothing else in the world changes.
    /// </summary>
    private static IEnumerable<CreatureTemplate> DisplayTemplates(SpellStore store)
    {
        SortedSet<uint> entries = [];
        foreach (SpellInfo spell in store.All)
        {
            foreach (SpellEffectInfo effect in spell.Effects)
            {
                if (effect.AuraType is AuraType.Transform or AuraType.Mounted && effect.MiscValue > 0 && !s_entriesMissingInRetail.Contains((uint)effect.MiscValue))
                {
                    entries.Add((uint)effect.MiscValue);
                }
            }
        }

        return entries.Select(entry => new CreatureTemplate
        {
            Entry = entry, Name = "Sweep Display " + entry, MinLevel = 1, MaxLevel = 1, DisplayIds = [StandInDisplay], DisplayProbabilities = [1],
        });
    }

    private static Dictionary<string, string?> DbcSettings(string directory)
    {
        string Dbc(string name) => Path.Combine(directory, name + ".dbc");
        return new Dictionary<string, string?>
        {
            ["Creatures:FactionTemplateDbcPath"] = Dbc("FactionTemplate"),
            ["Creatures:CreatureDisplayInfoDbcPath"] = Dbc("CreatureDisplayInfo"),
            ["Creatures:CreatureModelDataDbcPath"] = Dbc("CreatureModelData"),
            ["Quests:FactionTemplateDbcPath"] = Dbc("FactionTemplate"),
            ["Reputation:FactionDbcPath"] = Dbc("Faction"),
            ["Skills:SkillLineDbcPath"] = Dbc("SkillLine"),
            ["Skills:SkillRaceClassInfoDbcPath"] = Dbc("SkillRaceClassInfo"),
            ["Skills:SkillTiersDbcPath"] = Dbc("SkillTiers"),
            ["Skills:SkillLineAbilityDbcPath"] = Dbc("SkillLineAbility"),
            ["Combat:ShapeshiftFormDbcPath"] = Dbc("SpellShapeshiftForm"),
            ["Talents:TalentDbcPath"] = Dbc("Talent"),
            ["Talents:TalentTabDbcPath"] = Dbc("TalentTab"),
            // Without it EnchantingFeature installs the refusing guard and no enchant effect has a handler (the daemon configures it).
            ["Enchanting:SpellItemEnchantmentDbcPath"] = Dbc("SpellItemEnchantment"),
        };
    }

    public IServiceProvider Services => _services;

    public WorldRuntime World { get; }

    public SpellFeature Spells { get; }

    public SpellSystem System => Spells.System;

    public OpcodeTable Opcodes { get; }

    public SessionRegistry Registry { get; }

    public Map Map { get; }

    public CreatureMapSystem Creatures { get; }

    public GameObjectMapSystem? Objects { get; }

    /// <summary>
    /// Exceptions caught since the last <see cref="TakeFaults"/>: every fault the engine counted in <see cref="FaultLedger.Shared"/> (logged
    /// or not, throttled or not) and every exception a rig service logged that the ledger did not count, plus error lines.
    /// </summary>
    public IReadOnlyList<SweepFault> TakeFaults() => _faults.Take();

    private T AddUpdater<T>(T updater) where T : class, IMapUpdater
    {
        Map.AddUpdater(updater);
        return updater;
    }

    /// <summary>Advance game time by <paramref name="ms"/> in world ticks of at most <paramref name="step"/>.</summary>
    public void Advance(uint ms, uint step = 50)
    {
        for (uint done = 0; done < ms;)
        {
            uint diff = Math.Min(step, ms - done);
            World.RunTick(diff);
            done += diff;
        }
    }

    /// <summary>A player of <paramref name="cls"/> at <paramref name="level"/>, standing at the rig origin plus an offset.</summary>
    public Player AddPlayer(Class cls, byte level, float dx = 0, float dy = 0, Race? race = null)
    {
        int id = Interlocked.Increment(ref _nextGuid);
        Race chosen = race ?? DefaultRace(cls);
        var character = new CharacterRecord
        {
            Id = id, AccountId = id, Name = "Sweep" + id, Race = (byte)chosen, Class = (byte)cls, Gender = 0, Level = level, MapId = MapId,
            ZoneId = 12, X = BaseX + dx, Y = BaseY + dy, Z = BaseZ,
        };
        PowerType power = PowerFor(cls);
        uint maxHealth = 100u + (uint)level * 60u;
        uint maxPower = power == PowerType.Mana ? 100u + (uint)level * 50u : power == PowerType.Rage ? 1000u : 100u;
        uint startPower = power == PowerType.Rage ? 1000u : maxPower;
        var appearance = new PlayerAppearance(49, chosen is Race.Orc or Race.Undead or Race.Tauren or Race.Troll ? 2u : 1u, power,
            maxHealth, power == PowerType.Mana ? maxPower : 0, maxHealth, maxPower, startPower, 400);
        var player = new Player(character, appearance, new NullPlayerSession(id, AccountSecurity.Player));
        player.Relocate(character.X, character.Y, character.Z, 0f, 0);
        World.AddPlayer(player, Map);
        World.NotifyLoggedIn(player); // the login subscriptions (stand-state, food/drink) a real login installs
        return player;
    }

    public void RemovePlayer(Player player)
    {
        if (player.IsInWorld)
        {
            World.RemovePlayer(player);
        }
    }

    public Creature AddCreature(bool hostile, byte level, float dx, float dy)
    {
        var template = new CreatureTemplate
        {
            Entry = hostile ? HostileEntry : FriendlyEntry, Name = hostile ? "Sweep Hostile" : "Sweep Friendly", MinLevel = level, MaxLevel = level,
            MinLevelHealth = 1_000_000, MaxLevelHealth = 1_000_000, MinLevelMana = 5000, MaxLevelMana = 5000, Faction = hostile ? HostileFaction : FriendlyFaction, Civilian = false,
        };
        return Creatures.SpawnTemporary(template, BaseX + dx, BaseY + dy, BaseZ, 0f);
    }

    public void RemoveCreature(Creature creature)
    {
        if (creature.IsInWorld)
        {
            Creatures.Despawn(creature);
        }
    }

    public static PowerType PowerFor(Class cls) => cls switch
    {
        Class.Warrior => PowerType.Rage,
        Class.Rogue => PowerType.Energy,
        _ => PowerType.Mana,
    };

    private static Race DefaultRace(Class cls) => cls switch
    {
        Class.Shaman => Race.Orc,
        Class.Druid => Race.NightElf,
        Class.Hunter => Race.Dwarf,
        Class.Paladin => Race.Human,
        Class.Warlock => Race.Human,
        _ => Race.Human,
    };

    public void Dispose()
    {
        try
        {
            _services.StopWorldFeaturesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Feature shutdown is not under test; a fault here is still recorded by the capture.
            _faults.Record("rig.dispose", ex);
        }

        World.Dispose();
        _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>One exception a rig service logged.</summary>
internal sealed record SweepFault(string Category, string Type, string Message, string TopFrame);

/// <summary>
/// The rig's fault capture, so an exception the engine catches is never a silent pass. Two sources:
/// <list type="bullet">
/// <item><see cref="FaultLedger.Shared"/>, which every catch site records into (FaultLedger.Record, and LogFault through it) whether or not
/// it logs; its throttle only limits the log lines, the counts always move. <see cref="Take"/> reports each signature whose count went up.
/// The ledger is process-wide, so the sweep tests run in a non-parallel collection (<see cref="SpellContractSweepCollection"/>).</item>
/// <item>The rig's logger factory: an exception logged at any level that the ledger did not count, and an Error line without one.</item>
/// </list>
/// </summary>
internal sealed class SweepFaultCapture : ILoggerFactory
{
    /// <summary>The <see cref="SweepFault.Type"/> of an error-level line that carries no exception.</summary>
    public const string ErrorLogType = "error-log";

    private readonly List<SweepFault> _faults = [];
    private readonly object _gate = new();
    private Dictionary<string, long> _ledgerCounts = [];
    private long _ledgerTotal = -1;
    private long _ledgerEpoch = -1;

    /// <summary>Report the ledger signatures whose counts rose since the last call (the first call only takes the starting counts).</summary>
    private void DrainLedger()
    {
        FaultLedger ledger = FaultLedger.Shared;
        long total = ledger.Total;
        long epoch = ledger.Epoch;
        if (total == _ledgerTotal && epoch == _ledgerEpoch)
        {
            return;
        }

        bool first = _ledgerTotal < 0;
        if (epoch != _ledgerEpoch)
        {
            // A Reset cleared the counts: every entry since then is new.
            _ledgerCounts = [];
        }

        _ledgerTotal = total;
        _ledgerEpoch = epoch;
        Dictionary<string, long> counts = [];
        foreach (FaultSnapshot entry in ledger.Snapshot())
        {
            counts[entry.Id] = entry.Count;
            if (!first && entry.Count > _ledgerCounts.GetValueOrDefault(entry.Id))
            {
                _faults.Add(new SweepFault(entry.Site, entry.ExceptionType, entry.SampleMessage, entry.Frames.Count > 0 ? entry.Frames[0] : ""));
            }
        }

        _ledgerCounts = counts;
    }

    public void Record(string category, Exception exception)
    {
        lock (_gate)
        {
            _faults.Add(new SweepFault(category, exception.GetType().Name, exception.Message, TopFrame(exception)));
        }
    }

    public IReadOnlyList<SweepFault> Take()
    {
        lock (_gate)
        {
            DrainLedger();
            SweepFault[] taken = [.. _faults];
            _faults.Clear();
            return taken;
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    internal static string TopFrame(Exception exception)
    {
        string? trace = exception.StackTrace;
        if (string.IsNullOrEmpty(trace))
        {
            return "";
        }

        string first = trace.Split('\n', 2)[0].Trim();
        return first.StartsWith("at ", StringComparison.Ordinal) ? first[3..] : first;
    }

    private sealed class CaptureLogger(SweepFaultCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Enabled at every level: a caller that checks IsEnabled first ([LoggerMessage], LoggerMessage.Define) would otherwise drop a
        // fault logged as a warning. The filtering is in Log.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                // A LogFault site counted it in the ledger first: the ledger reports it (once, unthrottled).
                if (!FaultLedger.WasRecorded(exception))
                {
                    owner.Record(category, exception);
                }
            }
            else if (logLevel >= LogLevel.Error)
            {
                lock (owner._gate)
                {
                    owner._faults.Add(new SweepFault(category, ErrorLogType, formatter(state, null), ""));
                }
            }
        }
    }
}
