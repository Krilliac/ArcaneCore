using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Short John Mithril (entry 14508, spawn 636, Gurubashi Arena), the Gurubashi Arena Booty Run (game event 16, every 3 hours). cmangos
/// <c>world_map_eastern_kingdoms::OnEventHappened</c> (world_eastern_kingdoms.cpp) only starts his waypoint movement; the run itself is
/// DB data that classic-db z2815 does not carry, so it is ported from vmangos 20170726102314_world.sql: event_scripts 9530 (he yells
/// broadcast_text 9730, exclaims, drops his gossip and quest giver flags, and walks off three seconds later), creature_movement_template
/// 14508 points 1-31, movement script 1450816 at point 14 on the arena floor (he runs, yells 9731 and leaves the Arena Treasure Chest
/// 179697 for three hours) and 1450833 at point 31 back on his ledge (walk, flags back).
/// The chest is placed beside point 14: the vmangos gameobject guid 12029 position is not in this DB.
/// </summary>
public sealed class ShortJohnMithrilAI(Creature creature) : CreatureAI(creature)
{
    public const ushort GameEventGurubashiArena = 16;
    public const uint Entry = 14508, GoArenaTreasureChest = 179697, ChestDespawnSeconds = 10_800;
    public const int YellBootyRun = 9730, YellBloodletting = 9731;
    private const uint EmoteOneshotExclamation = 5, StartDelayMs = 3000, FloorWaitMs = 1000;

    private static readonly Vector3[] s_toFloor =
    [
        new(-13187.1f, 319.775f, 33.2346f), new(-13215.1f, 322.781f, 33.2446f), new(-13234.3f, 314.129f, 33.2076f),
        new(-13248.8f, 299.046f, 33.2264f), new(-13254.9f, 284.07f, 33.2425f), new(-13255.6f, 264.58f, 33.245f),
        new(-13247.6f, 243.589f, 33.2082f), new(-13237.6f, 231.746f, 33.2374f), new(-13233.3f, 231.889f, 33.2352f),
        new(-13234.5f, 239.465f, 33.3607f), new(-13239.7f, 247.561f, 29.2974f), new(-13244.5f, 256.393f, 22.6305f),
        new(-13241.2f, 261.35f, 21.933f), new(-13204.1f, 277.168f, 21.9821f),
    ];

    private static readonly Vector3[] s_toLedge =
    [
        new(-13219.2f, 275.451f, 21.9821f), new(-13240.6f, 268.921f, 21.933f), new(-13244f, 261.878f, 21.933f),
        new(-13243.4f, 254.911f, 23.8869f), new(-13239f, 245.371f, 29.2373f), new(-13233.6f, 238.16f, 33.3501f),
        new(-13237.5f, 232.776f, 33.2308f), new(-13250f, 248.904f, 33.2329f), new(-13255.2f, 264.038f, 33.2444f),
        new(-13255.3f, 282.627f, 33.2429f), new(-13248.9f, 298.446f, 33.2419f), new(-13233.8f, 314.316f, 33.2081f),
        new(-13216f, 322.76f, 33.2444f), new(-13199.4f, 323.398f, 33.2425f), new(-13188.3f, 320.076f, 33.2348f),
        new(-13184.8f, 329.885f, 37.955f), new(-13184.5f, 332.671f, 40.3299f),
    ];

    private const float LedgeOrientation = 4.41568f, ChestX = -13202.3f, ChestY = 276.6f, ChestZ = 21.86f;

    public enum RunStage { Idle, Announced, ToFloor, OnFloor, ToLedge }

    private RunStage _stage;
    private uint _timerMs;

    public RunStage Stage => _stage;
    public GameObject? Chest { get; private set; }

    /// <summary>event_scripts 9530: the event started. A run already under way is not restarted.</summary>
    public void StartBootyRun()
    {
        if (_stage != RunStage.Idle || !Me.IsAlive)
        {
            return;
        }

        System?.SayText(Me, YellBootyRun);
        System?.PlayEmote(Me, EmoteOneshotExclamation);
        Me.NpcFlags &= ~(uint)(NpcFlags.Gossip | NpcFlags.QuestGiver);
        _stage = RunStage.Announced;
        _timerMs = StartDelayMs;
    }

    public override void OnRespawn() => Finish();

    /// <summary>
    /// An evade breaks the raw spline the run walks and sends him back to his ledge spawn. vmangos runs this as a creature_movement path, so an
    /// evade returns to the last waypoint and the path continues; this port has no such generator, so the leg to the floor starts again from
    /// home. (The leg back to the ledge ends at his spawn and finishes by itself.)
    /// </summary>
    public override void OnReachedHome()
    {
        base.OnReachedHome();
        if (_stage is RunStage.ToFloor or RunStage.OnFloor && System is { } system)
        {
            _stage = RunStage.ToFloor;
            system.MovePath(Me, s_toFloor, run: false, SplineFacing.None);
        }
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        _stage = RunStage.Idle;
    }

    private void Finish()
    {
        _stage = RunStage.Idle;
        _timerMs = 0;
        Me.NpcFlags |= (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver);
    }

    private static bool Near(Creature c, Vector3 p) => MathF.Abs(c.X - p.X) < 1.5f && MathF.Abs(c.Y - p.Y) < 1.5f;

    public override void OnUpdate(uint diffMs)
    {
        if (_stage != RunStage.Idle && System is { } system && !Me.Combat.IsInCombat)
        {
            switch (_stage)
            {
                case RunStage.Announced:
                    if (_timerMs <= diffMs)
                    {
                        _stage = RunStage.ToFloor;
                        system.MovePath(Me, s_toFloor, run: false, SplineFacing.None);
                    }
                    else
                    {
                        _timerMs -= diffMs;
                    }

                    break;
                case RunStage.ToFloor:
                    if (Near(Me, s_toFloor[^1]))
                    {
                        _stage = RunStage.OnFloor;
                        _timerMs = FloorWaitMs;
                    }

                    break;
                case RunStage.OnFloor:
                    if (_timerMs <= diffMs)
                    {
                        // 1450816: set run, yell, spawn the chest (temp summon for 10800 s).
                        system.SayText(Me, YellBloodletting);
                        Chest = system.Map.FindUpdater<GameObjectMapSystem>()?.Summon(GoArenaTreasureChest, ChestX, ChestY, ChestZ, 0f, ChestDespawnSeconds);
                        _stage = RunStage.ToLedge;
                        system.MovePath(Me, s_toLedge, run: true, SplineFacing.ToAngle(LedgeOrientation));
                    }
                    else
                    {
                        _timerMs -= diffMs;
                    }

                    break;
                case RunStage.ToLedge:
                    if (Near(Me, s_toLedge[^1]))
                    {
                        // 1450833: walk, stop waypoints, flags back.
                        Finish();
                    }

                    break;
            }
        }

        UpdateVictim();
    }
}
