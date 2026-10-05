using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
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
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using GameActionButton = ArcaneCore.Game.Pets.ActionButton;

namespace ArcaneCore.MockClient.Tests;

public sealed partial class WorldLifecyclePersistenceTests
{
    [Fact]
    public async Task SocketHunterAbandon_DeletesPetAndCooldownRows_AndColdCallReportsNoPet()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        const uint petNumber = 991101;
        const uint petSpell = 49502;
        const uint callSpell = 49504;
        const uint category = 77;
        ulong character;
        ulong petGuid;

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
                InstallSyntheticPetSpells(firstHost);
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                PersistentPetSnapshot snapshot = new((int)character, petNumber, 9909001, 12, 3456, 321, 0, 654, 2,
                    [GameActionButton.Make(petSpell, ActionType.Enabled).Packed],
                    [new PersistentPetSpell(petSpell, true, false)],
                    Cooldowns: [new PersistentPetCooldown(0, petSpell, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000),
                        new PersistentPetCooldown(1, 0, category, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)]);
                Assert.NotNull(firstHost.Services.GetRequiredService<PetsFeature>().Service.RestoreCurrentPet(owner, snapshot));
                return true;
            }).WaitAsync(token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token);
            petGuid = await firstHost.World.InvokeAsync(() =>
                firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.GetPet()!.Guid.Value).WaitAsync(token);

            PetsFeature firstPets = firstHost.Services.GetRequiredService<PetsFeature>();
            await firstHost.World.InvokeAsync(() =>
            {
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                firstPets.Service.QueueCurrentPetSave(owner);
                return true;
            }).WaitAsync(token);
            await firstPets.Service.FlushCharacterAsync(checked((int)character));
            await using CharacterDbContext beforeDb = new(ClientOptions(fixture.CharacterDatabasePath));
            Assert.NotNull(await new EfPersistentPetStore(beforeDb).LoadCurrentAsync(checked((int)character), token));
            Assert.Equal(2, await beforeDb.Set<PersistentPetCooldownRow>()
                .CountAsync(r => r.CharacterId == checked((int)character), token));

            await client.SendAsync((ushort)WorldOpcode.CmsgPetAbandon, BitConverter.GetBytes(petGuid), token);
            Assert.Equal(new byte[8], (await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token)).Payload);
            await firstPets.Service.FlushCharacterAsync(checked((int)character));
            await using CharacterDbContext abandonDb = new(ClientOptions(fixture.CharacterDatabasePath));
            Assert.Null(await new EfPersistentPetStore(abandonDb).LoadCallableAsync(checked((int)character), token));
            Assert.Empty(await abandonDb.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == checked((int)character)).ToListAsync(token));
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
        await coldHost.World.InvokeAsync(() =>
        {
            Player owner = coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            coldHost.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(owner, callSpell);
            Assert.Null(owner.GetPet());
            return true;
        }).WaitAsync(token);
        await fresh.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(callSpell), token);
        Assert.Equal([7], (await freshClient.ReadUntilAsync((ushort)WorldOpcode.SmsgPetTameFailure, token)).Payload);
    }

}
