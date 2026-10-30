using System.Globalization;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>One vmangos <c>spell_scripts</c> row in the cmangos layout, or why it has none.</summary>
/// <param name="Row">The <c>dbscripts_on_spell</c> row; its <see cref="DbScriptRow.ConditionId"/> is still 0 when <paramref name="VmangosConditionId"/> is set.</param>
/// <param name="VmangosConditionId">The row's vmangos <c>condition_id</c>, to be resolved to a cmangos condition (0: none).</param>
/// <param name="Rejection">Why the row cannot be translated (null when it can).</param>
public readonly record struct VmangosSpellScriptStep(SpellScriptRow? Row, uint VmangosConditionId, string? Rejection);

/// <summary>
/// Translates a vmangos <c>spell_scripts</c> row (vmangos ScriptMgr::LoadScripts, ScriptMgr.cpp:86: <c>id, delay, command, datalong..4,
/// target_param1/2, target_type, data_flags, dataint..4, x, y, z, o, condition_id</c>) into the cmangos <c>dbscripts_on_spell</c> layout this
/// server runs, only where the two command sets mean the same thing. vmangos and cmangos share many command numbers but not their fields,
/// their flags or their id namespaces, so every accepted field below is checked against both cores and everything else is refused:
/// <list type="bullet">
/// <item>delay: vmangos schedules at <c>GameTime + delay</c> seconds (Maps/Map.cpp:2541, 2559); cmangos delays are milliseconds.</item>
/// <item>targets: only <c>target_type</c> 0 (TARGET_T_PROVIDED_TARGET, Maps/ScriptCommands.h:1140) with no target parameters, where vmangos
/// FindScriptFinalTargets (Maps/Map.cpp:2592-2624) swaps source and target for SF_GENERAL_SWAP_INITIAL_TARGETS (0x01) and again for
/// SWAP_FINAL_TARGETS (0x02), then SF_GENERAL_TARGET_SELF (0x04) makes the target the source: with no buddy that is cmangos
/// SCRIPT_FLAG_REVERSE_DIRECTION (0x02) for an odd number of swaps and SCRIPT_FLAG_SOURCE_TARGETS_SELF (0x04), applied in the same order
/// (cmangos ScriptAction::GetScriptProcessTargets). SF_GENERAL_SKIP_MISSING_TARGETS (0x10) only skips a step whose object left the map,
/// which the executor does anyway; SF_GENERAL_ABORT_ON_FAILURE (0x08) has no cmangos counterpart and is refused.</item>
/// <item>the condition is checked after the targets are final in both cores (vmangos Map::ScriptsProcess, Maps/Map.cpp:2661-2662), but
/// condition ids are per database: the vmangos id is returned for resolution (<see cref="DbScriptDumpImporter"/>).</item>
/// </list>
/// Commands: 15 CAST_SPELL (vmangos ScriptCommand_CastSpell, Maps/ScriptCommands.cpp:608-675: datalong spell, datalong2 CF_* flags; cmangos
/// ScriptMgr.cpp:2155-2202: datalong spell, datalong2 cast flags with TRIGGERED_OLD_TRIGGERED 0x01, dataint..4 random spells, so they must
/// be 0), 17 CREATE_ITEM (both: datalong item, datalong2 amount, the target player else the source player; vmangos ScriptCommands.cpp:677-692,
/// cmangos ScriptMgr.cpp:2241-2256) and 18 DESPAWN_CREATURE (vmangos :694-709 despawns the creature source after datalong ms; cmangos
/// DESPAWN_SELF takes the target creature, else the source, so SOURCE_TARGETS_SELF is added to keep vmangos' choice). TALK takes broadcast
/// text ids in vmangos and db_script_string ids in classic-db; RESPAWN_GAMEOBJECT and the buddy targets name vmangos spawn guids; neither is
/// translatable, nor is any other command. The source data is GPL and never committed; this class only re-expresses the layouts.
/// </summary>
public static class VmangosSpellScriptTranslator
{
    /// <summary>vmangos <c>spell_scripts</c>.</summary>
    public const string Table = "spell_scripts";

    private const uint SwapInitial = 0x01, SwapFinal = 0x02, TargetSelf = 0x04, SkipMissingTargets = 0x10;
    private const uint CastTriggered = 0x02, CastForce = 0x04;
    private const uint CmangosReverseDirection = 0x02, CmangosSourceTargetsSelf = 0x04, CmangosTriggered = 0x01;

    /// <summary>Translate one dump row of <see cref="Table"/> (row order <paramref name="ordinal"/> within its id).</summary>
    public static VmangosSpellScriptStep Translate(DumpRow row, uint ordinal)
    {
        ArgumentNullException.ThrowIfNull(row);
        uint command = U32(row, "command");
        uint flags = U32(row, "data_flags");
        if (U32(row, "target_type") != 0 || U32(row, "target_param1") != 0 || U32(row, "target_param2") != 0)
        {
            return Refuse($"target_type {U32(row, "target_type")} (a vmangos target search, whose parameters are vmangos ids)");
        }

        if ((flags & ~(SwapInitial | SwapFinal | TargetSelf | SkipMissingTargets)) != 0)
        {
            return Refuse($"data_flags 0x{flags:X} (only the swap, target-self and skip-missing flags have a cmangos meaning)");
        }

        if (U32(row, "datalong3") != 0 || U32(row, "datalong4") != 0)
        {
            return Refuse("datalong3/datalong4 set");
        }

        if (I32(row, "dataint") != 0 || I32(row, "dataint2") != 0 || I32(row, "dataint3") != 0 || I32(row, "dataint4") != 0)
        {
            return Refuse("dataint..dataint4 set (cmangos reads them as random alternatives)");
        }

        ulong delayMs = (ulong)U32(row, "delay") * 1000;
        if (delayMs > uint.MaxValue)
        {
            return Refuse("delay too long");
        }

        bool swapped = ((flags & SwapInitial) != 0) ^ ((flags & SwapFinal) != 0);
        uint cmangosFlags = (swapped ? CmangosReverseDirection : 0) | ((flags & TargetSelf) != 0 ? CmangosSourceTargetsSelf : 0);
        uint dataLong = U32(row, "datalong");
        uint dataLong2 = U32(row, "datalong2");
        switch (command)
        {
            case 15:
                if ((dataLong2 & ~(CastTriggered | CastForce)) != 0)
                {
                    return Refuse($"CAST_SPELL flags 0x{dataLong2:X} (only CF_TRIGGERED and CF_FORCE_CAST have a cmangos meaning)");
                }

                // CF_FORCE_CAST only lifts the checks of Creature::TryToCast; a cmangos script cast never makes them.
                dataLong2 = (dataLong2 & CastTriggered) != 0 ? CmangosTriggered : 0;
                break;
            case 17:
                if (dataLong2 == 0)
                {
                    return Refuse("CREATE_ITEM with amount 0");
                }

                break;
            case 18:
                if (dataLong2 != 0)
                {
                    return Refuse("DESPAWN_CREATURE with a respawn delay");
                }

                cmangosFlags |= CmangosSourceTargetsSelf;
                break;
            default:
                return Refuse($"command {command} has no verified cmangos translation");
        }

        var step = new SpellScriptRow
        {
            Id = U32(row, "id"),
            Ordinal = ordinal,
            Delay = (uint)delayMs,
            Priority = U32(row, "priority"),
            Command = command,
            DataLong = dataLong,
            DataLong2 = dataLong2,
            DataFlags = cmangosFlags,
        };
        return new VmangosSpellScriptStep(step, U32(row, "condition_id"), null);
    }

    private static VmangosSpellScriptStep Refuse(string why) => new(null, 0, why);

    private static uint U32(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value)
            ? uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;

    private static int I32(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value)
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
}
