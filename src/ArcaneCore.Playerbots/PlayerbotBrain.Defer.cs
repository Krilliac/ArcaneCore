using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The brain's side of the defer ledger (<see cref="PlayerbotDeferLedger"/>): once per alive think it compares the bot and its world
/// with what it saw at the last think and wakes the deferrals waiting for what changed (<see cref="DeferWake"/>).
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>The deferral state, built together so the stall watch's pin cells and the set-asides share one ledger from construction.</summary>
    private sealed class DeferState
    {
        public DeferState(PlayerbotSharedSetAsides? shared, ArcaneCore.Game.Maps.WorldRuntime world)
        {
            var ledger = new PlayerbotDeferLedger();
            Suspensions = new PlayerbotSuspensions(ledger, world) { Shared = shared };
            Stall = new PlayerbotStallWatch { Ledger = ledger, World = world };
        }

        public PlayerbotSuspensions Suspensions { get; }
        public PlayerbotStallWatch Stall { get; }
    }

    private readonly DeferState _defer = new(shared, session.World);
    private DeferSnapshot _deferSeen;
    private CreatureWorldFeature? _deferCreatures;
    private QuestNpcFeature? _deferQuests;
    private bool _deferFeaturesLooked;

    /// <summary>The dead-end pins every bot shares (<see cref="PlayerbotSharedPins"/>); set by the feature at construction, null in a host or test without it.</summary>
    internal PlayerbotSharedPins? SharedPins { init => _defer.Stall.SharedPins = value; }

    private PlayerbotSuspensions _suspensions => _defer.Suspensions;
    private PlayerbotStallWatch _stall => _defer.Stall;

    /// <summary>What the last probe saw. A struct inside the brain: no allocation per probe.</summary>
    private struct DeferSnapshot
    {
        public bool Valid;
        public uint Level;
        public object? Nav;
        public Guid NavIdentity;
        public CreatureContent? Content;
        public int DefinitionsVersion;
        public QuestStore? Quests;
    }

    /// <summary>
    /// Wake the deferrals whose cause happened since the previous probe: a level, a replaced navigation dataset, creature definitions or
    /// quest store. Every probe records what it saw (a handful of field reads through feature references looked up once per brain, an
    /// absent feature included), so a swap between a deferral beginning and the next think still wakes it; only a change calls the
    /// ledger. The first probe only records. World thread, once per alive think; no allocation (a hand-built map without a collision updater
    /// makes its open-default <c>MapCollision</c> on every <c>Map.Collision</c> read, which only tests have).
    /// </summary>
    internal void ObserveDeferWakes(Player player)
    {
        PlayerbotDeferLedger ledger = _suspensions.Ledger;
        ref DeferSnapshot seen = ref _deferSeen;
        if (!_deferFeaturesLooked)
        {
            _deferCreatures = _session.Services.GetService<CreatureWorldFeature>();
            _deferQuests = _session.Services.GetService<QuestNpcFeature>();
            _deferFeaturesLooked = true;
        }

        DeferWake causes = DeferWake.None;
        uint level = player.Level;
        object? nav = player.Map?.Collision.Pathfinder;
        Guid identity = nav is NavMeshPathfinder mesh ? mesh.Dataset.Identity : default;
        CreatureContent? content = _deferCreatures?.Content;
        int version = content?.DefinitionsVersion ?? 0;
        QuestStore? quests = _deferQuests?.Services.Quests;
        if (seen.Valid)
        {
            if (level > seen.Level) causes |= DeferWake.Level;
            if (!ReferenceEquals(nav, seen.Nav) || identity != seen.NavIdentity) causes |= DeferWake.NavDataset;
            if (!ReferenceEquals(content, seen.Content) || version != seen.DefinitionsVersion) causes |= DeferWake.Definitions;
            if (!ReferenceEquals(quests, seen.Quests)) causes |= DeferWake.QuestStore;
        }

        seen.Valid = true;
        seen.Level = level;
        seen.Nav = nav;
        seen.NavIdentity = identity;
        seen.Content = content;
        seen.DefinitionsVersion = version;
        seen.Quests = quests;
        if (causes == DeferWake.None) return;
        long now = PlayerbotWorldClock.UptimeMs(_session.World);
        if ((ledger.WaitingAt(now) & causes) == DeferWake.None) return;
        ledger.Seed = player.Guid.Value;
        ledger.WakeAll(causes, now);
    }
}

/// <summary>
/// The ledger's clock (<see cref="PlayerbotDeferLedger"/>): the world's 64-bit uptime in milliseconds, which does not wrap. Callers hold the
/// 32-bit <c>NowMs</c>, which does; <see cref="Ms(ArcaneCore.Game.Maps.WorldRuntime, uint)"/> places such a time on the uptime clock relative to
/// the world's present (one uptime read), so a time within 24 days of it (any a bot reads or sets) is placed exactly, however long ago the
/// bot last looked. No state: any thread, and a facade converts once per call.
/// </summary>
internal static class PlayerbotWorldClock
{
    /// <summary>Milliseconds since the world was created.</summary>
    internal static long UptimeMs(ArcaneCore.Game.Maps.WorldRuntime world) => (long)world.Uptime.TotalMilliseconds;

    /// <summary>The uptime-clock time of the world time <paramref name="nowMs"/> (earlier or later than the world's present by less than 2^31 ms).</summary>
    internal static long Ms(ArcaneCore.Game.Maps.WorldRuntime world, uint nowMs)
    {
        long uptime = UptimeMs(world);
        return uptime + unchecked((int)(nowMs - (uint)uptime));
    }
}
