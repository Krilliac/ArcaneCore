using System.Globalization;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Dump;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Items;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Character;

/// <summary>Where <c>.pdump</c> reads and writes its files. Unregistered: <see cref="DefaultDirectory"/> under the working directory.</summary>
public sealed class PlayerDumpOptions
{
    public const string DefaultDirectory = "pdump";

    public string Directory { get; set; } = DefaultDirectory;
}

/// <summary>
/// <c>.pdump write $filename $playerNameOrGUID</c> and <c>.pdump load $filename $account [$newname] [$newguid]</c> (vmangos
/// HandlePDumpWriteCommand / HandlePDumpLoadCommand, CharacterCommands.cpp:4897-5040; SEC_ADMINISTRATOR, Chat.cpp:757-762), over
/// <see cref="PlayerDumpWriter"/> and <see cref="PlayerDumpReader"/>.
/// <para>
/// A deliberate difference: vmangos opens any path the administrator types. Here the file name is a bare name inside
/// <see cref="PlayerDumpOptions.Directory"/>; a path is refused as a file that cannot be opened. The database work runs off the world
/// thread; ids come from the running allocators (items, mail, item text, pet numbers), so a loaded character never collides with
/// live objects.
/// </para>
/// </summary>
public sealed class PlayerDumpCommands : ICommandGroup
{
    // mangos_string (dumps/z2815.sql).
    private const string ImportSuccess = "Character loaded successfully!";              // LANG_COMMAND_IMPORT_SUCCESS 479
    private const string ImportFailed = "Failed to load the character!";                // LANG_COMMAND_IMPORT_FAILED 480
    private const string ExportSuccess = "Character dumped successfully!";              // LANG_COMMAND_EXPORT_SUCCESS 481
    private const string ExportFailed = "Character dump failed!";                       // LANG_COMMAND_EXPORT_FAILED 482
    private const string FileOpenFail = "Failed to open file: {0}";                     // LANG_FILE_OPEN_FAIL 1112
    private const string AccountFull = "Account {0} ({1}) have max amount allowed characters (client limit)"; // LANG_ACCOUNT_CHARACTER_LIST_FULL 1113
    private const string DumpBroken = "Dump file have broken data!";                    // LANG_DUMP_BROKEN 1114
    private const string InvalidName = "Invalid character name!";                       // LANG_INVALID_CHARACTER_NAME 1115
    private const string InvalidGuid = "Invalid character guid!";                       // LANG_INVALID_CHARACTER_GUID 1116
    private const string GuidInUse = "Character guid {0} in use!";                      // LANG_CHARACTER_GUID_IN_USE 1117
    private const string AccountNotExist = "Account {0} does not exist.";               // LANG_ACCOUNT_NOT_EXIST 413

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("pdump", AccountSecurity.Administrator, "Syntax: .pdump $subcommand", Children:
        [
            new ChatCommand("load", AccountSecurity.Administrator,
                "Syntax: .pdump load $filename $account [$newname] [$newguid]\nLoad a character dump from the file into the account, under the new name and guid if given.",
                Load, RetailLevel: 6),
            new ChatCommand("write", AccountSecurity.Administrator,
                "Syntax: .pdump write $filename $playerNameOrGUID\nWrite the character's dump to the file.",
                Write, RetailLevel: 6),
        ], RetailLevel: 6),
    ];

    private static bool Write(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? file = args.ExtractQuotedOrLiteral();
        string? who = args.ExtractLiteral();
        if (file is null || who is null)
        {
            return false;
        }

        CharacterDirectory directory = context.Session.Services.GetRequiredService<CharacterDirectory>();
        CharacterIdentity? target = uint.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out uint guid) && guid <= int.MaxValue
            ? directory.Find((int)guid)
            : PlayerNames.TryNormalize(StripLink(who), out string name) ? directory.FindByName(name) : null;
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (ResolvePath(context, file) is not { } path)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, FileOpenFail, file));
            return true;
        }

        Run(context, async services =>
        {
            string? dump = await new PlayerDumpWriter(services.GetRequiredService<CharacterDbContext>()).GetDumpAsync(target.Id).ConfigureAwait(false);
            if (dump is null)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, dump).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Reply(string.Format(CultureInfo.InvariantCulture, FileOpenFail, file));
                return;
            }

            context.Reply(ExportSuccess);
        }, ExportFailed);
        return true;
    }

    private static bool Load(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? file = args.ExtractQuotedOrLiteral();
        string? account = args.ExtractLiteral();
        if (file is null || account is null)
        {
            return false;
        }

        string name = string.Empty;
        uint guid = 0;
        if (args.ExtractLiteral() is { } rawName)
        {
            CharacterCreationOptions options = context.Session.Services.GetService<CharacterCreationFeature>()?.Options ?? new();
            if (!PlayerNames.TryNormalize(rawName, out name) || CharacterNameRules.Check(name, NameRuleSettings.From(options, create: true)) is not null)
            {
                context.Reply(InvalidName);
                return true;
            }

            if (!args.IsEmpty)
            {
                if (!args.ExtractUInt32(out guid))
                {
                    return false;
                }

                if (guid == 0 || guid > int.MaxValue)
                {
                    context.Reply(InvalidGuid);
                    return true;
                }

                if (context.Session.Services.GetRequiredService<CharacterDirectory>().Find((int)guid) is not null)
                {
                    context.Reply(string.Format(CultureInfo.InvariantCulture, GuidInUse, guid));
                    return true;
                }
            }
        }

        if (ResolvePath(context, file) is not { } path)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, FileOpenFail, file));
            return true;
        }

        IServiceProvider world = context.Session.Services;
        Run(context, async services =>
        {
            if (await FindAccountAsync(services, account).ConfigureAwait(false) is not { } owner)
            {
                context.Reply(string.Format(CultureInfo.InvariantCulture, AccountNotExist, account.ToUpperInvariant()));
                return;
            }

            string dump;
            try
            {
                dump = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Reply(string.Format(CultureInfo.InvariantCulture, FileOpenFail, file));
                return;
            }

            CharacterDbContext db = services.GetRequiredService<CharacterDbContext>();
            IPlayerDumpIds ids = new WorldPlayerDumpIds(await StoredPlayerDumpIds.CreateAsync(db).ConfigureAwait(false), world);
            PlayerDumpLoad load = await new PlayerDumpReader(db).LoadDumpAsync(dump, owner.Id, name, (int)guid, ids).ConfigureAwait(false);
            switch (load.Result)
            {
                case DumpReturn.Success:
                    Kernel.Characters.CharacterRecord record = (await db.Characters.FindAsync(load.CharacterId).ConfigureAwait(false))!;
                    world.GetRequiredService<CharacterDirectory>().Add(new CharacterIdentity(record.Id, record.AccountId, record.Name,
                        record.Race, record.Gender, record.Class, record.Level, record.ZoneId));
                    context.Reply(ImportSuccess);
                    break;
                case DumpReturn.FileBroken or DumpReturn.UnexpectedEnd:
                    context.Reply(DumpBroken);
                    break;
                case DumpReturn.TooManyChars:
                    context.Reply(string.Format(CultureInfo.InvariantCulture, AccountFull, owner.Username, owner.Id));
                    break;
                case DumpReturn.NameInUse:
                    context.Reply(InvalidName);
                    break;
                default:
                    context.Reply(ImportFailed);
                    break;
            }
        }, ImportFailed);
        return true;
    }

    /// <summary>vmangos ExtractAccountId: an account name, or a number naming the account id.</summary>
    private static async Task<Account?> FindAccountAsync(IServiceProvider services, string value)
    {
        IAccountStore accounts = services.GetRequiredService<IAccountStore>();
        if (await accounts.FindByUsernameAsync(value).ConfigureAwait(false) is { } byName)
        {
            return byName;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && services.GetService<IAccountAdmin>() is { } admin
            && (await admin.GetUsernamesAsync([id]).ConfigureAwait(false)).TryGetValue(id, out string? username))
        {
            return await accounts.FindByUsernameAsync(username).ConfigureAwait(false);
        }

        return null;
    }

    private static string StripLink(string raw)
    {
        var args = new CommandArgs(raw);
        return args.ExtractKeyFromLink("Hplayer", out _, out _) ?? raw;
    }

    /// <summary>The file inside the dump directory, or null for anything but a bare file name.</summary>
    internal static string? ResolvePath(CommandContext context, string file)
    {
        if (file.Length == 0 || file is "." or ".." || file.IndexOfAny([.. System.IO.Path.GetInvalidFileNameChars(), '/', '\\']) >= 0)
        {
            return null;
        }

        string dir = context.Session.Services.GetService<PlayerDumpOptions>()?.Directory ?? PlayerDumpOptions.DefaultDirectory;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, file));
    }

    private static void Run(CommandContext context, Func<IServiceProvider, Task> work, string failure)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "the .pdump command failed");
                context.Reply(failure);
            }
        });
    }

    /// <summary>The running allocators, each raised above the stored maximum first; the stored counter alone when a feature is absent.</summary>
    private sealed class WorldPlayerDumpIds(StoredPlayerDumpIds stored, IServiceProvider world) : IPlayerDumpIds
    {
        private readonly uint _item = stored.NextItemGuid() - 1, _mail = stored.NextMailId() - 1, _text = stored.NextItemTextId() - 1, _pet = stored.NextPetNumber() - 1;

        public uint NextItemGuid()
        {
            if (world.GetService<ItemsFeature>() is not { } items)
            {
                return stored.NextItemGuid();
            }

            items.GuidAllocator.Seed(_item);
            return items.GuidAllocator.Next();
        }

        public uint NextMailId() => world.GetService<EconomyFeature>()?.ReserveMailId(_mail) ?? stored.NextMailId();

        public uint NextItemTextId() => world.GetService<EconomyFeature>()?.ReserveItemTextId(_text) ?? stored.NextItemTextId();

        public uint NextPetNumber() => world.GetService<PetsFeature>()?.Service.ReservePetNumberAbove(_pet) ?? stored.NextPetNumber();
    }
}
