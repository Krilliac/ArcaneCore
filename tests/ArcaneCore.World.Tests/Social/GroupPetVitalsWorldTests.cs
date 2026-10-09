using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
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

public sealed class GroupPetVitalsWorldTests
{
    [Fact]
    public async Task OutOfRangeGroupStats_ReportLivePetBodyAndPowerChanges()
    {
        var template = new CreatureTemplate { Entry = PetTestServices.Entry, Name = "Vitals Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35 };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }

        await using (host)
        await using (WorldTestClient owner = await host.EnterWorldAsync("PVOWNER", "Pvowner"))
        await using (WorldTestClient member = await host.EnterWorldAsync("PVMEMBER", "Pvmember"))
        {
            // This test runs every out-of-range pass itself and reads the packet each one sends. The server's 1 s timer pass would race it:
            // landing between a change and the test's pass it sends that change first (read by a drain, or by the wrong ReadUntilAsync),
            // and the test's pass then has nothing to send.
            await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SocialFeature>().PeriodicStatsPass = false);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
            });
            await owner.CollectAsync();
            await member.CollectAsync();
            await owner.SendAsync(WorldOpcode.CmsgGroupInvite, System.Text.Encoding.UTF8.GetBytes("Pvmember\0"));
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await owner.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            await host.PlaceAsync("Pvmember", 5000, 0, 83.5f);
            await member.CollectAsync();
            await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats());
            await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();
                var snapshot = new PersistentPetSnapshot((int)player.Guid.Low, 901, PetTestServices.Entry, 1, 0, 50, 30, 0, 1, [], [], Name: "Vitals", NameTimestamp: 1, RenameAllowed: false);
                Assert.NotNull(pets.Service.RestoreCurrentPet(player, snapshot));
                player.GetPet()!.MaxHealth = 50;
                player.GetPet()!.Health = 50;
                player.GetPet()!.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.GetPet()!.SetUInt32(UpdateFields.UnitFieldPower1, 30);
                player.GetPet()!.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
                Assert.NotNull(player.GetPet());
            });
            byte[] full = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var reader = new PacketReader(full);
            reader.ReadPackedGuid();
            GroupUpdateFlags mask = (GroupUpdateFlags)reader.ReadUInt32();
            Assert.Equal((GroupUpdateFlags)0x001FF800, mask);
            Assert.NotEqual(0ul, reader.ReadUInt64());
            Assert.Equal("Vitals", reader.ReadCString());
            Assert.Equal((ushort)903, reader.ReadUInt16());
            Assert.Equal((ushort)50, reader.ReadUInt16());
            Assert.Equal((ushort)50, reader.ReadUInt16());
            Assert.Equal((byte)PowerType.Mana, reader.ReadByte());
            Assert.Equal((ushort)30, reader.ReadUInt16());
            Assert.Equal((ushort)100, reader.ReadUInt16());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal((ushort)0, reader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                const uint aura = 49590;
                spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
                {
                    Id = aura, Name = "Pet party aura", StartRecoveryCategory = 0, StartRecoveryTime = 0,
                    Duration = new SpellDuration(60_000, 0, 60_000),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, BasePoints = -1, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                spells.System.CastSpell(player.GetPet()!, aura, SpellCastTargets.ForSelf(), triggered: true);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] auraPacket = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var auraReader = new PacketReader(auraPacket);
            auraReader.ReadPackedGuid();
            Assert.Equal((uint)GroupUpdateFlags.PetAuras, auraReader.ReadUInt32());
            Assert.Equal(1u, auraReader.ReadUInt32());
            Assert.Equal((ushort)49590, auraReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                const uint negative = 49591;
                spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
                {
                    Id = negative, Name = "Pet negative aura", Attributes = SpellAttributes.AuraIsDebuff,
                    Duration = new SpellDuration(60_000, 0, 60_000),
                    StartRecoveryCategory = 0, StartRecoveryTime = 0,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, BasePoints = -1, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                spells.System.CastSpell(player.GetPet()!, 49590, SpellCastTargets.ForSelf(), triggered: true);
                spells.System.CastSpell(player.GetPet()!, negative, SpellCastTargets.ForSelf(), triggered: true);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] bothAuras = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var bothReader = new PacketReader(bothAuras);
            bothReader.ReadPackedGuid();
            // The positive aura was only refreshed (same slot): vmangos SpellAuraHolder::Refresh does not call UpdateAuraForGroup, so only the
            // negative slot that changed is reported.
            Assert.Equal((uint)GroupUpdateFlags.PetAurasNegative, bothReader.ReadUInt32());
            Assert.Equal((ushort)1, bothReader.ReadUInt16());
            Assert.Equal((ushort)49591, bothReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(player.GetPet()!, 49590);
                host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(player.GetPet()!, 49591);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] removedAura = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var removedReader = new PacketReader(removedAura);
            removedReader.ReadPackedGuid();
            Assert.Equal((uint)(GroupUpdateFlags.PetAuras | GroupUpdateFlags.PetAurasNegative), removedReader.ReadUInt32());
            Assert.Equal(1u, removedReader.ReadUInt32());
            Assert.Equal((ushort)0, removedReader.ReadUInt16());
            Assert.Equal((ushort)1, removedReader.ReadUInt16());
            Assert.Equal((ushort)0, removedReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                Creature pet = host.World.FindOnlinePlayer("Pvowner")!.GetPet()!;
                pet.Health = 42;
                pet.SetUInt32(UpdateFields.UnitFieldPower1, 25);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] changed = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var changedReader = new PacketReader(changed);
            changedReader.ReadPackedGuid();
            GroupUpdateFlags changedMask = (GroupUpdateFlags)changedReader.ReadUInt32();
            Assert.Equal(GroupUpdateFlags.PetCurrentHp | GroupUpdateFlags.PetCurrentPower, changedMask);
            Assert.Equal((ushort)42, changedReader.ReadUInt16());
            Assert.Equal((ushort)25, changedReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                Creature pet = host.World.FindOnlinePlayer("Pvowner")!.GetPet()!;
                pet.SetUInt32(UpdateFields.UnitFieldDisplayid, 904);
                pet.MaxHealth = 70;
                pet.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Energy);
                pet.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy, 20);
                pet.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 100);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] typeChanged = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var typeReader = new PacketReader(typeChanged);
            typeReader.ReadPackedGuid();
            Assert.Equal(GroupUpdateFlags.PetModelId | GroupUpdateFlags.PetMaxHp | GroupUpdateFlags.PetPowerType
                | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower, (GroupUpdateFlags)typeReader.ReadUInt32());
            Assert.Equal((ushort)904, typeReader.ReadUInt16());
            Assert.Equal((ushort)70, typeReader.ReadUInt16());
            Assert.Equal((byte)PowerType.Energy, typeReader.ReadByte());
            Assert.Equal((ushort)20, typeReader.ReadUInt16());
            Assert.Equal((ushort)100, typeReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                Creature former = player.GetPet()!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                // Queue a slot on the old identity, then replace its baseline
                // before the group pass; stale callbacks must not add slots.
                spells.System.CastSpell(former, 49590, SpellCastTargets.ForSelf(), triggered: true);
                player.SetPetGuid(ObjectGuid.Empty);
                spells.System.CastSpell(former, 49591, SpellCastTargets.ForSelf(), triggered: true);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] cleared = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var clearedReader = new PacketReader(cleared);
            clearedReader.ReadPackedGuid();
            Assert.Equal((GroupUpdateFlags)0x001FF800, (GroupUpdateFlags)clearedReader.ReadUInt32());
            Assert.Equal(0ul, clearedReader.ReadUInt64());
            Assert.Equal(string.Empty, clearedReader.ReadCString());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());
            Assert.Equal((byte)0, clearedReader.ReadByte());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());
            Assert.Equal(0u, clearedReader.ReadUInt32());
            Assert.Equal((ushort)0, clearedReader.ReadUInt16());

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Pvowner")!;
                PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();
                var replacement = new PersistentPetSnapshot((int)player.Guid.Low, 902, PetTestServices.Entry, 1, 0, 60, 10, 0, 1, [], [], Name: "Replacement", NameTimestamp: 2, RenameAllowed: false);
                Assert.NotNull(pets.Service.RestoreCurrentPet(player, replacement));
                player.GetPet()!.MaxHealth = 60;
                player.GetPet()!.Health = 60;
                player.GetPet()!.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.GetPet()!.SetUInt32(UpdateFields.UnitFieldPower1, 10);
                player.GetPet()!.SetUInt32(UpdateFields.UnitFieldMaxpower1, 50);
                host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.UpdateOutOfRangeStats();
            });
            byte[] replacementPacket = await member.ReadUntilAsync(WorldOpcode.SmsgPartyMemberStats);
            var replacementReader = new PacketReader(replacementPacket);
            replacementReader.ReadPackedGuid();
            Assert.Equal((GroupUpdateFlags)0x001FF800, (GroupUpdateFlags)replacementReader.ReadUInt32());
            Assert.NotEqual(0ul, replacementReader.ReadUInt64());
            Assert.Equal("Replacement", replacementReader.ReadCString());
            Assert.Equal((ushort)903, replacementReader.ReadUInt16());
            Assert.Equal((ushort)60, replacementReader.ReadUInt16());
            Assert.Equal((ushort)60, replacementReader.ReadUInt16());
            Assert.Equal((byte)PowerType.Mana, replacementReader.ReadByte());
            Assert.Equal((ushort)10, replacementReader.ReadUInt16());
            Assert.Equal((ushort)50, replacementReader.ReadUInt16());
            Assert.Equal(0u, replacementReader.ReadUInt32());
            Assert.Equal((ushort)0, replacementReader.ReadUInt16());
        }
    }
}
