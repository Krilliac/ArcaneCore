using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The spell system end to end: real sessions over loopback, in-memory spell tables.</summary>
public sealed class SpellWorldTests
{
    [Fact]
    public async Task Login_SendsTheStartingSpells_InSmsgInitialSpells()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NOVICE", "Novice");

        Assert.Equal([Heal, Bolt], ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
    }

    [Fact]
    public async Task CastSpell_Instant_StartsCastsAndHeals()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HEALER", "Healer");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Heal));

        Assert.NotEmpty(await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart));
        Assert.Equal(new byte[] { 0x29, 0x23, 0, 0, 0 }, await client.ReadUntilAsync(WorldOpcode.SmsgCastResult));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellheallog);
    }

    [Fact]
    public async Task CastSpell_WithACastTime_LandsOnTheWorldTick_AndDamagesTheTarget()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient caster = await host.EnterWorldAsync("CASTER", "Caster");
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Target");
        await caster.CollectAsync();
        await target.CollectAsync();
        ulong targetGuid = await host.PlayerStateAsync("Target", p => p.Guid.Value);
        uint health = await host.PlayerStateAsync("Target", p => p.Health);

        await caster.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Bolt, targetGuid));

        await target.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await caster.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await target.ReadUntilAsync(WorldOpcode.SmsgSpellnonmeleedamagelog);
        Assert.Equal(health - 7, await host.PlayerStateAsync("Target", p => p.Health));
    }

    [Fact]
    public async Task CancelCast_InterruptsTheCastInProgress()
    {
        await using var host = WorldTestHost.Start(configure: o => o.TickIntervalMs = 50);
        await using WorldTestClient caster = await host.EnterWorldAsync("QUITTER", "Quitter");
        await using WorldTestClient target = await host.EnterWorldAsync("SPARED", "Spared");
        await caster.CollectAsync();
        await target.CollectAsync();
        ulong targetGuid = await host.PlayerStateAsync("Spared", p => p.Guid.Value);

        await caster.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Bolt, targetGuid));
        await caster.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await caster.SendAsync(WorldOpcode.CmsgCancelCast, BitConverter.GetBytes(Bolt));

        byte[] result = await caster.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.Interrupted, result[5]);
        Assert.DoesNotContain((await target.CollectAsync(TimeSpan.FromMilliseconds(700))).Select(p => p.Opcode), op => op == WorldOpcode.SmsgSpellnonmeleedamagelog);
    }

    [Fact]
    public async Task UnknownSpell_IsIgnored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHEATER", "Cheater");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Learnable));

        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task LearnCommand_TeachesASpell_ThatSurvivesRelogging_AndCanBeCast()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TEACHER", "Teacher", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".learn {Learnable}");
        Assert.Equal(BitConverter.GetBytes(Learnable), await gm.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell));

        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await gm.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        Account account = (await host.Accounts.FindByUsernameAsync("TEACHER"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        await gm.LoginAsync((ulong)record.Id);
        Assert.Equal([Heal, Bolt, Learnable], ParseInitialSpells(gm.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        await gm.CollectAsync();

        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Learnable));
        await gm.ReadUntilAsync(WorldOpcode.SmsgSpellGo);

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".unlearn {Learnable}");
        Assert.Equal(new byte[] { 0x2B, 0x23 }, await gm.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell));
    }

    [Fact]
    public async Task PeriodicAura_ShowsInTheAuraFields_AndCancelAuraRemovesIt()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RENEWER", "Renewer", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cast {Renew}");
        Assert.Equal(0, (await gm.ReadUntilAsync(WorldOpcode.SmsgUpdateAuraDuration))[0]); // positive slot 0
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Renewer")!.GetUInt32(UpdateFields.UnitFieldAura) == Renew, "aura field");

        await gm.SendAsync(WorldOpcode.CmsgCancelAura, BitConverter.GetBytes(Renew));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Renewer")!.GetUInt32(UpdateFields.UnitFieldAura) == 0, "aura removed");
    }

    [Fact]
    public async Task SpellbookCache_GrantsDefaultsOnce_AndWritesThroughInOrder()
    {
        var store = new InMemoryCharacterSpellStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<Data.Characters.Spells.ICharacterSpellStore>(_ => store)
            .BuildServiceProvider();
        var cache = new SpellbookCache(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        cache.Load([new Data.Characters.Spells.CharacterSpellRow { CharacterId = 5, Spell = 1 }]);
        cache.Start();
        Player fresh = NewPlayer(7);
        Player veteran = NewPlayer(5);

        Assert.True(cache.EnsureDefaults(fresh, [Heal, Bolt]));
        Assert.False(cache.EnsureDefaults(fresh, [Learnable]));
        Assert.False(cache.EnsureDefaults(veteran, [Heal]));
        Assert.True(cache.LearnSpell(fresh, Learnable));
        Assert.False(cache.LearnSpell(fresh, Learnable));
        Assert.True(cache.ForgetSpell(fresh, Heal));
        Assert.False(cache.ForgetSpell(fresh, Heal));
        await cache.FlushAsync();

        Assert.Equal([Bolt, Learnable], cache.GetSpells(fresh));
        Assert.Equal([Bolt, Learnable], await store.GetAsync(7));
        Assert.Equal([1u], cache.GetSpells(veteran));
        Assert.Empty(await store.GetAsync(5)); // loaded rows are not rewritten

        cache.DeleteCharacter(7);
        await cache.DisposeAsync();
        Assert.Empty(await store.GetAsync(7));
    }

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullSession());

    private static byte[] CastPayload(uint spell, ulong unit = 0)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        (unit == 0 ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(new ObjectGuid(unit))).Write(writer);
        return writer.ToArray();
    }

    private static List<uint> ParseInitialSpells(byte[] packet)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1));
        return [.. Enumerable.Range(0, count).Select(i => (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3 + (i * 4))))];
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}
