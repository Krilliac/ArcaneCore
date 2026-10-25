using System.Buffers;
using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>One map in the <c>hello</c> reply.</summary>
internal readonly record struct MapSummary(uint MapId, uint InstanceId, int Players, int Creatures);

/// <summary>
/// Writes the server's lines (docs/areas/map-feed.md, "Protocol"). Every writer takes a <see cref="Utf8JsonWriter"/> over a buffer and
/// writes one JSON object, without the newline; the <c>...Line</c> helpers return a finished line for the control messages (rare, so
/// they allocate their bytes). Property names are pre-encoded once. A non-finite number would throw in the writer, so the capture keeps
/// them out of a frame and these writers are only given finite values. Any thread.
/// </summary>
internal static class MapFeedJson
{
    private static readonly JsonEncodedText Op = JsonEncodedText.Encode("op");
    private static readonly JsonEncodedText OpHello = JsonEncodedText.Encode("hello");
    private static readonly JsonEncodedText OpFrame = JsonEncodedText.Encode("frame");
    private static readonly JsonEncodedText OpResult = JsonEncodedText.Encode("result");
    private static readonly JsonEncodedText OpError = JsonEncodedText.Encode("error");
    private static readonly JsonEncodedText Protocol = JsonEncodedText.Encode("protocol");
    private static readonly JsonEncodedText Rev = JsonEncodedText.Encode("rev");
    private static readonly JsonEncodedText CombatFlag = JsonEncodedText.Encode("cb");
    private static readonly JsonEncodedText DisplayIdProperty = JsonEncodedText.Encode("d");
    private static readonly JsonEncodedText Sha = JsonEncodedText.Encode("sha");
    private static readonly JsonEncodedText Pid = JsonEncodedText.Encode("pid");
    private static readonly JsonEncodedText Tools = JsonEncodedText.Encode("tools");
    private static readonly JsonEncodedText Maps = JsonEncodedText.Encode("maps");
    private static readonly JsonEncodedText Map = JsonEncodedText.Encode("map");
    private static readonly JsonEncodedText Instance = JsonEncodedText.Encode("instance");
    private static readonly JsonEncodedText Players = JsonEncodedText.Encode("players");
    private static readonly JsonEncodedText Creatures = JsonEncodedText.Encode("creatures");
    private static readonly JsonEncodedText Seq = JsonEncodedText.Encode("seq");
    private static readonly JsonEncodedText ClockMs = JsonEncodedText.Encode("clockMs");
    private static readonly JsonEncodedText Grids = JsonEncodedText.Encode("grids");
    private static readonly JsonEncodedText Counters = JsonEncodedText.Encode("counters");
    private static readonly JsonEncodedText Entities = JsonEncodedText.Encode("entities");
    private static readonly JsonEncodedText Bots = JsonEncodedText.Encode("bots");
    private static readonly JsonEncodedText GameObjects = JsonEncodedText.Encode("gameobjects");
    private static readonly JsonEncodedText Emitted = JsonEncodedText.Encode("emitted");
    private static readonly JsonEncodedText Truncated = JsonEncodedText.Encode("truncated");
    private static readonly JsonEncodedText NonFinite = JsonEncodedText.Encode("nonFinite");
    private static readonly JsonEncodedText PathsOmitted = JsonEncodedText.Encode("pathsOmitted");
    private static readonly JsonEncodedText Dropped = JsonEncodedText.Encode("dropped");
    private static readonly JsonEncodedText Online = JsonEncodedText.Encode("online");
    private static readonly JsonEncodedText DiffMs = JsonEncodedText.Encode("diffMs");
    private static readonly JsonEncodedText DeferredPaths = JsonEncodedText.Encode("deferredPaths");
    private static readonly JsonEncodedText DirectFallbackPaths = JsonEncodedText.Encode("directFallbackPaths");
    private static readonly JsonEncodedText NoPathPaths = JsonEncodedText.Encode("noPathPaths");
    private static readonly JsonEncodedText HomePathTeleports = JsonEncodedText.Encode("homePathTeleports");
    private static readonly JsonEncodedText Guid = JsonEncodedText.Encode("g");
    private static readonly JsonEncodedText Kind = JsonEncodedText.Encode("k");
    private static readonly JsonEncodedText Entry = JsonEncodedText.Encode("e");
    private static readonly JsonEncodedText Name = JsonEncodedText.Encode("n");
    private static readonly JsonEncodedText X = JsonEncodedText.Encode("x");
    private static readonly JsonEncodedText Y = JsonEncodedText.Encode("y");
    private static readonly JsonEncodedText Z = JsonEncodedText.Encode("z");
    private static readonly JsonEncodedText Orientation = JsonEncodedText.Encode("o");
    private static readonly JsonEncodedText Health = JsonEncodedText.Encode("hp");
    private static readonly JsonEncodedText Motion = JsonEncodedText.Encode("mt");
    private static readonly JsonEncodedText Target = JsonEncodedText.Encode("tg");
    private static readonly JsonEncodedText Home = JsonEncodedText.Encode("home");
    private static readonly JsonEncodedText Spline = JsonEncodedText.Encode("sp");
    private static readonly JsonEncodedText Route = JsonEncodedText.Encode("route");
    private static readonly JsonEncodedText Id = JsonEncodedText.Encode("id");
    private static readonly JsonEncodedText Start = JsonEncodedText.Encode("start");
    private static readonly JsonEncodedText Points = JsonEncodedText.Encode("pts");
    private static readonly JsonEncodedText T0 = JsonEncodedText.Encode("t0");
    private static readonly JsonEncodedText Duration = JsonEncodedText.Encode("dur");
    private static readonly JsonEncodedText Run = JsonEncodedText.Encode("run");
    private static readonly JsonEncodedText Cut = JsonEncodedText.Encode("cut");
    private static readonly JsonEncodedText Next = JsonEncodedText.Encode("next");
    private static readonly JsonEncodedText Flags = JsonEncodedText.Encode("flags");
    private static readonly JsonEncodedText Ok = JsonEncodedText.Encode("ok");
    private static readonly JsonEncodedText Code = JsonEncodedText.Encode("code");
    private static readonly JsonEncodedText Message = JsonEncodedText.Encode("message");
    private static readonly JsonEncodedText KindProperty = JsonEncodedText.Encode("kind");
    private static readonly JsonEncodedText Clear = JsonEncodedText.Encode("clear");
    private static readonly JsonEncodedText ClearWithDoodads = JsonEncodedText.Encode("clearDoodads");
    private static readonly JsonEncodedText Hit = JsonEncodedText.Encode("hit");
    private static readonly JsonEncodedText Data = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText Terrain = JsonEncodedText.Encode("terrain");
    private static readonly JsonEncodedText Model = JsonEncodedText.Encode("model");
    private static readonly JsonEncodedText Floor = JsonEncodedText.Encode("floor");
    private static readonly JsonEncodedText Outdoors = JsonEncodedText.Encode("outdoors");
    private static readonly JsonEncodedText Type = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText HasPath = JsonEncodedText.Encode("hasPath");
    private static readonly JsonEncodedText Length = JsonEncodedText.Encode("length");
    private static readonly JsonEncodedText Verdict = JsonEncodedText.Encode("verdict");
    private static readonly JsonEncodedText Depth = JsonEncodedText.Encode("depth");

    private static readonly JsonEncodedText KindPlayer = JsonEncodedText.Encode("player");
    private static readonly JsonEncodedText KindBot = JsonEncodedText.Encode("bot");
    private static readonly JsonEncodedText KindCreature = JsonEncodedText.Encode("creature");
    private static readonly JsonEncodedText KindGameObject = JsonEncodedText.Encode("gameobject");

    private static readonly JsonEncodedText MotionIdle = JsonEncodedText.Encode("idle");
    private static readonly JsonEncodedText MotionRandom = JsonEncodedText.Encode("random");
    private static readonly JsonEncodedText MotionWaypoint = JsonEncodedText.Encode("waypoint");
    private static readonly JsonEncodedText MotionConfused = JsonEncodedText.Encode("confused");
    private static readonly JsonEncodedText MotionChase = JsonEncodedText.Encode("chase");
    private static readonly JsonEncodedText MotionHome = JsonEncodedText.Encode("home");
    private static readonly JsonEncodedText MotionPoint = JsonEncodedText.Encode("point");
    private static readonly JsonEncodedText MotionFleeing = JsonEncodedText.Encode("fleeing");
    private static readonly JsonEncodedText MotionDistract = JsonEncodedText.Encode("distract");
    private static readonly JsonEncodedText MotionFollow = JsonEncodedText.Encode("follow");
    private static readonly JsonEncodedText MotionFormation = JsonEncodedText.Encode("formation");
    private static readonly JsonEncodedText MotionUnknown = JsonEncodedText.Encode("unknown");

    private static readonly JsonEncodedText[] RouteFlagNames =
    [
        JsonEncodedText.Encode("meshEndedShort"),
        JsonEncodedText.Encode("partialToGoal"),
        JsonEncodedText.Encode("chase"),
        JsonEncodedText.Encode("navigated"),
        JsonEncodedText.Encode("questReturn"),
    ];

    private static readonly (PathType Flag, JsonEncodedText Name)[] PathTypeNames =
    [
        (PathType.Normal, JsonEncodedText.Encode("normal")),
        (PathType.Shortcut, JsonEncodedText.Encode("shortcut")),
        (PathType.Incomplete, JsonEncodedText.Encode("incomplete")),
        (PathType.NoPath, JsonEncodedText.Encode("noPath")),
        (PathType.NotUsingPath, JsonEncodedText.Encode("notUsingPath")),
        (PathType.DestForced, JsonEncodedText.Encode("destForced")),
        (PathType.FlyPath, JsonEncodedText.Encode("flyPath")),
        (PathType.Underwater, JsonEncodedText.Encode("underwater")),
        (PathType.Caster, JsonEncodedText.Encode("caster")),
        (PathType.Short, JsonEncodedText.Encode("short")),
        (PathType.TilesPending, JsonEncodedText.Encode("tilesPending")),
    ];

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>The <c>hello</c> reply; <c>"tools":true</c> only when the packet tools are on (a daemon without them says nothing).</summary>
    public static void WriteHello(Utf8JsonWriter writer, string sha, int pid, ReadOnlySpan<MapSummary> maps, bool tools = false)
    {
        writer.WriteStartObject();
        writer.WriteString(Op, OpHello);
        writer.WriteNumber(Protocol, MapFeedProtocol.Version);
        writer.WriteNumber(Rev, MapFeedProtocol.Revision);
        writer.WriteString(Sha, sha);
        writer.WriteNumber(Pid, pid);
        if (tools)
        {
            writer.WriteBoolean(Tools, true);
        }

        writer.WriteStartArray(Maps);
        foreach (MapSummary map in maps)
        {
            writer.WriteStartObject();
            writer.WriteNumber(Map, map.MapId);
            writer.WriteNumber(Instance, map.InstanceId);
            writer.WriteNumber(Players, map.Players);
            writer.WriteNumber(Creatures, map.Creatures);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>An <c>error</c> line.</summary>
    public static void WriteError(Utf8JsonWriter writer, string code, string message)
    {
        writer.WriteStartObject();
        writer.WriteString(Op, OpError);
        writer.WriteString(Code, code);
        writer.WriteString(Message, message);
        writer.WriteEndObject();
    }

    /// <summary>The finished <c>hello</c> line, newline included.</summary>
    public static byte[] HelloLine(string sha, int pid, ReadOnlySpan<MapSummary> maps, bool tools = false)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteHello(writer, sha, pid, maps, tools);
        }

        return Finish(buffer);
    }

    /// <summary>The finished <c>error</c> line, newline included.</summary>
    public static byte[] ErrorLine(string code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteError(writer, code, message);
        }

        return Finish(buffer);
    }

    /// <summary>The finished <c>result</c> line, newline included.</summary>
    public static byte[] ResultLine(QueryAnswer answer)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteResult(writer, answer);
        }

        return Finish(buffer);
    }

    internal static byte[] Finish(ArrayBufferWriter<byte> buffer)
    {
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A <c>result</c> line: the answer, or the refusal when it is not <see cref="QueryAnswer.Ok"/>.</summary>
    public static void WriteResult(Utf8JsonWriter writer, QueryAnswer answer)
    {
        writer.WriteStartObject();
        writer.WriteString(Op, OpResult);
        writer.WriteNumber(Id, answer.Id);
        writer.WriteBoolean(Ok, answer.Ok);
        if (!answer.Ok)
        {
            writer.WriteString(Code, answer.Code);
            writer.WriteString(Message, answer.Message);
            writer.WriteEndObject();
            return;
        }

        switch (answer.Kind)
        {
            case QueryKind.LineOfSight:
                writer.WriteString(KindProperty, "los"u8);
                writer.WriteBoolean(Clear, answer.Clear);
                writer.WriteBoolean(ClearWithDoodads, answer.ClearWithDoodads);
                writer.WriteBoolean(Data, answer.DataBacked);
                if (answer.HasHit)
                {
                    WritePoint(writer, Hit, answer.Hit);
                }

                break;
            case QueryKind.Height:
                writer.WriteString(KindProperty, "height"u8);
                writer.WriteBoolean(Data, answer.DataBacked);
                WriteOptional(writer, Terrain, answer.Terrain);
                WriteOptional(writer, Model, answer.Model);
                WriteOptional(writer, Floor, answer.Floor);
                if (answer.Outdoors is { } outdoors)
                {
                    writer.WriteBoolean(Outdoors, outdoors);
                }
                else
                {
                    writer.WriteNull(Outdoors);
                }

                break;
            case QueryKind.Path:
                writer.WriteString(KindProperty, "path"u8);
                writer.WriteBoolean(Data, answer.DataBacked);
                writer.WriteBoolean(HasPath, answer.HasPath);
                writer.WriteStartArray(Type);
                foreach ((PathType flag, JsonEncodedText name) in PathTypeNames)
                {
                    if ((answer.PathType & flag) != 0)
                    {
                        writer.WriteStringValue(name);
                    }
                }

                writer.WriteEndArray();
                writer.WriteNumber(Length, answer.Length);
                writer.WriteStartArray(Points);
                foreach (Vector3 point in answer.Points)
                {
                    WritePointValue(writer, point);
                }

                writer.WriteEndArray();
                if (answer.Cut)
                {
                    writer.WriteBoolean(Cut, true);
                }

                break;
            default:
                writer.WriteString(KindProperty, "undermap"u8);
                writer.WriteString(Verdict, answer.Verdict switch
                {
                    UnderMapKind.Ok => "ok"u8,
                    UnderMapKind.UnderMap => "undermap"u8,
                    _ => "unknown"u8,
                });
                if (answer.Verdict == UnderMapKind.UnderMap)
                {
                    writer.WriteNumber(Depth, answer.Depth);
                }

                break;
        }

        writer.WriteEndObject();
    }

    /// <summary>One <c>frame</c> line. <paramref name="sequence"/> and <paramref name="dropped"/> belong to the connection.</summary>
    public static void WriteFrame(Utf8JsonWriter writer, FeedFrame frame, long sequence, long dropped)
    {
        writer.WriteStartObject();
        writer.WriteString(Op, OpFrame);
        writer.WriteNumber(Seq, sequence);
        writer.WriteNumber(ClockMs, frame.ClockMs);
        writer.WriteNumber(Map, frame.MapId);
        writer.WriteNumber(Instance, frame.InstanceId);
        writer.WriteStartArray(Grids);
        for (int i = 0; i < frame.GridCount; i++)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(frame.Grids[i].X);
            writer.WriteNumberValue(frame.Grids[i].Y);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        WriteCounters(writer, frame, dropped);
        writer.WriteStartArray(Entities);
        for (int i = 0; i < frame.EntityCount; i++)
        {
            WriteEntity(writer, frame, ref frame.Entities[i]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCounters(Utf8JsonWriter writer, FeedFrame frame, long dropped)
    {
        ref readonly FeedCounters counters = ref frame.Counters;
        writer.WriteStartObject(Counters);
        writer.WriteNumber(Players, counters.Players);
        writer.WriteNumber(Bots, counters.Bots);
        writer.WriteNumber(Creatures, counters.Creatures);
        writer.WriteNumber(GameObjects, counters.GameObjects);
        writer.WriteNumber(Emitted, frame.EntityCount);
        writer.WriteNumber(Truncated, counters.Truncated);
        writer.WriteNumber(NonFinite, counters.NonFinite);
        writer.WriteNumber(PathsOmitted, counters.PathsOmitted);
        writer.WriteNumber(Dropped, dropped);
        writer.WriteNumber(Online, counters.Online);
        writer.WriteNumber(DiffMs, counters.DiffMs);
        writer.WriteNumber(DeferredPaths, counters.DeferredPaths);
        writer.WriteNumber(DirectFallbackPaths, counters.DirectFallbackPaths);
        writer.WriteNumber(NoPathPaths, counters.NoPathPaths);
        writer.WriteNumber(HomePathTeleports, counters.HomePathTeleports);
        writer.WriteEndObject();
    }

    private static void WriteEntity(Utf8JsonWriter writer, FeedFrame frame, ref FeedEntity entity)
    {
        writer.WriteStartObject();
        WriteGuid(writer, Guid, entity.Guid);
        writer.WriteString(Kind, entity.Kind switch
        {
            FeedEntityKind.Player => KindPlayer,
            FeedEntityKind.Bot => KindBot,
            FeedEntityKind.Creature => KindCreature,
            _ => KindGameObject,
        });
        if (entity.Kind is FeedEntityKind.Creature or FeedEntityKind.GameObject)
        {
            writer.WriteNumber(Entry, entity.Entry);
        }

        writer.WriteString(Name, entity.Name ?? string.Empty);
        writer.WriteNumber(X, entity.X);
        writer.WriteNumber(Y, entity.Y);
        writer.WriteNumber(Z, entity.Z);
        writer.WriteNumber(Orientation, entity.Orientation);
        if ((entity.Parts & FeedEntityParts.Health) != 0)
        {
            writer.WriteNumber(Health, entity.HealthPercent);
        }

        if (entity.InCombat)
        {
            writer.WriteBoolean(CombatFlag, true);
        }

        if (entity.DisplayId != 0)
        {
            writer.WriteNumber(DisplayIdProperty, entity.DisplayId);
        }

        if (entity.Kind == FeedEntityKind.Creature)
        {
            writer.WriteString(Motion, MotionName(entity.Motion));
        }

        if ((entity.Parts & FeedEntityParts.Target) != 0)
        {
            WriteGuid(writer, Target, entity.Target);
        }

        if ((entity.Parts & FeedEntityParts.Home) != 0)
        {
            WritePoint(writer, Home, new Vector3(entity.HomeX, entity.HomeY, entity.HomeZ));
        }

        if ((entity.Parts & FeedEntityParts.Spline) != 0)
        {
            WriteSpline(writer, frame, ref entity);
        }

        if ((entity.Parts & FeedEntityParts.Route) != 0)
        {
            WriteRoute(writer, frame, ref entity);
        }

        writer.WriteEndObject();
    }

    private static void WriteSpline(Utf8JsonWriter writer, FeedFrame frame, ref FeedEntity entity)
    {
        writer.WriteStartObject(Spline);
        writer.WriteNumber(Id, entity.SplineId);
        WritePoint(writer, Start, new Vector3(entity.SplineStartX, entity.SplineStartY, entity.SplineStartZ));
        writer.WriteStartArray(Points);
        for (int i = 0; i < entity.SplineCount; i++)
        {
            WritePointValue(writer, frame.Points[entity.SplineOffset + i]);
        }

        writer.WriteEndArray();
        writer.WriteNumber(T0, entity.SplineT0);
        writer.WriteNumber(Duration, entity.SplineDuration);
        writer.WriteBoolean(Run, entity.SplineRun);
        if ((entity.Parts & FeedEntityParts.SplineCut) != 0)
        {
            writer.WriteBoolean(Cut, true);
        }

        writer.WriteEndObject();
    }

    private static void WriteRoute(Utf8JsonWriter writer, FeedFrame frame, ref FeedEntity entity)
    {
        writer.WriteStartObject(Route);
        writer.WriteStartArray(Points);
        for (int i = 0; i < entity.RouteCount; i++)
        {
            WritePointValue(writer, frame.Points[entity.RouteOffset + i]);
        }

        writer.WriteEndArray();
        writer.WriteNumber(Next, entity.RouteNext);
        writer.WriteStartArray(Flags);
        for (int bit = 0; bit < RouteFlagNames.Length; bit++)
        {
            if (((int)entity.RouteFlags & (1 << bit)) != 0)
            {
                writer.WriteStringValue(RouteFlagNames[bit]);
            }
        }

        writer.WriteEndArray();
        if ((entity.Parts & FeedEntityParts.RouteCut) != 0)
        {
            writer.WriteBoolean(Cut, true);
        }

        writer.WriteEndObject();
    }

    private static JsonEncodedText MotionName(byte type) => (MovementGeneratorType)type switch
    {
        MovementGeneratorType.Idle => MotionIdle,
        MovementGeneratorType.Random => MotionRandom,
        MovementGeneratorType.Waypoint => MotionWaypoint,
        MovementGeneratorType.Confused => MotionConfused,
        MovementGeneratorType.Chase => MotionChase,
        MovementGeneratorType.Home => MotionHome,
        MovementGeneratorType.Point => MotionPoint,
        MovementGeneratorType.Fleeing => MotionFleeing,
        MovementGeneratorType.Distract => MotionDistract,
        MovementGeneratorType.Follow => MotionFollow,
        MovementGeneratorType.Formation => MotionFormation,
        _ => MotionUnknown,
    };

    /// <summary>A GUID as 16 upper-case hex digits (no prefix), the way the server logs it.</summary>
    private static void WriteGuid(Utf8JsonWriter writer, JsonEncodedText property, ulong value)
    {
        Span<byte> digits = stackalloc byte[16];
        for (int i = 15; i >= 0; i--)
        {
            int nibble = (int)(value & 0xF);
            digits[i] = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
            value >>= 4;
        }

        writer.WriteString(property, digits);
    }

    private static void WriteOptional(Utf8JsonWriter writer, JsonEncodedText property, float? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(property, number);
        }
        else
        {
            writer.WriteNull(property);
        }
    }

    private static void WritePoint(Utf8JsonWriter writer, JsonEncodedText property, Vector3 point)
    {
        writer.WriteStartArray(property);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteNumberValue(point.Z);
        writer.WriteEndArray();
    }

    private static void WritePointValue(Utf8JsonWriter writer, Vector3 point)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteNumberValue(point.Z);
        writer.WriteEndArray();
    }
}
