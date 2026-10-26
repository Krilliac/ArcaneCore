using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// Where one session currently is: the latest <c>presence</c> message of a UI connection, or the last <c>session.join</c> /
/// <c>session.move</c> of an agent session, as the other sessions see it (<c>ui.presence</c> events, the hello
/// <c>sessions</c> list, <c>status.ui.sessions</c> and <c>sessions.list</c> all use <see cref="ToJson"/>).
/// </summary>
/// <param name="Id">The session id: a UI's connection id (the <c>ui:&lt;id&gt;</c> origin without its prefix) or an agent session's id.</param>
/// <param name="Name">The session's display name.</param>
/// <param name="Map">Map id the session is looking at.</param>
/// <param name="X">World x of the session's focus point (the eye in fly mode, else the camera target).</param>
/// <param name="Y">World y of the focus point.</param>
/// <param name="Z">World z of the focus point.</param>
/// <param name="Yaw">Camera yaw in degrees, within [0, 360).</param>
/// <param name="Mode">Camera mode: <c>orbit</c>, <c>fly</c> or <c>top</c>.</param>
/// <param name="Kind"><see cref="KindUi"/> for a browser UI connection, <see cref="KindAgent"/> for a scripted session.</param>
public sealed record UiPresence(string Id, string Name, uint Map, double X, double Y, double Z, double Yaw, string Mode, string Kind = UiPresence.KindUi)
{
    /// <summary>Largest absolute coordinate a presence message may carry.</summary>
    public const double MaxCoordinate = 100_000;

    /// <summary>Longest display name, in chars.</summary>
    public const int MaxNameLength = 32;

    /// <summary><see cref="Kind"/> of a session that is a browser UI connection.</summary>
    public const string KindUi = "ui";

    /// <summary><see cref="Kind"/> of a session created with <c>session.join</c>.</summary>
    public const string KindAgent = "agent";

    /// <summary>The camera modes a presence message may name.</summary>
    public static bool IsMode([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? mode) => mode is "orbit" or "fly" or "top";

    /// <summary>
    /// Trims <paramref name="name"/>, drops control and format characters (bidi overrides, zero-width marks) and unpaired surrogates, and caps it at
    /// <see cref="MaxNameLength"/> chars; null when nothing is left.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var kept = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                kept.Append(c).Append(name[++i]);
            }
            else if (!char.IsSurrogate(c) && !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                // Unpaired surrogates are dropped too: they are not text and cannot be encoded.
                kept.Append(c);
            }
        }

        string cleaned = kept.ToString().Trim();
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength];
            // Never end on half of a surrogate pair.
            if (char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary><c>{id, name, map, at:[x,y,z], yaw, mode, kind}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["map"] = Map,
        ["at"] = new JsonArray(X, Y, Z),
        ["yaw"] = Yaw,
        ["mode"] = Mode,
        ["kind"] = Kind,
    };

    /// <summary>
    /// The name a message asks for. An absent <c>name</c> keeps <paramref name="current"/>; a present one that is empty after
    /// cleaning resets to <paramref name="defaultName"/>. False when <c>name</c> is present but not text.
    /// </summary>
    internal static bool TryResolveName(JsonObject message, string current, string defaultName, out string name)
    {
        name = current;
        if (message["name"] is not { } node)
        {
            return true;
        }

        if (!TryGetText(node, out string? raw))
        {
            return false;
        }

        name = CleanName(raw) ?? defaultName;
        return true;
    }

    /// <summary>
    /// Reads and validates the position fields of a presence message (the socket's <c>presence</c> op and the session commands
    /// share this): <c>map</c> an integer 0..uint.MaxValue, <c>at</c> exactly three finite numbers within
    /// <see cref="MaxCoordinate"/>, <c>yaw</c> finite (wrapped into [0, 360)), <c>mode</c> one of the three modes. A field that is
    /// absent comes from <paramref name="basis"/>; without a basis every field is required.
    /// </summary>
    /// <param name="problem">When false, what is wrong, in a sentence.</param>
    internal static bool TryRead(JsonObject message, UiPresence? basis, string id, string name, string kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UiPresence? presence, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? problem)
    {
        presence = null;
        long map;
        double x;
        double y;
        double z;
        double yaw;
        string? mode;
        if (message["map"] is null && basis is not null)
        {
            map = basis.Map;
        }
        else if (!JsonScalars.TryGetInt64(message["map"], out map) || map < 0 || map > uint.MaxValue)
        {
            problem = "map must be an integer from 0 to 4294967295";
            return false;
        }

        if (message["at"] is null && basis is not null)
        {
            (x, y, z) = (basis.X, basis.Y, basis.Z);
        }
        else if (message["at"] is not JsonArray { Count: 3 } at
            || !TryGetCoordinate(at[0], out x) || !TryGetCoordinate(at[1], out y) || !TryGetCoordinate(at[2], out z))
        {
            (x, y, z) = (0, 0, 0);
            problem = $"at must be three numbers [x, y, z], each within +-{MaxCoordinate.ToString("0", CultureInfo.InvariantCulture)}";
            return false;
        }

        if (message["yaw"] is null && basis is not null)
        {
            yaw = basis.Yaw;
        }
        else if (!JsonScalars.TryGetDouble(message["yaw"], out yaw))
        {
            problem = "yaw must be a finite number of degrees";
            return false;
        }

        if (message["mode"] is null && basis is not null)
        {
            mode = basis.Mode;
        }
        else if (!TryGetText(message["mode"], out mode) || !IsMode(mode))
        {
            problem = "mode must be orbit, fly or top";
            return false;
        }

        double wrapped = yaw % 360.0;
        if (wrapped < 0)
        {
            wrapped += 360.0;
        }

        // A tiny negative yaw can round up to exactly 360 after the add.
        if (wrapped >= 360.0)
        {
            wrapped = 0;
        }

        presence = new UiPresence(id, name, (uint)map, x, y, z, wrapped, mode, kind);
        problem = null;
        return true;
    }

    /// <summary>
    /// A JSON string node's text. A string holding a lone surrogate escape (<c>"\ud800"</c>) parses but cannot be read as text:
    /// <see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/> for it, which here means "not valid".
    /// </summary>
    internal static bool TryGetText(JsonNode? node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        text = null;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            return value.TryGetValue(out text) && text is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetCoordinate(JsonNode? node, out double result)
        => JsonScalars.TryGetDouble(node, out result) && Math.Abs(result) <= MaxCoordinate;
}
