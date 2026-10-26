using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Writes the result lines of the live-insight queries (docs/areas/atlas-insight.md). They never go through <see cref="MapFeedJson.WriteResult"/>,
/// whose last branch would call any kind it does not know an under-map answer. A refusal is the one thing written by the shared writer
/// (it writes only a code and a message, no kind).
/// </summary>
internal static class MapFeedInsightJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>The finished line (with its newline) for <paramref name="answer"/>; a refusal is written by <see cref="MapFeedJson.ResultLine"/>.</summary>
    public static byte[] ResultLine(InsightAnswer answer)
    {
        if (!answer.Ok || (answer.Inspect is null && answer.Anomalies is null))
        {
            return MapFeedJson.ResultLine(QueryAnswer.Refused(answer.Id, answer.Ok ? MapFeedProtocol.BadArgs : answer.Code, answer.Ok ? "no answer" : answer.Message));
        }

        var buffer = new ArrayBufferWriter<byte>(answer.Anomalies is { } a ? 256 + (a.Rows.Length * 220) : 1024);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "result");
            writer.WriteNumber("id", answer.Id);
            writer.WriteBoolean("ok", true);
            if (answer.Inspect is { } inspect)
            {
                writer.WriteString("kind", "inspect");
                WriteInspect(writer, inspect);
            }
            else
            {
                writer.WriteString("kind", "anomalies");
                WriteAnomalies(writer, answer.Anomalies!);
            }

            writer.WriteEndObject();
        }

        return MapFeedJson.Finish(buffer);
    }

    private static string Hex(ulong guid) => guid.ToString("X16", CultureInfo.InvariantCulture);

    private static void WritePoint(Utf8JsonWriter writer, string name, Vector3 point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(Finite(point.X));
        writer.WriteNumberValue(Finite(point.Y));
        writer.WriteNumberValue(Finite(point.Z));
        writer.WriteEndArray();
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

    private static void WriteGuidOrNull(Utf8JsonWriter writer, string name, ulong guid)
    {
        if (guid == 0)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, Hex(guid));
        }
    }

    private static void WriteInspect(Utf8JsonWriter writer, InspectData d)
    {
        writer.WriteString("guid", Hex(d.Guid));
        writer.WriteString("entity", d.EntityKind);
        writer.WriteNumber("entry", d.Entry);
        writer.WriteString("name", d.Name);
        writer.WriteNumber("map", d.MapId);
        writer.WriteNumber("instance", d.InstanceId);
        WritePoint(writer, "p", d.Position);
        writer.WriteBoolean("unit", d.IsUnit);
        if (!d.IsUnit)
        {
            return;
        }

        writer.WriteNumber("level", d.Level);
        writer.WriteNumber("hp", d.Health);
        writer.WriteNumber("maxHp", d.MaxHealth);
        writer.WriteStartObject("power");
        writer.WriteString("type", d.PowerType);
        writer.WriteNumber("value", d.Power);
        writer.WriteNumber("max", d.MaxPower);
        writer.WriteEndObject();
        writer.WriteNumber("faction", d.Faction);
        writer.WriteNumber("flags", d.UnitFlags);
        writer.WriteString("stand", d.StandState);
        writer.WriteBoolean("combat", d.InCombat);
        WriteGuidOrNull(writer, "target", d.Target);
        WriteGuidOrNull(writer, "victim", d.Victim);

        writer.WriteStartArray("threat");
        foreach (InspectThreat threat in d.Threat)
        {
            writer.WriteStartObject();
            writer.WriteString("g", Hex(threat.Guid));
            if (threat.Name is null)
            {
                writer.WriteNull("name");
            }
            else
            {
                writer.WriteString("name", threat.Name);
            }

            writer.WriteNumber("t", Finite(threat.Threat));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteBoolean("aurasAvailable", d.AurasAvailable);
        writer.WriteStartArray("auras");
        foreach (InspectAura aura in d.Auras)
        {
            writer.WriteStartObject();
            writer.WriteNumber("spell", aura.SpellId);
            writer.WriteNumber("stacks", aura.Stacks);
            writer.WriteNumber("remainingMs", aura.RemainingMs);
            writer.WriteNumber("durationMs", aura.DurationMs);
            WriteGuidOrNull(writer, "caster", aura.Caster);
            writer.WriteBoolean("positive", aura.Positive);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (d.MotionCurrent is null)
        {
            writer.WriteNull("motion");
        }
        else
        {
            writer.WriteStartObject("motion");
            writer.WriteString("current", d.MotionCurrent);
            writer.WriteString("default", d.MotionDefault);
            writer.WriteStartArray("stack");
            foreach (string type in d.MotionStack)
            {
                writer.WriteStringValue(type);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (d.Ai is null)
        {
            writer.WriteNull("ai");
        }
        else
        {
            writer.WriteString("ai", d.Ai);
        }

        if (d.AiName is null)
        {
            writer.WriteNull("aiName");
        }
        else
        {
            writer.WriteString("aiName", d.AiName);
        }

        if (d.Bot is not { } bot)
        {
            writer.WriteNull("bot");
        }
        else
        {
            writer.WriteStartObject("bot");
            writer.WriteNumber("routePoints", bot.RoutePoints);
            writer.WriteNumber("next", bot.Next);
            writer.WriteStartArray("flags");
            foreach (string flag in bot.Flags)
            {
                writer.WriteStringValue(flag);
            }

            writer.WriteEndArray();
            if (bot.Goal is { } goal)
            {
                WritePoint(writer, "goal", goal);
            }
            else
            {
                writer.WriteNull("goal");
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteAnomalies(Utf8JsonWriter writer, AnomalyData d)
    {
        writer.WriteNumber("latest", d.Latest);
        writer.WriteNumber("lost", d.Lost);
        if (d.Reset)
        {
            writer.WriteBoolean("reset", true);
        }

        writer.WriteStartArray("items");
        foreach (AnomalyRow row in d.Rows)
        {
            AnomalySlot slot = row.Slot;
            writer.WriteStartObject();
            writer.WriteNumber("seq", slot.Seq);
            writer.WriteNumber("t", slot.At.ToUnixTimeMilliseconds());
            writer.WriteString("key", slot.Key);
            writer.WriteString("subsystem", slot.Subsystem);
            writer.WriteString("code", slot.Code);
            writer.WriteString("severity", slot.Severity.ToString().ToLowerInvariant());
            writer.WriteString("subject", slot.Subject);
            writer.WriteString("detail", slot.Detail);
            writer.WriteNumber("count", slot.Count);
            if (row.HasPosition)
            {
                writer.WriteString("pos", "current");
                writer.WriteNumber("map", row.MapId);
                writer.WriteNumber("instance", row.InstanceId);
                WritePoint(writer, "p", row.Position);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
