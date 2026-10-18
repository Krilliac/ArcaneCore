using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Simone the Inconspicuous (14527), Un'Goro Crater, the fourth demon of The Stave of the Ancients (7636): mangos-classic ScriptDev2
/// <c>npc_simone_the_inconspicuous</c> (kalimdor/ungoro_crater.cpp:663-806). Unlike the other three, Simone does not change entry: the
/// demon is a separate summon. On every reset she takes her gossip back and has Precious (14528) follow her, summoning him when none is within
/// 100 yd. Asked for her real face (the shared <see cref="StaveDemonGossip"/>), she laughs, stops and loses her gossip; she shouts 5 s later,
/// and after 10 s, while the player is still on the map, summons Simone the Seductress (14533) and goes (respawning in 35 minutes, which the
/// demon's death or vanishing moves), and Precious goes too with Precious the Devourer (14538) in his place. In a fight she casts Fool's Plight
/// (23504) every 5-10 s.
/// </summary>
public sealed class SimoneTheInconspicuousAI(Creature creature) : ScriptedAI(creature), IStaveFriendlyForm
{
    public const uint Entry = 14527, NpcSimoneTheSeductress = 14533, NpcPrecious = 14528, NpcPreciousTheDevourer = 14538;
    public const uint SpellFoolsPlight = 23504, EmoteOneshotShout = 5, EmoteOneshotLaugh = 11;
    public const uint TransformMs = 10_000, TransformEmoteMs = 5000, RespawnWhileDemonSeconds = 35 * 60;
    public const float PreciousSearchRange = 100f, PetFollowDistance = 1f, PetFollowAngle = MathF.PI / 2f;

    private ObjectGuid _player;
    private bool _transform, _gatherPrecious;
    private uint _transformMs, _transformEmoteMs, _foolsPlightMs;

    public bool IsTransforming => _transform;

    protected override void Reset()
    {
        Me.NpcFlags = (uint)NpcFlags.Gossip;
        _foolsPlightMs = (uint)(System?.RandomInt(5000, 10_000) ?? 5000);
        _transformMs = TransformMs;
        _transformEmoteMs = TransformEmoteMs;
        _transform = false;
        _gatherPrecious = true;
    }

    /// <summary>
    /// The Precious part of the source's Reset, run on the first update after it: at the first spawn a database Precious next to her may be
    /// added to the map after her, and looking at once would summon a second one.
    /// </summary>
    private void GatherPrecious()
    {
        _gatherPrecious = false;
        if (System is not { } system)
        {
            return;
        }

        Creature? precious = system.ClosestOfEntry(Me, NpcPrecious, PreciousSearchRange, aliveOnly: true)
            ?? system.SummonDeadDespawn(Me, NpcPrecious, Me.X, Me.Y, Me.Z, Me.Orientation);
        precious?.Motion.MoveFollow(Me, PetFollowDistance, PetFollowAngle);
    }

    /// <summary>GossipSelect: CLOSE_GOSSIP_MENU, BeginEvent, then the laugh.</summary>
    public void BeginEvent(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_transform)
        {
            return;
        }

        _player = player.Guid;
        System?.MoveIdle(Me);
        Me.NpcFlags = 0;
        _transform = true;
        System?.PlayEmote(Me, EmoteOneshotLaugh);
    }

    private void Transform()
    {
        if (System is not { } system || system.Map.FindPlayer(_player) is null)
        {
            return;
        }

        Creature? demon = system.SummonDeadDespawn(Me, NpcSimoneTheSeductress, Me.X, Me.Y, Me.Z, Me.Orientation);
        Creature? precious = system.ClosestOfEntry(Me, NpcPrecious, PreciousSearchRange, aliveOnly: true);
        if (demon is null)
        {
            return;
        }

        if (demon.AI is SimoneTheSeductressAI seductress)
        {
            seductress.SimoneGuid = Me.Guid;
            if (precious is not null && system.SummonDeadDespawn(Me, NpcPreciousTheDevourer, precious.X, precious.Y, precious.Z, precious.Orientation) is { } devourer)
            {
                seductress.PreciousGuid = devourer.Guid;
                if (devourer.AI is PreciousTheDevourerAI devourerAi)
                {
                    devourerAi.SimoneGuid = demon.Guid;
                }
            }
        }

        if (precious is not null)
        {
            system.ForcedDespawn(precious, 0);
        }

        system.SetNextRespawnDelay(Me, RespawnWhileDemonSeconds);
        system.ForcedDespawn(Me, 0);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_gatherPrecious)
        {
            GatherPrecious();
        }

        if (_transform)
        {
            if (_transformEmoteMs != 0)
            {
                if (_transformEmoteMs <= diffMs)
                {
                    System?.PlayEmote(Me, EmoteOneshotShout);
                    _transformEmoteMs = 0;
                }
                else
                {
                    _transformEmoteMs -= diffMs;
                }
            }

            if (_transformMs < diffMs)
            {
                _transform = false;
                Transform();
                if (!Me.IsAlive)
                {
                    return;
                }
            }
            else
            {
                _transformMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_foolsPlightMs < diffMs)
        {
            if (DoCast(victim, SpellFoolsPlight) == CreatureCastResult.Ok)
            {
                _foolsPlightMs = (uint)(System?.RandomInt(5000, 10_000) ?? 5000);
            }
        }
        else
        {
            _foolsPlightMs -= diffMs;
        }
    }
}

/// <summary>
/// Simone the Seductress (14533): mangos-classic <c>npc_simone_seductress</c> (ungoro_crater.cpp:432-652). The same one-hunter rules as the
/// other Stave demons (<see cref="StaveDemonAI"/>), but across her and Precious the Devourer: either one's aggro brings the other in, a second
/// unit on her threat list (at once) or on Precious's (checked every 2 s) makes both vanish and leaves The Cleaner fighting both threat lists,
/// and her vanishing or death moves the friendly Simone's respawn (15 minutes or 2 hours). Out of combat she sends an engaged Precious home
/// every 2.5 s (first after 7.5 s), and back home she brings a new Precious the Devourer when hers is dead or gone. She casts Temptress' Kiss
/// (23205) at 3-6 s then every 45 s and Chain Lightning (23206) at 3-6 s then every 8-12 s; Viper Sting (Rank 3, 14280) silences her (23207)
/// with its emote. Left alone 20 minutes out of combat she vanishes without The Cleaner.
/// </summary>
public sealed class SimoneTheSeductressAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = SimoneTheInconspicuousAI.NpcSimoneTheSeductress;
    public const uint SpellChainLightning = 23206, SpellTemptressKiss = 23205, SpellSilence = 23207, SpellViperStingRank3 = 14280;
    public const uint TemptressKissMs = 45_000, ThreatCheckFirstMs = 5000, ThreatCheckMs = 2000, SplitCheckFirstMs = 7500, SplitCheckMs = 2500;
    public const int EmoteSilence = -1001250;

    private ObjectGuid _hunter;
    private uint _temptressKissMs, _chainLightningMs, _threatCheckMs, _splitCheckMs;
    private uint _despawnMs = StaveDemonAI.DemonLifetimeMs;

    /// <summary>The friendly Simone that summoned her (whose respawn she moves).</summary>
    public ObjectGuid SimoneGuid { get; set; }

    /// <summary>Her Precious the Devourer.</summary>
    public ObjectGuid PreciousGuid { get; set; }

    public ObjectGuid Hunter => _hunter;

    public uint DespawnMs => _despawnMs;

    protected override void Reset()
    {
        _hunter = default;
        _temptressKissMs = (uint)(System?.RandomInt(3000, 6000) ?? 3000);
        _chainLightningMs = (uint)(System?.RandomInt(3000, 6000) ?? 3000);
        _threatCheckMs = ThreatCheckFirstMs;
        _splitCheckMs = SplitCheckFirstMs;
    }

    private Creature? Precious => PreciousGuid.IsEmpty ? null : System?.FindCreature(PreciousGuid);

    private Creature? Simone => SimoneGuid.IsEmpty ? null : System?.FindCreature(SimoneGuid);

    /// <summary>JustReachedHome: a new Precious the Devourer when hers is dead or gone, then Reset.</summary>
    public override void OnReachedHome()
    {
        if (System is { } system && Precious is not { IsAlive: true })
        {
            if (Precious is { } dead)
            {
                system.ForcedDespawn(dead, 0);
            }

            if (system.SummonDeadDespawn(Me, SimoneTheInconspicuousAI.NpcPreciousTheDevourer, Me.X, Me.Y, Me.Z, Me.Orientation) is { } devourer)
            {
                PreciousGuid = devourer.Guid;
                if (devourer.AI is PreciousTheDevourerAI ai)
                {
                    ai.SimoneGuid = Me.Guid;
                }
            }
        }

        Reset();
    }

    public override void OnAggro(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Precious is { IsAlive: true } precious && !precious.Combat.IsInCombat)
        {
            precious.AI?.AttackStart(target);
        }

        if (target.Class == Class.Hunter && (_hunter.IsEmpty || _hunter == target.Guid))
        {
            _hunter = target.Guid;
        }
        else
        {
            DemonDespawn();
        }
    }

    /// <summary>JustDied: the friendly Simone respawns in two hours.</summary>
    public override void OnDeath(Unit? killer)
    {
        if (Simone is { } simone)
        {
            System?.SetRespawnIn(simone, StaveDemonAI.RespawnAfterDeathSeconds);
        }
    }

    public void DemonDespawn(bool triggered = true)
    {
        if (System is not { } system)
        {
            return;
        }

        if (Simone is { } simone)
        {
            system.SetRespawnIn(simone, StaveDemonAI.RespawnAfterVanishSeconds);
        }

        Creature? precious = Precious;
        if (triggered && system.SummonDeadDespawn(Me, StaveDemonAI.NpcTheCleaner, Me.X, Me.Y, Me.Z, 0f) is { } cleaner)
        {
            foreach (Unit unit in Living(Me).Concat(precious is null ? [] : Living(precious)).Distinct())
            {
                cleaner.AI?.AttackStart(unit);
            }
        }

        if (precious is not null)
        {
            system.ForcedDespawn(precious, 0);
        }

        system.ForcedDespawn(Me, 0);
    }

    private static IEnumerable<Unit> Living(Creature creature)
        => creature.Combat.HasThreatList ? [.. creature.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive)] : [];

    private static int ThreatCount(Creature creature) => creature.Combat.HasThreatList ? creature.Combat.Threat.Entries.Count : 0;

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellViperStingRank3 && DoCast(Me, SpellSilence, triggered: true) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, EmoteSilence);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_despawnMs < diffMs)
        {
            if (Me.IsAlive && !Me.Combat.IsInCombat)
            {
                DemonDespawn(triggered: false);
                return;
            }
        }
        else
        {
            _despawnMs -= diffMs;
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            if (_splitCheckMs < diffMs)
            {
                _splitCheckMs = SplitCheckMs;
                if (Precious is { } precious && precious.Combat.IsInCombat)
                {
                    System?.EnterEvadeMode(precious);
                }
            }
            else
            {
                _splitCheckMs -= diffMs;
            }

            return;
        }

        if (ThreatCount(Me) > 1)
        {
            DemonDespawn();
            return;
        }

        if (_threatCheckMs < diffMs)
        {
            _threatCheckMs = ThreatCheckMs;
            if (Precious is { } precious && ThreatCount(precious) > 1)
            {
                DemonDespawn();
                return;
            }
        }
        else
        {
            _threatCheckMs -= diffMs;
        }

        if (_temptressKissMs < diffMs)
        {
            if (DoCast(victim, SpellTemptressKiss) == CreatureCastResult.Ok)
            {
                _temptressKissMs = TemptressKissMs;
            }
        }
        else
        {
            _temptressKissMs -= diffMs;
        }

        if (_chainLightningMs < diffMs)
        {
            if (DoCast(victim, SpellChainLightning) == CreatureCastResult.Ok)
            {
                _chainLightningMs = (uint)(System?.RandomInt(8000, 12_000) ?? 8000);
            }
        }
        else
        {
            _chainLightningMs -= diffMs;
        }
    }
}

/// <summary>
/// Precious the Devourer (14538): mangos-classic <c>npc_precious_the_devourer</c> (ungoro_crater.cpp:363-421). Its aggro brings Simone the
/// Seductress in; it goes when it evades with her dead; out of combat it sends an engaged Simone home every 2.5 s (first after 7.5 s).
/// </summary>
public sealed class PreciousTheDevourerAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = SimoneTheInconspicuousAI.NpcPreciousTheDevourer;

    private uint _splitCheckMs;

    /// <summary>Its Simone the Seductress.</summary>
    public ObjectGuid SimoneGuid { get; set; }

    private Creature? Simone => SimoneGuid.IsEmpty ? null : System?.FindCreature(SimoneGuid);

    protected override void Reset() => _splitCheckMs = SimoneTheSeductressAI.SplitCheckFirstMs;

    public override void OnAggro(Unit target)
    {
        if (Simone is { IsAlive: true } simone && !simone.Combat.IsInCombat)
        {
            simone.AI?.AttackStart(target);
        }
    }

    public override void OnEvade()
    {
        if (Simone is { IsAlive: false })
        {
            System?.ForcedDespawn(Me, 0);
        }

        base.OnEvade();
    }

    public override void OnUpdate(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (_splitCheckMs < diffMs)
        {
            _splitCheckMs = SimoneTheSeductressAI.SplitCheckMs;
            if (Simone is { } simone && simone.Combat.IsInCombat)
            {
                System?.EnterEvadeMode(simone);
            }
        }
        else
        {
            _splitCheckMs -= diffMs;
        }
    }
}
