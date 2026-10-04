using System.Text;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// A character flagged for a rename is listed with CHARACTER_FLAG_RENAME (0x4000) in SMSG_CHAR_ENUM, read by the mock client's own
/// parser, so a stock client is prompted; the rename it answers with clears the flag from the next list. Real SQLite/EF
/// (<c>character_at_login</c> through the real store), the real world and a loopback client.
/// </summary>
public sealed class CharacterRenameEnumFlagTests
{
    private const string Account = "RENAMEFLAG";
    private const string Password = "PASSWORD";
    private const uint FlagRename = 0x4000;

    [Fact]
    public async Task TheList_CarriesTheRenameFlag_ForTheFlaggedCharacter_UntilItIsRenamed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await server.AddAccountAsync(Account, Password, token);
        await using WorldClient client = await AuthenticateAsync(server, token);
        var connection = new ScenarioConnection(client);
        await connection.CreateCharacterAsync("Plain", token);
        await connection.CreateCharacterAsync("Flagged", token);

        IReadOnlyList<MockCharacter> before = await connection.EnumerateAsync(token);
        Assert.All(before, c => Assert.Equal(0u, c.Flags));
        ulong flagged = before.Single(c => c.Name == "Flagged").Guid;
        ulong plain = before.Single(c => c.Name == "Plain").Guid;

        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ICharacterRenameStore>()
                .SetFlagAsync((int)flagged, CharacterAtLoginFlags.Rename, token));
        }

        IReadOnlyList<MockCharacter> prompted = await connection.EnumerateAsync(token);
        Assert.Equal(FlagRename, prompted.Single(c => c.Guid == flagged).Flags);
        Assert.Equal(0u, prompted.Single(c => c.Guid == plain).Flags);

        // The rename the prompt leads to.
        await connection.SendAsync(WorldOpcode.CmsgCharRename, RenameRequest(flagged, "Renamed"), token);
        byte[] answer = await connection.ReadUntilAsync(WorldOpcode.SmsgCharRename, token);
        Assert.Equal(0, answer[0]); // RESPONSE_SUCCESS

        IReadOnlyList<MockCharacter> after = await connection.EnumerateAsync(token);
        MockCharacter renamed = after.Single(c => c.Guid == flagged);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(0u, renamed.Flags);
    }

    private static byte[] RenameRequest(ulong guid, string name)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(guid);
        writer.WriteBytes(Encoding.UTF8.GetBytes(name));
        writer.WriteByte(0);
        return writer.ToArray();
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, Account, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        Assert.Equal((byte)0x0C, await client.AuthenticateAsync(Account, logon.SessionKey, token));
        return client;
    }
}
