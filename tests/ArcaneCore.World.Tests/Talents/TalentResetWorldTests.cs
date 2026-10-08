using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Talents.TalentWorldFixture;

namespace ArcaneCore.World.Tests.Talents;

/// <summary>
/// The wipe of a feigning player (vmangos SkillHandler.cpp:46-48) and the reset-at-login request (vmangos
/// CHARACTER_FLAG_RESET_TALENTS_ON_LOGIN: applied for free and cleared at login, CharacterHandler.cpp:661-665; cleared by any reset,
/// Player.cpp:4077-4078), end to end over loopback with the synthetic catalog of <see cref="TalentWorldFixture"/>.
/// </summary>
public sealed class TalentResetWorldTests
{
    private const uint Gold = 10000;
    private static readonly ObjectGuid Trainer = QuestInteractionFixture.Guid;

    internal static WorldTestHost Start(TalentWorldFixture fixture)
    {
        TalentTestServices.Current.Value = fixture;
        QuestInteractionTestServices.Current.Value = new QuestInteractionFixture();
        try
        {
            // A short logout delay: the relogs here are of ordinary players, who wait out the logout timer (GMs leave at once).
            return WorldTestHost.Start(configure: o => o.LogoutDelayMs = 20);
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    internal static async Task<(WorldTestClient Client, CharacterRecord Record)> EnterAsync(
        WorldTestHost host, string name, byte level = 12, uint money = 0, AccountSecurity security = AccountSecurity.Player)
    {
        byte[] key = await host.AddAccountAsync(name, security);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(name, key);
        await client.CreateCharacterAsync(name);
        Account account = (await host.Accounts.FindByUsernameAsync(name))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        record.Level = level;
        record.Money = money;
        await client.LoginAsync((ulong)record.Id);
        return (client, record);
    }

    internal static async Task RelogAsync(WorldTestHost host, WorldTestClient client, CharacterRecord record)
    {
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(record.Name) is null, "the character leaves the world");
        await client.LoginAsync((ulong)record.Id);
    }

    internal static byte[] LearnTalent(uint talent, uint rank) => [.. BitConverter.GetBytes(talent), .. BitConverter.GetBytes(rank)];

    internal static uint FreePoints(WorldTestHost host, string name)
        => host.World.FindOnlinePlayer(name)!.GetUInt32(UpdateFields.PlayerCharacterPoints1);

    internal static bool Knows(WorldTestHost host, string name, uint spell)
        => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.HasSpell(host.World.FindOnlinePlayer(name)!, spell);

    private static SpellSystem Spells(WorldTestHost host) => host.WorldServices.GetRequiredService<SpellFeature>().System;

    private static async Task MakeTrainerAsync(WorldTestHost host, string name)
    {
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(Trainer), "the trainer becomes visible");
        await host.OnWorldAsync(() => ((Creature)host.World.FindOnlinePlayer(name)!.Map!.FindObject(Trainer)!).NpcFlags
            = (uint)(NpcFlags.Gossip | NpcFlags.Trainer));
    }

    [Fact]
    public async Task Wipe_WhileFeigningDeath_EndsTheFeign_ThenResets()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _) = await EnterAsync(host, "FEIGNER", money: 10 * Gold);
        await using WorldTestClient clientScope = client;
        await MakeTrainerAsync(host, "FEIGNER");
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        Assert.True(await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("FEIGNER")!;
            Spells(host).CastSpell(player, FeignDeath, SpellCastTargets.ForSelf(), triggered: true);
            return Spells(host).IsFeigningDeath(player);
        }));
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, Guid(Trainer));

        await client.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell);
        Assert.False(await host.OnWorldAsync(() => Spells(host).IsFeigningDeath(host.World.FindOnlinePlayer("FEIGNER")!)));
        Assert.False(await host.OnWorldAsync(() => Spells(host).HasAura(host.World.FindOnlinePlayer("FEIGNER")!, FeignDeath)));
        Assert.False(await host.OnWorldAsync(() => Knows(host, "FEIGNER", T1R1)));
    }

    [Fact]
    public async Task Wipe_AtSomethingThatIsNotATrainer_LeavesTheFeign()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        (WorldTestClient client, _) = await EnterAsync(host, "STILLFEIGN", money: 10 * Gold);
        await using WorldTestClient clientScope = client;
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("STILLFEIGN")!.VisibleObjects.Contains(Trainer), "the creature becomes visible");
        await host.OnWorldAsync(() =>
            Spells(host).CastSpell(host.World.FindOnlinePlayer("STILLFEIGN")!, FeignDeath, SpellCastTargets.ForSelf(), triggered: true));
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.MsgTalentWipeConfirm, Guid(Trainer));   // only a quest giver: refused before the feign check
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 0));      // a later packet: the wipe was handled before it
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);

        Assert.True(await host.OnWorldAsync(() => Spells(host).IsFeigningDeath(host.World.FindOnlinePlayer("STILLFEIGN")!)));
    }

    [Fact]
    public async Task ARequestedReset_IsAppliedForFreeAtLogin_Cleared_AndAnnounced()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record) = await EnterAsync(host, "FLAGGED", money: 10 * Gold);
        await using WorldTestClient clientScope = client;
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 2));   // three points: rank 3 of talent 1
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        Assert.Equal(0u, await host.OnWorldAsync(() => FreePoints(host, "FLAGGED")));
        fixture.ResetFlags.Flag(record.Id);

        await RelogAsync(host, client, record);

        Assert.Equal(ChatPackets.BuildNotification(TalentFeature.TalentsResetText), await client.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.False(await host.OnWorldAsync(() => Knows(host, "FLAGGED", T1R3)));
        Assert.Equal(3u, await host.OnWorldAsync(() => FreePoints(host, "FLAGGED")));
        Assert.Equal(10 * Gold, await host.PlayerStateAsync("FLAGGED", p => p.Money));            // free
        Assert.Equal(0u, await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<TalentFeature>().Service!.StateOf(host.World.FindOnlinePlayer("FLAGGED")!).Respec.Multiplier));
        await host.WaitForWorldAsync(() => !fixture.ResetFlags.IsFlagged(record.Id), "the request is cleared");

        // The request was used up: the next login keeps a newly learned talent.
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await RelogAsync(host, client, record);
        Assert.True(await host.OnWorldAsync(() => Knows(host, "FLAGGED", T1R1)));
    }

    [Fact]
    public async Task WithoutARequest_TheLoginKeepsTheTalents_AndSendsNoNotification()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record) = await EnterAsync(host, "UNFLAGGED");
        await using WorldTestClient clientScope = client;
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);

        await RelogAsync(host, client, record);
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, LearnTalent(1, 1));   // a reply that follows the whole login sequence

        Assert.DoesNotContain(await ReadThroughAsync(client, WorldOpcode.SmsgLearnedSpell), op => op == WorldOpcode.SmsgNotification);
        Assert.True(await host.OnWorldAsync(() => Knows(host, "UNFLAGGED", T1R2)));
    }

    [Fact]
    public async Task AnUnreadableRequestStore_FailsTheLogin_InsteadOfSkippingTheReset()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        (WorldTestClient client, CharacterRecord record) = await EnterAsync(host, "STOREDOWN");
        await using WorldTestClient clientScope = client;
        fixture.ResetFlags.Flag(record.Id);
        fixture.ResetFlags.FailReads = true;
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(record.Name) is null, "the character leaves the world");

        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, BitConverter.GetBytes((ulong)record.Id));

        await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.True(fixture.ResetFlags.IsFlagged(record.Id));
    }

    private static byte[] Guid(ObjectGuid guid) => BitConverter.GetBytes(guid.Value);

    /// <summary>The opcodes received up to and including the first <paramref name="last"/>.</summary>
    private static async Task<List<WorldOpcode>> ReadThroughAsync(WorldTestClient client, WorldOpcode last)
    {
        var seen = new List<WorldOpcode>();
        while (true)
        {
            (WorldOpcode opcode, _) = await client.ReadAsync();
            seen.Add(opcode);
            if (opcode == last)
            {
                return seen;
            }
        }
    }
}
