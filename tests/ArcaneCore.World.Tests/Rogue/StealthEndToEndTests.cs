using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Stealth;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Rogue;

/// <summary>Stealth through the discovered <see cref="StealthFeature"/> on the real world loop.</summary>
public sealed class StealthEndToEndTests
{
    private const uint StealthSpell = 910401;

    private static SpellInfo Stealth() => new()
    {
        Id = StealthSpell,
        Name = "Test Stealth",
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        AuraInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMask.StealthFamily,
        Dispel = StealthBreakRules.DispelStealth,
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            AuraType = AuraType.ModStealth,
            BasePoints = 4,
            BaseDice = 1,
            DieSides = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };

    [Fact]
    public async Task TheFeatureRegistersTheHandlers_StealthHidesTheRogue_AndTheDetectionPassRevealsItWhenAViewerIsClose()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient rogueClient = await host.EnterWorldAsync("STEALTHR", "Stealthr");
        await using WorldTestClient viewerClient = await host.EnterWorldAsync("STEALTHV", "Stealthv");
        await host.PlaceAsync("Stealthr", -8949f, -132f, 83.5f);
        await host.PlaceAsync("Stealthv", -8949f - 30f, -132f, 83.5f);

        SpellFeature spells = null!;
        await host.OnWorldAsync(() =>
        {
            Player rogue = host.World.FindOnlinePlayer("Stealthr")!;
            Player viewer = host.World.FindOnlinePlayer("Stealthv")!;
            spells = ((WorldSession)rogue.Session).Services.GetRequiredService<SpellFeature>();
            Assert.True(spells.System.HasAuraHandler(AuraType.ModStealth));
            Assert.True(spells.System.HasAuraHandler(AuraType.ModStealthLevel));
            Assert.True(spells.System.HasAuraHandler(AuraType.ModStealthDetect));
            spells.System.Store = new SpellStore([.. spells.System.Store.All, Stealth()], [], []);
            Assert.Contains(rogue.Guid, viewer.VisibleObjects);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(rogue, StealthSpell, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(0x02, rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
            Assert.DoesNotContain(rogue.Guid, viewer.VisibleObjects);
        });

        // 30 yards away: several detection passes never reveal the rogue.
        await Task.Delay(2500);
        await host.OnWorldAsync(() => Assert.DoesNotContain(
            host.World.FindOnlinePlayer("Stealthr")!.Guid, host.World.FindOnlinePlayer("Stealthv")!.VisibleObjects));

        // 3 yards away: the next detection pass (at most 2 s) reveals it.
        await host.PlaceAsync("Stealthv", -8949f - 3f, -132f, 83.5f);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Stealthv")!.VisibleObjects.Contains(host.World.FindOnlinePlayer("Stealthr")!.Guid),
            "the viewer detects the nearby stealthed rogue");

        // The stealth aura going away clears the flags.
        await host.OnWorldAsync(() =>
        {
            Player rogue = host.World.FindOnlinePlayer("Stealthr")!;
            spells.System.RemoveAuras(rogue, StealthSpell);
            Assert.Equal(0, rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        });
    }
}
