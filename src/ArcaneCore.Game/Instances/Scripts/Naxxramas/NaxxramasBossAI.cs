using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Arachnid and Plague encounters. Spell IDs, timings, and transitions are independently
/// implemented from vmangos eastern_kingdoms/eastern_plaguelands/naxxramas/
/// boss_{anubrekhan,faerlina,maexxna,noth,heigan,loatheb}.cpp Reset/Aggro/UpdateAI,
/// with the 1.12 spell list cross checked against mangos-classic ScriptDevAI/scripts/
/// eastern_kingdoms/naxxramas/boss_*.cpp ExecuteAction. No GPL implementation copied.
/// </summary>
public sealed class NaxxramasBossAI : RaidCreatureAI
{
    private readonly uint _entry;
    private uint _phaseTimer, _secondaryTimer, _doomCount, _phaseCount, _balconyWaves, _corpseTimer;
    private uint _eruptionStep, _manaBurnTimer;
    private bool _balcony, _dance, _enraged, _embraced;
    private bool _sporeWest;
    private readonly List<Creature> _adds = [];
    private readonly List<Creature> _deadGuards = [];
    private sealed class PendingWrap(Player target)
    {
        public Player Target = target;
        public uint Timer = 2000;
        public bool Cocoon;
    }
    private readonly List<PendingWrap> _wraps = [];
    private readonly List<uint> _portTimers = [];
    private readonly HashSet<ObjectGuid> _portedThisRotation = [];
    // mangos-classic boss_noth.cpp ExecuteAction balcony: UNIT_FLAG_UNINTERACTIBLE | UNIT_FLAG_IMMUNE_TO_PLAYER
    // on top of Immune All (29230), removed again on the return to the ground.
    private const UnitFlags BalconyFlags = UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
    private static readonly (float X, float Y, float Z)[] WrapWalls =
    [
        (3562.40f,-3890.35f,314.30f),(3560.78f,-3878.10f,316.18f),
        (3554.95f,-3863.24f,314.46f),(3549.02f,-3855.07f,311.58f),
        (3538.34f,-3844.68f,314.21f),(3526.43f,-3838.73f,317.10f),
        (3507.84f,-3832.71f,319.00f),(3493.35f,-3834.06f,318.71f),
    ];

    public NaxxramasBossAI(Creature creature, NaxxramasInstance instance)
        : base(creature, instance, creature.Entry switch
        {
            15956 => NaxxramasInstance.AnubRekhan, 15953 => NaxxramasInstance.Faerlina,
            15952 => NaxxramasInstance.Maexxna, 15954 => NaxxramasInstance.Noth,
            15936 => NaxxramasInstance.Heigan, 16011 => NaxxramasInstance.Loatheb,
            _ => throw new ArgumentOutOfRangeException(nameof(creature))
        })
    {
        _entry = creature.Entry;
        Reset();
    }

    public bool IsOnBalcony => _balcony;
    public bool IsDancing => _dance;
    private Unit? PlayerTarget() => RandomTarget(u => u is Player);

    protected override void Reset()
    {
        base.Reset();
        _balcony = _dance = _enraged = _embraced = false;
        _sporeWest = false;
        _doomCount = _phaseCount = _eruptionStep = _balconyWaves = 0;
        _secondaryTimer = 0;
        _deadGuards.Clear();
        _corpseTimer = _entry == 15956 ? Random(20000, 80000) : 0;
        foreach (PendingWrap wrap in _wraps) System?.RemoveAuras(wrap.Target, 28622);
        _wraps.Clear();
        _phaseTimer = _entry switch
        {
            15956 => Random(80000, 120000), 15953 => 60000,
            15952 => 40000, 15954 => 90000, 15936 => 90000,
            16011 => 120000, _ => 0,
        };
        if (_entry == 15954) Me.UnitFlags &= ~BalconyFlags;
        if (_entry == 16011)
            foreach (Player player in Raid.Instance.Players) System?.RemoveAuras(player, 29232);
        foreach (Creature add in _adds.ToArray()) System?.Despawn(add);
        _adds.Clear();
        switch (_entry)
        {
            case 15956: // boss_anubrekhanAI: impale, locust, guard.
                Cast(18943, triggered: true);
                Schedule(12000, 18000, 12000, 18000, () =>
                    !(System?.HasAura(Me, 28785) ?? false) && Cast(28783, PlayerTarget() ?? Victim));
                break;
            case 15953: // boss_faerlinaAI: poison bolt volley, rain, sixty-second enrage.
                Spell(28796, 8000, 8000, 10000, 12000, () => _embraced ? null : Victim);
                Spell(28794, 16000, 16000, 8000, 12000, PlayerTarget);
                break;
            case 15952: // boss_maexxnaAI: spray/poisons/spiderlings and web wraps.
                Cast(19818, triggered: true);
                Spell(28741, 9000, 11000, 9000, 11000, () => Victim);
                Spell(28776, 15000, 15000, 5000, 10000, () => Victim);
                Spell(29434, 30000, 30000, 40000, 40000);
                Schedule(20000, 20000, 40000, 40000, WebWrap);
                break;
            case 15954: // boss_nothAI: curses/blinks/three warriors, balcony waves.
                NothGroundActions(false);
                break;
            case 15936: // boss_heiganAI: fever, mana burn and slow/fast eruptions.
                _portedThisRotation.Clear();
                HeiganGroundActions(afterDance: false);
                break;
            case 16011: // boss_loathebAI: healing restriction, poison, spores and doom.
                Spell(29201, 5000, 5000, 10000, 10000);
                Spell(29865, 5000, 5000, 12000, 12000);
                Spell(30281, 5000, 5000, 30000, 30000);
                Schedule(13000, 13000, 13000, 13000, Spore);
                break;
        }
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target); // sets the encounter in progress and pulls the zone
        if (_entry == 15956)
        {
            Cast(29103, triggered: true);
            // creature_linking_template (16573, 533, 15956, flags 1031) FLAG_AGGRO_ON_AGGRO: the database guards join the pull.
            foreach (Creature guard in StaticGuards().Where(c => c.IsAlive))
                guard.AI?.AttackStart(target);
        }
        if (_entry == 16011) _sporeWest = Random(0, 1) == 0;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry == 17293) return; // Heigan's short-lived fissure trigger.
        if (!_adds.Contains(summoned)) _adds.Add(summoned);
        if (_entry == 15952 && summoned.Entry == 17055 && PlayerTarget() is { } target)
            summoned.AI?.AttackStart(target);
        if (_entry == 15954 && summoned.Entry is 16981 or 16982 or 16983 or 16984
            && (PlayerTarget() ?? Victim) is { } raider)
            summoned.AI?.AttackStart(raider);
    }

    // Anub'Rekhan's two pre-pull Crypt Guards are world content, not script summons. ClassicDB z2815 spawns them on map 533
    // (GUIDs 5331024 and 5331025, the two points vmangos boss_anubrekhanAI::CheckSpawnInitialCryptGuards summons at) and links them
    // to the boss with creature_linking_template (16573, 533, 15956, 1031). mangos-classic boss_anubrekhan.cpp relies on exactly
    // that and never summons them; summoning as well (the vmangos form, written for a database without the spawns) doubled them.
    private IEnumerable<Creature> StaticGuards()
        => System?.Creatures.Where(c => c.Entry == 16573 && c.Spawn is not null).ToArray() ?? [];

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (_entry != 15953 || spell.Id != 28732) return;
        // vmangos boss_faerlinaAI::SpellHit: a mind-controlled worshipper's Widow's Embrace removes
        // the enrage and delays its return; volley waits while the 30-second embrace lasts. In 1.12 a
        // worshipper that simply dies does nothing (its self-kill is WidowsEmbraceScript).
        System?.RemoveAuras(Me, 28798);
        _embraced = true;
        _phaseTimer = Math.Max(_phaseTimer, 30000);
        _secondaryTimer = 30000;
    }

    public void CryptGuardDied(Creature guard)
    {
        if (_entry == 15956 && !_deadGuards.Contains(guard)) _deadGuards.Add(guard);
    }

    public override void OnEvade()
    {
        // mangos-classic boss_anubrekhanAI::EnterEvadeMode: the summoned guards go first (Reset despawns them, SPELL_DESPAWN_GUARDS
        // 29379), then the linked database guards come back (FLAG_RESPAWN_ON_EVADE): dead or exploded ones respawn, living ones evade.
        base.OnEvade(); // resets and marks the encounter failed unless it is done
        if (_entry != 15956 || System is not { } system) return;
        foreach (Creature guard in StaticGuards())
        {
            if (!guard.IsAlive) system.ForceRespawn(guard);
            else if (guard.Combat.IsInCombat) guard.AI?.EnterEvadeMode();
        }
    }

    private Creature? Spawn(uint entry, float x, float y, float z, Unit? attack = null, bool track = true, float orientation = 0)
    {
        if (System?.Content.FindTemplate(entry) is not { } template) return null;
        Creature add = System.SpawnTemporary(template, x, y, z, orientation, Me);
        if (track && !_adds.Contains(add)) _adds.Add(add);
        if (attack is not null && add.AI is not null) add.AI.AttackStart(attack);
        return add;
    }

    private bool Blink()
    {
        if (_balcony) return true;
        Cast(29212, triggered: true);
        if (!Cast((uint)(29208 + Random(0, 3)), Me, triggered: true)) return false;
        foreach (var threat in Me.Combat.Threat.Entries.ToArray())
            Me.Combat.Threat.ModifyThreatPercent(threat.Target, -100);
        if (PlayerTarget() is { } target) Me.Combat.Threat.AddThreat(target, 1);
        return true;
    }

    private void NothGroundActions(bool afterBalcony)
    {
        ClearActions();
        Spell(29213, afterBalcony ? 2000u : 8000u, afterBalcony ? 10000u : 12000u,
            50000, 60000, () => Victim);
        Schedule(afterBalcony ? 2000u : 30000u, afterBalcony ? 10000u : 40000u,
            30000, 40000, Blink);
        Schedule(afterBalcony ? 2000u : 10000u, 10000, 30000, 30000, Warriors);
    }

    private bool Warriors()
    {
        if (_balcony) return true;
        foreach (uint spell in new uint[] { 29247, 29248, 29249 }) Cast(spell, triggered: true);
        return true;
    }

    private bool Spore()
    {
        // vmangos boss_loathebAI::Aggro/UpdateAI: one fixed side chosen for this pull.
        // ClassicDB's spore EventAI casts Fungal Bloom (29232) on death.
        float x = _sporeWest ? 2951f : 2870f;
        float y = _sporeWest ? -4016f : -3978f;
        return Spawn(16286, x, y, 274f, PlayerTarget() ?? Victim) is not null;
    }

    private void HeiganGroundActions(bool afterDance)
    {
        // vmangos boss_heiganAI::Aggro and EventDanceEnd: fever at 30 s (5 s after a dance) then every
        // 20-25 s, mana burn from 15 s (10 s), one-shot player ports at 40 s, or at 18 and 48 s after a dance.
        ClearActions();
        Spell(29998, afterDance ? 5000u : 30000u, afterDance ? 5000u : 30000u, 20000, 25000);
        _manaBurnTimer = afterDance ? 10000u : 15000u;
        _secondaryTimer = afterDance ? 10000u : 15000u;
        _portTimers.Clear();
        if (afterDance) _portTimers.AddRange([18000, 48000]);
        else _portTimers.Add(40000);
    }

    private void PortPlayers()
    {
        // vmangos boss_heiganAI::EventPortPlayer: skip the tank, then up to three random living players
        // not yet ported since the last dance started. mangos-classic boss_heigan.cpp SPELL_TELEPORT_PLAYERS
        // 29273; ClassicDB spell_target_position 29273 owns the same-map destination.
        List<Player> candidates = Me.Combat.Threat.Entries.Skip(1).Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && !_portedThisRotation.Contains(p.Guid)).ToList();
        for (int i = 0; i < 3 && candidates.Count > 0; i++)
        {
            Player player = candidates[(int)Random(0, (uint)candidates.Count - 1)];
            candidates.Remove(player);
            _portedThisRotation.Add(player.Guid);
            // ClassicDB z2815 spell 29273: TELEPORT_UNITS at UNIT_CASTER to its spell_target_position, so the player is the caster
            // (mangos-classic boss_heigan.cpp: target->CastSpell(target, SPELL_TELEPORT_PLAYERS)). Cast by Heigan it moved Heigan.
            System?.CastSpellByUnit(player, 29273, player, triggered: true);
        }
    }

    private bool ManaBurn()
    {
        // vmangos boss_heiganAI::CheckManausersAndRepeat: Mana Burn (29310, an area around Heigan) only when a living mana user on
        // the threat list is within 28 yd of him (3D, centre to centre); 3 s after a cast, otherwise looked at again in 1 s.
        bool inRange = Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>().Any(p =>
            p.IsAlive && p.PowerType == PowerType.Mana && ReferenceEquals(p.Map, Me.Map)
            && DistanceSquared(p, Me) < ManaBurnReach * ManaBurnReach);
        return inRange && Cast(29310, Me);
    }

    private const float ManaBurnReach = 28f;

    // vmangos naxxramas.h eyeStalkPossitions: EventStartDance summons a plague-wave creature (17293) at each eye stalk for the
    // 45-second dance, each casting Plague Wave (30243) on itself.
    private static readonly (float X, float Y, float Z, float O)[] EyeStalks =
    [
        (2761.28f,-3765.37f,275.08f,1.24f),(2770.17f,-3782.11f,275.08f,1.33f),(2798.11f,-3788.94f,275.08f,2.35f),
        (2797.91f,-3776.86f,275.08f,2.25f),(2792.06f,-3762.52f,275.08f,2.9f),(2789.87f,-3752.15f,275.08f,2.74f),
        (2804.21f,-3757.96f,275.08f,3.9f),(2821.16f,-3759.75f,275.08f,4.47f),(2834.64f,-3751.23f,275.08f,4.27f),
        (2843.54f,-3768.08f,275.08f,3.06f),(2862.4f,-3758.3f,275.08f,4.8f),(2877.8f,-3762.46f,275.08f,4.8f),
        (2894.11f,-3757.89f,275.08f,4.56f),(2895.25f,-3779.5f,275.08f,2.4f),(2881.59f,-3782.22f,275.08f,2.79f),
        (2867.2f,-3778.21f,275.08f,3.01f),(2851.39f,-3776.54f,275.08f,2.69f),(2846.16f,-3789.13f,275.08f,1.79f),
        (2830.09f,-3776.49f,275.08f,0.94f),(2813.34f,-3780.97f,275.08f,1.84f),
    ];

    private void SummonPlagueWaves()
    {
        // vmangos boss_heiganAI::SummmonPlagueWave: TEMPSUMMON_TIMED_DESPAWN 45000, pCloud->CastSpell(pCloud, SPELL_PLAGUE_WAVE, true).
        foreach (var stalk in EyeStalks)
        {
            if (Spawn(17293, stalk.X, stalk.Y, stalk.Z, track: false, orientation: stalk.O) is not { } wave) continue;
            System?.CastSpell(wave, 30243, wave, true);
            System?.ForcedDespawn(wave, 45000);
        }
    }

    private bool WebWrap()
    {
        // vmangos boss_maexxnaAI::DoCastWebWrap: up to three random non-tank players who are not
        // already wrapped, each sent to its own wall point.
        // The 40-second cooldown restarts whether or not anyone was wrapped (vmangos UpdateAI: m_uiWebWrapTimer = WebWrapCooldown()
        // after DoCastWebWrap, "probably no point checking if successfull"), so an empty pool is still a completed action.
        List<Player> pool = Me.Combat.Threat.Entries.Skip(1).Select(e => e.Target)
            .OfType<Player>().Where(p => p.IsAlive && !p.IsGameMaster && !(System?.HasAura(p, 28622) ?? false)).ToList();
        if (pool.Count == 0) return true;
        var candidates = new List<Player>();
        while (candidates.Count < 3 && pool.Count > 0)
        {
            Player pick = pool[(int)Random(0, (uint)pool.Count - 1)];
            pool.Remove(pick);
            candidates.Add(pick);
        }
        int firstWall = (int)Random(0, (uint)WrapWalls.Length - 1);
        for (int i = 0; i < candidates.Count; i++)
        {
            Player player = candidates[i];
            var wall = WrapWalls[(firstWall + i) % WrapWalls.Length];
            // vmangos boss_maexxnaAI::DoCastWebWrap/UpdateWraps: flight lasts 2 s,
            // web stun follows, cocoon appears 3 s later. A same-map teleport
            // lands at the script's wall point; ArcaneCore has no scripted knockback AI seam.
            var teleport = Raid.Instance.FindUpdater<GameObjectMapSystem>()?.Teleports ?? new NearTeleportSink();
            if (teleport.Teleport(player, 533, wall.X, wall.Y, wall.Z, player.Orientation))
                _wraps.Add(new PendingWrap(player));
        }
        return true;
    }

    private void ProcessWraps(uint diffMs)
    {
        foreach (PendingWrap wrap in _wraps.ToArray())
        {
            if (!wrap.Target.IsAlive || !ReferenceEquals(wrap.Target.Map, Me.Map))
            { _wraps.Remove(wrap); continue; }
            if (!Due(ref wrap.Timer, diffMs)) continue;
            if (!wrap.Cocoon)
            {
                // ClassicDB z2815 spell 28622: both effects APPLY_AURA (stun, periodic damage) at UNIT_CASTER, so the wrapped player casts
                // it on itself (vmangos boss_maexxnaAI::UpdateWraps: pl->CastSpell(pl, 28622, true)).
                System?.CastSpellByUnit(wrap.Target, 28622, wrap.Target, triggered: true);
                wrap.Cocoon = true;
                wrap.Timer = 3000;
            }
            else
            {
                Spawn(16486, wrap.Target.X, wrap.Target.Y, wrap.Target.Z);
                _wraps.Remove(wrap);
            }
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (_entry == 15952) ProcessWraps(diffMs);
        if (_entry == 15953 && _embraced && Due(ref _secondaryTimer, diffMs)) _embraced = false;
        if (_entry == 15956 && Due(ref _corpseTimer, diffMs))
        {
            if (_deadGuards.Count > 0)
            {
                Creature corpse = _deadGuards[(int)Random(0, (uint)_deadGuards.Count - 1)];
                _deadGuards.Remove(corpse);
                if (corpse.Map == Me.Map)
                {
                    ((NaxxramasInstance)Raid).ExplodeCryptGuard(corpse);
                    System?.ForcedDespawn(corpse, 250);
                }
                _corpseTimer = Random(20000, 80000);
            }
            else _corpseTimer = Random(10000, 20000);
        }
        if (_entry == 15952 && !_enraged && Below(30) && Cast(28747)) _enraged = true;

        if (_entry == 15954 && _balcony)
        {
            if (Due(ref _secondaryTimer, diffMs))
            {
                Cast(29231, Me, triggered: true);
                System?.RemoveAuras(Me, 29230);
                Me.UnitFlags &= ~BalconyFlags;
                _balcony = false;
                SetMeleeEnabled(true);
                CombatMovement = true;
                _phaseCount++;
                _phaseTimer = _phaseCount == 1 ? 110000u : 180000u;
                NothGroundActions(true);
            }
            else if (_balconyWaves < 2 && Due(ref _phaseTimer, diffMs))
            {
                SpawnBalconyWave();
                _balconyWaves++;
                _phaseTimer = _phaseCount switch
                {
                    0 => Random(25000, 30000),
                    1 => Random(44000, 49000),
                    _ => Random(57000, 62000),
                };
            }
            return;
        }
        if (_entry == 15936 && Due(ref _secondaryTimer, diffMs))
        {
            Erupt();
            _secondaryTimer = _dance ? 3000u : 10000u;
        }
        if (_entry == 15936 && !_dance && Due(ref _manaBurnTimer, diffMs))
            _manaBurnTimer = ManaBurn() ? 3000u : 1000u;
        if (_entry == 15936 && !_dance)
            for (int i = _portTimers.Count - 1; i >= 0; i--)
            {
                uint timer = _portTimers[i];
                if (Due(ref timer, diffMs)) { _portTimers.RemoveAt(i); PortPlayers(); }
                else _portTimers[i] = timer;
            }

        if (Due(ref _phaseTimer, diffMs)) AdvancePhase();
        if (!_dance && !_balcony) TickActions(diffMs);
    }

    private void SpawnBalconyWave()
    {
        // vmangos boss_nothAI::Summon4Champions/Summon2Guardians/Summon3Constructs.
        Cast(Random(0, 1) == 0 ? 29217u : 29227u, triggered: true);
        Cast(Random(0, 1) == 0 ? 29224u : 29225u, triggered: true);
        Cast(Random(0, 1) == 0 ? 29258u : 29262u, triggered: true);
        Cast(new uint[] { 29255, 29257, 29267, 29238 }[(int)Random(0, 3)], triggered: true);
        if (_phaseCount >= 1)
        {
            Cast(Random(0, 1) == 0 ? 29226u : 29239u, triggered: true);
            Cast(Random(0, 1) == 0 ? 29256u : 29268u, triggered: true);
        }
        if (_phaseCount >= 2)
        {
            Spawn(16982, 2649f, -3456f, 264f, PlayerTarget() ?? Victim);
            Spawn(16982, 2727f, -3458f, 263.5f, PlayerTarget() ?? Victim);
            Spawn(16982, 2727f, -3534f, 268f, PlayerTarget() ?? Victim);
        }
    }

    private void Erupt()
    {
        // vmangos boss_heiganAI::UpdateEruption: safe sections run 0,1,2,3,2,1;
        // each other section receives fissure casts. Coordinates are sect*SafeSpot.
        (float X, float Y, float Z)[][] sections =
        [
            [(2799.5f,-3691f,273.62f),(2810.67f,-3706.06f,275f),(2803.51f,-3697.42f,274.1f)],
            [(2790.51f,-3690.45f,273.622f)],
            [(2778.40f,-3702.645f,273.621f)],
            [(2777.2f,-3712.41f,273.63f),(2783.06f,-3717.7f,273.63f),(2791.62f,-3726.04f,273.63f)],
        ];
        int step = (int)(_eruptionStep++ % 6);
        int safe = step <= 3 ? step : 6 - step;
        // vmangos boss_heiganAI::UpdateEruption uses a short-lived plague-wave
        // creature to trip every imported trap outside the safe section.
        if (Spawn(17293, 2773f, -3684f, 292f, track: false) is { } controller)
        {
            ((NaxxramasInstance)Raid).ActivateHeiganTraps(safe, controller);
            System?.ForcedDespawn(controller, 1000);
        }
        for (int section = 0; section < sections.Length; section++)
        {
            if (section == safe) continue;
            foreach (var point in sections[section]) EruptAt(point.X, point.Y, point.Z);
        }
        if (_dance)
            foreach (var point in new (float X, float Y, float Z)[]
                { (2747f,-3754f,274f),(2805.8f,-3695.88f,273.61f),(2812.95f,-3703.52f,273.61f) })
                EruptAt(point.X, point.Y, point.Z);
    }

    private void EruptAt(float x, float y, float z)
    {
        // The fissure trigger must exist in world content. Never substitute a raid-wide cast.
        if (Spawn(17293, x, y, z, track: false) is not { } fissure) return;
        System?.CastSpell(fissure, 29371, fissure, true);
        System?.ForcedDespawn(fissure, 1000);
    }

    private void AdvancePhase()
    {
        switch (_entry)
        {
            case 15956:
                if (Cast(28785))
                {
                    Spawn(16573, 3316.46f, -3476.23f, 287.26f, PlayerTarget() ?? Victim);
                    _phaseTimer = Random(90000, 110000);
                }
                break;
            case 15953:
                if (!_embraced && Cast(28798)) _phaseTimer = 60000;
                break;
            case 15952:
                if (Cast(29484, Victim)) _phaseTimer = 40000;
                break;
            case 15954:
                if (Cast(29216, Me, triggered: true))
                {
                    Cast(29230, Me, triggered: true);
                    Me.UnitFlags |= BalconyFlags;
                    _balcony = true;
                    SetMeleeEnabled(false);
                    CombatMovement = false;
                    System?.MoveIdle(Me);
                    _balconyWaves = 0;
                    _phaseTimer = Random(5000, 7000);
                    _secondaryTimer = 70000 + _phaseCount * 25000;
                }
                break;
            case 15936:
                if (!_dance)
                {
                    if (!Cast(30211, Me, triggered: true)) break;
                    _dance = true;
                    _eruptionStep = 0;
                    SetMeleeEnabled(false); CombatMovement = false;
                    System?.MoveIdle(Me);
                    Cast(29350, Me, triggered: true);
                    SummonPlagueWaves();
                    _portedThisRotation.Clear(); // vmangos EventStartDance: portedPlayersThisPhase.clear()
                    _portTimers.Clear();
                    _phaseTimer = 45000;
                    _secondaryTimer = 4000;
                }
                else
                {
                    _dance = false;
                    _eruptionStep = 0;
                    SetMeleeEnabled(true); CombatMovement = true;
                    System?.RemoveAuras(Me, 29350);
                    _phaseTimer = 90000;
                    HeiganGroundActions(afterDance: true);
                }
                break;
            case 16011:
                if (Cast(29204))
                {
                    _doomCount++;
                    _phaseTimer = _doomCount > 6 ? 15000u : 30000u;
                }
                break;
        }
    }
}
