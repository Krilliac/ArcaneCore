using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
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

    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgCharEnum, SessionStates.CharacterSelect, HandleCharEnumAsync);
        table.OnSession(WorldOpcode.CmsgCharCreate, SessionStates.CharacterSelect, HandleCharCreateAsync);
        table.OnSession(WorldOpcode.CmsgCharDelete, SessionStates.CharacterSelect, HandleCharDeleteAsync);
        table.OnSession(WorldOpcode.CmsgPlayerLogin, SessionStates.CharacterSelect, HandlePlayerLoginAsync);
    }

    private static async Task HandleCharEnumAsync(WorldSession session, byte[] payload)
    {
        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IReadOnlyList<CharacterRecord> list = await characters.GetByAccountAsync(session.AccountId).ConfigureAwait(false);

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

        session.Send(WorldOpcode.SmsgCharEnum, CharacterPackets.BuildCharEnum(list, equipment));
    }

    private static async Task HandleCharCreateAsync(WorldSession session, byte[] payload)
    {
        // CMSG_CHAR_CREATE: CString name, u8 race, class, gender, skin, face, hair style,
        // hair color, facial hair, outfit id (vmangos WorldSession::HandleCharCreateOpcode).
        var reader = new PacketReader(payload);
        string rawName = reader.ReadCString();
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

        // Checks in vmangos order: race/class, name rules, name in use, characters per realm.
        if (!await worldData.IsValidRaceClassAsync(race, cls).ConfigureAwait(false) || gender > 1)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateFailed);
            return;
        }

        string name = CharacterNames.Normalize(rawName);
        if (CharacterNames.Validate(name) is { } nameError)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, nameError);
            return;
        }

        if (await characters.IsNameTakenAsync(name).ConfigureAwait(false))
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateNameInUse);
            return;
        }

        if (await characters.CountByAccountAsync(session.AccountId).ConfigureAwait(false) >= session.World.Options.CharactersPerRealm)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateServerLimit);
            return;
        }

        StartPosition? start = await worldData.GetStartPositionAsync(race, cls).ConfigureAwait(false);
        if (start is null)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        CharacterRecord created = await characters.CreateAsync(new CharacterRecord
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
        }).ConfigureAwait(false);

        session.Services.GetRequiredService<CharacterDirectory>().Add(
            new CharacterIdentity(created.Id, created.AccountId, created.Name, created.Race, created.Gender, created.Class));
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
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateSuccess);
    }

    private static async Task HandleCharDeleteAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();

        // A character still in the world (e.g. a lingering previous session) is not deletable.
        bool deleted = guid <= int.MaxValue
            && !session.World.IsOnline(ObjectGuid.Player((uint)guid))
            && await session.Services.GetRequiredService<ICharacterStore>()
                .DeleteAsync((int)guid, session.AccountId).ConfigureAwait(false);
        if (deleted)
        {
            session.Services.GetRequiredService<CharacterDirectory>().Remove((int)guid);
        }

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
            if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            {
                await saves.FlushCharacterAsync(character.Id).ConfigureAwait(false);
            }

            // The previous session may have committed a reward while this login waited.
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
            foreach (ICharacterHooks hooks in session.Services.GetServices<ICharacterHooks>())
            {
                await hooks.OnPlayerLoadingAsync(session, character, player).ConfigureAwait(false);
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
