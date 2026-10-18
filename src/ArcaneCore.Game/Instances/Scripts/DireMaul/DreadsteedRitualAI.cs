using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.Classic;

namespace ArcaneCore.Game.Instances.Scripts.DireMaul;

/// <summary>
/// mangos-classic kalimdor/dire_maul/dire_maul.cpp npc_warlock_mount_ritualAI (14501), the Dreadsteed ritual's controller (a
/// Scripted_NoMovementAI). 48 s after it appears the ritual starts (the circle and the portals), then nine demon waves follow, each a DB relay
/// (1450101-1450109) that also shows one of the ritual symbols for 900 s: eight 45 s apart and a last one 17.5 s before the end. At the end
/// every demon it summoned teleports out (29216), says SAY_UNSUMMON_DEMON and goes a second later, the circle activates, TYPE_DREADSTEED
/// becomes SPECIAL and the controller despawns. From 70 s on, every 30-45 s it deactivates one of the bell, the wheel and the candle that
/// is still active (the instance fails the ritual once all three are).
/// <para>
/// The demons first walk halfway to the pedestal at z -32, then to the pedestal, then wander within 15 yd of it; the pedestal is their
/// home, so they wander there again after an evade (JustSummoned, SummonedMovementInform and SummonedJustReachedHome). ArcaneCore has no
/// summoned-movement-inform hook, so the controller watches each demon's movement each update and gives the next leg when one ends.
/// </para>
/// </summary>
public sealed class DreadsteedRitualAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = DireMaulInstance.NpcWarlockDummyInfernal;
    public const uint SummonWavesRelayStart = 1450100;
    public const uint SpellTeleport = 29216;
    public const int SayUnsummonDemon = -1429005;
    public const uint PointHalfway = 0, PointPedestal = 1;
    public const float WanderRadius = 15f;
    public static readonly (float X, float Y, float Z) Pedestal = (-38.5f, 811.8f, -29.5f);

    private readonly Dictionary<ObjectGuid, int> _demons = [];
    private int _phase;
    private uint _phaseMs = 48000, _deactivateMs = 70000;

    /// <summary>The next phase (0 waits, 1 starts the ritual, 2-10 are the waves, 11 ends it).</summary>
    public int Phase => _phase;

    /// <summary>The demons summoned so far that it still tracks.</summary>
    public int DemonCount => _demons.Count;

    private DireMaulInstance? Instance => Me.Map?.FindUpdater<InstanceData>() as DireMaulInstance;

    public override void OnRespawn()
    {
        _phase = 0;
        _phaseMs = 48000;
        _deactivateMs = 70000;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        _demons[summoned.Guid] = 0;
        System?.SetHomePosition(summoned, Pedestal.X, Pedestal.Y, Pedestal.Z, 0); // SetRespawnCoord
        summoned.Motion.MovePoint(PointHalfway, (summoned.X + Pedestal.X) / 2f, (summoned.Y + Pedestal.Y) / 2f, -32f, run: false);
    }

    public override void OnUpdate(uint diffMs)
    {
        GuideDemons();
        if (Instance is not { } instance || System is not { } system)
        {
            return;
        }

        if (_phaseMs <= diffMs)
        {
            switch (_phase)
            {
                case 1:
                    instance.ProcessDreadsteedRitualStart();
                    _phaseMs = 1000;
                    break;
                case >= 2 and <= 9:
                    _phaseMs = 45000;
                    SummonPack(instance, system, _phase - 1);
                    break;
                case 10:
                    _phaseMs = 17500;
                    SummonPack(instance, system, _phase - 1);
                    break;
                case 11:
                    Finish(instance, system);
                    break;
            }

            ++_phase;
        }
        else
        {
            _phaseMs -= diffMs;
        }

        if (_deactivateMs <= diffMs)
        {
            DeactivateOne(instance);
            _deactivateMs = (uint)system.RandomInt(30000, 45000);
        }
        else
        {
            _deactivateMs -= diffMs;
        }
    }

    private void SummonPack(DireMaulInstance instance, CreatureMapSystem system, int index)
    {
        system.StartRelayScript(SummonWavesRelayStart + (uint)index, Me, Me);
        if (index - 1 < instance.RitualSymbols.Count
            && Me.Map?.FindUpdater<GameObjectMapSystem>()?.Find(instance.RitualSymbols[index - 1]) is { } symbol)
        {
            system.RespawnGameObjectFor(symbol, 900);
        }
    }

    private void Finish(DireMaulInstance instance, CreatureMapSystem system)
    {
        foreach (ObjectGuid guid in _demons.Keys.ToArray())
        {
            if (system.FindCreature(guid) is { IsAlive: true } demon)
            {
                system.CastSpell(demon, SpellTeleport, demon, triggered: true);
                system.SayText(demon, SayUnsummonDemon);
                system.ForcedDespawn(demon, 1000);
            }
        }

        _demons.Clear();
        if (instance.RitualCircle is { IsSpawned: true } circle)
        {
            circle.State = GameObjectState.Active; // UseDoorOrButton of the (generic) circle: its activation visual
        }

        instance.SetData(DireMaulInstance.TypeDreadsteed, EncounterState.Special);
        system.ForcedDespawn(Me, 0);
    }

    private void DeactivateOne(DireMaulInstance instance)
    {
        if (System is not { } system || Me.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        uint[] entries = [DireMaulInstance.GoBellOfDethmoora, DireMaulInstance.GoWheelOfBlackMarch, DireMaulInstance.GoDoomsdayCandle];
        int start = system.RandomInt(0, 2);
        for (int i = 0; i < entries.Length; i++)
        {
            if (instance.RitualObject(entries[(start + i) % entries.Length]) is { LootState: GameObjectLootState.Activated } go)
            {
                objects.ToggleDoorOrButton(go); // ResetDoorOrButton
                return;
            }
        }
    }

    private void GuideDemons()
    {
        if (System is not { } system || _demons.Count == 0)
        {
            return;
        }

        foreach ((ObjectGuid guid, int leg) in _demons.ToArray())
        {
            if (system.FindCreature(guid) is not { IsAlive: true } demon)
            {
                _demons.Remove(guid);
                continue;
            }

            MovementGeneratorType type = demon.Motion.CurrentType;
            if (demon.Combat.IsInCombat || type is MovementGeneratorType.Point or MovementGeneratorType.Home or MovementGeneratorType.Random)
            {
                continue;
            }

            if (leg == 0)
            {
                demon.Motion.MovePoint(PointPedestal, Pedestal.X, Pedestal.Y, Pedestal.Z, run: false);
                _demons[guid] = 1;
            }
            else
            {
                demon.Motion.MoveRandom(new RandomMovementGenerator(WanderRadius, new CreatureHome(Pedestal.X, Pedestal.Y, Pedestal.Z, 0), run: false));
                _demons[guid] = 2;
            }
        }
    }
}
