using System.Buffers.Binary;
using System.Text.Json;
using ArcaneCore.Game.Items;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Finite normal-session probe for the build-5875 missing-item feedback producer.</summary>
internal static class ItemFeedbackProbe
{
    private const int OverallSeconds = 30;
    private const int ActionSeconds = 5;
    private const int MaximumFrames = 128;
    private const long MaximumBytes = 1024 * 1024;
    private const byte MissingSlot = 250;

    internal sealed record ProbeReport(
        string Outcome,
        byte? ActualErrorCode,
        int? ActualBodyLength,
        int Frames,
        long Bytes,
        bool CleanLogout,
        string? Error = null);

    internal static async Task<int> MainAsync(string[] args, TextWriter output, TextWriter error)
    {
        ProbeReport report;
        ScenarioConnection? connection = null;
        WorldClient? client = null;
        bool entered = false, cleanLogout = false;
        byte? errorCode = null;
        int? bodyLength = null;
        string? failure = null;
        try
        {
            ValidateWhitelist(args);
            LiveSession.Options options = LiveSession.Parse(args);
            _ = ProtocolPackets.NormalizeAccount(options.Account);
            _ = ProtocolPackets.NormalizePassword(options.Password);

            using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(OverallSeconds));
            LogonResult logon = await LogonClient.AuthenticateAsync(options.Realm, options.Account, options.Password,
                overall.Token).ConfigureAwait(false);
            client = await WorldClient.ConnectAsync(
                logon.Realms.Single().GetLoopbackEndpoint(), overall.Token).ConfigureAwait(false);
            byte worldResult = await client.AuthenticateAsync(options.Account, logon.SessionKey, overall.Token)
                .ConfigureAwait(false);
            if (worldResult != 0x0C)
                throw new MockProtocolException($"world authentication rejected: 0x{worldResult:X2}");

            connection = new ScenarioConnection(client, maximumObjects: 4096,
                maximumFrames: 4096, maximumBytes: MaximumBytes);
            IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(overall.Token)
                .ConfigureAwait(false);
            MockCharacter? selected = LiveSession.SelectCharacter(characters, options.Character, explicitRequest: true,
                out bool createRequested);
            if (createRequested)
            {
                await connection.CreateCharacterAsync(options.Character, overall.Token).ConfigureAwait(false);
                selected = (await connection.EnumerateAsync(overall.Token).ConfigureAwait(false))
                    .FirstOrDefault(c => c.Name.Equals(options.Character, StringComparison.OrdinalIgnoreCase));
            }

            if (selected is null || selected.Race != 1 || selected.Class != 1)
                throw new MockProtocolException("probe requires an explicit Human Warrior character");
            await connection.LoginAsync(selected.Guid, overall.Token).ConfigureAwait(false);
            entered = true;

            using (var action = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
            {
                action.CancelAfter(TimeSpan.FromSeconds(ActionSeconds));
                await connection.SendAsync(WorldOpcode.CmsgUseItem,
                    [InventorySlots.Bag0, MissingSlot, 0, 0, 0], action.Token).ConfigureAwait(false);
                WorldFrame frame = await ReadFailureAsync(connection, selected.Guid, action.Token).ConfigureAwait(false);
                errorCode = frame.Payload.Length > 0 ? frame.Payload[0] : null;
                bodyLength = frame.Payload.Length;
                failure = ValidateFailure(frame);
            }

        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or TimeoutException or OperationCanceledException)
        {
            failure = ex.GetType().Name;
            error.WriteLine($"Item feedback probe failed: {failure}");
        }
        finally
        {
            if (entered && connection is not null)
            {
                try { cleanLogout = await LogoutAsync(connection).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
                { failure ??= "logout-incomplete"; }
            }
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        report = new ProbeReport(entered && failure is null && cleanLogout ? "passed" : "failed",
            errorCode, bodyLength, connection?.FramesReceived ?? 0, connection?.BytesReceived ?? 0, cleanLogout, failure);

        output.WriteLine(JsonSerializer.Serialize(report));
        return report.Outcome == "passed" ? 0 : 1;
    }

    private static async Task<WorldFrame> ReadFailureAsync(ScenarioConnection connection, ulong player, CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumFrames; index++)
        {
            await connection.WaitForTrafficAsync(cancellationToken).ConfigureAwait(false);
            WorldFrame frame = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgInventoryChangeFailure)
                return frame;
            if (IsOwnSuccess(frame, player))
                throw new MockProtocolException("missing-item use produced a successful spell or item-use packet");
        }

        throw new MockProtocolException($"item failure was not observed within {MaximumFrames} frames");
    }

    internal static bool IsOwnSuccess(WorldFrame frame, ulong player)
    {
        if (frame.Opcode == (ushort)WorldOpcode.SmsgItemPushResult)
        {
            if (frame.Payload.Length < 8) throw new MockProtocolException("malformed item-push result");
            return BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload) == player;
        }
        if (frame.Opcode != (ushort)WorldOpcode.SmsgSpellStart && frame.Opcode != (ushort)WorldOpcode.SmsgSpellGo) return false;
        try
        {
            var reader = new PacketReader(frame.Payload);
            _ = reader.ReadPackedGuid();
            return reader.ReadPackedGuid() == player;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        { throw new MockProtocolException("malformed spell success body"); }
    }

    internal static string? ValidateFailure(WorldFrame frame)
    {
        if (frame.Opcode != (ushort)WorldOpcode.SmsgInventoryChangeFailure)
            return "unexpected feedback opcode";
        if (frame.Payload.Length != 18)
            return $"unexpected inventory failure body length {frame.Payload.Length}; expected 18";
        if (frame.Payload[0] != (byte)InventoryResult.ItemNotFound)
            return $"unexpected inventory failure code {frame.Payload[0]}; expected {(byte)InventoryResult.ItemNotFound}";
        if (BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(1)) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(9)) != 0
            || frame.Payload[17] != 0)
            return "missing-item failure contained nonzero item GUIDs or bag slot";
        return null;
    }

    private static async Task<bool> LogoutAsync(ScenarioConnection connection)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(OverallSeconds));
        await connection.SendAsync(WorldOpcode.CmsgLogoutRequest, [], bound.Token).ConfigureAwait(false);
        byte[] response = await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutResponse, bound.Token)
            .ConfigureAwait(false);
        if (!LiveSession.IsSuccessfulLogoutResponse(response))
            return false;
        return (await LiveSession.ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, bound.Token)
            .ConfigureAwait(false)).Length == 0;
    }

    private static void ValidateWhitelist(string[] args)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            if (flag is not ("--account" or "--character" or "--password-env" or "--realm"))
                throw new ArgumentException("unsupported item-feedback probe argument");
            if (!seen.Add(flag) || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{flag} requires one value and cannot be repeated.");
        }

        if (!seen.SetEquals(["--account", "--character", "--password-env"]) &&
            !(seen.Contains("--account") && seen.Contains("--character") && seen.Contains("--password-env")))
            throw new ArgumentException("--account, --character, and --password-env are required.");
    }
}
