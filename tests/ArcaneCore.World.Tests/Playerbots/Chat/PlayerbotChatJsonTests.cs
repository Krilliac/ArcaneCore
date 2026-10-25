using System.Text.Json;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>Tolerant extraction of the model's JSON object, and that unreadable output is an anomaly, never a fault.</summary>
public sealed class PlayerbotChatJsonTests
{
    private static string? Reply(string? answer)
    {
        using JsonDocument? document = PlayerbotChatJson.TryParseObject(answer, out _);
        return document is not null && document.RootElement.TryGetProperty("reply", out JsonElement reply) ? reply.GetString() : null;
    }

    [Theory]
    [InlineData("{\"reply\": \"Hi there\", \"intent\": \"none\"}")]
    [InlineData("```json\n{\"reply\": \"Hi there\"}\n```")]
    [InlineData("```\n{\"reply\": \"Hi there\"}\n```")]
    [InlineData("```json {\"reply\": \"Hi there\"}```")]
    [InlineData("{\"reply\": \"Hi there\"}\n```")]                          // valid JSON then a trailing fence ('`' after a single value)
    [InlineData("{\"reply\": \"Hi there\"} and some chatter {not json}")]  // trailing text
    [InlineData("Sure! {\"reply\": \"Hi there\"}")]                        // leading text
    [InlineData("{\\\"reply\\\": \\\"Hi there\\\"}")]                      // escaped ('\' invalid start of a property name)
    [InlineData("{'reply': 'Hi there'}")]                                  // single-quoted keys (''' invalid start)
    [InlineData("{reply: \"Hi there\"}")]                                  // unquoted keys ('r' invalid start)
    [InlineData("{name: \"x\", reply: 'Hi there'}")]                       // unquoted keys, 'n' invalid start
    public void TheObjectIsRecovered(string answer) => Assert.Equal("Hi there", Reply(answer));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    [InlineData("{\"reply\": \"cut off")]
    [InlineData("{oops: nothing useful}")]
    [InlineData("{'text': 'wrong field'}")]
    [InlineData("{{{{")]
    [InlineData("{\\\"reply\\\": broken")]
    public void GarbageIsRejected_WithAnError(string? answer)
    {
        using JsonDocument? document = PlayerbotChatJson.TryParseObject(answer, out string? error);
        Assert.Null(document);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void AnEscapedObject_ReadsItsEscapes()
        => Assert.Equal("say \"hi\"", Reply("{\\\"reply\\\": \\\"say \\\\\\\"hi\\\\\\\"\\\"}"));

    [Theory]
    [InlineData("{'text': 'x'}")]
    [InlineData("{name: x}")]
    [InlineData("{\"reply\": \"cut off")]
    public void UnreadableAnswers_AreAnomaliesNotFaults(string answer)
    {
        long Faults() => FaultLedger.Shared.Snapshot().Where(s => s.Site == "playerbot.chat-prompts").Sum(s => s.Count);
        long before = Faults();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "bot-chat/prompt-json-invalid");

        (string? reply, _) = PlayerbotChatPrompts.ReadAnswer(answer);
        _ = PlayerbotChatPrompts.ReadAction(answer);

        Assert.Null(reply);
        Assert.Equal(before, Faults());
        if (!answer.Contains("cut off", StringComparison.Ordinal)) Assert.True(capture.Count("bot-chat/prompt-json-invalid") >= 1);
    }
}
