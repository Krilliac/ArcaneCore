using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Gm;

/// <summary>The spell side of the GM cheats (vmangos PLAYER_CHEAT_NO_COOLDOWN / NO_CAST_TIME / NO_POWER, Spell.cpp).</summary>
public sealed class GmCheatSpellTests
{
    [Fact]
    public void NoCastTimeAndNoPower_TheBoltLandsAtOnceForFree()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        SpellTestKit.FlagForPvp(caster);
        (Player target, _) = kit.AddHostilePlayer(2, 10, 0);
        kit.World.RunTick(0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        uint health = target.Health;
        caster.Cheats = PlayerCheats.NoCastTime | PlayerCheats.NoPowerCost;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        kit.Advance(100);

        Assert.True(target.Health < health);
        Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
    }

    [Fact]
    public void WithoutCheats_TheBoltStillTakesItsCastTimeAndRage()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        SpellTestKit.FlagForPvp(caster);
        (Player target, _) = kit.AddHostilePlayer(2, 10, 0);
        kit.World.RunTick(0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(SpellCastState.Preparing, kit.System.GetState(caster.Guid)!.CurrentCast!.State);
    }

    [Fact]
    public void NoCooldown_StartsNoCooldownAndClearsTheClientTimer()
    {
        using var kit = new SpellTestKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(caster, CooldownSpell);
        SpellInfo spell = kit.Store.Get(CooldownSpell)!;
        caster.Cheats = PlayerCheats.NoCooldown;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CooldownSpell, SpellCastTargets.ForSelf()));
        kit.Advance(100);

        Assert.True(kit.System.IsSpellReady(caster, spell));
        Assert.Empty(kit.System.GetActiveCooldowns(caster));
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgClearCooldown));

        caster.Cheats = PlayerCheats.None;
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CooldownSpell, SpellCastTargets.ForSelf()));
        kit.Advance(100);
        Assert.False(kit.System.IsSpellReadyIgnoringGlobalCooldown(caster, spell));
    }
}