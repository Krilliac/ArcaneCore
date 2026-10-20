using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>The party AI fights players under the one hostility rule (<see cref="ArcaneCore.World.Playerbots.Combat.PlayerbotHostility"/>) and sticks to the leader's raid marks.</summary>
public sealed class PlayerbotPartyPvpTests
{
    private sealed record Rig(WorldTestHost Host, WorldTestClient Master, WorldSession Bot, WorldSession Enemy, PlayerbotPartyAI Ai)
    {
        public Player MasterPlayer => Host.World.FindOnlinePlayer("Pvppartym")!;

        public Player BotPlayer => Bot.Player!;

        public Player EnemyPlayer => Enemy.Player!;

        public GroupManager Groups => Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;

        /// <summary>One think of the bot: a fresh budget, the intake and the decision.</summary>
        public void Think()
        {
            Bot.ManagedBudget = new ManagedActionBudget(8);
            Ai.Intake(BotPlayer);
            Ai.Update(BotPlayer, 500);
        }

        /// <summary>The master whispers a command to the bot; the next think reads it.</summary>
        public void Tell(string text)
            => Bot.Send(WorldOpcode.SmsgMessagechat, global::ArcaneCore.World.Packets.ChatPackets.BuildMessage(ChatType.Whisper, Language.Common, MasterPlayer.Guid, text, ChatTag.None));
    }

    private static async Task Run(Func<Rig, Task> body)
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        WorldSession bot = await PartyTestHost.EnterManagedAsync(host, "PVPPARTYBOT", "Pvppartybot");
        await using WorldTestClient master = await host.EnterWorldAsync("PVPPARTYM", "Pvppartym");
        WorldSession enemy = await PlayerbotPvpTestKit.EnterAsync(host, "PVPPARTYE", "Pvppartye", PlayerbotPvpTestKit.Orc);
        try
        {
            var ai = new PlayerbotPartyAI(bot, PartyTestHost.PlayerbotOptionsOf(host));
            var rig = new Rig(host, master, bot, enemy, ai);
            await host.OnWorldAsync(() =>
            {
                Player b = bot.Player!, m = rig.MasterPlayer, e = enemy.Player!;
                Assert.Same(b.Map, e.Map);
                e.Relocate(b.X + 4, b.Y, b.Z, MathF.PI, host.World.NowMs);
                rig.Groups.Invite(m, b.Name);
                rig.Groups.Accept(b);
                Assert.True(ai.Drives(b));
                bot.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store = new SpellStore([], [], []);
                return true;
            });
            await body(rig);
        }
        finally
        {
            bot.Kick();
            enemy.Kick();
            await bot.ManagedClosed;
            await enemy.ManagedClosed;
        }
    }

    [Fact]
    public Task AttackOrderOnFlaggedEnemyPlayerIsObeyed() => Run(async rig =>
    {
        ulong botGuid = 0;
        await rig.Host.OnWorldAsync(() =>
        {
            PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
            rig.MasterPlayer.Selection = rig.EnemyPlayer.Guid;
            botGuid = rig.BotPlayer.Guid.Value;
            rig.Tell("attack");
            rig.Think();
            rig.Think();
            Assert.Same(rig.EnemyPlayer, rig.Ai.InspectionUnit);
        });
        Assert.Equal("Attacking Pvppartye.", (await PartyTestHost.ReadWhisperFromAsync(rig.Master, botGuid)).Text);
    });

    [Fact]
    public Task AttackOrderOnUnflaggedEnemyPlayerIsRefused() => Run(async rig =>
    {
        ulong botGuid = 0;
        await rig.Host.OnWorldAsync(() =>
        {
            rig.MasterPlayer.Selection = rig.EnemyPlayer.Guid;
            botGuid = rig.BotPlayer.Guid.Value;
            rig.Tell("attack");
            rig.Think();
            rig.Think();
            Assert.Null(rig.Ai.InspectionUnit);
        });
        Assert.Equal("I have no target to attack.", (await PartyTestHost.ReadWhisperFromAsync(rig.Master, botGuid)).Text);
    });

    [Fact]
    public Task BotAssistsMasterAgainstPlayerMasterAttacks() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.MasterPlayer.Combat.Victim = rig.EnemyPlayer;
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.InspectionUnit);
    }));

    [Fact]
    public Task BotDoesNotAssistMasterAgainstUnflaggedPlayer() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        rig.MasterPlayer.Combat.Victim = rig.EnemyPlayer;
        rig.Think();
        Assert.Null(rig.Ai.InspectionUnit);
    }));

    [Fact]
    public Task BotDefendsMemberAttackedByFlaggedPlayer() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(rig.MasterPlayer);
        Assert.True(rig.MasterPlayer.Map!.Combat.Attack(rig.EnemyPlayer, rig.MasterPlayer));
        rig.MasterPlayer.Map!.Combat.DealDamage(rig.EnemyPlayer, rig.MasterPlayer, 1);
        Assert.Contains(rig.EnemyPlayer, rig.MasterPlayer.Combat.Attackers);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.InspectionUnit);
    }));

    [Fact]
    public async Task SkullMarkedTargetBeatsMasterVictim()
    {
        await Run(async rig =>
        {
            Creature victim = null!, skull = null!;
            await rig.Host.OnWorldAsync(() =>
            {
                Player bot = rig.BotPlayer;
                var template = new CreatureTemplate
                {
                    Entry = 990_411, Name = "dummy", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500,
                };
                var system = new CreatureMapSystem(bot.Map!, new CreatureContent([template], [], [], [], []), random: new Random(1));
                victim = system.SpawnTemporary(template, bot.X - 3, bot.Y, bot.Z, 0f);
                skull = system.SpawnTemporary(template, bot.X, bot.Y + 3, bot.Z, 0f);
                rig.MasterPlayer.Combat.Victim = victim;
                return true;
            });
            var packet = new PacketWriter();
            packet.WriteByte(7);
            packet.WriteUInt64(skull.Guid.Value);
            await rig.Master.SendAsync(WorldOpcode.MsgRaidTargetUpdate, packet.ToArray());
            await rig.Host.WaitForWorldAsync(() => rig.Groups.GetGroup(rig.MasterPlayer.Guid)?.TargetIcons[7] == skull.Guid, "the skull is set");
            await rig.Host.OnWorldAsync(() =>
            {
                rig.Think();
                Assert.Same(skull, rig.Ai.InspectionTarget);
            });
        });
    }
}
