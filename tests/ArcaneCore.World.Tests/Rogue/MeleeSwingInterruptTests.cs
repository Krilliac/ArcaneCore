using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Rogue;

/// <summary>
/// A white swing ends with RemoveAurasWithInterruptFlags(ATTACKING) on the attacker (vmangos Unit.cpp:2285),
/// proven through the production path: the map combat tick swings, the discovered SpellFeature reacts.
/// </summary>
public sealed class MeleeSwingInterruptTests
{
    private const uint AttackingAura = 910101;
    private const uint InertAura = 910102;

    private sealed class EveryoneFights : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;

        public override bool CanAttack(Unit attacker, Unit victim) => !ReferenceEquals(attacker, victim);
    }

    private static SpellInfo SelfAura(uint id, uint interrupt) => new()
    {
        Id = id,
        Name = $"Test aura {id}",
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        AuraInterruptFlags = (SpellAuraInterruptFlags)interrupt,
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            AuraType = AuraType.Dummy,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };

    [Fact]
    public async Task WhiteSwing_RemovesAttackingAuras_AndLeavesAurasWithoutTheBit()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("SWINGA", "Swinga");
        await using WorldTestClient b = await host.EnterWorldAsync("SWINGB", "Swingb");
        SpellFeature spells = null!;
        await host.OnWorldAsync(() =>
        {
            Player attacker = host.World.FindOnlinePlayer("Swinga")!;
            Player victim = host.World.FindOnlinePlayer("Swingb")!;
            spells = ((WorldSession)attacker.Session).Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore(
                [.. spells.System.Store.All, SelfAura(AttackingAura, AuraInterruptMask.Attacking), SelfAura(InertAura, 0)], [], []);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(attacker, AttackingAura, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(attacker, InertAura, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(spells.System.HasAura(attacker, AttackingAura));
            host.World.GetMap(0).Combat.Hooks = new EveryoneFights();
            Assert.True(host.World.GetMap(0).Combat.Attack(attacker, victim));
        });

        await host.WaitForWorldAsync(
            () => !spells.System.HasAura(host.World.FindOnlinePlayer("Swinga")!, AttackingAura),
            "the first white swing removes the ATTACKING aura");
        await host.OnWorldAsync(() => Assert.True(spells.System.HasAura(host.World.FindOnlinePlayer("Swinga")!, InertAura)));
    }
}
