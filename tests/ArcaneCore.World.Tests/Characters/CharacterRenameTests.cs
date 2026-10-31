using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Characters;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Characters.Rename;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Names;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// Character rename (mangos HandleCharRenameOpcode, CharacterHandlerCustomize.cpp:86-190; <c>.character rename</c>,
/// PlayerCommands.cpp:166-200): a flagged character of the account is renamed to a valid, unused name and its flag is cleared; every
/// other case answers and changes nothing.
/// </summary>
public sealed class CharacterRenameTests
{
    private const byte NoName = 0x45;
    private const byte TooShort = 0x46;
    private const byte TooLong = 0x47;
    private const byte MixedLanguages = 0x49;
    private const byte CreateError = 0x2F;
    private const byte Profane = 0x4A;
    private const byte Reserved = 0x4B;

    private static byte[] Request(ulong guid, string name) => Request(guid, Encoding.UTF8.GetBytes(name));

    private static byte[] Request(ulong guid, byte[] rawName)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(guid);
        writer.WriteBytes(rawName);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    private static byte[] Code(byte code) => [code];

    private static InMemoryRenameStore Flags(WorldTestHost host) => host.WorldServices.GetRequiredService<InMemoryRenameStore>();

    private static async Task<(WorldTestClient Client, byte[] Key)> AccountWithAsync(WorldTestHost host, string account, params string[] characters)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        foreach (string name in characters)
        {
            await client.CreateCharacterAsync(name);
        }

        return (client, key);
    }

    /// <summary>Send CMSG_CHAR_RENAME and read SMSG_CHAR_RENAME.</summary>
    private static async Task<byte[]> RenameAsync(WorldTestClient client, ulong guid, string name)
    {
        await client.SendAsync(WorldOpcode.CmsgCharRename, Request(guid, name));
        return await client.ReadUntilAsync(WorldOpcode.SmsgCharRename);
    }

    [Fact]
    public void TheHandler_IsDiscovered_AndAcceptsTheOpcodeAtTheCharacterScreenOnly()
    {
        OpcodeTable table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Assert.True(table.TryGet(WorldOpcode.CmsgCharRename, out OpcodeHandler? handler));
        Assert.NotNull(handler.Session);
        Assert.True(handler.AllowsState(SessionState.CharacterSelect));
        Assert.False(handler.AllowsState(SessionState.InWorld));
        Assert.False(handler.AllowsState(SessionState.Connected));
    }

    [Fact]
    public async Task AFlaggedCharacter_IsRenamed_TheFlagIsCleared_AndTheDirectoryFollows()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME1", "Oldname");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            byte[] reply = await RenameAsync(client, 1, "jaina"); // normalized to "Jaina"

            var reader = new PacketReader(reply);
            Assert.Equal(0, reader.ReadByte()); // RESPONSE_SUCCESS
            Assert.Equal(1ul, reader.ReadUInt64());
            Assert.Equal("Jaina", reader.ReadCString());
            Assert.Equal(0, reader.Remaining);
            Assert.Equal("Jaina", (await host.Characters.GetByIdAsync(1))!.Name);
            Assert.Equal(0u, Flags(host).FlagsOf(1)); // cleared
            Assert.Equal("Jaina", host.Directory.Find(1)!.Name);
            Assert.NotNull(host.Directory.FindByName("jaina"));
            Assert.Null(host.Directory.FindByName("Oldname"));

            // The flag is gone, so the same character cannot be renamed again.
            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "Thrall"));
            Assert.Equal("Jaina", (await host.Characters.GetByIdAsync(1))!.Name);
        }
    }

    [Fact]
    public async Task ARename_TellsEveryOnlineClientToDropItsCachedName()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME9", "Cached");
        await using WorldTestClient watcher = await host.EnterWorldAsync("RENAMEW", "Watcher");
        await watcher.CollectAsync();
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            Assert.Equal(0, (await RenameAsync(client, 1, "Recached"))[0]);

            byte[] invalidate = await watcher.ReadUntilAsync(WorldOpcode.SmsgInvalidatePlayer);
            Assert.Equal(1ul, new PacketReader(invalidate).ReadUInt64());

            // A refused rename invalidates nothing.
            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "Another"));
            await watcher.AssertSilentAsync(TimeSpan.FromMilliseconds(200));
        }
    }

    [Fact]
    public async Task ANameThatIsTaken_IsRefused_InAnyCase_AndTheFlagStays()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME2", "Firstone", "Taken");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "Taken"));
            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "tAKEN")); // normalizes to the taken name
            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "Firstone")); // the character's own name is a duplicate too

            Assert.Equal("Firstone", (await host.Characters.GetByIdAsync(1))!.Name);
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(1));

            // A free name still works afterwards.
            Assert.Equal(0, (await RenameAsync(client, 1, "Freename"))[0]);
        }
    }

    [Fact]
    public async Task ACharacterWithoutTheFlag_OrOfAnotherAccount_IsNotRenamed()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient mine, _) = await AccountWithAsync(host, "RENAME3", "Mine");
        (WorldTestClient other, _) = await AccountWithAsync(host, "RENAME4", "Theirs");
        await using (mine)
        await using (other)
        {
            // Not flagged.
            Assert.Equal(Code(CreateError), await RenameAsync(mine, 1, "Newmine"));

            // Flagged, but it belongs to the other account.
            await Flags(host).SetFlagAsync(2, CharacterAtLoginFlags.Rename);
            Assert.Equal(Code(CreateError), await RenameAsync(mine, 2, "Stolen"));
            Assert.Equal("Theirs", (await host.Characters.GetByIdAsync(2))!.Name);
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(2));

            // No such character, and a guid with high bits (not a player guid of this realm).
            Assert.Equal(Code(CreateError), await RenameAsync(mine, 99, "Nobody"));
            Assert.Equal(Code(CreateError), await RenameAsync(mine, 0x0000_0001_0000_0001, "Nobody"));
        }
    }

    [Fact]
    public async Task InvalidNames_AreAnsweredWithTheNameCodes_BeforeTheStoreIsAsked()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME5", "Valid");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            Assert.Equal(Code(TooShort), await RenameAsync(client, 1, "A"));
            Assert.Equal(Code(TooLong), await RenameAsync(client, 1, "Abcdefghijklm")); // 13
            Assert.Equal(Code(NoName), await RenameAsync(client, 1, "Abcdefghijklmnop")); // 16 code points: normalizePlayerName fails
            Assert.Equal(Code(NoName), await RenameAsync(client, 1, string.Empty));
            Assert.Equal(Code(MixedLanguages), await RenameAsync(client, 1, "Abc1")); // digits are not letters of any script
            await client.SendAsync(WorldOpcode.CmsgCharRename, Request(1, [0xFF, 0xFE, (byte)'a'])); // not UTF-8
            Assert.Equal(Code(NoName), await client.ReadUntilAsync(WorldOpcode.SmsgCharRename));
            await client.SendAsync(WorldOpcode.CmsgCharRename, [1, 2, 3]); // shorter than a guid
            Assert.Equal(Code(NoName), await client.ReadUntilAsync(WorldOpcode.SmsgCharRename));

            Assert.Equal("Valid", (await host.Characters.GetByIdAsync(1))!.Name);
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(1));
        }
    }

    [Fact]
    public async Task AStoreFailure_AnswersCreateError_AndTheSessionLives()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME6", "Stored");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);
            Flags(host).FailRenames = true;
            Assert.Equal(Code(CreateError), await RenameAsync(client, 1, "Newname"));

            Flags(host).FailRenames = false;
            Assert.Equal(0, (await RenameAsync(client, 1, "Newname"))[0]);
        }
    }

    [Fact]
    public async Task TheCharacterListHelper_ReturnsTheFlagsOfTheAccountsCharacters()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME7", "Flaga", "Flagb");
        await using (client)
        {
            await Flags(host).SetFlagAsync(2, CharacterAtLoginFlags.Rename);
            Account account = (await host.Accounts.FindByUsernameAsync("RENAME7"))!;

            IReadOnlyDictionary<int, uint> flags = await Flags(host).GetFlagsAsync(account.Id);

            Assert.Equal(new Dictionary<int, uint> { [2] = CharacterAtLoginFlags.Rename }, flags);
            Assert.Equal(0x4000u, CharacterRenamePackets.CharacterFlagRename);
        }
    }

    [Fact]
    public async Task DecideAsync_ReportsTheOutcome_WithoutThePacketLayer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAME8", "Decide", "Taken");
        await using (client)
        {
            int account = (await host.Accounts.FindByUsernameAsync("RENAME8"))!.Id;
            InMemoryRenameStore store = Flags(host);
            var options = new CharacterCreationOptions();
            await store.SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            CharacterRenameDecision taken = await CharacterRename.DecideAsync(store, options, NameCatalog.Empty, AccountSecurity.Player, account, new CharacterRenameRequest(1, "taken"u8.ToArray()));
            Assert.Equal(CharacterRenameOutcome.NameTaken, taken.Outcome);
            Assert.Equal(CharResult.CharCreateError, taken.Failure);
            Assert.False(taken.Rejected);

            CharacterRenameDecision rejected = await CharacterRename.DecideAsync(store, options, NameCatalog.Empty, AccountSecurity.Player, account, new CharacterRenameRequest(1, "x"u8.ToArray()));
            Assert.Null(rejected.Outcome);
            Assert.Equal(CharResult.CharNameTooShort, rejected.Failure);
            Assert.True(rejected.Rejected);

            CharacterRenameDecision renamed = await CharacterRename.DecideAsync(store, options, NameCatalog.Empty, AccountSecurity.Player, account, new CharacterRenameRequest(1, "elune"u8.ToArray()));
            Assert.Equal(CharacterRenameOutcome.Renamed, renamed.Outcome);
            Assert.Null(renamed.Failure);
            Assert.Equal(("Elune", "Decide"), (renamed.NewName, renamed.OldName));

            // MinPlayerName from the creation options applies to renames too.
            await store.SetFlagAsync(1, CharacterAtLoginFlags.Rename);
            CharacterRenameDecision strict = await CharacterRename.DecideAsync(store, new CharacterCreationOptions { MinPlayerName = 5 }, NameCatalog.Empty, AccountSecurity.Player, account, new CharacterRenameRequest(1, "abcd"u8.ToArray()));
            Assert.Equal(CharResult.CharNameTooShort, strict.Failure);
        }
    }

    /// <summary>
    /// The realm's name catalog as character creation applies it: a NamesProfanity pattern, a NamesReserved pattern and one SQL
    /// <c>reserved_name</c> row.
    /// </summary>
    private static void UseNameCatalog(WorldTestHost host)
        => host.WorldServices.GetRequiredService<NameCatalogFeature>().RestoreCatalog(new NameCatalog(
            [new Regex("^bad", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)],
            [new Regex("^admin", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)],
            [],
            new HashSet<string>(StringComparer.Ordinal) { "staffname" }));

    [Fact]
    public async Task AProfaneOrReservedName_IsRefused_AsAtCreation_AndNothingChanges()
    {
        // vmangos HandleCharRenameOpcode runs ObjectMgr::CheckPlayerName(newname, true) (CharacterHandler.cpp:774), which ends in
        // ValidateName over NamesProfanity/NamesReserved (ObjectMgr.cpp:9597, DBCStores.cpp:617-628), then refuses a player the
        // reserved_name list (CharacterHandler.cpp:784), exactly as HandleCharCreateOpcode does (:257, :264).
        await using WorldTestHost host = WorldTestHost.Start();
        UseNameCatalog(host);
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAMEC1", "Keepname");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            Assert.Equal(Code(Profane), await RenameAsync(client, 1, "Badword"));
            Assert.Equal(Code(Reserved), await RenameAsync(client, 1, "Adminx"));
            Assert.Equal(Code(Reserved), await RenameAsync(client, 1, "staffname"));
            Assert.Equal("Keepname", (await host.Characters.GetByIdAsync(1))!.Name);
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(1)); // the flag stays
            Assert.Equal("Keepname", host.Directory.Find(1)!.Name);

            // The same names are refused at creation with the same codes.
            Assert.Equal(Profane, await client.TryCreateCharacterAsync("Badword"));
            Assert.Equal(Reserved, await client.TryCreateCharacterAsync("Staffname"));

            // An allowed name still renames.
            Assert.Equal(0, (await RenameAsync(client, 1, "Goodname"))[0]);
            Assert.Equal("Goodname", (await host.Characters.GetByIdAsync(1))!.Name);
        }
    }

    [Fact]
    public async Task Staff_MayTakeASqlReservedName_ButNotADbcProfaneOrReservedOne()
    {
        // vmangos refuses reserved_name to SEC_PLAYER only (CharacterHandler.cpp:784); ValidateName's DBC rules apply to everyone.
        await using WorldTestHost host = WorldTestHost.Start();
        UseNameCatalog(host);
        byte[] key = await host.AddAccountAsync("RENAMEC2", AccountSecurity.GameMaster);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("RENAMEC2", key);
        await client.CreateCharacterAsync("Staffer");
        await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

        Assert.Equal(Code(Profane), await RenameAsync(client, 1, "Badword"));
        Assert.Equal(Code(Reserved), await RenameAsync(client, 1, "Adminx"));
        Assert.Equal(0, (await RenameAsync(client, 1, "staffname"))[0]);
        Assert.Equal("Staffname", (await host.Characters.GetByIdAsync(1))!.Name);
    }

    // --- login gate (vmangos Player::LoadFromDB, Player.cpp:14612-14626) ---------------------------

    private const byte LoginFailedCode = 0x41; // CHAR_LOGIN_FAILED

    private static async Task<byte[]> TryLoginAsync(WorldTestClient client, ulong guid)
    {
        var login = new PacketWriter(8);
        login.WriteUInt64(guid);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        return await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
    }

    private static async Task<uint> EnumFlagsAsync(WorldTestClient client, ulong guid)
    {
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum));
        int count = reader.ReadByte();
        for (int i = 0; i < count; i++)
        {
            ulong id = reader.ReadUInt64();
            reader.ReadCString();
            for (int b = 0; b < 9; b++)
            {
                reader.ReadByte();
            }

            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadUInt32();
            uint flags = reader.ReadUInt32();
            if (id == guid)
            {
                return flags;
            }

            reader.ReadByte();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            for (int slot = 0; slot < 20; slot++)
            {
                reader.ReadUInt32();
                reader.ReadByte();
            }
        }

        throw new InvalidOperationException("character not listed");
    }

    [Fact]
    public async Task AFlaggedCharacter_CannotLogIn_AndNeverEntersTheWorld()
    {
        // vmangos LoadFromDB returns false for CHARACTER_FLAG_RENAME (Player.cpp:14612-14618) and HandlePlayerLogin kicks (CharacterHandler.cpp:485-487);
        // here the client gets SMSG_CHARACTER_LOGIN_FAILED (CHAR_LOGIN_FAILED) and stays on the character screen.
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAMEL1", "Flagged");
        await using (client)
        {
            await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

            Assert.Equal(Code(LoginFailedCode), await TryLoginAsync(client, 1));
            Assert.Equal(0, host.World.OnlinePlayerCount);
            Assert.Equal(0x4000u, await EnumFlagsAsync(client, 1));
        }
    }

    [Fact]
    public async Task AStoredNameThatFailsTheRules_IsFlagged_RefusedAtLogin_AndLogsInAfterTheRename()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAMEL2", "Okname");
        await using (client)
        {
            (await host.Characters.GetByIdAsync(1))!.Name = "Abc1"; // a digit written straight to the store

            Assert.Equal(Code(LoginFailedCode), await TryLoginAsync(client, 1));
            Assert.Equal(0, host.World.OnlinePlayerCount);
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(1));
            Assert.Equal(0x4000u, await EnumFlagsAsync(client, 1));

            Assert.Equal(0, (await RenameAsync(client, 1, "Fixedname"))[0]);
            await client.LoginAsync(1);
            Assert.Equal(1, host.World.OnlinePlayerCount);
        }
    }

    [Fact]
    public async Task AReservedStoredName_RefusesAPlayer_ButStaffMayLogInOnASqlReservedName()
    {
        // vmangos Player.cpp:14620-14621: CheckPlayerName fails for everyone, IsReservedName only for SEC_PLAYER.
        await using WorldTestHost host = WorldTestHost.Start();
        UseNameCatalog(host);
        (WorldTestClient player, _) = await AccountWithAsync(host, "RENAMEL3", "Plain");
        byte[] key = await host.AddAccountAsync("RENAMEL4", AccountSecurity.GameMaster);
        await using WorldTestClient gm = await host.ConnectAsync();
        await gm.AuthenticateAsync("RENAMEL4", key);
        await gm.CreateCharacterAsync("Staffer");
        await using (player)
        {
            (await host.Characters.GetByIdAsync(1))!.Name = "Staffname";

            Assert.Equal(Code(LoginFailedCode), await TryLoginAsync(player, 1));
            Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(1));

            (await host.Characters.GetByIdAsync(2))!.Name = "Staffname"; // reserved_name only
            await gm.LoginAsync(2); // reserved_name only: staff may play it
            Assert.Equal(0u, Flags(host).FlagsOf(2));
        }

        // A DBC-reserved name is refused for staff too.
        await using WorldTestClient gm2 = await host.ConnectAsync();
        byte[] key2 = await host.AddAccountAsync("RENAMEL5", AccountSecurity.GameMaster);
        await gm2.AuthenticateAsync("RENAMEL5", key2);
        await gm2.CreateCharacterAsync("Staffthree");
        (await host.Characters.GetByIdAsync(3))!.Name = "Adminx";
        Assert.Equal(Code(LoginFailedCode), await TryLoginAsync(gm2, 3));
        Assert.Equal(CharacterAtLoginFlags.Rename, Flags(host).FlagsOf(3));
    }

    [Fact]
    public async Task AFailingFlagRead_IsLogged_AndTheLoginGoesOn()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await AccountWithAsync(host, "RENAMEL6", "Outage");
        await using (client)
        {
            Flags(host).FailFlagReads = true;
            await client.LoginAsync(1);
            Assert.Equal(1, host.World.OnlinePlayerCount);
        }
    }

    // --- .character rename ---------------------------------------------------------------

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    private static async Task<string> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public void TheCommand_NeedsAGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("character rename", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("character rename", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("character", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task GmRename_FlagsAnOnlinePlayer_ByNameOrSelection_AndNobodyElse()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CRGM", "Crgm", AccountSecurity.Administrator);
        await using WorldTestClient target = await host.EnterWorldAsync("CRTARGET", "Crtarget");
        await gm.CollectAsync();
        await target.CollectAsync();

        // By name.
        Assert.Equal($"Forced rename for player {Link("Crtarget")} will be requested at next login.", await RunAsync(gm, ".character rename crtarget"));
        await WorldTestHost.WaitForAsync(() => Flags(host).FlagsOf(2) == CharacterAtLoginFlags.Rename, "the flag to be stored");
        Assert.Equal(0u, Flags(host).FlagsOf(1));

        // By selection, and the caller itself when nothing is selected.
        Player selected = await host.PlayerAsync("Crtarget");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Crgm")!.Selection = default);
        Assert.Equal($"Forced rename for player {Link("Crgm")} will be requested at next login.", await RunAsync(gm, ".character rename"));
        await WorldTestHost.WaitForAsync(() => Flags(host).FlagsOf(1) == CharacterAtLoginFlags.Rename, "the flag to be stored");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Crgm")!.Selection = selected.Guid);
        Assert.Equal($"Forced rename for player {Link("Crtarget")} will be requested at next login.", await RunAsync(gm, ".character rename"));

        // Nobody of that name.
        Assert.Equal("Player not found!", await RunAsync(gm, ".character rename nosuchplayer"));
    }

    [Fact]
    public async Task GmRename_FlagsAnOfflineCharacter_ThroughTheDirectory()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient sleeper, _) = await AccountWithAsync(host, "CRSLEEP", "Crsleeper");
        await using (sleeper)
        await using (WorldTestClient gm = await host.EnterWorldAsync("CRGM2", "Crgmtwo", AccountSecurity.GameMaster))
        {
            await gm.CollectAsync();
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Crgmtwo")!.Selection = default);

            Assert.Equal($"Forced rename for player {Link("Crsleeper")} (GUID #1) will be requested at next login.", await RunAsync(gm, ".character rename Crsleeper"));

            // The offline path answers first and stores the flag afterwards, off the world thread.
            await WorldTestHost.WaitForAsync(() => Flags(host).FlagsOf(1) == CharacterAtLoginFlags.Rename, "the flag to be stored");
        }
    }

    [Fact]
    public async Task GmRename_RefusesATargetWhoseAccountOutranksTheCaller_OnlineAndOffline()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient boss = await host.EnterWorldAsync("CRBOSS", "Crboss", AccountSecurity.Administrator);
        byte[] key = await host.AddAccountAsync("CROFFBOSS", AccountSecurity.Administrator);
        await using (WorldTestClient offlineBoss = await host.ConnectAsync())
        {
            await offlineBoss.AuthenticateAsync("CROFFBOSS", key);
            await offlineBoss.CreateCharacterAsync("Croffboss");
        }

        await using WorldTestClient gm = await host.EnterWorldAsync("CRGM3", "Crgmthree", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Crgmthree")!.Selection = default);

        Assert.Equal("You have low security level for this.", await RunAsync(gm, ".character rename Crboss"));
        Assert.Equal("You have low security level for this.", await RunAsync(gm, ".character rename Croffboss"));
        Assert.Equal(0u, Flags(host).FlagsOf(1));
        Assert.Equal(0u, Flags(host).FlagsOf(2));
    }
}
