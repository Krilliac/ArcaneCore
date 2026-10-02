using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
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
        table.OnSession(WorldOpcode.CmsgCharEnum, SessionState.CharacterSelect, HandleCharEnumAsync);
        table.OnSession(WorldOpcode.CmsgCharCreate, SessionState.CharacterSelect, HandleCharCreateAsync);
        table.OnSession(WorldOpcode.CmsgCharDelete, SessionState.CharacterSelect, HandleCharDeleteAsync);
        table.OnSession(WorldOpcode.CmsgPlayerLogin, SessionState.CharacterSelect, HandlePlayerLoginAsync);
    }

    private static async Task HandleCharEnumAsync(WorldSession session, byte[] payload)
    {
        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IReadOnlyList<CharacterRecord> list = await characters.GetByAccountAsync(session.AccountId).ConfigureAwait(false);
        session.Send(WorldOpcode.SmsgCharEnum, CharacterPackets.BuildCharEnum(list));
    }

    private static async Task HandleCharCreateAsync(WorldSession session, byte[] payload)
    {
        // CMSG_CHAR_CREATE: CString name, u8 race, class, gender, skin, face, hair style,
        // hair color, facial hair, outfit id (vmangos WorldSession::HandleCharCreateOpcode).
        var reader = new PacketReader(payload);
        string name = reader.ReadCString().Trim();
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

        if (name.Length is < 1 or > 12)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharNameNoName);
            return;
        }

        if (!await worldData.IsValidRaceClassAsync(race, cls).ConfigureAwait(false) || gender > 1)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateFailed);
            return;
        }

        if (await characters.IsNameTakenAsync(name).ConfigureAwait(false))
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateNameInUse);
            return;
        }

        StartPosition? start = await worldData.GetStartPositionAsync(race, cls).ConfigureAwait(false);
        if (start is null)
        {
            SendResult(session, WorldOpcode.SmsgCharCreate, CharResult.CharCreateError);
            return;
        }

        await characters.CreateAsync(new CharacterRecord
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
        }).ConfigureAwait(false);

        session.Logger.LogInformation("[{Endpoint}] '{Account}' created character '{Name}'",
            session.RemoteEndpoint, session.AccountName, name);
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

        RaceInfo? raceInfo = await worldData.GetRaceInfoAsync(character.Race, character.Gender).ConfigureAwait(false);
        ClassInfo? classInfo = await worldData.GetClassInfoAsync(character.Class).ConfigureAwait(false);
        if (raceInfo is null || classInfo is null)
        {
            session.Logger.LogError("[{Endpoint}] missing world data for character {Guid}", session.RemoteEndpoint, character.Id);
            session.Send(WorldOpcode.SmsgCharacterLoginFailed, CharacterPackets.BuildLoginFailed(CharResult.CharLoginFailed));
            return;
        }

        if (!session.TryBeginLogin())
        {
            return;
        }

        var player = new Player(character, CharacterPackets.BuildAppearance(raceInfo, classInfo), session);
        WorldRuntime world = session.World;
        world.Post(() => EnterWorld(session, world, character, player));
    }

    /// <summary>World thread: send the login sequence and put the player into its map.</summary>
    private static void EnterWorld(WorldSession session, WorldRuntime world, CharacterRecord character, Player player)
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

        // Login order per vmangos HandlePlayerLogin / Player::SendInitialPacketsBeforeAddToMap:
        // verify world, tutorials, spells, time speed, then the map add sends the self create.
        player.Relocate(character.X, character.Y, character.Z, character.Orientation, world.NowMs);
        session.Send(WorldOpcode.SmsgLoginVerifyWorld, CharacterPackets.BuildLoginVerifyWorld(character));
        session.Send(WorldOpcode.SmsgTutorialFlags, CharacterPackets.BuildTutorialFlags());
        session.Send(WorldOpcode.SmsgInitialSpells, CharacterPackets.BuildInitialSpells());
        session.Send(WorldOpcode.SmsgLoginSettimespeed, CharacterPackets.BuildTimeSpeed(DateTime.UtcNow));

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

        session.Logger.LogInformation("[{Endpoint}] '{Account}' entered the world as '{Name}'",
            session.RemoteEndpoint, session.AccountName, player.Name);
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
}
