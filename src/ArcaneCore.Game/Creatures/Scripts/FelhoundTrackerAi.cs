using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Felhound Tracker (8668), the guardian the Azsharite quest (3602) gives: mangos-classic ScriptDev2 <c>npc_felhound_tracker</c>
/// (kalimdor/azshara.cpp:194-316), a ScriptedPetAI. When its owner, on the quest, roars at it, it looks for Azsharite: the first of the four
/// crystal entries (152620, 152621, 152622, 152631) with any spawned object within 40 yd, nearest first, the first of those still flagged
/// GO_FLAG_INTERACT_COND. It runs to 3 yd short of it, stops there and waits 20 s before it is a pet again; with nothing found it growls
/// (sound 1249). Outside that it is the generic <see cref="PetAI"/>. Registered by <see cref="Instances.Scripts.World.KalimdorWorldMapData"/>
/// as a map entry script, like Razorfen Kraul's Snufflenose Gopher.
/// </summary>
public sealed class FelhoundTrackerAi : CreatureAI, IScriptedPetAi
{
    public const uint Entry = 8668, QuestAzsharite = 3602, TextEmoteRoar = 74, SoundGrowl = 1249, WaitMs = 20_000;
    public const float SearchRange = 40f, ContactDistance = 3f;
    public static readonly uint[] Crystals = [152620, 152621, 152622, 152631];

    private readonly PetAI _pet;
    private bool _moving;
    private uint _waitMs;

    public FelhoundTrackerAi(Creature creature) : base(creature) => _pet = new PetAI(creature);

    public PetAI Pet => _pet;

    public bool IsMovingToCrystal => _moving;

    public uint WaitingMs => _waitMs;

    public override bool AggroesOnSight => _pet.AggroesOnSight;

    public override void OnRespawn()
    {
        _moving = false;
        _waitMs = 0;
    }

    public override void MoveInLineOfSight(Unit who) => _pet.MoveInLineOfSight(who);

    public override void OnAttackedBy(Unit attacker) => _pet.OnAttackedBy(attacker);

    public override void OnKilledUnit(Unit victim) => _pet.OnKilledUnit(victim);

    public override bool AttackStart(Unit target) => _pet.AttackTarget(target);

    /// <summary>ReceiveEmote: a roar from a player on the quest starts a new search.</summary>
    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (textEmote != TextEmoteRoar || System?.QuestIncomplete(player, QuestAzsharite) != true)
        {
            return;
        }

        _waitMs = 0;
        _moving = false;
        FindNewCrystal();
    }

    /// <summary>DoFindNewCrystal; false (after the growl) when no crystal qualifies.</summary>
    public bool FindNewCrystal()
    {
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return false;
        }

        GameObject[] inRange = [];
        foreach (uint entry in Crystals)
        {
            inRange = [.. objects.GameObjects.Where(go => go.Entry == entry && go.IsSpawned && DistanceSq(go, Me) <= SearchRange * SearchRange)];
            if (inRange.Length > 0)
            {
                break;
            }
        }

        GameObject? crystal = inRange.OrderBy(go => DistanceSq(go, Me)).FirstOrDefault(go => (go.Flags & GameObjectFlags.InteractCond) != 0);
        if (crystal is null)
        {
            System?.PlayDirectSound(Me, SoundGrowl);
            return false;
        }

        float dx = Me.X - crystal.X, dy = Me.Y - crystal.Y, length = MathF.Sqrt((dx * dx) + (dy * dy));
        float reach = ContactDistance + Me.BoundingRadius;
        (float x, float y) = length > 0.01f ? (crystal.X + (dx / length * reach), crystal.Y + (dy / length * reach)) : (crystal.X + reach, crystal.Y);
        Me.GetCharmInfo()?.ClearFlags();
        Me.Motion.Clear();
        Me.Motion.MovePoint(1, x, y, crystal.Z, run: true);
        _moving = true;
        return true;
    }

    /// <summary>MovementInform: at the crystal it stops and waits 20 s.</summary>
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || pointId == 0 || !_moving)
        {
            _pet.OnMovementInform(type, pointId);
            return;
        }

        System?.MoveIdle(Me);
        _moving = false;
        _waitMs = WaitMs;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_moving)
        {
            return;
        }

        if (_waitMs != 0)
        {
            if (_waitMs < diffMs)
            {
                _waitMs = 0;
            }
            else
            {
                _waitMs -= diffMs;
                return;
            }
        }

        _pet.OnUpdate(diffMs);
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
        => ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z));
}
