using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

/// <summary>The pets area in the world daemon: opcode registration, the summon sink seam and the spell effects.</summary>
public sealed class PetsWorldTests
{
    private static readonly WorldOpcode[] PetOpcodes =
    [
        WorldOpcode.CmsgPetAction, WorldOpcode.CmsgPetSetAction, WorldOpcode.CmsgPetSpellAutocast, WorldOpcode.CmsgPetStopAttack,
        WorldOpcode.CmsgPetCastSpell, WorldOpcode.CmsgPetCancelAura, WorldOpcode.CmsgPetNameQuery, WorldOpcode.CmsgPetAbandon,
        WorldOpcode.CmsgRequestPetInfo,
    ];

    [Fact]
    public void PetOpcodes_AreRegisteredAsInWorldHandlers()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Assert.Contains(typeof(PetHandlers), WorldServiceCollectionExtensions.HandlerGroups.Select(g => g.GetType()));
        foreach (WorldOpcode opcode in PetOpcodes)
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler? handler), opcode.ToString());
            Assert.NotNull(handler.World);
            Assert.True(handler.AllowsState(SessionState.InWorld));
            Assert.False(handler.AllowsState(SessionState.CharacterSelect));
        }
    }

    [Fact]
    public void PetsFeature_IsRegisteredAsTheSummonSink()
    {
        var services = new ServiceCollection().AddWorldFeatures();
        Assert.Contains(services, d => d.ServiceType == typeof(PetsFeature) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ISpellSummonSink) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task Daemon_WiresTheSinkAndTheSummonEffectsIntoTheSpellSystem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var pets = host.WorldServices.GetRequiredService<PetsFeature>();
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;

        Assert.Same(pets, host.WorldServices.GetRequiredService<ISpellSummonSink>());
        Assert.Same(pets, spells.Summons);
        foreach (SpellEffectName effect in new[]
        {
            SpellEffectName.SummonTotem, SpellEffectName.SummonTotemSlot1, SpellEffectName.SummonTotemSlot2, SpellEffectName.SummonTotemSlot3,
            SpellEffectName.SummonTotemSlot4, SpellEffectName.SummonWild, SpellEffectName.SummonGuardian, SpellEffectName.SummonCritter,
        })
        {
            Assert.True(spells.HasEffectHandler(effect), effect.ToString());
        }

        // SPELL_EFFECT_SUMMON keeps its built-in handler (the quest reward preflight models it).
        Assert.True(spells.HasBuiltInEffectHandler(SpellEffectName.Summon));
    }

    [Fact]
    public async Task Daemon_LoadsThePetTablesFromTheRegisteredStore()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        PetContent content = host.WorldServices.GetRequiredService<PetsFeature>().Service.Content;

        Assert.Equal(PetTestServices.Health, content.FindLevelStats(PetTestServices.Entry, 1)!.Health);
        Assert.Equal([PetTestServices.Spell], content.GetCreateSpells(PetTestServices.Entry));
    }

    [Fact]
    public async Task PetPackets_ForAPlayerWithoutAPet_AreIgnored_AndTheSessionStaysUp()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETLESS", "Petless");
        await client.CollectAsync();

        var action = new PacketWriter(20);
        action.WriteUInt64(0xF140000000000001);
        action.WriteUInt32(0x07000002);
        action.WriteUInt64(0);
        await client.SendAsync(WorldOpcode.CmsgPetAction, action.ToArray());
        await client.SendAsync(WorldOpcode.CmsgRequestPetInfo, []);
        var name = new PacketWriter(12);
        name.WriteUInt32(7);
        name.WriteUInt64(0xF140000000000001);
        await client.SendAsync(WorldOpcode.CmsgPetNameQuery, name.ToArray());

        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode),
            op => op is WorldOpcode.SmsgPetSpells or WorldOpcode.SmsgPetNameQueryResponse or WorldOpcode.SmsgPetActionFeedback);

        // still alive: a ping is answered
        var ping = new PacketWriter(8);
        ping.WriteUInt32(1);
        ping.WriteUInt32(0);
        await client.SendAsync(WorldOpcode.CmsgPing, ping.ToArray());
        Assert.NotEmpty(await client.ReadUntilAsync(WorldOpcode.SmsgPong));
    }
}
