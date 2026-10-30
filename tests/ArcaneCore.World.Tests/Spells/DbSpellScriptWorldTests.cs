using ArcaneCore.Data.Content;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// The spell DB scripts end to end: the classic-db <c>dbscripts_on_spell</c> and vmangos <c>spell_scripts</c> excerpts
/// (<see cref="DbSpellScriptRows"/>) go through the content importer into a world SQLite database, the creature store loads them, and the
/// composed world runs a spell's script when its DUMMY or SCRIPT_EFFECT effect hits (cmangos Spell::EffectDummy / EffectScriptEffect,
/// Spells/SpellEffects.cpp:1517-1532 and :4176-4191; vmangos Spell::EffectScriptEffect, SpellEffects.cpp:4565-4571). The scripted
/// spells are stand-ins with the ids and the effect layout of Spell.dbc; the spells they cast are marker auras.
/// </summary>
public sealed class DbSpellScriptWorldTests
{
    private const uint LunarCapStormwind = 26406;
    private const uint TeleportStormwind = 26454;
    private const uint PortToMoonglade = 26451;
    private const uint RiftSpawnManifestation = 9096;
    private const uint RiftSpawnBecomesVisible = 9738;
    private const byte Human = 1;
    private const byte Orc = 2;

    private static readonly Lazy<Task<(CreatureContent Creatures, IReadOnlyList<ConditionRecord> Conditions)>> s_content = new(ImportAsync);

    [Fact]
    public async Task ImportedRows_TranslateTheVmangosScript_AndResolveItsConditionsByMeaning()
    {
        (CreatureContent creatures, _) = await s_content.Value;

        RelayScriptStep rift = Assert.Single(creatures.Ai.DbScripts.Get(DbScriptKind.Spell, RiftSpawnManifestation));
        Assert.Equal((15u, RiftSpawnBecomesVisible, 0u), (rift.Command, rift.DataLong, rift.DataFlags));

        IReadOnlyList<RelayScriptStep> cap = creatures.Ai.DbScripts.Get(DbScriptKind.Spell, LunarCapStormwind);
        Assert.Equal([TeleportStormwind, PortToMoonglade], cap.Select(s => s.DataLong));
        // data_flags 5 (swap initial targets, target self) is REVERSE_DIRECTION | SOURCE_TARGETS_SELF; CF_TRIGGERED is TRIGGERED_OLD_TRIGGERED.
        Assert.All(cap, s => Assert.Equal((15u, 0x06u, 0x01u), (s.Command, s.DataFlags, s.DataLong2)));
        // vmangos condition 3 (Alliance) is classic-db 4, vmangos 2 (Horde) classic-db 3: matched by meaning, never by id.
        Assert.Equal([4u, 3u], cap.Select(s => s.ConditionId));
    }

    [Theory]
    [InlineData(Human, TeleportStormwind, PortToMoonglade)]
    [InlineData(Orc, PortToMoonglade, TeleportStormwind)]
    public async Task LunarTeleportCap_ScriptEffect_CastsTheTeamsSpellOnTheCaster(byte race, uint expected, uint notExpected)
    {
        await using WorldTestHost host = await StartAsync();
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        await host.OnWorldAsync(() => InstallSpells(spells));
        await using WorldTestClient client = await host.EnterWorldAsync("LUNARCAP" + race, "Lunarcap" + (char)('a' + race), race: race);
        Player player = await host.PlayerAsync("Lunarcap" + (char)('a' + race));

        await host.OnWorldAsync(() =>
        {
            AttachScriptExecutor(host, player);
            Assert.False(spells.HasAura(player, expected));
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, LunarCapStormwind, SpellCastTargets.ForUnit(player.Guid), triggered: true));
        });

        // With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS) the effect, and so the script, runs at the batch boundary.
        await host.WaitForWorldAsync(() => spells.HasAura(player, expected), $"spell_scripts {LunarCapStormwind} should cast {expected}");
        Assert.False(await host.OnWorldAsync(() => spells.HasAura(player, notExpected)));
    }

    [Fact]
    public async Task RiftSpawnManifestation_Dummy_RunsItsDbScript()
    {
        await using WorldTestHost host = await StartAsync();
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        await host.OnWorldAsync(() => InstallSpells(spells));
        await using WorldTestClient client = await host.EnterWorldAsync("RIFTSPAWN", "Riftspawn");
        Player player = await host.PlayerAsync("Riftspawn");

        await host.OnWorldAsync(() =>
        {
            AttachScriptExecutor(host, player);
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, RiftSpawnManifestation, SpellCastTargets.ForSelf(), triggered: true));
        });

        await host.WaitForWorldAsync(() => spells.HasAura(player, RiftSpawnBecomesVisible),
            $"dbscripts_on_spell {RiftSpawnManifestation} should cast {RiftSpawnBecomesVisible}");
    }

    /// <summary>
    /// The map's DB script executor. The creature feature attaches one to every map with spawns (as the SEND_EVENT scripts need too,
    /// ScriptedEvents.Start); this content has none, so the test attaches it the same way.
    /// </summary>
    private static void AttachScriptExecutor(WorldTestHost host, Player player)
        => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!);

    private static async Task<WorldTestHost> StartAsync()
    {
        (CreatureContent creatures, IReadOnlyList<ConditionRecord> conditions) = await s_content.Value;
        CreatureTestStore.Current.Value = new CreatureTestContext(creatures);
        try
        {
            return WorldTestHost.Start(configureServices: s => s.AddSingleton<IConditionContentStore>(new Conditions(conditions)));
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    /// <summary>The two scripted spells as Spell.dbc lays them out (effect, implicit target A) and the three marker spells they cast.</summary>
    private static void InstallSpells(SpellSystem spells)
        => spells.Store = new SpellStore(
        [
            .. spells.Store.All,
            Scripted(LunarCapStormwind, SpellEffectName.ScriptEffect, SpellImplicitTarget.UnitFriend),
            Scripted(RiftSpawnManifestation, SpellEffectName.Dummy, SpellImplicitTarget.UnitCaster),
            Marker(TeleportStormwind), Marker(PortToMoonglade), Marker(RiftSpawnBecomesVisible),
        ], [], []);

    private static SpellInfo Scripted(uint id, SpellEffectName effect, SpellImplicitTarget target) => new()
    {
        Id = id, Name = "Scripted " + id, RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Effects = [new SpellEffectInfo { Effect = effect, TargetA = target }],
    };

    private static SpellInfo Marker(uint id) => new()
    {
        Id = id, Name = "Marker " + id, RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster,
            AuraType = AuraType.ModStat, BasePoints = 0, BaseDice = 1, DieSides = 1, MiscValue = 0,
        }],
    };

    /// <summary>Both excerpts through the creature importer (which writes the DB scripts) and the classic-db conditions through theirs.</summary>
    private static async Task<(CreatureContent, IReadOnlyList<ConditionRecord>)> ImportAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-spellscripts-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new WorldDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                var creatures = new CreatureDumpImporter();
                creatures.Read(new StringReader(DbSpellScriptRows.ClassicDb + "\n" + DbSpellScriptRows.VMangos));
                await creatures.WriteAsync(db, replace: false);
                var conditions = new ConditionsDumpImporter();
                conditions.Read(new StringReader(DbSpellScriptRows.ClassicDb));
                await conditions.WriteAsync(db, replace: false);
            }

            await using var read = new WorldDbContext(options);
            return (await new EfCreatureDataStore(read).LoadAsync(), await new EfConditionContentStore(read).LoadAsync());
        }
        finally
        {
            File.Delete(path); // Pooling=False: no pooled connection holds the file
        }
    }

    private sealed class Conditions(IReadOnlyList<ConditionRecord> records) : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(records);
    }
}
