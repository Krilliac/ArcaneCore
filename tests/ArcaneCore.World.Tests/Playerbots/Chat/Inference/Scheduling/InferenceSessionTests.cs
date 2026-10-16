// Lifecycle tests for the session state machine ported from Krilliac/Sonder-Inference
// src/sessions/session.cpp @1c4c152 (Apache-2.0). Upstream covers these through engine-level
// tests that need a backend; these exercise the same rules without one.

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Sessions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class InferenceSessionTests
{
    [Fact]
    public void OneRequestInFlightThenIdleAgain()
    {
        var counters = new InferenceRequestCounters();
        using var session = new InferenceSession("bot-1", WorkloadClass.InteractivePlayer, counters);
        Assert.Equal(InferenceSessionState.Idle, session.State);

        Assert.Equal(BeginRequestError.None, session.TryBegin(CancellationToken.None, out InferenceRequest? first));
        Assert.NotNull(first);
        Assert.Equal(InferenceSessionState.Running, session.State);
        Assert.Equal(BeginRequestError.RequestInFlight, session.TryBegin(CancellationToken.None, out InferenceRequest? second));
        Assert.Null(second);

        first.MarkAdmitted(reusedPromptTokens: 48);
        first.MarkToken();
        first.MarkToken();
        RequestSummary? summary = first.Complete(RequestOutcome.Completed);
        Assert.True(summary.HasValue);
        Assert.Equal(RequestOutcome.Completed, summary.GetValueOrDefault().Outcome);
        Assert.Equal(2u, summary.GetValueOrDefault().GeneratedTokens);
        Assert.Equal(48UL, summary.GetValueOrDefault().ReusedPromptTokens);
        Assert.True(summary.GetValueOrDefault().TtftMs >= 0);
        Assert.Null(first.Complete(RequestOutcome.Failed)); // only the first end counts

        Assert.Equal(InferenceSessionState.Idle, session.State);
        Assert.Equal(RequestOutcome.Completed, session.LastOutcome);
        Assert.Equal(1UL, session.RequestsStarted);
        Assert.Equal(1L, counters.Started);
        Assert.Equal(1L, counters.Completed);
        Assert.Equal(1L, counters.RejectedBusy);
        Assert.Equal(2L, counters.GeneratedTokens);
        Assert.Equal(0L, counters.InFlight);
    }

    [Fact]
    public void CancelTripsTheRequestTokenAndKeepsTheSessionUsable()
    {
        using var session = new InferenceSession("bot-2", WorkloadClass.BotToBot);
        Assert.Equal(BeginRequestError.None, session.TryBegin(CancellationToken.None, out InferenceRequest? request));
        Assert.NotNull(request);
        CancellationToken token = request.Token;
        session.Cancel();
        Assert.True(token.IsCancellationRequested);
        RequestSummary? summary = request.Complete(RequestOutcome.None);
        Assert.Equal(RequestOutcome.Cancelled, summary.GetValueOrDefault().Outcome);
        Assert.True(summary.GetValueOrDefault().CancelLatencyMs >= 0);
        Assert.Equal(InferenceSessionState.Idle, session.State);
        Assert.Equal(1L, session.Counters.Cancelled);

        Assert.Equal(BeginRequestError.None, session.TryBegin(CancellationToken.None, out InferenceRequest? again));
        Assert.NotNull(again);
        Assert.NotEqual(request.RequestId, again.RequestId);
        again.Dispose(); // unfinished, not cancelled: ends as failed
        Assert.Equal(RequestOutcome.Failed, session.LastOutcome);
    }

    [Fact]
    public void CallerTokenCancelsTheRequest()
    {
        using var cts = new CancellationTokenSource();
        using var session = new InferenceSession("bot-3", WorkloadClass.Ambient);
        Assert.Equal(BeginRequestError.None, session.TryBegin(cts.Token, out InferenceRequest? request));
        Assert.NotNull(request);
        cts.Cancel();
        Assert.True(request.IsCancellationRequested);
        request.Dispose();
        Assert.Equal(RequestOutcome.Cancelled, session.LastOutcome);
    }

    [Fact]
    public void CloseCancelsInFlightWorkAndRefusesNewRequests()
    {
        var session = new InferenceSession("bot-4", WorkloadClass.InteractivePlayer);
        Assert.Equal(BeginRequestError.None, session.TryBegin(CancellationToken.None, out InferenceRequest? request));
        Assert.NotNull(request);
        session.Close();
        Assert.True(request.IsCancellationRequested);
        Assert.Equal(InferenceSessionState.Closed, session.State);
        request.Dispose();
        Assert.Equal(InferenceSessionState.Closed, session.State); // ending does not reopen
        Assert.Equal(BeginRequestError.SessionClosed, session.TryBegin(CancellationToken.None, out InferenceRequest? refused));
        Assert.Null(refused);
        Assert.Equal(1L, session.Counters.RejectedClosed);
        session.Close(); // idempotent
    }

    [Fact]
    public void SharedCountersIssueUniqueRequestIdsAcrossSessions()
    {
        var counters = new InferenceRequestCounters();
        using var a = new InferenceSession("a", WorkloadClass.BotToBot, counters);
        using var b = new InferenceSession("b", WorkloadClass.BotToBot, counters);
        Assert.Equal(BeginRequestError.None, a.TryBegin(CancellationToken.None, out InferenceRequest? ra));
        Assert.Equal(BeginRequestError.None, b.TryBegin(CancellationToken.None, out InferenceRequest? rb));
        Assert.NotNull(ra);
        Assert.NotNull(rb);
        Assert.NotEqual(ra.RequestId, rb.RequestId);
        Assert.Equal(2L, counters.InFlight);
        ra.Complete(RequestOutcome.Completed);
        rb.Complete(RequestOutcome.Completed);
        Assert.Equal(0L, counters.InFlight);
    }
}
