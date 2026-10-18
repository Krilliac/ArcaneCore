using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos SpellEffects.cpp cases in <c>Spells/Generic/OpenWorldDummyScripts.cs</c> and the contested-PvP flag (Silithus flag 29518).</summary>
public sealed class OpenWorldDummyScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Self(uint id, SpellEffectName effect = SpellEffectName.ScriptEffect) => Instant(Spell(id, Effect(effect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo Marker(uint id) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void SilithusFlagClick_MarksTheClickerContestedPvp()
    {
        using SpellTestKit kit = Kit(Self(29518, SpellEffectName.Dummy), Marker(29519));
        (Player clicker, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(clicker, 29518, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(clicker, 29519));
        Assert.NotEqual(PlayerFlags.None, clicker.Flags & PlayerFlags.ContestedPvp);
        Assert.Equal(CombatConstants.PvpContestedTimerMs, clicker.Combat.PvpContestedTimer);
    }

    [Fact]
    public void ContestedPvp_LingersUntilTheTimerRunsOut_OverrideClearsAtOnce()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        MapCombat.UpdatePvpContested(player, true);
        Assert.NotEqual(PlayerFlags.None, player.Flags & PlayerFlags.ContestedPvp);

        MapCombat.UpdatePvpContested(player, false); // timer still running: stays (vmangos Player.cpp:18597)
        Assert.NotEqual(PlayerFlags.None, player.Flags & PlayerFlags.ContestedPvp);

        MapCombat.UpdatePvpContested(player, false, overriding: true); // resurrection / hearth
        Assert.Equal(PlayerFlags.None, player.Flags & PlayerFlags.ContestedPvp);
        Assert.Equal(0u, player.Combat.PvpContestedTimer);
    }

    [Fact]
    public void ValentineEndCheck_StripsTheEventAurasOnlyOnceTheEventIsOver()
    {
        using SpellTestKit kit = Kit(Self(27657), Marker(26869), Marker(27741));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 26869, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, 27741, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.IsGameEventActive = id => id == ValentineEndCheckScript.LoveIsInTheAirEvent;
        kit.System.CastSpell(player, 27657, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, 26869));

        kit.System.IsGameEventActive = static _ => false;
        kit.System.CastSpell(player, 27657, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(kit.System.HasAura(player, 26869));
        Assert.False(kit.System.HasAura(player, 27741));
    }

    [Fact]
    public void CallOfTheAshbringer_PlaysAWhisperToTheCarrierOnly()
    {
        using SpellTestKit kit = Kit(Self(28414));
        (Player carrier, FakeSession session) = kit.AddPlayer(1);
        (_, FakeSession bystander) = kit.AddPlayer(2, 1f, 1f);
        session.Sent.Clear();
        bystander.Sent.Clear();
        kit.System.CastSpell(carrier, 28414, SpellCastTargets.ForSelf(), triggered: true);
        var sound = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPlaySound);
        Assert.Contains(new PacketReader(sound.Payload).ReadUInt32(), CallOfTheAshbringerScript.Whispers);
        Assert.DoesNotContain(bystander.Sent, p => p.Opcode == WorldOpcode.SmsgPlaySound);
    }

    [Fact]
    public void Scripts_AreRegistered()
    {
        var registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in new uint[] { 6700, 28414, 23853, 27657 })
        {
            Assert.True(registry.Find(id) is not null, $"spell {id} has no script");
        }
    }
}
