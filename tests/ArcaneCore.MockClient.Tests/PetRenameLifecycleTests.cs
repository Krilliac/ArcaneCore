using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed partial class WorldLifecyclePersistenceTests
{
    [Fact]
    public async Task SocketHunterRename_PersistsAcrossColdHost_AndSecondRenameIsRefused()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        const uint petNumber = 991201;
        ulong character;

        await using (PersistentHost firstHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token))
        {
            await using WorldClient client = await AuthenticateAsync(firstHost, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await using (AsyncServiceScope scope = firstHost.Services.CreateAsyncScope())
            {
                CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
                CharacterRecord row = await db.Characters.SingleAsync(r => r.Id == checked((int)character), token);
                row.Class = (byte)Class.Hunter;
                await db.SaveChangesAsync(token);
            }
            await connection.LoginAsync(character, token);
            ulong petGuid = await firstHost.World.InvokeAsync(() =>
            {
                InstallSyntheticPetSpells(firstHost);
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                PersistentPetSnapshot snapshot = new((int)character, petNumber, 9909001, 12, 3456, 321, 0, 654, 2, [], []);
                Assert.NotNull(firstHost.Services.GetRequiredService<PetsFeature>().Service.RestoreCurrentPet(owner, snapshot));
                return owner.GetPet()!.Guid.Value;
            }).WaitAsync(token);
            Assert.NotEqual(UnitFlags.None, await firstHost.World.InvokeAsync(() =>
                firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!.UnitFlags & UnitFlags.PetRename).WaitAsync(token));
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token);
            await firstHost.World.InvokeAsync(() =>
            {
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                firstHost.Services.GetRequiredService<PetsFeature>().Service.QueueCurrentPetSave(owner);
                return true;
            }).WaitAsync(token);
            await firstHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));

            var rename = new PacketWriter();
            rename.WriteUInt64(petGuid);
            rename.WriteCString("Rex");
            await client.SendAsync((ushort)WorldOpcode.CmsgPetRename, rename.ToArray(), token);
            var query = new PacketWriter();
            query.WriteUInt32(petNumber);
            query.WriteUInt64(petGuid);
            await client.SendAsync((ushort)WorldOpcode.CmsgPetNameQuery, query.ToArray(), token);
            byte[] firstResponse = (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetNameQueryResponse, token)).Payload;
            var firstReader = new PacketReader(firstResponse);
            firstReader.ReadUInt32();
            Assert.Equal("Rex", firstReader.ReadCString());
            Assert.True(firstReader.ReadUInt32() > 0);
            Assert.Equal(0u, await firstHost.World.InvokeAsync(() =>
                (uint)(firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!.UnitFlags & UnitFlags.PetRename)).WaitAsync(token));
            await firstHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));
            await using CharacterDbContext persistedDb = new(ClientOptions(fixture.CharacterDatabasePath));
            PersistentPetSnapshot? persisted = await new EfPersistentPetStore(persistedDb).LoadCurrentAsync(checked((int)character), token);
            Assert.Equal("Rex", persisted!.Name);
        }

        await using PersistentHost coldHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await coldHost.World.InvokeAsync(() => { InstallSyntheticPetSpells(coldHost); return true; }).WaitAsync(token);
        await using WorldClient freshClient = await AuthenticateAsync(coldHost, token);
        var fresh = new ScenarioConnection(freshClient);
        await fresh.LoginAsync(character, token);
        ulong freshGuid = await coldHost.World.InvokeAsync(() => coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!.Guid.Value).WaitAsync(token);
        Assert.Equal(0u, await coldHost.World.InvokeAsync(() =>
            (uint)(coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!.UnitFlags & UnitFlags.PetRename)).WaitAsync(token));
        var coldQuery = new PacketWriter();
        coldQuery.WriteUInt32(petNumber);
        coldQuery.WriteUInt64(freshGuid);
        await freshClient.SendAsync((ushort)WorldOpcode.CmsgPetNameQuery, coldQuery.ToArray(), token);
        byte[] response = (await freshClient.ReadUntilAsync((ushort)WorldOpcode.SmsgPetNameQueryResponse, token)).Payload;
        var reader = new PacketReader(response);
        Assert.Equal(petNumber, reader.ReadUInt32());
        Assert.Equal("Rex", reader.ReadCString());
        uint nameTimestamp = reader.ReadUInt32();
        Assert.True(nameTimestamp > 0);
        Assert.Equal(nameTimestamp, await coldHost.World.InvokeAsync(() =>
            coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!
                .GetUInt32(UpdateFields.UnitFieldPetNameTimestamp)).WaitAsync(token));

        var second = new PacketWriter();
        second.WriteUInt64(freshGuid);
        second.WriteCString("Nope");
        await freshClient.SendAsync((ushort)WorldOpcode.CmsgPetRename, second.ToArray(), token);
        await coldHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));
        await freshClient.SendAsync((ushort)WorldOpcode.CmsgPetNameQuery, coldQuery.ToArray(), token);
        byte[] unchanged = (await freshClient.ReadUntilAsync((ushort)WorldOpcode.SmsgPetNameQueryResponse, token)).Payload;
        var unchangedReader = new PacketReader(unchanged);
        unchangedReader.ReadUInt32();
        Assert.Equal("Rex", unchangedReader.ReadCString());
    }
}
