using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Tests.Creatures;
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
        // SpellSystem's effect table is a plain Dictionary written on the world thread (features post their installs there at start). Reading it
        // from the test thread while such a write resizes it can report an installed key as missing (ledger: SummonTotemSlot1 false right after
        // SummonTotem passed). Read it on the world thread, behind the already queued installs.
        (SpellEffectName Effect, bool Has)[] seen = await host.OnWorldAsync(() => new[]
        {
            SpellEffectName.SummonTotem, SpellEffectName.SummonTotemSlot1, SpellEffectName.SummonTotemSlot2, SpellEffectName.SummonTotemSlot3,
            SpellEffectName.SummonTotemSlot4, SpellEffectName.SummonWild, SpellEffectName.SummonGuardian, SpellEffectName.SummonCritter,
        }.Select(effect => (effect, spells.HasEffectHandler(effect))).ToArray());
        foreach ((SpellEffectName effect, bool has) in seen)
        {
            Assert.True(has, effect.ToString());
        }

        // SPELL_EFFECT_SUMMON keeps its built-in handler (the quest reward preflight models it).
        Assert.True(await host.OnWorldAsync(() => spells.HasBuiltInEffectHandler(SpellEffectName.Summon)));
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

    /// <summary>Pins the PetsFeature LevelChanged subscription: a level gained through PlayerProgression reaches a summoned warlock demon.</summary>
    [Fact]
    public async Task OwnerLevelUp_ThroughPlayerProgression_CarriesTheSummonedWarlockDemon()
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Level Follower", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost prepared;
        try { prepared = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using WorldTestHost host = prepared;
        await using WorldTestClient client = await host.EnterWorldAsync("DEMONLVL", "Demonlvl");
        const uint summonSpell = 991002;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Demonlvl")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = summonSpell, Name = "Summon level follower", Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.SummonPet, MiscValue = (int)PetTestServices.Entry, TargetA = SpellImplicitTarget.UnitCaster,
                    BasePoints = 0, BaseDice = 1, DieSides = 1,
                }],
            }], [], []);
            feature.Spellbook.LearnSpell(player, summonSpell);
            return true;
        });
        await client.CollectAsync();
        var writer = new PacketWriter();
        writer.WriteUInt32(summonSpell);
        writer.WriteUInt16(0);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgPetSpells);

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Demonlvl")!;
            Creature demon = Assert.IsType<Creature>(player.Map!.FindObject(player.PetGuid));
            Assert.Equal(player.Level, demon.Level);
            host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(player, 12);
            Assert.Equal(12, (int)player.Level);
            Assert.Equal(12, (int)demon.Level);
            return true;
        });
    }

    /// <summary>
    /// CMSG_PET_ACTION with COMMAND_DISMISS on a summoned warlock demon reaches SMSG_PET_DISMISS_SOUND at the owner's socket (mangos-classic Pet::PlayDismissSound,
    /// Pet.cpp:1401-1422, from HandlePetAction COMMAND_DISMISS, PetHandler.cpp:259-263): sound 0 for a pet whose summoning spell has no sound entry, then the pet's
    /// position.
    /// </summary>
    [Fact]
    public async Task TheDismissCommand_OverTheWire_SendsThePetDismissSoundToTheOwner()
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Dismiss Follower", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost prepared;
        try { prepared = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using WorldTestHost host = prepared;
        await using WorldTestClient client = await host.EnterWorldAsync("DEMONDIS", "Demondis");
        const uint summonSpell = 991003;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Demondis")!;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = summonSpell, Name = "Summon dismiss follower", Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.SummonPet, MiscValue = (int)PetTestServices.Entry, TargetA = SpellImplicitTarget.UnitCaster,
                    BasePoints = 0, BaseDice = 1, DieSides = 1,
                }],
            }], [], []);
            feature.Spellbook.LearnSpell(player, summonSpell);
            return true;
        });
        await client.CollectAsync();
        var cast = new PacketWriter();
        cast.WriteUInt32(summonSpell);
        cast.WriteUInt16(0);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, cast.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgPetSpells);
        (ObjectGuid petGuid, float petX, float petY) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Demondis")!;
            Creature demon = Assert.IsType<Creature>(player.Map!.FindObject(player.PetGuid));
            return (demon.Guid, demon.X, demon.Y);
        });

        var action = new PacketWriter(20);
        action.WriteUInt64(petGuid.Value);
        action.WriteUInt32(((uint)0x07 << 24) | (uint)CommandState.Dismiss);
        action.WriteUInt64(0);
        await client.SendAsync(WorldOpcode.CmsgPetAction, action.ToArray());

        byte[] sound = await client.ReadUntilAsync(WorldOpcode.SmsgPetDismissSound);
        Assert.Equal(16, sound.Length);
        Assert.Equal(0u, BitConverter.ToUInt32(sound, 0));
        Assert.InRange(BitConverter.ToSingle(sound, 4), petX - 10f, petX + 10f);
        Assert.InRange(BitConverter.ToSingle(sound, 8), petY - 10f, petY + 10f);
    }
}
