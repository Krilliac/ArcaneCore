using System.Buffers.Binary;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// S5c: the gateway side of a cross-worker transfer. The control frames are pinned with golden bytes written out by hand from the layout in
/// <see cref="GatewayTransferWire"/>; the router is then driven against in-memory sessions and links: packets sent during the freeze reach the target in
/// order after the commit, a cancel resumes the source, stale generations are dropped and a buffer overflow aborts cleanly.
/// </summary>
public sealed class GatewayTransferRouterTests
{
    private static readonly Guid TransferId = new(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), bigEndian: false);
    private static readonly Guid Incarnation = new(Convert.FromHexString("101112131415161718191A1B1C1D1E1F"), bigEndian: false);

    private const string GoldenStage = "18" + "0102030405060708090A0B0C0D0E0F10" + "0500000000000000" + "0700000000000000"
        + "02000000" + "656B" + "03000000" + "6B616C" + "01000000" + "00000000" + "0000803F" + "00000040" + "0000003F" + "000080BF";
    private const string GoldenCancel = "19" + "0102030405060708090A0B0C0D0E0F10" + "0500000000000000" + "0700000000000000" + "02000000" + "656B" + "01000000" + "78";
    private const string GoldenCommit = "1A" + "0102030405060708090A0B0C0D0E0F10" + "0500000000000000" + "0700000000000000"
        + "03000000" + "6B616C" + "02000000" + "6335" + "0900000000000000" + "101112131415161718191A1B1C1D1E1F";
    private const string GoldenAck = "1B" + "0102030405060708090A0B0C0D0E0F10" + "03" + "01" + "00000000";

    // ---- wire ----

    [Fact]
    public void Stage_MatchesTheGoldenBytes_AndRoundTrips()
    {
        var stage = new GatewayTransferStage(TransferId, 5, 7, "ek", "kal", 1, 0, new TransferPosition(1f, 2f, 0.5f, -1f));
        byte[] frame = GatewayTransferWire.Encode(stage);
        Assert.Equal(GoldenStage, Convert.ToHexString(frame));
        Assert.Equal((byte)GatewayWorkerFrameType.TransferStage, frame[0]);
        Assert.Equal(stage, GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.TransferStage, frame.AsSpan(1)));
    }

    [Fact]
    public void Cancel_MatchesTheGoldenBytes_AndRoundTrips()
    {
        var cancel = new GatewayTransferCancel(TransferId, 5, 7, "ek", "x");
        byte[] frame = GatewayTransferWire.Encode(cancel);
        Assert.Equal(GoldenCancel, Convert.ToHexString(frame));
        Assert.Equal(cancel, GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.TransferCancel, frame.AsSpan(1)));
    }

    [Fact]
    public void Commit_MatchesTheGoldenBytes_AndRoundTrips()
    {
        var commit = new GatewayTransferCommit(TransferId, 5, 7, "kal", "c5", 9, Incarnation);
        byte[] frame = GatewayTransferWire.Encode(commit);
        Assert.Equal(GoldenCommit, Convert.ToHexString(frame));
        Assert.Equal(commit, GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.TransferCommit, frame.AsSpan(1)));
    }

    [Fact]
    public void Ack_MatchesTheGoldenBytes_AndRoundTrips()
    {
        var ack = new GatewayTransferAck(TransferId, GatewayTransferPhase.Commit, true, string.Empty);
        byte[] frame = GatewayTransferWire.EncodeAck(ack);
        Assert.Equal(GoldenAck, Convert.ToHexString(frame));
        Assert.Equal(ack, GatewayTransferWire.DecodeAck(frame.AsSpan(1)));
    }

    [Fact]
    public void Decoders_RefuseTruncatedFramesTrailingBytesAndBadPhases()
    {
        byte[] stage = GatewayTransferWire.Encode(new GatewayTransferStage(TransferId, 5, 7, "ek", "kal", 1, 0, default));
        Assert.Throws<InvalidDataException>(() => GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.TransferStage, stage.AsSpan(1, stage.Length - 2)));
        Assert.Throws<InvalidDataException>(() => GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.TransferStage, [.. stage.AsSpan(1), (byte)0]));
        Assert.Throws<InvalidDataException>(() => GatewayTransferWire.DecodeControl(GatewayWorkerFrameType.SessionOpen, stage.AsSpan(1)));

        byte[] ack = GatewayTransferWire.EncodeAck(new GatewayTransferAck(TransferId, GatewayTransferPhase.Stage, true, string.Empty));
        ack[17] = 9; // phase byte
        Assert.Throws<InvalidDataException>(() => GatewayTransferWire.DecodeAck(ack.AsSpan(1)));
    }

    [Fact]
    public void Encode_RefusesNodeIdsOverTheTextLimit()
    {
        string tooLong = new('n', GatewaySessionWire.MaxTextBytes + 1);
        Assert.Throws<ArgumentException>(() => GatewayTransferWire.Encode(new GatewayTransferStage(TransferId, 5, 7, tooLong, "kal", 1, 0, default)));
    }

    // ---- router ----

    private static readonly CharacterOwnershipToken Token = new("character:5", 9, Incarnation);

    private sealed class FakeLink : IGatewayTransferLink
    {
        public List<(long Generation, WorldOpcode Opcode, byte[] Payload, uint ReceivedMs)> Sent { get; } = [];
        public List<(long Generation, ClusterSessionDetachReason Reason)> Closed { get; } = [];
        public bool Down { get; set; }

        public bool SendClientPacket(long generation, WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs)
        {
            if (Down)
            {
                return false;
            }

            Sent.Add((generation, opcode, payload.ToArray(), receivedMs));
            return true;
        }

        public void CloseSession(long generation, ClusterSessionDetachReason reason) => Closed.Add((generation, reason));
    }

    private sealed class FakeSession(long generation) : IGatewayTransferSession
    {
        public long SessionGeneration { get; private set; } = generation;
        public bool IsOpen { get; set; } = true;
        public List<(WorldOpcode Opcode, byte[] Payload)> ToClient { get; } = [];
        public List<(WorldOpcode Opcode, byte[] Payload, uint ReceivedMs)> ToSource { get; } = [];
        public GatewayTransferTarget? Repointed { get; private set; }
        public string? Kicked { get; private set; }
        public Action? OnRepoint { get; set; }
        public Action? OnForwardToWorker { get; set; }

        public void SendToClient(WorldOpcode opcode, ReadOnlySpan<byte> payload) => ToClient.Add((opcode, payload.ToArray()));

        public bool ForwardToWorker(WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs)
        {
            OnForwardToWorker?.Invoke();
            ToSource.Add((opcode, payload.ToArray(), receivedMs));
            return true;
        }

        public void Repoint(GatewayTransferTarget target)
        {
            OnRepoint?.Invoke();
            Repointed = target;
            SessionGeneration = target.Generation;
        }

        public void Kick(string reason) => Kicked = reason;
    }

    private sealed class FakeEnvironment : IGatewayTransferEnvironment
    {
        public Dictionary<long, FakeSession> Sessions { get; } = [];
        public FakeLink TargetLink { get; } = new();
        public bool RefuseTarget { get; set; }

        public IGatewayTransferSession? FindSession(long sessionGeneration) => Sessions.GetValueOrDefault(sessionGeneration);

        public Task<GatewayTransferTarget?> OpenTargetSessionAsync(IGatewayTransferSession session, string targetNode, CancellationToken cancellationToken) =>
            Task.FromResult<GatewayTransferTarget?>(RefuseTarget ? null : new GatewayTransferTarget(TargetLink, 70, 4));
    }

    private static (GatewayTransferRouter Router, FakeEnvironment Env, FakeSession Session) Setup(int maxPackets = GatewayTransferRouter.DefaultMaxBufferedPackets, int maxBytes = GatewayTransferRouter.DefaultMaxBufferedBytes)
    {
        var env = new FakeEnvironment();
        var session = new FakeSession(7);
        env.Sessions[7] = session;
        return (new GatewayTransferRouter(env, maxPackets, maxBytes), env, session);
    }

    private static Task Stage(GatewayTransferRouter router, uint map = 30) =>
        router.StageWorldPortAsync(TransferId, 5, 7, "ek", "kal", map, 0, new TransferPosition(1, 2, 3, 0));

    private static byte[] Bytes(params byte[] bytes) => bytes;

    [Fact]
    public async Task Stage_SendsTransferPending_AndHoldsPacketsUntilCommit_ThenTheTargetGetsThemInOrder()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        await Stage(router, map: 30);

        (WorldOpcode pendingOpcode, byte[] pendingPayload) = Assert.Single(session.ToClient);
        Assert.Equal(WorldOpcode.SmsgTransferPending, pendingOpcode);
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(pendingPayload));
        Assert.Equal(1, router.StagedCount);

        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 100));
        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPlayerLogin, Bytes(2, 2), 101));
        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(3), 102));
        Assert.Empty(env.TargetLink.Sent);
        Assert.Empty(session.ToSource);

        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);

        Assert.Equal(new[] { (byte)1, (byte)2, (byte)3 }, env.TargetLink.Sent.Select(sent => sent.Payload[0]).ToArray());
        Assert.All(env.TargetLink.Sent, sent => Assert.Equal(70, sent.Generation));
        Assert.Equal(new uint[] { 100, 101, 102 }, env.TargetLink.Sent.Select(sent => sent.ReceivedMs).ToArray());
        Assert.NotNull(session.Repointed);
        Assert.Equal(70, session.SessionGeneration);
        Assert.Empty(session.ToSource);
        Assert.Equal(0, router.StagedCount);
        Assert.Equal(Token, router.OwnershipOf(70));
        Assert.Null(session.Kicked);

        // After the commit packets are not held any more.
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(70, WorldOpcode.CmsgPing, Bytes(4), 103));
    }

    [Fact]
    public async Task Cancel_ResumesTheSource_ReplaysTheBufferThere_AndClosesTheTargetSession()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        await Stage(router);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 10);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(2), 11);

        await router.CancelStagedWorldPortAsync(TransferId, 5, 7, "ek", "target-rejected");

        Assert.Equal(new[] { (byte)1, (byte)2 }, session.ToSource.Select(sent => sent.Payload[0]).ToArray());
        Assert.Equal(new uint[] { 10, 11 }, session.ToSource.Select(sent => sent.ReceivedMs).ToArray());
        Assert.Empty(env.TargetLink.Sent);
        Assert.Null(session.Repointed);
        Assert.Equal(7, session.SessionGeneration);
        Assert.Equal((70L, ClusterSessionDetachReason.Transferred), Assert.Single(env.TargetLink.Closed));
        Assert.Equal(0, router.StagedCount);
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(3), 12));

        // A repeated cancel and a late commit of the cancelled transfer change nothing.
        await router.CancelStagedWorldPortAsync(TransferId, 5, 7, "ek", "again");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(router)); // an ended transfer id is not staged again
    }

    [Fact]
    public async Task WhileACommitOrACancelReplaysTheBuffer_TheStagedCountStaysUp_SoClientReadLoopsWaitAtTheGate()
    {
        // Intercept skips the gate while nothing is staged. Lowered before the replay, re-point and retire are done, the count would let a client read loop
        // route a packet past the half-finished switch: down the old link, or ahead of the replayed packets.
        (GatewayTransferRouter router, _, FakeSession session) = Setup();
        await Stage(router);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        int duringCommit = -1;
        session.OnRepoint = () => duringCommit = router.StagedCount;
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);
        Assert.Equal(1, duringCommit);
        Assert.Equal(0, router.StagedCount);

        (GatewayTransferRouter cancelled, _, FakeSession resumed) = Setup();
        await Stage(cancelled);
        cancelled.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        int duringResume = -1;
        resumed.OnForwardToWorker = () => duringResume = cancelled.StagedCount;
        await cancelled.CancelStagedWorldPortAsync(TransferId, 5, 7, "ek", "target-rejected");
        Assert.Equal(1, duringResume);
        Assert.Equal(0, cancelled.StagedCount);
    }

    [Fact]
    public async Task AfterCommit_PacketsStampedWithTheOldGenerationAreDropped_AndStaleServerPacketsRefused()
    {
        (GatewayTransferRouter router, _, _) = Setup();
        await Stage(router);
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);

        Assert.Equal(GatewayTransferVerdict.Drop, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1));
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(70, WorldOpcode.CmsgPing, Bytes(1), 1));
        Assert.False(router.IsCurrent(7, 4));
        Assert.True(router.IsCurrent(70, 4));
        Assert.False(router.IsCurrent(70, 3)); // the source worker's epoch
    }

    [Fact]
    public async Task Commit_IsIdempotent_AndRefusesAnUnstagedOrMisdirectedTransfer()
    {
        (GatewayTransferRouter router, FakeEnvironment env, _) = Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token));

        await Stage(router);
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CommitRouteSwitchAsync(TransferId, 5, 7, "other", Token));

        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);
        Assert.Single(env.TargetLink.Sent); // replayed once
    }

    [Fact]
    public async Task PacketOverflow_AbortsCleanly_FreesTheBuffer_ClosesTheTarget_AndKicksTheClient()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup(maxPackets: 2);
        await Stage(router);

        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1));
        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(2), 2));
        Assert.Equal(GatewayTransferVerdict.Overflow, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(3), 3));

        Assert.NotNull(session.Kicked);
        Assert.Equal((70L, ClusterSessionDetachReason.Transferred), Assert.Single(env.TargetLink.Closed));
        Assert.Equal(GatewayTransferVerdict.Drop, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(4), 4));

        // The coordinator's later commit finds nothing to route: no replay, no repoint, no exception, no staged state left.
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);
        Assert.Empty(env.TargetLink.Sent);
        Assert.Null(session.Repointed);
        Assert.Equal(0, router.StagedCount);
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(8, WorldOpcode.CmsgPing, Bytes(1), 1));
    }

    [Fact]
    public async Task ByteOverflow_AbortsLikeAPacketOverflow()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup(maxBytes: 8);
        await Stage(router);

        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, new byte[6], 1));
        Assert.Equal(GatewayTransferVerdict.Overflow, router.Intercept(7, WorldOpcode.CmsgPing, new byte[3], 2));
        Assert.NotNull(session.Kicked);
        Assert.Single(env.TargetLink.Closed);
    }

    [Fact]
    public async Task Stage_WhenTheTargetRefuses_ReleasesTheBufferToTheSourceAndThrows()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        env.RefuseTarget = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(router));

        Assert.Equal(0, router.StagedCount);
        Assert.Empty(session.ToClient); // no TRANSFER_PENDING for a transfer that did not stage
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1));
    }

    [Fact]
    public async Task Stage_RefusesAnUnknownSession_AndASecondTransferOfTheSameSession()
    {
        (GatewayTransferRouter router, _, _) = Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.StageWorldPortAsync(TransferId, 5, 99, "ek", "kal", 1, 0, default));

        await Stage(router);
        await Stage(router); // the same transfer again: no-op
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.StageWorldPortAsync(Guid.NewGuid(), 5, 7, "ek", "kal", 1, 0, default));
        Assert.Equal(1, router.StagedCount);
    }

    [Fact]
    public async Task TargetSessionClosedWhileStaged_DoesNotKick_AndTheCancelResumesTheSource()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        await Stage(router);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 10);

        router.OnTargetSessionClosed(session, 70, ClusterSessionDetachReason.LinkLost);

        Assert.Null(session.Kicked);
        Assert.Equal(1, router.StagedCount);
        Assert.Equal(GatewayTransferVerdict.Held, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(2), 11));

        await router.CancelStagedWorldPortAsync(TransferId, 5, 7, "ek", "target-lost");

        Assert.Null(session.Kicked);
        Assert.Equal(new[] { (byte)1, (byte)2 }, session.ToSource.Select(sent => sent.Payload[0]).ToArray());
        Assert.Empty(env.TargetLink.Sent);
        Assert.Empty(env.TargetLink.Closed); // the lost target session is not closed a second time
        Assert.Equal(7, session.SessionGeneration);
        Assert.Equal(0, router.StagedCount);
    }

    [Fact]
    public async Task TargetSessionClosedAfterCommit_KicksTheClient_ButAStaleGenerationDoesNot()
    {
        (GatewayTransferRouter router, _, FakeSession session) = Setup();
        await Stage(router);
        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);

        router.OnTargetSessionClosed(session, 7, ClusterSessionDetachReason.LinkLost);
        Assert.Null(session.Kicked);

        router.OnTargetSessionClosed(session, 70, ClusterSessionDetachReason.LinkLost);
        Assert.NotNull(session.Kicked);
    }

    [Fact]
    public async Task Commit_ForAClientThatLeft_ClosesTheTargetSessionAndRoutesNothing()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        await Stage(router);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        session.IsOpen = false;

        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);

        Assert.Empty(env.TargetLink.Sent);
        Assert.Null(session.Repointed);
        Assert.Equal((70L, ClusterSessionDetachReason.ClientDisconnect), Assert.Single(env.TargetLink.Closed));
    }

    [Fact]
    public async Task Commit_WhenTheTargetLinkDropsDuringReplay_KicksTheClient()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        await Stage(router);
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        env.TargetLink.Down = true;

        await router.CommitRouteSwitchAsync(TransferId, 5, 7, "kal", Token);

        Assert.NotNull(session.Kicked);
    }

    [Fact]
    public async Task HandleControl_AnswersEachFrameWithAnAck_AndReportsRefusals()
    {
        (GatewayTransferRouter router, FakeEnvironment env, FakeSession session) = Setup();
        var acks = new List<GatewayTransferAck>();

        await router.HandleControlAsync(new GatewayTransferStage(TransferId, 5, 7, "ek", "kal", 30, 0, default), ack => { acks.Add(ack); return true; });
        router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1);
        await router.HandleControlAsync(new GatewayTransferCommit(TransferId, 5, 7, "kal", Token.Scope, Token.Epoch, Token.Incarnation), ack => { acks.Add(ack); return true; });
        await router.HandleControlAsync(new GatewayTransferCommit(Guid.NewGuid(), 5, 7, "kal", Token.Scope, Token.Epoch, Token.Incarnation), ack => { acks.Add(ack); return true; });

        Assert.Equal(3, acks.Count);
        Assert.Equal(new GatewayTransferAck(TransferId, GatewayTransferPhase.Stage, true, string.Empty), acks[0]);
        Assert.Equal(new GatewayTransferAck(TransferId, GatewayTransferPhase.Commit, true, string.Empty), acks[1]);
        Assert.False(acks[2].Accepted);
        Assert.NotEmpty(acks[2].Reason);
        Assert.Single(env.TargetLink.Sent);
        Assert.Equal(Token, router.OwnershipOf(session.SessionGeneration));
    }

    [Fact]
    public void Intercept_WithNoTransfer_PassesEverything()
    {
        (GatewayTransferRouter router, _, _) = Setup();
        Assert.Equal(GatewayTransferVerdict.Pass, router.Intercept(7, WorldOpcode.CmsgPing, Bytes(1), 1));
        Assert.Equal(0, router.StagedCount);
    }
}
