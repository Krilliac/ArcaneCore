using ArcaneCore.Game.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

public sealed class CallPetWorldTests
{
    [Fact]
    public async Task PetsFeature_InstallsSummonPetEffect56Producer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        Assert.True(spells.HasEffectHandler(SpellEffectName.SummonPet));
    }

    [Fact]
    public async Task CmsgCastSpell_Effect56RestoresPreloadedCachedHunterPet()
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Cached Call Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost prepared;
        try { prepared = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using WorldTestHost host = prepared;
        await using WorldTestClient client = await host.EnterWorldAsync("CALLPET", "Callpet");
        const uint callSpell = 991001;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Callpet")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
            PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();
            pets.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
                new((int)player.Guid.Low, 801, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], []));
            pets.Service.ReadCurrentPetAsync(player).GetAwaiter().GetResult();
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = callSpell, Name = "Call current test", Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.SummonPet, MiscValue = 0, TargetA = SpellImplicitTarget.UnitCaster,
                    BasePoints = 0, BaseDice = 1, DieSides = 1,
                }],
            }], [], []);
            feature.Spellbook.LearnSpell(player, callSpell);
            return true;
        });
        await client.CollectAsync();
        var writer = new PacketWriter();
        writer.WriteUInt32(callSpell);
        writer.WriteUInt16(0);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        byte[] petPacket = await client.ReadUntilAsync(WorldOpcode.SmsgPetSpells);
        ulong packetGuid = BitConverter.ToUInt64(petPacket, 0);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Callpet")!;
            Assert.Equal(801u, host.WorldServices.GetRequiredService<PetsFeature>().Service.CaptureCurrentPet(player)!.PetNumber);
            Assert.Equal(player.PetGuid.Value, packetGuid);
            return true;
        });
    }
}
