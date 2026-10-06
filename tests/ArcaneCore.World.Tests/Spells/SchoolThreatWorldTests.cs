using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class SchoolThreatWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorldSpellDamageAndEffectiveHealing_ApplyHolyThreatModifierOnce(bool periodic)
    {
        var template = new CreatureTemplate
        {
            Entry = 299, Name = "Threat Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903],
            Faction = 32, MinLevelHealth = 100, MaxLevelHealth = 100,
        };
        var spawn = new CreatureSpawn { Guid = 4242, Entry = 299, MapId = 0, X = -8940, Y = -132, Z = 83.5f };
        var context = new CreatureTestContext(new CreatureContent([template], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            await using WorldTestClient client = await host.EnterWorldAsync("THREATWOLF", "Threatwolf");
            await using WorldTestClient freshClient = await host.EnterWorldAsync("THREATFRESH", "Threatfresh");
            await host.OnWorldAsync(() =>
            {
                Player caster = host.World.FindOnlinePlayer("Threatwolf")!;
                Creature wolf = context.Feature!.FindSystem(0)!.FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, 299, 4242))!;
                SpellSystem spells = ((ArcaneCore.World.Net.WorldSession)caster.Session).Services
                    .GetRequiredService<SpellFeature>().System;
                SpellInfo holyDamage = new() { Id = 9100, School = SpellSchool.Holy };
                SpellInfo threatAura = new()
                {
                    Id = 9101, Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModThreat,
                        MiscValue = 1 << (int)SpellSchool.Holy, BasePoints = -41, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    }, new(), new()],
                };
                SpellInfo secondHolyAura = threatAura with
                {
                    Id = 9102,
                    Effects = [threatAura.Effects[0] with { BasePoints = 9, DieSides = 1 }, new(), new()],
                };
                SpellInfo unmatchedFireAura = threatAura with
                {
                    Id = 9103,
                    Effects = [threatAura.Effects[0] with
                    {
                        MiscValue = 1 << (int)SpellSchool.Fire, BasePoints = 99, DieSides = 1,
                    }, new(), new()],
                };
                spells.Store = new SpellStore([.. spells.Store.All, holyDamage, threatAura, secondHolyAura, unmatchedFireAura], [], []);
                foreach (uint auraId in new[] { threatAura.Id, secondHolyAura.Id, unmatchedFireAura.Id })
                {
                    Assert.Equal(SpellCastResult.CastOk,
                        spells.CastSpell(caster, auraId, SpellCastTargets.ForSelf(), triggered: true));
                }

                Assert.Equal(10u, spells.Damage.DealSpellDamage(caster, wolf, holyDamage, 10, periodic: periodic));
                Assert.Equal(90u, wolf.Health);
                Assert.Equal(6.6f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 10;
                Assert.Equal(10u, spells.Damage.Heal(caster, caster, holyDamage, 20));
                Assert.Equal(9.9f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                // Explicit threat uses the same school product once, not again in the threat list.
                spells.AddSpellThreat(caster, wolf, holyDamage, 10);
                Assert.Equal(16.5f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                caster.Health = caster.MaxHealth - 10;
                SpellInfo suppressedHeal = holyDamage with { AttributesEx4 = 0x00000020 };
                Assert.Equal(10u, spells.Damage.Heal(caster, caster, suppressedHeal, 20));
                Assert.Equal(16.5f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                SpellInfo suppressedDamage = holyDamage with { AttributesEx4 = 0x00000010 };
                Assert.Equal(10u, spells.Damage.DealSpellDamage(caster, wolf, suppressedDamage, 10, periodic: periodic));
                Assert.Equal(80u, wolf.Health);
                Assert.Equal(16.5f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                // NO_THREAT can raise an already-established reference without double scaling.
                SpellInfo existingOnly = holyDamage with { AttributesEx = (SpellAttributesEx)0x00000400 };
                Assert.Equal(10u, spells.Damage.DealSpellDamage(caster, wolf, existingOnly, 10, periodic: periodic));
                Assert.Equal(23.1f, wolf.Combat.Threat.GetThreat(caster), precision: 3);

                Player freshCaster = host.World.FindOnlinePlayer("Threatfresh")!;
                Assert.DoesNotContain(wolf.Combat.Threat.Entries,
                    entry => ReferenceEquals(entry.Target, freshCaster));
                Assert.Equal(10u, spells.Damage.DealSpellDamage(freshCaster, wolf, suppressedDamage, 10, periodic: periodic));
                Assert.DoesNotContain(wolf.Combat.Threat.Entries,
                    entry => ReferenceEquals(entry.Target, freshCaster));
                Assert.Equal(10u, spells.Damage.DealSpellDamage(freshCaster, wolf, existingOnly, 10, periodic: periodic));
                Assert.DoesNotContain(wolf.Combat.Threat.Entries,
                    entry => ReferenceEquals(entry.Target, freshCaster));
                Assert.Equal(0u, spells.Damage.DealSpellDamage(freshCaster, wolf, existingOnly, 0, periodic: periodic));
                Assert.DoesNotContain(wolf.Combat.Threat.Entries,
                    entry => ReferenceEquals(entry.Target, freshCaster));
            });
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }
}
