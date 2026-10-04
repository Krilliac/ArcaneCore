using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// The combat feature installs the reputation hooks and creature hostility in a real world process, and only when both
/// Faction.dbc and the FactionTemplate catalog are loaded (a half-configured world stays template-only and says so).
/// </summary>
public sealed class ReputationCombatWorldTests
{
    private const uint Stormwind = 72;
    private const uint GuardEntry = 68;
    private const uint GuardSpawnGuid = 7700;

    // Template 1 is the human player template; 11 is a Stormwind guard (faction 72) friendly to it.
    private static readonly FactionTemplateCatalog Templates = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 1, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(11, Stormwind, 0, OwnMask: 2, FriendlyMask: 1, HostileMask: 8),
    ]);

    private static WorldTestHost Start(bool factions, bool templates, out CreatureTestContext context)
    {
        var guard = new CreatureTemplate
        {
            Entry = GuardEntry, Name = "Stormwind City Guard", MinLevel = 60, MaxLevel = 60, DisplayIds = [3167], Faction = 11,
            CreatureType = 7, MinLevelHealth = 4000, MaxLevelHealth = 4000,
        };
        var spawn = new CreatureSpawn { Guid = GuardSpawnGuid, Entry = GuardEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        context = new CreatureTestContext(new CreatureContent([guard], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        if (factions)
        {
            ReputationTestServices.Current.Value = new MemoryReputationStore();
        }

        if (templates)
        {
            ReputationTestServices.Templates.Value = Templates;
        }

        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            ReputationTestServices.Current.Value = null;
            ReputationTestServices.Templates.Value = null;
        }
    }

    private static ReputationCombatFeature CombatFeature(WorldTestHost host) => host.WorldServices.GetRequiredService<ReputationCombatFeature>();

    [Fact]
    public async Task WithBothCatalogs_TheHooksAndTheCreatureHostilityFollowReputation()
    {
        await using WorldTestHost host = Start(factions: true, templates: true, out CreatureTestContext context);
        ReputationCombatFeature combat = CombatFeature(host);
        Assert.True(combat.IsActive, combat.InactiveReason);
        Assert.Same(combat, context.Feature!.AiServices.Hostility);
        Assert.IsType<ReputationCombatHooks>(CombatHooks.For(host.World));

        await using WorldTestClient client = await host.EnterWorldAsync("REPCOMBAT", "Repcombat");
        await host.PlaceAsync("Repcombat", -8940f, -132f, 83.5f);
        await host.WaitForWorldAsync(() => context.Feature!.FindSystem(0)?.FindCreature(new ObjectGuid(GuardGuid())) is not null, "the guard spawns");

        // A new human is Neutral with Stormwind: the guard is friendly, not hostile and not attackable.
        Assert.False(await host.OnWorldAsync(() => Probe(host, context, out _, out _)));
        Assert.False(await host.OnWorldAsync(() => { Probe(host, context, out _, out bool attackable); return attackable; }));

        // Hated: war is declared and the guard turns on him, in aggro and in attackability.
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Repcombat")!;
            var feature = ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();
            Assert.True(feature.Service.SetReputation(player, Stormwind, ReputationMath.Bottom));
        });
        Assert.True(await host.OnWorldAsync(() => Probe(host, context, out _, out _)));
        Assert.True(await host.OnWorldAsync(() => { Probe(host, context, out _, out bool attackable); return attackable; }));
    }

    [Fact]
    public async Task WithoutFactionData_NothingIsInstalled_AndTheReasonIsRecorded()
    {
        await using WorldTestHost host = Start(factions: false, templates: true, out CreatureTestContext context);
        ReputationCombatFeature combat = CombatFeature(host);
        Assert.False(combat.IsActive);
        Assert.Contains("Reputation:FactionDbcPath", combat.InactiveReason);
        Assert.IsType<FactionCombatHooks>(CombatHooks.For(host.World)); // the template-only hooks stay
        Assert.NotNull(context.Feature);
    }

    [Fact]
    public async Task WithFactionsButNoTemplates_TheHalfConfiguredWorldIsReportedInactive()
    {
        await using WorldTestHost host = Start(factions: true, templates: false, out _);
        ReputationCombatFeature combat = CombatFeature(host);
        Assert.False(combat.IsActive);
        Assert.Contains("FactionTemplateDbcPath", combat.InactiveReason);
    }

    private static ulong GuardGuid() => ObjectGuid.WithEntry(HighGuid.Unit, GuardEntry, GuardSpawnGuid).Value;

    private static bool Probe(WorldTestHost host, CreatureTestContext context, out Creature creature, out bool attackable)
    {
        Player player = host.World.FindOnlinePlayer("Repcombat")!;
        creature = context.Feature!.FindSystem(0)!.FindCreature(new ObjectGuid(GuardGuid()))!;
        attackable = CombatHooks.For(host.World).CanAttack(player, creature);
        return context.Feature.AiServices.Hostility.IsHostile(creature, player);
    }
}
