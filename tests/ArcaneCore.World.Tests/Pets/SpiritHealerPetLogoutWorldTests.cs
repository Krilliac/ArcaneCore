using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

/// <summary>
/// The pet a battleground spirit guide brings back (vmangos <c>m_petEntry</c> / <c>m_petSpell</c>, Player::AutoReSummonPet) belongs to the
/// player object, so it does not outlive the session: in the world daemon a logout forgets it. Without that a warlock who relogs with no demon
/// (demons are not stored) would get the old session's demon back from a spirit heal, paying a soul shard for it.
/// </summary>
public sealed class SpiritHealerPetLogoutWorldTests
{
    [Fact]
    public async Task ALogout_ForgetsThePetTheSpiritHealerWouldBringBack()
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Remembered Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost prepared;
        try { prepared = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player); }
        finally { CreatureTestStore.Current.Value = null; }
        await using WorldTestHost host = prepared;
        await using WorldTestClient client = await host.EnterWorldAsync("SPIRITPET", "Spiritpet");
        PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();

        Player player = await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Spiritpet")!;
            hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
            Assert.NotNull(pets.Service.RestoreCurrentPet(hunter,
                new PersistentPetSnapshot((int)hunter.Guid.Low, 811, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], [])));
            Assert.NotNull(pets.Service.PetForSpiritHealer(hunter));
            return hunter;
        });

        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);

        Assert.Null(await host.OnWorldAsync(() => pets.Service.PetForSpiritHealer(player)));
    }
}
