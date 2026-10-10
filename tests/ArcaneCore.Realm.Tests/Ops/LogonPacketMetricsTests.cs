using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests.Ops;

/// <summary>
/// Per-command logon traffic (docs/ops/metrics.md, "Per-opcode traffic"). The sessions count on a private meter through the
/// injected table, so the exact numbers are not disturbed by other tests sharing the process-wide logon table.
/// </summary>
public sealed class LogonPacketMetricsTests
{
    // vmangos src/realmd/AuthCodes.h:31-40 (eAuthCmd).
    private static readonly Dictionary<byte, string> Reference = new()
    {
        [0x00] = "CMD_AUTH_LOGON_CHALLENGE",
        [0x01] = "CMD_AUTH_LOGON_PROOF",
        [0x02] = "CMD_AUTH_RECONNECT_CHALLENGE",
        [0x03] = "CMD_AUTH_RECONNECT_PROOF",
        [0x10] = "CMD_REALM_LIST",
        [0x30] = "CMD_XFER_INITIATE",
        [0x31] = "CMD_XFER_DATA",
        [0x32] = "CMD_XFER_ACCEPT",
        [0x33] = "CMD_XFER_RESUME",
        [0x34] = "CMD_XFER_CANCEL",
    };

    [Fact]
    public void Table_LabelsEveryAuthCommandWithItsReferenceName()
    {
        Assert.Equal(Reference.Count, LogonPacketMetrics.Commands.Count);
        foreach (KeyValuePair<int, string> command in LogonPacketMetrics.Commands)
        {
            Assert.Equal(Reference[(byte)command.Key], command.Value);
            Assert.Equal(command.Value, LogonPacketMetrics.Table.LabelOf((uint)command.Key));
        }

        foreach (AuthCommand command in Enum.GetValues<AuthCommand>())
        {
            Assert.Equal(Reference[(byte)command], LogonPacketMetrics.Table.LabelOf((byte)command));
        }

        Assert.Equal(OpcodeTable.UnknownLabel, LogonPacketMetrics.Table.LabelOf(0x7F));
        Assert.Equal("logon", LogonPacketMetrics.Table.Protocol);
    }

    [Fact]
    public async Task Session_CountsRealmListAndUnknownCommand()
    {
        using var meter = new Meter("ArcaneCore.Test." + Guid.NewGuid().ToString("N"));
        OpcodeTable table = new OpcodeTrafficMeter(meter).Register("logon", LogonPacketMetrics.Commands);
        using MeterListener listener = Listen(meter);

        (TcpClient tcp, Task server) = await StartSessionAsync(table);
        NetworkStream client = tcp.GetStream();
        using (tcp)
        {
            // CMD_REALM_LIST + its unused u32, then a byte no logon command uses. Unauthenticated, so no reply.
            await client.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0, 0x7F });
            await server; // the unknown byte closes the session
        }

        Assert.Equal(new OpcodeCounts(1, 5, 0, 0), table.Read((uint)AuthCommand.RealmList));
        Assert.Equal(new OpcodeCounts(1, 1, 0, 0), table.Read(0x7F));
        Assert.Equal(0, table.Read((uint)AuthCommand.LogonChallenge).PacketsIn);
    }

    [Fact]
    public async Task Session_CountsChallengeRequestAndReply()
    {
        using var meter = new Meter("ArcaneCore.Test." + Guid.NewGuid().ToString("N"));
        OpcodeTable table = new OpcodeTrafficMeter(meter).Register("logon", LogonPacketMetrics.Commands);
        using MeterListener listener = Listen(meter);
        byte[] request = BuildChallenge("GHOST");

        (TcpClient tcp, Task server) = await StartSessionAsync(table);
        NetworkStream client = tcp.GetStream();
        using (tcp)
        {
            await client.WriteAsync(request);
            byte[] reply = new byte[3];
            await client.ReadExactlyAsync(reply); // cmd, error 0, UnknownAccount (autocreate is off)
            Assert.Equal((byte)AuthResult.UnknownAccount, reply[2]);
            tcp.Client.Shutdown(SocketShutdown.Send);
            await server;
        }

        // cmd + error + u16 size + body is the whole request.
        Assert.Equal(new OpcodeCounts(1, request.Length, 1, 3), table.Read((uint)AuthCommand.LogonChallenge));
        Assert.Equal(0, table.Read(0x7F).PacketsIn);
    }

    private static MeterListener Listen(Meter meter)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();
        return listener;
    }

    private static async Task<(TcpClient Client, Task Server)> StartSessionAsync(OpcodeTable traffic)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = accepted.GetStream();
            var session = new LogonSession(
                stream, new InMemoryAccountStore(), new InMemoryRealmStore([]),
                new AuthOptions { AutocreateAccounts = false },
                NullLogger.Instance, "test", traffic: traffic);
            await session.RunAsync(CancellationToken.None);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return (client, server);
    }

    // Same layout as LogonHandshakeTests.BuildChallenge: cmd, error, u16 body size, then the sAuthLogonChallengeBody fields.
    private static byte[] BuildChallenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username.ToUpperInvariant());
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        byte[] build = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(build, ClientBuild.Vanilla1121);
        body.AddRange(build);
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[4]); // timezone
        body.AddRange(new byte[4]); // ip
        body.Add((byte)name.Length);
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        byte[] size = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)body.Count);
        packet.AddRange(size);
        packet.AddRange(body);
        return [.. packet];
    }
}
