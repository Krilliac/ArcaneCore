using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.Maps.SpawnGroups;

/// <summary>The shapes of <c>spawn_group_formation.FormationType</c> (cmangos SpawnGroupFormationType, Maps/SpawnGroupDefines.h).</summary>
public enum FormationShape : byte
{
    Random = 0,
    SingleFile = 1,
    SideBySide = 2,
    LikeGeese = 3,
    FannedOutBehind = 4,
    FannedOutInFront = 5,
    CircleTheLeader = 6,
}

/// <summary>One place of a formation: the member whose spawn row names the slot, who holds it now, and where it is around the leader.</summary>
/// <param name="SlotId">The <c>spawn_group_spawn.SlotId</c> (0 the leader).</param>
/// <param name="RealOwner">The spawn guid the slot belongs to.</param>
internal sealed class FormationSlot(int SlotId, uint RealOwner)
{
    public int SlotId { get; } = SlotId;

    public uint RealOwner { get; } = RealOwner;

    /// <summary>The creature in the slot now (a slot's owner can move to slot 0 when the leader dies), or null.</summary>
    public object? Owner { get; set; }

    /// <summary>Radians from the leader's facing (π is straight behind).</summary>
    public float Angle { get; set; }

    /// <summary>Yards from the leader.</summary>
    public float Distance { get; set; }
}

/// <summary>
/// The formation of one creature spawn group (cmangos FormationData, Maps/SpawnGroup.cpp:736-1290; no code copied): the slots of the group's
/// members and their place around the leader for the shape, and who leads. The leader (slot 0) walks the formation's path (or wanders, or
/// stands); the others hold their slot relative to the leader's facing. When the leader dies, the first living member takes slot 0 at once
/// out of combat, or when the group is home after its fight, and resumes the path at the node after the last one the leader reached.
/// <para>
/// <see cref="FixSlotsPositions"/> follows FormationData::FixSlotsPositions for every shape (classic-db z2815 uses all seven: 17 random,
/// 24 single file, 54 side by side, 24 like geese, 25 fanned out behind, 1 fanned out in front, 19 circle). The random shape's slow drift
/// (FormationData::Update, a new variation every 20-50 s) is not modelled: its members keep the variation drawn when the slots are placed.
/// SPAWN_GROUP_FORMATION_OPTION_KEEP_COMPACT and FORMATION_MIRRORING are not used by z2815 (every row has options 0; no group has flag 0x10).
/// </para>
/// World thread only.
/// </summary>
internal sealed class FormationState
{
    private readonly SortedDictionary<int, FormationSlot> _slots = [];

    public FormationState(uint groupId, SpawnGroupFormation entry, IEnumerable<SpawnGroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(members);
        GroupId = groupId;
        Entry = entry;
        Shape = Enum.IsDefined((FormationShape)entry.FormationType) ? (FormationShape)entry.FormationType : FormationShape.Random;
        Spread = entry.Spread > 0 ? entry.Spread : 3.0f; // FormationEntry default
        foreach (SpawnGroupMember member in members.Where(m => m.SlotId >= 0))
        {
            _slots.TryAdd(member.SlotId, new FormationSlot(member.SlotId, member.Guid));
        }
    }

    public uint GroupId { get; }

    public SpawnGroupFormation Entry { get; }

    public FormationShape Shape { get; }

    public float Spread { get; }

    /// <summary>The leader's movement: 1 random, 2 waypoint (looping), 4 linear waypoint (back and forth); anything else stands.</summary>
    public byte MovementType => Entry.MovementType;

    public uint PathId => Entry.PathId;

    /// <summary>The index of the last path node the previous leader reached (-1 none): a new leader resumes after it.</summary>
    public int LastWaypointIndex { get; set; } = -1;

    /// <summary>The leader died in a fight: a new one is chosen when the group is home (cmangos m_masterDied).</summary>
    public bool MasterDied { get; set; }

    /// <summary>The leader whose path movement was started last (a new leader gets it once).</summary>
    public object? MovingLeader { get; set; }

    public IReadOnlyCollection<FormationSlot> Slots => _slots.Values;

    public FormationSlot? MasterSlot => _slots.GetValueOrDefault(0);

    public object? Master => MasterSlot?.Owner;

    /// <summary>The slot whose spawn row is <paramref name="spawnGuid"/>.</summary>
    public FormationSlot? DefaultSlotOf(uint spawnGuid) => _slots.Values.FirstOrDefault(s => s.RealOwner == spawnGuid);

    /// <summary>The slot <paramref name="owner"/> holds now.</summary>
    public FormationSlot? SlotOf(object owner) => _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Owner, owner));

    /// <summary>cmangos FormationData::SwitchSlotOwner.</summary>
    public static void Swap(FormationSlot a, FormationSlot b) => (a.Owner, b.Owner) = (b.Owner, a.Owner);

    /// <summary>
    /// cmangos FormationData::FixSlotsPositions: the angle and distance of every slot for the shape. The leader's slot is (0, 0); the
    /// followers are numbered in slot order. <paramref name="modelWidth"/> is the widest member (the random shape's spacing).
    /// </summary>
    public void FixSlotsPositions(float modelWidth, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        int total = _slots.Count;
        if (total <= 1)
        {
            return;
        }

        int followers = total - 1;
        int count = 1;
        foreach (FormationSlot slot in _slots.Values)
        {
            if (slot.Owner is not null && ReferenceEquals(slot.Owner, Master))
            {
                slot.Angle = 0;
                slot.Distance = 0;
                continue;
            }

            (slot.Angle, slot.Distance) = Place(Shape, Spread, count, followers, modelWidth, random);
            count++;
        }
    }

    /// <summary>The angle and distance of follower number <paramref name="count"/> (1-based) of <paramref name="followers"/>.</summary>
    internal static (float Angle, float Distance) Place(FormationShape shape, float spread, int count, int followers, float modelWidth, Random random)
    {
        const float Pi = MathF.PI;
        switch (shape)
        {
            case FormationShape.SingleFile:
                return (Pi, spread * count);

            case FormationShape.SideBySide:
                return ((count & 1) == 0 ? (Pi / 2f) + Pi : Pi / 2f, spread * (((count - 1) / 2) + 1));

            case FormationShape.LikeGeese:
                return ((count & 1) == 0 ? Pi + (Pi / 4f) : Pi - (Pi / 4f), spread * (((count - 1) / 2) + 1));

            case FormationShape.FannedOutBehind:
                return ((Pi / 2f) + ((Pi / followers) * (count - 1)), spread);

            case FormationShape.FannedOutInFront:
            {
                float angle = Pi + (Pi / 2f) + ((Pi / followers) * (count - 1));
                return (angle > Pi * 2f ? angle - (Pi * 2f) : angle, spread);
            }

            case FormationShape.CircleTheLeader:
                return ((Pi * 2f / followers) * (count - 1), spread);

            default:
                return RandomPlace(spread, count, modelWidth, random);
        }
    }

    /// <summary>
    /// The random shape (cmangos FixSlotsPositions SPAWN_GROUP_FORMATION_TYPE_RANDOM): the first nine followers fill a loose wedge behind
    /// the leader, the rest stand beside it; each place is then shifted once by AddPositionVariation within the slot's maximum variation.
    /// </summary>
    private static (float Angle, float Distance) RandomPlace(float spread, int count, float modelWidth, Random random)
    {
        const float Pi = MathF.PI;
        float inter = modelWidth * 3.0f;
        (float angle, float distance) = count switch
        {
            1 or 2 or 5 or 6 or 7 or 8 => Wedge(count is 1 or 2 ? 1 : count is 5 or 6 ? 2 : 3, count is 1 or 5 or 7 ? -1 : 1),
            3 => (Pi, spread + (inter * 2f)),
            4 => (Pi, spread + (inter * 3f)),
            9 => (Pi, spread + inter),
            _ => ((count & 1) == 0 ? (Pi / 2f) + Pi : Pi / 2f, inter * (((count - 10) / 2) + 1)),
        };

        // FormationSlotData::SetMaxVariation((π/8) * modelWidth, inter) then AddPositionVariation(now = true).
        float maxAngle = Pi / 8f * modelWidth;
        float maxDistance = inter;
        angle = angle - (maxAngle / 2f) + Between(random, maxAngle / 100f, maxAngle);
        distance = distance - (maxDistance / 2f) + Between(random, maxDistance / 100f, maxDistance);
        return (angle, distance);

        (float, float) Wedge(int rows, int side)
        {
            float back = spread + (inter * rows);
            float hyp = MathF.Sqrt((back * back) + (inter * inter));
            return (hyp <= 0 ? Pi : Pi + (side * MathF.Asin(inter / hyp)), hyp);
        }
    }

    private static float Between(Random random, float min, float max) => max <= min ? min : min + ((float)random.NextDouble() * (max - min));
}
