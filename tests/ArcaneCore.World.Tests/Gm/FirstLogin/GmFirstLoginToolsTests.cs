using System.Buffers.Binary;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.FirstLogin;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Gm.FirstLogin;

/// <summary>
/// First-login policy tests. Lifecycle tests use the real WorldTestHost, SpellFeature, SpellbookCache, and
/// ICharacterSpellStore; the content fixture only adds the exact build-5875 row needed here.
/// </summary>
public sealed class GmFirstLoginToolsTests
{
    private const uint DetectInvisibility = 2970;
    private static readonly uint[] ExplicitGameMasterSet =
        [13, 26, 47, 1557, 1908, 10032, 18209, 18210, 18800, 23452];
    private static readonly uint[] ExplicitAdministratorSet =
        [260, 265, 530, 2650, 2653, 2654, 5259, 5696, 9454, 23775, 24199,
         27204, 29607, 31366, 1509, 18139, 2763, 6147, 28432];

    [Fact]
    public void Disabled_policy_does_not_bind_unused_invalid_tool_settings()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "false",
            [$"{GmFirstLoginToolsOptions.SectionName}:MinimumSecurity"] = "unused-invalid-role",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = "unused-invalid-id",
        });
        Assert.False(GmFirstLoginToolsOptions.Bind(config).Enabled);
        Assert.Empty(new GmFirstLoginToolsConfigChecks().Check(config));
    }

    [Fact]
    public void Enabled_invalid_role_is_reported_by_pre_host_config_check()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:MinimumSecurity"] = nameof(AccountSecurity.Player),
        });
        var issue = Assert.Single(new GmFirstLoginToolsConfigChecks().Check(config));
        Assert.Equal(GmFirstLoginToolsOptions.SectionName, issue.Key);
        Assert.Equal(ArcaneCore.Kernel.Configuration.Validation.ConfigSeverity.Error, issue.Severity);
    }

    [Fact]
    public async Task Disabled_unused_invalid_settings_do_not_break_real_login_or_commands()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "false",
            [$"{GmFirstLoginToolsOptions.SectionName}:MinimumSecurity"] = "unused-invalid-role",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = "unused-invalid-id",
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(config));
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_OFF", "GmTOff", AccountSecurity.GameMaster);
        Assert.Equal([Heal, Bolt], await host.PlayerStateAsync("GmTOff",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p)));
        await client.SendChatAsync(ChatType.Say, Language.Common, ".lookup spell definitely-not-a-spell");
        Assert.Equal("No spells found!", (await client.ReadChatAsync()).Text);
    }

    [Fact]
    public void Defaults_are_disabled_and_game_master_gated()
    {
        var options = new GmFirstLoginToolsOptions();
        Assert.False(options.Enabled);
        Assert.Equal(AccountSecurity.GameMaster, options.MinimumSecurity);
        Assert.True(options.IncludeDeveloperSpells);
        Assert.True(options.ShowToolGuide);
        Assert.Empty(options.ExcludedSpellIds);
    }

    [Fact]
    public void Options_reject_zero_and_duplicate_explicit_ids_before_catalog_selection()
    {
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [0] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [DetectInvisibility, DetectInvisibility] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GmFirstLoginToolsOptions { Enabled = true, ExcludedSpellIds = [0] }.Validate());
    }

    [Fact]
    public async Task Disabled_player_login_preserves_real_starting_book_and_store()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_PLAYER", "GmTPlayer", AccountSecurity.Player);

        IReadOnlyList<uint> book = await host.PlayerStateAsync("GmTPlayer",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p));
        Assert.Equal([Heal, Bolt], book);
        Assert.Equal([Heal, Bolt], await Store(host).GetAsync(CharacterId(host, "GMTOOLS_PLAYER")));
        Assert.DoesNotContain(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        Assert.False(await host.PlayerStateAsync("GmTPlayer", p => p.IsGameMaster));
    }

    [Fact]
    public async Task Enabled_first_login_uses_real_observer_book_store_and_initial_packet()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:MinimumSecurity"] = nameof(AccountSecurity.GameMaster),
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
        });

        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith((DetectInvisibility, "Detect Invisibility"))));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_GM", "GmTGM", AccountSecurity.GameMaster);

        int characterId = CharacterId(host, "GMTOOLS_GM");
        IReadOnlyList<uint> book = await host.PlayerStateAsync("GmTGM",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p));
        IReadOnlyList<uint> stored = await Store(host).GetAsync(characterId);

        Assert.Equal([DetectInvisibility, Heal, Bolt], book);
        Assert.Equal(book, stored);
        Assert.Contains(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        Assert.False(await host.PlayerStateAsync("GmTGM", p => p.IsGameMaster));
        Assert.Equal(AccountSecurity.GameMaster, await host.PlayerStateAsync("GmTGM", p => p.Security));
        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task Enabled_policy_does_not_grant_an_ordinary_player()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith((DetectInvisibility, "Detect Invisibility"))));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_PLAIN", "GmTPlain", AccountSecurity.Player);

        Assert.Equal([Heal, Bolt], await host.PlayerStateAsync("GmTPlain",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p)));
        Assert.DoesNotContain(DetectInvisibility, await Store(host).GetAsync(CharacterId(host, "GMTOOLS_PLAIN")));
        Assert.DoesNotContain(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
    }

    [Fact]
    public async Task Played_character_unlearns_are_not_regranted_on_relog()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith((DetectInvisibility, "Detect Invisibility"))));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_RELOG", "GmTRelog", AccountSecurity.GameMaster);
        Assert.Contains(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));

        await client.SendChatAsync(ChatType.Say, Language.Common, $".unlearn {DetectInvisibility}");
        Assert.Equal(BitConverter.GetBytes((ushort)DetectInvisibility), await client.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell));
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        Account account = (await host.Accounts.FindByUsernameAsync("GMTOOLS_RELOG"))!;
        int characterId = (await host.Characters.GetByAccountAsync(account.Id)).Single().Id;
        await host.SaveQueue.FlushCharacterAsync(characterId);
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        Assert.True(record.PlayedTime > 0);

        await client.LoginAsync((ulong)record.Id);
        Assert.DoesNotContain(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        Assert.DoesNotContain(DetectInvisibility, await Store(host).GetAsync(record.Id));
    }

    [Fact]
    public async Task Excluded_id_is_not_learned_and_normal_book_remains_present()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
            [$"{GmFirstLoginToolsOptions.SectionName}:ExcludedSpellIds:0"] = DetectInvisibility.ToString(),
        });
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith((DetectInvisibility, "Detect Invisibility"))));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("GMTOOLS_EXCLUDED", "GmTExcl", AccountSecurity.GameMaster);

        Assert.Equal([Heal, Bolt], await host.PlayerStateAsync("GmTExcl",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p)));
        Assert.DoesNotContain(DetectInvisibility, ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
    }

    [Fact]
    public void Catalog_expected_sets_are_explicit_and_do_not_expand_adjacent_ranks()
    {
        Assert.Equal(10, ExplicitGameMasterSet.Length);
        Assert.Equal(19, ExplicitAdministratorSet.Length);
        Assert.DoesNotContain(1558u, ExplicitGameMasterSet);
        Assert.DoesNotContain(29608u, ExplicitAdministratorSet);
    }

    [Fact]
    public void Catalog_selection_applies_security_to_two_real_entries()
    {
        SpellStore store = new(
            [new SpellInfo { Id = DetectInvisibility, Name = "Detect Invisibility" },
             new SpellInfo { Id = 9454, Name = "Freeze" }], [], []);
        var options = new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [DetectInvisibility, 9454] };

        Assert.Equal([DetectInvisibility], GmFirstLoginToolCatalog.Select(options, AccountSecurity.GameMaster, store).Select(s => s.Id));
        Assert.Equal([DetectInvisibility, 9454], GmFirstLoginToolCatalog.Select(options, AccountSecurity.Administrator, store).Select(s => s.Id));
    }

    [Fact]
    public void Catalog_rejects_missing_or_name_mismatched_content_before_feature_can_write()
    {
        var options = new GmFirstLoginToolsOptions { Enabled = true, SpellIds = [DetectInvisibility] };
        Assert.Throws<InvalidOperationException>(() => GmFirstLoginToolCatalog.Select(options, AccountSecurity.GameMaster,
            new SpellStore([], [], [])));
        Assert.Throws<InvalidOperationException>(() => GmFirstLoginToolCatalog.Select(options, AccountSecurity.GameMaster,
            new SpellStore([new SpellInfo { Id = DetectInvisibility, Name = "Wrong name" }], [], [])));
        Assert.Throws<InvalidOperationException>(() => GmFirstLoginToolCatalog.Select(options, AccountSecurity.GameMaster,
            new SpellStore([new SpellInfo { Id = DetectInvisibility, Name = "Detect Invisibility", Attributes = SpellAttributes.Passive }], [], [])));
    }

    [Fact]
    public async Task Flush_failure_fails_login_then_retries_final_book_once()
    {
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
        });
        var store = new FailingGrantStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith((DetectInvisibility, "Detect Invisibility"))));
            services.AddSingleton<ICharacterSpellStore>(store);
        });
        host.ExpectSessionFaults = true;

        byte[] key = await host.AddAccountAsync("GMTOOLS_FAIL", AccountSecurity.GameMaster);
        await using WorldTestClient failed = await host.ConnectAsync();
        await failed.AuthenticateAsync("GMTOOLS_FAIL", key);
        await failed.CreateCharacterAsync("GmTFail");
        CharacterRecord record = (await host.Characters.GetByAccountAsync(
            (await host.Accounts.FindByUsernameAsync("GMTOOLS_FAIL"))!.Id)).Single();
        await AssertFailedLoginAsync(failed, (ulong)record.Id);
        Assert.DoesNotContain(DetectInvisibility, await store.GetAsync(record.Id));

        store.FailGrant = false;
        await using WorldTestClient retry = await host.ConnectAsync();
        await retry.AuthenticateAsync("GMTOOLS_FAIL", key);
        await retry.LoginAsync((ulong)record.Id);
        IReadOnlyList<uint> persisted = await store.GetAsync(record.Id);
        Assert.Equal(1, persisted.Count(id => id == DetectInvisibility));
        Assert.Equal([DetectInvisibility, Heal, Bolt], await host.PlayerStateAsync("GmTFail",
            p => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.GetSpells(p)));
    }

    [Fact]
    public async Task Partial_grant_failure_recovers_the_complete_admin_snapshot_without_duplicates()
    {
        const uint freeze = 9454;
        IConfiguration config = Configuration(new Dictionary<string, string?>
        {
            [$"{GmFirstLoginToolsOptions.SectionName}:Enabled"] = "true",
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:0"] = DetectInvisibility.ToString(),
            [$"{GmFirstLoginToolsOptions.SectionName}:SpellIds:1"] = freeze.ToString(),
        });
        var store = new FailingGrantStore(freeze);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(config);
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(ContentWith(
                (DetectInvisibility, "Detect Invisibility"), (freeze, "Freeze"))));
            services.AddSingleton<ICharacterSpellStore>(store);
        });
        host.ExpectSessionFaults = true;

        byte[] key = await host.AddAccountAsync("GMTOOLS_PARTIAL", AccountSecurity.Administrator);
        await using WorldTestClient failed = await host.ConnectAsync();
        await failed.AuthenticateAsync("GMTOOLS_PARTIAL", key);
        await failed.CreateCharacterAsync("GmTPart");
        CharacterRecord record = (await host.Characters.GetByAccountAsync(
            (await host.Accounts.FindByUsernameAsync("GMTOOLS_PARTIAL"))!.Id)).Single();
        await AssertFailedLoginAsync(failed, (ulong)record.Id);

        IReadOnlyList<uint> partial = await store.GetAsync(record.Id);
        Assert.Contains(DetectInvisibility, partial);
        Assert.DoesNotContain(freeze, partial);

        store.FailGrant = false;
        await using WorldTestClient retry = await host.ConnectAsync();
        await retry.AuthenticateAsync("GMTOOLS_PARTIAL", key);
        await retry.LoginAsync((ulong)record.Id);
        IReadOnlyList<uint> recovered = await store.GetAsync(record.Id);
        Assert.Equal([DetectInvisibility, Heal, Bolt, freeze], recovered);
        Assert.Equal(recovered, ParseInitialSpells(retry.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        Assert.Equal(recovered.Count, recovered.Distinct().Count());
        Assert.False(await host.PlayerStateAsync("GmTPart", p => p.IsGameMaster));
        Assert.DoesNotContain((await retry.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    private static async Task AssertFailedLoginAsync(WorldTestClient client, ulong character)
    {
        var login = new PacketWriter(8);
        login.WriteUInt64(character);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        var (opcode, payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharacterLoginFailed, opcode);
        Assert.Equal(new byte[] { (byte)CharResult.CharLoginFailed }, payload);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ICharacterSpellStore Store(WorldTestHost host) =>
        host.WorldServices.GetRequiredService<ICharacterSpellStore>();

    private static int CharacterId(WorldTestHost host, string account)
    {
        Account row = host.Accounts.FindByUsernameAsync(account).GetAwaiter().GetResult()!;
        return host.Characters.GetByAccountAsync(row.Id).GetAwaiter().GetResult().Single().Id;
    }

    private static SpellContent ContentWith(params (uint Id, string Name)[] additions)
    {
        SpellContent baseContent = Content();
        SpellTemplateRow[] rows = [.. additions.Select(addition => new SpellTemplateRow
        {
            Id = addition.Id, SpellName = addition.Name, RangeIndex = 1, Effect1 = 10, EffectBasePoints1 = 4,
            EffectDieSides1 = 1, EffectImplicitTargetA1 = 1, StartRecoveryCategory = 133, StartRecoveryTime = 1500,
        })];
        return baseContent with { Spells = [.. baseContent.Spells, .. rows] };
    }

    private static List<uint> ParseInitialSpells(byte[] packet)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1));
        return [.. Enumerable.Range(0, count).Select(i => (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3 + (i * 4))))];
    }

    /// A failure wrapper around the existing real in-memory character-spell store. It only
    /// fails the feature's selected grant, leaving character creation and normal rows intact.
    private sealed class FailingGrantStore(uint failSpell = DetectInvisibility) : ICharacterSpellStore
    {
        private readonly InMemoryCharacterSpellStore _inner = new();
        private readonly uint _failSpell = failSpell;
        public bool FailGrant { get; set; } = true;

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default) => _inner.GetAllAsync(cancellationToken);
        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetAsync(characterId, cancellationToken);
        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => _inner.RemoveAsync(characterId, spell, cancellationToken);
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => _inner.DeleteCharacterAsync(characterId, cancellationToken);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
            => FailGrant && spells.Contains(_failSpell)
                ? Task.FromException(new IOException("selected grant storage unavailable"))
                : _inner.AddAsync(characterId, spells, cancellationToken);
    }
}
