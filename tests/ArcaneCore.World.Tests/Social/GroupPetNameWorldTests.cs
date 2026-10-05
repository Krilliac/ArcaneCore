using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

public sealed class GroupPetNameWorldTests
{
    [Fact]
    public async Task GroupRename_EmitsOutOfRangePetNameStatsWithCurrentCString()
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Group Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        WorldTestHost host;
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        try { host = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }

        await using (host)
        await using (WorldTestClient leader = await host.EnterWorldAsync("GPETLEAD", "Gpetlead"))
        await using (WorldTestClient member = await host.EnterWorldAsync("GPETMEM", "Gpetmem"))
        {
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Gpetlead")!;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();
                PersistentPetSnapshot snapshot = new((int)player.Guid.Low, 801, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], [],
                    Name: "OldName", NameTimestamp: 1, RenameAllowed: true);
                Assert.NotNull(pets.Service.RestoreCurrentPet(player, snapshot));
                Assert.Equal("OldName", pets.Service.CaptureCurrentPet(player)!.Name);
                return true;
            });
            await leader.CollectAsync();
            await member.CollectAsync();
            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, System.Text.Encoding.UTF8.GetBytes("Gpetmem\0"));
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await leader.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            await host.PlaceAsync("Gpetmem", 5000, 0, 83.5f);
            await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>()
                .Context.Groups.UpdateOutOfRangeStats());
            await member.CollectAsync();
            ulong petGuid = await host.PlayerStateAsync("Gpetlead", p => p.GetPet()!.Guid.Value);

            var rename = new PacketWriter(32);
            rename.WriteUInt64(petGuid);
            rename.WriteCString("NewName");
            await leader.SendAsync(WorldOpcode.CmsgPetRename, rename.ToArray());

            byte[] stats = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            ulong leaderGuid = await host.PlayerStateAsync("Gpetlead", p => p.Guid.Value);
            var reader = new PacketReader(stats);
            Assert.Equal(leaderGuid, reader.ReadPackedGuid());
            Assert.Equal((uint)GroupUpdateFlags.PetName, reader.ReadUInt32());
            Assert.Equal("NewName", reader.ReadCString());
        }
    }
}
