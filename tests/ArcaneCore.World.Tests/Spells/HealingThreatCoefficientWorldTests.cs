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

public sealed class HealingThreatCoefficientWorldTests
{
    [Fact]
    public async Task DirectPaladinHeal_UsesQuarterCoefficient_PeriodicRemainsHalf_AndSplitsEffectiveThreat()
    {
        var template = new CreatureTemplate
        {
            Entry = 299, Name = "Healing Threat Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903],
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
            await using WorldTestClient client = await host.EnterWorldAsync("PALADINHEAL", "Paladinheal");
            await host.OnWorldAsync(() =>
            {
                Player caster = host.World.FindOnlinePlayer("Paladinheal")!;
                caster.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Paladin);
                caster.MaxHealth = 1000;
                caster.Health = caster.MaxHealth;
                SpellSystem spells = ((WorldSession)caster.Session).Services.GetRequiredService<SpellFeature>().System;
                spells.CombatRules = SpellCombatRules.Neutral;
                Creature wolfA = context.Feature!.FindSystem(0)!.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, 299, 4242))!;
                Creature wolfB = context.Feature!.FindSystem(0)!.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, 299, 4243))!;

                SpellInfo establish = new()
                {
                    Id = 9200, School = SpellSchool.Normal,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BasePoints = 0, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitEnemy }, new(), new()],
                };
                SpellInfo holyThreatAura = new()
                {
                    Id = 9201, Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModThreat,
                        BasePoints = -21, BaseDice = 1, DieSides = 1, MiscValue = 1 << (int)SpellSchool.Holy,
                        TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                SpellInfo fireThreatAura = holyThreatAura with
                {
                    Id = 9202,
                    Effects = [holyThreatAura.Effects[0] with { BasePoints = 99, BaseDice = 1, MiscValue = 1 << (int)SpellSchool.Fire }, new(), new()],
                };
                SpellInfo directHeal = new()
                {
                    Id = 9203, School = SpellSchool.Holy,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal, BasePoints = 99, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                SpellInfo periodicHeal = new()
                {
                    Id = 9204, School = SpellSchool.Holy, Duration = new SpellDuration(3000, 0, 3000),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicHeal,
                        BasePoints = 99, BaseDice = 1, DieSides = 1, Amplitude = 1000, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                SpellInfo suppressedDirect = directHeal with { Id = 9205, AttributesEx4 = 0x00000008 }; // SPELL_ATTR_EX4_NO_HELPFUL_THREAT (vmangos SpellDefines.h:989)
                spells.Store = new SpellStore([.. spells.Store.All, establish, holyThreatAura, fireThreatAura,
                    directHeal, periodicHeal, suppressedDirect], [], []);

                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, holyThreatAura.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, fireThreatAura.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(10u, spells.Damage.DealSpellDamage(caster, wolfA, establish, 10, periodic: false));
                Assert.Equal(10u, spells.Damage.DealSpellDamage(caster, wolfB, establish, 10, periodic: false));

                caster.Health = caster.MaxHealth - 100;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, directHeal.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(20f, wolfA.Combat.Threat.GetThreat(caster), precision: 3); // 10 damage + 100 * .25 * .8 / 2
                Assert.Equal(20f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 100;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, periodicHeal.Id, SpellCastTargets.ForSelf(), triggered: true));
                spells.Update(1000);
                Assert.Equal(40f, wolfA.Combat.Threat.GetThreat(caster), precision: 3); // periodic uses .5 * .8 / 2
                Assert.Equal(40f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 100;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, suppressedDirect.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(40f, wolfA.Combat.Threat.GetThreat(caster), precision: 3);
                Assert.Equal(40f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 25;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, directHeal.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(42.5f, wolfA.Combat.Threat.GetThreat(caster), precision: 3); // overheal is excluded: 25 * .25 * .8 / 2
                Assert.Equal(42.5f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);

                caster.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
                caster.Health = caster.MaxHealth - 100;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(caster, directHeal.Id, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(62.5f, wolfA.Combat.Threat.GetThreat(caster), precision: 3); // non-Paladin direct uses .5 * .8 / 2
                Assert.Equal(62.5f, wolfB.Combat.Threat.GetThreat(caster), precision: 3);
            });
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }
}
