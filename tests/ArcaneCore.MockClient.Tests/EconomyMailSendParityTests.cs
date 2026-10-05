using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// CMSG_SEND_MAIL against vmangos: silent drops (D:\refs\vmangos\src\game\Handlers\MailHandler.cpp:155-166), the
/// recipient box cap (:258), COD zeroing and the one-hour delivery delay (:404-411), SMSG_RECEIVED_MAIL when a delayed
/// letter becomes deliverable (D:\refs\vmangos\src\game\Chat\MasterPlayer.cpp:66-76), the 3-day read clamp (:454-458).
/// Only SQLite is exercised here; no store, query or schema changed.
/// </summary>
public sealed class EconomyMailSendParityTests
{
    private const string SenderAccount = "MAILSENDER";
    private const string ReceiverAccount = "MAILRECEIVER";
    private const string Password = "PASSWORD";
    /// <summary>The synthetic world has a game object system but no mailbox spawn; the real check is covered by MailboxAccessTests.</summary>
    internal sealed class AnyMailbox : IMailboxAccess
    {
        public bool CanUseMailbox(Player player, ObjectGuid mailbox) => player.IsInWorld && mailbox.High == HighGuid.GameObject;
    }

    /// <summary>The synthetic NPC is a Stormwind-style auctioneer of house 2 with 15% deposit and 5% cut.</summary>
    internal sealed class AnyAuctioneer : IAuctioneerAccess
    {
        public AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer)
            => player.IsInWorld && auctioneer.Value == SyntheticArcaneServer.NpcGuid ? new AuctionHouseEntry(2, 15, 5) : null;
    }

    internal static readonly ObjectGuid Mailbox = ObjectGuid.WithEntry(HighGuid.GameObject, 900081, 1);

    [Theory]
    [InlineData("subject")]
    [InlineData("body")]
    [InlineData("cod")]
    public async Task Oversize_subject_body_and_cod_are_dropped_without_an_answer(string field)
    {
        await using Rig rig = await Rig.StartAsync();
        string subject = field == "subject" ? new string('s', 65) : "ok";
        string body = field == "body" ? new string('b', 501) : "";
        uint cod = field == "cod" ? 100_000_001u : 0u;
        await rig.SendAsync("Mailrecv", subject, body, money: 0, cod);
        // Packets arrive in order: a refused mail to nobody answers RecipientNotFound, so if the oversize letter had been
        // answered or stored, the first result would be its own.
        await rig.SendAsync("Nobodyxx", "hi", "", 0, 0);
        (_, MailResult result) = await rig.ReadResultAsync();
        Assert.Equal(MailResult.RecipientNotFound, result);
        Assert.Empty(await rig.ReceiverMailAsync());
    }

    [Fact]
    public async Task Letters_get_vmangos_delay_expiry_and_cod_zeroing()
    {
        await using Rig rig = await Rig.StartAsync();
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        await rig.SendAsync("Mailrecv", "text", "hello", 0, 0);
        Assert.Equal(MailResult.Ok, (await rig.ReadResultAsync()).Result);
        await rig.SendAsync("Mailrecv", "gold", "", 10, 0);
        Assert.Equal(MailResult.Ok, (await rig.ReadResultAsync()).Result);
        await rig.SendAsync("Mailrecv", "cod", "", 0, 50);
        Assert.Equal(MailResult.Ok, (await rig.ReadResultAsync()).Result);

        IReadOnlyList<MailRecord> mails = (await rig.ReceiverMailAsync()).OrderBy(m => m.Id).ToList();
        Assert.Equal(3, mails.Count);
        MailRecord text = mails[0], gold = mails[1], cod = mails[2];
        Assert.Equal((now, now + (30 * 86400)), (text.DeliverTime, text.ExpireTime));
        Assert.True((text.Checked & MailCheckMask.HasBody) != 0);
        Assert.Equal((now + 3600, now + 3600 + (30 * 86400)), (gold.DeliverTime, gold.ExpireTime));
        Assert.Equal(10u, gold.Money);
        Assert.Equal((0u, now), (cod.Cod, cod.DeliverTime)); // a COD without an item is zeroed, not refused
        Assert.True((cod.Checked & MailCheckMask.Copied) != 0);
    }

    [Fact]
    public async Task Recipient_box_is_refused_only_above_the_cap()
    {
        await using Rig rig = await Rig.StartAsync();
        int receiver = checked((int)rig.ReceiverGuid);
        await using (AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope())
        {
            long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
            var seed = new List<EconomyChange>();
            for (uint i = 0; i < 100; i++)
            {
                seed.Add(new InsertMail(new MailRecord
                {
                    Id = 5000 + i, MessageType = MailMessageType.Auction, SenderId = 1, ReceiverId = receiver, Subject = "seed",
                    DeliverTime = now, ExpireTime = now + 86400,
                }, null));
            }

            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
                .CommitAsync(new EconomyCommitRequest(Guid.NewGuid(), [], seed)));
        }

        await rig.SendAsync("Mailrecv", "one", "", 0, 0); // 100 letters held: accepted (vmangos refuses count > 100)
        Assert.Equal(MailResult.Ok, (await rig.ReadResultAsync()).Result);
        await rig.SendAsync("Mailrecv", "two", "", 0, 0); // 101 held: refused
        Assert.Equal(MailResult.RecipientCapReached, (await rig.ReadResultAsync()).Result);
    }

    [Fact]
    public async Task Delayed_letter_is_hidden_then_announced_with_received_mail_and_reading_clamps_expiry()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        await rig.SendAsync("Mailrecv", "gold", "", 10, 0);
        Assert.Equal(MailResult.Ok, (await rig.ReadResultAsync()).Result);

        // Nothing is announced and the list is empty while the letter is in transit.
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgPing, ScenarioWire.Ping(1, 0), CancellationToken.None);
        await rig.AssertNoReceivedMailUntilPongAsync();
        await rig.Receiver.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), CancellationToken.None);
        Assert.Equal(0, (await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, CancellationToken.None))[0]);

        rig.Clock.Advance(TimeSpan.FromSeconds(3601));
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgReceivedMail, rig.Token);
        await rig.Receiver.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), CancellationToken.None);
        Assert.Equal(1, (await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, CancellationToken.None))[0]);

        MailRecord letter = Assert.Single(await rig.ReceiverMailAsync());
        await rig.Receiver.SendAsync(WorldOpcode.CmsgMailMarkAsRead, ScenarioWire.GuidQuest(Mailbox.Value, letter.Id), CancellationToken.None);
        // The handler runs asynchronously and has no reply packet: poll the store until the read flag lands.
        MailRecord read = letter;
        for (int attempt = 0; attempt < 100 && (read.Checked & MailCheckMask.Read) == 0; attempt++)
        {
            await Task.Delay(50, rig.Token);
            read = Assert.Single(await rig.ReceiverMailAsync());
        }

        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(now + (3 * 86400), read.ExpireTime);
        Assert.True((read.Checked & MailCheckMask.Read) != 0);
    }

    internal sealed class Rig : IAsyncDisposable
    {
        private readonly WorldClient _senderClient;
        private readonly WorldClient? _receiverClient;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));

        private Rig(SyntheticArcaneServer server, ManualClock clock, WorldClient sender, ScenarioConnection senderConn, ulong receiverGuid,
            WorldClient? receiverClient, ScenarioConnection? receiverConn)
        {
            Server = server;
            Clock = clock;
            _senderClient = sender;
            Sender = senderConn;
            ReceiverGuid = receiverGuid;
            _receiverClient = receiverClient;
            Receiver = receiverConn;
        }

        public SyntheticArcaneServer Server { get; }
        public ManualClock Clock { get; }
        public ScenarioConnection Sender { get; }
        public ScenarioConnection? Receiver { get; }
        public ulong ReceiverGuid { get; }
        public ulong SenderGuid { get; init; }
        public CancellationToken Token => _deadline.Token;

        public static async Task<Rig> StartAsync(bool loginReceiver = false, Action<IServiceCollection>? configureServices = null)
        {
            var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            CancellationToken token = deadline.Token;
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(
                services =>
                {
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton<IMailboxAccess>(new AnyMailbox());
                    services.AddSingleton<IAuctioneerAccess>(new AnyAuctioneer());
                    configureServices?.Invoke(services);
                }, token);
            await server.AddAccountAsync(SenderAccount, Password, token);
            await server.AddAccountAsync(ReceiverAccount, Password, token);

            // The receiver character is created first, then the sender logs in and is funded.
            WorldClient receiverClient = await AuthenticateAsync(server, ReceiverAccount, token);
            var receiverConn = new ScenarioConnection(receiverClient);
            await receiverConn.CreateCharacterAsync("Mailrecv", token);
            ulong receiverGuid = Assert.Single(await receiverConn.EnumerateAsync(token)).Guid;
            if (loginReceiver)
            {
                await receiverConn.LoginAsync(receiverGuid, token);
            }
            else
            {
                await receiverClient.DisposeAsync();
                receiverClient = null!;
                receiverConn = null!;
            }

            WorldClient senderClient = await AuthenticateAsync(server, SenderAccount, token);
            var senderConn = new ScenarioConnection(senderClient);
            await senderConn.CreateCharacterAsync("Mailsend", token);
            ulong senderGuid = Assert.Single(await senderConn.EnumerateAsync(token)).Guid;
            await senderConn.LoginAsync(senderGuid, token);
            await server.World.InvokeAsync(() =>
            {
                Player player = server.World.FindOnlinePlayer(new ObjectGuid(senderGuid))!;
                player.Money = 10_000;
                return true;
            }).WaitAsync(token);
            return new Rig(server, clock, senderClient, senderConn, receiverGuid, receiverClient, receiverConn) { SenderGuid = senderGuid };
        }

        public Task SendAsync(string receiver, string subject, string body, uint money, uint cod)
        {
            using var stream = new MemoryStream();
            Span<byte> eight = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(eight, Mailbox.Value);
            stream.Write(eight);
            foreach (string text in new[] { receiver, subject, body })
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.WriteByte(0);
            }

            stream.Write(new byte[8]);   // two unknown u32
            stream.Write(new byte[8]);   // item guid: none
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(four, money);
            stream.Write(four);
            BinaryPrimitives.WriteUInt32LittleEndian(four, cod);
            stream.Write(four);
            stream.Write(new byte[8]);
            return Sender.SendAsync(WorldOpcode.CmsgSendMail, stream.ToArray(), Token);
        }

        public async Task<(MailAction Action, MailResult Result)> ReadResultAsync()
        {
            byte[] payload = await Sender.ReadUntilAsync(WorldOpcode.SmsgSendMailResult, Token);
            var reader = new PacketReader(payload);
            reader.ReadUInt32();
            MailAction action = (MailAction)reader.ReadUInt32();
            return (action, (MailResult)reader.ReadUInt32());
        }

        public async Task<IReadOnlyList<MailRecord>> ReceiverMailAsync()
        {
            await Server.Services.GetRequiredService<EconomyFeature>().DrainAsync().WaitAsync(Token);
            await using AsyncServiceScope scope = Server.Services.CreateAsyncScope();
            return await new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
                .GetMailsAsync(checked((int)ReceiverGuid), Token);
        }

        public async Task<IReadOnlyList<MailRecord>> MailsOfAsync(ulong guid)
        {
            await Server.Services.GetRequiredService<EconomyFeature>().DrainAsync().WaitAsync(Token);
            await using AsyncServiceScope scope = Server.Services.CreateAsyncScope();
            return await new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
                .GetMailsAsync(checked((int)guid), Token);
        }

        public async Task AssertNoReceivedMailUntilPongAsync()
        {
            while (true)
            {
                WorldFrame frame = await Receiver!.ReadAsync(Token);
                Assert.NotEqual((ushort)WorldOpcode.SmsgReceivedMail, frame.Opcode);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgPong)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _senderClient.DisposeAsync();
            if (_receiverClient is not null)
            {
                await _receiverClient.DisposeAsync();
            }

            await Server.DisposeAsync();
            _deadline.Dispose();
        }
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, string account, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, account, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        Assert.Equal((byte)0x0C, await client.AuthenticateAsync(account, logon.SessionKey, token));
        return client;
    }

    public sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
