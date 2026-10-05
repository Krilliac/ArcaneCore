using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using GameActionButton = ArcaneCore.Game.Pets.ActionButton;

namespace ArcaneCore.MockClient.Tests;

public sealed partial class WorldLifecyclePersistenceTests
{
    [Fact]
    public async Task DismissThenColdLogin_CallRestoresDetachedHunterPetFromRealSqlite()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        const uint petNumber = 991001;
        const uint petSpell = 49502;
        const uint dismissSpell = 49503;
        const uint callSpell = 49504;
        const uint category = 77;
        ulong character;
        // The shared synthetic creature template intentionally has no mana cap; restore clamps
        // the stored mana to zero while preserving health, XP, happiness and cooldown state.
        PersistentPetSnapshot expected = new(0, petNumber, 9909001, 12, 3456, 321, 0, 654, 2,
            [GameActionButton.Make(petSpell, ActionType.Enabled).Packed, GameActionButton.Make(0, ActionType.Disabled).Packed],
            [new PersistentPetSpell(petSpell, true, false)],
            Cooldowns: [new PersistentPetCooldown(0, petSpell, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000),
                new PersistentPetCooldown(1, 0, category,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)]);

        await using (PersistentHost firstHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token))
        {
            await using WorldClient client = await AuthenticateAsync(firstHost, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await using (AsyncServiceScope scope = firstHost.Services.CreateAsyncScope())
            {
                CharacterDbContext characterDb = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
                CharacterRecord row = await characterDb.Characters.SingleAsync(r => r.Id == checked((int)character), token);
                row.Class = (byte)Class.Hunter;
                await characterDb.SaveChangesAsync(token);
            }

            await connection.LoginAsync(character, token);
            await firstHost.World.InvokeAsync(() =>
            {
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                InstallSyntheticPetSpells(firstHost);
                SpellFeature spells = firstHost.Services.GetRequiredService<SpellFeature>();
                spells.Spellbook.LearnSpell(owner, dismissSpell);
                spells.Spellbook.LearnSpell(owner, callSpell);
                expected = expected with { CharacterId = checked((int)character) };
                PetsFeature pets = firstHost.Services.GetRequiredService<PetsFeature>();
                Assert.NotNull(pets.Service.RestoreCurrentPet(owner, expected));
                Assert.Equal(petNumber, pets.Service.CaptureCurrentPet(owner)!.PetNumber);
                return true;
            }).WaitAsync(token);

            // RestoreCurrentPet publishes the initial pet-spells packet; consume it before
            // the wire dismiss so the next packet is the empty removal action bar.
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token);
            await connection.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(dismissSpell), token);
            Assert.Equal(new byte[8], await connection.ReadUntilAsync(WorldOpcode.SmsgPetSpells, token));
            await firstHost.World.InvokeAsync(() =>
            {
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                Assert.True(owner.PetGuid.IsEmpty);
                return true;
            }).WaitAsync(token);

            PetsFeature pets = firstHost.Services.GetRequiredService<PetsFeature>();
            await pets.Service.FlushCharacterAsync(checked((int)character));
            await using CharacterDbContext detachedDb = new(ClientOptions(fixture.CharacterDatabasePath));
            PersistentPetSnapshot? detached = await new EfPersistentPetStore(detachedDb).LoadCallableAsync(checked((int)character), token);
            Assert.NotNull(detached);
            Assert.False(detached!.IsCurrent);
            Assert.Null(await new EfPersistentPetStore(detachedDb).LoadCurrentAsync(checked((int)character), token));
            Assert.Equal((petNumber, expected.Entry, expected.Health, expected.Mana),
                (detached.PetNumber, detached.Entry, detached.Health, detached.Mana));
        }

        await using PersistentHost coldHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await coldHost.World.InvokeAsync(() =>
        {
            InstallSyntheticPetSpells(coldHost);
            return true;
        }).WaitAsync(token);
        await using WorldClient freshClient = await AuthenticateAsync(coldHost, token);
        var fresh = new ScenarioConnection(freshClient);
        await fresh.LoginAsync(character, token);
        Assert.Null(await coldHost.World.InvokeAsync(() =>
        {
            Player owner = coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            return owner.Map?.FindObject(owner.PetGuid);
        }).WaitAsync(token));

        await coldHost.World.InvokeAsync(() =>
        {
            Player owner = coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            coldHost.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(owner, callSpell);
            return true;
        }).WaitAsync(token);
        await fresh.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(callSpell), token);
        byte[] packet = await fresh.ReadUntilAsync(WorldOpcode.SmsgPetSpells, token);
        ulong restoredGuid = await coldHost.World.InvokeAsync(() =>
            coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.PetGuid.Value).WaitAsync(token);
        Assert.NotEqual(0ul, restoredGuid);
        Assert.True(packet.Length >= 57,
            $"synthetic SMSG_PET_SPELLS too short: length={packet.Length}, hex={Convert.ToHexString(packet)}");
        Assert.Equal(restoredGuid, BinaryPrimitives.ReadUInt64LittleEndian(packet));
        Assert.Equal(petNumber, await coldHost.World.InvokeAsync(() =>
            coldHost.Services.GetRequiredService<PetsFeature>().Service.CaptureCurrentPet(
                coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!)!.PetNumber).WaitAsync(token));
        PersistentPetSnapshot restored = await coldHost.World.InvokeAsync(() =>
            coldHost.Services.GetRequiredService<PetsFeature>().Service.CaptureCurrentPet(
                coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!)!).WaitAsync(token);
        Assert.Equal((expected.Health, expected.Mana), (restored.Health, restored.Mana));
        Assert.Contains(restored.Spells, spell => spell.SpellId == petSpell && spell.Autocast);
        Assert.NotNull(restored.Cooldowns);
        Assert.Contains(restored.Cooldowns!, cooldown => cooldown.Kind == 0 && cooldown.SpellId == petSpell && cooldown.Category == 0);
        Assert.Contains(restored.Cooldowns!, cooldown => cooldown.Kind == 1 && cooldown.SpellId == 0 && cooldown.Category == category);
        Assert.Equal(GameActionButton.Make(petSpell, ActionType.Enabled).Packed,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
        int spellCountOffset = 56;
        Assert.True(packet[spellCountOffset] > 0,
            $"synthetic SMSG_PET_SPELLS has no spells: length={packet.Length}, hex={Convert.ToHexString(packet)}");
        int cooldownCountOffset = spellCountOffset + 1 + packet[spellCountOffset] * 4;
        // One wire entry combines the independent spell and category timers.
        Assert.Equal((byte)1, packet[cooldownCountOffset]);
        int cooldownOffset = cooldownCountOffset + 1;
        Assert.Equal((ushort)petSpell, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(cooldownOffset)));
        Assert.Equal((ushort)category, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(cooldownOffset + 2)));
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(cooldownOffset + 4)) > 0);
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(cooldownOffset + 8)) > 0);

        await coldHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));
        await using CharacterDbContext promotedDb = new(ClientOptions(fixture.CharacterDatabasePath));
        PersistentPetSnapshot? promoted = await new EfPersistentPetStore(promotedDb).LoadCurrentAsync(checked((int)character), token);
        Assert.NotNull(promoted);
        Assert.True(promoted!.IsCurrent);
        Assert.Equal(petNumber, promoted.PetNumber);
    }

    private static byte[] CastSelf(uint spell)
    {
        var writer = new PacketWriter();
        writer.WriteUInt32(spell);
        writer.WriteUInt16(0);
        return writer.ToArray();
    }

    private static void InstallSyntheticPetSpells(PersistentHost host)
    {
        SpellFeature spells = host.Services.GetRequiredService<SpellFeature>();
        spells.System.Store = new SpellStore([.. spells.System.Store.All,
            new SpellInfo { Id = 49502, Name = "Synthetic pet cooldown", Category = 77,
                RecoveryTime = 60_000, CategoryRecoveryTime = 60_000 },
            new SpellInfo { Id = 49503, Name = "Synthetic dismiss", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.DismissPet, TargetA = SpellImplicitTarget.UnitCaster }] },
            new SpellInfo { Id = 49504, Name = "Synthetic call", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SummonPet, MiscValue = 0,
                    BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }] },
        ], [], []);
    }

    private static DbContextOptions<CharacterDbContext> ClientOptions(string path)
        => new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 1,
        }.ToString()).Options;
}
