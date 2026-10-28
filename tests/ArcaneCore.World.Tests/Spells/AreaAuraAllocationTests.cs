using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// The party area aura pass (<c>SpellSystem.UpdateAreaAuras</c>, a paladin's Devotion Aura style source) runs for every unit holding such a
/// source on every spell tick. In the steady state (children already applied, nobody moving) it must not allocate, through the real
/// <see cref="WorldSpellGroups"/> over a <see cref="GroupManager"/>. Driven through the internal <c>UpdateAreaAurasOf</c> seam because the full
/// <c>SpellSystem.Update</c> also runs unrelated per-state work.
/// </summary>
public sealed class AreaAuraAllocationTests
{
    [Fact]
    public void UpdateAreaAuras_SteadyStateWithFourPartyMembersInRange_AllocatesNothing()
    {
        using var kit = new AreaAuraGroupKit();
        kit.Cast();
        SpellAuraHolder source = Assert.Single(kit.Spells.GetAuras(kit.Paladin));
        Assert.True(source.IsAreaSource);
        Assert.Equal(4, source.AreaChildren.Count); // precondition: the steady state really has all four children
        SpellSystem spells = kit.Spells;
        Player paladin = kit.Paladin;

        void Window() { for (int i = 0; i < 1000; i++) { spells.UpdateAreaAurasOf(paladin); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "UpdateAreaAuras, 4 members in range");
        Assert.Equal(4, source.AreaChildren.Count);
    }

    [Fact]
    public void UpdateAreaAuras_WithAMemberOutOfRange_AllocatesNothing()
    {
        using var kit = new AreaAuraGroupKit();
        kit.Players[5].Relocate(500, 0, kit.Players[5].Z, 0, 0); // one party member far away: skipped by the radius check each pass
        kit.Cast();
        SpellAuraHolder source = Assert.Single(kit.Spells.GetAuras(kit.Paladin));
        Assert.Equal(3, source.AreaChildren.Count);
        SpellSystem spells = kit.Spells;
        Player paladin = kit.Paladin;

        void Window() { for (int i = 0; i < 1000; i++) { spells.UpdateAreaAurasOf(paladin); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "UpdateAreaAuras, 3 of 4 members in range");
    }

    [Fact]
    public void UpdateAreaAuras_RealGroups_SpreadsAndRemovesChildren()
    {
        // Behaviour through the real resolver (the Game tests use a fake): leaving the group drops the child, rejoining restores it.
        using var kit = new AreaAuraGroupKit();
        kit.Cast();
        SpellAuraHolder source = Assert.Single(kit.Spells.GetAuras(kit.Paladin));
        Assert.Equal(4, source.AreaChildren.Count);
        foreach (int id in new[] { 2, 3, 4, 5 })
        {
            Assert.Single(kit.Spells.GetAuras(kit.Players[id]));
        }

        kit.Players[3].Relocate(500, 0, kit.Players[3].Z, 0, 0);
        kit.Tick();

        Assert.Empty(kit.Spells.GetAuras(kit.Players[3]));
        Assert.Equal(3, source.AreaChildren.Count);
    }
}

/// <summary>A world with a social feature (real <see cref="GroupManager"/>), five grouped players 2 yards apart, a <see cref="SpellSystem"/> on <see cref="WorldSpellGroups"/> and a Devotion-style party area aura.</summary>
internal sealed class AreaAuraGroupKit : IDisposable
{
    public const uint Devotion = 948001;

    private static readonly ulong[] NoIcons = new ulong[8];

    private readonly ServiceProvider _services;
    private readonly SocialFeature _feature;
    private uint _now = 10_000;

    public AreaAuraGroupKit()
    {
        _services = new ServiceCollection().AddSingleton<ISocialStore>(new EmptyStore()).BuildServiceProvider();
        World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var directory = new CharacterDirectory();
        for (int i = 1; i <= 5; i++)
        {
            directory.Add(new CharacterIdentity(i, i, $"P{i}", 1, 0, 1));
            var player = new Player(new CharacterRecord
            {
                Id = i, AccountId = i, Name = $"P{i}", Race = (byte)Race.Human,
                Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 60, ZoneId = 12,
                MapId = 0, X = (i - 1) * 2, Y = 0, Z = 83.5f,
            }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new QuietSession(i));
            Players[i] = player;
            World.AddPlayer(player);
        }

        World.RunTick(0);
        _feature = new SocialFeature(directory, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        _feature.Attach(World);
        GroupRestoreResult result = Groups.Restore(
        [
            new GroupRecord(1, 1, (byte)LootMethod.GroupLoot, 1, Group.DefaultLootThreshold, false, NoIcons,
                [.. Enumerable.Range(1, 5).Select(m => new GroupMemberRecord(m, 0, false))]),
        ]);
        Assert.Single(result.Restored);

        var devotion = new SpellInfo
        {
            Id = Devotion,
            Name = "Devotion Aura (test)",
            RangeIndex = SpellConstants.RangeIndexSelfOnly,
            Range = new SpellRange(0, 0),
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            Effects =
            [
                new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAreaAuraParty, AuraType = AuraType.ModResistance, MiscValue = 1,
                    BasePoints = 54, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, Radius = 30,
                },
            ],
        };
        Spells = new SpellSystem(new SpellStore([devotion], [], []), () => _now, random: new Random(1)) { MapUpdateIntervalMs = 0 };
        Spells.Groups = new WorldSpellGroups(() => Groups);
    }

    public WorldRuntime World { get; }

    public Dictionary<int, Player> Players { get; } = [];

    public Player Paladin => Players[1];

    public SpellSystem Spells { get; }

    public GroupManager Groups => _feature.Context.Groups;

    /// <summary>The paladin casts the aura and the system ticks until every child is applied.</summary>
    public void Cast()
    {
        Assert.Equal(SpellCastResult.CastOk, Spells.CastSpell(Paladin, Devotion, SpellCastTargets.ForSelf(), triggered: true));
        Tick();
        Tick();
    }

    public void Tick()
    {
        _now += 100;
        Spells.Update(100);
    }

    public void Dispose()
    {
        World.Dispose();
        _services.Dispose();
    }

    private sealed class QuietSession(int accountId) : IPlayerSession
    {
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class EmptyStore : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
