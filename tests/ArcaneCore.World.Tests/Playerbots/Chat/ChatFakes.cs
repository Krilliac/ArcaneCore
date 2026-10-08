using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Chat;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>A provider transport with no network: answers from a script, records every call.</summary>
internal sealed class FakeBotChatClient : IBotChatClient
{
    private readonly ConcurrentQueue<Func<PlayerbotChatProviderOptions, CancellationToken, Task<BotChatResult>>> _script = new();

    public ConcurrentQueue<(PlayerbotChatProviderOptions Provider, string? Key, BotChatPrompt Prompt, bool OnWorldThread)> Calls { get; } = new();

    /// <summary>Answer when the script is empty (default: a JSON reply "fake reply").</summary>
    public Func<PlayerbotChatProviderOptions, CancellationToken, Task<BotChatResult>> Default { get; set; }
        = (_, _) => Task.FromResult(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"fake reply\", \"intent\": \"none\"}", 100, 10));

    /// <summary>Set by world tests: whether a call runs on the world thread.</summary>
    public Func<bool>? IsWorldThread { get; set; }

    public void Then(Func<PlayerbotChatProviderOptions, CancellationToken, Task<BotChatResult>> step) => _script.Enqueue(step);

    public void ThenReturn(BotChatResult result) => Then((_, _) => Task.FromResult(result));

    public Task<BotChatResult> CompleteAsync(PlayerbotChatProviderOptions provider, string? apiKey, BotChatPrompt prompt, CancellationToken cancellationToken)
    {
        Calls.Enqueue((provider, apiKey, prompt, IsWorldThread?.Invoke() ?? false));
        return (_script.TryDequeue(out var step) ? step : Default)(provider, cancellationToken);
    }

    /// <summary>A request that never answers until it is cancelled (the timeout).</summary>
    public static async Task<BotChatResult> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return BotChatResult.Fail(BotChatOutcome.Ok);
    }
}

internal sealed class FakeBotChatEnvironment(Dictionary<string, string> values) : IBotChatEnvironment
{
    public Dictionary<string, string> Values { get; } = values;

    public string? Get(string variable) => Values.TryGetValue(variable, out string? value) ? value : null;
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Every formatted log line (and exception text), to prove what never appears in a log.</summary>
internal sealed class ListLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
}

internal static class ChatSamples
{
    public static BotChatPersona Persona(PlayerbotGoalKind? goal = PlayerbotGoalKind.Quest, string? quest = "Wolves Across the Border",
        string? zone = "Elwynn Forest", string? subzone = "Northshire Valley", byte level = 7, string? master = null, bool inGroup = false,
        Race race = Race.Human, Class @class = Class.Warrior)
        => new("Chatbot", race, @class, Gender.Male, level, zone, subzone, goal, quest, master, inGroup, []);

    public static BotChatAsk Ask(string text, BotChatPersona? persona = null, ulong sender = 0x42, ChatType channel = ChatType.Whisper,
        bool fromMaster = false, bool inviteAllowed = false, Guid? bot = null)
        => new(bot ?? BotId, new ObjectGuid(0x7), persona ?? Persona(), new ObjectGuid(sender), "Nathan", channel, text, fromMaster, inviteAllowed);

    public static readonly Guid BotId = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
}
