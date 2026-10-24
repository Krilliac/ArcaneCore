using System.Numerics;

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
/// loading anything. Not yet implemented.
/// </summary>
internal static class MapFeedInspect
{
    public static InsightAnswer Run(WorldMapFeed feed, in FeedRequest request) => throw new NotImplementedException();
}

/// <summary>The <c>anomalies</c> query: the ring's notices with the current position of what each names, resolved on the world thread.</summary>
internal static class MapFeedAnomalies
{
    public static InsightAnswer Run(WorldMapFeed feed, in FeedRequest request) => throw new NotImplementedException();
}
