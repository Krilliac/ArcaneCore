using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// Say, yell and emote share <see cref="ArcaneCore.Game.Maps.Map.BroadcastInRange"/>, which follows vmangos
/// MessageDistDeliverer (GridNotifiers.cpp): the listener is in range when their camera body is
/// (Camera::GetBody — far sight, mind vision, possession), not when their feet are.
/// </summary>
public sealed class SayCameraRangeTests
{
    [Fact]
    public async Task Say_IsHeardWhereTheCameraIs_NotWhereTheBodyStands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        await using WorldTestClient ear = await host.EnterWorldAsync("EAR", "Ear");
        Player speakerPlayer = await host.PlayerAsync("Speaker");
        await host.PlaceAsync("Ear", speakerPlayer.X + 80, speakerPlayer.Y, speakerPlayer.Z); // outside the 25-yard say range
        await ear.CollectAsync();
        await speaker.CollectAsync();

        await host.OnWorldAsync(() =>
        {
            Player mouth = host.World.FindOnlinePlayer("Speaker")!;
            Player listener = host.World.FindOnlinePlayer("Ear")!;
            Creature eye = EyeAt(mouth, mouth.X, mouth.Y, mouth.Z);
            mouth.Map!.AddObject(eye);
            UnitControl.SightWatchers.AddOrUpdate(eye, listener);
            CharmService.SetView(listener, eye);
        });
        await ear.CollectAsync();

        await speaker.SendChatAsync(ChatType.Say, Language.Common, "beside the eye");
        Assert.Equal("beside the eye", (await ear.ReadChatAsync()).Text);

        await host.OnWorldAsync(() => CharmService.SetView(host.World.FindOnlinePlayer("Ear")!, null));
        await ear.CollectAsync();
        await speaker.CollectAsync(); // the speaker hears its own first say too
        await speaker.SendChatAsync(ChatType.Say, Language.Common, "feet are still far");
        Assert.Equal("feet are still far", (await speaker.ReadChatAsync()).Text);
        Assert.DoesNotContain(await ear.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task Say_BesideTheSpeaker_IsNotHeardWhileTheCameraLooksElsewhere()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        await using WorldTestClient ear = await host.EnterWorldAsync("EAR", "Ear");
        await ear.CollectAsync();
        await speaker.CollectAsync();

        await host.OnWorldAsync(() =>
        {
            Player mouth = host.World.FindOnlinePlayer("Speaker")!;
            Player listener = host.World.FindOnlinePlayer("Ear")!;
            Creature eye = EyeAt(mouth, mouth.X + 80, mouth.Y, mouth.Z);
            mouth.Map!.AddObject(eye);
            UnitControl.SightWatchers.AddOrUpdate(eye, listener);
            CharmService.SetView(listener, eye);
        });
        await ear.CollectAsync();

        await speaker.SendChatAsync(ChatType.Say, Language.Common, "you are looking away");
        Assert.Equal("you are looking away", (await speaker.ReadChatAsync()).Text);
        Assert.DoesNotContain(await ear.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        await host.OnWorldAsync(() => CharmService.SetView(host.World.FindOnlinePlayer("Ear")!, null));
        await ear.CollectAsync();
        await speaker.SendChatAsync(ChatType.Say, Language.Common, "looking back");
        Assert.Equal("looking back", (await ear.ReadChatAsync()).Text);
    }

    private static Creature EyeAt(Player speaker, float x, float y, float z)
    {
        var template = new CreatureTemplate
        {
            Entry = 990610, Name = "Eye", MinLevel = 1, MaxLevel = 1, MinLevelHealth = 1, MaxLevelHealth = 1, Faction = 35,
        };
        var spawn = new CreatureSpawn { Guid = 990610, Entry = 990610, MapId = speaker.MapId, X = x, Y = y, Z = z };
        return new Creature(990610, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
    }
}
