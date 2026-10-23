using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Aged, Dying and Ancient Kodos (entries 4700, 4701, 4702) and the Tamed Kodo (11627) of Desolace, "Kodo Roundup" (quest 5561):
/// mangos-classic ScriptDev2 <c>npc_aged_dying_ancient_kodo</c> (kalimdor/desolace.cpp:62-125, registered :677-681). A kodo without the gossip
/// flag that sees Smeed Scrabblescrew (11596) within 10 yd makes him say one of three lines and casts Kodo Kombo Gossip (18362, triggered) on
/// itself; that spell stops it, gives it the gossip flag and starts a one-minute timer after which, out of combat, it is killed and respawned
/// (its original entry back). It does not aggro on sight (the source's MoveInLineOfSight does not call the base). The taming is
/// <see cref="KodoKombobulatorScript"/>, the hand-in <see cref="AgedDyingAncientKodoGossip"/>.
/// </summary>
public sealed class AgedDyingAncientKodoAI(Creature creature) : ScriptedAI(creature)
{
    public const uint NpcAgedKodo = 4700, NpcDyingKodo = 4701, NpcAncientKodo = 4702, NpcTamedKodo = 11627, NpcSmeed = 11596, QuestKodoRoundup = 5561;
    public const uint SpellKodoKomboItem = 18153, SpellKodoKomboPlayerBuff = 18172, SpellKodoKomboDespawnBuff = 18377, SpellKodoKomboGossip = 18362;
    public const uint RespawnAfterGossipMs = 60_000, HandInDespawnMs = 10_000;

    /// <summary>SAY_SMEED_HOME_1..3 (:42-44), said by Smeed.</summary>
    public static readonly int[] SmeedSays = [-1000348, -1000349, -1000350];

    /// <summary>IsWithinDistInMap(Smeed, 10): 3D, bounding radii added.</summary>
    public const float SmeedRange = 10f;

    private uint _despawnMs;

    /// <summary>m_uiDespawnTimer: 0 while not counting.</summary>
    public uint DespawnMs => _despawnMs;

    /// <summary>Whether <paramref name="entry"/> is one of the three wild kodos the Kombobulator takes (KodoKombobulator::OnCheckCast).</summary>
    public static bool IsWildKodo(uint entry) => entry is NpcAgedKodo or NpcDyingKodo or NpcAncientKodo;

    protected override void Reset() => _despawnMs = 0;

    /// <summary>MoveInLineOfSight (:73-93).</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Creature { Entry: NpcSmeed } smeed || (Me.NpcFlags & (uint)NpcFlags.Gossip) != 0 || System is not { } system)
        {
            return;
        }

        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        float reach = SmeedRange + Me.BoundingRadius + who.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) > reach * reach)
        {
            return;
        }

        system.SayText(smeed, SmeedSays[system.RandomInt(0, SmeedSays.Length - 1)]);
        DoCast(Me, SpellKodoKomboGossip, triggered: true); // a dummy aura, cast to reach SpellHit
    }

    /// <summary>SpellHit (:95-103).</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id != SpellKodoKomboGossip)
        {
            return;
        }

        System?.MoveIdle(Me);
        Me.NpcFlags |= (uint)NpcFlags.Gossip;
        _despawnMs = RespawnAfterGossipMs;
    }

    /// <summary>
    /// UpdateAI (:105-124). Deviation: a timer at 0 stays off; the source's <c>m_uiDespawnTimer -= diff</c> also runs at 0 and wraps the unsigned
    /// timer to about 49.7 days.
    /// </summary>
    public override void OnUpdate(uint diffMs)
    {
        if (_despawnMs != 0)
        {
            if (_despawnMs <= diffMs)
            {
                if (Me.Combat.Victim is null && Me.IsAlive && System is { } system)
                {
                    Reset();
                    system.KillCreature(Me); // SetDeathState(JUST_DIED)
                    system.ForceRespawn(Me); // Respawn()
                    return;
                }
            }
            else
            {
                _despawnMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

/// <summary>
/// Kodo Kombobulator (18153): mangos-classic <c>KodoKombobulator</c> (kalimdor/desolace.cpp:133-161, registered :712). The target must be a wild
/// kodo (4700-4702), the caster without Kodo Kombo Player Buff (18172) and the kodo without Kodo Kombo Despawn Buff (18377). The kodo gives the
/// caster 18172 (triggered), becomes a Tamed Kodo (11627), casts 18377 on itself, stops a waypoint walk and follows the caster at the pet
/// distance and angle.
/// </summary>
[SpellScript(AgedDyingAncientKodoAI.SpellKodoKomboItem)]
public sealed class KodoKombobulatorScript : ISpellScript
{
    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Target is not Creature target || !AgedDyingAncientKodoAI.IsWildKodo(target.Entry))
        {
            return SpellCastResult.BadTargets;
        }

        return context.System.HasAura(context.Caster, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff)
            || context.System.HasAura(target, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff)
            ? SpellCastResult.CasterAurastate
            : SpellCastResult.CastOk;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature target || context.Target.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        Unit caster = context.Caster;
        context.System.CastSpell(target, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff, SpellCastTargets.ForUnit(caster.Guid), triggered: true);
        creatures.UpdateEntry(target, AgedDyingAncientKodoAI.NpcTamedKodo);
        context.System.CastSpell(target, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff, SpellCastTargets.ForSelf(), triggered: false);
        if (target.Motion.CurrentType == MovementGeneratorType.Waypoint)
        {
            creatures.MoveIdle(target);
        }

        target.Motion.MoveFollow(caster, FollowerAI.FollowDistance, FollowerAI.FollowAngle);
    }
}

/// <summary>
/// GossipHello_npc_aged_dying_ancient_kodo (desolace.cpp:163-177): a player with Kodo Kombo Player Buff (18172) talking to a kodo with Kodo Kombo
/// Despawn Buff (18377) gets the speak-to credit for the kodo's entry (Player::TalkedToCreature; the creature objective of 5561 is 11627), loses
/// the buff, and the kodo goes 10 s later. The kodo's own gossip menu is shown either way (the script answers no menu of its own).
/// </summary>
public sealed class AgedDyingAncientKodoGossip : INpcGossipScript
{
    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (player.Map?.FindUpdater<CreatureMapSystem>() is { } creatures && creatures.FindCreature(npc.Guid) is { } kodo
            && creatures.HasAura(player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff)
            && creatures.HasAura(kodo, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff))
        {
            creatures.TalkedToCreature(player, kodo.Entry, kodo.Guid);
            creatures.RemoveAuras(player, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff);
            creatures.ForcedDespawn(kodo, AgedDyingAncientKodoAI.HandInDespawnMs);
        }

        return null;
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
}
