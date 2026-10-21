using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// A saved camera position. <see cref="Source"/> is the repository file and line the coordinates were taken from for
/// a built-in bookmark, and null for one the user saved.
/// </summary>
public sealed record Bookmark(string Name, uint Map, WorldPoint Target, double Distance, double Yaw, double Pitch, CameraMode Mode, string? Note, string? Source)
{
    public bool BuiltIn => Source is not null;

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["name"] = Name,
            ["map"] = Map,
            ["at"] = HostJson.Point(Target),
            ["distance"] = Distance,
            ["yaw"] = Yaw,
            ["pitch"] = Pitch,
            ["mode"] = ViewState.Name(Mode),
            ["builtin"] = BuiltIn,
        };
        if (Note is not null)
        {
            json["note"] = Note;
        }

        if (Source is not null)
        {
            json["source"] = Source;
        }

        return json;
    }

    /// <summary>The <c>view.set</c> patch that moves the camera here.</summary>
    public JsonObject ToPatch() => new()
    {
        ["map"] = Map,
        ["camera"] = new JsonObject
        {
            ["target"] = HostJson.Point(Target),
            ["distance"] = Distance,
            ["yaw"] = Yaw,
            ["pitch"] = Pitch,
            ["mode"] = ViewState.Name(Mode),
        },
    };

    /// <summary>Whether <paramref name="name"/> is usable: 1 to 64 letters, digits, spaces and . _ - ' characters.</summary>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length is >= 1 and <= 64 && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '\'');
    }
}

/// <summary>
/// Bookmarks that ship with the host. Every coordinate is copied from this repository (the file and line is the
/// bookmark's <see cref="Bookmark.Source"/>, and a test re-reads that line), never from memory or client data.
/// </summary>
public static class BuiltInBookmarks
{
    private const uint EasternKingdoms = 0;
    private const uint Kalimdor = 1;

    public static IReadOnlyList<Bookmark> All { get; } =
    [
        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57 (Farley, innkeeper inside the Lion's Pride Inn; EasternKingdoms = 0 at :30)
        At("goldshire-inn", EasternKingdoms, -9462.66, 16.1915, 57.0459, 45, "Inside the Lion's Pride Inn, Goldshire; the real-terrain test anchor.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:51 (Llane Beshere, inside Northshire Abbey)
        At("northshire-abbey", EasternKingdoms, -8918.36, -208.411, 82.3088, 45, "Inside Northshire Abbey, where the WMO floor sits under the terrain.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:51"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:40 (human start position)
        At("northshire-start", EasternKingdoms, -8949.95, -132.493, 83.5312, 120, "Human start position in Northshire Valley.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:40"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:48 (undead start inside the Deathknell crypt; map 0 as :124 queries it)
        At("deathknell-crypt", EasternKingdoms, 1676.35, 1677.45, 121.67, 45, "Undead start inside the Deathknell crypt, below the terrain surface.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:48"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:41 (dwarf and gnome start position)
        At("coldridge-valley", EasternKingdoms, -6240.32, 331.033, 382.758, 120, "Dwarf and gnome start position in Coldridge Valley.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:41"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:42 (orc and troll start position; Kalimdor = 1 at :31)
        At("valley-of-trials", Kalimdor, -618.518, -4251.67, 38.718, 120, "Orc and troll start position in the Valley of Trials.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:42"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:43 (night elf start position)
        At("shadowglen", Kalimdor, 10311.3, 831.463, 1326.41, 120, "Night elf start position in Shadowglen, Teldrassil.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:43"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:44 (tauren start position)
        At("camp-narache", Kalimdor, -2917.58, -257.98, 52.9968, 120, "Tauren start position at Camp Narache, Mulgore.", "tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:44"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/UnderMapRealTerrainTests.cs:33 (an Ironforge floor point that is not under the map; map 0u)
        At("ironforge", EasternKingdoms, -5010.6, -1060.9, 502.0, 120, "A walkable Ironforge floor point: stacked storeys of WMO floors over one terrain surface.", "tests/ArcaneCore.Game.Tests/Collision/UnderMapRealTerrainTests.cs:33"),

        // ref: tests/ArcaneCore.Game.Tests/Collision/UnderMapRealTerrainTests.cs:36 (an Undercity floor point that is not under the map; map 0u)
        At("undercity", EasternKingdoms, 1475.6, 376.3, -59.4, 120, "A walkable Undercity floor point, far below the terrain surface.", "tests/ArcaneCore.Game.Tests/Collision/UnderMapRealTerrainTests.cs:36"),

        // ref: tests/ArcaneCore.World.Tests/Playerbots/Elevator/PlayerbotElevatorRealTerrainTests.cs:158 (ElevatorCar low stop; map 0 is ElevatorCar.MapId, src/ArcaneCore.Playerbots/PlayerbotElevator.cs:34)
        At("undercity-lift", EasternKingdoms, 1596.22, 291.798, -40.76, 60, "Bottom stop of the Undercity lift; the car travels to z 55.65.", "tests/ArcaneCore.World.Tests/Playerbots/Elevator/PlayerbotElevatorRealTerrainTests.cs:158"),

        // ref: src/ArcaneCore.Game/Holidays/FireworksShow.cs:140 (Booty Bay launch point; the same area is queried as map 0 at tests/ArcaneCore.Game.Tests/Collision/VMapNativeOracleTests.cs:27)
        At("booty-bay", EasternKingdoms, -14358.03, 515.058, 34.2664, 140, "Booty Bay harbour, from the fireworks show launch points.", "src/ArcaneCore.Game/Holidays/FireworksShow.cs:140"),

        // ref: tests/ArcaneCore.World.Tests/Playerbots/Elevator/PlayerbotElevatorRealTerrainTests.cs:51 (top of the Thunder Bluff lift; Kalimdor = 1 at tests/ArcaneCore.World.Tests/Playerbots/Elevator/ElevatorTestData.cs:15)
        At("thunder-bluff-mesa", Kalimdor, -1280, 120, 131, 140, "Thunder Bluff mesa, the top of the lifts.", "tests/ArcaneCore.World.Tests/Playerbots/Elevator/PlayerbotElevatorRealTerrainTests.cs:51"),

        // ref: tests/ArcaneCore.World.Tests/Playerbots/PlayerbotRuttheranRecoveryTerrainTests.cs:42 (WorldSafeLocs.dbc row 129, map 1)
        At("ruttheran-village", Kalimdor, 8706.0, 965.0, 13.27, 120, "Rut'theran Village, at the foot of the Teldrassil portal.", "tests/ArcaneCore.World.Tests/Playerbots/PlayerbotRuttheranRecoveryTerrainTests.cs:42"),
    ];

    private static Bookmark At(string name, uint map, double x, double y, double z, double distance, string note, string source)
        => new(name, map, new WorldPoint(x, y, z), distance, 45, 45, CameraMode.Orbit, note, source);
}

/// <summary>
/// The built-in bookmarks plus the user's saved ones, persisted in <see cref="AtlasState.BookmarksFile"/>. Thread-safe;
/// every write replaces the file atomically. A file that cannot be read is kept aside (renamed) before the next save so
/// it is never silently overwritten, and the reason is exposed as <see cref="LoadProblem"/>.
/// </summary>
public sealed class BookmarkStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _file;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly List<Bookmark> _saved = [];

    public BookmarkStore(string file, ILoggerFactory loggers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(loggers);
        _file = file;
        _log = loggers.CreateLogger("ArcaneCore.Atlas.Bookmarks");
        Load();
    }

    /// <summary>Why the saved bookmarks could not be read, or null.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Built-in bookmarks first, then saved ones in the order they were added.</summary>
    public IReadOnlyList<Bookmark> All()
    {
        lock (_gate)
        {
            return [.. BuiltInBookmarks.All, .. _saved];
        }
    }

    public bool TryGet(string name, out Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            bookmark = BuiltInBookmarks.All.Concat(_saved).FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))!;
            return bookmark is not null;
        }
    }

    /// <summary>Saves a bookmark, replacing a saved one of the same name. A built-in name is refused.</summary>
    public void Save(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (!Bookmark.IsValidName(bookmark.Name))
        {
            throw new CommandException(CommandException.BadArguments, $"Bookmark name '{bookmark.Name}' must be 1 to 64 letters, digits, spaces or . _ - ' characters and start with a letter or digit.");
        }

        lock (_gate)
        {
            if (BuiltInBookmarks.All.Any(b => string.Equals(b.Name, bookmark.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CommandException(CommandException.BadArguments, $"'{bookmark.Name}' is a built-in bookmark and cannot be replaced.");
            }

            _saved.RemoveAll(b => string.Equals(b.Name, bookmark.Name, StringComparison.OrdinalIgnoreCase));
            _saved.Add(bookmark with { Source = null });
            Persist();
        }
    }

    /// <summary>Removes a saved bookmark; false when none has that name. A built-in name is refused.</summary>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (BuiltInBookmarks.All.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CommandException(CommandException.BadArguments, $"'{name}' is a built-in bookmark and cannot be removed.");
            }

            if (_saved.RemoveAll(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_file))
        {
            return;
        }

        try
        {
            JsonObject root = JsonNode.Parse(File.ReadAllText(_file), nodeOptions: null, HostJson.Strict)?.AsObject() ?? throw new InvalidDataException("the file is not a JSON object");
            if (root["bookmarks"] is not JsonArray list)
            {
                throw new InvalidDataException("the file has no 'bookmarks' array");
            }

            foreach (JsonNode? node in list)
            {
                _saved.Add(Parse(node as JsonObject ?? throw new InvalidDataException("a bookmark entry is not an object")));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or FormatException or CommandException)
        {
            _saved.Clear();
            LoadProblem = $"Saved bookmarks in '{_file}' could not be read ({e.Message}); the file is kept aside when a bookmark is next saved.";
            _log.LogWarning("{Problem}", LoadProblem);
        }
    }

    private static Bookmark Parse(JsonObject entry)
    {
        string name = entry["name"]?.GetValue<string>() ?? throw new InvalidDataException("a bookmark has no name");
        if (!Bookmark.IsValidName(name))
        {
            throw new InvalidDataException($"bookmark name '{name}' is not valid");
        }

        if (!JsonScalars.TryGetInt64(entry["map"], out long map) || map is < 0 or > uint.MaxValue)
        {
            throw new InvalidDataException($"bookmark '{name}' has no valid map");
        }

        double[] at = Numbers(entry["at"], 3, name, "at");
        if (!JsonScalars.TryGetDouble(entry["distance"], out double distance) || distance < CameraState.MinDistance || distance > CameraState.MaxDistance
            || !JsonScalars.TryGetDouble(entry["yaw"], out double yaw) || !JsonScalars.TryGetDouble(entry["pitch"], out double pitch)
            || Math.Abs(pitch) > CameraState.MaxPitch)
        {
            throw new InvalidDataException($"bookmark '{name}' has an invalid camera");
        }

        CameraMode mode = Enum.TryParse(entry["mode"]?.GetValue<string>(), ignoreCase: true, out CameraMode parsed) ? parsed : throw new InvalidDataException($"bookmark '{name}' has an invalid mode");
        return new Bookmark(name, (uint)map, new WorldPoint(at[0], at[1], at[2]), distance, yaw, pitch, mode, entry["note"]?.GetValue<string>(), null);
    }

    private static double[] Numbers(JsonNode? node, int count, string name, string field)
    {
        if (node is JsonArray array && array.Count == count)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (!JsonScalars.TryGetDouble(array[i], out values[i]))
                {
                    throw new InvalidDataException($"bookmark '{name}' has a non-numeric {field}");
                }
            }

            return values;
        }

        throw new InvalidDataException($"bookmark '{name}' needs {count} numbers in {field}");
    }

    private void Persist()
    {
        var list = new JsonArray();
        foreach (Bookmark b in _saved)
        {
            JsonObject entry = b.ToJson();
            entry.Remove("builtin");
            list.Add(entry);
        }

        string text = new JsonObject { ["version"] = 1, ["bookmarks"] = list }.ToJsonString(Indented);
        try
        {
            string? directory = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (LoadProblem is not null && File.Exists(_file))
            {
                string aside = $"{_file}.unreadable-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
                File.Move(_file, aside, overwrite: true);
                LoadProblem = null;
            }

            string temp = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CommandException(CommandException.Failed, $"Bookmarks could not be saved to '{_file}': {e.Message}", e);
        }
    }
}
