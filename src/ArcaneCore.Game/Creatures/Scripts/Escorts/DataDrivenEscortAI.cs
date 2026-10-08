using System.Text.Json;
using System.Text.Json.Serialization;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts.Escorts;

/// <summary>Only source checked ScriptDev2 escort facts are admitted to this catalog.</summary>
public sealed class EscortSpec
{
    public uint Entry { get; init; }
    public uint QuestId { get; init; }
    public uint Faction { get; init; }
    public bool ClearImmuneToNpc { get; init; }
    public bool ImmuneOnRespawn { get; init; }
    public bool FactionAfterStart { get; init; }
    public bool RequirePlayerAtWaypoint { get; init; }
    public bool InstantRespawn { get; init; }
    public int StartText { get; init; }
    public bool StartTextToPlayer { get; init; }
    public byte? SpawnStandState { get; init; }
    public byte? StartStandState { get; init; }
    public byte? HomeStandState { get; init; }

    /// <summary>The script constructor's SetReactState (cmangos UnitAI::m_reactState, kept by the AI across respawns); null keeps the template's.</summary>
    public byte? ReactState { get; init; }

    public EscortAggroSpec? Aggro { get; init; }
    public string Source { get; init; } = "";
    public EscortWaypointSpec[] Waypoints { get; init; } = [];
}

public sealed class EscortAggroSpec
{
    public int ChanceDenominator { get; init; }
    public bool NonPlayerOnly { get; init; }
    public uint AreaId { get; init; }

    /// <summary>DoScriptText(text, m_creature, pWho): the aggressor is the text's target; false is DoScriptText(text, m_creature).</summary>
    public bool ToTarget { get; init; }

    public int[] TextIds { get; init; } = [];
}

public sealed class EscortWaypointSpec
{
    public uint Point { get; init; }
    public EscortActionSpec[] Actions { get; init; } = [];
}

public sealed class EscortActionSpec
{
    public string Type { get; init; } = "";
    public int Id { get; init; }
    public uint DespawnMs { get; init; }
    public bool AttackEscort { get; init; }
    public bool CorpseTimed { get; init; }
    public bool OocOrCorpse { get; init; }

    /// <summary>
    /// DoScriptText(text, m_creature, pPlayer) with the escort player as the target: every one of these in the ported scripts sits under
    /// <c>if (Player* pPlayer = GetPlayerForEscort())</c> or after the WaypointReached early return, so it is skipped without the player.
    /// </summary>
    public bool ToPlayer { get; init; }
    public uint SpeakerEntry { get; init; }
    public float Radius { get; init; }
    public float[][] Positions { get; init; } = [];
}

public static class EscortSpecCatalog
{
    private static readonly IReadOnlyDictionary<uint, EscortSpec> s_specs = Load();

    public static IReadOnlyCollection<EscortSpec> All => [.. s_specs.Values];

    public static EscortSpec? Find(uint entry) => s_specs.GetValueOrDefault(entry);

    private static IReadOnlyDictionary<uint, EscortSpec> Load()
    {
        using Stream stream = typeof(EscortSpecCatalog).Assembly.GetManifestResourceStream(
            "ArcaneCore.Game.Creatures.Scripts.Escorts.validated.json")
            ?? throw new InvalidOperationException("validated escort specs are missing");
        return Parse(stream);
    }

    /// <summary>Reads and checks a spec list; any malformed, unknown or duplicate entry throws (fail closed).</summary>
    internal static IReadOnlyDictionary<uint, EscortSpec> Parse(Stream stream)
    {
        // A misspelt field must not load as its default: unknown members fail the load.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        EscortSpec[] specs = JsonSerializer.Deserialize<EscortSpec[]>(stream, options)
            ?? throw new InvalidOperationException("validated escort specs are empty");
        var byEntry = new Dictionary<uint, EscortSpec>();
        foreach (EscortSpec spec in specs)
        {
            if (spec.Entry == 0 || spec.QuestId == 0 || string.IsNullOrWhiteSpace(spec.Source)
                || spec.Waypoints.Length == 0 || !byEntry.TryAdd(spec.Entry, spec))
            {
                throw new InvalidOperationException($"invalid or duplicate escort spec for entry {spec.Entry}");
            }

            if (spec.Aggro is { } aggro && (aggro.ChanceDenominator < 1 || aggro.TextIds.Length == 0
                || aggro.TextIds.Any(id => id == 0)))
            {
                throw new InvalidOperationException($"invalid escort aggro for entry {spec.Entry}");
            }

            if (spec.ReactState is { } react && !Enum.IsDefined((CreatureReactState)react))
            {
                throw new InvalidOperationException($"invalid escort react state {react} for entry {spec.Entry}");
            }

            var points = new HashSet<uint>();
            foreach (EscortWaypointSpec waypoint in spec.Waypoints)
            {
                if (waypoint.Point == 0 || !points.Add(waypoint.Point) || waypoint.Actions.Length == 0)
                {
                    throw new InvalidOperationException($"invalid escort point {spec.Entry}:{waypoint.Point}");
                }

                foreach (EscortActionSpec action in waypoint.Actions)
                {
                    bool valid = action.Type switch
                    {
                        "say" => action.Id != 0 && action.Positions.Length == 0,
                        "say_nearby" => action.Id != 0 && action.SpeakerEntry > 0 && action.Radius > 0
                            && float.IsFinite(action.Radius) && action.Positions.Length == 0,
                        "quest_complete" => action.Id == spec.QuestId && action.Positions.Length == 0,
                        "set_run" => action.Id is 0 or 1 && action.Positions.Length == 0,
                        "summon" => action.Id > 0 && action.DespawnMs > 0 && action.Positions.Length > 0
                            && !(action.CorpseTimed && action.OocOrCorpse)
                            && action.Positions.All(p => p.Length == 4 && p.All(float.IsFinite)),
                        _ => false,
                    };
                    if (!valid)
                    {
                        throw new InvalidOperationException($"invalid escort action {spec.Entry}:{waypoint.Point}:{action.Type}");
                    }
                }
            }
        }

        return byEntry;
    }
}

/// <summary>
/// ScriptDev2 npc_escortAI waypoint actions: DoScriptText, SummonCreature and
/// RewardPlayerAndGroupAtEventExplored. The per-entry source functions are pinned in the spec.
/// </summary>
public sealed class DataDrivenEscortAI(Creature creature, EscortSpec spec) : EscortAI(creature), IQuestScriptAI
{
    public EscortSpec Spec { get; } = spec;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != Spec.QuestId)
        {
            return;
        }

        if (Spec.FactionAfterStart)
        {
            Start(player: player, questId: questId, instantRespawn: Spec.InstantRespawn);
        }

        if (Spec.Faction != 0)
        {
            Me.FactionTemplate = Spec.Faction;
        }
        if (Spec.ClearImmuneToNpc)
        {
            Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        }

        if (Spec.StartText != 0)
        {
            System?.SayText(Me, Spec.StartText, Spec.StartTextToPlayer ? player : null);
        }

        if (!Spec.FactionAfterStart)
        {
            Start(player: player, questId: questId, instantRespawn: Spec.InstantRespawn);
        }
    }

    protected override void JustSpawned()
    {
        if (Spec.SpawnStandState is { } state)
        {
            Me.StandState = (StandState)state;
        }

        ApplyReactState();
    }

    /// <summary>
    /// The constructor's SetReactState lives on the cmangos AI and so outlasts a respawn; this server resets the creature's react state from
    /// the template at every respawn, so it is put back then as well.
    /// </summary>
    private void ApplyReactState()
    {
        if (Spec.ReactState is { } react)
        {
            Me.ReactState = (CreatureReactState)react;
        }
    }

    protected override void JustRespawned()
    {
        base.JustRespawned();
        ApplyReactState();
        if (Spec.ImmuneOnRespawn || (Spec.ClearImmuneToNpc && (Me.Template.UnitFlags & (uint)UnitFlags.ImmuneToNpc) != 0))
        {
            Me.UnitFlags |= UnitFlags.ImmuneToNpc;
        }
    }

    protected override void JustStartedEscort()
    {
        if (Spec.StartStandState is { } state)
        {
            Me.StandState = (StandState)state;
        }
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        if (Spec.HomeStandState is { } state)
        {
            Me.StandState = (StandState)state;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (System is not { } system || Spec.Aggro is not { } eventSpec || system.RandomInt(0, eventSpec.ChanceDenominator - 1) != 0
            || (eventSpec.NonPlayerOnly && target is Player)
            || (eventSpec.AreaId != 0 && system.ZoneAndAreaOf(Me).AreaId != eventSpec.AreaId))
        {
            return;
        }

        int text = eventSpec.TextIds[system.RandomInt(0, eventSpec.TextIds.Length - 1)];
        system.SayText(Me, text, eventSpec.ToTarget ? target : null);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (Spec.RequirePlayerAtWaypoint && GetPlayerForEscort() is null)
        {
            return;
        }

        EscortWaypointSpec? waypoint = Spec.Waypoints.FirstOrDefault(p => p.Point == pointId);
        if (waypoint is null)
        {
            return;
        }

        foreach (EscortActionSpec action in waypoint.Actions)
        {
            switch (action.Type)
            {
                case "say" when !action.ToPlayer:
                    System?.SayText(Me, action.Id);
                    break;
                case "say":
                    if (GetPlayerForEscort() is { } addressed)
                    {
                        System?.SayText(Me, action.Id, addressed);
                    }

                    break;
                case "say_nearby":
                    if (System is { } voiceSystem && voiceSystem.CreaturesOfEntryInRange(Me, action.SpeakerEntry, action.Radius).FirstOrDefault() is { } speaker)
                    {
                        voiceSystem.SayText(speaker, action.Id, Me);
                    }

                    break;
                case "quest_complete":
                    if (GetPlayerForEscort() is { } player)
                    {
                        System?.RewardGroupEventExplored(player, (uint)action.Id, Me);
                    }

                    break;
                case "set_run":
                    SetRun(action.Id == 1);
                    break;
                case "summon":
                    if (System is { } system)
                    {
                        foreach (float[] position in action.Positions)
                        {
                            if (action.CorpseTimed)
                            {
                                system.SummonCorpseTimedDespawn(Me, (uint)action.Id, position[0], position[1], position[2], position[3],
                                    action.AttackEscort ? Me : null, action.DespawnMs);
                            }
                            else
                            {
                                system.SummonAt(Me, (uint)action.Id, position[0], position[1], position[2], position[3],
                                    action.AttackEscort ? Me : null, action.DespawnMs, action.OocOrCorpse);
                            }
                        }
                    }

                    break;
            }
        }
    }
}
