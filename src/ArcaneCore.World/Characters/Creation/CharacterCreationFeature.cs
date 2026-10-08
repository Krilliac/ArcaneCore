using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters.Creation;

/// <summary>
/// Owns the character-creation options (configuration section
/// <see cref="CharacterCreationOptions.SectionName"/>) and logs the active rule set once at start.
/// The create handler reads <see cref="Options"/> through it; a host that never configures anything
/// runs the retail defaults (vmangos CharacterHandler.cpp rules), <see cref="CharacterCreationMode.Legacy"/>
/// restores the permissive behaviour of earlier builds.
/// </summary>
public sealed class CharacterCreationFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CharacterCreationFeature> _logger;

    public CharacterCreationFeature(IServiceProvider services, ILogger<CharacterCreationFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The options in force (replaceable by tests before the first creation).</summary>
    public CharacterCreationOptions Options { get; private set; } = new();

    /// <summary>The appearance data of <see cref="CharacterCreationOptions.CharSectionsDbcPath"/>; empty (not checked) without it.</summary>
    public CharacterAppearanceCatalog Appearance { get; private set; } = CharacterAppearanceCatalog.Empty;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var options = new CharacterCreationOptions();
        _services.GetService<IConfiguration>()?.GetSection(CharacterCreationOptions.SectionName).Bind(options);
        Options = options;
        Appearance = LoadAppearance(options);

        if (options.Mode == CharacterCreationMode.Legacy)
        {
            _logger.LogWarning("CharacterCreation: Mode=Legacy, the retail creation rules are off (no disabled mask, no PvP faction rule, no NOT_PLAYABLE check, no appearance check, no start level or money)");
            return;
        }

        _logger.LogInformation(
            "CharacterCreation: retail rules (GameType {GameType}, disabled mask {Mask}, StrictPlayerNames {Strict}, MinPlayerName {Min}, start level {Level}, start money {Money})",
            options.GameType, options.CharactersCreatingDisabled, options.StrictPlayerNames, options.EffectiveMinPlayerName,
            options.StartPlayerLevel, options.StartMoney);
        if (Appearance.IsEmpty)
        {
            _logger.LogWarning(
                "CharacterCreation:CharSectionsDbcPath and CharacterFacialHairStylesDbcPath are not set: the appearance of a new character is not checked (vmangos Player::ValidateAppearance)");
        }
        else
        {
            _logger.LogInformation(
                "CharacterCreation: appearance is checked against {Sections} CharSections and {Styles} CharacterFacialHairStyles rows ({Path})",
                Appearance.SectionCount, Appearance.FacialHairStyleCount, options.CharSectionsDbcPath);
        }

        // vmangos reads CharactersPerAccount and never enforces it either (World.cpp:631); a deliberate difference, not missing data.
        _logger.LogInformation("CharacterCreation: the cross-realm CharactersPerAccount limit is not enforced (docs/areas/character-creation.md)");
    }

    /// <summary>
    /// Both appearance files or neither: one alone is a configuration mistake and refuses startup, as does a configured file that cannot be
    /// read or has another layout (fail closed, like every other optional client DBC).
    /// </summary>
    private static CharacterAppearanceCatalog LoadAppearance(CharacterCreationOptions options)
    {
        bool sections = !string.IsNullOrWhiteSpace(options.CharSectionsDbcPath);
        bool styles = !string.IsNullOrWhiteSpace(options.CharacterFacialHairStylesDbcPath);
        if (sections != styles)
        {
            throw new InvalidOperationException(
                "CharacterCreation:CharSectionsDbcPath and CharacterCreation:CharacterFacialHairStylesDbcPath must be set together (vmangos Player::ValidateAppearance reads both)");
        }

        return sections ? CharacterAppearanceDbcReader.Load(options.CharSectionsDbcPath!, options.CharacterFacialHairStylesDbcPath!) : CharacterAppearanceCatalog.Empty;
    }

    /// <summary>The effective MaxPlayerLevel (the progression options once loaded, else the vanilla 60).</summary>
    public int MaxPlayerLevel => (int)Math.Min(_services.GetService<ProgressionFeature>()?.Progression?.Options.MaxPlayerLevel ?? 60u, 255u);
}
