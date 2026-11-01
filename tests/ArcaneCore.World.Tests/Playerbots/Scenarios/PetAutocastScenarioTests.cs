using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// A managed hunter bot with a live pet turns on the autocast of the pet's harmful spells (Growl, Claw) through the real packet path: the 13-byte
/// CMSG_PET_SPELL_AUTOCAST that <see cref="PlayerbotPetControl.EnableHarmfulAutocast"/> builds goes through the managed session to
/// <c>PetController.HandleAutocast</c>, and the pet's <see cref="CharmInfo"/> ends with both spells enabled (vmangos HandlePetSpellAutocastOpcode,
/// PetHandler.cpp:420-450). A passive spell and a beneficial spell stay as they were.
/// </summary>
public sealed class PetAutocastScenarioTests
{
    private const string Hunter = "Scnhunter";
    private const uint PetNumber = 990778;
    private const uint GrowlId = 940101;
    private const uint ClawId = 940102;
    private const uint PassiveId = 940103;
    private const uint BuffId = 940104;

    [Fact]
    public async Task BotPath_EnablesAutocastOfGrowlAndClaw_AndLeavesPassiveAndBeneficialSpellsAlone()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        SummonService pets = world.Services.GetRequiredService<PetsFeature>().Service;
        SpellSystem spells = world.Services.GetRequiredService<SpellFeature>().System;
        await world.RunPassingAsync(new DelegateScenario("pet-autocast", async context =>
        {
            ScenarioBot hunter = await context.StepAsync("login hunter", () => context.LoginAsync(Hunter));
            Player hunterPlayer = await hunter.ReadAsync(p => p);

            await context.StepAsync("make the bot a hunter", () => context.ReadAsync(() =>
            {
                hunterPlayer.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                hunterPlayer.Level = 5;
                return hunterPlayer.Class;
            }));

            await context.StepAsync("hunter calls its pet", async () =>
            {
                ObjectGuid guid = await context.ReadAsync(() => pets.RestoreCurrentPet(hunterPlayer, new PersistentPetSnapshot(
                    (int)hunterPlayer.Guid.Low, PetNumber, ScenarioTestContent.WolfEntry, 5, 0, 9, 0, 0, 1, [], []))?.Guid ?? ObjectGuid.Empty);
                ScenarioContext.Expect(!guid.IsEmpty, "the pet was summoned");
            });

            await context.StepAsync("the bot path turns the autocast on", () => context.ReadAsync(() =>
            {
                var pet = (Creature)hunterPlayer.Map!.FindObject(hunterPlayer.PetGuid)!;
                CharmInfo charm = pet.Summon!.Charm!;
                SpellStore original = spells.Store;
                spells.Store = new SpellStore(
                [
                    RotationTestKit.Hostile("Growl", id: GrowlId),
                    RotationTestKit.Hostile("Claw", id: ClawId),
                    RotationTestKit.Hostile("Furious Howl Passive", id: PassiveId) with { Attributes = SpellAttributes.Passive },
                    RotationTestKit.Aura("Pet Buff") with { Id = BuffId },
                ], [], []);
                try
                {
                    ScenarioContext.Expect(charm.LearnSpell(GrowlId) && charm.LearnSpell(ClawId), "Growl and Claw were learned");
                    ScenarioContext.Expect(charm.LearnSpell(PassiveId, ActionType.Passive) && charm.LearnSpell(BuffId), "the passive and beneficial spells were learned");
                    ScenarioContext.ExpectEqual(ActionType.Disabled, charm.SpellStates[GrowlId], "Growl starts with autocast off");
                    ScenarioContext.ExpectEqual(ActionType.Disabled, charm.SpellStates[ClawId], "Claw starts with autocast off");

                    hunter.Session!.ManagedBudget = null;
                    int sent = PlayerbotPetControl.EnableHarmfulAutocast(hunter.Session!, hunterPlayer, spells);

                    ScenarioContext.ExpectEqual(2, sent, "one autocast request each for Growl and Claw");
                    ScenarioContext.ExpectEqual(ActionType.Enabled, charm.SpellStates[GrowlId], "Growl autocast");
                    ScenarioContext.ExpectEqual(ActionType.Enabled, charm.SpellStates[ClawId], "Claw autocast");
                    ScenarioContext.ExpectEqual(ActionType.Passive, charm.SpellStates[PassiveId], "the passive spell is untouched");
                    ScenarioContext.ExpectEqual(ActionType.Disabled, charm.SpellStates[BuffId], "the beneficial spell is untouched");
                    ScenarioContext.ExpectEqual(0, PlayerbotPetControl.EnableHarmfulAutocast(hunter.Session!, hunterPlayer, spells), "nothing is left to enable");
                }
                finally
                {
                    spells.Store = original;
                }

                return true;
            }));
        }));
    }
}
