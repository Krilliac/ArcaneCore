namespace ArcaneCore.Game.Reload;

/// <summary>
/// An <see cref="IContentReloadable"/> that exists only when its own switch says so (a reload retail does not have, shipped off). The reload
/// feature leaves a reloadable whose <see cref="IsEnabled"/> is false out of the coordinator, so its <c>.reload</c> name is an unknown
/// name, like any other.
/// </summary>
public interface IOptionalReloadable
{
    /// <summary>Whether the reload is registered at all.</summary>
    bool IsEnabled { get; }
}
