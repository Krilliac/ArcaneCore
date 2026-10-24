namespace ArcaneCore.World.OptionKeys;

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
/// What a pack (the playerbots, a script module) registers its options into, so the one editor (<c>.server config</c>) lists, reads and
/// changes them without the core naming a pack type. Every getter and setter is called on the world thread. A getter returns null when the
/// owner is not running; such a key is left out of the listing. A key with no setter, or with <see cref="OptionApplyClass.Restart"/>, is
/// read-only. A setter must not throw for a value inside the registered bounds.
/// </summary>
public interface IOptionRegistry
{
    /// <summary>A boolean option.</summary>
    void AddBool(string path, string description, OptionApplyClass apply, Func<bool?> get, Action<bool>? set = null);

    /// <summary>A whole-number option; the editor refuses a value outside <paramref name="min"/>..<paramref name="max"/>.</summary>
    void AddInteger(string path, string description, OptionApplyClass apply, long min, long max, Func<long?> get, Action<long>? set = null);

    /// <summary>A decimal option; the editor refuses a value outside <paramref name="min"/>..<paramref name="max"/> and any non-finite value.</summary>
    void AddNumber(string path, string description, OptionApplyClass apply, double min, double max, Func<double?> get, Action<double>? set = null);

    /// <summary>A text option. A <paramref name="secret"/> value is never printed; the editor shows it masked.</summary>
    void AddText(string path, string description, OptionApplyClass apply, Func<string?> get, Action<string>? set = null, bool secret = false);
}

/// <summary>
/// A source of option keys for <c>.server config</c>, resolved from the service provider (register it as a singleton). It is asked to
/// register on each use of the editor, so the closures it hands out may capture the live option objects.
/// </summary>
public interface IOptionKeyProvider
{
    /// <summary>Registers this source's keys into <paramref name="registry"/>.</summary>
    void RegisterOptions(IOptionRegistry registry);
}
