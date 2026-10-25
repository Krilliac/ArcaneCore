using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Plugs the dev UI's action layer into Atlas: the <see cref="LiveActions"/> service and the <c>action.*</c> and <c>devui.*</c> commands.
/// Discovered by reflection. The daemon seam is whatever <see cref="IFeedRequester"/> the process registered; without one every action
/// answers <c>unavailable</c> (the dev UI is for the local operator only, and nothing here works without a connected daemon).
/// </summary>
public sealed class DevUiModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "devui";

    /// <inheritdoc />
    public int Order => ModuleOrder.Host - 50;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AtlasServices services = context.Services;
        var actions = new LiveActions(
            () => services.Get<IFeedRequester>(),
            new LazyDirectory(() => services.Get<LiveFeed>()?.Store),
            () => context.View.Current.MapId,
            context.Events);
        services.Add(actions);
        LiveActionCommands.Register(context.Commands);
    }

    private sealed class LazyDirectory(Func<LiveStore?> store) : IEntityDirectory
    {
        public LiveEntity? Get(ulong guid) => store() is { } s && s.TryGetEntity(guid, out LiveEntity? entity) ? entity : null;

        public LiveEntity? FindPlayer(string name)
            => store()?.Snapshot(e => e.Kind is LiveKind.Player or LiveKind.Bot && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()?.Entity;
    }
}
