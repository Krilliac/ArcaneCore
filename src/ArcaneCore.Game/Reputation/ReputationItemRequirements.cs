using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// The real answer for the item reputation requirement (vmangos Player::GetReputationRank as used by
/// Player::CanUseItem, Player.cpp:10045): a decorator over whatever <see cref="IItemRequirements"/> the
/// inventory already has (the skills decorator, the stats ability flag), overriding only
/// <see cref="ReputationRank"/>. Reads live state on every call, so a rank gained in play lifts the
/// equip refusal without a relog. A faction without a standing for the player is Neutral, as in vmangos.
/// Without a loaded faction catalog the decorator is not installed and the fail-closed default stays.
/// </summary>
public sealed class ReputationItemRequirements(IItemRequirements fallback, ReputationService service) : IItemRequirements
{
    private readonly IItemRequirements _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    private readonly ReputationService _service = service ?? throw new ArgumentNullException(nameof(service));

    public bool CanDualWield(PlayerInventory inventory) => _fallback.CanDualWield(inventory);

    public uint SkillValue(PlayerInventory inventory, uint skill) => _fallback.SkillValue(inventory, skill);

    public bool HasSpell(PlayerInventory inventory, uint spellId) => _fallback.HasSpell(inventory, spellId);

    public byte HonorRank(PlayerInventory inventory) => _fallback.HonorRank(inventory);

    public uint ReputationRank(PlayerInventory inventory, uint faction)
        => inventory.Player is { } player ? (uint)_service.GetRank(player, faction) : _fallback.ReputationRank(inventory, faction);
}
