using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.TestSupport;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// <c>game_event_mail</c> delivery (vmangos GameEventMgr::SendEventMails, GameEventMgr.cpp:1038-1061 and the resume rule at :802-804):
/// the service hands each row of the started event to its mail sender, a resume does not, and the delivery mails the online characters
/// of the row's races (and finishers of its quest) through the <see cref="IScriptMailSink"/>.
/// </summary>
public sealed class GameEventMailTests
{
    private const ushort Scourge = 17;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingSender : IGameEventMailSender
    {
        public List<(int Event, GameEventMailRecord Mail)> Calls { get; } = [];

        public void Send(int signedEvent, GameEventMailRecord mail) => Calls.Add((signedEvent, mail));
    }

    private sealed class RecordingMail : IScriptMailSink
    {
        public List<(Player Receiver, uint Sender, uint Template, uint Money, uint Delay)> Calls { get; } = [];

        public void Send(Player receiver, uint senderEntry, uint templateId, uint money, uint delaySeconds)
            => Calls.Add((receiver, senderEntry, templateId, money, delaySeconds));
    }

    private static GameEventService Make(RecordingSender sender, params GameEventMailRecord[] mails)
    {
        var options = new GameEventOptions();
        var content = new GameEventContent(
            [new GameEventRecord(Scourge, 1, 1440, 120, 0, 0, "Scourge Invasion")],
            [new GameEventTimeRecord(Scourge, Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "2030-12-31 22:59:59")],
            [], [], [], [], mails);
        GameEventLoadResult load = GameEventLoader.Load(content, options, Now, TimeZoneInfo.Utc);
        var service = new GameEventService(load, options, () => Now, TimeZoneInfo.Utc, NullLogger.Instance);
        service.SetMailSender(sender);
        return service;
    }

    private static Player MakePlayer(int id, byte race)
    {
        var character = new CharacterRecord { Id = id, AccountId = 1, Name = "Mailee" + id, Race = race, Class = 1, Gender = 0, Level = 60, MapId = 0 };
        return new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
    }

    [Fact]
    public void StartingTheEvent_SendsItsMailRowsOnce_AndAResumeSendsNone()
    {
        var row = new GameEventMailRecord(Scourge, 255, 0, 171, 16285);
        var started = new RecordingSender();
        GameEventService service = Make(started, row);

        service.Initialize(new HashSet<ushort>());
        service.Update();

        Assert.True(service.IsActiveEvent(Scourge));
        Assert.Equal([(Scourge, row)], started.Calls);

        var resumed = new RecordingSender();
        GameEventService afterRestart = Make(resumed, row);
        afterRestart.Initialize(new HashSet<ushort> { Scourge });

        Assert.True(afterRestart.IsActiveEvent(Scourge));
        Assert.Empty(resumed.Calls);
    }

    [Fact]
    public void StoppingTheEvent_SendsTheNegativeRows_AndAStartDoesNotSendThem()
    {
        var start = new GameEventMailRecord(Scourge, 255, 0, 171, 16285);
        var stop = new GameEventMailRecord(-Scourge, 255, 0, 172, 16285);
        var sender = new RecordingSender();
        GameEventService service = Make(sender, start, stop);

        service.Initialize(new HashSet<ushort>());
        Assert.Equal([(Scourge, start)], sender.Calls);

        Assert.True(service.StopEvent(Scourge, overwrite: true));
        Assert.Equal([(Scourge, start), (-Scourge, stop)], sender.Calls);
    }

    [Fact]
    public void ASenderThatThrows_DoesNotStopTheStart()
    {
        var sender = new ThrowingSender();
        GameEventService service = Make(new RecordingSender(), new GameEventMailRecord(Scourge, 255, 0, 171, 16285));
        service.SetMailSender(sender);

        service.Initialize(new HashSet<ushort>());

        Assert.True(service.IsActiveEvent(Scourge));
        Assert.Equal(1, sender.Calls);
    }

    private sealed class ThrowingSender : IGameEventMailSender
    {
        public int Calls { get; private set; }

        public void Send(int signedEvent, GameEventMailRecord mail)
        {
            Calls++;
            throw new InvalidOperationException("sink down");
        }
    }

    [Fact]
    public void TheDelivery_MailsOnlyTheOnlineCharactersOfTheRowsRaces()
    {
        Player human = MakePlayer(1, 1);
        Player orc = MakePlayer(2, 2);
        Player troll = MakePlayer(3, 8);
        var mail = new RecordingMail();
        var delivery = new GameEventMailDelivery(() => [human, orc, troll], () => mail, (_, _) => true, NullLogger.Instance);

        // Race mask 1 << (race - 1): 1 is Human, 128 is Troll.
        delivery.Send(Scourge, new GameEventMailRecord(Scourge, 1 | 128, 0, 171, 16285));

        Assert.Equal([human, troll], mail.Calls.Select(c => c.Receiver).ToArray());
        Assert.All(mail.Calls, c => Assert.Equal((16285u, 171u, 0u, 0u), (c.Sender, c.Template, c.Money, c.Delay)));
    }

    [Fact]
    public void TheDelivery_WithAQuest_MailsOnlyThoseWhoFinishedIt()
    {
        Player done = MakePlayer(1, 1);
        Player notDone = MakePlayer(2, 1);
        var mail = new RecordingMail();
        var delivery = new GameEventMailDelivery(() => [done, notDone], () => mail, (p, quest) => quest == 8000 && ReferenceEquals(p, done), NullLogger.Instance);

        delivery.Send(Scourge, new GameEventMailRecord(Scourge, 255, 8000, 1, 2));

        Assert.Equal([done], mail.Calls.Select(c => c.Receiver).ToArray());
    }

    [Fact]
    public void TheDelivery_WithoutAMailService_SendsNothingAndDoesNotThrow()
    {
        var delivery = new GameEventMailDelivery(() => [MakePlayer(1, 1)], () => null, (_, _) => true, NullLogger.Instance);

        delivery.Send(Scourge, new GameEventMailRecord(Scourge, 255, 0, 171, 16285));
        delivery.Send(Scourge, new GameEventMailRecord(Scourge, 255, 0, 171, 16285));
    }

    [Fact]
    public void StartingEvent17_ThroughTheService_MailsTheMatchingCharactersOnce_AndAResumeDoesNotMailAgain()
    {
        Player human = MakePlayer(1, 1);
        Player orc = MakePlayer(2, 2);
        var mail = new RecordingMail();
        var delivery = new GameEventMailDelivery(() => [human, orc], () => mail, (_, _) => true, NullLogger.Instance);
        var row = new GameEventMailRecord(Scourge, 1, 0, 171, 16285);
        GameEventService service = Make(new RecordingSender(), row);
        service.SetMailSender(delivery);

        service.Initialize(new HashSet<ushort>());
        service.Update();
        Assert.Equal([human], mail.Calls.Select(c => c.Receiver).ToArray());

        GameEventService afterRestart = Make(new RecordingSender(), row);
        afterRestart.SetMailSender(delivery);
        afterRestart.Initialize(new HashSet<ushort> { Scourge });

        Assert.Single(mail.Calls);
    }
}
