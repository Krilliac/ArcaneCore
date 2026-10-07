using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Chest behaviours of the reference core beyond opening the loot: the chest level gate, the key that opened a locked chest, the
/// restock timer of a chest that never despawns, and the extra opens of a multi-use mineral vein (vmangos Player::SendLoot,
/// WorldSession::DoLootRelease and GameObject::Update; behaviour re-implemented, no code copied).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>chest.level (data9, GameObjectDefines.h:259-277).</summary>
    public const int ChestLevelData = 9;

    /// <summary>chest.chestRestockTime (data2), whole seconds.</summary>
    public const int ChestRestockData = 2;

    /// <summary>chest.consumable (data3).</summary>
    public const int ChestConsumableData = 3;

    /// <summary>chest.minSuccessOpens (data4) and chest.maxSuccessOpens (data5): the opens of a mineral vein.</summary>
    public const int ChestMinOpensData = 4;

    public const int ChestMaxOpensData = 5;

    /// <summary>Player::SendLoot (Player.cpp:7658): a chest more than this many levels above its opener is refused.</summary>
    public const uint ChestLevelLeeway = 10;

    /// <summary>
    /// DoLootRelease (LootHandler.cpp:468-471): the lock skill a vein's next-open roll is measured against when the lock is unknown.
    /// </summary>
    public const int DefaultVeinRequiredSkill = 175;

    /// <summary>
    /// Player::SendLoot (Player.cpp:7656-7665): a chest whose chest.level is more than <see cref="ChestLevelLeeway"/> above the opener's
    /// level is not opened; the client gets SMSG_LOOT_RELEASE_RESPONSE and the attempt is reported (vmangos: the passive anticheat
    /// writes it to the log and the online game masters; here it is a warning in the log).
    /// </summary>
    private bool ChestLevelRefuses(Player player, GameObject go)
    {
        uint level = go.Type == GameObjectType.Chest ? go.Template.GetData(ChestLevelData) : 0;
        if (level <= player.Level + ChestLevelLeeway)
        {
            return false;
        }

        player.Session.Send(WorldOpcode.SmsgLootReleaseResponse, LootPackets.ReleaseResponse(go.Guid));
        _logger.LogWarning("Level {Level} player {Player} attempted to loot chest {Spawn} (entry {Entry}, level {ChestLevel})",
            player.Level, player.Name, go.Spawn?.Guid ?? go.Guid.Counter, go.Entry, level);
        return true;
    }

    /// <summary>
    /// A key that opened a chest from the bags is used up the way the key's own use would use it up: the reference core opens such a
    /// chest through the open-lock spell cast from the key (Spell::CanOpenLock, Spell.cpp:7885-7888, with the key as the cast item), and
    /// Spell::TakeCastItem (Spell.cpp:4991-5048) then takes a charge of the key's on-use spell and destroys a spent expendable key
    /// (negative charges). A key without such a spell (a door key, a quest key) stays in the bags.
    /// </summary>
    private static void UseUpKey(Player player, Item key)
    {
        if (player.Inventory.GetItemByGuid(key.Guid) is { } held)
        {
            ItemSpellCharges.TakeCharge(player, held);
        }
    }

    /// <summary>
    /// GameObject::Update, GO_JUST_DEACTIVATED of a chest (GameObject.cpp:629-639): a chest with a restock time that is not consumable
    /// stays in the world, empty and not ready (GO_NOT_READY), until the restock time ran out; then it is ready again (GameObject.cpp:381-394)
    /// and its loot is generated anew by the next opener. False when the chest despawns as usual. A dungeon chest whose loot is stored
    /// with the instance save keeps its stored life cycle.
    /// </summary>
    private bool TryStartRestock(GameObject go)
    {
        uint restock = go.Template.GetData(ChestRestockData);
        if (go.Type != GameObjectType.Chest || restock == 0 || go.Template.GetData(ChestConsumableData) != 0 || DurableKeyOf(go) is not null)
        {
            return false;
        }

        Loot?.ForgetLoot(go);
        go.Loot = null;
        go.State = GameObjectState.Ready;
        go.ResetAfterSecond = null;
        go.LootState = GameObjectLootState.NotReady;
        go.RestockAfterSecond = ClockSeconds + restock;
        go.ForceFieldUpdate(UpdateFields.GameobjectDynFlags);
        return true;
    }

    /// <summary>GameObject::Update, GO_NOT_READY of a restocking chest (GameObject.cpp:381-394): ready once the restock time ran out.</summary>
    private void UpdateRestock(GameObject go)
    {
        if (go.LootState == GameObjectLootState.NotReady && go.RestockAfterSecond is { } after && after <= ClockSeconds)
        {
            go.RestockAfterSecond = null;
            go.LootState = GameObjectLootState.Ready;
            go.ForceFieldUpdate(UpdateFields.GameobjectDynFlags);
        }
    }

    /// <summary>
    /// WorldSession::DoLootRelease, a looted-out chest (LootHandler.cpp:435-487): a mineral vein (minSuccessOpens set and below
    /// maxSuccessOpens) counts the open; until it has been opened min times it is always ready again, from then on it stays with the
    /// chance <c>100 * pow(0.8 * nextRate, 4 / max * uses) + mining / (lockSkill + 25)</c> percent, and at max opens it is used up. A vein
    /// that stays ready keeps its skill-up list and generates fresh loot for the next open. True when the vein stays.
    /// </summary>
    private bool VeinStaysAfterLooting(Player? looter, GameObject go)
    {
        uint min = go.Template.GetData(ChestMinOpensData);
        uint max = go.Template.GetData(ChestMaxOpensData);
        if (go.Type != GameObjectType.Chest || min == 0 || max <= min)
        {
            return false;
        }

        float minAmount = min * Options.MiningAmountRate;
        float maxAmount = max * Options.MiningAmountRate;
        go.UseCount++;
        float uses = go.UseCount;
        if (uses >= maxAmount)
        {
            return false;
        }

        if (uses < minAmount)
        {
            return true;
        }

        int required = _content.FindLock(GameObjectLocks.LockIdOf(go.Template)) is { Skills.Count: > 0 } entry
            ? unchecked((int)entry.Skills[0]) : DefaultVeinRequiredSkill;
        float skill = looter is null ? 0f : SkillValue(looter, LockSkills.Mining) / (float)(required + 25);
        double chance = Math.Pow(0.8 * Options.MiningNextRate, 4 * (1 / (double)maxAmount) * uses);
        return Random.NextDouble() * 100.0 < (100.0 * chance) + skill;
    }

    /// <summary>A vein that stays after being looted out: ready, closed and with no loot, so the next opener generates new loot.</summary>
    private void ReadyVeinAgain(GameObject go)
    {
        Loot?.ForgetLoot(go);
        go.Loot = null;
        go.State = GameObjectState.Ready;
        go.ResetAfterSecond = null;
        go.LootState = GameObjectLootState.Ready;
    }
}
