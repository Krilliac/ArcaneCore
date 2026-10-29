using System.Globalization;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Registers every built-in write-behind queue with a <see cref="PersistenceProbeRegistry"/>, one line per queue. A queue is looked
/// up lazily (a feature's queue exists only once the feature loaded), so the registration order does not matter. A new persistence
/// queue gets one <c>Queue(...)</c> line here, or registers itself on the registry from its own feature.
/// </summary>
public static class WorldPersistenceProbes
{
    /// <summary>A registry holding the built-in queues, resolved against <paramref name="services"/>.</summary>
    public static PersistenceProbeRegistry CreateRegistry(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registry = new PersistenceProbeRegistry();
        RegisterBuiltIn(registry, services);
        return registry;
    }

    /// <summary>Add the built-in queues to <paramref name="registry"/>.</summary>
    public static void RegisterBuiltIn(PersistenceProbeRegistry registry, IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sp);

        // Per-character queues
        Queue(registry, "character saves", () => sp.GetService<CharacterSaveQueue>(), q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "social", () => sp.GetService<SocialWriteQueue>() ?? sp.GetService<SocialFeature>()?.Writes, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "reputation", () => sp.GetService<ReputationWriteQueue>() ?? sp.GetService<ReputationFeature>()?.Writes, q => q.Pending, q => Characters(q.RetainedCharacters));
        Queue(registry, "instances", () => sp.GetService<InstanceWriteQueue>() ?? sp.GetService<InstanceFeature>()?.Writes, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "honor", () => sp.GetService<HonorWriteQueue>() ?? sp.GetService<HonorFeature>()?.Writes, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "quests and taxi mask", () => sp.GetService<QuestNpcPersistence>() ?? sp.GetService<QuestNpcFeature>()?.Persistence, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "spell state", () => sp.GetService<SpellStatePersistence>() ?? sp.GetService<SpellFeature>()?.State, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "rest", () => sp.GetService<RestWriteQueue>() ?? sp.GetService<RestFeature>()?.Writes, q => q.Pending, terminal: q => q.TerminalFailure);
        Queue(registry, "explored zones", () => sp.GetService<ExploredZonesWriteQueue>() ?? sp.GetService<ExploredZonesPersistence>()?.Writes, q => q.Pending, terminal: q => q.TerminalFailure);
        Queue(registry, "taxi routes", () => sp.GetService<TaxiFlightWriteQueue>() ?? sp.GetService<NpcServicesFeature>()?.RouteWrites, q => q.Pending, q => Characters(q.RetainedCharacters), q => q.TerminalFailure);
        Queue(registry, "spellbook", () => sp.GetService<SpellbookCache>() ?? sp.GetService<SpellFeature>()?.Spellbook, q => q.PendingWrites, terminal: q => q.TerminalFailure);
        Queue(registry, "talents", () => sp.GetService<TalentPersistence>() ?? sp.GetService<TalentFeature>()?.Persistence, q => q.PendingWrites, terminal: q => q.TerminalFailure);
        Queue(registry, "skills", () => sp.GetService<SkillSaveCoordinator>() ?? sp.GetService<SkillsFeature>()?.Saves, q => q.Pending, terminal: q => q.TerminalFailure);
        Queue(registry, "pets", () => sp.GetService<SummonService>() ?? sp.GetService<PetsFeature>()?.Service, q => q.PendingPersistenceWrites, q => Characters(q.RetainedPersistenceCharacters));

        // World queues
        Queue(registry, "creature respawns", () => sp.GetService<CreatureRespawnFeature>()?.Persistence as CreatureRespawnQueue, q => q.Pending, q => q.RetainedKeys);

        // Keyed store queues
        Queue(registry, "gm audit", () => sp.GetService<GmAuditFeature>()?.Writes, q => q.Pending, q => q.RetainedKeys, retainedFailure: q => q.LastFailure);
        Queue(registry, "groups", () => sp.GetService<SocialGroupPersistenceFeature>()?.Writes, q => q.Pending, q => q.RetainedKeys, retainedFailure: q => q.LastFailure);
        Queue(registry, "account addresses", () => sp.GetService<AccountAddressFeature>()?.Writes, q => q.Pending, q => q.RetainedKeys, retainedFailure: q => q.LastFailure);
        Queue(registry, "account data", () => sp.GetService<Handlers.AccountDataFeature>()?.Writes, q => q.Pending, q => q.RetainedKeys, retainedFailure: q => q.LastFailure);
    }

    private static void Queue<T>(
        PersistenceProbeRegistry registry,
        string name,
        Func<T?> find,
        Func<T, int> pending,
        Func<T, IReadOnlyList<string>>? retained = null,
        Func<T, Exception?>? terminal = null,
        Func<T, Exception?>? retainedFailure = null)
        where T : class
        => registry.Register(name, () => find() is { } queue
            ? new DelegatePersistenceQueueProbe(
                name,
                () => pending(queue),
                retained is null ? null : () => retained(queue),
                terminal is null ? null : () => terminal(queue),
                retainedFailure is null ? null : () => retainedFailure(queue))
            : null);

    private static IReadOnlyList<string> Characters(IReadOnlyList<int> ids)
        => ids.Count == 0 ? [] : [.. ids.Select(id => string.Create(CultureInfo.InvariantCulture, $"char:{id}"))];
}
