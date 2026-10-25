using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Atlas;

/// <summary>
/// The read-only <c>.atlas</c> leaves that answer with data instead of prose: <c>debug</c> (one creature's aggro, leash, home, spawn, path,
/// threat and AI) and <c>spawns</c> (the spawn points near a point). Each prints exactly one JSON line, so a tool reads the single reply line.
/// World thread only; nothing is changed.
/// </summary>
internal static class AtlasDebugCommands
{
    /// <summary>The rows <c>spawns</c> prints before it says the list was cut.</summary>
    public const int MaxSpawnRows = 500;

    /// <summary>The largest radius <c>spawns</c> accepts (yards); a larger one is a syntax failure.</summary>
    public const float MaxSpawnRadius = 5000f;

    /// <summary><c>.atlas debug 0xGUID</c>.</summary>
    public static bool Debug(CommandContext context, string args)
    {
        string[] tokens = AtlasArgs.Tokens(args);
        if (tokens.Length != 1 || !AtlasArgs.TryGuid(tokens[0], out ulong guid))
        {
            return false;
        }

        if (GmActionContext.FindUnit(context, guid) is not Creature creature)
        {
            context.Fail("target-gone");
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        CreatureOptions options = context.Session.Services.GetRequiredService<CreatureWorldFeature>().Options;
        float aggro = CreatureAggro.GetAttackDistance(creature.Template.Detection, creature.Level, creature.Level, options.AggroRate);
        float leash = MathF.Max(1.5f * aggro, options.ThreatRadius);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("guid", guid.ToString("X16", CultureInfo.InvariantCulture));
            w.WriteNumber("entry", creature.Template.Entry);
            w.WriteNumber("aggroRadius", aggro);
            w.WriteNumber("leash", leash);

            CreatureHome home = creature.Home;
            w.WriteStartObject("home");
            WritePoint(w, home.X, home.Y, home.Z);
            w.WriteEndObject();

            if (creature.Spawn is { } spawn)
            {
                w.WriteStartObject("spawn");
                w.WriteNumber("guid", spawn.Guid);
                WritePoint(w, spawn.X, spawn.Y, spawn.Z);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("spawn");
            }

            w.WriteStartArray("waypoints");
            if (creature.Spline is { } spline)
            {
                foreach (System.Numerics.Vector3 point in spline.Points)
                {
                    w.WriteStartObject();
                    WritePoint(w, point.X, point.Y, point.Z);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();

            w.WriteStartArray("threat");
            if (creature.Combat.HasThreatList)
            {
                foreach (ThreatEntry entry in creature.Combat.Threat.Entries)
                {
                    w.WriteStartObject();
                    w.WriteString("guid", entry.Target.Guid.Value.ToString("X16", CultureInfo.InvariantCulture));
                    w.WriteNumber("threat", entry.Threat);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteString("ai", string.IsNullOrEmpty(creature.Template.ScriptName) ? creature.Template.AIName : creature.Template.ScriptName);
            w.WriteEndObject();
        }

        context.Reply(System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
        return true;
    }

    /// <summary><c>.atlas spawns #map $x $y $radius</c>.</summary>
    public static bool Spawns(CommandContext context, string args)
    {
        string[] tokens = AtlasArgs.Tokens(args);
        if (tokens.Length != 4
            || !AtlasArgs.TryUInt(tokens[0], out uint mapId)
            || !AtlasArgs.TryFloat(tokens[1], out float x) || !AtlasArgs.TryFloat(tokens[2], out float y)
            || !AtlasArgs.TryFloat(tokens[3], out float radius) || radius < 0f || radius > MaxSpawnRadius)
        {
            return false;
        }

        float limit = radius * radius;
        using var stream = new MemoryStream();
        bool truncated = false;
        int rows = 0;
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("rows");

            if (context.Session.Services.GetService<CreatureWorldFeature>() is { } creatures)
            {
                foreach (CreatureSpawn spawn in creatures.Content.GetSpawns(mapId))
                {
                    if (Outside(spawn.X, spawn.Y, x, y, limit))
                    {
                        continue;
                    }

                    if (rows == MaxSpawnRows)
                    {
                        truncated = true;
                        break;
                    }

                    WriteRow(w, "creature", spawn.Guid, spawn.Entry, spawn.X, spawn.Y, spawn.Z);
                    rows++;
                }
            }

            if (!truncated && context.Session.Services.GetService<GameObjectLootFeature>() is { } objects)
            {
                foreach (GameObjectSpawn spawn in objects.Content.GetSpawns(mapId))
                {
                    if (Outside(spawn.X, spawn.Y, x, y, limit))
                    {
                        continue;
                    }

                    if (rows == MaxSpawnRows)
                    {
                        truncated = true;
                        break;
                    }

                    WriteRow(w, "gameobject", spawn.Guid, spawn.Entry, spawn.X, spawn.Y, spawn.Z);
                    rows++;
                }
            }

            w.WriteEndArray();
            w.WriteBoolean("truncated", truncated);
            w.WriteEndObject();
        }

        context.Reply(System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
        return true;
    }

    private static bool Outside(float sx, float sy, float x, float y, float limit)
    {
        float dx = sx - x;
        float dy = sy - y;
        return dx * dx + dy * dy > limit;
    }

    private static void WriteRow(Utf8JsonWriter w, string kind, uint guid, uint entry, float x, float y, float z)
    {
        w.WriteStartObject();
        w.WriteString("kind", kind);
        w.WriteNumber("guid", guid);
        w.WriteNumber("entry", entry);
        WritePoint(w, x, y, z);
        w.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter w, float x, float y, float z)
    {
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteNumber("z", z);
    }
}
