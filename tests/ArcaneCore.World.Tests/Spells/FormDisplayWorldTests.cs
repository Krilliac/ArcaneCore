using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class FormDisplayWorldTests
{
    [Fact]
    public async Task CatForm_RealSocketCastPublishesPinnedDisplayAndRemovalRestoresNative()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(
            new ShapeshiftFormCatalog([new ShapeshiftFormInfo(1, 0, 0)])));
        await using WorldTestClient client = await host.EnterWorldAsync("FORM", "Form");
        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 998110, Name = "Synthetic Cat Form", Duration = new SpellDuration(60_000, 0, 60_000),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = -1,
                    BaseDice = 1, DieSides = 1, AuraType = AuraType.ModShapeshift, MiscValue = 1,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Player player = host.World.FindOnlinePlayer("Form")!;
            feature.Spellbook.LearnSpell(player, 998110);
            player.NativeDisplayId = 100;
            player.DisplayId = 100;
        });

        var writer = new PacketWriter(16);
        writer.WriteUInt32(998110);
        SpellCastTargets.ForSelf().Write(writer);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Form")!.DisplayId == 892, "cat display");
        Assert.Equal((uint)892, await host.PlayerStateAsync("Form", p => p.DisplayId));
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(
            host.World.FindOnlinePlayer("Form")!, 998110));
        Assert.Equal(100u, await host.PlayerStateAsync("Form", p => p.DisplayId));
        await client.CollectAsync();
    }
}
