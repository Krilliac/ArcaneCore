using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Procs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// The world binding of the proc engine (docs/areas/procs.md): the spell_proc_event table is loaded and handed to the spell system,
/// <c>.reload spell_proc_event</c> swaps it, and a kill on a map procs the killer's KILL auras (vmangos Unit::Kill, Unit.cpp:1102-1104).
/// </summary>
public sealed class SpellProcFeatureTests
{
    private const uint KillAura = 993_001;
    private const uint KillBuff = 993_002;

    // A spell the default test content holds: the table drops rows of spells that do not exist (vmangos SpellRankHelper::RecordRank).
    private static readonly uint Known = SpellTestServices.Content().Spells[0].Id;
    private static readonly uint OtherKnown = SpellTestServices.Content().Spells[1].Id;

    [Fact]
    public async Task TheTable_IsLoadedFromTheStore_AndIsTheSpellSystemsCatalog()
    {
        var store = new MutableStore(new SpellProcEventRecord(Known, 0, 0, 0, 0, 0, 0, 0, 0, 25, 0));
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellProcEventDataStore>(store));
        SpellProcFeature feature = host.WorldServices.GetRequiredService<SpellProcFeature>();

        await host.OnWorldAsync(() =>
        {
            Assert.Same(feature.Table, host.WorldServices.GetRequiredService<SpellFeature>().System.ProcEvents);
            Assert.Equal(25f, feature.Table.Find(Known)!.CustomChance);
            return true;
        });
    }

    [Fact]
    public async Task ReloadSpellProcEvent_SwapsTheRows()
    {
        var store = new MutableStore(new SpellProcEventRecord(Known, 0, 0, 0, 0, 0, 0, 0, 0, 25, 0));
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellProcEventDataStore>(store));
        var services = new ServiceCollection()
            .AddSingleton(host.WorldServices.GetRequiredService<SpellProcFeature>())
            .AddSingleton<ISpellProcEventDataStore>(store)
            .BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new SpellProcEventReloadable(services));
        store.Rows = [new SpellProcEventRecord(Known, 0, 0, 0, 0, 0, 0, 0, 0, 50, 0), new SpellProcEventRecord(OtherKnown, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0)];

        ReloadResult result = await coordinator.ReloadAsync("spell_proc_event");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        SpellProcFeature feature = host.WorldServices.GetRequiredService<SpellProcFeature>();
        Assert.Equal(50f, await host.OnWorldAsync(() => feature.Table.Find(Known)!.CustomChance));
        Assert.Equal(2, await host.OnWorldAsync(() => feature.Table.Content.Count));
    }

    [Fact]
    public async Task AKillOnAMap_ProcsTheKillersKillAura()
    {
        SpellContent baseContent = SpellTestServices.Content();
        SpellTemplateRow[] rows =
        [
            new SpellTemplateRow
            {
                Id = KillAura, SpellName = "Kill Proc", RangeIndex = 1, DurationIndex = 21, SpellVisual = 1, ProcFlags = 0x2, ProcChance = 100,
                Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = -1, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 42,
                EffectTriggerSpell1 = KillBuff,
            },
            new SpellTemplateRow
            {
                Id = KillBuff, SpellName = "Kill Buff", RangeIndex = 1, DurationIndex = 3, SpellVisual = 1,
                Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 4, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 22,
                EffectMiscValue1 = 1,
            },
        ];
        await using var host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(baseContent with { Spells = [.. baseContent.Spells, .. rows] })));
        await using WorldTestClient killerClient = await host.EnterWorldAsync("PROCKILL", "Prockill");
        await using WorldTestClient victimClient = await host.EnterWorldAsync("PROCDEAD", "Procdead");

        uint appliedAt = await host.OnWorldAsync(() =>
        {
            Player killer = host.World.FindOnlinePlayer("Prockill")!;
            var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            spells.CastSpell(killer, KillAura, ArcaneCore.Game.Spells.SpellCastTargets.ForSelf(), triggered: true);
            Assert.True(spells.HasAura(killer, KillAura));
            return host.World.NowMs;
        });

        // The killer's own aura procs only from a kill after the millisecond it was applied in (vmangos Unit.cpp:8958: apply time >= proc time skips it).
        while (host.World.NowMs == appliedAt)
        {
            await Task.Delay(1);
        }

        bool buffed = await host.OnWorldAsync(() =>
        {
            Player killer = host.World.FindOnlinePlayer("Prockill")!;
            Player victim = host.World.FindOnlinePlayer("Procdead")!;
            var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            killer.Level = 10;
            victim.Level = 10; // above the killer's gray level: an honor-or-XP target

            killer.Map!.FindUpdater<MapCombat>()!.Kill(killer, victim);

            return spells.HasAura(killer, KillBuff);
        });

        Assert.True(buffed);
    }

    private sealed class MutableStore(params SpellProcEventRecord[] rows) : ISpellProcEventDataStore
    {
        public SpellProcEventRecord[] Rows { get; set; } = rows;

        public Task<SpellProcEventContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SpellProcEventContent(Rows));
    }
}
