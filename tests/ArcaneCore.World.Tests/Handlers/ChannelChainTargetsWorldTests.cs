using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// SMSG_SPELL_UPDATE_CHAIN_TARGETS at the start of a channel whose visual has a channel kit (vmangos Spell::SendChannelStart, Spell.cpp:4880-4906), over the
/// wire: CMSG_CAST_SPELL from a real client on the socket, the packet read back at the caster's and at the target's socket. The channel-kit table is
/// the live-fx data feature's; here the seam <see cref="SpellSystem.HasChannelKit"/> stands in for it, as the game-level test does.
/// Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class ChannelChainTargetsWorldTests
{
    private const uint ChannelSpell = 991004;
    private const uint VisualWithKit = 7;

    [Fact]
    public async Task AChannelWithAKitVisual_SendsTheChainTargetsToTheCasterAndTheTargetOverTheWire()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient caster = await host.EnterWorldAsync("CHAINA", "Chaincaster");
        await using WorldTestClient friend = await host.EnterWorldAsync("CHAINB", "Chainfriend");
        ulong casterGuid = await host.PlayerStateAsync("Chaincaster", p => p.Guid.Value);
        ulong friendGuid = await host.PlayerStateAsync("Chainfriend", p => p.Guid.Value);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Chaincaster")!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.HasChannelKit = visual => visual == VisualWithKit;
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = ChannelSpell,
                Name = "Chain channel",
                AttributesEx = SpellAttributesEx.IsChanneled,
                Duration = new SpellDuration(5000, 0, 5000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = VisualWithKit,
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitFriend,
                    BasePoints = 0, BaseDice = 1, DieSides = 1,
                }],
            }], [], []);
            feature.Spellbook.LearnSpell(player, ChannelSpell);
            return true;
        });
        await caster.CollectAsync();
        await friend.CollectAsync();

        var cast = new PacketWriter(16);
        cast.WriteUInt32(ChannelSpell);
        SpellCastTargets.ForUnit(new ObjectGuid(friendGuid)).Write(cast);
        await caster.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray());

        byte[] expected = SpellPackets.BuildSpellUpdateChainTargets(casterGuid, ChannelSpell, [friendGuid]);
        Assert.Equal(expected, await caster.ReadUntilAsync(WorldOpcode.SmsgSpellUpdateChainTargets));
        Assert.Equal(expected, await friend.ReadUntilAsync(WorldOpcode.SmsgSpellUpdateChainTargets));
    }
}
