using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class LeechThreatWorldTests
{
    [Fact]
    public async Task DirectHealthLeechAddsDamageThreatOnly_PeriodicLeechAddsHalfHealThreatPerVmangos()
    {
        var template = new CreatureTemplate
        {
            Entry = 299, Name = "Leech Threat Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903],
            Faction = 32, MinLevelHealth = 100, MaxLevelHealth = 100,
        };
        var spawns = new[]
        {
            new CreatureSpawn { Guid = 4242, Entry = 299, MapId = 0, X = -8940, Y = -132, Z = 83.5f },
            new CreatureSpawn { Guid = 4243, Entry = 299, MapId = 0, X = -8938, Y = -132, Z = 83.5f },
        };
        var context = new CreatureTestContext(new CreatureContent([template], spawns, [], [], []));
        CreatureTestStore.Current.Value = context;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            await using WorldTestClient client = await host.EnterWorldAsync("LEECHTHREAT", "Leechthreat");
            await host.OnWorldAsync(() =>
            {
                Player caster = host.World.FindOnlinePlayer("Leechthreat")!;
                caster.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Paladin);
                caster.MaxHealth = 1000;
                caster.Health = caster.MaxHealth;
                SpellSystem spells = ((WorldSession)caster.Session).Services.GetRequiredService<SpellFeature>().System;
                spells.CombatRules = SpellCombatRules.Neutral;
                Creature wolfA = context.Feature!.FindSystem(0)!.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, 299, 4242))!;
                Creature wolfB = context.Feature!.FindSystem(0)!.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, 299, 4243))!;

                SpellInfo holyThreatAura = new()
                {
                    Id = 9300, Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModThreat,
                        BasePoints = -21, BaseDice = 1, DieSides = 1, MiscValue = 1 << (int)SpellSchool.Holy,
                        TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                SpellInfo fireThreatAura = holyThreatAura with
                {
                    Id = 9301,
                    Effects = [holyThreatAura.Effects[0] with { BasePoints = 99, BaseDice = 1, MiscValue = 1 << (int)SpellSchool.Fire }, new(), new()],
                };
                SpellInfo directLeech = new()
                {
                    Id = 9302, School = SpellSchool.Holy, RangeIndex = 4, Range = new SpellRange(0, 30),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.HealthLeech, BasePoints = 19,
                        BaseDice = 1, DieSides = 1, MultipleValue = 1, TargetA = SpellImplicitTarget.UnitEnemy }, new(), new()],
                };
                SpellInfo periodicLeech = new()
                {
                    Id = 9303, School = SpellSchool.Holy, RangeIndex = 4, Range = new SpellRange(0, 30), Duration = new SpellDuration(3000, 0, 3000),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicLeech,
                        BasePoints = 19, BaseDice = 1, DieSides = 1, MultipleValue = 1, Amplitude = 1000,
                        TargetA = SpellImplicitTarget.UnitEnemy }, new(), new()],
                };
                SpellInfo suppressedLeech = periodicLeech with { Id = 9304, AttributesEx4 = 0x00000008 }; // SPELL_ATTR_EX4_NO_HELPFUL_THREAT
                spells.Store = new SpellStore([.. spells.Store.All, holyThreatAura, fireThreatAura, directLeech, periodicLeech, suppressedLeech], [], []);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, holyThreatAura.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, fireThreatAura.Id, SpellCastTargets.ForSelf(), triggered: true));

                caster.Health = caster.MaxHealth - 20;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, directLeech.Id,
                    SpellCastTargets.ForUnit(wolfA.Guid), triggered: true));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, directLeech.Id,
                    SpellCastTargets.ForUnit(wolfB.Guid), triggered: true));
                Assert.Equal(80u, wolfA.Health);
                Assert.Equal(80u, wolfB.Health);
                Assert.Equal(caster.MaxHealth, caster.Health);
                Assert.Equal(16f, wolfA.Combat.Threat.GetThreat(caster), precision: 3); // damage 20 * Holy .8; no healing threat
                Assert.Equal(16f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 40;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, periodicLeech.Id,
                    SpellCastTargets.ForUnit(wolfA.Guid), triggered: true));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, periodicLeech.Id,
                    SpellCastTargets.ForUnit(wolfB.Guid), triggered: true));
                spells.Update(1000);

                // Each tick adds 20 * .8 damage threat plus (20 * .5 / 2) * .8 to both hostile references.
                Assert.Equal(40f, wolfA.Combat.Threat.GetThreat(caster), precision: 3);
                Assert.Equal(40f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);
                Assert.Equal(60u, wolfA.Health);
                Assert.Equal(60u, wolfB.Health);

                spells.RemoveAuras(wolfA, periodicLeech.Id);
                spells.RemoveAuras(wolfB, periodicLeech.Id);
                caster.Health = caster.MaxHealth - 5;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, periodicLeech.Id,
                    SpellCastTargets.ForUnit(wolfA.Guid), triggered: true));
                spells.Update(1000);
                Assert.Equal(caster.MaxHealth, caster.Health);
                Assert.Equal(40u, wolfA.Health);
                // Only five effective health is restored: 5 * .5 * .8 / 2 = 1 to each reference.
                Assert.Equal(57f, wolfA.Combat.Threat.GetThreat(caster), precision: 3);
                Assert.Equal(41f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                spells.RemoveAuras(wolfA, periodicLeech.Id);
                caster.Health = caster.MaxHealth - 20;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, suppressedLeech.Id,
                    SpellCastTargets.ForUnit(wolfA.Guid), triggered: true));
                spells.Update(1000);
                Assert.Equal(caster.MaxHealth, caster.Health);
                Assert.Equal(20u, wolfA.Health);
                // Helpful suppression keeps the harmful damage threat and omits both heal assists.
                Assert.Equal(73f, wolfA.Combat.Threat.GetThreat(caster), precision: 3);
                Assert.Equal(41f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                Assert.Equal(caster.MaxHealth, caster.Health);
            });
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }
}
