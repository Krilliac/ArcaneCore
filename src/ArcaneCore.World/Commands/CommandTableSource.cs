namespace ArcaneCore.World.Commands;

/// <summary>Outcome of <see cref="CommandTableSource.TryAdd"/>.</summary>
/// <param name="Added">How many roots were appended (0 when rejected or when nothing was new).</param>
/// <param name="Error">Why the whole batch was rejected; null when it was applied.</param>
public readonly record struct CommandAddResult(int Added, string? Error)
{
    public bool Applied => Error is null;
}

/// <summary>
/// The live command table. Chat reads <see cref="Current"/> for every command, so a root appended
/// at runtime (a command group that only exists after a code hot reload) is reachable at once,
/// and a root can never be seen half-added: the table is immutable and a swap replaces the whole
/// reference. With nothing ever added, <see cref="Current"/> is the table the daemon built at
/// startup and behavior is unchanged.
/// </summary>
/// <remarks>
/// Only <em>appending</em> is supported. Existing roots keep their <see cref="ChatCommand"/>
/// objects: a method-body edit changes what their handler delegates run without touching the
/// table. A new root must not change how any existing input resolves; <see cref="Validate"/>
/// enforces that (vmangos FindCommand: an exact name beats an abbreviation, otherwise the first
/// match in table order wins).
/// </remarks>
public sealed class CommandTableSource(CommandTable initial)
{
    private readonly Lock _gate = new();
    private volatile CommandTable _current = initial;

    /// <summary>The table to resolve the next command against (an atomic read).</summary>
    public CommandTable Current => _current;

    /// <summary>
    /// Append <paramref name="added"/> after the existing roots, all or nothing. On rejection the
    /// current table is kept and <see cref="CommandAddResult.Error"/> says why.
    /// </summary>
    public CommandAddResult TryAdd(IReadOnlyList<ChatCommand> added)
    {
        lock (_gate)
        {
            CommandTable current = _current;
            string? error = Validate(current.Roots, added);
            if (error is not null)
            {
                return new CommandAddResult(0, error);
            }

            if (added.Count == 0)
            {
                return new CommandAddResult(0, null);
            }

            _current = new CommandTable([.. current.Roots, .. added]);
            return new CommandAddResult(added.Count, null);
        }
    }

    /// <summary>
    /// Remove the roots named <paramref name="remove"/> and append <paramref name="add"/>, all or
    /// nothing, as one swap (a hot-loaded module replacing its previous version, or going away; a
    /// failed refresh undoing its own append). The additions are validated against the roots that
    /// remain. Names that are not in the table are ignored.
    /// </summary>
    public CommandAddResult TryReplace(IReadOnlyCollection<string> remove, IReadOnlyList<ChatCommand> add)
    {
        lock (_gate)
        {
            ChatCommand[] remaining = [.. _current.Roots.Where(c => !remove.Contains(c.Name, StringComparer.OrdinalIgnoreCase))];
            string? error = Validate(remaining, add);
            if (error is not null)
            {
                return new CommandAddResult(0, error);
            }

            _current = new CommandTable([.. remaining, .. add]);
            return new CommandAddResult(add.Count, null);
        }
    }

    /// <summary>
    /// Why appending <paramref name="added"/> to <paramref name="existing"/> would be unsafe, or
    /// null. Checked against every existing root whatever its security level, because a root
    /// hidden from one caller still changes what another caller's abbreviation resolves to.
    /// A new root is rejected when it
    /// <list type="bullet">
    /// <item>has an empty name or a name containing whitespace (it could never be typed);</item>
    /// <item>equals an existing root or another new root (a duplicate root fails startup too);</item>
    /// <item>is a proper prefix of an existing root: it would turn that root's abbreviation into
    /// an exact match for the new command.</item>
    /// </list>
    /// An existing root that is a prefix of a new root is fine: the existing root still wins its
    /// own exact name and the first-match rule for shorter abbreviations.
    /// </summary>
    public static string? Validate(IReadOnlyList<ChatCommand> existing, IReadOnlyList<ChatCommand> added)
    {
        var seen = new HashSet<string>(existing.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (ChatCommand root in added)
        {
            string name = root.Name;
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
            {
                return $"chat command name '{name}' is empty or contains whitespace";
            }

            if (!seen.Add(name))
            {
                return $"chat command '.{name}' is defined twice";
            }

            ChatCommand? shadowed = existing.FirstOrDefault(e =>
                e.Name.Length > name.Length && e.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (shadowed is not null)
            {
                return $"chat command '.{name}' is a prefix of existing '.{shadowed.Name}' and would change how '.{name}' resolves";
            }
        }

        return null;
    }
}
