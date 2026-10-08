using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// A hunter's pet goes with it through a far teleport, seen from another session (vmangos Player::ExecuteTeleportFar
/// "remove pet on map change", Player.cpp:2045-2048, and HandleMoveWorldportAckOpcode "resummon pet", MovementHandler.cpp:197-198).
/// Two scripted bots: a hunter with its current pet on Eastern Kingdoms, and a watcher already standing in Kalimdor. The
/// hunter is teleported across through the ordinary teleport service; the pet comes back at its side in Kalimdor with the same pet
/// number, the hunter gets its pet bar again (SMSG_PET_SPELLS for the new pet), and the watcher's client is sent the pet.
/// </summary>
public sealed class PetTeleportScenarioTests
{
    private const string Hunter = "Scnhunter";
    private const uint PetNumber = 990777;
    private const float KalimdorX = 1600f;
    private const float KalimdorY = -4400f;
    private const float KalimdorZ = 10f;

    [Fact]
    public async Task FarTeleport_TheHuntersPetArrivesWithIt_AndAnotherSessionSeesIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        SummonService pets = world.Services.GetRequiredService<PetsFeature>().Service;
        await world.RunPassingAsync(new DelegateScenario("pet-teleport", async context =>
        {
            ScenarioBot hunter = await context.StepAsync("login hunter", () => context.LoginAsync(Hunter));
            ScenarioBot watcher = await context.StepAsync("login watcher", () => context.LoginAsync(PlayerbotScenarioCatalog.BotB));
            Player hunterPlayer = await hunter.ReadAsync(p => p);
            Player watcherPlayer = await watcher.ReadAsync(p => p);

            // The test content creates warriors only (InMemoryWorldDataStore); the class byte makes this bot a hunter on the world thread.
            await context.StepAsync("make the bot a hunter", () => context.ReadAsync(() =>
            {
                hunterPlayer.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                return hunterPlayer.Class;
            }));

            await context.StepAsync("watcher waits in Kalimdor", () => context.PlaceAsync(watcher, 1, KalimdorX, KalimdorY, KalimdorZ));

            ObjectGuid firstPet = await context.StepAsync("hunter calls its pet", async () =>
            {
                ObjectGuid guid = await context.ReadAsync(() => pets.RestoreCurrentPet(hunterPlayer, new PersistentPetSnapshot(
                    (int)hunterPlayer.Guid.Low, PetNumber, WolfEntry, 5, 0, 9, 0, 0, 1, [], []))?.Guid ?? ObjectGuid.Empty);
                ScenarioContext.Expect(!guid.IsEmpty, "the pet was summoned");
                ScenarioContext.ExpectEqual(guid, await hunter.ReadAsync(p => p.PetGuid), "hunter pet guid");
                return guid;
            });

            long mark = hunter.Mark();
            await context.StepAsync("hunter is teleported to Kalimdor", () => context.PlaceAsync(hunter, 1, KalimdorX + 3f, KalimdorY, KalimdorZ));

            ObjectGuid petGuid = await context.StepAsync("the pet came back at the hunter's side", async () =>
            {
                await context.WaitUntilAsync("the hunter has a pet in Kalimdor", () =>
                    hunterPlayer.Map is { MapId: 1 } map && map.FindObject(hunterPlayer.PetGuid) is Creature);
                return await context.ReadAsync(() =>
                {
                    var pet = (Creature)hunterPlayer.Map!.FindObject(hunterPlayer.PetGuid)!;
                    ScenarioContext.ExpectEqual(PetNumber, pet.Guid.Entry, "pet number (the pet GUID carries it)");
                    ScenarioContext.ExpectEqual(WolfEntry, pet.Entry, "pet creature");
                    ScenarioContext.ExpectEqual(9u, pet.Health, "pet health (the wounded pet keeps its health)");
                    ScenarioContext.ExpectEqual(hunterPlayer.Guid, pet.OwnerGuid, "pet owner");
                    ScenarioContext.Expect(MathF.Abs(pet.X - hunterPlayer.X) < 5f && MathF.Abs(pet.Y - hunterPlayer.Y) < 5f, "the pet stands by the hunter");
                    ScenarioContext.Expect(world.Host.World.GetMap(0).FindObject(firstPet) is null, "the old pet left Eastern Kingdoms");
                    return pet.Guid;
                });
            });

            await context.StepAsync("the hunter got its pet bar back", async () =>
            {
                await context.WaitUntilAsync("SMSG_PET_SPELLS for the new pet", () =>
                    hunter.Received(WorldOpcode.SmsgPetSpells, payload => payload, mark).Any(p => p.Length >= 8 && BitConverter.ToUInt64(p, 0) == petGuid.Value));
            });

            await context.StepAsync("the watcher's client is sent the pet", () =>
                context.WaitUntilAsync("the watcher sees the hunter's pet", () => watcherPlayer.VisibleObjects.Contains(petGuid)));
        }));
    }
}
