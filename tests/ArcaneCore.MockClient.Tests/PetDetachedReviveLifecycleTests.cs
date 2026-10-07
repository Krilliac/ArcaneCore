using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
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

namespace ArcaneCore.MockClient.Tests;

public sealed partial class WorldLifecyclePersistenceTests
{
    [Fact]
    public async Task ColdDetachedDeadPet_Effect109RevivesAndPromotesNameCooldowns()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        const uint petNumber = 991301;
        const uint reviveSpell = 49505;
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
            await firstHost.World.InvokeAsync(() =>
            {
                InstallSyntheticPetSpells(firstHost);
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                PersistentPetSnapshot snapshot = new((int)character, petNumber, 9909001, 12, 3456, 321, 0, 654, 2, [],
                    [new PersistentPetSpell(49502, true, false)],
                    Cooldowns: [new PersistentPetCooldown(0, 49502, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)],
                    Name: "Rex", NameTimestamp: 7, RenameAllowed: false);
                Assert.NotNull(firstHost.Services.GetRequiredService<PetsFeature>().Service.RestoreCurrentPet(owner, snapshot));
                return true;
            }).WaitAsync(token);
            await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token);
            await firstHost.World.InvokeAsync(() =>
            {
                Player owner = firstHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                Creature pet = owner.GetPet()!;
                owner.Map!.FindUpdater<CreatureMapSystem>()!.KillCreature(pet);
                PetsFeature pets = firstHost.Services.GetRequiredService<PetsFeature>();
                pets.Service.QueueDetachedPetSave(owner);
                pets.Service.Unsummon(pet);
                return true;
            }).WaitAsync(token);
            await firstHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));
            await using CharacterDbContext detachedDb = new(ClientOptions(fixture.CharacterDatabasePath));
            PersistentPetSnapshot? detached = await new EfPersistentPetStore(detachedDb).LoadCallableAsync(checked((int)character), token);
            Assert.NotNull(detached);
            Assert.False(detached!.IsCurrent);
            Assert.Equal(0u, detached.Health);
            Assert.Equal("Rex", detached.Name);
        }

        await using PersistentHost coldHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await coldHost.World.InvokeAsync(() =>
        {
            InstallSyntheticPetSpells(coldHost);
            SpellFeature feature = coldHost.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = reviveSpell, Name = "Synthetic detached revive", Attributes = SpellAttributes.AllowCastWhileDead,
                RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SummonDeadPet, BasePoints = 49, BaseDice = 1,
                    DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            return true;
        }).WaitAsync(token);
        await using WorldClient freshClient = await AuthenticateAsync(coldHost, token);
        var fresh = new ScenarioConnection(freshClient);
        await fresh.LoginAsync(character, token);
        await coldHost.World.InvokeAsync(() =>
        {
            Player owner = coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            coldHost.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(owner, reviveSpell);
            Assert.Null(owner.GetPet());
            return true;
        }).WaitAsync(token);
        await fresh.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(reviveSpell), token);
        await freshClient.ReadUntilAsync((ushort)WorldOpcode.SmsgPetSpells, token);
        PersistentPetSnapshot restored = await coldHost.World.InvokeAsync(() =>
            coldHost.Services.GetRequiredService<PetsFeature>().Service.CaptureCurrentPet(
                coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!)!).WaitAsync(token);
        Assert.Equal((petNumber, "Rex", 7u), (restored.PetNumber, restored.Name, restored.NameTimestamp));
        Assert.True(restored.IsCurrent);
        Assert.True(restored.Health > 0);
        Assert.Contains(restored.Cooldowns!, cooldown => cooldown.SpellId == 49502);
        await coldHost.Services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(checked((int)character));
        await using CharacterDbContext verifiedDb = new(ClientOptions(fixture.CharacterDatabasePath));
        PersistentPetSnapshot? persisted = await new EfPersistentPetStore(verifiedDb).LoadCurrentAsync(checked((int)character), token);
        Assert.NotNull(persisted);
        Assert.Equal((restored.Health, petNumber, "Rex", 7u, false),
            (persisted.Health, persisted.PetNumber, persisted.Name, persisted.NameTimestamp, persisted.RenameAllowed));
        Assert.Contains(persisted.Cooldowns!, cooldown => cooldown.SpellId == 49502);
    }
}
