using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>The line formats the cluster host supervisor and its children exchange on stdout and stdin (<see cref="SupervisorProtocol"/>).</summary>
public sealed class SupervisorProtocolTests
{
    [Fact]
    public void Dumping_WithMaxAndToken_RoundTripsThroughTheHeartbeatLineFormat()
    {
        string line = "heartbeat 2026-10-29T10:00:00.000Z pid=1 " + SupervisorProtocol.FormatDumpingState(360, "tok");

        Assert.Equal("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=360 token=tok", line);
        Assert.True(SupervisorProtocol.TryParseDumping(line, out int max, out string token));
        Assert.Equal(360, max);
        Assert.Equal("tok", token);
        Assert.False(SupervisorProtocol.TryParseHeartbeat(line, out _), "a dumping line is not a plain heartbeat");
    }

    [Theory]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=360")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping token=tok")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=x token=tok")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=360 token=tok extra")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=360 token=tok\r")]
    [InlineData("x heartbeat 2026-10-29T10:00:00.000Z pid=1 dumping max=360 token=tok")]
    public void Dumping_MalformedOrWithoutAToken_IsRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseDumping(line, out _, out _));

    [Fact]
    public void FormatDumpingState_WithoutAToken_WritesNone_AndClampsTheMax()
        => Assert.Equal("dumping max=1", SupervisorProtocol.FormatDumpingState(0, null));

    [Theory]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=4242 ready", SupervisorHeartbeatState.Ready)]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=4242 alive", SupervisorHeartbeatState.Alive)]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 stopping", SupervisorHeartbeatState.Stopping)]
    public void Heartbeat_WellFormed_IsParsed(string line, SupervisorHeartbeatState expected)
    {
        Assert.True(SupervisorProtocol.TryParseHeartbeat(line, out SupervisorHeartbeatState state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("")]
    [InlineData("heartbeat")]
    [InlineData("heartbeat  pid=1 alive")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=abc alive")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 dead")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 alive extra")]
    [InlineData(" heartbeat 2026-10-29T10:00:00.000Z pid=1 alive")]
    [InlineData("info: heartbeat 2026-10-29T10:00:00.000Z pid=1 alive")]
    public void Heartbeat_Malformed_IsRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseHeartbeat(line, out _));

    [Fact]
    public void Heartbeat_WhatTheTextSinkWrites_IsWhatTheParserReads()
    {
        var writer = new StringWriter();
        using var sink = new TextWriterHeartbeatSink(writer, 77);
        sink.Ready("up");
        sink.Beat();
        sink.Stopping("down");

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.True(SupervisorProtocol.TryParseHeartbeat(lines[0], out SupervisorHeartbeatState ready));
        Assert.True(SupervisorProtocol.TryParseHeartbeat(lines[1], out SupervisorHeartbeatState alive));
        Assert.True(SupervisorProtocol.TryParseHeartbeat(lines[2], out SupervisorHeartbeatState stopping));
        Assert.Equal([SupervisorHeartbeatState.Ready, SupervisorHeartbeatState.Alive, SupervisorHeartbeatState.Stopping], [ready, alive, stopping]);
    }

    [Theory]
    [InlineData(0, SupervisorStopIntent.Shutdown)]
    [InlineData(2, SupervisorStopIntent.Restart)]
    [InlineData(1, SupervisorStopIntent.Failure)]
    [InlineData(70, SupervisorStopIntent.Failure)]
    [InlineData(125, SupervisorStopIntent.Failure)]
    public void IntentFor_FollowsTheExitCodeContract(int code, SupervisorStopIntent expected)
        => Assert.Equal(expected, SupervisorProtocol.IntentFor(code));

    [Theory]
    [InlineData(SupervisorStopIntent.Restart, 2, "lifecycle stopping token=tok intent=restart code=2")]
    [InlineData(SupervisorStopIntent.Shutdown, 0, "lifecycle stopping token=tok intent=shutdown code=0")]
    [InlineData(SupervisorStopIntent.Failure, 9, "lifecycle stopping token=tok intent=failure code=9")]
    public void Stopping_RoundTrips(SupervisorStopIntent intent, int code, string expectedLine)
    {
        string line = SupervisorProtocol.FormatStopping("tok", intent, code);

        Assert.Equal(expectedLine, line);
        Assert.True(SupervisorProtocol.TryParseStopping(line, out string parsedToken, out SupervisorStopIntent parsedIntent, out int parsedCode));
        Assert.Equal("tok", parsedToken);
        Assert.Equal(intent, parsedIntent);
        Assert.Equal(code, parsedCode);
    }

    [Theory]
    [InlineData("lifecycle stopping")]
    [InlineData("lifecycle stopping intent=restart code=2")] // no token: unauthenticated
    [InlineData("lifecycle stopping token= intent=restart code=2")]
    [InlineData("lifecycle stopping token=t intent=sideways code=1")]
    [InlineData("lifecycle stopping token=t intent=restart code=x")]
    [InlineData("lifecycle stopping token=t intent=restart")]
    [InlineData("lifecycle stopped token=t intent=restart code=2")]
    [InlineData("lifecycle stopping token=t intent=restart code=2\r")]
    [InlineData("\rlifecycle stopping token=t intent=restart code=2")]
    [InlineData("lifecycle stopping token=t\rintent=restart code=2")]
    public void Stopping_Malformed_IsRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseStopping(line, out _, out _, out _));

    [Theory]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 alive\r")]
    [InlineData("x\rheartbeat 2026-10-29T10:00:00.000Z pid=1 alive")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 alive\n")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1\talive")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 alive\0")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z pid=1 alive\u001b[0m")]
    [InlineData("heartbeat 2026-10-29T10:00:00.000Z\npid=1 alive")]
    public void Heartbeat_WithEmbeddedControlCharacters_IsRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseHeartbeat(line, out _));

    [Fact]
    public void LifecycleRequest_RoundTrips_WithTheToken()
    {
        var request = new SupervisorLifecycleRequest("ab12cd", 3, SupervisorLifecycleAction.Restart, 60, 2, "Admin");

        string line = SupervisorProtocol.FormatLifecycleRequest(request);

        Assert.Equal("lifecycle request token=ab12cd realm=3 action=restart delay=60 code=2 by=Admin", line);
        Assert.True(SupervisorProtocol.TryParseLifecycleRequest(line, out SupervisorLifecycleRequest parsed));
        Assert.Equal(request, parsed);
    }

    [Theory]
    [InlineData("lifecycle request realm=3 action=restart delay=60 code=2 by=Admin")] // no token
    [InlineData("lifecycle request token=t realm=x action=restart delay=60 code=2 by=Admin")]
    [InlineData("lifecycle request token=t realm=3 action=explode delay=60 code=2 by=Admin")]
    [InlineData("lifecycle request token=t realm=3 action=restart delay=-1 code=2 by=Admin")]
    [InlineData("lifecycle request token=t realm=3 action=restart delay=60 code=126 by=Admin")] // above ExitCodes.MaxRequested
    [InlineData("lifecycle request token=t realm=3 action=restart delay=60 code=2")] // no issuer
    [InlineData("lifecycle request token=t realm=3 action=restart delay=60 code=2 by=Admin trailing")]
    public void LifecycleRequest_Malformed_IsRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseLifecycleRequest(line, out _));

    [Fact]
    public void LifecycleRequest_IssuerNameWithSpacesAndControlCharacters_IsMadeOneToken()
    {
        var request = new SupervisorLifecycleRequest("t", 1, SupervisorLifecycleAction.Shutdown, 0, 0, "Bad Name\r\nlifecycle request x");

        string line = SupervisorProtocol.FormatLifecycleRequest(request);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.True(SupervisorProtocol.TryParseLifecycleRequest(line, out SupervisorLifecycleRequest parsed));
        Assert.Equal("Bad_Name__lifecycle_request_x", parsed.By);
    }

    [Fact]
    public void ForwardedLifecycle_RoundTrips()
    {
        var request = new SupervisorLifecycleRequest(string.Empty, 5, SupervisorLifecycleAction.Shutdown, 30, 0, "Admin");

        string line = SupervisorProtocol.FormatForwardedLifecycle(request);

        Assert.Equal("lifecycle shutdown delay=30 code=0 by=Admin", line);
        Assert.True(SupervisorProtocol.TryParseForwardedLifecycle(line, out SupervisorLifecycleRequest parsed));
        Assert.Equal(SupervisorLifecycleAction.Shutdown, parsed.Action);
        Assert.Equal(30u, parsed.DelaySeconds);
        Assert.Equal(0, parsed.ExitCode);
        Assert.Equal("Admin", parsed.By);
    }

    [Theory]
    [InlineData("lifecycle request token=t realm=3 action=restart delay=60 code=2 by=Admin")]
    [InlineData("lifecycle stopping token=t intent=restart code=2")]
    [InlineData("lifecycle restart delay=x code=2 by=Admin")]
    [InlineData("lifecycle restart delay=1 code=200 by=Admin")]
    [InlineData("drain")]
    public void ForwardedLifecycle_OtherLines_AreRejected(string line)
        => Assert.False(SupervisorProtocol.TryParseForwardedLifecycle(line, out _));

    [Theory]
    [InlineData(SupervisorLifecycleStatus.Forwarded)]
    [InlineData(SupervisorLifecycleStatus.UnknownRealm)]
    [InlineData(SupervisorLifecycleStatus.Rejected)]
    public void LifecycleResult_RoundTrips(SupervisorLifecycleStatus status)
    {
        var result = new SupervisorLifecycleResult(4, status, "Admin");

        string line = SupervisorProtocol.FormatLifecycleResult(result);

        Assert.True(SupervisorProtocol.TryParseLifecycleResult(line, out SupervisorLifecycleResult parsed));
        Assert.Equal(result, parsed);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void IsEnabled_ReadsTheEnvironmentVariable(string? value, bool expected)
        => Assert.Equal(expected, SupervisorProtocol.IsEnabled(name => name == SupervisorProtocol.EnvironmentVariable ? value : null));
}
