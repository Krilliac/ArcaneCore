using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class CreatureRespawnAuraWorldTests
{
    private const uint Entry = 962100;
    private const uint Persistent = 962101;
    private const uint Passive = 962102;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Respawn_ClearsStalePersistentHolderBeforeFreshAiPassive(bool force)
    {
        await using WorldTestHost host = Start();
        CreatureWorldFeature creatures = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        await host.OnWorldAsync(() =>
        {
            InstallSpells(spells);
            creatures.Options.CorpseDecayNormalSeconds = 0;
            creatures.AiServices.Factory.Register("RespawnAuraTest", c => new PassiveAi(c, spells));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("RESPAWNAURA", "Respawnaura");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem system = creatures.FindSystem(0)!;
            Creature creature = Assert.Single(system.Creatures);
            Assert.True(spells.HasAura(creature, Passive));
            Assert.Equal(7, creature.GetInt32(UpdateFields.UnitFieldStat0));
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(creature, Persistent, SpellCastTargets.ForSelf(), true));
            Assert.Equal(18, creature.GetInt32(UpdateFields.UnitFieldStat0));

            system.KillCreature(creature);
            Assert.True(spells.HasAura(creature, Persistent));
            Assert.True(spells.HasAura(creature, Passive));
            system.Update(creature.Map!, 1); // remove the corpse; same object is waiting to respawn
            Assert.Equal(CreatureDeathState.Dead, creature.DeathState);
            Assert.Empty(spells.GetAuras(creature));

            // A script can add a death-persistent self aura during DEAD. It must not leak into
            // the new life, even when the respawn runs before the next spell-system update.
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(creature, Persistent, SpellCastTargets.ForSelf(), true));
            SpellAuraHolder stale = Assert.Single(spells.GetAuras(creature));
            Assert.Equal(11, creature.GetInt32(UpdateFields.UnitFieldStat0));
            if (force)
            {
                system.ForceRespawn(creature);
            }
            else
            {
                system.Update(host.World.GetMap(0), 30_000);
            }

            Assert.Equal(CreatureDeathState.Alive, creature.DeathState);
            Assert.True(stale.IsRemoved);
            SpellAuraHolder fresh = Assert.Single(spells.GetAuras(creature));
            Assert.Equal(Passive, fresh.Spell.Id);
            Assert.Equal(7, creature.GetInt32(UpdateFields.UnitFieldStat0));
            Assert.Same(creature, spells.ResolveAuraActor(fresh));
        });
    }

    private static WorldTestHost Start()
    {
        var template = new CreatureTemplate
        {
            Entry = Entry, Name = "Respawn Aura Test", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = 55, MaxLevelHealth = 55, Faction = 35,
            AIName = "RespawnAuraTest",
        };
        var spawn = new CreatureSpawn
        {
            Guid = 962100, Entry = Entry, MapId = 0, X = -8940, Y = -132, Z = 83.5f,
            SpawnTimeMinSeconds = 30, SpawnTimeMaxSeconds = 30,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [spawn], [], [], []));
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static void InstallSpells(SpellSystem spells)
        => spells.Store = new SpellStore(
        [
            .. spells.Store.All,
            StatSpell(Persistent, 11) with { AttributesEx3 = 0x00100000, Attributes = SpellAttributes.AllowCastWhileDead },
            StatSpell(Passive, 7) with { Attributes = SpellAttributes.Passive },
        ], [], []);

    private static SpellInfo StatSpell(uint id, int amount) => new()
    {
        Id = id, Name = "Respawn Stat Aura", RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, TargetA = SpellImplicitTarget.UnitCaster,
            AuraType = AuraType.ModStat, BasePoints = amount - 1, BaseDice = 1, DieSides = 1, MiscValue = 0,
        }],
    };

    private sealed class PassiveAi(Creature creature, SpellSystem spells) : CreatureAI(creature)
    {
        public override void OnRespawn()
            => Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(Me, Passive, SpellCastTargets.ForSelf(), true));
    }
}
