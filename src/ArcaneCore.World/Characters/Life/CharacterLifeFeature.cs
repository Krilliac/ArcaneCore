using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Characters.Life;

/// <summary>
/// Restores a character's stored life at login: experience, the recent-death window, health and
/// power (<see cref="PlayerLife"/>). It runs in <see cref="ICharacterHooks.OnPlayerLoadedAsync"/>,
/// i.e. after every loading hook has set items, spells and level stats, because vmangos restores
/// health and power only after UpdateAllStats (Player.cpp:15057-15070). Saving needs nothing here:
/// <c>Player.CreateSnapshot</c> carries the life in every state save.
/// <para>
/// Auras and passive spells are applied once the player is in the world; if they raise the
/// maximums, the stored values that were clamped are re-applied on the next world tick
/// (<see cref="PlayerLife.ReapplyAfterAuras"/>).
/// </para>
/// </summary>
public sealed class CharacterLifeFeature : IWorldFeature, ICharacterHooks, IDisposable
{
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the character life feature is already attached");
        }

        _world = world;
        world.PlayerLoggedIn += OnPlayerLoggedIn;
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
        }
    }

    /// <summary>Session task: load the stored life and apply it on top of everything the loading hooks produced.</summary>
    public async Task OnPlayerLoadedAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<ICharacterLifeStore>() is not { } store)
        {
            return;
        }

        CharacterLife? life = await store.LoadAsync(character.Id).ConfigureAwait(false);
        if (life is null)
        {
            return; // never saved with a life: the fresh values stand
        }

        PlayerLife.ApplyDeathWindow(player, life, DeathHooks.For(session.World).Clock.UnixSeconds);
        // Experience goes through the progression service so the next-level clamp has one owner.
        session.Services.GetRequiredService<ProgressionFeature>().Progression.InitializeLoadedPlayer(player, life.Xp);
        player.LoadedLife = PlayerLife.ApplyVitals(player, life);
        if (PlayerLife.IsGhostWithBody(life))
        {
            // The body is put back on the world thread once the player is in its map (DeathFeature).
            PlayerLife.ApplyGhostState(player);
        }
    }

    private void OnPlayerLoggedIn(Player player)
    {
        if (player.LoadedLife is null || _world is not { } world)
        {
            return;
        }

        // After every PlayerLoggedIn handler (the spell system restores auras there).
        world.Post(() =>
        {
            LoadedLife? loaded = player.LoadedLife;
            player.LoadedLife = null;
            if (loaded is not null && player.IsInWorld)
            {
                PlayerLife.ReapplyAfterAuras(player, loaded);
            }
        });
    }
}
