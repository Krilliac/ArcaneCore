using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>
/// <c>arcane-mock live</c>: log in to an already running loopback server (the dev runner, scripts/dev-runner.ps1),
/// enter the world and then stay connected, sending chat lines (for example <c>.server info</c>) on an interval and
/// printing every system reply with a timestamp and the connection's local port. It exists so a change made to the
/// server's code while it runs can be seen arriving on the SAME connection.
/// </summary>
internal static class LiveSession
{
    private const string DefaultPasswordVariable = "ARCANE_ACCOUNT_PASSWORD";
    private static readonly TimeSpan ReplyQuietPeriod = TimeSpan.FromMilliseconds(400);

    internal sealed record Options(
        IPEndPoint Realm,
        string Account,
        string Password,
        string Character,
        IReadOnlyList<string> Say,
        TimeSpan Interval,
        TimeSpan Duration,
        string? StopFile,
        string? ScriptFile,
        bool CharacterExplicit);

    internal static async Task<int> MainAsync(string[] args, TextWriter output, TextWriter error)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            return await RunAsync(options, output, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or MockProtocolException or InvalidOperationException or TimeoutException)
        {
            error.WriteLine($"{Stamp()} live session failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static string Usage =>
        "usage: arcane-mock live --account NAME (--password-env VAR | --credentials-file FILE) [--realm 127.0.0.1:3724]" + Environment.NewLine
        + "                        [--character NAME] [--say TEXT]... [--interval-ms 1000] [--duration-s 120] [--stop-file FILE] [--script-file FILE]" + Environment.NewLine
        + "       --script-file: every NEW line appended to this file while the session runs is sent as chat (e.g. .hotcode status)." + Environment.NewLine
        + "       --credentials-file reads 'account=' and 'password=' lines (scripts/dev-runner.ps1 writes one); the password is never an argument." + Environment.NewLine
        + "       The password variable defaults to " + DefaultPasswordVariable + ". The realm must be a loopback address.";

    internal static Options Parse(string[] args)
    {
        string realm = "127.0.0.1:3724";
        string? account = null;
        string? passwordVariable = null;
        string? credentials = null;
        string character = "Livetest";
        var say = new List<string>();
        int intervalMs = 1000;
        int durationSeconds = 120;
        string? stopFile = null;
        string? scriptFile = null;
        bool characterExplicit = false;

        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{flag} needs a value.");
            switch (flag)
            {
                case "--realm": realm = Next(); break;
                case "--account": account = Next(); break;
                case "--password-env": passwordVariable = Next(); break;
                case "--credentials-file": credentials = Next(); break;
                case "--character": character = Next(); characterExplicit = true; break;
                case "--say": say.Add(Next()); break;
                case "--interval-ms": intervalMs = ParseInt(Next(), flag, 50, 3_600_000); break;
                case "--duration-s": durationSeconds = ParseInt(Next(), flag, 1, 86_400); break;
                case "--stop-file": stopFile = Next(); break;
                case "--script-file": scriptFile = Next(); break;
                default: throw new ArgumentException($"unknown argument '{flag}'.");
            }
        }

        string? password = null;
        if (credentials is not null)
        {
            foreach (string line in File.ReadAllLines(credentials))
            {
                int eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }

                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                if (key.Equals("account", StringComparison.OrdinalIgnoreCase) && account is null)
                {
                    account = value;
                }
                else if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    password = value;
                }
            }
        }
        else
        {
            password = Environment.GetEnvironmentVariable(passwordVariable ?? DefaultPasswordVariable);
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            throw new ArgumentException("--account is required.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("no password: use --credentials-file, or set the environment variable named by --password-env.");
        }

        if (!IPEndPoint.TryParse(realm, out IPEndPoint? realmEndpoint))
        {
            throw new ArgumentException($"--realm '{realm}' is not a numeric endpoint.");
        }

        LoopbackOnly.Validate(realmEndpoint);
        return new Options(realmEndpoint, account, password, character, say, TimeSpan.FromMilliseconds(intervalMs),
            TimeSpan.FromSeconds(durationSeconds), stopFile, scriptFile, characterExplicit);
    }

    private static int ParseInt(string text, string flag, int min, int max)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"{flag} must be a whole number {min}..{max}.");

    internal static async Task<int> RunAsync(Options options, TextWriter output, CancellationToken ct)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(options.Realm, options.Account, options.Password, ct).ConfigureAwait(false);
        output.WriteLine($"{Stamp()} realm login ok; realm list: {string.Join(", ", logon.Realms.Select(r => $"{r.Name} {r.Address}"))}");
        IPEndPoint world = logon.Realms[0].GetLoopbackEndpoint();

        await using WorldClient client = await WorldClient.ConnectAsync(world, ct).ConfigureAwait(false);
        byte result = await client.AuthenticateAsync(options.Account, logon.SessionKey, ct).ConfigureAwait(false);
        if (result != 0x0C)
        {
            output.WriteLine($"{Stamp()} world authentication rejected: 0x{result:X2}");
            return 1;
        }

        var connection = new ScenarioConnection(client);
        IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(ct).ConfigureAwait(false);
        MockCharacter? chosen = SelectCharacter(characters, options.Character, options.CharacterExplicit, out bool createRequested);
        if (createRequested)
        {
            await connection.CreateCharacterAsync(options.Character, ct).ConfigureAwait(false);
            output.WriteLine($"{Stamp()} created character {options.Character}");
            chosen = (await connection.EnumerateAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(c => c.Name.Equals(options.Character, StringComparison.OrdinalIgnoreCase))
                ?? throw new MockProtocolException($"created character '{options.Character}' was not returned by character enumeration");
        }

        await connection.LoginAsync((chosen ?? throw new MockProtocolException("no character was selected or created")).Guid, ct).ConfigureAwait(false);
        string local = client.LocalEndPoint?.ToString() ?? "?";
        output.WriteLine($"{Stamp()} IN WORLD as {chosen.Name}; connection {local} -> {world}");

        DateTime end = DateTime.UtcNow + options.Duration;
        int sequence = 0;
        int sent = 0;
        int scriptIndex = 0;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            if (options.StopFile is not null && File.Exists(options.StopFile))
            {
                output.WriteLine($"{Stamp()} stop file seen");
                break;
            }

            foreach (string scripted in NewScriptLines(options.ScriptFile, ref scriptIndex))
            {
                output.WriteLine($"{Stamp()} [{local}] > {scripted}");
                await client.SendAsync((ushort)WorldOpcode.CmsgMessagechat, SayPacket(scripted), ct).ConfigureAwait(false);
                await DrainAsync(client, output, local, ReplyQuietPeriod, ct).ConfigureAwait(false);
            }

            if (options.Say.Count > 0)
            {
                string text = options.Say[sent++ % options.Say.Count];
                output.WriteLine($"{Stamp()} [{local}] > {text}");
                await client.SendAsync((ushort)WorldOpcode.CmsgMessagechat, SayPacket(text), ct).ConfigureAwait(false);
            }
            else
            {
                await client.SendAsync((ushort)WorldOpcode.CmsgPing, ScenarioWire.Ping(unchecked((uint)++sequence), 0), ct).ConfigureAwait(false);
            }

            await DrainAsync(client, output, local, ReplyQuietPeriod, ct).ConfigureAwait(false);
            await Task.Delay(options.Interval, ct).ConfigureAwait(false);
        }

        try
        {
            await LogoutAsync(connection, ct).ConfigureAwait(false);
            output.WriteLine($"{Stamp()} logged out cleanly; connection {local} stayed up for the whole session");
        }
        catch (Exception ex) when (ex is IOException or MockProtocolException or ObjectDisposedException)
        {
            output.WriteLine($"{Stamp()} logout did not complete cleanly: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task LogoutAsync(ScenarioConnection connection, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken bounded = timeout.Token;
        try
        {
            await connection.SendAsync(WorldOpcode.CmsgLogoutRequest, [], bounded).ConfigureAwait(false);
            byte[] response = await ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutResponse, bounded).ConfigureAwait(false);
            if (!IsSuccessfulLogoutResponse(response))
            {
                throw new MockProtocolException("logout response was not a successful normal or immediate logout");
            }

            byte[] complete = await ReadLogoutUntilAsync(connection, WorldOpcode.SmsgLogoutComplete, bounded).ConfigureAwait(false);
            if (complete.Length != 0)
            {
                throw new MockProtocolException("logout complete contained an unexpected body");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MockProtocolException("logout stage exceeded its bounded wait");
        }
        catch (TimeoutException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MockProtocolException($"logout packet read timed out before stage completion: {error.Message}");
        }
    }

    internal static async Task<byte[]> ReadLogoutUntilAsync(ScenarioConnection connection, WorldOpcode opcode, CancellationToken cancellationToken)
    {
        // The caller bounds the entire normal logout by 30 seconds. Nearby movement can exceed
        // 128 frames during the server's 20-second delay; bound drained bytes rather than crowd size.
        int receivedBytes = 0;
        while (true)
        {
            await connection.WaitForTrafficAsync(cancellationToken).ConfigureAwait(false);
            WorldFrame frame = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            receivedBytes += frame.Payload.Length + 4;
            if (receivedBytes > 1024 * 1024)
            {
                throw new MockProtocolException("logout exceeded its one-megabyte traffic budget");
            }
            if (frame.Opcode == (ushort)opcode)
            {
                return frame.Payload;
            }
        }
    }

    internal static MockCharacter? SelectCharacter(IReadOnlyList<MockCharacter> characters, string requested,
        bool explicitRequest, out bool createRequested)
    {
        createRequested = false;
        MockCharacter? chosen = characters.FirstOrDefault(c => c.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (chosen is not null || explicitRequest)
        {
            createRequested = chosen is null;
            return chosen;
        }

        MockCharacter? fallback = characters.FirstOrDefault();
        createRequested = fallback is null;
        return fallback;
    }

    internal static bool IsSuccessfulLogoutResponse(ReadOnlySpan<byte> payload)
        => payload.Length == 5
            && BinaryPrimitives.ReadUInt32LittleEndian(payload) == 0
            && payload[4] <= 1;

    /// <summary>Lines appended to the script file since the last call (the file may not exist yet or be mid-write).</summary>
    private static List<string> NewScriptLines(string? path, ref int index)
    {
        var fresh = new List<string>();
        if (path is null || !File.Exists(path))
        {
            return fresh;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            int line = 0;
            while (reader.ReadLine() is { } text)
            {
                if (line++ >= index && text.Trim().Length > 0)
                {
                    fresh.Add(text.Trim());
                }
            }

            index = line;
        }
        catch (IOException)
        {
            // mid-write: try again next cycle
        }

        return fresh;
    }

    /// <summary>CMSG_MESSAGECHAT: u32 type (say), u32 language (Common: the server only accepts Universal for AFK/DND), the CString message.</summary>
    internal static byte[] SayPacket(string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[8 + body.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)ChatType.Say);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)Language.Common);
        body.CopyTo(payload, 8);
        return payload;
    }

    /// <summary>SMSG_MESSAGECHAT for a system line: u8 type, u32 language, u64 sender, u32 length, text, u8 tag. Null for anything else.</summary>
    internal static string? DecodeSystemLine(byte[] payload)
    {
        if (payload.Length < 18 || payload[0] != (byte)ChatType.System)
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(13));
        if (length == 0 || length > payload.Length - 17)
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload, 17, (int)length - 1);
    }

    private static async Task DrainAsync(WorldClient client, TextWriter output, string local, TimeSpan quiet, CancellationToken ct)
    {
        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(quiet);
            try
            {
                await client.WaitForTrafficAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // quiet: no more reply is coming for this line
            }

            WorldFrame frame = await client.ReadAsync(ct).ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgMessagechat)
            {
                string? line = DecodeSystemLine(frame.Payload);
                output.WriteLine($"{Stamp()} [{local}] < {line ?? "(non-system chat frame, " + frame.Payload.Length.ToString(CultureInfo.InvariantCulture) + " bytes)"}");
            }
            else if (frame.Opcode == (ushort)WorldOpcode.SmsgNewWorld && frame.Payload.Length >= 20)
            {
                // A far teleport (.tele into a dungeon, a summon): the client ends its loading screen with MSG_MOVE_WORLDPORT_ACK, as the
                // 1.12 client does (vmangos WorldSession::HandleMoveWorldportAckOpcode); without it the player stays between maps.
                uint map = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
                float x = BinaryPrimitives.ReadSingleLittleEndian(frame.Payload.AsSpan(4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(frame.Payload.AsSpan(8));
                float z = BinaryPrimitives.ReadSingleLittleEndian(frame.Payload.AsSpan(12));
                output.WriteLine(FormattableString.Invariant($"{Stamp()} [{local}] < SMSG_NEW_WORLD map {map} ({x:F1}, {y:F1}, {z:F1}); sending MSG_MOVE_WORLDPORT_ACK"));
                await client.SendAsync((ushort)WorldOpcode.MsgMoveWorldportAck, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            }
            else if (frame.Opcode == (ushort)WorldOpcode.SmsgTransferAborted)
            {
                output.WriteLine($"{Stamp()} [{local}] < SMSG_TRANSFER_ABORTED {Convert.ToHexString(frame.Payload)}");
            }
        }
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
