using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Game;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Talents;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Talents;

/// <summary>
/// The talent system in the world daemon (discovered <see cref="IWorldFeature"/>): CMSG_LEARN_TALENT and the trainer wipe
/// flow, free points kept current at login and on every level change, the respec economy and the disabled-spell set
/// persisted, and a startup report of the talent effects the spell system cannot apply yet. Section "Talents"
/// (<see cref="TalentOptions"/>); without Talent.dbc and TalentTab.dbc paths the feature is inert and says so
/// (docs/areas/talents.md).
/// <para>
/// Features attach in full-name order, so the spell, NPC and progression features it hooks are already built.
/// </para>
/// </summary>
public sealed partial class TalentFeature : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    /// <summary>"Untalent Visual Effect": the trainer casts it on the player after a wipe (vmangos SkillHandler.cpp:57, mangos-classic :63).</summary>
    public const uint UntalentVisualSpell = 14867;

    private readonly IServiceProvider _services;
    private readonly ILogger<TalentFeature> _logger;
    private WorldRuntime? _world;
    private SpellFeature? _spells;
    private QuestNpcFeature? _npcs;
    private PlayerProgression? _progression;
    private readonly IServiceScopeFactory? _scopes;

    public TalentFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<TalentFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopes = scopes;
        Persistence = new TalentPersistence(scopes, logger);
    }

    public TalentOptions Options { get; } = new();

    public TalentPersistence Persistence { get; }

    /// <summary>The world-thread talent service; null while the feature is inert (no Talent.dbc configured).</summary>
    public TalentService? Service { get; private set; }

    public TalentCatalog? Catalog { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the talent feature is already attached");
        }

        _world = world;
        _services.GetService<IConfiguration>()?.GetSection(TalentOptions.Section).Bind(Options);
        Catalog = _services.GetService<TalentCatalog>() ?? LoadCatalog();
        if (Catalog is null)
        {
            _logger.LogWarning(
                "Talents: {Section}:TalentDbcPath and {Section}:TalentTabDbcPath are not set; the talent system is inert (no talent points, " +
                "CMSG_LEARN_TALENT is ignored, the unlearn option stays hidden)", TalentOptions.Section, TalentOptions.Section);
            return;
        }

        _spells = _services.GetRequiredService<SpellFeature>();
        IRankChain? chain = BuildRankChain();
        Service = new TalentService(Catalog, _spells.System, Options, logger: _logger)
        {
            Sink = new PersistenceSink(this),
            RankChain = chain,
            KnownSpells = player => _spells.Spellbook.GetSpells(player),
        };
        Persistence.Start();
        Service.ResetAttempted += OnResetAttempted;
        world.PlayerLoggedIn += OnPlayerLoggedIn;

        if (_services.GetService<ProgressionFeature>() is { } progression)
        {
            _progression = progression.Progression;
            _progression.LevelChanged += OnLevelChanged;
        }

        if (_services.GetService<QuestNpcFeature>() is { } npcs)
        {
            _npcs = npcs;
            npcs.Services.UnlearnTalentsOffered = TalentTrainerRules.CanTrainAndResetTalentsOf;
            npcs.Services.ForeignOptionSelected += OnForeignOption;
        }

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, _spells.System);
        _logger.LogInformation("Loaded {Talents} talents in {Tabs} tabs. {Report}", Catalog.TalentCount, Catalog.TabCount, report.Describe());
        _logger.LogInformation("{ModReport}", Game.Spells.Mods.TalentModCoverage.Build(Catalog, _spells.System).Describe());
        if (chain is null)
        {
            _logger.LogWarning("Talents: no SkillLineAbility.dbc is available, so trainer-learned higher ranks of talent abilities are not disabled by a respec");
        }
    }

    /// <summary>
    /// Player::LoadFromDB for talents, on the session task before the player is visible: wait for earlier writes, restore the
    /// respec economy and the disabled set, drop disabled spells and superseded ranks from the book, and settle the free
    /// points (an overspend resets, like vmangos InitTalentForLevel after _LoadSpells, Player.cpp:14980). It runs after the
    /// spell feature's hook (full-name order), so the book is loaded. A pending reset-at-login request is read here and applied
    /// once the player is in the world (<see cref="ReadLoginResetAsync"/>). A storage error fails the login.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (Service is not { } service)
        {
            return;
        }

        (CharacterTalentState? stored, IReadOnlyList<uint> disabled) = await Persistence
            .LoadCharacterAsync(character.Id, session.Services.GetService<ICharacterTalentStore>()).ConfigureAwait(false);
        service.LoadState(player, stored is null ? default : new RespecState(stored.ResetMultiplier, stored.ResetTimeUnix), disabled);
        service.RemoveDisabledFromBook(player);
        service.RemoveSupersededRanks(player);
        service.InitTalentForLevel(player);
        await ReadLoginResetAsync(session, character, player).ConfigureAwait(false);
    }

    /// <summary>
    /// MSG_TALENT_WIPE_CONFIRM from the client (vmangos HandleTalentWipeConfirmOpcode, SkillHandler.cpp:39-58): the NPC must be
    /// an interactable trainer (and, by default, a class trainer of the player's class); a feigning player stops feigning
    /// (vmangos removes the feign-death auras right after the trainer check, :46-48); a refused reset sends the empty
    /// confirmation, a successful one has the trainer cast the visual spell on the player. World thread.
    /// </summary>
    public void OnWipeConfirm(Player player, ObjectGuid trainer)
    {
        if (Service is not { } service || _npcs is null)
        {
            return;
        }

        NpcInfo? npc = _npcs.Services.InteractableNpc(player, trainer, NpcFlags.Trainer);
        if (npc is null)
        {
            _logger.LogDebug("{Player} confirmed a talent wipe at {Trainer}, which is not an interactable trainer", player.Name, trainer);
            return;
        }

        // vmangos: HasUnitState(UNIT_STATE_FEIGN_DEATH) -> RemoveSpellsCausingAura(SPELL_AURA_FEIGN_DEATH).
        if (_spells!.System.IsFeigningDeath(player))
        {
            _spells.System.BreakFeignDeath(player);
        }

        if (Options.RequireClassTrainerForWipe && !TalentTrainerRules.CanTrainAndResetTalentsOf(player, npc))
        {
            _logger.LogDebug("{Player} confirmed a talent wipe at {Trainer}, which is not a trainer of their class", player.Name, trainer);
            return;
        }

        if (!service.ResetTalents(player, noCost: false))
        {
            if (Options.WipeRefusalAlsoSendsEmptyConfirm)
            {
                service.SendEmptyWipeConfirm(player);
            }

            return;
        }

        Unit caster = player.Map?.FindObject(trainer) as Unit ?? player;
        _spells!.System.CastSpell(caster, UntalentVisualSpell, SpellCastTargets.ForUnit(player.Guid), triggered: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is not null)
        {
            _world.PlayerLoggedIn -= OnPlayerLoggedIn;
        }

        if (Service is not null)
        {
            Service.ResetAttempted -= OnResetAttempted;
        }

        if (_progression is not null)
        {
            _progression.LevelChanged -= OnLevelChanged;
        }

        if (_npcs is not null)
        {
            _npcs.Services.ForeignOptionSelected -= OnForeignOption;
        }

        Service?.Dispose();
        await Persistence.DisposeAsync().ConfigureAwait(false);
    }

    public Task StopAsync() => DisposeAsync().AsTask();

    private TalentCatalog? LoadCatalog()
    {
        bool talents = !string.IsNullOrWhiteSpace(Options.TalentDbcPath);
        bool tabs = !string.IsNullOrWhiteSpace(Options.TalentTabDbcPath);
        if (!talents && !tabs)
        {
            return null;
        }

        if (talents != tabs)
        {
            throw new InvalidOperationException($"{TalentOptions.Section}:TalentDbcPath and {TalentOptions.Section}:TalentTabDbcPath must be set together");
        }

        try
        {
            return TalentDbcReaders.Load(Options.TalentDbcPath!, Options.TalentTabDbcPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            // Fail closed, but name the settings: the reader's own message only says which file it could not read.
            throw new InvalidOperationException(
                $"{TalentOptions.Section}:TalentDbcPath ('{Options.TalentDbcPath}') or {TalentOptions.Section}:TalentTabDbcPath " +
                $"('{Options.TalentTabDbcPath}') could not be read: {ex.Message}. Fix the paths, or unset both to run without talents.", ex);
        }
    }

    private IRankChain? BuildRankChain()
    {
        SkillLineAbilityCatalog? abilities = _services.GetService<SkillLineAbilityCatalog>();
        if (abilities is null && _services.GetService<NpcServicesFeature>()?.Options.SkillLineAbilityDbcPath is { Length: > 0 } path)
        {
            abilities = NpcServiceDbcReaders.LoadSkillLineAbilities(path);
        }

        return abilities is null ? null : new SkillLineRankChain(abilities);
    }

    private void OnLevelChanged(Player player) => Service?.InitTalentForLevel(player);

    private void OnForeignOption(Player player, NpcInfo npc, GossipOption option)
    {
        if (option == GossipOption.UnlearnTalents)
        {
            Service?.SendWipeConfirm(player, npc.Guid);
        }
    }

    private static int CharacterId(Player player) => SpellbookCache.CharacterId(player);

    private sealed class PersistenceSink(TalentFeature feature) : ITalentSink
    {
        public void RespecChanged(Player player, RespecState state) => feature.Persistence.SaveRespec(CharacterId(player), state);

        public void DisabledChanged(Player player, uint spellId, bool disabled) => feature.Persistence.SetDisabled(CharacterId(player), spellId, disabled);

        public void CharacterChanged(Player player) => feature._world?.SavePlayer(player);
    }
}
