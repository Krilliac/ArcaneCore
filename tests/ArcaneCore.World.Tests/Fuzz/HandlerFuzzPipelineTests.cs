using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// The reporting pipeline of the opcode handler fuzz, end to end: the real case loop (the same world batch, session runner, exception
/// classification and invariant checks as the real run) over a small table of planted handlers. Each defect must come out as its finding
/// kind, and a well-behaved handler must come out clean, so a harness that stopped reporting would fail here.
/// </summary>
public sealed class HandlerFuzzPipelineTests(ITestOutputHelper output)
{
    // Opcode values are only labels here: the planted table is injected, nothing is dispatched through the real one.
    private const WorldOpcode ThrowsNull = WorldOpcode.CmsgBootme;
    private const WorldOpcode ThrowsForeignRange = WorldOpcode.CmsgDblookup;
    private const WorldOpcode ChangesMoney = WorldOpcode.CmsgQueryObjectPosition;
    private const WorldOpcode WellBehaved = WorldOpcode.CmsgQueryObjectRotation;
    private const WorldOpcode HangsSession = WorldOpcode.CmsgZoneMap;
    private const WorldOpcode Kicks = WorldOpcode.CmsgCreatemonster;
    private const WorldOpcode RaisesFault = WorldOpcode.CmsgDestroymonster;

    [Fact]
    public async Task PlantedDefects_AreReported_AsTheirFindingKinds_AndAGoodHandlerIsNot()
    {
        var table = new OpcodeTable();
        table.OnWorld(ThrowsNull, (_, _, _) => throw new NullReferenceException("planted"));
        table.OnWorld(ThrowsForeignRange, (_, _, _) => throw new ArgumentOutOfRangeException("x", "planted: not the packet reader's parameter"));
        table.OnWorld(ChangesMoney, (_, player, _) => player.Money += 1);
        table.OnWorld(WellBehaved, (_, _, payload) =>
        {
            var reader = new PacketReader(payload);
            reader.ReadUInt32(); // a short payload ends in the controlled outcome, which is not a finding
        });
        table.OnSession(HangsSession, SessionStates.Authenticated, (_, _) => new TaskCompletionSource().Task);
        table.OnWorld(Kicks, (session, _, _) => session.Kick("planted kick"));
        string faultSite = "fuzz.planted." + Guid.NewGuid().ToString("N"); // a signature no other test has recorded, so the ledger raises a notice
        table.OnWorld(RaisesFault, (_, _, _) => FaultLedger.Shared.Record(faultSite, new InvalidOperationException("planted fault")));

        var options = new HandlerFuzzOptions
        {
            Table = table,
            SessionHandlerDeadline = TimeSpan.FromMilliseconds(300), // short only for this test; the real run's bound is 10 s
        };
        var run = new HandlerFuzzRun(output, new HandlerFuzzSettings(HandlerFuzzSettings.DefaultRandomIterations, HandlerFuzzSettings.DefaultBaseSeed, null), options);
        List<HandlerFuzzFinding> findings = await run.ExecuteAsync();
        output.WriteLine(run.FormatFindings());

        bool Has(WorldOpcode opcode, string kindStart) => findings.Any(f => f.Opcode == opcode && f.Kind.StartsWith(kindStart, StringComparison.Ordinal));

        Assert.True(Has(ThrowsNull, "System.NullReferenceException"), "NRE");
        Assert.True(Has(ThrowsForeignRange, "ArgumentOutOfRangeException (ParamName=x"), "AOORE with a foreign ParamName");
        Assert.True(Has(ChangesMoney, "money-changed-outside-set"), "money change outside the set");
        Assert.True(Has(HangsSession, "handler-hung"), "session handler hang");
        Assert.True(Has(Kicks, "unexpected-kick"), "kick outside the allowlist");
        Assert.True(Has(Kicks, "recovery-capped"), "recovery cap outside the allowlist");
        Assert.True(Has(RaisesFault, "deferred-fault:fuzz.planted."), "fault recorded on the world thread");
        Assert.DoesNotContain(findings, f => f.Opcode == WellBehaved);
    }

    [Fact]
    public async Task AFilterThatSelectsNothing_IsAFinding_NotAPass()
    {
        var table = new OpcodeTable();
        table.OnWorld(WellBehaved, (_, _, _) => { });
        var run = new HandlerFuzzRun(output, new HandlerFuzzSettings(HandlerFuzzSettings.DefaultRandomIterations, HandlerFuzzSettings.DefaultBaseSeed, "CMSG_NO_SUCH_OPCODE"), new HandlerFuzzOptions { Table = table });

        List<HandlerFuzzFinding> findings = await run.ExecuteAsync();

        Assert.Contains(findings, f => f.Kind == "opcode-filter-selected-nothing");
    }
}
