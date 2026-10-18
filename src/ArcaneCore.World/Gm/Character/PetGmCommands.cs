using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// The <c>.pet</c> root (vmangos petCommandTable, Chat.cpp; the root is SEC_GAMEMASTER, :1308) with <c>.pet info</c> (SEC_MODERATOR),
/// <c>.pet list</c> and <c>.pet loyalty</c> (SEC_GAMEMASTER): CharacterCommands.cpp HandlePetInfoCommand, HandlePetListCommand and
/// HandlePetLoyaltyCommand. <c>rename</c>, <c>delete</c>, <c>learnspell</c> and <c>unlearnspell</c> are not provided.
/// </summary>
public sealed class PetGmCommands : ICommandGroup
{
    /// <summary>vmangos PetType: SUMMON_PET.</summary>
    public const uint SummonPet = 0;

    /// <summary>vmangos PetType: HUNTER_PET.</summary>
    public const uint HunterPet = 1;

    public const string NoPetSelected = "Select a pet first.";

    public const string NotAHunterPet = "Only a hunter pet has loyalty.";

    public const string PetsUnavailable = "The pet store is unavailable; nothing was read. See the server log.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("pet", AccountSecurity.GameMaster, "Syntax: .pet $subcommand\nType .pet to see the list of possible subcommands or .help pet $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .pet list $playername\nList the stored pets of a character, current and stabled.",
                List, RetailLevel: 3),
            new ChatCommand("loyalty", AccountSecurity.GameMaster, "Syntax: .pet loyalty #points\nAdd (or with a negative number take) loyalty points of the selected hunter pet.",
                Loyalty, RetailLevel: 3),
            new ChatCommand("info", AccountSecurity.Moderator, "Syntax: .pet info\nShow the state of the selected pet.",
                Info, RetailLevel: 1),
        ], RetailLevel: 3),
    ];

    private static Creature? SelectedPet(CommandContext context)
        => GmSelectedUnit.Of(context) is Creature pet && SummonService.IsPet(pet) ? pet : null;

    private static bool IsHunterPet(Creature pet) => pet.GetOwner() is Player { Class: Class.Hunter };

    private static bool Info(CommandContext context, string text)
    {
        if (SelectedPet(context) is not { } pet)
        {
            context.Reply(NoPetSelected);
            return true;
        }

        string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        context.Reply(F($"Info for 0x{pet.Guid.Value:X16}"));
        context.Reply(F($"Owner: 0x{pet.OwnerGuid.Value:X16}"));
        context.Reply(F($"Pet type: {(IsHunterPet(pet) ? HunterPet : SummonPet)}"));
        context.Reply(F($"Loyalty level: {PetLoyalty.Level(pet)}"));
        context.Reply(F($"Pet number: {pet.GetUInt32(UpdateFields.UnitFieldPetnumber)}"));
        context.Reply(F($"Pet name timestamp: {pet.GetUInt32(UpdateFields.UnitFieldPetNameTimestamp)}"));
        context.Reply(F($"Pet experience: {pet.GetUInt32(UpdateFields.UnitFieldPetexperience)}"));
        context.Reply(F($"Pet next level xp: {pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp)}"));
        context.Reply(F($"Training points: {pet.GetUInt32(UpdateFields.UnitTrainingPoints)}"));
        return true;
    }

    private static bool Loyalty(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.IsEmpty || !args.ExtractInt32(out int points) || !args.IsEmpty)
        {
            return false;
        }

        if (SelectedPet(context) is not { } pet)
        {
            context.Reply(NoPetSelected);
            return true;
        }

        if (!IsHunterPet(pet) || pet.GetOwner() is not Player owner)
        {
            context.Reply(NotAHunterPet);
            return true;
        }

        if (!context.CanActOn(owner) || context.Session.Services.GetService<PetsFeature>()?.Service is not { } summons)
        {
            return true;
        }

        summons.ModifyLoyaltyByCommand(pet, owner, points);
        return true;
    }

    private static bool List(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
        if (raw is null || !args.IsEmpty)
        {
            return false;
        }

        if (!PlayerNames.TryNormalize(raw, out string name)
            || context.Session.Services.GetService<CharacterDirectory>()?.FindByName(name) is not { } identity)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger log = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            List<string> lines;
            try
            {
                lines = await ReadAsync(scopes, identity.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Reading the pets of character {Id} for .pet list failed", identity.Id);
                lines = [PetsUnavailable];
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Found {lines.Count(l => l.StartsWith('#'))} pets for character {identity.Name} (#{identity.Id})."));
            context.World.Post(() =>
            {
                foreach (string line in lines)
                {
                    context.Reply(line);
                }
            });
        });
        return true;
    }

    private static async Task<List<string>> ReadAsync(IServiceScopeFactory scopes, int characterId)
    {
        var lines = new List<string>();
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IPersistentPetStore>() is { } current
            && await current.LoadCurrentAsync(characterId).ConfigureAwait(false) is { } pet)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"#{pet.PetNumber}: \"{pet.Name}\" (Current pet)"));
        }

        if (scope.ServiceProvider.GetService<IPetStableStore>() is { } stable)
        {
            foreach (StabledPet stabled in (await stable.LoadAsync(characterId).ConfigureAwait(false)).Pets.OrderBy(p => p.Slot))
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"#{stabled.Pet.PetNumber}: \"{stabled.Pet.Name}\" (In stable)"));
            }
        }

        return lines;
    }
}