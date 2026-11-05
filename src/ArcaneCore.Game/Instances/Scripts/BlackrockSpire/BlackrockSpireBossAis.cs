using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockSpire;

/// <summary>boss_gythAI (mangos-classic blackrock_spire/boss_gyth.cpp: Reset, UpdateAI).</summary>
public sealed class GythAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellSummonRend = 16328;
    private uint _acid, _freeze, _breath, _knock;
    private bool _chromaticChaos, _summonedRend;

    public override void OnRespawn()
    {
        _acid = 8_000;
        _freeze = 11_000;
        _breath = 4_000;
        _knock = 23_000;
        _chromaticChaos = _summonedRend = false;
        Me.InvincibilityHpThreshold = 1; // ScriptedAI::SetDeathPrevention(true)
        DoCast(Me, 16167, triggered: true); // Rend mounts Gyth
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry == BlackrockSpireInstance.NpcRend)
        {
            (Me.Map?.FindUpdater<BlackrockSpireInstance>())?.TrackRend(summoned);
            summoned.System?.SayText(summoned, -1229019);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (!_chromaticChaos && (ulong)Me.Health * 2 < Me.MaxHealth
            && Me.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Template.Entry == BlackrockSpireInstance.NpcNefarius && c.IsAlive) is { } nefarius)
        {
            if (nefarius.System?.CastSpell(nefarius, 16337, Me, triggered: true) == CreatureCastResult.Ok)
            {
                nefarius.System.SayText(nefarius, -1229017);
                _chromaticChaos = true;
            }
        }

        CastWhenReady(ref _acid, diffMs, Me, 16359, 7_000, 7_000);
        CastWhenReady(ref _freeze, diffMs, Me, 16350, 16_000, 16_000);
        CastWhenReady(ref _breath, diffMs, Me, 16390, 10_500, 10_500);
        CastWhenReady(ref _knock, diffMs, Victim, 10101, 23_000, 23_000);

        if (!_summonedRend && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 11)
        {
            // boss_gyth: on CAST_OK of Summon Rend Blackhand (16328: SUMMON_WILD 10429, instant) Gyth stops preventing his death and drops
            // the mount aura; Rend arrives through the spell (JustSummoned). Only when the spell cannot be cast at all here (no spell
            // table row, no spell system) is Rend placed directly; any other failure retries on the next update.
            CreatureCastResult cast = DoCast(Me, SpellSummonRend);
            bool summoned = cast == CreatureCastResult.Ok;
            if (cast is CreatureCastResult.UnknownSpell or CreatureCastResult.NoSpellSystem
                && System?.SummonDeadDespawn(Me, BlackrockSpireInstance.NpcRend, Me.X, Me.Y, Me.Z, Me.Orientation) is { } rend)
            {
                Me.Map?.FindUpdater<BlackrockSpireInstance>()?.TrackRend(rend);
                summoned = true;
            }

            if (summoned)
            {
                _summonedRend = true;
                Me.InvincibilityHpThreshold = 0;
                System?.RemoveAuras(Me, 16167);
            }
        }
    }
}

/// <summary>boss_pyroguard_emberseerAI (mangos-classic blackrock_spire/boss_pyroguard_emberseer.cpp: Reset, DoHandleEmberseerGrowing, UpdateAI).</summary>
public sealed class PyroguardEmberseerAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _encage, _nova, _buffet, _pyro;
    private int _growingStacks;

    public override void OnRespawn()
    {
        _encage = 10_000;
        _nova = 6_000;
        _buffet = 3_000;
        _pyro = 14_000;
        _growingStacks = 0;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
        DoCast(Me, 13377, triggered: true);
    }

    /// <summary>cmangos AI_EVENT_CUSTOM_A (AIDefines.h), sent by Emberseer Growing 16049 (SpellEffects.cpp, DUMMY case 16049).</summary>
    public const uint AiEventCustomA = 1000;

    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType != AiEventCustomA || ++_growingStacks > 20)
        {
            return;
        }

        if (_growingStacks == 10)
        {
            System?.SayText(Me, -1229001);
        }
        else if (_growingStacks == 20)
        {
            System?.SayText(Me, -1229002);
            System?.SayText(Me, -1229003);
            DoCast(Me, 16047, triggered: true);
            DoCast(Me, 16534, triggered: true);
            DoCast(Me, 16052, triggered: true);
            if (Me.Map?.FindUpdater<BlackrockSpireInstance>() is { } spire)
            {
                spire.UseEmberseerRunes(reset: false);
                // Redundant in the reference too: the event is in progress before the boss is fully grown, so the altar cannot start it again.
                spire.SetData(BlackrockSpireInstance.TypeEmberseer, EncounterState.InProgress);
            }

            Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
        }
    }

    public override void OnReachedHome()
        => Me.Map?.FindUpdater<BlackrockSpireInstance>()?.SetData(BlackrockSpireInstance.TypeEmberseer, EncounterState.Fail);

    public override void OnDeath(Unit? killer)
        => Me.Map?.FindUpdater<BlackrockSpireInstance>()?.SetData(BlackrockSpireInstance.TypeEmberseer, EncounterState.Done);

    public override void OnUpdate(uint diffMs)
    {
        if (_encage > 0)
        {
            if (diffMs >= _encage)
            {
                foreach (Creature incarcerator in System?.Creatures.Where(c => c.Template.Entry == BlackrockSpireInstance.NpcIncarcerator && c.IsAlive).ToArray() ?? [])
                {
                    incarcerator.System?.CastSpell(incarcerator, 15281, Me, triggered: false);
                }

                _encage = 0;
            }
            else
            {
                _encage -= diffMs;
            }
        }

        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _nova, diffMs, Me, 23462, 6_000, 6_000);
        CastWhenReady(ref _buffet, diffMs, Me, 23341, 14_000, 14_000);
        CastWhenReady(ref _pyro, diffMs, RandomThreatTarget(), 20228, 15_000, 15_000);
    }
}

/// <summary>boss_overlordwyrmthalakAI (mangos-classic blackrock_spire/boss_overlord_wyrmthalak.cpp: Reset, JustSummoned, UpdateAI).</summary>
public sealed class OverlordWyrmthalakAI(Creature creature) : ScriptDevBossAI(creature)
{
    private static readonly uint[] Adds = [9583, 9268, 9216];
    private uint _blast, _shout, _cleave, _knock;
    private bool _summoned;

    public override void OnRespawn()
    {
        _blast = 20_000;
        _shout = 2_000;
        _cleave = 6_000;
        _knock = 12_000;
        _summoned = false;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        // JustSummoned: SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0), else the victim (boss_overlord_wyrmthalak.cpp:69-79).
        if ((RandomThreatTarget() ?? Victim) is { } target)
        {
            summoned.AI?.AttackStart(target);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _blast, diffMs, Me, 11130, 20_000, 20_000);
        CastWhenReady(ref _shout, diffMs, Me, 23511, 10_000, 10_000);
        CastWhenReady(ref _cleave, diffMs, Victim, 20691, 7_000, 7_000);
        CastWhenReady(ref _knock, diffMs, Me, 20686, 14_000, 14_000);

        if (!_summoned && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 51)
        {
            _summoned = true;
            for (int i = 0; i < 2; i++)
            {
                uint entry = Adds[System?.RandomInt(0, 2) ?? 0];
                float x = i == 0 ? -51.6805f : -54.4554f;
                if (System?.SummonDeadDespawn(Me, entry, x, -439.8f, 78.288f, 4.657f) is { } add)
                {
                    System.MarkTimedDespawn(add, 300_000); // boss_overlord_wyrmthalak.cpp:126-127, TEMPSPAWN_TIMED_DESPAWN 300000
                    add.Motion.MovePoint(0, i == 0 ? -39.355381f : -49.875881f,
                        i == 0 ? -513.456482f : -511.896942f, i == 0 ? 88.472046f : 88.195160f, run: true);
                }
            }
        }
    }
}

/// <summary>
/// boss_warmastervooneAI (vmangos blackrock_spire/boss_warmaster_voone.cpp: Reset, SpellHitTarget, UpdateAI). Throw Axe (16075) is cast
/// every 8 s until his two axes are gone: the first landed axe leaves him one (item 12348 in the main hand, off hand empty), the second
/// leaves him unarmed (both hands empty, Unarmed Passive 16076), and with that aura he throws no more. The ClassicDB EventAI rows had
/// the same six timers but no weapon swap; registering this AI replaces them.
/// </summary>
public sealed class WarmasterVooneAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellSnapKick = 15618, SpellCleave = 15284, SpellUppercut = 10966, SpellMortalStrike = 15708,
        SpellPummel = 15615, SpellThrowAxe = 16075, SpellUnarmedPassive = 16076, ItemRemainingAxe = 12348;

    private uint _snapKick, _cleave, _uppercut, _mortalStrike, _pummel, _throwAxe;
    private int _axesThrown;
    private uint[]? _equipment;

    /// <summary>Axes that have landed so far this fight (m_axesThrownCount).</summary>
    public int AxesThrown => _axesThrown;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _snapKick = 8_000;
        _cleave = 14_000;
        _uppercut = 20_000;
        _mortalStrike = 12_000;
        _pummel = 32_000;
        _throwAxe = 1_000;
        _axesThrown = 0;
        if (_equipment is not null)
        {
            // The creature_equip_template weapons come back with the reset (a respawn reloads the equipment in the source's core).
            Me.RestoreVirtualItems(_equipment);
            _equipment = null;
        }

        System?.RemoveAuras(Me, SpellUnarmedPassive);
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id != SpellThrowAxe)
        {
            return;
        }

        switch (_axesThrown)
        {
            case 0:
                _equipment ??= Me.SnapshotVirtualItems();
                if (System?.AiServices.ItemTemplateOf?.Invoke(ItemRemainingAxe) is { } axe)
                {
                    Me.SetVirtualItem(0, axe);
                }

                Me.ClearVirtualItem(1);
                break;
            case 1:
                _equipment ??= Me.SnapshotVirtualItems();
                Me.ClearVirtualItem(0);
                Me.ClearVirtualItem(1);
                DoCast(Me, SpellUnarmedPassive, triggered: true);
                break;
        }

        _axesThrown++;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _snapKick, diffMs, Victim, SpellSnapKick, 6_000, 6_000);
        CastWhenReady(ref _cleave, diffMs, Victim, SpellCleave, 12_000, 12_000);
        CastWhenReady(ref _uppercut, diffMs, Victim, SpellUppercut, 14_000, 14_000);
        CastWhenReady(ref _mortalStrike, diffMs, Victim, SpellMortalStrike, 10_000, 10_000);
        CastWhenReady(ref _pummel, diffMs, Victim, SpellPummel, 16_000, 16_000);
        if (System?.HasAura(Me, SpellUnarmedPassive) != true)
        {
            CastWhenReady(ref _throwAxe, diffMs, Victim, SpellThrowAxe, 8_000, 8_000);
        }
    }
}

/// <summary>
/// boss_highlordomokkAI (vmangos blackrock_spire/boss_highlord_omokk.cpp: Reset, UpdateAI). The script's six timers replace the ClassicDB
/// EventAI's lone Knock Away; the EventAI's Enrage at 20 percent health and its kill and random speech (rows 919601, 919602, 919604) are
/// not in the source script and are kept.
/// </summary>
public sealed class HighlordOmokkAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellWarStomp = 24375, SpellStrike = 18368, SpellRend = 18106, SpellSunderArmor = 24317,
        SpellKnockAway = 20686, SpellSlow = 22356, SpellEnrage = 8269;

    private static readonly int[] RandomSay = [1925, 1926, 1927];
    private const int KillSay = 1927;

    private uint _warStomp, _strike, _rend, _sunder, _knockAway, _slow, _say;
    private bool _enraged;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _warStomp = 15_000;
        _strike = 10_000;
        _rend = 14_000;
        _sunder = 2_000;
        _knockAway = 18_000;
        _slow = 24_000;
        _say = (uint)(System?.RandomInt(1_000, 3_000) ?? 1_000);
        _enraged = false;
    }

    public override void OnKilledUnit(Unit victim)
    {
        if (victim is Player)
        {
            System?.SayText(Me, KillSay);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _warStomp, diffMs, Me, SpellWarStomp, 14_000, 14_000);
        CastWhenReady(ref _strike, diffMs, Victim, SpellStrike, 10_000, 10_000);
        CastWhenReady(ref _rend, diffMs, Victim, SpellRend, 18_000, 18_000);
        CastWhenReady(ref _sunder, diffMs, Victim, SpellSunderArmor, 25_000, 25_000);
        CastWhenReady(ref _knockAway, diffMs, Me, SpellKnockAway, 12_000, 12_000);
        CastWhenReady(ref _slow, diffMs, Me, SpellSlow, 18_000, 18_000);

        if (!_enraged && Me.HealthPercent <= 20f && DoCast(Me, SpellEnrage, triggered: true) == CreatureCastResult.Ok)
        {
            _enraged = true;
        }

        if (_say > diffMs)
        {
            _say -= diffMs;
        }
        else
        {
            _say = (uint)(System?.RandomInt(15_000, 25_000) ?? 15_000);
            System?.SayText(Me, RandomSay[System?.RandomInt(0, RandomSay.Length - 1) ?? 0]);
        }
    }
}

/// <summary>
/// boss_shadowvoshAI (vmangos blackrock_spire/boss_shadow_hunter_voshgajin.cpp: Reset, UpdateAI): Curse of Blood on himself every 45 s
/// (first at 2 s, spell 24673 as in the script; the ClassicDB EventAI cast 16098), Hex on a random target every 15 s (first at 8 s) and
/// Cleave on the victim every 7 s (first at 14 s).
/// </summary>
public sealed class ShadowHunterVoshgajinAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellCurseOfBlood = 24673, SpellHex = 16708, SpellCleave = 20691;

    private uint _curse, _hex, _cleave;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _curse = 2_000;
        _hex = 8_000;
        _cleave = 14_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _curse, diffMs, Me, SpellCurseOfBlood, 45_000, 45_000);
        CastWhenReady(ref _hex, diffMs, RandomThreatTarget(), SpellHex, 15_000, 15_000);
        CastWhenReady(ref _cleave, diffMs, Victim, SpellCleave, 7_000, 7_000);
    }
}

/// <summary>
/// boss_halyconAI (vmangos blackrock_spire/boss_halycon.cpp: Reset, UpdateAI, JustDied). Crowd Pummel (first 8 s, then 14 s) and Mighty
/// Blow (first 14 s, then 10 s) on the victim; her death summons Gizrul the Slavener (10268) at the lair, sets his home on the stairs and
/// puts him in combat with the whole instance. The ClassicDB EventAI's spawn Thrash (8876) and Rend (13738) are not in the source script
/// and are kept.
/// </summary>
public sealed class HalyconAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellCrowdPummel = 10887, SpellMightyBlow = 14099, SpellThrash = 8876, SpellRend = 13738, NpcGizrul = 10268;
    public const int TextDeathEmote = 5548;

    private uint _crowdPummel, _mightyBlow, _rend;
    private bool _summoned;

    public override void OnRespawn()
    {
        ResetFight();
        _summoned = false;
        DoCast(Me, SpellThrash, triggered: true);
    }

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _crowdPummel = 8_000;
        _mightyBlow = 14_000;
        _rend = (uint)(System?.RandomInt(8_000, 10_000) ?? 8_000);
    }

    public override void OnDeath(Unit? killer)
    {
        if (_summoned)
        {
            return;
        }

        _summoned = true;
        System?.SayText(Me, TextDeathEmote);
        if (System?.SummonDeadDespawn(Me, NpcGizrul, -167.58f, -382.41f, 64.401f, 1.563f) is { } gizrul)
        {
            System.SetHomePosition(gizrul, -172.633f, -324.253f, 64.401f, 4.74f);
            System.SetInCombatWithZone(gizrul);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _crowdPummel, diffMs, Victim, SpellCrowdPummel, 14_000, 14_000);
        CastWhenReady(ref _mightyBlow, diffMs, Victim, SpellMightyBlow, 10_000, 10_000);
        CastWhenReady(ref _rend, diffMs, Victim, SpellRend, 12_000, 14_000);
    }
}

/// <summary>
/// go_urok_challengeAI (vmangos blackrock_spire/boss_urok.cpp), the Challenge to Urok object (175584) a player plants with the Ogre Banner
/// spell 16447. Its first update puts six summoning circles (175571) in the ogre hall, three seconds later three underlings (a mage on
/// circle 0, Enforcers on 2 and 3) come out and everyone is pulled onto them. Each underling that dies is replaced (a random mage or
/// Enforcer on the next circle of the order table) until the eighth death, which brings Urok Doomhowl (10584) out of circle 5 and takes the
/// circles away. Using the object lets the user kill the nearest underling with spell 16452, once per 30 s. An idle underling walks to the
/// object and destroys its banner (event 4777), which ends the challenge: circles and object are removed. One instance serves every object;
/// the state is kept by the object. World thread.
/// </summary>
public sealed class UrokChallengeAI : IGameObjectAi
{
    public const uint GoChallenge = 175584, GoSummonCircle = 175571, NpcEnforcer = 10601, NpcMagus = 10602, NpcDoomhowl = 10584;
    public const uint SpellKillAdd = 16452, SpellAddSummoned = 16562, SpellDestroySpear = 16557, EventBannerDestroyed = 4777;
    public const uint FirstWaveDelayMs = 3_000, SecondStepDelayMs = 10_000, KillAddCooldownMs = 30_000, UnderlingLifetimeMs = 400_000;
    public const int UnderlingsToKill = 8, CircleCount = 6;

    private static readonly (float X, float Y, float Z, float O)[] Circles =
    [
        (-13.7275f, -384.816f, 48.9746f, 3.68265f),
        (-27.8804f, -385.891f, 48.5067f, 3.7001f),
        (-24.8824f, -369.619f, 49.7059f, 3.4034f),
        (-12.3689f, -376.475f, 49.335f, 5.044f),
        (-34.5437f, -370.233f, 50.3396f, 5.35816f),
        (-47.9095f, -369.089f, 51.5425f, 1.37881f),
    ];

    private static readonly byte[] CircleOrder = [0, 2, 3, 1, 4, 5, 1, 2, 3, 5];

    private sealed class State
    {
        public bool Active = true;
        public uint Step;
        public uint Timer;
        public uint SpellTimer;
        public int Dead;
        public readonly GameObject?[] Runes = new GameObject?[CircleCount];
        public readonly Creature?[] Current = new Creature?[3];
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameObject, State> _states = new();

    /// <summary>Whether the challenge of <paramref name="go"/> is still running (false once the banner fell; Doomhowl's arrival does not clear it, as in the source).</summary>
    public bool IsActive(GameObject go) => _states.GetOrCreateValue(go).Active;

    /// <summary>How many underlings of <paramref name="go"/>'s challenge have died.</summary>
    public int DeadUnderlings(GameObject go) => _states.GetOrCreateValue(go).Dead;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        State s = _states.GetOrCreateValue(go);
        s.SpellTimer = s.SpellTimer < diffMs ? 0 : s.SpellTimer - diffMs;
        if (!s.Active || s.Step >= 2)
        {
            return;
        }

        if (s.Timer >= diffMs)
        {
            s.Timer -= diffMs;
            return;
        }

        if (s.Step == 0)
        {
            for (int i = 0; i < Circles.Length; i++)
            {
                (float x, float y, float z, float o) = Circles[i];
                s.Runes[i] = objects.Summon(GoSummonCircle, x, y, z, o);
            }

            s.Timer = FirstWaveDelayMs;
        }
        else
        {
            SpawnAtCircle(objects, go, s, 0, NpcMagus, 0);
            SpawnAtCircle(objects, go, s, 2, NpcEnforcer, 1);
            SpawnAtCircle(objects, go, s, 3, NpcEnforcer, 2);
            s.Timer = SecondStepDelayMs;
        }

        s.Step++;
    }

    /// <summary>OnUse: the nearest underling within 20 yards dies to the kill spell, at most once per 30 s. The object takes the use either way.</summary>
    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        State s = _states.GetOrCreateValue(go);
        if (s.Active && s.SpellTimer == 0 && NearestUnderling(objects, go) is { } target)
        {
            s.SpellTimer = KillAddCooldownMs;
            objects.Spells?.Cast(go, SpellKillAdd, target, user);
        }

        return true;
    }

    /// <summary>EventBannerDestroyed: the challenge ends, the circles and the object go.</summary>
    public void EndChallenge(GameObjectMapSystem objects, GameObject go)
    {
        State s = _states.GetOrCreateValue(go);
        if (!s.Active)
        {
            return;
        }

        s.Active = false;
        RemoveCircles(objects, s);
        objects.Remove(go);
    }

    /// <summary>UrokUnderlingDied: the next underling takes the dead one's slot, and the eighth death brings the boss.</summary>
    public void UnderlingDied(GameObjectMapSystem objects, GameObject go, Creature underling)
    {
        State s = _states.GetOrCreateValue(go);
        if (!s.Active)
        {
            return;
        }

        for (int i = 0; i < s.Current.Length; i++)
        {
            if (!ReferenceEquals(s.Current[i], underling))
            {
                continue;
            }

            s.Dead++;
            if (s.Dead < UnderlingsToKill)
            {
                uint entry = objects.Random.Next(0, 2) == 1 ? NpcMagus : NpcEnforcer;
                SpawnAtCircle(objects, go, s, CircleOrder[s.Dead + 2], entry, i);
            }
            else if (s.Dead == UnderlingsToKill)
            {
                SpawnAtCircle(objects, go, s, 5, NpcDoomhowl, 0);
                RemoveCircles(objects, s);
            }
        }
    }

    private static void RemoveCircles(GameObjectMapSystem objects, State s)
    {
        for (int i = 0; i < s.Runes.Length; i++)
        {
            if (s.Runes[i] is { } rune)
            {
                objects.Remove(rune);
                s.Runes[i] = null;
            }
        }
    }

    private static void SpawnAtCircle(GameObjectMapSystem objects, GameObject go, State s, int circle, uint entry, int slot)
    {
        if (s.Runes[circle] is not { } rune || objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.SummonInstanceCreatureTimedOocOrDead(entry, rune.X, rune.Y, rune.Z, 0f, UnderlingLifetimeMs) is not { } summoned)
        {
            return;
        }

        if (entry != NpcDoomhowl)
        {
            s.Current[slot] = summoned;
            if (summoned.AI is UrokUnderlingAI underling)
            {
                underling.Mound = go;
            }

            creatures.CastSpell(summoned, SpellAddSummoned, summoned, triggered: true);
        }

        // Everyone in the map who is not its friend is its enemy at once.
        foreach (Player player in objects.Map.Players.ToArray())
        {
            if (player.IsAlive && objects.Map.Combat.Hooks.CanAttack(summoned, player))
            {
                summoned.AI?.AttackStart(player);
            }
        }
    }

    private static Creature? NearestUnderling(GameObjectMapSystem objects, GameObject go)
    {
        if (objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return null;
        }

        Creature? enforcer = creatures.CreaturesOfEntryInRange(go, NpcEnforcer, 20f).FirstOrDefault(c => c.IsAlive);
        Creature? magus = creatures.CreaturesOfEntryInRange(go, NpcMagus, 20f).FirstOrDefault(c => c.IsAlive);
        if (magus is null)
        {
            return enforcer;
        }

        return enforcer is not null && WorldGeometry.DistanceSq3D(go, enforcer) < WorldGeometry.DistanceSq3D(go, magus) ? enforcer : magus;
    }
}

/// <summary>
/// urokUnderlingAI (vmangos boss_urok.cpp): the Ogre Magus and Enforcer of the Urok challenge. In a fight they use their spells; with
/// nobody to fight they walk to the challenge object and, in reach of it, cast Destroy Spear 16557 at it (the spell sends event 4777,
/// <see cref="BlackrockSpireInstance.OnSpellEvent(Unit, uint)"/> ends the challenge) and try again 11 s later. Their death tells the
/// object. The spell timers are the ClassicDB EventAI rows 1060101-1060205, which carry what the source's DB spell lists do.
/// </summary>
public abstract class UrokUnderlingAI(Creature creature) : ScriptDevBossAI(creature)
{
    /// <summary>The point id of the walk to the banner (MovementInform checks point 2).</summary>
    public const uint BannerPointId = 2;

    /// <summary>How close to the object an underling must stand to hit the banner (CONTACT_DISTANCE + 1 in the source, measured from the object's edge).</summary>
    public const float BannerReach = 3f;

    private uint _bannerTimer;

    /// <summary>The challenge object that summoned this underling (SetMoundGuid).</summary>
    public GameObject? Mound { get; set; }

    public override void OnRespawn()
    {
        _bannerTimer = 0;
        ResetSpells();
    }

    public override void OnEvade() => ResetSpells();

    protected abstract void ResetSpells();

    protected abstract void CombatUpdate(uint diffMs);

    public override void OnDeath(Unit? killer)
    {
        if (Mound is { } mound && Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects && objects.AiFor(mound) is UrokChallengeAI challenge)
        {
            challenge.UnderlingDied(objects, mound, Me);
        }
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type == MovementGeneratorType.Point && pointId == BannerPointId)
        {
            HitBanner();
        }
    }

    /// <summary>BannerDestroyed (event 4777): the challenge object this underling stands at ends the challenge.</summary>
    public void BannerDestroyed()
    {
        if (FindMound() is { } mound && Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects && objects.AiFor(mound) is UrokChallengeAI challenge)
        {
            challenge.EndChallenge(objects, mound);
        }
    }

    private GameObject? FindMound()
    {
        IReadOnlyCollection<GameObject> all = Me.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects ?? [];
        if (Mound is { } own)
        {
            if (all.Contains(own))
            {
                return own;
            }

            // The challenge ended and took its object away: like FindNearestGameObject, find nothing there.
            Mound = null;
        }

        GameObject? best = null;
        float bestSq = 40f * 40f;
        foreach (GameObject go in all)
        {
            float sq = WorldGeometry.DistanceSq3D(Me, go);
            if (go.Entry == UrokChallengeAI.GoChallenge && sq <= bestSq)
            {
                bestSq = sq;
                best = go;
            }
        }

        return best;
    }

    private bool HitBanner()
    {
        if (FindMound() is not { } mound || WorldGeometry.DistanceSq3D(Me, mound) > BannerReach * BannerReach)
        {
            return false;
        }

        System?.CastSpellAtDestination(Me, UrokChallengeAI.SpellDestroySpear, mound.X, mound.Y, mound.Z, triggered: false);
        _bannerTimer = 11_000;
        return true;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            if (_bannerTimer < diffMs)
            {
                if (!HitBanner() && FindMound() is { } mound)
                {
                    // GameObject::GetContactPoint: a point beside the object on the side of the underling.
                    float dx = Me.X - mound.X;
                    float dy = Me.Y - mound.Y;
                    float length = MathF.Sqrt((dx * dx) + (dy * dy));
                    float scale = length > 0.001f ? 0.5f / length : 0f;
                    Me.Motion.MovePoint(BannerPointId, mound.X + (dx * scale), mound.Y + (dy * scale), mound.Z, run: true);
                    _bannerTimer = 10_000;
                }
            }
            else
            {
                _bannerTimer -= diffMs;
            }

            return;
        }

        CombatUpdate(diffMs);
    }
}

/// <summary>urokEnforcerAI: Strike (first after 5 to 15 s, then every 5 to 20 s).</summary>
public sealed class UrokEnforcerAI(Creature creature) : UrokUnderlingAI(creature)
{
    public const uint SpellStrike = 14516;
    private uint _strike;

    protected override void ResetSpells() => _strike = (uint)(System?.RandomInt(5_000, 15_000) ?? 5_000);

    protected override void CombatUpdate(uint diffMs) => CastWhenReady(ref _strike, diffMs, Victim, SpellStrike, 5_000, 20_000);
}

/// <summary>urokOgreMagusAI: Slow on a random target (first 8 to 11 s, then 17 to 24 s), Bloodlust at or below 30 percent health (every 30 to 35 s) and Arcane Bolt on the victim every 5 to 10 s (melee mode).</summary>
public sealed class UrokOgreMagusAI(Creature creature) : UrokUnderlingAI(creature)
{
    public const uint SpellSlow = 13747, SpellBloodlust = 6742, SpellArcaneBolt = 15979;
    private uint _slow, _bloodlust, _bolt;

    protected override void ResetSpells()
    {
        _slow = (uint)(System?.RandomInt(8_000, 11_000) ?? 8_000);
        _bloodlust = 0;
        _bolt = 0;
    }

    protected override void CombatUpdate(uint diffMs)
    {
        CastWhenReady(ref _slow, diffMs, RandomThreatTarget(), SpellSlow, 17_000, 24_000);
        if (Me.HealthPercent <= 30f)
        {
            CastWhenReady(ref _bloodlust, diffMs, Me, SpellBloodlust, 30_000, 35_000);
        }

        // The melee-mode row (1060205): the magus has no ranged-mode movement, so the 1.2-2.4 s ranged cadence (1060204) does not apply.
        CastWhenReady(ref _bolt, diffMs, Victim, SpellArcaneBolt, 5_000, 10_000);
    }
}
