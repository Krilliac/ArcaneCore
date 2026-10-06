using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// Wave 53 coverage for the vmangos .modify rage/energy extension. These tests use the real
/// WorldTestHost, command table, player fields, socket replies, character save queue, and relog.
/// </summary>
public sealed class ModifyPowerCommandTests
{
    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    [Fact]
    public void Power_commands_are_game_master_commands_and_moderators_cannot_resolve_them()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "modify rage", "modify energy" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task Rage_UsesTheTenPointClientFactor_AndPreservesOtherPlayerState()
    {
        await using WorldTestHost host = StartPowerHost();
        await using WorldTestClient gm = await host.EnterWorldAsync("PWRGM", "Pwrgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        (uint health, uint maxHealth, PlayerFlags flags) = await host.PlayerStateAsync("Pwrgm",
            p => (p.Health, p.MaxHealth, p.Flags));

        uint[] otherPowers = await host.PlayerStateAsync("Pwrgm", p => new[]
        {
            p.GetUInt32(UpdateFields.UnitFieldPower1),
            p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
            p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Focus),
        });
        Assert.Equal($"You changed rage of {Link("Pwrgm")} to 25/50.", await Run(gm, ".modify rage 25 50"));
        Assert.Equal($"You changed rage of {Link("Pwrgm")} to 25/50.", await Run(gm, ".modify rage 25"));
        Assert.Equal((250u, 500u), await host.PlayerStateAsync("Pwrgm", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage))));
        Assert.Equal((health, maxHealth, flags), await host.PlayerStateAsync("Pwrgm",
            p => (p.Health, p.MaxHealth, p.Flags)));
        Assert.Equal(otherPowers, await host.PlayerStateAsync("Pwrgm", p => new[]
        {
            p.GetUInt32(UpdateFields.UnitFieldPower1),
            p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
            p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Focus),
        }));
    }

    [Fact]
    public async Task SelectedEnergyTarget_getsCallerAndTargetReplies_andUsesFactorOne()
    {
        await using WorldTestHost host = StartPowerHost(withRogue: true);
        await using WorldTestClient gm = await host.EnterWorldAsync("PWRSELGM", "Pwrselgm", AccountSecurity.GameMaster);
        await using WorldTestClient rogue = await EnterClassAsync(host, "PWRROGUE", "Pwrrogue", AccountSecurity.Player, cls: 4);
        await gm.CollectAsync();
        await rogue.CollectAsync();
        Player target = await host.PlayerAsync("Pwrrogue");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Pwrselgm")!.Selection = target.Guid);

        Assert.Equal($"You changed ENERGY of {Link("Pwrrogue")} to 40/80.", await Run(gm, ".modify energy 40 80"));
        Assert.Equal($"{Link("Pwrselgm")} changed your ENERGY to 40/80.", (await rogue.ReadChatAsync()).Text);
        Assert.Equal((40u, 80u), await host.PlayerStateAsync("Pwrrogue", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy))));
    }

    [Fact]
    public async Task GameMaster_cannot_modify_a_higher_security_target()
    {
        await using WorldTestHost host = StartPowerHost(withRogue: true);
        await using WorldTestClient gm = await host.EnterWorldAsync("PWRLOW", "Pwrlow", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await EnterClassAsync(host, "PWRHIGH", "Pwrhigh", AccountSecurity.Administrator, cls: 4);
        await gm.CollectAsync();
        await admin.CollectAsync();
        Player target = await host.PlayerAsync("Pwrhigh");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Pwrlow")!.Selection = target.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify energy 40 80");
        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
        Assert.Equal((100u, 100u), await host.PlayerStateAsync("Pwrhigh", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy))));
    }

    [Fact]
    public async Task Invalid_values_overflow_and_bad_targets_do_not_mutate_power()
    {
        await using WorldTestHost host = StartPowerHost();
        await using WorldTestClient gm = await host.EnterWorldAsync("PWRBAD", "Pwrbad", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Assert.Equal($"You changed rage of {Link("Pwrbad")} to 0/100.", await Run(gm, ".modify rage 0"));
        Assert.StartsWith("Syntax:", (await RunSyntax(gm, ".modify rage -1"))!);
        Assert.StartsWith("Syntax:", (await RunSyntax(gm, ".modify rage 20 nope"))!);
        Assert.StartsWith("Syntax:", (await RunSyntax(gm, ".modify rage 20 30 extra"))!);
        Assert.Equal("Incorrect values.", await Run(gm, ".modify rage 4294967295"));
        Assert.Equal($"You changed rage of {Link("Pwrbad")} to 50/100.", await Run(gm, ".modify rage 50 100"));
        Assert.Equal("Incorrect values.", await Run(gm, ".modify rage 40 30"));
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Pwrbad")!.Selection = ObjectGuid.WithEntry(HighGuid.Unit, 1, 999999));
        Assert.Equal("No character selected.", await Run(gm, ".modify rage 10"));
        Assert.Equal((500u, 1000u), await host.PlayerStateAsync("Pwrbad", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage))));
    }

    [Fact]
    public async Task CurrentEnergy_persists_through_real_logout_relog_with_class_maximum()
    {
        await using WorldTestHost host = StartPowerHost(withRogue: true);
        await using WorldTestClient rogue = await EnterClassAsync(host, "PWRPERSIST", "Pwrpersist", AccountSecurity.GameMaster, cls: 4);
        await rogue.CollectAsync();
        Assert.Equal($"You changed ENERGY of {Link("Pwrpersist")} to 37/150.", await Run(rogue, ".modify energy 37 150"));
        Account savedAccount = (await host.Accounts.FindByUsernameAsync("PWRPERSIST"))!;
        CharacterRecord savedRecord = (await host.Characters.GetByAccountAsync(savedAccount.Id)).Single();
        Assert.Equal((37u, 150u), await host.PlayerStateAsync("Pwrpersist", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy))));
        await host.SaveQueue.FlushCharacterAsync(savedRecord.Id);
        await WorldTestHost.WaitForAsync(() => host.Characters.Life(savedRecord.Id)?.Powers.ElementAtOrDefault(3) == 37,
            "current energy in the character life store");
        await rogue.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await rogue.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await host.SaveQueue.FlushCharacterAsync(savedRecord.Id);

        await rogue.LoginAsync((ulong)savedRecord.Id);
        Assert.Equal((37u, 100u), await host.PlayerStateAsync("Pwrpersist", p =>
            (p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy),
             p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy))));
    }

    private static WorldTestHost StartPowerHost(bool withRogue = false)
        => WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Combat:RateEnergy"] = "0",
                    ["Combat:RateRageLoss"] = "0",
                }).Build());
            if (withRogue)
            {
                services.AddSingleton<IWorldDataStore, EnergyWorldDataStore>();
            }
        });

    private static async Task<string?> Run(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    private static async Task<string?> RunSyntax(WorldTestClient client, string command)
    {
        string? first = await Run(client, command);
        await client.CollectAsync();
        return first;
    }

    private static async Task<WorldTestClient> EnterClassAsync(WorldTestHost host, string account,
        string character, AccountSecurity security, byte cls)
    {
        byte[] key = await host.AddAccountAsync(account, security);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character, cls: cls);
        Account row = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(row.Id)).Single();
        await client.LoginAsync((ulong)record.Id);
        return client;
    }

    private sealed class EnergyWorldDataStore : IWorldDataStore
    {
        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<StartPosition?>(race == 1 && cls is 1 or 4 ? new StartPosition(0, 12, -8949.95f, -132.493f, 83.5312f, 0f) : null);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
            => Task.FromResult<RaceInfo?>(race == 1 ? new RaceInfo(gender == 0 ? 49u : 50u, 1) : null);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<ClassInfo?>(cls switch
            {
                1 => new ClassInfo(60, 0, (byte)PowerType.Rage),
                4 => new ClassInfo(60, 0, (byte)PowerType.Energy),
                _ => null,
            });

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult(race == 1 && cls is 1 or 4);
    }
}
