using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Pets.Control;

/// <summary>
/// The control state vmangos keeps on <c>Unit</c> and <c>Player</c> besides the update fields (docs/areas/unit-control.md):
/// <c>m_possessorGuid</c> (Unit.h:1269), <c>UNIT_STATE_POSSESSED</c>, the charm info of a creature that is not a pet
/// (<c>Unit::InitCharmInfo</c>, a pet keeps its own in <see cref="SummonLinks.Charm"/>), the player's mover
/// (<c>Player::m_mover</c>, Player.h:1730) and the session's client mover (<c>WorldSession::m_clientMoverGuid</c>). The charm and
/// charmer GUIDs themselves are the update fields (<see cref="OwnerLinks"/>), as in vmangos.
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal sealed class UnitControlState
{
    /// <summary>vmangos m_possessorGuid: the player possessing this unit (possess and possess pet).</summary>
    public ObjectGuid Possessor;

    /// <summary>vmangos UNIT_STATE_POSSESSED (the flag UNIT_FLAG_POSSESSED is in the update field).</summary>
    public bool PossessedState;

    /// <summary>vmangos Unit::m_charmInfo for a charmed or possessed unit that is not a pet (a creature or a player).</summary>
    public CharmInfo? Charm;

    /// <summary>vmangos Player::m_mover: the unit this player's client moves; empty means itself.</summary>
    public ObjectGuid Mover;

    /// <summary>vmangos WorldSession::m_clientMoverGuid: what the client last said it moves (CMSG_SET_ACTIVE_MOVER); empty until it says.</summary>
    public ObjectGuid ClientMover;

    /// <summary>Whether the client ever named its mover (vmangos starts the session with an empty client mover).</summary>
    public bool ClientMoverKnown;

    /// <summary>vmangos Unit::m_invincibilityHpThreshold (Unit.h:480-508): damage never takes health below this; 0 = off.</summary>
    public uint InvincibilityHpThreshold;

    /// <summary>The creature AI that ran before the charm (restored when no creature system can build a fresh one).</summary>
    public Creatures.CreatureAI? PreviousAi;
}

/// <summary>The unit-control members (charm, possess, mover, view point, invincibility); see <see cref="UnitControlState"/>.</summary>
public static class UnitControl
{
    private static readonly ConditionalWeakTable<Unit, UnitControlState> s_states = new();

    internal static UnitControlState State(Unit unit) => s_states.GetValue(unit, static _ => new UnitControlState());

    internal static UnitControlState? Find(Unit unit) => s_states.TryGetValue(unit, out UnitControlState? state) ? state : null;

    extension(Unit unit)
    {
        /// <summary>vmangos Unit::GetPossessorGuid.</summary>
        public ObjectGuid PossessorGuid => Find(unit)?.Possessor ?? default;

        /// <summary>vmangos HasUnitState(UNIT_STATE_POSSESSED).</summary>
        public bool IsPossessedState => Find(unit)?.PossessedState == true;

        /// <summary>vmangos Unit::IsCharmed: a charmer is set (charm, possess, possess pet).</summary>
        public bool IsCharmed => !unit.CharmerGuid.IsEmpty;

        /// <summary>
        /// vmangos Unit::GetCharmInfo: a pet's charm info (<see cref="SummonLinks.Charm"/>), else the one a charm or possession gave the unit,
        /// else null.
        /// </summary>
        public CharmInfo? GetCharmInfo() => (unit as Creature)?.Summon?.Charm ?? Find(unit)?.Charm;

        /// <summary>vmangos Unit::GetCharm: the unit in UNIT_FIELD_CHARM in the same map, or null.</summary>
        public Unit? GetCharm() => unit.CharmGuid is { IsEmpty: false } guid ? unit.Map?.FindObject(guid) as Unit : null;

        /// <summary>vmangos Unit::GetInvincibilityHpThreshold (Spirit of Redemption, god mode).</summary>
        public uint InvincibilityHpThreshold
        {
            get => Find(unit)?.InvincibilityHpThreshold ?? 0;
            set
            {
                if (value != 0 || Find(unit) is not null)
                {
                    State(unit).InvincibilityHpThreshold = value;
                }
            }
        }
    }

    extension(Player player)
    {
        /// <summary>vmangos Player::GetMover: the unit the client moves (the possessed unit while possessing), never null.</summary>
        public Unit GetMover()
            => Find(player)?.Mover is { IsEmpty: false } mover && player.Map?.FindObject(mover) is Unit unit ? unit : player;

        /// <summary>vmangos Player::IsSelfMover.</summary>
        public bool IsSelfMover => ReferenceEquals(player.GetMover(), player);

        /// <summary>vmangos WorldSession::GetClientMoverGuid; the player itself until the client named another mover.</summary>
        public ObjectGuid ClientMoverGuid => Find(player) is { ClientMoverKnown: true } state ? state.ClientMover : player.Guid;

        /// <summary>
        /// vmangos Player::GetConfirmedMover (Player.cpp:20134-20149): the mover when the client agrees on it; while the client has not
        /// switched yet, the player itself if nothing controls it; null otherwise (a possessed or charmed player moves nothing). A client
        /// that never sent CMSG_SET_ACTIVE_MOVER counts as moving itself (vmangos answers null for it: "is this a fake client?"; the
        /// managed bot sessions of this server never send it).
        /// </summary>
        public Unit? GetConfirmedMover()
        {
            Unit mover = player.GetMover();
            if (mover.Guid == player.ClientMoverGuid)
            {
                return ReferenceEquals(mover, player) && (player.IsCharmed || !player.PossessorGuid.IsEmpty) ? null : mover;
            }

            return player.PossessorGuid.IsEmpty && player.CharmerGuid.IsEmpty ? player : null;
        }

        /// <summary>The object the player's camera looks from (vmangos Camera::GetBody, PLAYER_FARSIGHT): its charm while it possesses one, else itself.</summary>
        public WorldObject ViewPoint
        {
            get
            {
                ulong far = player.GetUInt64(UpdateFields.PlayerFarsight);
                return far != 0 && player.Map?.FindObject(new ObjectGuid(far)) is { } eye ? eye : player;
            }
        }
    }

    /// <summary>The player whose camera looks from <paramref name="unit"/>: its possessor when that player's farsight is the unit.</summary>
    internal static Player? ViewerOf(WorldObject obj)
        => obj is Unit unit && Find(unit)?.Possessor is { IsEmpty: false } possessor && unit.Map?.FindPlayer(possessor) is { } player
            && player.GetUInt64(UpdateFields.PlayerFarsight) == unit.Guid.Value ? player : null;
}
