using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>One entry of an inspected unit's threat list.</summary>
internal readonly record struct InspectThreat(ulong Guid, string? Name, float Threat);

/// <summary>One aura on an inspected unit. <see cref="RemainingMs"/> is -1 for a permanent aura.</summary>
internal readonly record struct InspectAura(uint SpellId, byte Stacks, int RemainingMs, int DurationMs, ulong Caster, bool Positive);

/// <summary>What the playerbot pack's route seam says about a bot.</summary>
internal readonly record struct InspectBot(int RoutePoints, int Next, string[] Flags, Vector3? Goal);

/// <summary>The values of one inspected unit, copied on the world thread.</summary>
internal sealed class InspectData
{
    public ulong Guid;
    public string EntityKind = string.Empty;
    public uint Entry;
    public string Name = string.Empty;
    public uint MapId;
    public uint InstanceId;
    public Vector3 Position;
    public bool IsUnit;
    public byte Level;
    public uint Health;
    public uint MaxHealth;
    public string? PowerType;
    public uint Power;
    public uint MaxPower;
    public uint Faction;
    public uint UnitFlags;
    public string StandState = string.Empty;
    public bool InCombat;
    public ulong Target;
    public ulong Victim;
    public InspectThreat[] Threat = [];
    public bool AurasAvailable;
    public InspectAura[] Auras = [];
    public string? MotionCurrent;
    public string? MotionDefault;
    public string[] MotionStack = [];
    public string? Ai;
    public string? AiName;
    public InspectBot? Bot;
}

/// <summary>One notice of an <c>anomalies</c> answer, with the position of what it names when the world still holds it.</summary>
internal readonly record struct AnomalyRow(AnomalySlot Slot, bool HasPosition, uint MapId, uint InstanceId, Vector3 Position);

/// <summary>The values of an <c>anomalies</c> answer.</summary>
internal sealed class AnomalyData
{
    public long Latest;
    public long Lost;
    public bool Reset;
    public AnomalyRow[] Rows = [];
}

/// <summary>The answer to an <c>inspect</c> or <c>anomalies</c> query, or its refusal.</summary>
internal sealed class InsightAnswer
{
    public long Id;
    public QueryKind Kind;
    public bool Ok = true;
    public string Code = string.Empty;
    public string Message = string.Empty;
    public InspectData? Inspect;
    public AnomalyData? Anomalies;

    public static InsightAnswer Refused(in FeedRequest request, string code, string message)
        => new() { Id = request.Id, Kind = request.Kind, Ok = false, Code = code, Message = message };
}

/// <summary>
/// The <c>inspect</c> query (docs/areas/atlas-insight.md): everything the server knows about one unit, read on the world thread without
/// loading anything (a map that is not running is refused, never created; the object lookup does not load grids).
/// </summary>
internal static class MapFeedInspect
{
    public static InsightAnswer Run(WorldMapFeed feed, in FeedRequest request)
    {
        Map? map = feed.World.FindMap(request.MapId, request.InstanceId);
        if (map is null || map.IsUnloaded)
        {
            return InsightAnswer.Refused(request, MapFeedProtocol.MapNotLoaded, $"map {request.MapId} instance {request.InstanceId} is not running");
        }

        WorldObject? found = map.FindObject(new ObjectGuid(request.InspectGuid));
        if (found is null)
        {
            return InsightAnswer.Refused(request, MapFeedProtocol.NotFound, "no object with that guid is on that map");
        }

        var data = new InspectData
        {
            Guid = found.Guid.Value,
            MapId = map.MapId,
            InstanceId = map.InstanceId,
            Position = new Vector3(found.X, found.Y, found.Z),
        };
        switch (found)
        {
            case Player player:
                data.EntityKind = feed.IsBot(player.Guid.Value) ? "bot" : "player";
                data.Name = player.Name;
                break;
            case Creature creature:
                data.EntityKind = "creature";
                data.Entry = creature.Template.Entry;
                data.Name = creature.Template.Name;
                break;
            case GameObject gameObject:
                data.EntityKind = "gameobject";
                data.Entry = gameObject.Template.Entry;
                data.Name = gameObject.Template.Name;
                break;
            default:
                data.EntityKind = "object";
                break;
        }

        if (found is Unit unit)
        {
            FillUnit(feed, unit, data);
        }

        return new InsightAnswer { Id = request.Id, Kind = request.Kind, Inspect = data };
    }

    private static void FillUnit(WorldMapFeed feed, Unit unit, InspectData data)
    {
        data.IsUnit = true;
        data.Level = unit.Level;
        data.Health = unit.Health;
        data.MaxHealth = unit.MaxHealth;
        data.Faction = unit.FactionTemplate;
        data.UnitFlags = (uint)unit.UnitFlags;
        data.StandState = unit.StandState.ToString();
        data.InCombat = unit.Combat.IsInCombat;
        data.Target = unit.Target.Value;
        data.Victim = unit.Combat.Victim?.Guid.Value ?? 0;

        PowerType power = unit.PowerType;
        data.PowerType = power.ToString();
        data.Power = unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);
        data.MaxPower = unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);

        if (unit.Combat.HasThreatList)
        {
            data.Threat = [.. unit.Combat.Threat.Entries
                .OrderByDescending(static e => e.Threat)
                .Take(MapFeedProtocol.MaxInspectThreat)
                .Select(static e => new InspectThreat(e.Target.Guid.Value, NameOf(e.Target), e.Threat))];
        }

        if (feed.Spells is { } spells)
        {
            data.AurasAvailable = true;
            data.Auras = [.. spells.GetAuras(unit).Where(static h => !h.IsRemoved).Select(static h => new InspectAura(
                h.Spell.Id, h.StackAmount, h.IsPermanent ? -1 : Math.Max(0, h.Duration), h.IsPermanent ? -1 : Math.Max(0, h.MaxDuration), h.CasterGuid.Value, h.IsPositive))];
        }

        if (unit is Creature creature)
        {
            data.MotionCurrent = creature.Motion.CurrentType.ToString();
            data.MotionDefault = creature.Motion.DefaultType.ToString();
            data.MotionStack = [.. creature.Motion.ActiveTypes.Select(static t => t.ToString())];
            data.AiName = creature.Template.AIName;
            data.Ai = creature.AI?.GetType().Name;
        }

        if (unit is Player bot && data.EntityKind == "bot" && feed.RouteSource is { } routes)
        {
            data.Bot = ReadBot(routes, bot);
        }
    }

    private static string? NameOf(Unit unit) => unit switch
    {
        Player player => player.Name,
        Creature creature => creature.Template.Name,
        _ => null,
    };

    private static InspectBot? ReadBot(IBotRouteSource routes, Player bot)
    {
        try
        {
            var points = new Vector3[MapFeedProtocol.MaxPathPoints];
            int total = routes.CopyRoute(bot, points, out BotRouteInfo info);
            if (total <= 0)
            {
                return null;
            }

            if (total > points.Length)
            {
                points = new Vector3[total];
                total = routes.CopyRoute(bot, points, out info);
            }

            int count = Math.Min(total, points.Length);
            if (count <= 0)
            {
                return null;
            }

            var flags = new List<string>();
            foreach (BotRouteFlags flag in Enum.GetValues<BotRouteFlags>())
            {
                if (flag != BotRouteFlags.None && (info.Flags & flag) == flag)
                {
                    flags.Add(flag.ToString());
                }
            }

            return new InspectBot(count, Math.Clamp(info.Next, 0, count), [.. flags], points[count - 1]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>The <c>anomalies</c> query: the ring's notices with the current position of what each names, resolved on the world thread.</summary>
internal static partial class MapFeedAnomalies
{
    public static InsightAnswer Run(WorldMapFeed feed, in FeedRequest request)
    {
        MapFeedAnomalyRing? ring = feed.AnomalyRing;
        if (ring is null)
        {
            return InsightAnswer.Refused(request, MapFeedProtocol.BadArgs, "the anomaly ring is not running");
        }

        AnomalyPage page = ring.Query(request.Since, request.MaxResults);
        var rows = new AnomalyRow[page.Slots.Length];
        var cache = new Dictionary<ulong, (bool Found, uint MapId, uint InstanceId, Vector3 Position)>();
        for (int i = 0; i < rows.Length; i++)
        {
            AnomalySlot slot = page.Slots[i];
            bool has = false;
            uint mapId = 0;
            uint instanceId = 0;
            Vector3 position = default;
            if (TryGuid(slot.Subject, out ulong guid))
            {
                if (!cache.TryGetValue(guid, out var hit))
                {
                    hit = Locate(feed.World, new ObjectGuid(guid));
                    if (!hit.Found && TryTransportGuid(slot.Subject, out ulong transport))
                    {
                        hit = Locate(feed.World, new ObjectGuid(transport)); // an elevator or tram carries HighGuid.Transport, not GameObject
                    }

                    cache[guid] = hit;
                }

                (has, mapId, instanceId, position) = hit;
            }

            rows[i] = new AnomalyRow(slot, has, mapId, instanceId, position);
        }

        return new InsightAnswer { Id = request.Id, Kind = request.Kind, Anomalies = new AnomalyData { Latest = page.Latest, Lost = page.Lost, Reset = page.Reset, Rows = rows } };
    }

    private static (bool Found, uint MapId, uint InstanceId, Vector3 Position) Locate(WorldRuntime world, ObjectGuid guid)
    {
        foreach (Map map in world.Maps)
        {
            if (!map.IsUnloaded && map.FindObject(guid) is { } found)
            {
                return (true, map.MapId, map.InstanceId, new Vector3(found.X, found.Y, found.Z));
            }
        }

        return (false, 0, 0, default);
    }

    /// <summary>The guid a ledger subject names, in the forms the reporters write: <c>creature 3100 spawn 812</c>, <c>gameobject 2843 spawn 77001</c>, <c>bot Thrall (guid 4D2)</c> (hex), <c>player Thrall (guid 1234)</c> (decimal).</summary>
    internal static bool TryGuid(string subject, out ulong guid)
    {
        guid = 0;
        if (subject.Length is 0 or > 300)
        {
            return false;
        }

        if (SpawnSubject().Match(subject) is { Success: true } spawn
            && uint.TryParse(spawn.Groups[2].ValueSpan, out uint entry) && uint.TryParse(spawn.Groups[3].ValueSpan, out uint counter))
        {
            HighGuid high = spawn.Groups[1].Value == "creature" ? HighGuid.Unit : HighGuid.GameObject;
            guid = ObjectGuid.WithEntry(high, entry, counter).Value;
            return true;
        }

        if (PlayerSubject().Match(subject) is { Success: true } player)
        {
            // The bot reporters write the guid in hex (PlayerbotAnomalies.SubjectOf); the Game observers and ServerAnomalies write the decimal low part.
            NumberStyles style = player.Groups[1].Value == "bot" ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
            if (uint.TryParse(player.Groups[2].ValueSpan, style, CultureInfo.InvariantCulture, out uint low) && low != 0)
            {
                guid = ObjectGuid.Player(low).Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For a <c>gameobject E spawn S</c> subject, the guid the same spawn has when its template is an elevator or tram
    /// (<see cref="GameObject.HighGuidOf"/>: <see cref="HighGuid.Transport"/>); the subject does not say which, so the caller tries both.
    /// </summary>
    internal static bool TryTransportGuid(string subject, out ulong guid)
    {
        guid = 0;
        if (subject.Length is 0 or > 300 || SpawnSubject().Match(subject) is not { Success: true } spawn || spawn.Groups[1].Value != "gameobject"
            || !uint.TryParse(spawn.Groups[2].ValueSpan, out uint entry) || !uint.TryParse(spawn.Groups[3].ValueSpan, out uint counter))
        {
            return false;
        }

        guid = ObjectGuid.WithEntry(HighGuid.Transport, entry, counter).Value;
        return true;
    }

    [GeneratedRegex(@"^(creature|gameobject) (?:entry )?(\d+) spawn (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SpawnSubject();

    [GeneratedRegex(@"^(bot|player) .*\(guid ([0-9A-Fa-f]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerSubject();
}
