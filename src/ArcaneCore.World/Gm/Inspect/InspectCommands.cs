using System.Globalization;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Inspect;

/// <summary>
/// The <c>.inspect</c> getters (no vmangos equivalent): read-only dumps of the selected unit (yourself without a selection) and of the
/// state the server keeps about it that the client never sees: <c>unit</c>, <c>stats</c>, <c>combat</c>, <c>threat</c>, <c>ai</c>,
/// <c>motion</c>, <c>save</c>, <c>fields</c>, and of the world around it: <c>map</c>, <c>spell</c>. A selected player of higher security
/// is refused as for every other GM command. Nothing here changes state.
/// </summary>
public sealed class InspectCommands : ICommandGroup
{
    /// <summary>Most rows a list (threat entries, attackers, update fields) prints.</summary>
    public const int MaxRows = 25;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("inspect", AccountSecurity.GameMaster,
            "Syntax: .inspect $subcommand\nRead-only dumps of the selected unit (yourself without a selection), its map and spells. Type .inspect to see the list of possible subcommands.",
            Children:
            [
                new ChatCommand("unit", AccountSecurity.GameMaster,
                    "Syntax: .inspect unit\nShow the selected unit: guid, entry, level, health and power, faction, display and scale, unit flags by name, stand state, speeds, movement flags, life and combat state, target, position, zone and area.",
                    Unit, RetailLevel: 3),
                new ChatCommand("stats", AccountSecurity.GameMaster,
                    "Syntax: .inspect stats\nShow the selected unit's primary stats, armor and resistances, attack power, weapon damage and attack times, base health and mana.",
                    Stats, RetailLevel: 3),
                new ChatCommand("combat", AccountSecurity.GameMaster,
                    "Syntax: .inspect combat\nShow the selected unit's combat state: victim, melee, attackers, combat and PvP timers, extra attacks, swing timers and the last swing error.",
                    Combat, RetailLevel: 3),
                new ChatCommand("threat", AccountSecurity.GameMaster,
                    "Syntax: .inspect threat\nList a selected creature's threat list (online and offline entries, temporary threat, the current victim), or the creatures that hold a selected player on theirs.",
                    Threat, RetailLevel: 3),
                new ChatCommand("ai", AccountSecurity.GameMaster,
                    "Syntax: .inspect ai\nShow the selected creature's AI: the AI type, ScriptName and AIName, react state, evade and pacify state, melee and combat movement, unreachable time, and its home.",
                    Ai, RetailLevel: 3),
                new ChatCommand("motion", AccountSecurity.GameMaster,
                    "Syntax: .inspect motion\nShow the selected creature's movement: the generator stack, the targeted unit, reachability, the running spline (points, progress, destination), waypoint progress, wander distance and home.",
                    Motion, RetailLevel: 3),
                new ChatCommand("save", AccountSecurity.GameMaster,
                    "Syntax: .inspect save [$player]\nShow the save state of the selected or named online player: the last save request, whether a snapshot is waiting, held or quarantined, and the save queue's backlog.",
                    Save, RetailLevel: 3),
                new ChatCommand("fields", AccountSecurity.GameMaster,
                    "Syntax: .inspect fields [#first] [#count]\nPrint raw update fields of the selected unit from index #first (default 0), #count of them (default 16, at most 25): index, hex, unsigned and float.",
                    Fields, RetailLevel: 3),
                new ChatCommand("map", AccountSecurity.GameMaster,
                    "Syntax: .inspect map\nShow your map: players (bots counted apart), objects, creatures by state (alive, dead, in combat, evading, moving), dormant respawns, map updaters (isolated ones named), objects in transit.",
                    MapInfo, RetailLevel: 3),
                new ChatCommand("spell", AccountSecurity.GameMaster,
                    "Syntax: .inspect spell #spellid\nShow a spell's data: name and rank, school, cast time, cooldowns, range, duration, power cost, attributes, and every effect with its targets, radius, aura, amounts and trigger spell.",
                    Spell, RetailLevel: 3),
            ], RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>The selected unit (yourself without a selection), or null after replying. A player of higher security is refused.</summary>
    internal static Unit? Target(CommandContext context)
    {
        Unit? unit = GmSelectedUnit.Require(context);
        if (unit is Player player && !context.CanActOn(player))
        {
            return null;
        }

        return unit;
    }

    /// <summary>The selected creature, or null after replying.</summary>
    internal static Creature? TargetCreature(CommandContext context)
    {
        if (GmSelectedUnit.Of(context) is Creature creature)
        {
            return creature;
        }

        context.Reply(GmNpcCommands.SelectCreature);
        return null;
    }

    internal static string Name(Unit unit) => unit switch
    {
        Player p => p.Name,
        Creature c => c.Template.Name,
        _ => unit.Guid.ToString(),
    };

    internal static string Describe(Unit unit) => unit switch
    {
        Player p => string.Create(C, $"{p.Name} (player, guid {p.Guid.Counter})"),
        Creature c => string.Create(C, $"{c.Template.Name} (entry {c.Template.Entry}, guid {c.Guid.Counter})"),
        _ => unit.Guid.ToString(),
    };

    /// <summary>Set flag names of <paramref name="value"/>, unnamed bits as hex.</summary>
    internal static string FlagNames<T>(uint value) where T : struct, Enum
    {
        if (value == 0)
        {
            return "none";
        }

        var names = new List<string>();
        uint rest = value;
        foreach (T flag in Enum.GetValues<T>())
        {
            uint bit = Convert.ToUInt32(flag, C);
            if (bit != 0 && (bit & (bit - 1)) == 0 && (value & bit) != 0)
            {
                names.Add(flag.ToString());
                rest &= ~bit;
            }
        }

        if (rest != 0)
        {
            names.Add(string.Create(C, $"0x{rest:X}"));
        }

        return string.Join("|", names);
    }

    private static bool Unit(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (Target(context) is not { } unit) return true;

        string kind = unit is Player ? "player" : unit is Creature creature ? (creature.IsPet ? "pet" : creature.IsTotem ? "totem" : "creature") : "unit";
        string entry = unit is Creature c ? string.Create(C, $" entry {c.Template.Entry}") : string.Empty;
        context.Reply(string.Create(C, $"{Name(unit)}: {kind}{entry}, guid {unit.Guid.Counter} (0x{unit.Guid.Value:X16}), level {unit.Level}, {unit.Race}/{unit.Class}/{unit.Gender}"));
        int power = (int)unit.PowerType;
        string powerText = power is >= 0 and < 5
            ? string.Create(C, $"{unit.PowerType} {unit.GetUInt32(UpdateFields.UnitFieldPower1 + power)}/{unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + power)}")
            : "no power";
        context.Reply(string.Create(C, $"health {unit.Health}/{unit.MaxHealth} ({unit.HealthPercent:F0}%), {powerText}, faction {unit.FactionTemplate}"));
        context.Reply(string.Create(C, $"display {unit.DisplayId} (native {unit.NativeDisplayId}, mount {unit.GetUInt32(UpdateFields.UnitFieldMountdisplayid)}), scale {unit.GetFloat(UpdateFields.ObjectFieldScaleX):F2}, bounding {unit.BoundingRadius:F2}, combat reach {unit.GetFloat(UpdateFields.UnitFieldCombatreach):F2}"));
        context.Reply(string.Create(C, $"unit flags 0x{(uint)unit.UnitFlags:X8}: {FlagNames<UnitFlags>((uint)unit.UnitFlags)}"));
        context.Reply(string.Create(C, $"stand {unit.StandState}, dynamic flags 0x{unit.GetUInt32(UpdateFields.UnitDynamicFlags):X}, npc flags 0x{unit.GetUInt32(UpdateFields.UnitNpcFlags):X}, emote state {unit.GetUInt32(UpdateFields.UnitNpcEmotestate)}"));
        context.Reply(string.Create(C, $"speeds: walk {unit.WalkSpeed:F2}, run {unit.RunSpeed:F2}, back {unit.RunBackSpeed:F2}, swim {unit.SwimSpeed:F2}, swim back {unit.SwimBackSpeed:F2}, turn {unit.TurnRate:F2}"));
        context.Reply(string.Create(C, $"movement flags 0x{(uint)unit.Movement.Flags:X8}: {FlagNames<ArcaneCore.Protocol.MovementFlags>((uint)unit.Movement.Flags)}"));
        string life = unit is Creature lc ? lc.DeathState.ToString() : unit.Combat.DeathState.ToString();
        string target = unit.Target.IsEmpty ? "none" : unit.Map?.FindObject(unit.Target) is Unit t ? Describe(t) : string.Create(C, $"0x{unit.Target.Value:X16}");
        context.Reply(string.Create(C, $"life {life}, alive {unit.IsAlive}, in combat {unit.Combat.IsInCombat}, target {target}"));
        (uint zone, uint area) = unit.Map is { } map ? map.GetZoneAndAreaId(unit.X, unit.Y, unit.Z) : (0u, 0u);
        context.Reply(string.Create(C, $"map {unit.MapId} instance {unit.Map?.InstanceId ?? 0}, zone {zone}, area {area}, at {unit.X:F2} {unit.Y:F2} {unit.Z:F2} facing {unit.Orientation:F2}"));
        InspectSectionRegistry.Render(context, InspectLeaf.Unit, new InspectSubject(unit, unit.Map));
        return true;
    }

    private static bool Stats(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (Target(context) is not { } unit) return true;

        context.Reply(string.Create(C,
            $"{Name(unit)} stats: str {unit.GetInt32(UpdateFields.UnitFieldStat0)}, agi {unit.GetInt32(UpdateFields.UnitFieldStat1)}, sta {unit.GetInt32(UpdateFields.UnitFieldStat2)}, int {unit.GetInt32(UpdateFields.UnitFieldStat3)}, spi {unit.GetInt32(UpdateFields.UnitFieldStat4)}"));
        int r = UpdateFields.UnitFieldResistances;
        context.Reply(string.Create(C,
            $"armor {unit.GetInt32(r)}, holy {unit.GetInt32(r + 1)}, fire {unit.GetInt32(r + 2)}, nature {unit.GetInt32(r + 3)}, frost {unit.GetInt32(r + 4)}, shadow {unit.GetInt32(r + 5)}, arcane {unit.GetInt32(r + 6)}"));
        context.Reply(string.Create(C,
            $"attack power {unit.GetInt32(UpdateFields.UnitFieldAttackPower)} (+{unit.GetInt32(UpdateFields.UnitFieldAttackPowerMods)}), ranged {unit.GetInt32(UpdateFields.UnitFieldRangedAttackPower)} (+{unit.GetInt32(UpdateFields.UnitFieldRangedAttackPowerMods)})"));
        context.Reply(string.Create(C,
            $"damage main {unit.GetFloat(UpdateFields.UnitFieldMindamage):F1}-{unit.GetFloat(UpdateFields.UnitFieldMaxdamage):F1} every {unit.GetUInt32(UpdateFields.UnitFieldBaseattacktime)} ms, off {unit.GetFloat(UpdateFields.UnitFieldMinoffhanddamage):F1}-{unit.GetFloat(UpdateFields.UnitFieldMaxoffhanddamage):F1} every {unit.GetUInt32(UpdateFields.UnitFieldBaseattacktime + 1)} ms, ranged {unit.GetFloat(UpdateFields.UnitFieldMinrangeddamage):F1}-{unit.GetFloat(UpdateFields.UnitFieldMaxrangeddamage):F1} every {unit.GetUInt32(UpdateFields.UnitFieldRangedattacktime)} ms"));
        context.Reply(string.Create(C,
            $"base health {unit.GetUInt32(UpdateFields.UnitFieldBaseHealth)}, base mana {unit.GetUInt32(UpdateFields.UnitFieldBaseMana)}, cast speed mod {unit.GetFloat(UpdateFields.UnitModCastSpeed):F2}"));
        if (unit is Creature creature)
        {
            context.Reply(string.Create(C,
                $"rates: health x{creature.HealthRate:F2}, damage x{creature.DamageRate:F2}, spell damage x{creature.SpellDamageRate:F2}; rank {creature.Template.Rank}, parry {creature.CanParry}, block {creature.CanBlock}, crush {creature.CanCrush}"));
        }

        return true;
    }

    private static bool Combat(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (Target(context) is not { } unit) return true;

        UnitCombat combat = unit.Combat;
        string victim = combat.Victim is { } v ? Describe(v) : "none";
        context.Reply(string.Create(C,
            $"{Name(unit)}: in combat {combat.IsInCombat}, victim {victim}, melee {combat.IsMeleeAttacking}, combat timer {combat.CombatTimer} ms, PvP combat {combat.InPvpCombat} (flag timer {combat.PvpFlagTimer} ms)"));
        context.Reply(string.Create(C,
            $"swing timers: main {combat.GetAttackTimer(WeaponAttackType.BaseAttack)}/{combat.GetAttackTime(WeaponAttackType.BaseAttack)} ms, off {combat.GetAttackTimer(WeaponAttackType.OffAttack)}/{combat.GetAttackTime(WeaponAttackType.OffAttack)} ms, ranged {combat.GetAttackTimer(WeaponAttackType.RangedAttack)}/{combat.GetAttackTime(WeaponAttackType.RangedAttack)} ms; last swing error {combat.LastSwingError}; extra attacks {combat.ExtraAttacks}"));
        context.Reply(string.Create(C, $"attackers {combat.Attackers.Count}, threatened by {combat.ThreatenedBy.Count}, own threat list {(combat.HasThreatList ? combat.Threat.Entries.Count + combat.Threat.OfflineEntries.Count : 0)} entries"));
        foreach (Unit attacker in combat.Attackers.Take(MaxRows))
        {
            context.Reply("  attacker " + Describe(attacker));
        }

        return true;
    }

    private static bool Threat(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (Target(context) is not { } unit) return true;

        ThreatLines(context, unit);
        InspectSectionRegistry.Render(context, InspectLeaf.Threat, new InspectSubject(unit, unit.Map));
        return true;
    }

    private static void ThreatLines(CommandContext context, Unit unit)
    {
        if (unit is Player player)
        {
            Unit[] holders = [.. player.Combat.ThreatenedBy.OrderByDescending(h => h.Combat.Threat.GetThreat(player))];
            context.Reply(string.Create(C, $"{player.Name} is on {holders.Length} threat list(s):"));
            foreach (Unit holder in holders.Take(MaxRows))
            {
                ThreatList list = holder.Combat.Threat;
                int rank = list.Entries.ToList().FindIndex(e => ReferenceEquals(e.Target, player));
                context.Reply(string.Create(C, $"  {Describe(holder)}: threat {list.GetThreat(player):F1}, {(rank >= 0 ? $"rank {rank + 1} of {list.Entries.Count}" : "offline")}{(ReferenceEquals(list.CurrentVictim, player) ? ", current victim" : string.Empty)}"));
            }

            return;
        }

        if (!unit.Combat.HasThreatList)
        {
            context.Reply($"{Name(unit)} has an empty threat list.");
            return;
        }

        ThreatList threat = unit.Combat.Threat;
        string current = threat.CurrentVictim is { } cv ? Describe(cv) : "none";
        context.Reply(string.Create(C, $"{Name(unit)} threat list: {threat.Entries.Count} online, {threat.OfflineEntries.Count} offline, current victim {current}, taunted {threat.HasTauntCasters}"));
        foreach (ThreatEntry entry in threat.Entries.Take(MaxRows))
        {
            context.Reply(string.Create(C, $"  {Describe(entry.Target)}: {entry.Threat:F1}{(entry.TempThreat != 0 ? $" (temp {entry.TempThreat:F1})" : string.Empty)}"));
        }

        foreach (ThreatEntry entry in threat.OfflineEntries.Take(MaxRows))
        {
            context.Reply(string.Create(C, $"  offline {Describe(entry.Target)}: {entry.Threat:F1}"));
        }
    }

    private static bool Ai(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (TargetCreature(context) is not { } creature) return true;

        CreatureAI? ai = creature.AI;
        string aiType = ai is null ? "none" : ai.GetType().Name;
        context.Reply(string.Create(C,
            $"{Describe(creature)} AI {aiType}; ScriptName '{creature.Template.ScriptName}', AIName '{creature.Template.AIName}'"));
        context.Reply(string.Create(C,
            $"react {creature.ReactState}, evading {creature.IsInEvadeMode} (unreachable {creature.IsEvadingUnreachable}, target unreachable for {creature.TargetNotReachableMs} ms), pacified {creature.IsTempPacified}"));
        if (ai is not null)
        {
            context.Reply(string.Create(C,
                $"melee {ai.MeleeEnabled}, combat movement {ai.CombatMovement}, caster chase distance {ai.CasterChaseDistance:F1}, aggroes on sight {ai.AggroesOnSight}, victim {(ai.Victim is { } v ? Describe(v) : "none")}"));
        }

        if (ai is CreatureEventAI eventAi)
        {
            int armed = eventAi.Engine.Holders.Count(h => h.Enabled);
            int casts = eventAi.Engine.Holders.Count(h => h.Event.Actions.Any(a => a.Type == (byte)EventAiActionType.Cast));
            context.Reply(string.Create(C,
                $"eventAI: phase {eventAi.Phase}, {armed}/{eventAi.Engine.Holders.Count} holder(s) armed, {casts} cast row(s), {eventAi.Unsupported.Count} unsupported"));
        }
        else if (ai is CreatureSmartAI smartAi)
        {
            SmartScript script = smartAi.Script;
            int active = script.Events.Count(h => h.Active);
            int casts = script.Events.Count(h => h.Row.ActionType == (byte)SmartAction.Cast);
            context.Reply(string.Create(C,
                $"smartAI: phase {script.Phase}, {active}/{script.Events.Count} event(s) active, {script.TimedActionList.Count} timed-list row(s), {casts} cast row(s)"));
        }

        context.Reply(string.Create(C,
            $"home {creature.Home.X:F2} {creature.Home.Y:F2} {creature.Home.Z:F2}, {Distance(creature, creature.Home.X, creature.Home.Y, creature.Home.Z):F1} yards away; movement type {creature.MovementType}, regenerates health {creature.RegeneratesHealth}"));
        InspectSectionRegistry.Render(context, InspectLeaf.Ai, new InspectSubject(creature, creature.Map));
        return true;
    }

    private static float Distance(WorldObject o, float x, float y, float z)
        => MathF.Sqrt(((o.X - x) * (o.X - x)) + ((o.Y - y) * (o.Y - y)) + ((o.Z - z) * (o.Z - z)));

    private static bool Motion(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (TargetCreature(context) is not { } creature) return true;

        MotionMaster motion = creature.Motion;
        string stack = string.Join(" > ", motion.ActiveTypes);
        string targeted = motion.TargetedUnit is { } t ? Describe(t) : "none";
        context.Reply(string.Create(C,
            $"{Describe(creature)} motion: current {motion.CurrentType} (default {motion.DefaultType}), stack [{stack}], targeted {targeted}, reachable {motion.IsReachable}, last waypoint {motion.LastReachedWaypoint}"));
        if (creature.Spline is { } spline && GmNpcCommands.SystemOf(context) is { } system)
        {
            long clock = system.ClockMs;
            IReadOnlyList<System.Numerics.Vector3> points = spline.Points;
            context.Reply(string.Create(C,
                $"spline {spline.Id}: {(spline.Run ? "run" : "walk")}, {points.Count} point(s), {spline.ElapsedMs(clock)}/{spline.DurationMs} ms, finished {spline.IsFinished(clock)}, to {spline.EndX:F2} {spline.EndY:F2} {spline.EndZ:F2} ({Distance(creature, spline.EndX, spline.EndY, spline.EndZ):F1} yards), facing {spline.Facing}"));
        }
        else
        {
            context.Reply("No spline running.");
        }

        context.Reply(string.Create(C,
            $"wander distance {creature.WanderDistance:F1}, home {creature.Home.X:F2} {creature.Home.Y:F2} {creature.Home.Z:F2}"));
        InspectSectionRegistry.Render(context, InspectLeaf.Motion, new InspectSubject(creature, creature.Map));
        return true;
    }

    private static bool Save(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!GmTargets.TryPlayer(context, parsed, out Player player) || !context.CanActOn(player))
        {
            return true;
        }

        int id = checked((int)player.Guid.Low);
        string last = player.LastSaveRequestMs is { } ms
            ? string.Create(C, $"{(context.World.NowMs - ms) / 1000.0:F0} s ago")
            : "never this session";
        if (context.Session.Services.GetService<CharacterSaveQueue>() is not { } queue)
        {
            context.Reply(string.Create(C, $"{player.Name} (character {id}): last save request {last}; no character save queue in this host."));
            return true;
        }

        context.Reply(string.Create(C,
            $"{player.Name} (character {id}): last save request {last}; snapshot waiting {queue.IsPending(id)}, held {queue.IsHeld(id)}, quarantined {queue.IsQuarantined(id)}, logging out {player.IsLoggingOut}"));
        context.Reply(string.Create(C,
            $"save queue: {queue.Pending} character(s) pending, oldest {queue.OldestPendingAge.TotalSeconds:F1} s"));
        return true;
    }

    private static bool Fields(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptUInt32(out uint first, 0) || !parsed.ExtractOptUInt32(out uint count, 16) || !parsed.IsEmpty || count == 0)
        {
            return false;
        }

        if (Target(context) is not { } unit) return true;
        count = Math.Min(count, MaxRows);
        if (first >= unit.ValuesCount)
        {
            context.Reply(string.Create(C, $"{Name(unit)} has {unit.ValuesCount} update fields (0-{unit.ValuesCount - 1})."));
            return true;
        }

        uint end = Math.Min((uint)unit.ValuesCount, first + count);
        context.Reply(string.Create(C, $"{Name(unit)} update fields {first}-{end - 1} of {unit.ValuesCount}:"));
        for (uint i = first; i < end; i++)
        {
            uint value = unit.GetUInt32((int)i);
            float f = BitConverter.UInt32BitsToSingle(value);
            string asFloat = float.IsFinite(f) && value != 0 && MathF.Abs(f) is > 1e-6f and < 1e9f ? string.Create(C, $" float {f:G6}") : string.Empty;
            context.Reply(string.Create(C, $"  [{i}] 0x{value:X8} {value}{asFloat}"));
        }

        return true;
    }

    private static bool MapInfo(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (context.Player.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        int bots = map.Players.Count(p => p.Session.IsPlayerbot);
        string name = map.Template?.Name is { Length: > 0 } n ? n : "?";
        context.Reply(string.Create(C,
            $"map {map.MapId} instance {map.InstanceId} [{name}]: {map.PlayerCount} players ({bots} bots), {map.ObjectCount} objects, {map.TransitCount} in transit, {map.RelocationsWithoutVisibility} relocations without a visibility pass"));
        if (GmNpcCommands.SystemOf(context) is { } system)
        {
            int alive = 0, dead = 0, combat = 0, evading = 0, moving = 0, pets = 0;
            foreach (Creature creature in system.Creatures)
            {
                if (creature.DeathState == CreatureDeathState.Alive) alive++; else dead++;
                if (creature.Combat.IsInCombat) combat++;
                if (creature.IsInEvadeMode) evading++;
                if (creature.IsMoving) moving++;
                if (creature.IsPet) pets++;
            }

            context.Reply(string.Create(C,
                $"creatures {system.Creatures.Count}: {alive} alive, {dead} dead, {combat} in combat, {evading} evading, {moving} moving, {pets} pets; {system.DormantRespawnCount} dormant respawns"));
        }

        string updaters = string.Join(", ", map.Updaters.Select(u => u.GetType().Name));
        IReadOnlyList<IMapUpdater> isolated = map.IsolatedUpdaters;
        context.Reply(string.Create(C, $"updaters ({map.Updaters.Count}): {updaters}"));
        if (isolated.Count > 0)
        {
            context.Reply("isolated after faults: " + string.Join(", ", isolated.Select(u => u.GetType().Name)));
        }

        InspectSectionRegistry.Render(context, InspectLeaf.Map, new InspectSubject(null, map));
        return true;
    }

    private static bool Spell(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || !parsed.IsEmpty)
        {
            return false;
        }

        SpellStore store = context.Session.Services.GetService<SpellFeature>()?.System.Store ?? SpellStore.Empty;
        if (store.Get(spellId) is not { } spell)
        {
            context.Reply(string.Create(C, $"Spell {spellId} does not exist."));
            return true;
        }

        string rank = spell.Rank.Length != 0 ? $" ({spell.Rank})" : string.Empty;
        context.Reply(string.Create(C,
            $"spell {spell.Id} {spell.Name}{rank}: school {spell.School}, level {spell.SpellLevel} (base {spell.BaseLevel}, max {spell.MaxLevel}), family {spell.SpellFamilyName} flags 0x{spell.SpellFamilyFlags:X}, damage class {spell.DamageClass}, dispel {spell.Dispel}, mechanic {spell.Mechanic}"));
        context.Reply(string.Create(C,
            $"cast {spell.CastTime.Base} ms, cooldown {spell.RecoveryTime} ms, category {spell.Category} cooldown {spell.CategoryRecoveryTime} ms, GCD {spell.StartRecoveryTime} ms (category {spell.StartRecoveryCategory}), range {spell.Range.Min:F0}-{spell.Range.Max:F0} yd, speed {spell.Speed:F1}"));
        context.Reply(string.Create(C,
            $"duration {spell.Duration.Base} ms (max {spell.Duration.Maximum}), power type {spell.PowerType} cost {spell.ManaCost} (+{spell.ManaCostPerLevel}/lvl, {spell.ManaCostPercentage}%), stacks {spell.StackAmount}, charges {spell.ProcCharges}, proc chance {spell.ProcChance}% flags 0x{(uint)spell.ProcFlags:X}"));
        context.Reply(string.Create(C,
            $"attributes 0x{(uint)spell.Attributes:X8} ex 0x{(uint)spell.AttributesEx:X8} ex2 0x{(uint)spell.AttributesEx2:X8} ex3 0x{spell.AttributesEx3:X8} ex4 0x{spell.AttributesEx4:X8}; passive {spell.IsPassive}, channeled {spell.IsChanneled}, positive {spell.IsPositive}"));
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.IsEmpty) continue;
            var line = new StringBuilder();
            line.Append(C, $"  effect {i}: {effect.Effect}({(int)effect.Effect}) targets {effect.TargetA}/{effect.TargetB}");
            if (effect.AuraType != 0) line.Append(C, $", aura {effect.AuraType}({(int)effect.AuraType})");
            line.Append(C, $", base {effect.BasePoints + 1}");
            if (effect.DieSides > 1) line.Append(C, $"-{effect.BasePoints + effect.DieSides}");
            if (effect.MiscValue != 0) line.Append(C, $", misc {effect.MiscValue}");
            if (effect.Radius > 0) line.Append(C, $", radius {effect.Radius:F0}");
            if (effect.Amplitude > 0) line.Append(C, $", every {effect.Amplitude} ms");
            if (effect.TriggerSpell > 0) line.Append(C, $", triggers {effect.TriggerSpell}");
            if (effect.ChainTarget > 0) line.Append(C, $", chain {effect.ChainTarget}");
            context.Reply(line.ToString());
        }

        return true;
    }
}
