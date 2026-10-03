using ArcaneCore.Data.Characters.Ranged;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ranged;

/// <summary>
/// The ammunition slot in the world daemon (discovered <see cref="IWorldFeature"/>): the starting
/// ammo of a new character (vmangos Player::AddStartingItems, Player.cpp:560-575), PLAYER_AMMO_ID
/// restored at login (Player::LoadFromDB, Player.cpp:14703: the stored value is taken as is) and
/// its write-through persistence. CMSG_SET_AMMO is served by <see cref="AmmoHandlers"/>.
/// </summary>
public sealed class AmmoFeature : IWorldFeature, ICharacterHooks, ICharacterDeleteHook
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AmmoFeature> _logger;

    public AmmoFeature(IServiceScopeFactory scopes, ILogger<AmmoFeature> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Persistence = new AmmoPersistence(scopes, logger);
    }

    /// <summary>How long the post-delete drain waits for the queued removal (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = CharacterDeletion.DrainTimeout;

    /// <summary>Saved ammo of every character.</summary>
    public AmmoPersistence Persistence { get; }

    public void Attach(WorldRuntime world)
    {
    }

    public Task StopAsync() => Persistence.FlushAsync();

    /// <summary>Queue the write of a player's current ammo (world thread).</summary>
    public void Save(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Persistence.Save(CharacterId(player), PlayerAmmo.CurrentAmmoId(player));
    }

    /// <summary>The character id the ammo is keyed by (the player GUID's low part is characters.Id).</summary>
    public static int CharacterId(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return unchecked((int)player.Guid.Low);
    }

    /// <summary>
    /// Player::Create → AddStartingItems: the starting outfit's ammo becomes the selected ammo.
    /// Only for hosts that store items (a host without an item store never gives the outfit either).
    /// </summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        if (session.Services.GetService<IItemStore>() is null
            || session.Services.GetService<ItemsFeature>() is not { } items)
        {
            return;
        }

        IItemTemplateStore templates = await items.EnsureLoadedAsync().ConfigureAwait(false);
        uint ammo = PlayerAmmo.SelectStartingAmmo(templates, character.Race, character.Class, character.Level);
        if (ammo != 0)
        {
            Persistence.Save(character.Id, ammo);
            await Persistence.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Player::LoadFromDB: PLAYER_AMMO_ID is read back unvalidated, before the player is visible.</summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint ammo = await Persistence.LoadAsync(character.Id, session.Services.GetService<ICharacterAmmoStore>()).ConfigureAwait(false);
        if (ammo != 0)
        {
            player.SetUInt32(UpdateFields.PlayerAmmoId, ammo);
        }
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return Persistence.FlushCharacterAsync(character.Id);
    }

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        Persistence.DeleteCharacter(character.Id);

        // "Attempted", not "succeeded": the queue logs and swallows a failed store call.
        await Persistence.FlushCharacterAsync(character.Id).WaitAsync(DrainTimeout).ConfigureAwait(false);
        _logger.LogDebug("Ammo of deleted character {Character} dropped", character.Id);
    }
}
