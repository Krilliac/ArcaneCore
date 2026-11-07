using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chat;

internal sealed partial class PlayerbotChat
{
    /// <summary>Whether a one-shot request can reach a model now: chat is active and a model provider (not only the built-in templates) is configured.</summary>
    internal bool HasModelNow => IsActive && Options.HasModelProvider;

    /// <summary>
    /// Worker or any non-world thread: one model request that belongs to no bot conversation (the GM ticket assistant). The same
    /// guards as <see cref="AnswerAsync"/>: personal data is stripped from <paramref name="untrustedText"/> when that is on, the rest is
    /// screened by the local filter (a flagged or unjudged text reaches no model), the text is cut to one chat line (<see cref="PlayerbotChatPrompts.MaxLineLength"/> characters)
    /// and that same cut is what each provider's input moderation sees and what the prompt carries (nothing is sent that was not moderated; a flagged text reaches no provider,
    /// a failed step skips that provider), provider budgets and cool-downs apply, and the request has the chat timeout.
    /// With <paramref name="screenOutput"/> the reply is also stripped of personal data, cut to one chat line, moderated by the same
    /// provider step and screened by the local filter; a reply that fails any of these is not returned. Null when no provider answered.
    /// </summary>
    /// <param name="build">Builds the prompt from the text as it may be sent.</param>
    internal async Task<string?> CompleteOnceAsync(string untrustedText, Func<string, BotChatPrompt> build, bool screenOutput, CancellationToken cancellationToken)
    {
        PlayerbotChatOptions options = Options;
        if (!HasModelNow)
        {
            return null;
        }

        PlayerbotChatSafetyOptions safety = options.Safety;
        bool screening = safety.Enabled;
        // Redaction first: what is screened and moderated is what would be sent, so a player's own email address is not a "flag".
        string outgoing = safety.StripPersonalData ? _safety.StripPersonalData(untrustedText) : untrustedText;
        if (screening)
        {
            PlayerbotChatSafetyFilter filter = _safety.Filter();
            await filter.Warmed.ConfigureAwait(false);
            SafetyVerdict verdict = _safety.ScreenInput(outgoing, filter);
            if (verdict.Flagged || verdict.TimedOut)
            {
                return null;
            }
        }

        string sent = PlayerbotChatPrompts.Clean(outgoing, PlayerbotChatPrompts.MaxLineLength);
        BotChatPrompt prompt = build(sent);
        int index = -1;
        foreach (PlayerbotChatProviderOptions provider in options.EffectiveProviders())
        {
            index++;
            if (provider.Kind == PlayerbotChatProviderKind.Builtin)
            {
                continue;
            }

            string? key;
            lock (_gate)
            {
                if (!CanAnswer(provider, _time.GetUtcNow(), out key))
                {
                    continue;
                }
            }

            if (screening && provider.Moderation != PlayerbotChatModerationKind.None)
            {
                BotChatModeration moderation = await ModerateTextAsync(provider, key, sent, options).ConfigureAwait(false);
                if (moderation.Verdict == BotChatModerationVerdict.Flagged)
                {
                    return null;
                }

                if (moderation.Verdict != BotChatModerationVerdict.Clean)
                {
                    FailModeration(provider, index, moderation);
                    continue;
                }
            }

            BotChatResult result = await RequestOnceAsync(provider, key, prompt, options, cancellationToken).ConfigureAwait(false);
            Record(provider, index, result);
            if (result.Outcome != BotChatOutcome.Ok || string.IsNullOrWhiteSpace(result.Text))
            {
                continue;
            }

            if (!screenOutput)
            {
                return result.Text;
            }

            string? reply = PlayerbotChatPrompts.ReadAnswer(result.Text).Reply;
            if (reply is null)
            {
                continue;
            }

            reply = PlayerbotChatPrompts.Clean(safety.StripPersonalData ? _safety.StripPersonalData(reply) : reply, PlayerbotChatOptions.MaxReplyLength);
            if (reply.Length == 0)
            {
                continue;
            }

            if (screening && provider.Moderation != PlayerbotChatModerationKind.None)
            {
                BotChatModeration moderation = await ModerateTextAsync(provider, key, reply, options).ConfigureAwait(false);
                if (moderation.Verdict != BotChatModerationVerdict.Clean)
                {
                    return null; // flagged, or not judged: never sent unchecked
                }
            }

            if (screening && safety.ScreenOutput && _safety.ScreenOutput(reply) is { } outVerdict && (outVerdict.Flagged || outVerdict.TimedOut))
            {
                return null;
            }

            return reply;
        }

        return null;
    }

    /// <summary>
    /// Any thread: a short model-written text that is not a chat reply (the ticket assistant's summary) with personal data stripped when that is on, and null
    /// when the local output screen flags it or cannot judge it in time. The summary is only ever shown to staff, so no provider moderation call is made for it.
    /// </summary>
    internal string? ScreenOneShotText(string text)
    {
        PlayerbotChatSafetyOptions safety = Options.Safety;
        string clean = safety.StripPersonalData ? _safety.StripPersonalData(text) : text;
        if (safety.Enabled && safety.ScreenOutput && _safety.ScreenOutput(clean) is { } verdict && (verdict.Flagged || verdict.TimedOut))
        {
            return null;
        }

        return clean;
    }

    private async Task<BotChatResult> RequestOnceAsync(
        PlayerbotChatProviderOptions provider, string? key, BotChatPrompt prompt, PlayerbotChatOptions options, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60)));
        try
        {
            return await _client.CompleteAsync(provider, key, prompt, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return BotChatResult.Fail(BotChatOutcome.Timeout);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            FaultLedger.Shared.Record("playerbot.chat-delivery", error);
            _logger.LogWarning("Bot chat: a one-shot request failed ({Error})", error.GetType().Name);
            return BotChatResult.Fail(BotChatOutcome.NetworkError);
        }
    }
}
