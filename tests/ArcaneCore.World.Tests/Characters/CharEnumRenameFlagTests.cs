using System.Text;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Game.Characters;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Rename;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Tests.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// The character list carries CHARACTER_FLAG_RENAME (0x4000) for a character with the at-login rename flag (mangos
/// Player::BuildEnumData), so a stock client is prompted; without it the rename flow is unreachable
/// (remediation of docs/areas/character-rename.md's known gap).
/// </summary>
public sealed class CharEnumRenameFlagTests
{
    private const uint FlagRename = 0x4000;

    /// <summary>The flags word of every listed character, by guid, read with the layout the list is written in.</summary>
    private static Dictionary<ulong, uint> FlagsByGuid(byte[] list)
    {
        var reader = new PacketReader(list);
        int count = reader.ReadByte();
        var flags = new Dictionary<ulong, uint>(count);
        for (int i = 0; i < count; i++)
        {
            ulong guid = reader.ReadUInt64();
            reader.ReadCString();
            for (int b = 0; b < 9; b++)
            {
                reader.ReadByte(); // race, class, gender, skin, face, hair style, hair color, facial hair, level
            }

            reader.ReadUInt32(); // zone
            reader.ReadUInt32(); // map
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadUInt32(); // guild id
            flags[guid] = reader.ReadUInt32();
            reader.ReadByte(); // first login
            reader.ReadUInt32(); // pet display id
            reader.ReadUInt32(); // pet level
            reader.ReadUInt32(); // pet family
            for (int slot = 0; slot < 20; slot++)
            {
                reader.ReadUInt32();
                reader.ReadByte();
            }
        }

        Assert.Equal(0, reader.Remaining);
        return flags;
    }

    private static async Task<Dictionary<ulong, uint>> ListAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        return FlagsByGuid(await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum));
    }

    private static InMemoryRenameStore Flags(WorldTestHost host) => host.WorldServices.GetRequiredService<InMemoryRenameStore>();

    private static async Task<WorldTestClient> AccountWithAsync(WorldTestHost host, string account, params string[] characters)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        foreach (string name in characters)
        {
            await client.CreateCharacterAsync(name);
        }

        return client;
    }

    private static byte[] RenameRequest(ulong guid, string name)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(guid);
        writer.WriteBytes(Encoding.UTF8.GetBytes(name));
        writer.WriteByte(0);
        return writer.ToArray();
    }

    [Fact]
    public void BuildCharEnum_WritesTheFlagsWord_ForTheFlaggedCharacterOnly()
    {
        CharacterRecord[] characters =
        [
            new() { Id = 7, Name = "Plain", Race = 1, Class = 1 },
            new() { Id = 8, Name = "Flagged", Race = 1, Class = 1 },
        ];

        byte[] without = CharacterPackets.BuildCharEnum(characters);
        byte[] with = CharacterPackets.BuildCharEnum(characters, equipment: null, characterFlags: new Dictionary<int, uint> { [8] = FlagRename });

        Assert.Equal(without.Length, with.Length); // the word is already in the layout; only its value changes
        Assert.Equal(new Dictionary<ulong, uint> { [7] = 0, [8] = 0 }, FlagsByGuid(without));
        Assert.Equal(new Dictionary<ulong, uint> { [7] = 0, [8] = FlagRename }, FlagsByGuid(with));
    }

    [Fact]
    public void CharEnumFlags_MapsTheAtLoginRenameBit_AndNothingElse()
    {
        Assert.Equal(0u, CharacterRename.CharEnumFlags(0));
        Assert.Equal(CharacterRenamePackets.CharacterFlagRename, CharacterRename.CharEnumFlags(CharacterAtLoginFlags.Rename));
        Assert.Equal(FlagRename, CharacterRename.CharEnumFlags(CharacterAtLoginFlags.Rename | 0x80));
        Assert.Equal(0u, CharacterRename.CharEnumFlags(0x02)); // another at-login bit has no character flag here
    }

    [Fact]
    public async Task TheCharacterList_ShowsTheRenameFlag_UntilTheRenameHappens()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await AccountWithAsync(host, "ENUMFLAG1", "Plainone", "Flagme");

        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0, [2] = 0 }, await ListAsync(client));

        await Flags(host).SetFlagAsync(2, CharacterAtLoginFlags.Rename);
        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0, [2] = FlagRename }, await ListAsync(client));

        // The prompted rename clears the flag, and the next list no longer prompts.
        await client.SendAsync(WorldOpcode.CmsgCharRename, RenameRequest(2, "Renamed"));
        Assert.Equal(0, (await client.ReadUntilAsync(WorldOpcode.SmsgCharRename))[0]);
        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0, [2] = 0 }, await ListAsync(client));
        Assert.Equal("Renamed", (await host.Characters.GetByIdAsync(2))!.Name);
    }

    [Fact]
    public async Task TheFlag_IsTheAccountsOwn_NotAnotherAccounts()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient mine = await AccountWithAsync(host, "ENUMFLAG2", "Mineplain");
        await using WorldTestClient other = await AccountWithAsync(host, "ENUMFLAG3", "Theirsflag");

        await Flags(host).SetFlagAsync(2, CharacterAtLoginFlags.Rename);

        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0 }, await ListAsync(mine));
        Assert.Equal(new Dictionary<ulong, uint> { [2] = FlagRename }, await ListAsync(other));
    }

    [Fact]
    public async Task GmRename_OfAnOfflineCharacter_PromptsItsOwnerAtTheCharacterScreen()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient sleeper = await AccountWithAsync(host, "ENUMSLEEP", "Enumsleeper");
        await using WorldTestClient gm = await host.EnterWorldAsync("ENUMGM", "Enumgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Enumgm")!.Selection = default);

        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0 }, await ListAsync(sleeper));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".character rename Enumsleeper");
        Assert.StartsWith("Forced rename for player ", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        await WorldTestHost.WaitForAsync(() => Flags(host).FlagsOf(1) == CharacterAtLoginFlags.Rename, "the flag to be stored");

        Assert.Equal(new Dictionary<ulong, uint> { [1] = FlagRename }, await ListAsync(sleeper));
    }

    [Fact]
    public async Task AFlagReadFailure_StillSendsTheList_WithoutFlags()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await AccountWithAsync(host, "ENUMFLAG4", "Outage");
        await Flags(host).SetFlagAsync(1, CharacterAtLoginFlags.Rename);

        Flags(host).FailFlagReads = true;
        Assert.Equal(new Dictionary<ulong, uint> { [1] = 0 }, await ListAsync(client));

        Flags(host).FailFlagReads = false;
        Assert.Equal(new Dictionary<ulong, uint> { [1] = FlagRename }, await ListAsync(client));
    }
}
