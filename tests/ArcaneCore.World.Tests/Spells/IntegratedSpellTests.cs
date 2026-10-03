using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class IntegratedSpellTests
{
    [Fact]
    public async Task LethalSpellDamage_UsesCombatDeathState_AndReportsActualDamage()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient caster = await host.EnterWorldAsync("SPELLKILLER", "Spellkiller");
        await using WorldTestClient target = await host.EnterWorldAsync("SPELLVICTIM", "Spellvictim");

        await host.OnWorldAsync(() =>
        {
            Player attacker = host.World.FindOnlinePlayer("Spellkiller")!;
            Player victim = host.World.FindOnlinePlayer("Spellvictim")!;
            victim.Health = 3;
            SpellFeature spells = Feature(attacker);
            Assert.Equal(3u, spells.System.Damage.DealSpellDamage(attacker, victim,
                spells.System.Store.Get(SpellTestServices.Bolt)!, 7, periodic: false));
            Assert.Equal(0u, victim.Health);
            Assert.Equal(DeathState.JustDied, victim.Combat.DeathState);
            Assert.True(victim.IsRooted);
        });
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Spellvictim")!.Combat.DeathState == DeathState.Corpse,
            "spell victim becomes a corpse through the combat tick");
    }

    [Fact]
    public async Task CreatureTarget_SpellDamageAddsThreat_AndLethalSpellStartsCreatureDeath()
    {
        var template = new CreatureTemplate
        {
            Entry = 299, Name = "Spell Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903],
            Faction = 32, MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        var spawn = new CreatureSpawn { Guid = 4242, Entry = 299, MapId = 0, X = -8940, Y = -132, Z = 83.5f };
        var context = new CreatureTestContext(new CreatureContent([template], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("SPELLWOLF", "Spellwolf"))
        await using (WorldTestClient healer = await host.EnterWorldAsync("SPELLHEALWOLF", "Healwolf"))
        {
            await host.OnWorldAsync(() =>
            {
                Player caster = host.World.FindOnlinePlayer("Spellwolf")!;
                ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, 299, 4242);
                Creature wolf = context.Feature!.FindSystem(0)!.FindCreature(guid)!;
                SpellFeature spells = Feature(caster);
                Assert.Same(wolf, spells.System.Units.Find(caster, guid));
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(caster, SpellTestServices.Bolt,
                    SpellCastTargets.ForUnit(guid), triggered: true));
                Assert.Equal(48u, wolf.Health);
                Assert.Equal(7f, wolf.Combat.Threat.GetThreat(caster));
                Assert.True(wolf.Combat.IsInCombat);
                Player healingPlayer = host.World.FindOnlinePlayer("Healwolf")!;
                caster.Health = caster.MaxHealth - 10;
                Assert.Equal(10u, spells.System.Damage.Heal(healingPlayer, caster,
                    spells.System.Store.Get(SpellTestServices.Heal)!, 20));
                Assert.Equal(5f, wolf.Combat.Threat.GetThreat(healingPlayer));
                Assert.True(healingPlayer.Combat.IsInCombat);
                wolf.Health = 1;
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(caster, SpellTestServices.Bolt,
                    SpellCastTargets.ForUnit(guid), triggered: true));
                Assert.Equal(0u, wolf.Health);
                Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
                Assert.Equal(DeathState.Corpse, wolf.Combat.DeathState);
            });
        }
    }

    [Fact]
    public async Task FarTeleportSpell_PreservesAurasAndCooldowns_UntilWorldportAcknowledgement()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SPELLPORT", "Spellport");
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Spellport")!;
            SpellFeature spells = Feature(player);
            var teleport = new SpellInfo
            {
                Id = 9100, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.TeleportUnits, TargetA = SpellImplicitTarget.UnitCaster,
                    TargetB = SpellImplicitTarget.LocationDatabase,
                }, new(), new()],
            };
            var root = new SpellInfo
            {
                Id = 9101, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                RecoveryTime = 60000, Duration = new SpellDuration(30000, 0, 30000),
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRoot,
                    TargetA = SpellImplicitTarget.UnitCaster,
                }, new(), new()],
            };
            spells.System.Store = new SpellStore([.. spells.System.Store.All, teleport, root], [],
                [(teleport.Id, new SpellTargetPosition(1, -441.8f, -2596f, 96f, 0))]);
            Assert.Equal(SpellCastResult.CastOk,
                spells.System.CastSpell(player, root.Id, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(SpellCastResult.CastOk,
                spells.System.CastSpell(player, SpellTestServices.Renew, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(player.IsRooted);
            Assert.Equal(SpellCastResult.CastOk,
                spells.System.CastSpell(player, teleport.Id, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(TeleportStage.FarScheduled, Teleports(player).StageOf(player));
            Assert.NotNull(player.Map);
        });
        await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending);
        await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Null(await host.PlayerStateAsync("Spellport", p => p.Map));
        Assert.Equal(TeleportStage.Far, await host.PlayerStateAsync("Spellport", p => Teleports(p).StageOf(p)));
        // Several world/spell ticks while the real client is loading the destination map.
        await client.CollectAsync(TimeSpan.FromMilliseconds(150));
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Spellport")!;
            SpellSystem spells = Feature(player).System;
            Assert.True(player.IsRooted);
            Assert.True(spells.HasAura(player, 9101));
            Assert.True(spells.HasAura(player, SpellTestServices.Renew));
            Assert.Contains(spells.GetActiveCooldowns(player), cooldown => cooldown.SpellId == 9101);
        });
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        Assert.Equal((1u, -441.8f), await host.PlayerStateAsync("Spellport", p => (p.Map!.MapId, p.X)));
        Assert.False(await host.PlayerStateAsync("Spellport", p => Teleports(p).IsBeingTeleported(p)));
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Spellport")!;
            SpellSystem spells = Feature(player).System;
            spells.CancelAura(player, SpellTestServices.Renew);
            Assert.False(spells.HasAura(player, SpellTestServices.Renew));
            // Root is a harmful aura; server removal is its valid cancellation path.
            spells.RemoveAuras(player, 9101);
            Assert.False(player.IsRooted);
            Assert.Contains(spells.GetActiveCooldowns(player), cooldown => cooldown.SpellId == 9101);
        });
    }

    [Fact]
    public async Task StartingSpells_ArePersistedBeforeCharacterCreationSucceeds()
    {
        var store = new HookSpellStore();
        await using WorldTestHost host = StartWithStore(store);
        byte[] key = await host.AddAccountAsync("SPELLCREATE");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("SPELLCREATE", key);
        await client.CreateCharacterAsync("Spellcreate");
        Account account = (await host.Accounts.FindByUsernameAsync("SPELLCREATE"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        Assert.Equal(new[] { SpellTestServices.Heal, SpellTestServices.Bolt }, await store.GetAsync(character.Id));
        Assert.Equal(0, host.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task SpellbookLoadFailure_FailsLoginBeforeEnteringWorld()
    {
        var store = new HookSpellStore();
        await using WorldTestHost host = StartWithStore(store);
        byte[] key = await host.AddAccountAsync("SPELLLOADFAIL");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("SPELLLOADFAIL", key);
        await client.CreateCharacterAsync("Spelldbfail");
        Account account = (await host.Accounts.FindByUsernameAsync("SPELLLOADFAIL"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        store.FailRead = true;
        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)character.Id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        Assert.Equal((byte)CharResult.CharLoginFailed, (await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task StartingSpellSaveFailure_ReportsCharacterCreationError()
    {
        await using WorldTestHost host = StartWithStore(new HookSpellStore { FailWrite = true });
        byte[] key = await host.AddAccountAsync("SPELLSAVEFAIL");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("SPELLSAVEFAIL", key);
        Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Spellbad"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedQueuedSpellChange_FailsRelogUntilRetryRecoversAuthoritativeCache(bool learn)
    {
        var store = new HookSpellStore();
        await using WorldTestHost host = StartWithStore(store);
        byte[] key = await host.AddAccountAsync("SPELLRETRY");
        Player player;
        SpellFeature spells;
        CharacterRecord character;
        await using (WorldTestClient initial = await host.ConnectAsync())
        {
            await initial.AuthenticateAsync("SPELLRETRY", key);
            await initial.CreateCharacterAsync("Spellretry");
            Account account = (await host.Accounts.FindByUsernameAsync("SPELLRETRY"))!;
            character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await initial.LoginAsync((ulong)character.Id);
            player = await host.PlayerAsync("Spellretry");
            spells = Feature(player);
            if (!learn)
            {
                await host.OnWorldAsync(() => Assert.True(spells.Spellbook.LearnSpell(player, SpellTestServices.Learnable)));
                await spells.Spellbook.FlushAsync();
            }

            store.FailWrite = true;
            await host.OnWorldAsync(() => Assert.True(learn
                ? spells.Spellbook.LearnSpell(player, SpellTestServices.Learnable)
                : spells.Spellbook.ForgetSpell(player, SpellTestServices.Learnable)));
            await spells.Spellbook.FlushAsync();
            Assert.Equal(learn, spells.Spellbook.HasSpell(player, SpellTestServices.Learnable));
            Assert.Equal(!learn, (await store.GetAsync(character.Id)).Contains(SpellTestServices.Learnable));
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "initial spell retry session leaves the world");
        await using WorldTestClient retry = await host.ConnectAsync();
        await retry.AuthenticateAsync("SPELLRETRY", key);
        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)character.Id);
        await retry.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        Assert.Equal((byte)CharResult.CharLoginFailed, (await retry.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
        Assert.Equal(learn, spells.Spellbook.HasSpell(player, SpellTestServices.Learnable));
        Assert.Equal(0, host.World.OnlinePlayerCount);

        store.FailWrite = false;
        await retry.LoginAsync((ulong)character.Id);
        Assert.Equal(learn, spells.Spellbook.HasSpell(player, SpellTestServices.Learnable));
        Assert.Equal(learn, (await store.GetAsync(character.Id)).Contains(SpellTestServices.Learnable));
    }

    private static SpellFeature Feature(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();

    private static TeleportService Teleports(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<TeleportFeature>().Teleports;

    private static WorldTestHost StartWithStore(HookSpellStore store)
    {
        ZIntegratedSpellTestServices.Current.Value = store;
        try { return WorldTestHost.Start(); }
        finally { ZIntegratedSpellTestServices.Current.Value = null; }
    }

    internal sealed class HookSpellStore : ICharacterSpellStore
    {
        private readonly InMemoryCharacterSpellStore _inner = new();
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => _inner.GetAllAsync(cancellationToken);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => FailRead ? throw new InvalidOperationException("spellbook unavailable") : _inner.GetAsync(characterId, cancellationToken);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
            => FailWrite ? throw new InvalidOperationException("spellbook unavailable") : _inner.AddAsync(characterId, spells, cancellationToken);

        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
            => FailWrite ? throw new InvalidOperationException("spellbook unavailable") : _inner.RemoveAsync(characterId, spell, cancellationToken);

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => _inner.DeleteCharacterAsync(characterId, cancellationToken);
    }
}

// Sort after SpellTestServices, so only these tests replace its normal in-memory store.
internal sealed class ZIntegratedSpellTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<IntegratedSpellTests.HookSpellStore?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is { } store)
        {
            services.AddSingleton<ICharacterSpellStore>(store);
        }
    }
}
