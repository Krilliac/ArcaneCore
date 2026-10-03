using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Characters;

/// <summary>
/// One equipment slot as the character list shows it: item display id and inventory type
/// (vmangos Player::BuildEnumData: display info id u32, inventory type u8).
/// </summary>
public readonly record struct CharEnumItem(uint DisplayId, byte InventoryType);

/// <summary>
/// Character-screen hooks for features that own per-character data (items, spells, skills,
/// quests, reputation …). Implement on an <see cref="Features.IWorldFeature"/>, which is then
/// also registered as an <see cref="ICharacterHooks"/>. Every method has a default that does
/// nothing, so a feature overrides only what it needs. All of them run on the session task,
/// which may use the databases; none may touch world state.
/// <para>
/// Order follows vmangos: <see cref="OnCharacterCreatedAsync"/> runs after the character row
/// exists (Player::Create then SaveToDB, which stores the starting items and spells);
/// <see cref="OnPlayerLoadingAsync"/> runs while the character loads, before it is handed to
/// the world thread (Player::LoadFromDB: _LoadInventory, _LoadSpells, _LoadQuestStatus …).
/// </para>
/// </summary>
public interface ICharacterHooks
{
    /// <summary>
    /// A character was just created (CMSG_CHAR_CREATE), before the client hears of it. An
    /// exception is logged and the client gets CHAR_CREATE_ERROR; the character row stays.
    /// </summary>
    Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character) => Task.CompletedTask;

    /// <summary>
    /// <paramref name="player"/> is being loaded for login and is not yet visible to the world
    /// thread, so the hook may fill it (inventory, spells …). An exception fails the login with
    /// CHAR_LOGIN_FAILED (fail closed: never enter the world with half-loaded data).
    /// </summary>
    Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player) => Task.CompletedTask;

    /// <summary>
    /// The visible equipment of <paramref name="characters"/> for SMSG_CHAR_ENUM, keyed by
    /// character id (up to 20 entries: the 19 equipment slots, then the first bag). Null or a
    /// missing id means an empty slot list. The first hook that answers for a character wins.
    /// </summary>
    Task<IReadOnlyDictionary<int, CharEnumItem[]>?> GetCharEnumEquipmentAsync(WorldSession session, IReadOnlyList<CharacterRecord> characters)
        => Task.FromResult<IReadOnlyDictionary<int, CharEnumItem[]>?>(null);
}
