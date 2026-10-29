using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Server-side storage of the client's settings blobs and tutorial flags (vmangos
/// MiscHandler.cpp HandleUpdateAccountData / HandleRequestAccountData, CharacterHandler.cpp
/// HandleTutorial*). Both belong to the account, so they run on the session task, which owns
/// <see cref="WorldSession.Settings"/>, and write through to the characters database: an unchanged value is not written, and writes
/// go through the world-lifetime, per-account throttled <see cref="AccountDataFeature"/>, which flushes held values when the session ends
/// and when the world stops. The warnings a client can trigger are throttled per session.
/// </summary>
public sealed class AccountDataHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        // The client uploads changed blobs when it leaves the world, i.e. back at the
        // character screen (vmangos STATUS_LOGGEDIN_OR_RECENTLY_LOGGEDOUT), so accept both
        // account-data opcodes in every authenticated state.
        table.OnSession(WorldOpcode.CmsgUpdateAccountData, SessionStates.Authenticated, HandleUpdateAccountDataAsync);
        table.OnSession(WorldOpcode.CmsgRequestAccountData, SessionStates.Authenticated, HandleRequestAccountDataAsync);
        table.OnSession(WorldOpcode.CmsgTutorialFlag, SessionStates.LoggedIn, HandleTutorialFlagAsync);
        table.OnSession(WorldOpcode.CmsgTutorialClear, SessionStates.LoggedIn, (session, _) => SetAllTutorialsAsync(session, 0xFFFFFFFF));
        table.OnSession(WorldOpcode.CmsgTutorialReset, SessionStates.LoggedIn, (session, _) => SetAllTutorialsAsync(session, 0));
    }

    /// <summary>CMSG_UPDATE_ACCOUNT_DATA: u32 type, u32 decompressed size, zlib data; size 0 erases.</summary>
    private static async Task HandleUpdateAccountDataAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint type = reader.ReadUInt32();
        uint size = reader.ReadUInt32();
        ReadOnlySpan<byte> compressed = reader.ReadToEnd();

        if (type >= AccountSettings.DataTypeCount)
        {
            if (ClientWarningGates.TryEnter(session, "account-data-type", out int suppressed))
            {
                session.Logger.LogWarning("[{Endpoint}] account data type {Type} out of range ({Suppressed} more since the last line)", session.RemoteEndpoint, type, suppressed);
            }

            return;
        }

        byte[] data;
        if (size == 0)
        {
            data = [];
        }
        else if (size > AccountDataCompression.MaxDecompressedSize
            || !AccountDataCompression.TryDecompress(compressed, (int)size, out data))
        {
            if (ClientWarningGates.TryEnter(session, "account-data-rejected", out int suppressed))
            {
                session.Logger.LogWarning("[{Endpoint}] rejected account data type {Type} ({Size} bytes declared; {Suppressed} more since the last line)",
                    session.RemoteEndpoint, type, size, suppressed);
            }

            return;
        }

        // Unchanged (same blob, or an erase of nothing): no write.
        AccountDataEntry? current = session.Settings.Data[type];
        if (data.Length == 0 ? current is null : current is not null && current.Data.AsSpan().SequenceEqual(data))
        {
            return;
        }

        var entry = new AccountDataEntry((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), data);
        session.Settings.Data[type] = data.Length == 0 ? null : entry;
        await SaveDataAsync(session, (int)type, entry).ConfigureAwait(false);
    }

    /// <summary>
    /// Through the world-lifetime <see cref="AccountDataFeature"/> (its own scope per write, throttled, drained at session end and world
    /// stop). A host without the feature or a store (a bare test host) writes inline to the session's own store on the session task,
    /// which owns that scope, so the session's context is never used from another thread.
    /// </summary>
    private static Task SaveDataAsync(WorldSession session, int type, AccountDataEntry entry)
    {
        if (session.Services.GetService<AccountDataFeature>() is { IsAttached: true } feature)
        {
            feature.SaveData(session.AccountId, type, entry, session.World.Options.AccountDataWriteIntervalMs);
            return Task.CompletedTask;
        }

        return session.Services.GetRequiredService<IAccountDataStore>().SaveDataAsync(session.AccountId, type, entry);
    }

    /// <summary>CMSG_REQUEST_ACCOUNT_DATA: u32 type → SMSG_UPDATE_ACCOUNT_DATA with the stored blob.</summary>
    private static Task HandleRequestAccountDataAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint type = reader.ReadUInt32();
        if (type >= AccountSettings.DataTypeCount)
        {
            if (ClientWarningGates.TryEnter(session, "account-data-type", out int suppressed))
            {
                session.Logger.LogWarning("[{Endpoint}] account data type {Type} out of range ({Suppressed} more since the last line)", session.RemoteEndpoint, type, suppressed);
            }

            return Task.CompletedTask;
        }

        byte[] data = session.Settings.Data[type]?.Data ?? [];
        session.Send(WorldOpcode.SmsgUpdateAccountData, MiscPackets.BuildUpdateAccountData(type, data));
        return Task.CompletedTask;
    }

    /// <summary>CMSG_TUTORIAL_FLAG: u32 flag index; sets bit index % 32 of word index / 32 (vmangos HandleTutorialFlagOpcode).</summary>
    private static Task HandleTutorialFlagAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint flag = reader.ReadUInt32();
        uint word = flag / 32;
        if (word >= AccountSettings.TutorialWordCount)
        {
            return Task.CompletedTask;
        }

        uint[] tutorials = session.Settings.Tutorials;
        uint updated = tutorials[word] | (1u << (int)(flag % 32));
        if (updated == tutorials[word])
        {
            return Task.CompletedTask;
        }

        tutorials[word] = updated;
        return SaveTutorialsAsync(session);
    }

    /// <summary>CMSG_TUTORIAL_CLEAR marks every tutorial seen; CMSG_TUTORIAL_RESET shows them all again.</summary>
    private static Task SetAllTutorialsAsync(WorldSession session, uint value)
    {
        uint[] tutorials = session.Settings.Tutorials;
        if (Array.TrueForAll(tutorials, word => word == value))
        {
            return Task.CompletedTask;
        }

        Array.Fill(tutorials, value);
        return SaveTutorialsAsync(session);
    }

    private static Task SaveTutorialsAsync(WorldSession session)
    {
        uint[] tutorials = [.. session.Settings.Tutorials];
        if (session.Services.GetService<AccountDataFeature>() is { IsAttached: true } feature)
        {
            feature.SaveTutorials(session.AccountId, tutorials, session.World.Options.AccountDataWriteIntervalMs);
            return Task.CompletedTask;
        }

        return session.Services.GetRequiredService<IAccountDataStore>().SaveTutorialsAsync(session.AccountId, tutorials);
    }
}
