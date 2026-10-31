using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Characters;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Names;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters.Rename;

/// <summary>The decision on one CMSG_CHAR_RENAME.</summary>
/// <param name="Outcome">What happened; <see cref="CharacterRenameOutcome.Renamed"/> only when the name changed.</param>
/// <param name="Failure">The result code SMSG_CHAR_RENAME carries for a refusal (null for a rename).</param>
/// <param name="NewName">The normalized name that was stored (a rename only).</param>
/// <param name="OldName">The name the character had (when the store found it).</param>
/// <param name="Rejected">The name failed the name rules or the name catalog before the store was asked.</param>
public readonly record struct CharacterRenameDecision(CharacterRenameOutcome? Outcome, CharResult? Failure, string? NewName, string? OldName, bool Rejected);

/// <summary>
/// The rename of a flagged character from the character screen (mangos WorldSession::HandleCharRenameOpcode and its database
/// callback, CharacterHandlerCustomize.cpp:86-190), on the session task: normalize and check the name, then one transaction renames
/// the character and clears its flag, provided it belongs to the account, carries the flag and nobody has the name.
/// </summary>
public static class CharacterRename
{
    /// <summary>
    /// Decide and apply a rename. Order as the reference: the name must normalize (valid UTF-8, at most 15 code points), pass
    /// the realm's name rules and the name catalog (<see cref="CharacterNameCatalog.Check"/>, as at creation); then the store applies
    /// the rename atomically. A refusal because the character is not flagged, is not the account's or the name is taken answers
    /// CHAR_CREATE_ERROR, as the reference's single validation query does.
    /// </summary>
    public static async Task<CharacterRenameDecision> DecideAsync(
        ICharacterRenameStore store, CharacterCreationOptions options, NameCatalog catalog, AccountSecurity security, int accountId,
        CharacterRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
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

        // NamesProfanity/NamesReserved for everyone, reserved_name for players (vmangos CharacterHandler.cpp:774 and :784).
        if (CharacterNameCatalog.Check(catalog, name, security) is { } refused)
        {
            return new CharacterRenameDecision(null, refused, name, null, Rejected: true);
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
            NameCatalog catalog = session.Services.GetService<NameCatalogFeature>()?.Catalog ?? NameCatalog.Empty;
            decision = await DecideAsync(store, options, catalog, session.Security, session.AccountId, request.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("character.rename", ex);
            AnomalyLedger.Shared.Report("character", "rename-failed", $"character {request.Value.Guid}", $"error={ex.GetType().Name}: {ex.Message}");
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
    /// The login gate (vmangos Player::LoadFromDB, Player.cpp:14612-14626): false when the character carries
    /// <see cref="CharacterAtLoginFlags.Rename"/>, or when its stored name fails the name rules or the name catalog (a reserved_name
    /// match refuses only Player-level accounts); a failing name is flagged in the store first so the next character list prompts
    /// the rename. vmangos kicks the session after the refusal (CharacterHandler.cpp:485-487); the caller answers
    /// SMSG_CHARACTER_LOGIN_FAILED instead. A failing store is logged and the login goes on, so the world never depends on the
    /// <c>character_at_login</c> table.
    /// </summary>
    public static async Task<bool> MayLogInAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ICharacterRenameStore? store = session.Services.GetService<ICharacterRenameStore>();
        if (store is null)
        {
            return true;
        }

        try
        {
            IReadOnlyDictionary<int, uint> flags = await store.GetFlagsAsync(session.AccountId).ConfigureAwait(false);
            if (flags.TryGetValue(character.Id, out uint bits) && (bits & CharacterAtLoginFlags.Rename) != 0)
            {
                session.Logger.LogWarning("[{Endpoint}] character {Id} attempts to login but must be renamed", session.RemoteEndpoint, character.Id);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("character.rename-login-flags", ex);
            AnomalyLedger.Shared.Report("character", "rename-failed", $"character {character.Id}", $"login at-login flags error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] the at-login flags of character {Id} could not be read; the login goes on", session.RemoteEndpoint, character.Id);
            return true;
        }

        CharacterCreationOptions options = session.Services.GetService<CharacterCreationFeature>()?.Options ?? new CharacterCreationOptions();
        NameCatalog catalog = session.Services.GetService<NameCatalogFeature>()?.Catalog ?? NameCatalog.Empty;
        if (CharacterNameRules.Check(character.Name, NameRuleSettings.From(options, create: false)) is null
            && CharacterNameCatalog.Check(catalog, character.Name, session.Security) is null)
        {
            return true;
        }

        session.Logger.LogWarning("[{Endpoint}] character {Id} has a stored name that fails the name rules; flagged for rename", session.RemoteEndpoint, character.Id);
        try
        {
            await store.SetFlagAsync(character.Id, CharacterAtLoginFlags.Rename).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("character.rename-login-flag", ex);
            AnomalyLedger.Shared.Report("character", "rename-failed", $"character {character.Id}", $"login flag write error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] character {Id} could not be flagged for rename; the login is refused anyway", session.RemoteEndpoint, character.Id);
        }

        return false;
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

    /// <summary>
    /// The CHARACTER_FLAG_* word SMSG_CHAR_ENUM carries for a character with these at-login flags (mangos Player::BuildEnumData:
    /// AT_LOGIN_RENAME sets CHARACTER_FLAG_RENAME); no other at-login bit has a character flag here.
    /// </summary>
    public static uint CharEnumFlags(uint atLoginFlags)
        => (atLoginFlags & CharacterAtLoginFlags.Rename) != 0 ? CharacterRenamePackets.CharacterFlagRename : 0u;

    /// <summary>
    /// The CHARACTER_FLAG_* words of the account's characters for the character list, from <see cref="FlagsOfAccountAsync"/>:
    /// only characters with a flag to show have an entry. A store failure is logged and shows no flag (the list is still sent;
    /// the prompt returns with the next list), so the character screen never depends on the <c>character_at_login</c> table.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, uint>> CharEnumFlagsOfAccountAsync(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyDictionary<int, uint> atLogin;
        try
        {
            atLogin = await FlagsOfAccountAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("character.rename-flags", ex);
            AnomalyLedger.Shared.Report("character", "rename-failed", $"account {session.AccountId}", $"at-login flags error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] the at-login flags of account '{Account}' could not be read; the character list shows none",
                session.RemoteEndpoint, session.AccountName);
            return new Dictionary<int, uint>();
        }

        var flags = new Dictionary<int, uint>(atLogin.Count);
        foreach ((int id, uint bits) in atLogin)
        {
            uint word = CharEnumFlags(bits);
            if (word != 0)
            {
                flags[id] = word;
            }
        }

        return flags;
    }
}
