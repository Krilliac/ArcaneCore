using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.World.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary><c>.reload chat_word_filter</c>: recompiles the rows off the world thread and publishes chat and name rules together.</summary>
public sealed class ChatWordFilterContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "chat_word_filter";

    public bool IncludedInAll => services.GetService<ChatWordFilterFeature>()?.Enabled == true;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ChatWordFilterFeature feature = services.GetRequiredService<ChatWordFilterFeature>();
        using IServiceScope scope = services.CreateScope();
        IChatWordFilterStore store = scope.ServiceProvider.GetService<IChatWordFilterStore>()
            ?? throw new InvalidOperationException("no chat word filter store is registered");
        var filter = new ChatWordFilter(await store.LoadAsync(cancellationToken).ConfigureAwait(false));
        return new Candidate(feature, filter);
    }

    private sealed class Candidate(ChatWordFilterFeature feature, ChatWordFilter filter) : ContentCandidate
    {
        public override string Summary => $"{filter.ChatRuleCount} chat rules, {filter.NamePatterns.Length} name rules, {filter.Rejected.Length} invalid";

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            ChatWordFilter previous = feature.Filter;
            transaction.Step("chat word filter", () => feature.Publish(filter), () => feature.Publish(previous));
        }
    }
}
