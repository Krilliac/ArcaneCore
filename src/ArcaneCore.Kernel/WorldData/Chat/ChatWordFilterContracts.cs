namespace ArcaneCore.Kernel.WorldData.Chat;

/// <summary>Where a <c>chat_word_filter</c> rule applies (bit flags).</summary>
[Flags]
public enum ChatWordFilterScope : byte
{
    None = 0,

    /// <summary>Player chat (say, yell, emote, party, raid, guild, whisper, channel).</summary>
    Chat = 1,

    /// <summary>Character and pet names: a match is answered CHAR_NAME_PROFANE.</summary>
    Names = 2,
}

/// <summary>What a chat match does.</summary>
public enum ChatWordFilterAction : byte
{
    /// <summary>Each match is replaced by asterisks of the same length.</summary>
    Censor = 0,

    /// <summary>The whole message is dropped and the speaker is told why.</summary>
    Block = 1,
}

/// <summary>One <c>chat_word_filter</c> row (an AscEmu WordFilter.cpp style .NET regex, matched case-insensitively).</summary>
public sealed record ChatWordFilterRule(uint Id, string Pattern, ChatWordFilterScope Scope, ChatWordFilterAction Action);

public interface IChatWordFilterStore
{
    Task<IReadOnlyList<ChatWordFilterRule>> LoadAsync(CancellationToken cancellationToken = default);
}
