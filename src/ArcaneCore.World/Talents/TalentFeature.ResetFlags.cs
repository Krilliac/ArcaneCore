using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Talents;

/// <summary>
/// The GM side of talent resets: an immediate free reset of an online character (<c>.reset talents</c>, vmangos
/// CharacterCommands.cpp:3892-3925) and the reset-at-login request (<see cref="ITalentResetFlagStore"/>, set for an offline character
/// or by <c>.reset all talents</c>, :3969-3990), which the login applies for free and clears (CharacterHandler.cpp:661-665).
/// </summary>
public sealed partial class TalentFeature
{
    /// <summary>LANG_RESET_TALENTS (mangos_string 216): the target's line, and the login notification.</summary>
    public const string TalentsResetText = "Your talents have been reset.";

    private static readonly object Marker = new();

    /// <summary>Players logging in with a reset request; it is applied once they are in the world.</summary>
    private readonly ConditionalWeakTable<Player, object> _resetAtLogin = new();

    /// <summary>Characters flagged by <c>.reset all talents</c> while online (vmangos sets their in-memory flag too, :3981-3983).</summary>
    private readonly HashSet<int> _flaggedOnline = [];
    private readonly Lock _flaggedLock = new();
    private readonly SemaphoreSlim _flagWrites = new(1, 1);

    /// <summary>
    /// <c>.reset talents</c> on an online character (vmangos HandleResetTalentsCommand, CharacterCommands.cpp:3902): a free reset,
    /// then the free points of its level (InitTalentForLevel); the changed fields reach the client with the next update. False
    /// while the feature is inert. World thread.
    /// </summary>
    public bool ResetTalentsNow(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Service is not { } service)
        {
            return false;
        }

        service.ResetTalents(player, noCost: true);
        service.InitTalentForLevel(player);
        return true;
    }

    /// <summary>
    /// <c>.reset all talents</c> for the characters online now: their request is remembered so that a reset before they log out
    /// clears it (vmangos ResetTalents clears the flag, Player.cpp:4077-4078). The stored flag is written by
    /// <see cref="ITalentResetFlagStore.FlagAllAsync"/>. World thread.
    /// </summary>
    public void MarkOnlineForLoginReset(IEnumerable<Player> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        lock (_flaggedLock)
        {
            foreach (Player player in players)
            {
                _flaggedOnline.Add(CharacterId(player));
            }
        }
    }

    /// <summary>
    /// Run one operation on the reset-request store in its own scope, one operation at a time. Off the world thread.
    /// Throws <see cref="InvalidOperationException"/> when the host has no characters database to keep the request in.
    /// </summary>
    public async Task<T> WithResetFlagsAsync<T>(Func<ITalentResetFlagStore, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_scopes is null)
        {
            throw new InvalidOperationException("the talent feature has no service scopes, so a reset request cannot be stored");
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        ITalentResetFlagStore store = TalentResetFlags.Resolve(scope.ServiceProvider)
            ?? throw new InvalidOperationException("no characters database is configured, so a reset request cannot be stored");
        await _flagWrites.WaitAsync().ConfigureAwait(false);
        try
        {
            return await work(store).ConfigureAwait(false);
        }
        finally
        {
            _flagWrites.Release();
        }
    }

    /// <summary>
    /// Login, on the session task: read whether the character has a reset request. It is applied once the player is in the world
    /// (<see cref="OnPlayerLoggedIn"/>), as vmangos does after the map add (CharacterHandler.cpp:661-665), so the spell removals reach
    /// a client that is in the world. Without a store nothing can have been requested. A storage error fails the login.
    /// </summary>
    private async Task ReadLoginResetAsync(WorldSession session, CharacterRecord character, Player player)
    {
        lock (_flaggedLock)
        {
            _flaggedOnline.Remove(character.Id);
        }

        if (TalentResetFlags.Resolve(session.Services) is not { } store)
        {
            return;
        }

        bool flagged;
        await _flagWrites.WaitAsync().ConfigureAwait(false);
        try
        {
            flagged = await store.IsFlaggedAsync(character.Id).ConfigureAwait(false);
        }
        finally
        {
            _flagWrites.Release();
        }

        if (flagged)
        {
            _resetAtLogin.AddOrUpdate(player, Marker);
        }
    }

    /// <summary>
    /// The player is in the world (world thread): a requested reset is applied for free with the points of the level
    /// (vmangos ResetTalents(true), then SendNotification(LANG_RESET_TALENTS)), and the request is cleared behind the world
    /// thread; if that write fails the next login resets once more.
    /// </summary>
    private void OnPlayerLoggedIn(Player player)
    {
        if (!_resetAtLogin.Remove(player) || !ResetTalentsNow(player))
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(TalentsResetText));
        _logger.LogInformation("{Player}: talents reset at login (a GM reset request)", player.Name);
        _ = ClearInBackgroundAsync(CharacterId(player), player.Name);
    }

    /// <summary>Any reset of a character flagged while online clears the stored request (vmangos Player.cpp:4077-4078).</summary>
    private void OnResetAttempted(Player player)
    {
        int id = CharacterId(player);
        lock (_flaggedLock)
        {
            if (!_flaggedOnline.Remove(id))
            {
                return;
            }
        }

        _ = ClearInBackgroundAsync(id, player.Name);
    }

    private async Task ClearInBackgroundAsync(int characterId, string name)
    {
        try
        {
            await WithResetFlagsAsync(async store =>
            {
                await store.ClearAsync(characterId).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The character keeps the request: its next login resets the talents once more, for free.
            _logger.LogError(ex, "{Player}: clearing the talent reset request failed", name);
        }
    }
}
