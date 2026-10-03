using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class DuelService
{
    /// <summary>AREA_FLAG_DUEL (vmangos Database/DBCEnums.h:61; wow_messages world/external/area_flags.wowm:9 CITY_ALLOW_DUELS): duels are allowed in the area.</summary>
    public const uint AreaFlagDuel = 0x40;

    private static readonly ConditionalWeakTable<SpellSystem, DuelService> s_bySpells = new();

    /// <summary>The AreaTable row of the area a player stands in, or null when unknown. Without it every area counts as unknown.</summary>
    public Func<Player, AreaTemplate?>? AreaOf { get; set; }

    /// <summary>Whether the first player has the second on its ignore list (vmangos <c>GetSocial()->HasIgnore</c>). Without it nobody ignores anybody.</summary>
    public Func<Player, ObjectGuid, bool>? IsIgnoring { get; set; }

    /// <summary>
    /// Attach the spell system: remember it (Grovel, aura removal, interruption), register the duel cast check on it and make
    /// <see cref="ForSpells"/> find this service for the duel spell effect.
    /// </summary>
    public void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        Spells = spells;
        s_bySpells.AddOrUpdate(spells, this);
        spells.RegisterCastCheck(new DuelCastCheck(this));
    }

    /// <summary>The service installed on <paramref name="spells"/>, or null.</summary>
    public static DuelService? ForSpells(SpellSystem spells)
        => s_bySpells.TryGetValue(spells, out DuelService? service) ? service : null;

    /// <summary>
    /// The cast-time rules of a duel challenge (vmangos Spell::CheckCast SPELL_EFFECT_DUEL, Spell.cpp:6187-6203): the caster and the target
    /// must be players (BAD_TARGETS; a self challenge is refused too, hardening the references leave open) and the target must not hold a duel
    /// (TARGET_DUELING). The transport comparison (NOT_ON_TRANSPORT) has nothing to compare on this base.
    /// </summary>
    internal SpellCastResult CheckChallenge(Unit caster, Unit? target)
    {
        if (!Options.Enabled)
        {
            return SpellCastResult.NoDueling;
        }

        if (caster is not Player || target is not Player other || ReferenceEquals(caster, target))
        {
            return SpellCastResult.BadTargets;
        }

        return other.Duel is not null ? SpellCastResult.TargetDueling : SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos Spell::EffectDuel (SpellEffects.cpp:4650-4761) after the cast landed. Returns the result to send as SMSG_CAST_RESULT, or null when the
    /// request was created or silently dropped (the references drop it without a message when the caster still holds a duel, the target ignores the
    /// caster or the maps differ).
    /// <list type="bullet">
    /// <item>A caster that holds a duel with someone else completes it first (WON when it had started, so the new challenger forfeits; otherwise
    /// INTERRUPTED) and goes on. vmangos then also deletes the target's duel object; that is unreachable on retail data (the cast check refuses a target in a duel)
    /// and is not copied, so a third player's duel is never orphaned.</item>
    /// <item>Challenging the opponent of a duel that is already running: TARGET_ENEMY. A target in a duel: TARGET_DUELING.</item>
    /// <item>Both players' areas must carry <see cref="AreaFlagDuel"/>; an area with no row passes unless <see cref="DuelOptions.RequireKnownArea"/>.</item>
    /// <item>The flag object spawns at the midpoint of the two players at the caster's height and orientation, with the caster's faction template,
    /// the caster's level + 1 and the caster as creator, before it becomes visible; it despawns after the spell's duration when that is positive.
    /// A world without the flag template cannot host duels: the request is dropped and leaves no state behind.</item>
    /// </list>
    /// </summary>
    public SpellCastResult? Challenge(Player caster, Player target, uint flagEntry, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        if (!Options.Enabled)
        {
            return SpellCastResult.NoDueling;
        }

        if (ReferenceEquals(caster, target))
        {
            return SpellCastResult.BadTargets;
        }

        if (caster.IsQuestSettlementPending || target.IsQuestSettlementPending)
        {
            return null;
        }

        if (caster.Duel is { } held && !ReferenceEquals(held.Opponent, target))
        {
            Complete(caster, held.StartTimeSeconds != 0 ? DuelCompleteType.Won : DuelCompleteType.Interrupted);
            caster.Duel = null; // vmangos deletes it at once (SpellEffects.cpp:4666-4668); the old opponent's half is dropped by its own update
        }

        if (caster.Duel is { } same && ReferenceEquals(same.Opponent, target) && same.StartTimeSeconds != 0)
        {
            return SpellCastResult.TargetEnemy;
        }

        if (target.Duel is not null)
        {
            return SpellCastResult.TargetDueling;
        }

        if (caster.Duel is not null || IsIgnoring?.Invoke(target, caster.Guid) == true || !ReferenceEquals(target.Map, caster.Map) || caster.Map is not { } map)
        {
            return null;
        }

        if (!AreaAllowsDuels(caster) || !AreaAllowsDuels(target))
        {
            return SpellCastResult.NoDueling;
        }

        GameObject? flag = map.FindUpdater<GameObjectMapSystem>()?.Summon(
            flagEntry, (caster.X + target.X) * 0.5f, (caster.Y + target.Y) * 0.5f, caster.Z, caster.Orientation,
            durationMs > 0 ? (uint)(durationMs / 1000) : 0);
        if (flag is null)
        {
            return null;
        }

        flag.SetUInt32(UpdateFields.GameobjectFaction, caster.FactionTemplate);
        flag.SetUInt32(UpdateFields.GameobjectLevel, (uint)caster.Level + 1);
        flag.SetUInt64(UpdateFields.ObjectFieldCreatedBy, caster.Guid.Value);
        Begin(caster, target, flag);
        return null;
    }

    /// <summary>
    /// The tail of vmangos Spell::EffectDuel once the flag object exists (SpellEffects.cpp:4732-4760): SMSG_DUEL_REQUESTED (flag guid, then
    /// challenger guid) to both players, the two crossed <see cref="DuelInfo"/> halves (the target's names the challenger as initiator and as
    /// opponent, exactly as vmangos builds <c>duel2</c>), and PLAYER_DUEL_ARBITER on both. The caller has validated the request.
    /// </summary>
    public void Begin(Player challenger, Player target, GameObject flag)
    {
        ArgumentNullException.ThrowIfNull(challenger);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(flag);
        byte[] requested = DuelPackets.Requested(flag.Guid.Value, challenger.Guid.Value);
        challenger.Session.Send(WorldOpcode.SmsgDuelRequested, requested);
        target.Session.Send(WorldOpcode.SmsgDuelRequested, requested);

        challenger.Duel = new DuelInfo(challenger, target);
        target.Duel = new DuelInfo(challenger, challenger);
        challenger.DuelArbiter = flag.Guid.Value;
        target.DuelArbiter = flag.Guid.Value;
    }

    private bool AreaAllowsDuels(Player player)
    {
        AreaTemplate? area = AreaOf?.Invoke(player);
        return area is null ? !Options.RequireKnownArea : (area.Flags & AreaFlagDuel) != 0;
    }
}

/// <summary>The duel challenge's cast-time check (<see cref="DuelService.CheckChallenge"/>), in the target phase.</summary>
internal sealed class DuelCastCheck(DuelService service) : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Target;

    public int Order => SpellCastCheckOrder.Facing;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => context.Spell.HasEffect(SpellEffectName.Duel) ? service.CheckChallenge(context.Caster, context.Target) : SpellCastResult.CastOk;
}
