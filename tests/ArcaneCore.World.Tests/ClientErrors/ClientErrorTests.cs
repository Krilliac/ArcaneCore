using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.ClientErrors;

/// <summary>The <see cref="ClientError"/> packets, its rate limit and what it keeps out of the packet (docs/areas/client-errors.md).</summary>
public sealed class ClientErrorTests
{
    private sealed class RecordingSession : IPlayerSession
    {
        public List<(WorldOpcode Opcode, byte[] Payload)> Sent { get; } = [];

        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Sent.Add((opcode, payload.ToArray()));

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    [Fact]
    public void SpellCast_SendsCastResultFailedWithResult0x1C()
    {
        var session = new RecordingSession();

        ClientErrorOutcome outcome = ClientError.SpellCast(session, 18960, "test: spell");

        Assert.True(outcome.Sent);
        (WorldOpcode opcode, byte[] payload) = Assert.Single(session.Sent);
        Assert.Equal(WorldOpcode.SmsgCastResult, opcode);
        // u32 spell, u8 status 2 (failed), u8 result 0x1C SPELL_FAILED_ERROR, nothing else.
        Assert.Equal(new byte[] { 0x10, 0x4A, 0x00, 0x00, 0x02, 0x1C }, payload);
    }

    [Fact]
    public void ItemMove_SendsInventoryChangeFailure40_WithTheItemGuids()
    {
        var session = new RecordingSession();

        ClientError.ItemMove(session, new ObjectGuid(0x4000000000000011), new ObjectGuid(0x4000000000000022), "test: item");

        (WorldOpcode opcode, byte[] payload) = Assert.Single(session.Sent);
        Assert.Equal(WorldOpcode.SmsgInventoryChangeFailure, opcode);
        Assert.Equal(1 + 8 + 8 + 1, payload.Length); // no required-level field: that is only for result 1
        Assert.Equal(40, payload[0]); // ERR_INTERNAL_BAG_ERROR
        Assert.Equal(0x4000000000000011ul, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1)));
        Assert.Equal(0x4000000000000022ul, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(9)));
        Assert.Equal(0, payload[17]);
    }

    [Theory]
    [InlineData(ClientErrorKind.CharacterCreate, WorldOpcode.SmsgCharCreate, 0x2F)]
    [InlineData(ClientErrorKind.CharacterDelete, WorldOpcode.SmsgCharDelete, 0x3A)]
    [InlineData(ClientErrorKind.CharacterLogin, WorldOpcode.SmsgCharacterLoginFailed, 0x41)]
    public void CharacterScreen_SendsTheResponseCode(ClientErrorKind kind, WorldOpcode expectedOpcode, byte code)
    {
        ClientErrorContext context = kind switch
        {
            ClientErrorKind.CharacterCreate => ClientErrorContext.CharacterCreate,
            ClientErrorKind.CharacterDelete => ClientErrorContext.CharacterDelete,
            _ => ClientErrorContext.CharacterLogin,
        };

        (WorldOpcode opcode, byte[] payload) = ClientError.Build(context);

        Assert.Equal(expectedOpcode, opcode);
        Assert.Equal(new[] { code }, payload);
    }

    [Fact]
    public void Mail_SendsResult6()
    {
        (WorldOpcode opcode, byte[] payload) = ClientError.Build(ClientErrorContext.Mail(77, MailAction.Send));

        Assert.Equal(WorldOpcode.SmsgSendMailResult, opcode);
        Assert.Equal(new byte[] { 77, 0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0 }, payload);
    }

    [Fact]
    public void Generic_SendsTheFixedNotification()
    {
        (WorldOpcode opcode, byte[] payload) = ClientError.Build(ClientErrorContext.Generic);

        Assert.Equal(WorldOpcode.SmsgNotification, opcode);
        Assert.Equal(Encoding.ASCII.GetBytes("Internal error\0"), payload);
    }

    [Fact]
    public void NoCauseOrExceptionText_ReachesThePacket()
    {
        const string Secret = "SECRET-db-password-and-stack";
        foreach (ClientErrorContext context in new[]
        {
            ClientErrorContext.Generic, ClientErrorContext.SpellCast(1), ClientErrorContext.ItemMove(), ClientErrorContext.CharacterCreate,
            ClientErrorContext.CharacterDelete, ClientErrorContext.CharacterLogin, ClientErrorContext.Mail(1, MailAction.Send),
        })
        {
            var session = new RecordingSession();
            ClientError.Internal(session, context, "cause " + Secret, logger: null, new InvalidOperationException(Secret), nowMs: 0);

            (WorldOpcode _, byte[] payload) = Assert.Single(session.Sent);
            Assert.Equal(ClientError.Build(context).Payload, payload); // the packet depends on the context alone
            Assert.DoesNotContain("SECRET", Encoding.Latin1.GetString(payload), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnsolicitedErrors_AreRateLimitedPerSession_ButAlwaysRecorded()
    {
        var session = new RecordingSession();
        var other = new RecordingSession();
        var probe = new RateProbeException();

        for (int i = 0; i < 10; i++)
        {
            ClientError.Internal(session, ClientErrorContext.SpellCast(1), "rate", logger: null, probe, nowMs: 1_000 + i);
        }

        ClientError.Internal(other, ClientErrorContext.SpellCast(1), "rate", logger: null, probe, nowMs: 1_000);

        Assert.Equal(ClientError.Burst, session.Sent.Count);
        Assert.Single(other.Sent); // the budget is per session

        // After the window the session may be told again.
        ClientError.Internal(session, ClientErrorContext.SpellCast(1), "rate", logger: null, probe, nowMs: 1_000 + ClientError.WindowMs);
        Assert.Equal(ClientError.Burst + 1, session.Sent.Count);

        FaultSnapshot fault = Assert.Single(FaultLedger.Shared.Snapshot(), s => s.ExceptionType == typeof(RateProbeException).FullName);
        Assert.Equal(12, fault.Count);
        Assert.Equal("client-error/spell", fault.Site);
    }

    [Fact]
    public void AnswerContexts_AreNeverRateLimited()
    {
        var session = new RecordingSession();
        for (int i = 0; i < 10; i++)
        {
            ClientError.Internal(session, ClientErrorContext.CharacterCreate, "answer", logger: null, exception: null, nowMs: 5);
        }

        Assert.Equal(10, session.Sent.Count);
    }

    [Fact]
    public void ARejectionWithoutAnException_IsStillRecordedWithItsCause()
    {
        var session = new RecordingSession();
        string cause = "rejection probe " + Guid.NewGuid().ToString("N");

        ClientErrorOutcome outcome = ClientError.Generic(session, cause);

        FaultSnapshot fault = Assert.IsType<FaultSnapshot>(FaultLedger.Shared.Find(outcome.FaultId));
        Assert.Equal(typeof(ServerRejectionException).FullName, fault.ExceptionType);
        // A rejection has no thrown stack, so the site names the rejecting member: two callers are two ledger entries.
        Assert.Equal($"client-error/generic/{nameof(ClientErrorTests)}.{nameof(ARejectionWithoutAnException_IsStillRecordedWithItsCause)}", fault.Site);
        Assert.Equal(cause, fault.SampleMessage);
    }

    [Fact]
    public void RateLimiter_IsASlidingWindow()
    {
        var limiter = new ClientErrorRateLimiter(burst: 2, windowMs: 100);

        Assert.True(limiter.TryAcquire(0));
        Assert.True(limiter.TryAcquire(10));
        Assert.False(limiter.TryAcquire(99));
        Assert.True(limiter.TryAcquire(100)); // the stamp at 0 left the window
        Assert.False(limiter.TryAcquire(109));
        Assert.True(limiter.TryAcquire(110));
    }

    private sealed class RateProbeException : Exception;
}
