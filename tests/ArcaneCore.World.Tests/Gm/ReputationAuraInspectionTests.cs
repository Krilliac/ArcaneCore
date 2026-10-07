using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

public sealed class ReputationAuraInspectionTests
{
    private const uint AuraSpell = 991901;
    private const uint SelectedAuraSpell = AuraSpell + 1;

    [Fact]
    public void AurasCommandRequiresGameMaster()
    {
        Assert.Null(ArcaneCore.World.Commands.ChatCommands.CreateTable().Resolve("auras", AccountSecurity.Player));
        Assert.NotNull(ArcaneCore.World.Commands.ChatCommands.CreateTable().Resolve("auras", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task AurasCommandDeniesHigherRankSelectedTarget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AURADENY", "Auradeny", AccountSecurity.GameMaster);
        await using WorldTestClient administrator = await host.EnterWorldAsync("AURAADMIN", "Auraadmin", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await administrator.CollectAsync();

        Player target = await host.PlayerAsync("Auraadmin");
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(target.Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Auradeny")!.Selection == target.Guid, "higher-rank selection");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras");

        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AurasCommandDispatchesAgainstSelfAndSelectedPlayer_AndReflectsRemoval()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AURAGM", "Auragm", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("AURATARGET", "Auratarget");
        await gm.CollectAsync();
        await target.CollectAsync();

        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([Aura(AuraSpell), Aura(SelectedAuraSpell, SpellImplicitTarget.UnitFriend)], [], []);
            Player player = host.World.FindOnlinePlayer("Auragm")!;
            Player selected = host.World.FindOnlinePlayer("Auratarget")!;
            Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, AuraSpell, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, SelectedAuraSpell, SpellCastTargets.ForUnit(selected.Guid), triggered: true));
        });

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras");
        Assert.Contains("Active auras on Auragm: page 1, 1 shown of 1", (await gm.ReadChatAsync()).Text);
        string selfLine = (await gm.ReadChatAsync()).Text;
        Assert.Contains($"spell {AuraSpell}", selfLine);
        Assert.Contains("amount 4", selfLine);
        Assert.Contains("misc 190", selfLine);
        Assert.Contains("duration", selfLine);

        Player selectedTarget = await host.PlayerAsync("Auratarget");
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(selectedTarget.Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Auragm")!.Selection == selectedTarget.Guid, "selected target");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras");
        Assert.Contains("Active auras on Auratarget: page 1, 1 shown of 1", (await gm.ReadChatAsync()).Text);
        Assert.Contains($"spell {SelectedAuraSpell}", (await gm.ReadChatAsync()).Text);

        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(selectedTarget, SelectedAuraSpell));
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras");
        Assert.Equal("Auratarget has no active auras.", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AurasCommandBoundsHolderOutput()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AURABOUND", "Aurabound", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await host.OnWorldAsync(() =>
        {
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            SpellInfo[] spells = Enumerable.Range(0, 13).Select(i => Aura(AuraSpell + (uint)i)).ToArray();
            feature.System.Store = new SpellStore(spells, [], []);
            Player player = host.World.FindOnlinePlayer("Aurabound")!;
            foreach (SpellInfo spell in spells)
                Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, spell.Id, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(13, feature.System.GetAuras(player).Count(holder => !holder.IsRemoved));
        });

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras");
        List<string> lines = (await gm.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat)
            .Select(p => ChatMessage.Parse(p.Payload).Text).ToList();
        Assert.Contains(lines, line => line.Contains("Active auras on Aurabound: page 1, 12 shown of 13", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Additional aura holders available on page 2.", StringComparison.Ordinal));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras 2");
        Assert.Contains("Active auras on Aurabound: page 2, 1 shown of 13", (await gm.ReadChatAsync()).Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    public async Task InvalidPageIsRejectedWithoutChangingAuraState(string page)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AURAPAGE", "Aurapage", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".auras " + page);
        Assert.Contains("Syntax: .auras", (await gm.ReadChatAsync()).Text);
        Assert.Equal(0, await host.PlayerStateAsync("Aurapage", player =>
            host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(player).Count));
    }

    private static SpellInfo Aura(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster) => new()
    {
        Id = id,
        Name = $"Inspection reputation aura {id}",
        SpellVisual = 1,
        Duration = new SpellDuration(60_000, 0, 60_000),
        Effects =
        [
            new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura,
                AuraType = AuraType.ModFactionReputationGain,
                BasePoints = 3,
                BaseDice = 1,
                DieSides = 1,
                MiscValue = 190,
                TargetA = target,
            },
            new(), new(),
        ],
    };
}
