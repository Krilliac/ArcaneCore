using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>The coarse reading of an affinity (-100..100) the chat uses.</summary>
internal static class PlayerbotSocialBands
{
    /// <summary>At or below this the bot dislikes the other.</summary>
    internal const int DislikeAt = -20;

    /// <summary>At or above this the bot likes the other.</summary>
    internal const int LikeAt = 20;

    /// <summary>At or above this the other is a close friend.</summary>
    internal const int CloseAt = 60;

    internal static BotChatAffinityBand Of(int affinity)
        => affinity <= DislikeAt ? BotChatAffinityBand.Dislike : affinity >= CloseAt ? BotChatAffinityBand.Close
            : affinity >= LikeAt ? BotChatAffinityBand.Like : BotChatAffinityBand.Neutral;
}
