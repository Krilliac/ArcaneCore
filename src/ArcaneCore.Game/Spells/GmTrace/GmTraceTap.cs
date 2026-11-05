using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Feeds the GM stream's <c>damage</c>, <c>spells</c> and <c>moves</c> channels from the packets <see cref="Map.BroadcastToObservers(WorldObject, WorldOpcode, ReadOnlySpan{byte}, Player?)"/>
/// is asked to send. Every combat log, spell packet and movement relay of the game goes through that method (CombatPackets.SendToSet,
/// SpellSystem.SendToSet and the movement relays), and the tap sits in front of the observer lookup, so a unit nobody watches still traces.
/// <para>
/// Cost: an opcode with no channel is one switch; a channel nobody follows is one lock-free set lookup. Only when a GM follows the channel
/// is the payload decoded (bounds-checked, allocation-free reads into a value-type state) and offered to the sink, which calls the
/// formatter only when a GM matches and has a rate-limit token. A short or odd payload is skipped. Runs on the map's thread (or the
/// world thread); formatters read the source and its own map only.
/// </para>
/// </summary>
internal static class GmTraceTap
{
    /// <summary>The most hit or miss targets of one SMSG_SPELL_GO that a line lists (the rest are counted).</summary>
    private const int MaxListed = 8;

    private static readonly bool[] IsMoveOpcode = BuildMoveTable();

    private static bool[] BuildMoveTable()
    {
        WorldOpcode[] all = Enum.GetValues<WorldOpcode>();
        ushort max = 0;
        foreach (WorldOpcode opcode in all)
        {
            max = (ushort)Math.Max(max, (ushort)opcode);
        }

        var table = new bool[max + 1];
        foreach (WorldOpcode opcode in all)
        {
            if (opcode is WorldOpcode.SmsgMonsterMove or WorldOpcode.SmsgMonsterMoveTransport
                || opcode.ToString().StartsWith("MsgMove", StringComparison.Ordinal))
            {
                table[(ushort)opcode] = true;
            }
        }

        return table;
    }

    /// <summary>Called by <see cref="Map"/> for every broadcast while the GM stream is active.</summary>
    internal static void OnBroadcast(IGmTraceSink trace, Map map, WorldObject source, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        try
        {
            switch (opcode)
            {
                case WorldOpcode.SmsgSpellnonmeleedamagelog:
                    if (trace.HasSubscribers(GmStreamChannels.Damage)) SpellDamage(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgAttackerstateupdate:
                    if (trace.HasSubscribers(GmStreamChannels.Damage)) MeleeDamage(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgSpellheallog:
                    if (trace.HasSubscribers(GmStreamChannels.Damage)) HealLog(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgPeriodicauralog:
                    if (trace.HasSubscribers(GmStreamChannels.Damage)) PeriodicLog(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgEnvironmentaldamagelog:
                    if (trace.HasSubscribers(GmStreamChannels.Damage)) EnvironmentalLog(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgSpellStart:
                    if (trace.HasSubscribers(GmStreamChannels.Spells)) SpellStart(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgSpellGo:
                    if (trace.HasSubscribers(GmStreamChannels.Spells)) SpellGo(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgSpelllogmiss:
                    if (trace.HasSubscribers(GmStreamChannels.Spells)) SpellLogMiss(trace, map, source, payload);
                    break;
                case WorldOpcode.SmsgSpellFailedOther:
                    if (trace.HasSubscribers(GmStreamChannels.Spells)) SpellFailedOther(trace, map, source, payload);
                    break;
                default:
                    if ((ushort)opcode < IsMoveOpcode.Length && IsMoveOpcode[(ushort)opcode] && trace.HasSubscribers(GmStreamChannels.Moves))
                    {
                        Move(trace, map, source, opcode);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            map.Logger.LogFault("gm.trace-tap", ex, "GM stream tap failed for {Opcode}", opcode); // tracing must never break a broadcast
        }
    }

    // --- names --------------------------------------------------------------------------

    /// <summary>"Name(Kind:counter)" for an object of <paramref name="map"/> that is found there, else "Kind:counter"; "none" for an empty GUID.</summary>
    internal static string Who(Map? map, ObjectGuid guid)
    {
        if (guid.IsEmpty)
        {
            return "none";
        }

        string kind = guid.IsPlayer ? "Player" : guid.High switch
        {
            HighGuid.Unit => "Creature",
            HighGuid.Pet => "Pet",
            HighGuid.GameObject => "GameObject",
            _ => guid.High.ToString(),
        };
        uint id = guid.IsPlayer ? guid.Low : guid.Counter;
        string? name = map?.FindObject(guid) switch
        {
            Player player => player.Name,
            Creature creature => creature.Template.Name,
            _ => null,
        };
        return name is null
            ? string.Create(CultureInfo.InvariantCulture, $"{kind}:{id}")
            : string.Create(CultureInfo.InvariantCulture, $"{name}({kind}:{id})");
    }

    /// <summary>"spell 133" or "spell 133 \"Fireball\"" when the name is known.</summary>
    internal static string SpellText(uint id, string? name) => "spell " + SpellRef(id, name);

    /// <summary>"133" or "133 \"Fireball\"" when the name is known.</summary>
    internal static string SpellRef(uint id, string? name)
        => string.IsNullOrEmpty(name)
            ? id.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{id} \"{name}\"");

    private static string School(uint school) => school == 0 ? "Physical" : ((SpellSchool)school).ToString();

    /// <summary>MsgMoveStartForward as MSG_MOVE_START_FORWARD.</summary>
    private static string OpcodeName(WorldOpcode opcode)
    {
        string pascal = opcode.ToString();
        var text = new System.Text.StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                text.Append('_');
            }

            text.Append(char.ToUpperInvariant(pascal[i]));
        }

        return text.ToString();
    }

    // --- damage -------------------------------------------------------------------------

    private readonly record struct DamageState(Map Map, ulong Actor, ulong Other, uint Spell, uint Amount, uint School, uint Absorb, long Resist, uint Block, uint HitInfo, bool Periodic);

    private static void SpellDamage(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadPackedGuid(out ulong target) || !reader.TryReadPackedGuid(out ulong caster)
            || !reader.TryReadUInt32(out uint spell) || !reader.TryReadUInt32(out uint damage) || !reader.TryReadByte(out byte school)
            || !reader.TryReadUInt32(out uint absorbed) || !reader.TryReadUInt32(out uint resisted) || !reader.TryReadByte(out byte periodic)
            || !reader.TryReadByte(out _) || !reader.TryReadUInt32(out uint blocked) || !reader.TryReadUInt32(out uint hitInfo))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(target));
        trace.Publish(GmStreamChannels.Damage, in subject,
            new DamageState(map, caster, target, spell, damage, school, absorbed, resisted, blocked, hitInfo, periodic != 0),
            static s => FormatSpellDamage(s));
    }

    private static string FormatSpellDamage(DamageState s)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Who(s.Map, new ObjectGuid(s.Actor))} -> {Who(s.Map, new ObjectGuid(s.Other))} {(s.Periodic ? "periodic " : string.Empty)}{SpellText(s.Spell, null)} {s.Amount} {School(s.School)} (absorb {s.Absorb}, resist {s.Resist}, block {s.Block}){((s.HitInfo & SpellSystem.SpellHitTypeCrit) != 0 ? " CRIT" : string.Empty)}");

    private readonly record struct MeleeState(Map Map, ulong Attacker, ulong Victim, uint HitInfo, uint Total, uint School, uint Absorb, long Resist, uint Block, uint VictimState, uint MeleeSpell);

    private static void MeleeDamage(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt32(out uint hitInfo) || !reader.TryReadPackedGuid(out ulong attacker) || !reader.TryReadPackedGuid(out ulong victim)
            || !reader.TryReadUInt32(out uint total) || !reader.TryReadByte(out byte count))
        {
            return;
        }

        uint firstSchool = 0;
        uint absorb = 0;
        long resist = 0;
        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt32(out uint school) || !reader.TryReadSingle(out _) || !reader.TryReadUInt32(out _)
                || !reader.TryReadUInt32(out uint subAbsorb) || !reader.TryReadInt32(out int subResist))
            {
                return;
            }

            if (i == 0)
            {
                // The sub-damage school is a school INDEX on the wire (0 = Normal), as retail 1.12.1 senders write it: vmangos
                // Combat.cpp:203 and cmangos-classic Unit.cpp:6112 both write GetFirstSchoolInMask(damageSchoolMask).
                firstSchool = school;
            }

            absorb += subAbsorb;
            resist += subResist;
        }

        if (!reader.TryReadUInt32(out uint victimState) || !reader.TryReadUInt32(out _) || !reader.TryReadUInt32(out uint meleeSpell) || !reader.TryReadUInt32(out uint blocked))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(attacker), new ObjectGuid(victim));
        trace.Publish(GmStreamChannels.Damage, in subject,
            new MeleeState(map, attacker, victim, hitInfo, total, firstSchool, absorb, resist, blocked, victimState, meleeSpell),
            static s => FormatMelee(s));
    }

    private static string FormatMelee(MeleeState s)
    {
        var hit = (HitInfo)s.HitInfo;
        string outcome = (hit & HitInfo.Miss) != 0 ? " MISS"
            : (hit & HitInfo.CriticalHit) != 0 ? " CRIT"
            : (hit & HitInfo.Crushing) != 0 ? " CRUSHING"
            : (hit & HitInfo.Glancing) != 0 ? " GLANCING"
            : string.Empty;
        var state = (VictimState)s.VictimState;
        string stateText = state is VictimState.Normal or VictimState.Unaffected ? string.Empty : " " + state;
        string via = s.MeleeSpell != 0 ? string.Create(CultureInfo.InvariantCulture, $" via spell {s.MeleeSpell}") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Who(s.Map, new ObjectGuid(s.Attacker))} -> {Who(s.Map, new ObjectGuid(s.Victim))} melee{via} {s.Total} {School(s.School)} (absorb {s.Absorb}, resist {s.Resist}, block {s.Block}){outcome}{stateText}");
    }

    private readonly record struct HealState(Map Map, ulong Caster, ulong Target, uint Spell, uint Amount, bool Crit, bool Periodic);

    private static void HealLog(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadPackedGuid(out ulong target) || !reader.TryReadPackedGuid(out ulong caster)
            || !reader.TryReadUInt32(out uint spell) || !reader.TryReadUInt32(out uint amount) || !reader.TryReadByte(out byte crit))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(target));
        trace.Publish(GmStreamChannels.Damage, in subject, new HealState(map, caster, target, spell, amount, crit != 0, Periodic: false), static s => FormatHeal(s));
    }

    private static string FormatHeal(HealState s)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Who(s.Map, new ObjectGuid(s.Caster))} -> {Who(s.Map, new ObjectGuid(s.Target))} {(s.Periodic ? "periodic " : string.Empty)}heal {SpellText(s.Spell, null)} {s.Amount}{(s.Crit ? " CRIT" : string.Empty)}");

    /// <summary>The most entries of one SMSG_PERIODICAURALOG read (the builder writes one).</summary>
    private const int MaxPeriodicEntries = 8;

    private static void PeriodicLog(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadPackedGuid(out ulong target) || !reader.TryReadPackedGuid(out ulong caster)
            || !reader.TryReadUInt32(out uint spell) || !reader.TryReadUInt32(out uint count))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(target));
        for (uint i = 0; i < count && i < MaxPeriodicEntries; i++)
        {
            if (!reader.TryReadUInt32(out uint auraType))
            {
                return;
            }

            switch ((AuraType)auraType)
            {
                case AuraType.PeriodicDamage:
                case AuraType.PeriodicDamagePercent:
                    if (!reader.TryReadUInt32(out uint amount) || !reader.TryReadUInt32(out uint school)
                        || !reader.TryReadUInt32(out uint absorbed) || !reader.TryReadUInt32(out uint resisted))
                    {
                        return;
                    }

                    trace.Publish(GmStreamChannels.Damage, in subject,
                        new DamageState(map, caster, target, spell, amount, school, absorbed, resisted, 0, 0, Periodic: true),
                        static s => FormatSpellDamage(s));
                    break;
                case AuraType.PeriodicHeal:
                case AuraType.ObsModHealth:
                    if (!reader.TryReadUInt32(out uint healed))
                    {
                        return;
                    }

                    trace.Publish(GmStreamChannels.Damage, in subject,
                        new HealState(map, caster, target, spell, healed, Crit: false, Periodic: true),
                        static s => FormatHeal(s));
                    break;
                case AuraType.ObsModMana:
                case AuraType.PeriodicEnergize:
                    if (!reader.TryReadUInt32(out _) || !reader.TryReadUInt32(out _))
                    {
                        return;
                    }

                    break;
                default:
                    // An entry of a type this tap does not decode ends the read: its length is unknown, so the rest cannot be located.
                    // That includes the mana-leech entry (DrainAuras.cs, BuildManaLeechLog), which is therefore not traced.
                    return;
            }
        }
    }

    private readonly record struct EnvironmentalState(Map Map, ulong Victim, byte Type, uint Damage, uint Absorb, int Resist);

    private static void EnvironmentalLog(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt64(out ulong victim) || !reader.TryReadByte(out byte type) || !reader.TryReadUInt32(out uint damage)
            || !reader.TryReadUInt32(out uint absorb) || !reader.TryReadInt32(out int resist))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(victim), ObjectGuid.Empty);
        trace.Publish(GmStreamChannels.Damage, in subject, new EnvironmentalState(map, victim, type, damage, absorb, resist),
            static s => string.Create(CultureInfo.InvariantCulture,
                $"{Who(s.Map, new ObjectGuid(s.Victim))} environmental {(EnvironmentalDamageType)s.Type} {s.Damage} (absorb {s.Absorb}, resist {s.Resist})"));
    }

    // --- spells -------------------------------------------------------------------------

    private readonly record struct StartState(Map Map, ulong Caster, ulong Target, uint Spell, uint CastTimeMs);

    private static void SpellStart(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadPackedGuid(out _) || !reader.TryReadPackedGuid(out ulong caster) || !reader.TryReadUInt32(out uint spell)
            || !reader.TryReadUInt16(out _) || !reader.TryReadUInt32(out uint castTime) || !reader.TryReadUInt16(out ushort mask))
        {
            return;
        }

        ulong target = 0;
        // The same flags SpellCastTargets.Write reads one packed GUID for (unit, game object, corpse).
        const ushort guidFlags = (ushort)SpellCastTargetFlags.Unit | (ushort)SpellCastTargetFlags.GameObject
            | (ushort)SpellCastTargetFlags.CorpseEnemy | (ushort)SpellCastTargetFlags.CorpseAlly;
        if ((mask & guidFlags) != 0 && !reader.TryReadPackedGuid(out target))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(target));
        trace.Publish(GmStreamChannels.Spells, in subject, new StartState(map, caster, target, spell, castTime),
            static s => string.Create(CultureInfo.InvariantCulture,
                $"cast start {SpellText(s.Spell, null)} by {Who(s.Map, new ObjectGuid(s.Caster))} on {Who(s.Map, new ObjectGuid(s.Target))}, cast {s.CastTimeMs} ms"));
    }

    [InlineArray(MaxListed)]
    private struct GuidList
    {
        private ulong _element;
    }

    [InlineArray(MaxListed)]
    private struct ReasonList
    {
        private byte _element;
    }

    private readonly record struct GoState(Map Map, ulong Caster, uint Spell, int HitCount, GuidList Hits, int MissCount, GuidList Misses, ReasonList Reasons);

    private static void SpellGo(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadPackedGuid(out _) || !reader.TryReadPackedGuid(out ulong caster) || !reader.TryReadUInt32(out uint spell)
            || !reader.TryReadUInt16(out _) || !reader.TryReadByte(out byte hitCount))
        {
            return;
        }

        GuidList hits = default;
        for (int i = 0; i < hitCount; i++)
        {
            if (!reader.TryReadUInt64(out ulong hit))
            {
                return;
            }

            if (i < MaxListed)
            {
                hits[i] = hit;
            }
        }

        if (!reader.TryReadByte(out byte missCount))
        {
            return;
        }

        GuidList misses = default;
        ReasonList reasons = default;
        for (int i = 0; i < missCount; i++)
        {
            if (!reader.TryReadUInt64(out ulong missed) || !reader.TryReadByte(out byte reason))
            {
                return;
            }

            if (reason == (byte)SpellMissInfo.Reflect && !reader.TryReadByte(out _))
            {
                return;
            }

            if (i < MaxListed)
            {
                misses[i] = missed;
                reasons[i] = reason;
            }
        }

        ulong other = hitCount > 0 ? hits[0] : missCount > 0 ? misses[0] : 0;
        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(other));
        trace.Publish(GmStreamChannels.Spells, in subject, new GoState(map, caster, spell, hitCount, hits, missCount, misses, reasons),
            static s => FormatGo(s));
    }

    private static string FormatGo(GoState s)
    {
        var text = new System.Text.StringBuilder(96);
        text.Append(CultureInfo.InvariantCulture, $"go {SpellText(s.Spell, null)} by {Who(s.Map, new ObjectGuid(s.Caster))}");
        if (s.HitCount > 0)
        {
            text.Append(" hit: ");
            for (int i = 0; i < Math.Min(s.HitCount, MaxListed); i++)
            {
                text.Append(i == 0 ? string.Empty : ", ").Append(Who(s.Map, new ObjectGuid(s.Hits[i])));
            }

            if (s.HitCount > MaxListed)
            {
                text.Append(CultureInfo.InvariantCulture, $", +{s.HitCount - MaxListed} more");
            }
        }

        if (s.MissCount > 0)
        {
            text.Append(s.HitCount > 0 ? "; miss: " : " miss: ");
            for (int i = 0; i < Math.Min(s.MissCount, MaxListed); i++)
            {
                text.Append(i == 0 ? string.Empty : ", ").Append(Who(s.Map, new ObjectGuid(s.Misses[i]))).Append(" (").Append(((SpellMissInfo)s.Reasons[i]).ToString()).Append(')');
            }

            if (s.MissCount > MaxListed)
            {
                text.Append(CultureInfo.InvariantCulture, $", +{s.MissCount - MaxListed} more");
            }
        }

        return text.ToString();
    }

    private readonly record struct MissState(Map Map, uint Spell, ulong Caster, ulong Target, byte Reason);

    /// <summary>The most targets of one SMSG_SPELLLOGMISS read (the builder writes one).</summary>
    private const int MaxMissTargets = 8;

    private static void SpellLogMiss(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt32(out uint spell) || !reader.TryReadUInt64(out ulong caster) || !reader.TryReadByte(out _) || !reader.TryReadUInt32(out uint count))
        {
            return;
        }

        for (uint i = 0; i < count && i < MaxMissTargets; i++)
        {
            if (!reader.TryReadUInt64(out ulong target) || !reader.TryReadByte(out byte reason))
            {
                return;
            }

            var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), new ObjectGuid(target));
            trace.Publish(GmStreamChannels.Spells, in subject, new MissState(map, spell, caster, target, reason),
                static s => string.Create(CultureInfo.InvariantCulture,
                    $"miss {SpellText(s.Spell, null)} {Who(s.Map, new ObjectGuid(s.Caster))} -> {Who(s.Map, new ObjectGuid(s.Target))}: {(SpellMissInfo)s.Reason}"));
        }
    }

    private readonly record struct FailedState(Map Map, ulong Caster, uint Spell);

    private static void SpellFailedOther(IGmTraceSink trace, Map map, WorldObject source, ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt64(out ulong caster) || !reader.TryReadUInt32(out uint spell))
        {
            return;
        }

        var subject = GmTraceGuard.SubjectOf(source, new ObjectGuid(caster), ObjectGuid.Empty);
        trace.Publish(GmStreamChannels.Spells, in subject, new FailedState(map, caster, spell),
            static s => string.Create(CultureInfo.InvariantCulture, $"interrupted {SpellText(s.Spell, null)} by {Who(s.Map, new ObjectGuid(s.Caster))}"));
    }

    // --- producers called from the spell code itself (not from a broadcast) -------------

    private readonly record struct CastFailState(Unit Caster, SpellInfo Spell, SpellCastResult Result, bool Triggered);

    /// <summary>The <c>spells</c> channel line for a failed cast (SpellSystem.SendCastResult), traced for creature and triggered casters too. The caller has checked <see cref="GmTraceGuard.Wants"/>.</summary>
    internal static void CastFailed(IGmTraceSink trace, Unit caster, SpellInfo spell, SpellCastResult result, bool triggered)
    {
        try
        {
            var subject = GmTraceGuard.SubjectOf(caster, caster.Target);
            trace.Publish(GmStreamChannels.Spells, in subject, new CastFailState(caster, spell, result, triggered),
                static s => string.Create(CultureInfo.InvariantCulture,
                    $"fail {SpellText(s.Spell.Id, s.Spell.Name)} by {Who(s.Caster.Map, s.Caster.Guid)}: {s.Result}{(s.Triggered ? " (triggered)" : string.Empty)}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            caster.Map?.Logger.LogFault("gm.trace-cast-failed", ex, "GM stream cast-failure trace failed for spell {SpellId}", spell.Id); // tracing must never break a cast
        }
    }

    private readonly record struct ProcState(Unit Owner, SpellInfo Aura, ProcFlags Flags, bool IsVictim, double Chance, double Roll, bool Hit);

    /// <summary>The <c>procs</c> channel line for one chance roll (SpellSystem.IsTriggeredAtSpellProcEvent). The caller has checked <see cref="GmTraceGuard.Wants"/>.</summary>
    internal static void ProcRoll(IGmTraceSink trace, Unit owner, Unit? target, SpellInfo aura, ProcFlags flags, bool isVictim, double chance, double roll, bool hit)
    {
        try
        {
            var subject = GmTraceGuard.SubjectOf(owner, target?.Guid ?? ObjectGuid.Empty);
            trace.Publish(GmStreamChannels.Procs, in subject, new ProcState(owner, aura, flags, isVictim, chance, roll, hit),
                static s => string.Create(CultureInfo.InvariantCulture,
                    $"{Who(s.Owner.Map, s.Owner.Guid)} aura {SpellRef(s.Aura.Id, s.Aura.Name)} on {s.Flags} ({(s.IsVictim ? "victim" : "attacker")}) chance {s.Chance:0.00}% roll {s.Roll:0.00} -> {(s.Hit ? "proc" : "no proc")}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            owner.Map?.Logger.LogFault("gm.trace-proc-roll", ex, "GM stream proc trace failed for aura {SpellId}", aura.Id); // tracing must never break a proc check
        }
    }

    // --- moves --------------------------------------------------------------------------

    private readonly record struct MoveState(Map Map, WorldObject Source, WorldOpcode Opcode);

    private static void Move(IGmTraceSink trace, Map map, WorldObject source, WorldOpcode opcode)
    {
        var subject = GmTraceGuard.SubjectOf(source, source.Guid, ObjectGuid.Empty);
        trace.Publish(GmStreamChannels.Moves, in subject, new MoveState(map, source, opcode), static s => FormatMove(s));
    }

    private static string FormatMove(MoveState s)
    {
        WorldObject o = s.Source;
        string speeds = o is Unit unit
            ? string.Create(CultureInfo.InvariantCulture, $" run {unit.RunSpeed:0.00} walk {unit.WalkSpeed:0.00} swim {unit.SwimSpeed:0.00}")
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Who(s.Map, o.Guid)} {OpcodeName(s.Opcode)} at ({o.X:0.0}, {o.Y:0.0}, {o.Z:0.0}) o {o.Orientation:0.00}{speeds}");
    }
}
