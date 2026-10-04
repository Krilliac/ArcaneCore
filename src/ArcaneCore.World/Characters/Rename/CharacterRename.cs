using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Game.Characters;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters.Rename;

/// <summary>The decision on one CMSG_CHAR_RENAME.</summary>
/// <param name="Outcome">What happened; <see cref="CharacterRenameOutcome.Renamed"/> only when the name changed.</param>
/// <param name="Failure">The result code SMSG_CHAR_RENAME carries for a refusal (null for a rename).</param>
/// <param name="NewName">The normalized name that was stored (a rename only).</param>
/// <param name="OldName">The name the character had (when the store found it).</param>
/// <param name="Rejected">The name failed the name rules before the store was asked.</param>
public readonly record struct CharacterRenameDecision(CharacterRenameOutcome? Outcome, CharResult? Failure, string? NewName, string? OldName, bool Rejected);

/// <summary>
/// The rename of a flagged character from the character screen (mangos WorldSession::HandleCharRenameOpcode and its database
/// callback, CharacterHandlerCustomize.cpp:86-190), on the session task: normalize and check the name, then one transaction renames
/// the character and clears its flag, provided it belongs to the account, carries the flag and nobody has the name.
/// The reserved-name list (mangos <c>reserved_name</c>) is not enforced, as at character creation (docs/areas/character-rename.md).
/// </summary>
public static class CharacterRename
{
    /// <summary>
    /// Decide and apply a rename. Order as the reference: the name must normalize (valid UTF-8, at most 15 code points) and pass
    /// the realm's name rules; then the store applies the rename atomically. A refusal because the character is not flagged, is
    /// not the account's or the name is taken answers CHAR_CREATE_ERROR, as the reference's single validation query does.
    /// </summary>
    public static async Task<CharacterRenameDecision> DecideAsync(
        ICharacterRenameStore store, CharacterCreationOptions options, int accountId, CharacterRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        string? name = CharacterNames.NormalizeUtf8(request.RawName);
        if (name is null)
        {
            return new CharacterRenameDecision(null, CharResult.CharNameNoName, null, null, Rejected: true);
        }

        // The reference checks with create = true (ObjectMgr::CheckPlayerName(newname, true)).
        if (CharacterNameRules.Check(name, NameRuleSettings.From(options, create: true)) is { } invalid)
        {
            return new CharacterRenameDecision(null, invalid, name, null, Rejected: true);
        }

        if (request.Guid > int.MaxValue)
        {
            return new CharacterRenameDecision(CharacterRenameOutcome.NotAllowed, CharResult.CharCreateError, name, null, Rejected: false);
        }

        CharacterRenameResult result = await store.RenameAsync((int)request.Guid, accountId, name).ConfigureAwait(false);
        return result.Outcome == CharacterRenameOutcome.Renamed
            ? new CharacterRenameDecision(CharacterRenameOutcome.Renamed, null, name, result.OldName, Rejected: false)
            : new CharacterRenameDecision(result.Outcome, CharResult.CharCreateError, name, result.OldName, Rejected: false);
    }

    /// <summary>
    /// CMSG_CHAR_RENAME end to end: decide, keep the character directory current, answer with SMSG_CHAR_RENAME. A store failure answers
    /// CHAR_CREATE_ERROR (the reference's callback has no other failure path) and is logged.
    /// </summary>
    public static async Task HandleAsync(WorldSession session, byte[] payload)
    {
        CharacterRenameRequest? request = CharacterRenamePackets.ReadRequest(payload);
        if (request is null)
        {
            session.Send(WorldOpcode.SmsgCharRename, CharacterRenamePackets.BuildFailure(CharResult.CharNameNoName));
            return;
        }

        CharacterRenameDecision decision;
        try
        {
            ICharacterRenameStore store = session.Services.GetRequiredService<ICharacterRenameStore>();
            CharacterCreationOptions options = session.Services.GetService<CharacterCreationFeature>()?.Options ?? new CharacterCreationOptions();
            decision = await DecideAsync(store, options, session.AccountId, request.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] rename of character {Guid} failed", session.RemoteEndpoint, request.Value.Guid);
            session.Send(WorldOpcode.SmsgCharRename, CharacterRenamePackets.BuildFailure(CharResult.CharCreateError));
            return;
        }

        if (decision.Outcome != CharacterRenameOutcome.Renamed)
        {
            session.Send(WorldOpcode.SmsgCharRename, CharacterRenamePackets.BuildFailure(decision.Failure!.Value));
            return;
        }

        CharacterDirectory directory = session.Services.GetRequiredService<CharacterDirectory>();
        int id = (int)request.Value.Guid;
        if (directory.Find(id) is { } identity)
        {
            directory.Add(identity with { Name = decision.NewName! });
        }

        session.Logger.LogInformation("[{Endpoint}] account '{Account}' renamed character {Id} from '{Old}' to '{New}'",
            session.RemoteEndpoint, session.AccountName, id, decision.OldName, decision.NewName);
        session.Send(WorldOpcode.SmsgCharRename, CharacterRenamePackets.BuildSuccess(request.Value.Guid, decision.NewName!));

        // Every client may have cached the old name: mangos World::InvalidatePlayerDataToAllClient (World.cpp:2700) broadcasts
        // SMSG_INVALIDATE_PLAYER with the guid, so they ask again. The broadcast runs on the world thread.
        ulong guid = request.Value.Guid;
        session.World.Post(() => session.World.BroadcastToAll(WorldOpcode.SmsgInvalidatePlayer, CharacterRenamePackets.BuildInvalidatePlayer(guid)));
    }

    /// <summary>
    /// The at-login flags of the account's characters, for the character list: a character with <see cref="CharacterAtLoginFlags.Rename"/>
    /// shows the rename prompt through <see cref="CharacterRenamePackets.CharacterFlagRename"/> in SMSG_CHAR_ENUM.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, uint>> FlagsOfAccountAsync(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Services.GetService<ICharacterRenameStore>() is { } store
            ? await store.GetFlagsAsync(session.AccountId).ConfigureAwait(false)
            : new Dictionary<int, uint>();
    }
}
