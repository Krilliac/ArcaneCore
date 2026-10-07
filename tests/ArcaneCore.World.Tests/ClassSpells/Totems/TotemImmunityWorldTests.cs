using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.ClassSpells.Totems;

/// <summary>Intrinsic totem rules use the same production world spell/application pipeline as other creature immunities.</summary>
public sealed class TotemImmunityWorldTests
{
    private const uint SummonSpell = 961100;
    private const uint HealSpell = 961101;
    private const uint StreamSpell = 961102;
    private const uint DamageAndAuraSpell = 961103;

    [Fact]
    public async Task ProductionWorld_BlocksForeignHealAndDebuff_PreservesDamageAndShamanRegeneration()
    {
        var template = new CreatureTemplate
        {
            Entry = TotemTestServices.TotemEntry, Name = "Immunity Test Totem", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI",
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("IMMUNETOTEM", "Immunetotem"))
        {
            await client.CollectAsync();
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Immunetotem")!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                TotemSystem totems = host.WorldServices.GetRequiredService<TotemFeature>().System!;
                spells.CombatRules = SpellCombatRules.Neutral;
                spells.Store = new SpellStore(
                [
                    .. spells.Store.All,
                    new SpellInfo
                    {
                        Id = SummonSpell, Name = "Immunity Totem Summon", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                        Duration = new SpellDuration(60_000, 0, 60_000),
                        Effects = [new SpellEffectInfo
                        {
                            Effect = SpellEffectName.SummonTotemSlot2, BasePoints = 4, BaseDice = 1, DieSides = 1,
                            TargetA = (SpellImplicitTarget)41, MiscValue = (int)TotemTestServices.TotemEntry,
                        }],
                    },
                    Targeted(HealSpell, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)),
                    Targeted(StreamSpell, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend))
                        with { SpellFamilyName = 11, SpellFamilyFlags = 0x4000 },
                    // A friendly-target damage spell is positive under vmangos IsPositiveEffect (no case, positive target); the debuff
                    // attribute makes it the negative spell the totem rule is about.
                    Targeted(DamageAndAuraSpell,
                        Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitFriend),
                        Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitFriend, AuraType.Dummy))
                        with { Attributes = SpellAttributes.AuraIsDebuff },
                ], [], []);

                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, SummonSpell, SpellCastTargets.ForSelf(), true));
                Creature totem = Assert.IsType<Creature>(totems.GetTotem(owner, TotemSlot.Earth));
                totem.MaxHealth = 100;
                totem.Health = 40;
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, HealSpell, SpellCastTargets.ForUnit(totem.Guid), true));
                Assert.Equal(40u, totem.Health);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, StreamSpell, SpellCastTargets.ForUnit(totem.Guid), true));
                Assert.Equal(50u, totem.Health);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(totem, HealSpell, SpellCastTargets.ForUnit(totem.Guid), true));
                Assert.Equal(60u, totem.Health);
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, DamageAndAuraSpell, SpellCastTargets.ForUnit(totem.Guid), true));
                Assert.Equal(50u, totem.Health);
                Assert.False(spells.HasAura(totem, DamageAndAuraSpell));
            });
        }
    }

    private static SpellInfo Targeted(uint id, params SpellEffectInfo[] effects) => new()
    {
        Id = id, Name = "Totem immunity test", RangeIndex = 4, Range = new SpellRange(0, 30),
        Duration = new SpellDuration(10_000, 0, 10_000), Effects = effects,
    };

    private static SpellEffectInfo Effect(SpellEffectName effect, int amount, SpellImplicitTarget target, AuraType aura = AuraType.None) => new()
    {
        Effect = effect, BasePoints = amount - 1, BaseDice = 1, DieSides = 1, TargetA = target, AuraType = aura,
    };
}
