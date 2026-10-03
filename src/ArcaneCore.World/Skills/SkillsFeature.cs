using System.Collections.Frozen;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Skills;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Skills;

/// <summary>
/// The skill system in the world daemon (docs/areas/skills.md): the skill content, each player's
/// <see cref="PlayerSkills"/>, the spell effects that grant skills and proficiencies, persistence of the skill
/// tables, level-ups, the skill-unlearn opcode and the GM commands. Handlers reach it through
/// <c>session.Services.GetRequiredService&lt;SkillsFeature&gt;()</c>.
/// <para>
/// Load order follows vmangos Player::LoadFromDB: the spellbook is loaded first by <see cref="SpellFeature"/>,
/// which then calls this feature as an <see cref="ISpellbookLoadObserver"/> (still on the session task, before the
/// player reaches the world thread): the free profession slots are reset, the stored skills are loaded (and the
/// spells they grant learned), then every known spell is run through the learn path so the skills it grants
/// exist and a profession's first rank spends its slot.
/// </para>
/// <para>
/// <see cref="SkillsMode.Legacy"/> (or Retail without any DBC file configured, which logs an error) leaves
/// <see cref="Player.Skills"/> null and every stand-in untouched.
/// </para>
/// </summary>
public sealed class SkillsFeature : IWorldFeature, ISpellbookLoadObserver, ICharacterDeleteHook, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SkillsFeature> _logger;
    private readonly HashSet<Map> _maps = [];
    private FrozenDictionary<uint, (int ItemClass, int SubClassMask)> _proficiencies = FrozenDictionary<uint, (int, int)>.Empty;
    private WorldRuntime? _world;
    private SpellFeature? _spells;
    private PlayerProgression? _progression;

    public SkillsFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<SkillsFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Saves = new SkillSaveCoordinator(scopes, logger);
    }

    public SkillsFeatureOptions Options { get; } = new();

    /// <summary>The Game-layer tuning in force (valid once <see cref="IsActive"/>).</summary>
    public SkillOptions SkillOptions { get; private set; } = new();

    /// <summary>The skill content; <see cref="SkillCatalog.Empty"/> while inactive.</summary>
    public SkillCatalog Catalog { get; private set; } = SkillCatalog.Empty;

    /// <summary>Whether players carry real skills (Retail with content). False leaves every legacy stand-in in place.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The snapshot writer.</summary>
    public SkillSaveCoordinator Saves { get; }

    /// <summary>How long a login or a deletion waits for an earlier write of the same character (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; set; } = CharacterDeletion.DrainTimeout;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _services.GetService<IConfiguration>()?.GetSection(SkillsFeatureOptions.SectionName).Bind(Options);
        if (Options.Mode == SkillsMode.Legacy)
        {
            _logger.LogWarning("Skills: Mode=Legacy, players have no skills (every skill reads 300, nobody can dual wield)");
            return;
        }

        IReadOnlyList<SpellTemplateRow> spellRows = ReadSpellRows();
        SkillCatalog? catalog = _services.GetService<SkillCatalog>() ?? LoadCatalog(spellRows);
        if (catalog is null)
        {
            _logger.LogError(
                "Skills: no skill DBC files are configured (Skills:SkillLineDbcPath, SkillRaceClassInfoDbcPath, SkillTiersDbcPath, SkillLineAbilityDbcPath); "
                + "running with the legacy stand-ins: every skill reads 300, trainers see no skill, nobody can dual wield");
            return;
        }

        _spells = _services.GetRequiredService<SpellFeature>();
        _progression = _services.GetService<ProgressionFeature>()?.Progression;
        SkillOptions = Options.ToSkillOptions(_progression?.Options.MaxPlayerLevel ?? 60);
        Catalog = catalog;
        _proficiencies = ReadProficiencies(spellRows);
        IsActive = true;

        RegisterEffects(_spells.System);
        _spells.Spellbook.SpellLearned += OnSpellLearned;
        _spells.Spellbook.SpellForgotten += OnSpellForgotten;
        Saves.Start();
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.MapCreated += OnMapCreated;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        if (_progression is not null)
        {
            _progression.LevelChanged += OnLevelChanged;
        }

        _logger.LogInformation(
            "Skills: {Lines} skill lines, {Abilities} spell abilities, {Grants} spells that grant a skill, {Proficiencies} proficiency spells",
            Catalog.LineCount, Catalog.Lines.Sum(l => Catalog.AbilitiesOfSkill(l.Id).Count), Catalog.LearnSkills.Count, _proficiencies.Count);
    }

    /// <summary>
    /// Rebuild a loading player's skills from its spellbook (vmangos Player::LoadFromDB, Player.cpp:14730-14977).
    /// Waits for an earlier write of the same character first, so a quick relog never reads stale rows.
    /// </summary>
    public async Task OnSpellbookLoadedAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (!IsActive || _spells is null)
        {
            return;
        }

        await Saves.FlushCharacterAsync(character.Id, DrainTimeout).ConfigureAwait(false);
        CharacterSkillSnapshot stored;
        await using (AsyncServiceScope scope = _scopes.CreateAsyncScope())
        {
            stored = scope.ServiceProvider.GetService<ICharacterSkillStore>() is { } store
                ? await store.LoadAsync(character.Id).ConfigureAwait(false)
                : new CharacterSkillSnapshot([], []);
        }

        var host = new SkillSpellHost(_spells, player);
        var skills = new PlayerSkills(player, Catalog, SkillOptions, host, _services.GetService<ArcaneCore.Game.Combat.ICombatRandom>());
        player.AttachSkills(skills);
        player.Inventory.Requirements = new PlayerItemRequirements(player.Inventory.Requirements);

        // The book as stored, taken before the skills teach anything: each of these goes through the learn path once.
        IReadOnlyList<uint> book = _spells.Spellbook.GetSpells(player);
        host.NotYetLoaded.UnionWith(book);
        skills.InitPrimaryProfessions();
        skills.Load(stored.Skills, stored.Forgotten);
        foreach (uint spellId in book)
        {
            host.NotYetLoaded.Remove(spellId);
            skills.OnSpellLearned(spellId);
        }
    }

    /// <summary>
    /// CMSG_UNLEARN_SKILL (vmangos HandleUnlearnSkillOpcode, SkillHandler.cpp:59-70): only skills whose
    /// SkillRaceClassInfo row for the player's race and class carries the UNLEARNABLE flag may be dropped.
    /// </summary>
    public bool TryUnlearnSkill(Player player, uint skillId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Skills is not { } skills)
        {
            return false;
        }

        SkillRaceClassInfoRecord? raceClass = Catalog.RaceClassInfo(skillId, (byte)player.Race, (byte)player.Class);
        if (raceClass is null || (raceClass.Flags & SkillRaceClassFlags.Unlearnable) == 0)
        {
            _logger.LogWarning("{Player} tried to unlearn skill {Skill}, which cannot be unlearned", player.Name, skillId);
            return false;
        }

        return skills.Set(skillId, 0, 0);
    }

    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (IsActive)
        {
            await Saves.FlushCharacterAsync(character.Id, DrainTimeout).ConfigureAwait(false);
        }
    }

    public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        Saves.Forget(character.Id);
        return Task.CompletedTask;
    }

    /// <summary>The world has stopped producing updates: write whatever is still dirty, then drain the writer.</summary>
    public async Task StopAsync()
    {
        if (_world is { } world && IsActive)
        {
            foreach (Player player in world.OnlinePlayers)
            {
                EnqueueSnapshot(player);
            }

            await Saves.FlushAllAsync(DrainTimeout).ConfigureAwait(false);
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is { } world)
        {
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
            world.MapCreated -= OnMapCreated;
        }

        if (_spells is { } spells)
        {
            spells.Spellbook.SpellLearned -= OnSpellLearned;
            spells.Spellbook.SpellForgotten -= OnSpellForgotten;
        }

        if (_progression is not null)
        {
            _progression.LevelChanged -= OnLevelChanged;
        }

        await Saves.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Queue a changed player's skill snapshot (world thread; nothing happens when nothing changed).</summary>
    internal void EnqueueSnapshot(Player player)
    {
        if (player.Skills is { } skills && skills.TakeSnapshotIfChanged() is { } snapshot)
        {
            Saves.Enqueue(SpellbookCache.CharacterId(player), snapshot);
        }
    }

    private void OnSpellLearned(Player player, uint spellId) => player.Skills?.OnSpellLearned(spellId);

    private void OnSpellForgotten(Player player, uint spellId) => player.Skills?.OnSpellForgotten(spellId);

    private void OnLevelChanged(Player player) => player.Skills?.UpdateSkillsForLevel();

    private void OnPlayerLoggingOut(Player player) => EnqueueSnapshot(player);

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new SkillPersistenceUpdater(this, (uint)Math.Max(1, Options.FlushDebounceMs)));
        }
    }

    private IReadOnlyList<SpellTemplateRow> ReadSpellRows()
    {
        using IServiceScope scope = _scopes.CreateScope();
        return scope.ServiceProvider.GetService<ISpellContentStore>() is { } content
            ? content.LoadAsync().GetAwaiter().GetResult().Spells
            : [];
    }

    private SkillCatalog? LoadCatalog(IReadOnlyList<SpellTemplateRow> spellRows)
    {
        string?[] paths = Options.DbcPaths;
        if (paths.All(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        if (paths.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Skills: SkillLineDbcPath, SkillRaceClassInfoDbcPath, SkillTiersDbcPath and SkillLineAbilityDbcPath must all be set (or none)");
        }

        // A configured file that is unreadable or has another layout refuses startup (fail closed).
        HashSet<uint> spellIds = [.. spellRows.Select(r => r.Id)];
        return SkillDbcReaders.Load(
            paths[0]!, paths[1]!, paths[2]!, paths[3]!,
            SpellLearnSkillTable.Build(SkillEffects(spellRows)),
            spellIds.Count == 0 ? null : spellIds.Contains);
    }

    /// <summary>Every SPELL_EFFECT_SKILL (118) effect of every spell (vmangos LoadSpellLearnSkills, SpellMgr.cpp:1850-1887).</summary>
    internal static IEnumerable<SpellSkillEffect> SkillEffects(IEnumerable<SpellTemplateRow> rows)
    {
        foreach (SpellTemplateRow row in rows)
        {
            if (row.Effect1 == (uint)SpellEffectName.Skill)
            {
                yield return new SpellSkillEffect(row.Id, 0, row.EffectMiscValue1, row.EffectBasePoints1, unchecked((int)row.EffectBaseDice1));
            }

            if (row.Effect2 == (uint)SpellEffectName.Skill)
            {
                yield return new SpellSkillEffect(row.Id, 1, row.EffectMiscValue2, row.EffectBasePoints2, unchecked((int)row.EffectBaseDice2));
            }

            if (row.Effect3 == (uint)SpellEffectName.Skill)
            {
                yield return new SpellSkillEffect(row.Id, 2, row.EffectMiscValue3, row.EffectBasePoints3, unchecked((int)row.EffectBaseDice3));
            }
        }
    }

    private static FrozenDictionary<uint, (int ItemClass, int SubClassMask)> ReadProficiencies(IEnumerable<SpellTemplateRow> rows)
        => rows
            .Where(r => r.Effect1 == (uint)SpellEffectName.Proficiency || r.Effect2 == (uint)SpellEffectName.Proficiency || r.Effect3 == (uint)SpellEffectName.Proficiency)
            .ToFrozenDictionary(r => r.Id, r => (r.EquippedItemClass, r.EquippedItemSubClassMask));

    /// <summary>
    /// The effect handlers (vmangos SpellEffects.cpp table, lines 80-178): SKILL, TRADE_SKILL, WEAPON, DEFENSE,
    /// DODGE and SPELL_DEFENSE do nothing by themselves (the skills they name come from the learn path),
    /// SKILL_STEP is EffectLearnSkill, PROFICIENCY, LANGUAGE, DUAL_WIELD, PARRY and BLOCK set player state.
    /// </summary>
    private void RegisterEffects(SpellSystem system)
    {
        foreach (SpellEffectName effect in new[]
        {
            SpellEffectName.Skill, SpellEffectName.TradeSkill, SpellEffectName.Weapon, SpellEffectName.Defense,
            SpellEffectName.Dodge, SpellEffectName.SpellDefense,
        })
        {
            system.RegisterEffect(effect, static _ => { });
        }

        system.RegisterEffect(SpellEffectName.SkillStep, EffectLearnSkill);
        system.RegisterEffect(SpellEffectName.Proficiency, EffectProficiency);
        system.RegisterEffect(SpellEffectName.Language, static context =>
        {
            if (context.Target is Player { Skills: { } skills })
            {
                skills.LearnLanguage((uint)context.Effect.MiscValue);
            }
        });
        system.RegisterEffect(SpellEffectName.DualWield, static context =>
        {
            if (context.Target is Player { Skills: { } skills })
            {
                skills.CanDualWield = true;
            }
        });
        system.RegisterEffect(SpellEffectName.Parry, static context =>
        {
            if (context.Target is Player { Skills: { } skills })
            {
                skills.CanParry = true;
            }
        });
        system.RegisterEffect(SpellEffectName.Block, static context =>
        {
            if (context.Target is Player { Skills: { } skills })
            {
                skills.CanBlock = true;
            }
        });
    }

    /// <summary>vmangos Spell::EffectLearnSkill (SpellEffects.cpp:2959-2977): value = the step, maximum = step * 75.</summary>
    private static void EffectLearnSkill(SpellEffectContext context)
    {
        if (context.Target is not Player { Skills: { } skills } || context.Value < 0)
        {
            return;
        }

        ushort skillId = unchecked((ushort)context.Effect.MiscValue);
        ushort step = unchecked((ushort)context.Value);
        ushort current = Math.Max((ushort)1, skills.GetValuePure(skillId));
        skills.Set(skillId, current, unchecked((ushort)(step * 75)), step);
    }

    /// <summary>vmangos Spell::EffectProficiency (SpellEffects.cpp:2304-2321): grow the mask and tell the client.</summary>
    private void EffectProficiency(SpellEffectContext context)
    {
        if (context.Target is not Player { Skills: { } skills } player
            || !_proficiencies.TryGetValue(context.Spell.Id, out (int ItemClass, int SubClassMask) proficiency))
        {
            return;
        }

        var itemClass = (ItemClass)proficiency.ItemClass;
        if (skills.AddProficiency(itemClass, unchecked((uint)proficiency.SubClassMask)) is { } mask)
        {
            player.Session.Send(WorldOpcode.SmsgSetProficiency, SkillPackets.SetProficiency(itemClass, mask));
        }
    }
}
