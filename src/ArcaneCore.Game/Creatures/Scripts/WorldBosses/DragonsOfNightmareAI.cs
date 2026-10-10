using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts.WorldBosses;

/// <summary>
/// The shared Nightmare mechanics of the four Emerald Dragons: vmangos scripts/world/dragons_of_nightmare/boss_dragon_of_nightmare.cpp
/// (boss_dragon_of_nightmareAI). Mark of Nature on aggro, Aura of Nature every 3-5 s, Seeping Fog left and right every 2 min 0.3 s (the
/// Dream Fog it summons is ClassicDB EventAI 15224), Noxious Breath, Tail Sweep, the special ability at 75/50/25 % health, and Summon Player
/// (24776) on a victim that has been out of melee reach or out of sight for 6 s. On evade the guardians go and Mark of Nature is removed.
/// Not ported: the vmangos weekly permutation of which dragon spawns where (sObjectMgr saved variables VAR_PERM_1..4 and HardcodedEvents);
/// ClassicDB's spawns keep their own dragon.
/// </summary>
public abstract class DragonOfNightmareAI : RaidBossAI
{
    public const uint SpellSeepingFogRight = 24813;
    public const uint SpellSeepingFogLeft = 24814;
    public const uint SpellNoxiousBreath = 24818;
    public const uint SpellTailSweep = 15847;
    public const uint SpellMarkOfNature = 25041;
    public const uint SpellAuraOfNature = 25044;
    public const uint SpellSummonPlayer = 24776;

    private uint _auraOfNatureMs;
    private uint _seepingFogMs;
    private uint _noxiousBreathMs;
    private uint _tailSweepMs;
    private uint _summonPlayerMs;

    /// <summary>m_uiEventCounter: the next special ability fires below 100 - 25 x this percent.</summary>
    protected uint EventCounter { get; private set; } = 1;

    protected DragonOfNightmareAI(Creature creature) : base(creature, null) => ResetDragon();

    /// <summary>The dragon's own ability at 75, 50 and 25 % (DoSpecialAbility); true when it happened.</summary>
    protected abstract bool DoSpecialAbility();

    /// <summary>The dragon's own timers (UpdateDragonAI); false skips the shared timers this update.</summary>
    protected virtual bool UpdateDragon(uint diffMs) => true;

    /// <summary>boss_dragon_of_nightmareAI::Reset.</summary>
    protected virtual void ResetDragon()
    {
        _auraOfNatureMs = 0;
        _seepingFogMs = 20000;
        _noxiousBreathMs = RandomDelay(7000, 10000);
        _tailSweepMs = 10000;
        _summonPlayerMs = 0;
        EventCounter = 1;
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        ResetDragon();
    }

    /// <summary>Aggro: Mark of Nature, triggered, when not already on.</summary>
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        if (System?.HasAura(Me, SpellMarkOfNature) != true) Cast(SpellMarkOfNature, Me, triggered: true);
    }

    /// <summary>EnterEvadeMode: RemoveGuardians and Mark of Nature removed, then the normal evade.</summary>
    public override void OnEvade()
    {
        System?.DespawnGuardians(Me, 0);
        System?.RemoveAuras(Me, SpellMarkOfNature);
        base.OnEvade();
    }

    /// <summary>A timer tick: true when it ran out (the caller re-arms it after a successful cast).</summary>
    protected static bool Tick(ref uint remaining, uint diffMs)
    {
        if (remaining <= diffMs)
        {
            remaining = 0;
            return true;
        }

        remaining -= diffMs;
        return false;
    }

    /// <summary>A random living, non-GM player on the threat list (SelectAttackingTarget RANDOM, SELECT_FLAG_PLAYER_NOT_GM).</summary>
    protected Player? RandomPlayerTarget(Func<Player, bool>? filter = null)
    {
        Player[] players = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && !p.IsGameMaster && ReferenceEquals(p.Map, Me.Map) && (filter is null || filter(p)))];
        return players.Length == 0 ? null : players[System!.RandomInt(0, players.Length - 1)];
    }

    /// <summary>boss_dragon_of_nightmareAI::UpdateAI, in the reference order.</summary>
    protected override void UpdateCombat(uint diffMs)
    {
        if (Tick(ref _auraOfNatureMs, diffMs) && Cast(SpellAuraOfNature, Me))
            _auraOfNatureMs = RandomDelay(3000, 5000);

        // GetHealthPercent() < 100 - counter * 25
        if (EventCounter <= 3 && HealthBelowPct(100 - (EventCounter * 25)) && DoSpecialAbility())
            ++EventCounter;

        if (!UpdateDragon(diffMs) || Victim is not { } victim)
            return;

        if (!MapCombat.CanReachWithMeleeAutoAttack(Me, victim) || !Me.IsWithinLineOfSight(victim))
        {
            _summonPlayerMs += diffMs;
            if (_summonPlayerMs > 6000 && Cast(SpellSummonPlayer, victim, triggered: true))
                _summonPlayerMs = 0;
        }
        else
        {
            _summonPlayerMs = 0;
        }

        if (Tick(ref _seepingFogMs, diffMs))
        {
            Cast(SpellSeepingFogRight, Me, triggered: true);
            Cast(SpellSeepingFogLeft, Me, triggered: true);
            _seepingFogMs = (2 * 60 * 1000) + 300;
        }

        if (Tick(ref _noxiousBreathMs, diffMs) && Cast(SpellNoxiousBreath, victim))
            _noxiousBreathMs = RandomDelay(9000, 11000);

        if (Tick(ref _tailSweepMs, diffMs) && Cast(SpellTailSweep, Me))
            _tailSweepMs = RandomDelay(6000, 8000);

        base.UpdateCombat(diffMs);
    }
}

/// <summary>Ysondre (14887): vmangos dragons_of_nightmare/boss_ysondre.cpp. Lightning Wave on a random player; at each 25 % she summons
/// Demented Druid Spirits (15260, ClassicDB EventAI): 0.75 per living non-GM player on her threat list, at least 3 and at most 15, which go
/// when out of combat and attack a random player.</summary>
public sealed class YsondreAI(Creature creature) : DragonOfNightmareAI(creature)
{
    public const uint Entry = 14887;
    public const uint SpellLightningWave = 24819;
    public const uint NpcDruidSpirit = 15260;
    public const int SayAggro = 10880;
    public const int SaySummonDruids = 10881;

    private uint _lightningWaveMs;

    protected override void ResetDragon()
    {
        base.ResetDragon();
        _lightningWaveMs = RandomDelay(10000, 13000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, SayAggro);
    }

    /// <summary>JustSummoned(NPC_DRUID_SPIRIT): AttackStart on a random player.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry == NpcDruidSpirit && RandomPlayerTarget() is { } target) summoned.AI?.AttackStart(target);
    }

    /// <summary>The number of druids: <c>count &lt; 20 ? count * 0.75 : 15</c>, at least 3 (uint8 truncation).</summary>
    public static int DruidCount(int attackers) => Math.Max(3, attackers < 20 ? (int)(attackers * 0.75f) : 15);

    protected override bool DoSpecialAbility()
    {
        if (System is not { } system) return false;
        int attackers = Me.Combat.Threat.Entries.Count(e => e.Target is Player { IsAlive: true, IsGameMaster: false });
        for (int i = 0; i < DruidCount(attackers); i++)
        {
            // DoSpawnCreature(..., TEMPSUMMON_TIMED_DESPAWN_OUT_OF_COMBAT, 30)
            if (system.SummonDeadDespawn(Me, NpcDruidSpirit, Me.X, Me.Y, Me.Z, Me.Orientation) is { } druid)
                system.MarkTimedOutOfCombatDespawn(druid, 30);
        }

        system.SayText(Me, SaySummonDruids);
        return true;
    }

    protected override bool UpdateDragon(uint diffMs)
    {
        if (Tick(ref _lightningWaveMs, diffMs) && RandomPlayerTarget() is { } target && Cast(SpellLightningWave, target))
            _lightningWaveMs = RandomDelay(8000, 12000);
        return true;
    }
}

/// <summary>Emeriss (14889): vmangos dragons_of_nightmare/boss_emeriss.cpp. Her aura every 10 s, Volatile Infection on a random player
/// without it every 10-16 s, and Corruption of the Earth at each 25 %. The Putrid Mushrooms of dead players come from spell data.</summary>
public sealed class EmerissAI(Creature creature) : DragonOfNightmareAI(creature)
{
    public const uint Entry = 14889;
    public const uint SpellEmerissAura = 24906;
    public const uint SpellVolatileInfection = 24928;
    public const uint SpellCorruptionOfTheEarth = 24910;
    public const int SayAggro = 10885;
    public const int SayCastCorruption = 10884;

    private uint _auraMs;
    private uint _volatileInfectionMs;

    protected override void ResetDragon()
    {
        base.ResetDragon();
        _auraMs = 0;
        _volatileInfectionMs = RandomDelay(11000, 13000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, SayAggro);
    }

    protected override bool DoSpecialAbility()
    {
        if (!Cast(SpellCorruptionOfTheEarth, Me)) return false;
        System?.SayText(Me, SayCastCorruption);
        return true;
    }

    protected override bool UpdateDragon(uint diffMs)
    {
        if (Tick(ref _auraMs, diffMs) && Cast(SpellEmerissAura, Me))
            _auraMs = 10000;

        // CF_AURA_NOT_PRESENT
        if (Tick(ref _volatileInfectionMs, diffMs) &&
            RandomPlayerTarget() is { } target && System?.HasAura(target, SpellVolatileInfection) != true && Cast(SpellVolatileInfection, target))
            _volatileInfectionMs = RandomDelay(10000, 16000);
        return true;
    }
}

/// <summary>Lethon (14888): vmangos dragons_of_nightmare/boss_lethon.cpp. Shadow Bolt Whirl from aggro; at each 25 % Draw Spirit, and every
/// player it hits leaves a Spirit Shade (15261, <see cref="SpiritShadeAI"/>) that walks to him and heals him with Dark Offering.</summary>
public sealed class LethonAI(Creature creature) : DragonOfNightmareAI(creature)
{
    public const uint Entry = 14888;
    public const uint SpellShadowBoltWhirl = 24834;
    public const uint SpellDrawSpirit = 24811;
    public const int SayAggro = 10883;
    public const int SaySummonShade = 10882;

    protected override void ResetDragon()
    {
        base.ResetDragon();
        System?.RemoveAuras(Me, SpellShadowBoltWhirl);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        if (System?.HasAura(Me, SpellShadowBoltWhirl) != true) Cast(SpellShadowBoltWhirl, Me, triggered: true);
        System?.SayText(Me, SayAggro);
    }

    /// <summary>SpellHitTarget(SPELL_DRAW_SPIRIT): a shade of the player at his place, for a minute or until it dies.</summary>
    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id != SpellDrawSpirit || target is not Player player || System is not { } system) return;
        if (system.SummonDeadDespawn(Me, SpiritShadeAI.Entry, player.X, player.Y, player.Z, player.Orientation) is not { } shade) return;
        system.MarkTimedDespawn(shade, 60 * 1000);
        shade.DisplayId = player.DisplayId;
        if (shade.AI is SpiritShadeAI shadeAi) shadeAi.Lethon = Me;
    }

    protected override bool DoSpecialAbility()
    {
        if (!Cast(SpellDrawSpirit, Me)) return false;
        System?.SayText(Me, SaySummonShade);
        return true;
    }
}

/// <summary>
/// Spirit Shade (15261): vmangos npc_spirit_shadeAI (boss_lethon.cpp). Unseen for 2.5 s, then it follows Lethon and casts Dark Offering
/// (24804) on him once within 5 yards (the reference casts it on reaching the follow target), despawning 300 ms later. It never fights.
/// Limit: this server has no per-creature visibility switch, so the 2.5 s are spent standing still rather than unseen.
/// </summary>
public sealed class SpiritShadeAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 15261;
    public const uint SpellDarkOffering = 24804;

    private uint _delayMs = 2500;
    private bool _offered;

    public Creature? Lethon { get; set; }

    public override bool AttackStart(Unit target) => false;

    public override void OnAttackedBy(Unit attacker)
    {
    }

    public override void MoveInLineOfSight(Unit who)
    {
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_offered) return;
        if (_delayMs > 0)
        {
            _delayMs = _delayMs > diffMs ? _delayMs - diffMs : 0;
            if (_delayMs > 0) return;
            if (Lethon is not { IsAlive: true, IsInWorld: true } lethon)
            {
                System?.ForcedDespawn(Me, 0);
                _offered = true;
                return;
            }

            if (!Offer(lethon)) Me.Motion.MoveFollow(lethon, 0, 0);
            return;
        }

        if (Lethon is { IsAlive: true, IsInWorld: true } target) Offer(target);
        else { System?.ForcedDespawn(Me, 0); _offered = true; }
    }

    private bool Offer(Creature lethon)
    {
        float dx = lethon.X - Me.X, dy = lethon.Y - Me.Y, dz = lethon.Z - Me.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > 5f * 5f) return false;
        DoCast(lethon, SpellDarkOffering, triggered: true);
        System?.ForcedDespawn(Me, 300);
        _offered = true;
        return true;
    }
}

/// <summary>Taerar (14890): vmangos dragons_of_nightmare/boss_taerar.cpp. Arcane Blast and Bellowing Roar; at each 25 % he stuns himself,
/// becomes unselectable and splits into three Shades of Taerar (15302, ClassicDB EventAI) until all three die or 2 minutes pass.</summary>
public sealed class TaerarAI(Creature creature) : DragonOfNightmareAI(creature)
{
    public const uint Entry = 14890;
    public const uint SpellArcaneBlast = 24857;
    public const uint SpellBellowingRoar = 22686;
    public const uint SpellShadeLeft = 24841;
    public const uint SpellShadeRight = 24842;
    public const uint SpellShadeFront = 24843;
    public const uint SpellSelfStun = 24883;
    public const uint NpcShadeOfTaerar = 15302;
    public const int SayAggro = 10886;
    public const int SaySummonShade = 10887;

    private uint _arcaneBlastMs;
    private uint _bellowingRoarMs;
    private uint _shadesTimeoutMs;
    private int _shadesDead;

    /// <summary>True while he is banished behind his shades.</summary>
    public bool Banished => _shadesTimeoutMs != 0;

    protected override void ResetDragon()
    {
        base.ResetDragon();
        _arcaneBlastMs = RandomDelay(11000, 13000);
        _bellowingRoarMs = RandomDelay(27000, 30000);
        _shadesTimeoutMs = 0;
        _shadesDead = 0;
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, SayAggro);
    }

    public override void OnEvade()
    {
        Unbanish();
        base.OnEvade();
    }

    protected override bool DoSpecialAbility()
    {
        if (!Cast(SpellSelfStun, Me)) return false;
        Cast(SpellShadeLeft, Me, triggered: true);
        Cast(SpellShadeRight, Me, triggered: true);
        Cast(SpellShadeFront, Me, triggered: true);
        Me.UnitFlags |= UnitFlags.NotSelectable;
        System?.SayText(Me, SaySummonShade);
        _shadesTimeoutMs = 120000;
        return true;
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (summoned.Entry != NpcShadeOfTaerar || ++_shadesDead != 3) return;
        Unbanish();
        System?.DespawnGuardians(Me, 0);
    }

    private void Unbanish()
    {
        System?.RemoveAuras(Me, SpellSelfStun);
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
        _shadesTimeoutMs = 0;
        _shadesDead = 0;
    }

    protected override bool UpdateDragon(uint diffMs)
    {
        if (_shadesTimeoutMs != 0)
        {
            System?.ExtendLeash(Me); // UpdateLeashExtensionTime
            if (Tick(ref _shadesTimeoutMs, diffMs)) Unbanish();
            return false;
        }

        if (Tick(ref _arcaneBlastMs, diffMs) && RandomPlayerTarget() is { } target && Cast(SpellArcaneBlast, target))
            _arcaneBlastMs = RandomDelay(10000, 16000);

        if (Tick(ref _bellowingRoarMs, diffMs) && Cast(SpellBellowingRoar, Me))
            _bellowingRoarMs = RandomDelay(25000, 28000);
        return true;
    }
}
