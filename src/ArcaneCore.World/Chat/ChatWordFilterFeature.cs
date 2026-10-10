using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Names;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Chat;

/// <summary>
/// Owns the live <see cref="ChatWordFilter"/> (world <c>chat_word_filter</c>, schema 47). The rows load only when
/// <see cref="ChatOptions.WordFilter"/> is on; otherwise the filter stays empty and chat is untouched.
/// </summary>
public sealed class ChatWordFilterFeature(IServiceProvider services) : IWorldFeature
{
    private ChatWordFilter _filter = ChatWordFilter.Empty;

    public ChatWordFilter Filter => Volatile.Read(ref _filter);

    /// <summary>Whether a store is registered and the option is on (the reload joins <c>.reload all</c> only then).</summary>
    public bool Enabled { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ChatOptions options = services.GetService<ChatFeature>()?.Options ?? new ChatOptions();
        if (!options.WordFilter)
        {
            return;
        }

        // ChatFeature attaches before this feature (ordinal type-name order) and has bound its options by now.
        using IServiceScope scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<IChatWordFilterStore>() is not { } store)
        {
            return;
        }

        Enabled = true;
        Publish(new ChatWordFilter(store.LoadAsync().GetAwaiter().GetResult()));
    }

    /// <summary>Publishes a new filter and its name patterns (world thread, or before the world starts).</summary>
    public void Publish(ChatWordFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Volatile.Write(ref _filter, filter);
        services.GetService<NameCatalogFeature>()?.ReplaceFilterPatterns(filter.NamePatterns);
        if (filter.Rejected.Length > 0)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<ChatWordFilterFeature>()
                .LogWarning("chat_word_filter rows with an invalid pattern were skipped: {Ids}", string.Join(", ", filter.Rejected));
        }
    }
}
