using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Rogue;

public sealed class InvisibilityEndToEndTests
{
    [Fact]
    public async Task DiscoveredFeatureHidesAndRevealsPlayersThroughRealAuraHandlers()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient hiddenClient = await host.EnterWorldAsync("INVISA", "Invisa");
        await using WorldTestClient viewerClient = await host.EnterWorldAsync("INVISB", "Invisb");
        await host.PlaceAsync("Invisa", -8949, -132, 83.5f);
        await host.PlaceAsync("Invisb", -8944, -132, 83.5f);
        await host.OnWorldAsync(() =>
        {
            var target = host.World.FindOnlinePlayer("Invisa")!;
            var viewer = host.World.FindOnlinePlayer("Invisb")!;
            var spells = ((WorldSession)target.Session).Services.GetRequiredService<SpellFeature>();
            Assert.True(spells.System.HasAuraHandler(AuraType.ModInvisibility));
            Assert.True(spells.System.HasAuraHandler(AuraType.ModInvisibilityDetection));
            spells.System.Store = new SpellStore([.. spells.System.Store.All,
                Aura(950280, AuraType.ModInvisibility), Aura(950290, AuraType.ModInvisibilityDetection)], [], []);
            Assert.Contains(target.Guid, viewer.VisibleObjects);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(target, 950280, SpellCastTargets.ForSelf(), triggered: true));
            Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(viewer, 950290, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Contains(target.Guid, viewer.VisibleObjects);
            spells.System.RemoveAuras(viewer, 950290);
            Assert.DoesNotContain(target.Guid, viewer.VisibleObjects);
            spells.System.RemoveAuras(target, 950280);
            Assert.Contains(target.Guid, viewer.VisibleObjects);
            return true;
        });
    }

    private static SpellInfo Aura(uint id, AuraType type) => new()
    {
        Id = id, Name = "invisibility fixture", Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1,
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = type,
            BasePoints = 9, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };
}
