using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>
/// Character deletion for the social systems (vmangos HandleCharDeleteOpcode and
/// Player::DeleteFromDB): a guild leader is refused; otherwise, on the world thread, the character
/// leaves its guild (GE_LEFT, roster saved), the directory forgets it, every loaded friend/ignore
/// list drops it (its owner gets FRIEND_REMOVED / FRIEND_IGNORE_REMOVED), it leaves its group and
/// a purge of its social rows is queued after every earlier social write
/// (docs/integration/character-delete.md).
/// </summary>
public sealed class SocialCharacterDeleteHook(SocialFeature social, CharacterDirectory directory) : IWorldFeature, ICharacterDeleteHook
{
    /// <summary>Upper bound for the guild preload and one world-thread round trip during deletion.</summary>
    public static readonly TimeSpan WorldCallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the post-delete drain waits for the queued purge (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = CharacterDeletion.DrainTimeout;

    private WorldRuntime? _world;

    public void Attach(WorldRuntime world) => _world = world;

    public async Task<bool> CanDeleteCharacterAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_world is not { } world)
        {
            return true; // not attached: SocialDataModule still refuses a stored guild leader
        }

        // Leadership is known once the stored guilds are installed; after a failed preload the
        // stored leader check in SocialDataModule remains the guard.
        await social.GuildsLoaded.WaitAsync(WorldCallTimeout).ConfigureAwait(false);
        uint id = (uint)character.Id;
        bool leads = await world.InvokeAsync(() => social.Context.Guilds.GetGuildOf(id)?.LeaderId == id)
            .WaitAsync(WorldCallTimeout).ConfigureAwait(false);
        return !leads;
    }

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_world is not { } world)
        {
            return;
        }

        uint id = (uint)character.Id;
        var guid = ObjectGuid.Player(id);
        await world.InvokeAsync(() =>
        {
            SocialContext context = social.Context;

            // Guild::DelMember while the name is still known for GE_LEFT; never a leader here.
            context.Guilds.AdminUninvite(id);

            // On the world thread, so no add-friend/invite by name can slip in after the cleanup.
            directory.Remove(character.Id);
            foreach (Player lister in world.OnlinePlayers.ToArray())
            {
                if (!context.Friends.IsLoaded(lister))
                {
                    continue; // a list still loading skips entries of unknown characters
                }

                PlayerSocial list = context.Friends.Get(lister);
                if (list.Has(id, SocialFlags.Friend))
                {
                    context.Friends.RemoveFriend(lister, guid);
                }

                if (list.Has(id, SocialFlags.Ignored))
                {
                    context.Friends.RemoveIgnore(lister, guid);
                }
            }

            context.Groups.OnCharacterDeleted(guid);

            // After every write queued above and any earlier snapshot that still names the character.
            context.Persistence.PurgeCharacter(character.Id);
            return true;
        }).WaitAsync(WorldCallTimeout).ConfigureAwait(false);

        // The deletion completes only after the purge was attempted (it is conditional on the id
        // still having no character row, so a recreated character keeps its friends and guild).
        await social.Context.Persistence.FlushAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
    }
}
