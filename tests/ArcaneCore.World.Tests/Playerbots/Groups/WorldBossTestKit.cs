using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The shared world of the world-boss raid tests (<see cref="PlayerbotWorldBossWorldTests"/>, <see cref="PlayerbotWorldBossFormingWorldTests"/>):
/// the group test world with one weakened event boss in the catalogue (<see cref="BossEntry"/>, listed as <c>entry@0</c> with the content
/// derivation off, so it has a <c>Placed</c> locator and the test places it with <c>SpawnTemporary</c>), small raid sizes, no player grace and
/// short timers; the bots, the boss and the meter capture.
/// </summary>
internal static class WorldBossTestKit
{
    /// <summary>The xunit collection of every test that reads the world-boss meter: the counter is process-wide, so those tests run one at a time.</summary>
    internal const string MeterCollection = "PlayerbotWorldBossMeter";

    internal const uint BossEntry = 990850, SecondBossEntry = 990851;
    internal const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    internal const uint LesserHeal = 2050, Fireball = 133;

    /// <summary>00:00 UTC of a day plus an hour: the campaigns' clock starts here, so a UTC midnight never falls inside a test.</summary>
    internal const long BaseUnix = 20_000L * 86_400 + 3_600;

    /// <summary>The boss stands 150 yards east of the start, on the flat ground of the group test world.</summary>
    internal static readonly Vector3 BossAt = new(StartX + 150f, StartY, StartZ);

    /// <summary>A rank-3 event boss: <paramref name="health"/> and <paramref name="damage"/> choose whether a raid of six kills it, or it kills the raid, or it only stands and fights one player.</summary>
    internal static CreatureTemplate Boss(uint entry = BossEntry, byte level = 60, uint health = 400, float damage = 5f) => new()
    {
        Entry = entry, Name = "Test Wyrm", Faction = 14, CreatureType = 2, MinLevel = level, MaxLevel = level, DisplayIds = [903],
        MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = damage, MaxMeleeDamage = damage, MeleeBaseAttackTime = 8000, Rank = 3, // a slow swing: the level-60 attack-power part of every blow lands a 72-health bot, and the test raid (no real healing, a mage out-damaging 400 health in ~50 s) must win the race on any dice
    };

    /// <summary>A boss no raid kills and that kills the raid in a blow or two.</summary>
    internal static CreatureTemplate Unbeatable(byte level = 60) => Boss(level: level, health: 20_000_000, damage: 5000f) with { MeleeBaseAttackTime = 2000 };

    /// <summary>A boss that hits for nothing and never dies: it holds whoever tapped it for as long as the test wants.</summary>
    internal static CreatureTemplate Passive() => Boss(health: 5_000_000, damage: 0f) with { MeleeBaseAttackTime = 3_600_000 }; // a swing an hour: damage 0 still lands the level-60 attack-power part, which kills a level-1 player in half a minute

    internal static void Tune(PlayerbotOptions options, Action<PlayerbotWorldBossOptions>? boss)
    {
        options.MaxBots = Math.Max(options.MaxBots, 12);
        options.Groups.Cooperation.RecruitBaseChance = 1f;
        options.Groups.DungeonRuns.Enabled = false;
        PlayerbotWorldBossOptions w = options.Groups.WorldBosses;
        w.PlayerGraceMinutes = 0;
        w.WatchSeconds = 5;
        w.MinRaidSize = 5;
        w.DefaultRaidSize = 6;
        w.DeriveFromContent = false;
        w.IncludedEntries = $"{BossEntry}@0";
        w.MusterWindowSeconds = 120;
        w.RallyYards = 20f;
        w.PullWaitSeconds = 30;
        w.HoldSeconds = 60;
        w.RetrySeconds = 60;
        boss?.Invoke(w);
    }

    internal static async Task<GroupTestWorld> StartAsync(Action<PlayerbotWorldBossOptions>? boss = null, Action<PlayerbotOptions>? configure = null, params CreatureTemplate[] templates)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([], options =>
        {
            Tune(options, boss);
            configure?.Invoke(options);
        }, extraTemplates: templates.Length == 0 ? [Boss()] : templates);
        world.Coordinator.UnixNow = () => BaseUnix + world.World.Host.World.NowMs / 1000;
        return world;
    }

    /// <summary>Level-60 Alliance bots of the given roles: warriors (tanks), priests (healers) and mages (damage), at the start.</summary>
    internal static async Task<List<(Guid Id, string Name)>> AddBotsAsync(GroupTestWorld world, string prefix, int tanks, int healers, int damage, byte level = 60)
    {
        var bots = new List<(Guid Id, string Name)>();
        for (int i = 0; i < tanks; i++) bots.Add(await world.AddBotAsync($"{prefix}tank{Letters(i)}", Human, Warrior, level, [Taunt]));
        for (int i = 0; i < healers; i++) bots.Add(await world.AddBotAsync($"{prefix}heal{Letters(i)}", Human, Priest, level, [LesserHeal, Resurrection]));
        for (int i = 0; i < damage; i++) bots.Add(await world.AddBotAsync($"{prefix}mage{Letters(i)}", Human, Mage, level, [Fireball]));
        return bots;
    }

    /// <summary>Character names take letters only: 0 is "a", 26 is "ba".</summary>
    private static string Letters(int index)
    {
        string text = "";
        do
        {
            text = (char)('a' + index % 26) + text;
            index /= 26;
        }
        while (index > 0);
        return text;
    }

    /// <summary>Place the event boss (a temporary spawn, no database row) on map 0, as <c>PlayerbotRaidEscalationWorldTests</c> places its elite.</summary>
    internal static Task<Creature> SpawnAsync(GroupTestWorld world, Guid onMapOf, CreatureTemplate template, Vector3 at) => world.OnWorldAsync(() =>
    {
        Player player = world.Player(onMapOf);
        var system = player.Map!.FindUpdater<CreatureMapSystem>()!;
        return system.SpawnTemporary(template, at.X, at.Y, at.Z, 0f);
    });

    internal static PlayerbotGroupCoordinator.BotGroup? Raid(GroupTestWorld world) => world.Coordinator.Groups.FirstOrDefault(g => g.WorldBoss is not null);

    /// <summary>The leader's current step, "none" when there is no leader AI.</summary>
    internal static string LeaderStep(GroupTestWorld world, PlayerbotGroupCoordinator.BotGroup group)
        => world.Coordinator.FindAI(group.LeaderBotId)?.Step ?? "none";

    internal static bool ThreatenedByAnyOf(Creature boss, IEnumerable<Player> players)
    {
        if (!boss.Combat.HasThreatList) return false;
        HashSet<ObjectGuid> guids = [.. players.Select(p => p.Guid)];
        foreach (ThreatEntry entry in boss.Combat.Threat.Entries)
            if (entry.Target is Player target && guids.Contains(target.Guid)) return true;
        return false;
    }

    /// <summary>The members' players, read on the world thread.</summary>
    internal static List<Player> MembersOf(GroupTestWorld world, PlayerbotGroupCoordinator.BotGroup group)
        => [.. group.Members.Where(m => !m.Real).Select(m => world.Bots.FindSession(m.BotId)?.Player).OfType<Player>()];

    /// <summary>Counts of <c>arcanecore.playerbot.worldboss_raids</c> by outcome since this object was made.</summary>
    internal sealed class OutcomeCounts : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new();

        internal OutcomeCounts()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "ArcaneCore.Playerbots.WorldBosses" && instrument.Name == "arcanecore.playerbot.worldboss_raids") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (KeyValuePair<string, object?> tag in tags)
                    if (tag.Key == "outcome" && tag.Value is string outcome) _counts.AddOrUpdate(outcome, value, (_, old) => old + value);
            });
            _listener.Start();
        }

        internal long Of(string outcome) => _counts.GetValueOrDefault(outcome);

        internal long Total => _counts.Values.Sum();

        public override string ToString() => string.Join(", ", _counts.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"));

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The raid warnings the members' clients received: chat type RaidWarning in SMSG_MESSAGECHAT, as the siege tests read them.</summary>
    internal sealed class RaidWarningLog
    {
        private readonly ConcurrentQueue<string> _lines = new();

        internal int Count => _lines.Count;

        internal string[] Lines => [.. _lines];

        /// <summary>World thread: listen on the session of <paramref name="botId"/>, keeping any observer already there.</summary>
        internal void Listen(GroupTestWorld world, Guid botId)
        {
            WorldSession session = world.Session(botId);
            Action<WorldOpcode, byte[]>? prior = session.ManagedPacketObserver;
            session.ManagedPacketObserver = (opcode, payload) =>
            {
                prior?.Invoke(opcode, payload);
                if (opcode == WorldOpcode.SmsgMessagechat && payload.Length > 0 && payload[0] == (byte)ChatType.RaidWarning)
                    _lines.Enqueue(ChatMessage.Parse(payload).Text);
            };
        }
    }

    /// <summary>The warning kinds the raid recorded, in order.</summary>
    internal static PlayerbotWorldBossWarning[] WarningsOf(PlayerbotGroupCoordinator.BotGroup group) => [.. group.WorldBoss!.Warnings];

    internal static bool Noted(GroupTestWorld world, string fragment) => world.Coordinator.Events.Any(e => e.Contains(fragment, StringComparison.Ordinal));
}
