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
/// HandlePetLoyaltyCommand, and <c>.pet rename</c> / <c>.pet delete</c> (SEC_GAMEMASTER, by stored pet number, owner offline) and <c>.pet learnspell</c> /
/// <c>.pet unlearnspell</c> (SEC_DEVELOPER, the selected live pet): HandlePetRenameCommand, HandlePetDeleteCommand, HandlePetLearnSpellCommand and
/// HandlePetUnlearnSpellCommand (CharacterCommands.cpp:5258-5345).
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

    public const string PetStoreFailed = "The pet store failed; nothing was changed. See the server log.";

    internal static string PetNotFound(uint number) => string.Create(CultureInfo.InvariantCulture, $"Pet #{number} not found");

    internal static string InvalidPetName(string name) => $"\"{name}\" is not a valid pet name";

    internal static string OwnerOnline(uint number, string owner) => string.Create(CultureInfo.InvariantCulture, $"Pet #{number} belongs to {owner}, who is online; log them out first.");

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
            new ChatCommand("rename", AccountSecurity.GameMaster, "Syntax: .pet rename #petnumber $newname\nRename a stored pet (see .pet list). Its owner must be offline.",
                Rename, RetailLevel: 3),
            new ChatCommand("delete", AccountSecurity.GameMaster, "Syntax: .pet delete #petnumber\nDelete a stored pet, current or stabled (see .pet list). Its owner must be offline.",
                Delete, RetailLevel: 3),
            new ChatCommand("learnspell", AccountSecurity.Administrator, "Syntax: .pet learnspell #spellid\nTeach the selected pet a spell.",
                LearnSpell, RetailLevel: 5),
            new ChatCommand("unlearnspell", AccountSecurity.Administrator, "Syntax: .pet unlearnspell #spellid\nMake the selected pet forget a spell.",
                UnlearnSpell, RetailLevel: 5),
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
        IDisposable hold = context.Defer(); // open until the world-thread reply ran
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
                try
                {
                    foreach (string line in lines)
                    {
                        context.Reply(line);
                    }
                }
                finally
                {
                    hold.Dispose();
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

    private static bool LearnSpell(CommandContext context, string text) => ChangeSpell(context, text, learn: true);

    private static bool UnlearnSpell(CommandContext context, string text) => ChangeSpell(context, text, learn: false);

    private static bool ChangeSpell(CommandContext context, string text, bool learn)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || spellId == 0 || !args.IsEmpty)
        {
            return false;
        }

        if (SelectedPet(context) is not { } pet || context.Session.Services.GetService<PetsFeature>()?.Service is not { } summons)
        {
            context.Reply(NoPetSelected);
            return true;
        }

        string name = SummonService.PetNameOf(pet);
        string reply = learn
            ? summons.LearnSpellByCommand(pet, spellId) ? $"Added spell {spellId} to pet {name}." : $"Failed to add spell {spellId} to pet {name}."
            : summons.UnlearnSpellByCommand(pet, spellId) ? $"Removed spell {spellId} from pet {name}." : $"Failed to remove spell {spellId} from pet {name}.";
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{reply}"));
        return true;
    }

    private static bool Rename(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint petNumber))
        {
            return false;
        }

        string raw = args.Rest.Trim();
        PetNameRules rules = context.Session.Services.GetService<PetNamingFeature>()?.Rules ?? new PetNameRules();
        if (rules.NormalizeAndValidate(raw) is not { } name)
        {
            context.Reply(InvalidPetName(raw));
            return true;
        }

        RunOnStoredPet(context, petNumber, async (store, found) =>
        {
            await store.RenameAsync(petNumber, name).ConfigureAwait(false);
            return string.Create(CultureInfo.InvariantCulture, $"Pet #{petNumber} (\"{found.Name}\", owner #{found.CharacterId}) renamed to \"{name}\"");
        });
        return true;
    }

    private static bool Delete(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint petNumber) || !args.IsEmpty)
        {
            return false;
        }

        RunOnStoredPet(context, petNumber, async (store, found) =>
        {
            await store.DeleteAsync(petNumber).ConfigureAwait(false);
            return string.Create(CultureInfo.InvariantCulture, $"Pet #{petNumber} (\"{found.Name}\", owner #{found.CharacterId}) deleted.");
        });
        return true;
    }

    /// <summary>
    /// Find the stored pet off the world thread, refuse it while its owner is online (the live pet and the login cache would write the old
    /// row back), fence the owner's pet against a login already under way, drain the owner's queued pet saves, run <paramref name="change"/>,
    /// then drop the owner's cached pet and release the fence so the next (or the waiting) login reads the database.
    /// </summary>
    private static void RunOnStoredPet(CommandContext context, uint petNumber, Func<IPetAdminStore, StoredPetLocation, Task<string>> change)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        SummonService? summons = context.Session.Services.GetService<PetsFeature>()?.Service;
        ILogger log = context.Session.Logger;
        IDisposable hold = context.Defer(); // open until the world-thread reply ran
        _ = Task.Run(async () =>
        {
            string reply;
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IPetAdminStore>() is not { } store)
                {
                    reply = PetsUnavailable;
                }
                else if (await store.FindAsync(petNumber).ConfigureAwait(false) is not { } found)
                {
                    reply = PetNotFound(petNumber);
                }
                else if (await context.World.InvokeAsync(() => OnlineOwnerOrFence(context, summons, found.CharacterId)).ConfigureAwait(false) is { } online)
                {
                    reply = OwnerOnline(petNumber, online);
                }
                else
                {
                    try
                    {
                        if (summons is not null)
                        {
                            await summons.FlushCharacterAsync(found.CharacterId).ConfigureAwait(false);
                        }

                        reply = await change(store, found).ConfigureAwait(false);
                    }
                    finally
                    {
                        summons?.EndStoredPetChange(found.CharacterId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogError(ex, "GM change of stored pet #{PetNumber} failed", petNumber);
                reply = PetStoreFailed;
            }

            context.World.Post(() =>
            {
                try
                {
                    context.Reply(reply);
                }
                finally
                {
                    hold.Dispose();
                }
            });
        });
    }

    /// <summary>
    /// World thread: the owner's name when they are online; otherwise fence their stored pet, in this same step, so a login already past its
    /// pet read (not yet online) rereads it after the change instead of restoring, and later saving back, what it read before.
    /// </summary>
    private static string? OnlineOwnerOrFence(CommandContext context, SummonService? summons, int characterId)
    {
        if (context.World.OnlinePlayers.FirstOrDefault(p => p.Guid.Low == (uint)characterId) is { } online)
        {
            return online.Name;
        }

        summons?.BeginStoredPetChange(characterId);
        return null;
    }
}