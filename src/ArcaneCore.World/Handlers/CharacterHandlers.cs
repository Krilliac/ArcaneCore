using ArcaneCore.Game;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Characters.Rename;
using ArcaneCore.World.Net;
using ArcaneCore.World.Names;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Persistence;
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
        IReadOnlyDictionary<int, uint> flags = await CharacterRename.CharEnumFlagsOfAccountAsync(session).ConfigureAwait(false);

        session.Send(WorldOpcode.SmsgCharEnum, CharacterPackets.BuildCharEnum(list, equipment, flags));
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

        public CharResult? CheckNameCatalog(string name)
        {
            NameCatalogResult result = catalog.Check(name);
            // vmangos staff may use SQL reserved names, while DBC profanity/reserved rules remain global.
            if (result == NameCatalogResult.Reserved && security > AccountSecurity.Player && catalog.IsSqlReservedOnly(name))
                return null;
            return result switch
            {
                NameCatalogResult.Profane => CharResult.CharNameProfane,
                NameCatalogResult.Reserved => CharResult.CharNameReserved,
                _ => null,
            };
        }

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
    }

    private static async Task HandlePlayerLoginAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong rawGuid = reader.ReadUInt64();

        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IWorldDataStore worldData = session.Services.GetRequiredService<IWorldDataStore>();

        CharacterRecord? character = rawGuid <= int.MaxValue
            ? await characters.GetByIdAsync((int)rawGuid).ConfigureAwait(false)
            : null;
        if (character is null || character.AccountId != session.AccountId)
        {
            session.Logger.LogWarning("[{Endpoint}] login for character {Guid} not owned by '{Account}'",
                session.RemoteEndpoint, rawGuid, session.AccountName);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginNoCharacter));
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
            session.Logger.LogError(ex, "[{Endpoint}] character saves did not drain before login", session.RemoteEndpoint);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        RaceInfo? raceInfo = await worldData.GetRaceInfoAsync(character.Race, character.Gender).ConfigureAwait(false);
        ClassInfo? classInfo = await worldData.GetClassInfoAsync(character.Class).ConfigureAwait(false);
        var home = new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ);
        if (home.IsUnset && await worldData.GetStartPositionAsync(character.Race, character.Class).ConfigureAwait(false) is { } start)
        {
            home = new HomeBind(start.MapId, start.ZoneId, start.X, start.Y, start.Z);
        }

        if (raceInfo is null || classInfo is null || home.IsUnset)
        {
            session.Logger.LogError("[{Endpoint}] missing world data for character {Guid}", session.RemoteEndpoint, character.Id);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        IReadOnlyList<ActionButton> buttons = await characters.GetActionButtonsAsync(character.Id).ConfigureAwait(false);
        var player = new Player(character, CharacterPackets.BuildAppearance(raceInfo, classInfo), session, buttons) { Home = home };

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
            session.Logger.LogError(ex, "[{Endpoint}] a feature failed to load character {Guid}", session.RemoteEndpoint, character.Id);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        if (!session.TryBeginLogin())
        {
            return;
        }

        // Account settings belong to this session task; the world thread gets finished packets.
        var account = new AccountLoginPackets(
            LoginPackets.BuildAccountDataMd5(session.Settings),
            LoginPackets.BuildTutorialFlags(session.Settings.Tutorials));
        WorldRuntime world = session.World;
        world.Post(() => EnterWorld(session, world, character, player, account));
    }

    /// <summary>World thread: send the login sequence and put the player into its map.</summary>
    private static void EnterWorld(WorldSession session, WorldRuntime world, CharacterRecord character, Player player, AccountLoginPackets account)
    {
        if (world.IsOnline(player.Guid))
        {
            session.AbortLogin();
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginDuplicateCharacter));
            return;
        }

        if (!session.TryEnterWorld(player))
        {
            return; // the client disconnected while the character loaded
        }

        // Login order per vmangos WorldSession::HandlePlayerLogin (LoginSequence): login
        // packets, SendInitialPacketsBeforeAddToMap, the map add (which sends the self
        // create), SendInitialPacketsAfterAddToMap; then the world features hear of the login.
        player.Relocate(character.X, character.Y, character.Z, character.Orientation, world.NowMs);
        LoginSequence.SendLoginPackets(session, world, character, account.DataMd5);
        LoginSequence.SendInitialPacketsBeforeAddToMap(session, player, account.TutorialFlags);

        try
        {
            world.AddPlayer(player);
        }
        catch (Exception ex)
        {
            // Never leave a session "in world" with a player that is not in a map.
            session.Logger.LogError(ex, "[{Endpoint}] could not add '{Name}' to the world", session.RemoteEndpoint, player.Name);
            session.Kick();
            return;
        }

        LoginSequence.SendInitialPacketsAfterAddToMap(session, player);
        // Every loading hook and map insertion succeeded with fresh durable state.
        // Stale old-session callbacks cannot save or remove this player by GUID alone.
        session.Services.GetService<CharacterSaveQueue>()?.ResumeCharacter(character.Id);
        session.Services.GetService<QuestNpcFeature>()?.Persistence.ResumeCharacter(character.Id);
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

            await Task.Delay(100).ConfigureAwait(false);
        }

        return true;
    }

    private static void SendResult(WorldSession session, WorldOpcode opcode, CharResult result)
        => session.Send(opcode, [(byte)result]);

    /// <summary>Account-level login packets, built on the session task from its own settings.</summary>
    private sealed record AccountLoginPackets(byte[] DataMd5, byte[] TutorialFlags);
}
