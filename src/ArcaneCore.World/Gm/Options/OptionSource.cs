namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>When a changed option takes effect.</summary>
public enum OptionApplyClass
{
    /// <summary>The consumers read the value at each use, so a change applies at once.</summary>
    Live,

    /// <summary>A change applies to what is created or loaded afterwards (new spawns, the next load); what exists keeps its value.</summary>
    NextLoad,

    /// <summary>The process reads the value once at start. The key is shown but read-only; the configuration file and a restart change it.</summary>
    Restart,
}

/// <summary>
/// What an <see cref="IOptionSource"/> registers its options into, so the one editor (<c>.server config</c>) lists, reads and changes
/// them. Every getter, setter and check is called on the world thread. A getter returns null when the owner is not running; such a key is
/// left out of the listing. A key with no setter, or with <see cref="OptionApplyClass.Restart"/>, is read-only. A <c>check</c> is the
/// key's cross-check: it sees the value already inside the registered bounds and returns the refusal text, or null to accept; the setter
/// runs only after it accepted, and a setter must not throw.
/// </summary>
public interface IOptionRegistry
{
    /// <summary>A boolean option.</summary>
    void AddBool(string path, string description, OptionApplyClass apply, Func<bool?> get, Action<bool>? set = null, Func<bool, string?>? check = null);

    /// <summary>A whole-number option; the editor refuses a value outside <paramref name="min"/>..<paramref name="max"/>.</summary>
    void AddInteger(string path, string description, OptionApplyClass apply, long min, long max, Func<long?> get, Action<long>? set = null, Func<long, string?>? check = null);

    /// <summary>A decimal option; the editor refuses a value outside <paramref name="min"/>..<paramref name="max"/> and any non-finite value.</summary>
    void AddNumber(string path, string description, OptionApplyClass apply, double min, double max, Func<double?> get, Action<double>? set = null, Func<double, string?>? check = null);

    /// <summary>A text option. A <paramref name="secret"/> value is never printed; the editor shows it masked.</summary>
    void AddText(string path, string description, OptionApplyClass apply, Func<string?> get, Action<string>? set = null, bool secret = false, Func<string, string?>? check = null);
}

/// <summary>
/// A source of option keys for <c>.server config</c>: a feature's own file registers the options it owns as a singleton of this type
/// (<c>services.AddSingleton&lt;IOptionSource, ...&gt;()</c>), so no shared catalog file is edited. It is asked to register on each use of the
/// editor, so the closures it hands out may capture the live option objects. It lives in the core assembly; the playerbot options are
/// reload keys already and need no source.
/// </summary>
public interface IOptionSource
{
    /// <summary>Registers this source's keys into <paramref name="registry"/>. World thread.</summary>
    void RegisterOptions(IOptionRegistry registry);
}
