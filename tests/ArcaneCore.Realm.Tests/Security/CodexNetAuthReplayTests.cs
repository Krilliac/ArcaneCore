using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Protocol;
using System.Net.Sockets;
using Xunit;

using static ArcaneCore.Realm.Tests.Security.CodexNetAuthRealmTests;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Codex finding 3 (net/auth), replayed exactly as the report words it: "Complete a challenge and
/// 74-byte CMD_AUTH_LOGON_PROOF on socket A, obtaining K1. Complete a separate logon on socket B so
/// the account key becomes K2. On still-open socket A, resend command 0x01 plus the original 74-byte
/// proof body" and the report's impact claim that the old key is then restored. The SRP client helpers
/// are shared with CodexNetAuthRealmTests.
/// </summary>
public sealed class CodexNetAuthReplayTests
{

    [Fact]
    public async Task CompletedProofReplayedOnTheStillOpenFirstSocket_DoesNotRestoreTheOldKeyOrAuthenticate()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = "ALICE", Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, "ALICE", "ALICEPW"), 32),
        });

        await using NetworkStream socketA = StartSession(accounts, new AuthOptions());
        await using NetworkStream socketB = StartSession(accounts, new AuthOptions());

        // Socket A: challenge + the 74-byte proof, obtaining K1.
        await socketA.WriteAsync(Challenge("ALICE"));
        (byte aResult, byte[] serverB1, byte[] salt1) = await ReadChallenge(socketA);
        Assert.Equal((byte)AuthResult.Success, aResult);
        byte[] proofA = ProofFor("ALICE", "ALICE", "ALICEPW", salt1, serverB1);
        Assert.Equal(1 + 74, proofA.Length);
        await socketA.WriteAsync(proofA);
        byte[] okA = await ReadN(socketA, 26);
        Assert.Equal((byte)AuthResult.Success, okA[1]);
        byte[] k1 = (await accounts.FindByUsernameAsync("ALICE"))!.SessionKey!.ToArray();

        // Socket B: a separate logon rotates the stored key to K2.
        await socketB.WriteAsync(Challenge("ALICE"));
        (byte bResult, byte[] serverB2, byte[] salt2) = await ReadChallenge(socketB);
        Assert.Equal((byte)AuthResult.Success, bResult);
        await socketB.WriteAsync(ProofFor("ALICE", "ALICE", "ALICEPW", salt2, serverB2));
        byte[] okB = await ReadN(socketB, 26);
        Assert.Equal((byte)AuthResult.Success, okB[1]);
        byte[] k2 = (await accounts.FindByUsernameAsync("ALICE"))!.SessionKey!.ToArray();
        Assert.NotEqual(k1, k2);

        // Back on the still-open socket A: resend command 0x01 plus the original proof body.
        await socketA.WriteAsync(proofA);
        byte[] replyToReplay = await ReadN(socketA, 2);
        Assert.Equal((byte)AuthCommand.LogonProof, replyToReplay[0]);
        Assert.NotEqual((byte)AuthResult.Success, replyToReplay[1]);
        await DrainAsync(socketA);

        byte[] after = (await accounts.FindByUsernameAsync("ALICE"))!.SessionKey!.ToArray();
        Assert.Equal(k2, after);          // K1 was not written back
        Assert.NotEqual(k1, after);

        // The replay did not authenticate the connection either: it is not served the realm list.
        await socketA.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 });
        Assert.False(await ReceivesDataAsync(socketA, TimeSpan.FromSeconds(1.5)), "a replayed proof must not unlock the realm list");
        Assert.Equal(k2, (await accounts.FindByUsernameAsync("ALICE"))!.SessionKey!.ToArray());
    }
}
