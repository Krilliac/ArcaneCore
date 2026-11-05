using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Characters.Rename;
using ArcaneCore.World.Names;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Character screen: enumerate, create, delete, and log in (vmangos CharacterHandler.cpp).
/// These run on the session task because they read and write the characters database.
/// </summary>
public sealed class CharacterHandlers : IOpcodeHandlerGroup
{
    /// <summary>How long a login waits for the same character's previous session to leave the world.</summary>
    private static readonly TimeSpan DuplicateLoginWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DuplicateLoginPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>The most characters the client can list (it cannot handle values larger than 10).</summary>
    private const int MaxListedCharacters = 10;

    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgCharEnum, SessionStates.CharacterSelect, HandleCharEnumAsync);
        table.OnSession(WorldOpcode.CmsgCharCreate, SessionStates.CharacterSelect, HandleCharCreateAsync);
        table.OnSession(WorldOpcode.CmsgCharDelete, SessionStates.CharacterSelect, HandleCharDeleteAsync);
        table.OnSession(WorldOpcode.CmsgPlayerLogin, SessionStates.CharacterSelect, HandlePlayerLoginAsync);
    }

    private static async Task HandleCharEnumAsync(WorldSession session, byte[] payload)
    {
        // Finish any deletion whose acknowledgement was lost before listing, so recovery does not
        // depend on the client retrying the delete (it no longer sees the character).
        await CharacterDeletion.ReconcilePendingAsync(session).ConfigureAwait(false);

        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IReadOnlyList<CharacterRecord> list = await characters.GetByAccountAsync(session.AccountId).ConfigureAwait(false);
        int owned = list.Count; // before the client limit: the realm list counts every character of the account

        if (session.Services.GetService<CharacterCreationFeature>()?.Options.Mode != CharacterCreationMode.Legacy)
        {
            list = await LimitForClientAsync(session, list).ConfigureAwait(false);
        }

        // Visible equipment comes from the features that own it (ICharacterHooks); first answer wins.
        Dictionary<int, CharEnumItem[]>? equipment = null;
        foreach (ICharacterHooks hooks in session.Services.GetServices<ICharacterHooks>())
        {
            IReadOnlyDictionary<int, CharEnumItem[]>? answer = await hooks.GetCharEnumEquipmentAsync(session, list).ConfigureAwait(false);
            if (answer is null)
            {
                continue;
            }

            equipment ??= [];
            foreach ((int id, CharEnumItem[] items) in answer)
            {
                equipment.TryAdd(id, items);
            }
        }

        // The at-login rename flag becomes CHARACTER_FLAG_RENAME, so the client prompts for a new name (Player::BuildEnumData).
        IReadOnlyDictionary<int, uint> renameFlags = await CharacterRename.CharEnumFlagsOfAccountAsync(session).ConfigureAwait(false);

        // The stored hide-helm/hide-cloak bits join the enum flags word, as vmangos Player::BuildEnumData sends characters.character_flags
        // (Player.cpp:1682-1683, 0e3ff01) and cmangos maps the stored PLAYER_FLAGS hide bits to char_flags (Player.cpp:1748-1754, ff53dfc).
        var flags = new Dictionary<int, uint>(renameFlags);
        foreach (CharacterRecord record in list)
        {
            uint hidden = record.CharacterFlags & CharacterAppearanceFlags.Mask;
            if (hidden != 0)
            {
                flags[record.Id] = flags.GetValueOrDefault(record.Id) | hidden;
            }
        }

        session.Send(WorldOpcode.SmsgCharEnum, CharacterPackets.BuildCharEnum(list, equipment, flags));
        await RealmCharacterCounts.RefreshAsync(session, owned).ConfigureAwait(false); // repairs a count that drifted; a no-op write when it did not
    }

    /// <summary>
    /// At most 10 characters, oldest first (vmangos HandleCharEnum LIMIT 0,10, CharacterHandler.cpp:168-183;
    /// the client cannot handle more, gtker smsg_char_enum.wowm), without those whose race/class pair has no
    /// create info (Player::BuildEnumData skips them, Player.cpp:1632-1650).
    /// </summary>
    private static async Task<IReadOnlyList<CharacterRecord>> LimitForClientAsync(WorldSession session, IReadOnlyList<CharacterRecord> list)
    {
        IWorldDataStore worldData = session.Services.GetRequiredService<IWorldDataStore>();
        var shown = new List<CharacterRecord>(Math.Min(list.Count, MaxListedCharacters));
        foreach (CharacterRecord character in list.Take(MaxListedCharacters))
        {
            if (await worldData.IsValidRaceClassAsync(character.Race, character.Class).ConfigureAwait(false))
            {
                shown.Add(character);
            }
            else
            {
                session.Logger.LogError("[{Endpoint}] character {Id} has race {Race} class {Class} without create info and is not listed",
                    session.RemoteEndpoint, character.Id, character.Race, character.Class);
            }
        }

        return shown;
    }

    private static async Task HandleCharCreateAsync(WorldSession session, byte[] payload)
    {
        // CMSG_CHAR_CREATE: CString name, u8 race, class, gender, skin, face, hair style,
        // hair color, facial hair, outfit id (vmangos WorldSession::HandleCharCreateOpcode).
        var reader = new PacketReader(payload);
        // Raw bytes: vmangos normalizePlayerName fails on invalid UTF-8 (CHAR_NAME_NO_NAME).
        byte[] rawName = reader.ReadCStringBytes().ToArray();
        byte race = reader.ReadByte();
        byte cls = reader.ReadByte();
        byte gender = reader.ReadByte();
        byte skin = reader.ReadByte();
        byte face = reader.ReadByte();
        byte hairStyle = reader.ReadByte();
        byte hairColor = reader.ReadByte();
        byte facialHair = reader.ReadByte();

        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IWorldDataStore worldData = session.Services.GetRequiredService<IWorldDataStore>();
        CharacterCreationFeature? feature = session.Services.GetService<CharacterCreationFeature>();
        CharacterCreationOptions options = feature?.Options ?? new CharacterCreationOptions();

        // The checks of vmangos HandleCharCreateOpcode, in its order (CharacterCreationRules).
        CharacterCreationDecision decision = await CharacterCreationRules.EvaluateAsync(
            new CharacterCreationRequest(rawName, race, cls, gender, new CharacterAppearance(skin, face, hairStyle, hairColor, facialHair)),
            session.Security,
            options,
            session.World.Options.CharactersPerRealm,
            new StoreFacts(characters, worldData, session.AccountId,
                session.Services.GetService<NameCatalogFeature>()?.Catalog ?? NameCatalog.Empty, session.Security,
                feature?.Appearance ?? CharacterAppearanceCatalog.Empty)).ConfigureAwait(false);
        if (!decision.Accepted)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, decision.Result);
            return;
        }

        string name = decision.Name!;
        StartPosition? start = await worldData.GetStartPositionAsync(race, cls).ConfigureAwait(false);
        if (start is null)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        var record = new CharacterRecord
        {
            AccountId = session.AccountId,
            Name = name,
            Race = race,
            Class = cls,
            Gender = gender,
            Skin = skin,
            Face = face,
            HairStyle = hairStyle,
            HairColor = hairColor,
            FacialHair = facialHair,
            MapId = start.MapId,
            ZoneId = start.ZoneId,
            X = start.X,
            Y = start.Y,
            Z = start.Z,
            Orientation = start.Orientation,

            // A new character is bound where it starts (vmangos Player::Create → SetHomebindToLocation).
            HomeMapId = start.MapId,
            HomeZoneId = start.ZoneId,
            HomeX = start.X,
            HomeY = start.Y,
            HomeZ = start.Z,
        };

        if (options.Mode == CharacterCreationMode.Retail)
        {
            // StartPlayerLevel / GM.StartLevel / StartPlayerMoney (vmangos Player.cpp:16217-16219).
            record.Level = (byte)options.StartLevelFor(session.Security > AccountSecurity.Player, feature?.MaxPlayerLevel ?? 60);
            record.Money = options.StartMoney;
        }

        // A store failure (or a refused id) must answer the client, never escape to the dispatcher,
        // which would drop the whole session without any SMSG_CHAR_CREATE.
        CharacterRecord created;
        try
        {
            created = await characters.CreateAsync(record).ConfigureAwait(false);
        }
        catch (CharacterNameTakenException)
        {
            // Another creation of the same name committed between the check and the insert.
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateNameInUse);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("handler.char-create", ex);
            AnomalyLedger.Shared.Report("character", "create-failed", $"character {name}", $"error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] could not create character '{Name}'", session.RemoteEndpoint, name);
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        session.Services.GetRequiredService<CharacterDirectory>().Add(
            new CharacterIdentity(created.Id, created.AccountId, created.Name, created.Race, created.Gender, created.Class, created.Level, created.ZoneId));
        session.Logger.LogInformation("[{Endpoint}] '{Account}' created character '{Name}'",
            session.RemoteEndpoint, session.AccountName, name);

        // Starting items, spells … (vmangos Player::Create + SaveToDB) belong to their features.
        try
        {
            foreach (ICharacterHooks hooks in session.Services.GetServices<ICharacterHooks>())
            {
                await hooks.OnCharacterCreatedAsync(session, created).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("handler.char-create-setup", ex);
            AnomalyLedger.Shared.Report("character", "create-failed", $"character {name} (id {created.Id})", $"setup error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] a feature failed to set up new character '{Name}'", session.RemoteEndpoint, name);
            if (options.Mode == CharacterCreationMode.Retail)
            {
                // vmangos saves a new character in one transaction (Player::SaveNewPlayer), so a
                // failure leaves nothing: take the half-created character back out through the whole
                // delete contract (hooks, per-module cleanup, ledger, directory).
                await RollBackCreationAsync(session, created).ConfigureAwait(false);
            }

            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateSuccess);
        await RealmCharacterCounts.RefreshAsync(session).ConfigureAwait(false); // the realm list shows the new count (vmangos CharacterHandler.cpp:314)
    }

    /// <summary>Remove a character whose creation failed after the row was inserted.</summary>
    private static async Task RollBackCreationAsync(WorldSession session, CharacterRecord created)
    {
        bool removed = false;
        try
        {
            removed = await CharacterDeletion.TryDeleteAsync(session, (ulong)created.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("handler.char-create-rollback", ex);
            AnomalyLedger.Shared.Report("character", "create-failed", $"character {created.Name} (id {created.Id})", $"rollback error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] could not roll back character {Id}", session.RemoteEndpoint, created.Id);
        }

        if (!removed)
        {
            session.Logger.LogError("[{Endpoint}] character '{Name}' ({Id}) stays half-created: its creation failed and the rollback was refused",
                session.RemoteEndpoint, created.Name, created.Id);
        }
    }

    /// <summary>The realm facts <see cref="CharacterCreationRules"/> asks for, read from the stores.</summary>
    private sealed class StoreFacts(
        ICharacterStore characters, IWorldDataStore worldData, int accountId, NameCatalog catalog, AccountSecurity security, CharacterAppearanceCatalog appearance)
        : ICharacterCreationFacts
    {
        // Without CharacterCreation:CharSectionsDbcPath the realm has no appearance data and checks nothing (the feature logs that once).
        public bool IsAppearanceValid(byte race, byte gender, CharacterAppearance looks) => appearance.IsEmpty || appearance.IsValid(race, gender, looks);

        public CharResult? CheckNameCatalog(string name) => CharacterNameCatalog.Check(catalog, name, security);

        public Task<bool> IsNameTakenAsync(string name) => characters.IsNameTakenAsync(name);

        public Task<int> CountOnRealmAsync() => characters.CountByAccountAsync(accountId);

        public async Task<byte?> FirstCharacterRaceAsync()
        {
            // GetByAccountAsync is ordered by id: the first element is the lowest guid.
            IReadOnlyList<CharacterRecord> own = await characters.GetByAccountAsync(accountId).ConfigureAwait(false);
            return own.Count == 0 ? null : own[0].Race;
        }

        public Task<bool> HasStartInfoAsync(byte race, byte cls) => worldData.IsValidRaceClassAsync(race, cls);
    }

    private static async Task HandleCharDeleteAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();

        // Stored rows go through the data modules, live state through ICharacterDeleteHook.
        bool deleted = await CharacterDeletion.TryDeleteAsync(session, guid).ConfigureAwait(false);
        SendResult(session, WorldOpcode.SmsgCharDelete, deleted ? CharResult.CharDeleteSuccess : CharResult.CharDeleteFailed);
        if (deleted)
        {
            await RealmCharacterCounts.RefreshAsync(session).ConfigureAwait(false); // vmangos Player.cpp:4492 recounts the account
        }
    }

    private static async Task HandlePlayerLoginAsync(WorldSession session, byte[] payload)
    {
        session.MarkLoginRequested();
        var reader = new PacketReader(payload);
        ulong rawGuid = reader.ReadUInt64();

        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IWorldDataStore worldData = session.Services.GetRequiredService<IWorldDataStore>();

        CharacterRecord? character = rawGuid <= int.MaxValue
            ? await characters.GetByIdAsync((int)rawGuid).ConfigureAwait(false)
            : null;
        if (character is null || character.AccountId != session.AccountId)
        {
            if (ClientWarningGates.TryEnter(session, "foreign-login", out int suppressed))
            {
                session.Logger.LogWarning("[{Endpoint}] login for character {Guid} not owned by '{Account}' ({Suppressed} more since the last line)",
                    session.RemoteEndpoint, rawGuid, session.AccountName, suppressed);
            }

            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginNoCharacter));
            return;
        }

        // A map another worker owns (Cluster:Worker:OwnedMaps): hand the session to the gateway, which re-opens it on the owner with this login packet.
        // Before the character is loaded, claimed or put anywhere, so the fenced claim happens only on the target worker.
        if (session.IsRemote && !session.World.Owns(character.MapId, 0) && session.TryRedirectRemote(character.MapId, 0))
        {
            session.Logger.LogInformation("[{Endpoint}] character {Guid} is saved on map {Map}, owned by another worker; redirecting the session",
                session.RemoteEndpoint, character.Id, character.MapId);
            return;
        }

        var guid = ObjectGuid.Player((uint)character.Id);
        if (!await WaitUntilOfflineAsync(session.World, guid).ConfigureAwait(false))
        {
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginDuplicateCharacter));
            return;
        }

        try
        {
            if (session.Services.GetService<QuestNpcFeature>() is { } quests)
            {
                await quests.WaitForSettlementAsync(character.Id).ConfigureAwait(false);
            }

            foreach (ICharacterSettlementBarrier barrier in session.Services.GetServices<ICharacterSettlementBarrier>())
            {
                await barrier.WaitForSettlementAsync(character.Id).ConfigureAwait(false);
            }

            if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            {
                await saves.FlushCharacterAsync(character.Id).ConfigureAwait(false);
            }

            // The previous session may have committed a settlement while this login waited.
            // Its final snapshot precedes the barrier; never construct a player from the
            // record fetched before waiting for that session to leave the world.
            character = await characters.GetByIdAsync(character.Id).ConfigureAwait(false);
            if (character is null || character.AccountId != session.AccountId)
            {
                session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginNoCharacter));
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("handler.char-login-drain", ex);
            AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character?.Id ?? (int)guid.Low}", $"drain error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] character saves did not drain before login", session.RemoteEndpoint);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        if (!await CharacterRename.MayLogInAsync(session, character).ConfigureAwait(false))
        {
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        (RaceInfo? raceInfo, ClassInfo? classInfo, HomeBind home) = await LoadLoginWorldDataAsync(worldData, character).ConfigureAwait(false);

        if (raceInfo is null || classInfo is null || home.IsUnset)
        {
            session.Logger.LogError("[{Endpoint}] missing world data for character {Guid}", session.RemoteEndpoint, character.Id);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        // Worker fencing scope: the per-character claim (cluster_transfer_owner) must be this worker's before the character is attached, or the write fence refuses every
        // claim-checked save of it. A live owner elsewhere or a transfer in flight refuses the login; a dead shard's claim is taken over (with the login-time reap,
        // Cluster:Fencing:ReapDeadOwners). Realm scope claims nothing. Released again at logout (CharacterClaimFeature) or, below, when this login does not enter the world.
        long claimTicket = 0; // this login's claim ticket (0: nothing claimed); a newer login's claim invalidates this login's releases
        // Resolved here, while the session's scope is alive: EnterWorld and the release run later on the world thread, after a disconnect may have ended the scope.
        CharacterClaimService? claimService = session.Services.GetService<CharacterClaimService>();
        if (claimService is { } claims)
        {
            // A live owner on another worker is, behind the gateway, usually a session of this account that is being replaced there: the gateway closed it for this
            // login, but its logout (the save that writes the map it is on, then the claim release) has not finished, so the record read above can still name a
            // map this worker owns. Wait for it as for a duplicate here (WaitUntilOfflineAsync), re-reading the record: once it names another worker's map the
            // session moves there, once the claim is released this worker claims.
            DateTime claimDeadline = DateTime.UtcNow + DuplicateLoginWait;
            int refusals = 0;
            while (true)
            {
                if (session.IsDisconnected)
                {
                    return; // the client left while this login waited: nothing is claimed, nobody to answer
                }

                CharacterClaimResult claim;
                try
                {
                    // Only the first refusal is a warning: the ones after it are the expected wait for the replaced session's logout.
                    (claim, claimTicket) = await claims.ClaimWithTicketAsync(character.Id, session.World.CharacterOwnership, quietRefusal: refusals > 0).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("handler.char-login-claim", ex);
                    AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"claim error={ex.GetType().Name}: {ex.Message}");
                    session.Logger.LogError(ex, "[{Endpoint}] could not claim character {Guid}", session.RemoteEndpoint, character.Id);
                    session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
                    return;
                }

                if (CharacterClaimService.Admits(claim))
                {
                    break;
                }

                refusals++;
                if (claim.Outcome != CharacterClaimOutcome.RefusedLiveOwner || !session.IsRemote || DateTime.UtcNow >= claimDeadline)
                {
                    if (refusals > 1)
                    {
                        session.Logger.LogWarning("[{Endpoint}] character {Guid} login refused after {Attempts} claim attempts waiting for its previous owner: {Outcome}",
                            session.RemoteEndpoint, character.Id, refusals, claim.Outcome);
                    }

                    session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(
                        claim.Outcome == CharacterClaimOutcome.RefusedLiveOwner ? CharResult.CharLoginDuplicateCharacter : CharResult.CharLoginFailed));
                    return;
                }

                await Task.Delay(DuplicateLoginPoll).ConfigureAwait(false);
                if (await RereadForLoginAsync(session, characters, character.Id).ConfigureAwait(false) is not { } current)
                {
                    return; // answered, or the client is gone
                }

                if (TryRedirectToOwner(session, current))
                {
                    return;
                }

                character = current;
            }

            // A claim was taken (Worker scope): the record read before it may predate the previous owner's final save, which was durable before that owner's claim was
            // released (CharacterClaimService.ScheduleRelease) and so before this claim was admitted. The player is built only from a record read after the claim; the checks made
            // on the older record are made again, and the claim is given back when the login does not go on here.
            if (claimTicket != 0)
            {
                // Any failure of these reads must give the claim back: nothing else releases it for a login that has no player yet (the logout release needs a Player).
                try
                {
                    CharacterRecord? fresh = await RereadForLoginAsync(session, characters, character.Id).ConfigureAwait(false);
                    if (fresh is null)
                    {
                        ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
                        return;
                    }

                    if (TryRedirectToOwner(session, fresh))
                    {
                        ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
                        return;
                    }

                    character = fresh;

                    // The checks made on the record read before the claim are made again on this one: the rename gate, the race and class data, and the bind point (the previous
                    // owner's final save may have moved it; the player's Home comes from here, not from the older record).
                    if (!await CharacterRename.MayLogInAsync(session, character).ConfigureAwait(false))
                    {
                        session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
                        ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
                        return;
                    }

                    (raceInfo, classInfo, home) = await LoadLoginWorldDataAsync(worldData, character).ConfigureAwait(false);
                    if (raceInfo is null || classInfo is null || home.IsUnset)
                    {
                        session.Logger.LogError("[{Endpoint}] missing world data for character {Guid}", session.RemoteEndpoint, character.Id);
                        session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
                        ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("handler.char-login-postclaim", ex);
                    AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"post-claim check error={ex.GetType().Name}: {ex.Message}");
                    session.Logger.LogError(ex, "[{Endpoint}] the checks after the claim of character {Guid} failed", session.RemoteEndpoint, character.Id);
                    if (!session.IsDisconnected)
                    {
                        session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
                    }

                    ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
                    return;
                }
            }
        }

        Player player;
        try
        {
            IReadOnlyList<ActionButton> buttons = await characters.GetActionButtonsAsync(character.Id).ConfigureAwait(false);
            player = new Player(character, CharacterPackets.BuildAppearance(raceInfo, classInfo), session, buttons)
            {
                Home = home,
                CharacterOwnership = session.World.CharacterOwnership,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No player will carry this ticket: the claim is given back (the logout release needs a Player), which also stops the ticket counting as "newest".
            FaultLedger.Shared.Record("handler.char-login-build", ex);
            AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"player build error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] could not build the player of character {Guid}", session.RemoteEndpoint, character.Id);
            if (!session.IsDisconnected)
            {
                session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            }

            ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
            return;
        }

        if (claimTicket != 0)
            claimService?.BindTicket(player, claimTicket); // the logout release finds it through the player

        // Per-character data owned by features (vmangos Player::LoadFromDB: inventory, spells,
        // quest status …). The player is not yet visible to the world thread. Fail closed.
        try
        {
            ICharacterHooks[] allHooks = [.. session.Services.GetServices<ICharacterHooks>()];
            foreach (ICharacterHooks hooks in allHooks)
            {
                await hooks.OnPlayerLoadingAsync(session, character, player).ConfigureAwait(false);
            }

            // Second phase: state that goes on top of everything the loading hooks produced
            // (stored health and power after the final maximums; Player.cpp:15057-15075).
            foreach (ICharacterHooks hooks in allHooks)
            {
                await hooks.OnPlayerLoadedAsync(session, character, player).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("handler.char-login-load", ex);
            AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"load error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] a feature failed to load character {Guid}", session.RemoteEndpoint, character.Id);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
            return;
        }

        if (!session.TryBeginLogin())
        {
            ReleaseUnenteredClaim(session, claimService, character.Id, claimTicket);
            return;
        }

        // Account settings belong to this session task; the world thread gets finished packets.
        var account = new AccountLoginPackets(
            LoginPackets.BuildAccountDataMd5(session.Settings),
            LoginPackets.BuildTutorialFlags(session.Settings.Tutorials));
        WorldRuntime world = session.World;
        CharacterSaveQueue? saveQueue = session.Services.GetService<CharacterSaveQueue>();
        QuestNpcFeature? questFeature = session.Services.GetService<QuestNpcFeature>();
        world.Post(() => EnterWorld(session, world, character, player, account, claimService, saveQueue, questFeature));
    }

    /// <summary>Race and class data for <paramref name="character"/>, and its bind point (the race and class start position when none is stored). Session task.</summary>
    private static async Task<(RaceInfo? Race, ClassInfo? Class, HomeBind Home)> LoadLoginWorldDataAsync(IWorldDataStore worldData, CharacterRecord character)
    {
        RaceInfo? raceInfo = await worldData.GetRaceInfoAsync(character.Race, character.Gender).ConfigureAwait(false);
        ClassInfo? classInfo = await worldData.GetClassInfoAsync(character.Class).ConfigureAwait(false);
        var home = new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ);
        if (home.IsUnset && await worldData.GetStartPositionAsync(character.Race, character.Class).ConfigureAwait(false) is { } start)
        {
            home = new HomeBind(start.MapId, start.ZoneId, start.X, start.Y, start.Z);
        }

        return (raceInfo, classInfo, home);
    }

    /// <summary>
    /// Reads the character again for a login that waited (session task). Null when the login must stop: the record is gone or belongs to another account (answered
    /// <see cref="CharResult.CharLoginNoCharacter"/>), the read failed (answered <see cref="CharResult.CharLoginFailed"/>), or the client disconnected (no answer; its scope
    /// may already be disposed, which is why a failing read never escapes the handler).
    /// </summary>
    private static async Task<CharacterRecord?> RereadForLoginAsync(WorldSession session, ICharacterStore characters, int characterId)
    {
        if (session.IsDisconnected)
        {
            return null;
        }

        CharacterRecord? current;
        try
        {
            current = await characters.GetByIdAsync(characterId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (session.IsDisconnected)
            {
                return null;
            }

            FaultLedger.Shared.Record("handler.char-login-reread", ex);
            session.Logger.LogError(ex, "[{Endpoint}] could not read character {Guid} again for its login", session.RemoteEndpoint, characterId);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return null;
        }

        if (session.IsDisconnected)
        {
            return null;
        }

        if (current is null || current.AccountId != session.AccountId)
        {
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginNoCharacter));
            return null;
        }

        return current;
    }

    /// <summary>True when <paramref name="character"/> is saved on a map another worker owns and the session was handed to the gateway for it (remote sessions only).</summary>
    private static bool TryRedirectToOwner(WorldSession session, CharacterRecord character)
    {
        if (!session.IsRemote || session.World.Owns(character.MapId, 0) || !session.TryRedirectRemote(character.MapId, 0))
        {
            return false;
        }

        session.Logger.LogInformation("[{Endpoint}] character {Guid} was saved on map {Map} by its previous session on another worker; redirecting the session",
            session.RemoteEndpoint, character.Id, character.MapId);
        return true;
    }

    /// <summary>
    /// A login that claimed the character but never put it into the world: the logout path will not run, so release the claim (a no-op when none was taken). Releases only what this login
    /// claimed: <paramref name="claimTicket"/> is this login's ticket, and a newer login's claim (a session replacement) makes the release a no-op.
    /// </summary>
    private static void ReleaseUnenteredClaim(WorldSession session, CharacterClaimService? claims, int characterId, long claimTicket)
        => claims?.ScheduleRelease(characterId, session.World.CharacterOwnership, () => session.World.IsOnline(ObjectGuid.Player((uint)characterId)), claimTicket);

    private static long ClaimTicketOf(CharacterClaimService? claims, Player player)
        => claims?.TicketOf(player) ?? 0;

    /// <summary>World thread: send the login sequence and put the player into its map.</summary>
    private static void EnterWorld(WorldSession session, WorldRuntime world, CharacterRecord character, Player player, AccountLoginPackets account, CharacterClaimService? claims,
        CharacterSaveQueue? saveQueue, QuestNpcFeature? questFeature)
    {
        if (world.IsOnline(player.Guid))
        {
            session.AbortLogin();
            // The character is online under an older login that keeps its claim: this login's ticket must not invalidate that login's release.
            if (claims is { } duplicateClaims)
                duplicateClaims.AbandonTicket(character.Id, duplicateClaims.TicketOf(player), session.World.CharacterOwnership,
                    () => session.World.IsOnline(ObjectGuid.Player((uint)character.Id)));
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginDuplicateCharacter));
            return;
        }

        if (!session.TryEnterWorld(player))
        {
            ReleaseUnenteredClaim(session, claims, character.Id, ClaimTicketOf(claims, player));
            return; // the client disconnected while the character loaded
        }

        // Login order per vmangos WorldSession::HandlePlayerLogin (LoginSequence): the login
        // relocation and map pick (Player::LoadFromDB: a dungeon without its instance sends the
        // player to the entrance; no instance bind yet), login packets,
        // SendInitialPacketsBeforeAddToMap, the map add (the instance bind and its
        // SMSG_INSTANCE_SAVE_CREATED, then the self create), SendInitialPacketsAfterAddToMap; then
        // the world features hear of the login.
        player.Relocate(character.X, character.Y, character.Z, character.Orientation, world.NowMs);
        Map map;
        try
        {
            map = world.ResolveLoginMap(player);
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("handler.char-login-map", ex);
            AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"resolve map error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] could not resolve the login map of '{Name}'", session.RemoteEndpoint, player.Name);
            session.Kick("login map could not be resolved");
            ReleaseUnenteredClaim(session, claims, character.Id, ClaimTicketOf(claims, player)); // never added to the world, so the logout path will not release it
            return;
        }

        LoginSequence.SendLoginPackets(session, world, map, player, account.DataMd5);
        LoginSequence.SendInitialPacketsBeforeAddToMap(session, player, account.TutorialFlags);

        try
        {
            world.AddPlayer(player, map);
        }
        catch (Exception ex)
        {
            // Never leave a session "in world" with a player that is not in a map.
            FaultLedger.Shared.Record("handler.char-login-add", ex);
            AnomalyLedger.Shared.Report("character", "login-load-failed", $"character {character.Id}", $"add to world error={ex.GetType().Name}: {ex.Message}");
            session.Logger.LogError(ex, "[{Endpoint}] could not add '{Name}' to the world", session.RemoteEndpoint, player.Name);
            session.Kick();
            ReleaseUnenteredClaim(session, claims, character.Id, ClaimTicketOf(claims, player)); // never added to the world, so the logout path will not release it
            return;
        }

        // Every loading hook and map insertion succeeded with fresh durable state.
        // Stale old-session callbacks cannot save or remove this player by GUID alone.
        // Resume before the last login packets: the client treats SMSG_INIT_WORLD_STATES as the
        // end of login, so a client that sees itself in the world can rely on its saves having
        // resumed. This runs on the world thread, so no state capture can interleave. The save
        // consumer may write the resumed slot at once, but the slot is empty at this point.
        saveQueue?.ResumeCharacter(character.Id);
        questFeature?.Persistence.ResumeCharacter(character.Id);
        LoginSequence.SendInitialPacketsAfterAddToMap(session, player);
        session.Logger.LogInformation("[{Endpoint}] '{Account}' entered the world as '{Name}'",
            session.RemoteEndpoint, session.AccountName, player.Name);
        world.NotifyLoggedIn(player);
    }

    private static async Task<bool> WaitUntilOfflineAsync(WorldRuntime world, ObjectGuid guid)
    {
        DateTime deadline = DateTime.UtcNow + DuplicateLoginWait;
        while (world.IsOnline(guid))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(DuplicateLoginPoll).ConfigureAwait(false);
        }

        return true;
    }

    private static void SendResult(WorldSession session, WorldOpcode opcode, CharResult result)
        => session.Send(opcode, [(byte)result]);

    /// <summary>Account-level login packets, built on the session task from its own settings.</summary>
    private sealed record AccountLoginPackets(byte[] DataMd5, byte[] TutorialFlags);
}
