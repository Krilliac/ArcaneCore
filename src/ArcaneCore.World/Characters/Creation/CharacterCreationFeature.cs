using ArcaneCore.Game.Maps;
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

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var options = new CharacterCreationOptions();
        _services.GetService<IConfiguration>()?.GetSection(CharacterCreationOptions.SectionName).Bind(options);
        Options = options;

        if (options.Mode == CharacterCreationMode.Legacy)
        {
            _logger.LogWarning("CharacterCreation: Mode=Legacy, the retail creation rules are off (no disabled mask, no PvP faction rule, no NOT_PLAYABLE check, no start level or money)");
            return;
        }

        _logger.LogInformation(
            "CharacterCreation: retail rules (GameType {GameType}, disabled mask {Mask}, StrictPlayerNames {Strict}, MinPlayerName {Min}, start level {Level}, start money {Money})",
            options.GameType, options.CharactersCreatingDisabled, options.StrictPlayerNames, options.EffectiveMinPlayerName,
            options.StartPlayerLevel, options.StartMoney);
        _logger.LogWarning(
            "CharacterCreation: appearance (CharSections.dbc), reserved and profane name lists, the cross-realm account limit and the starting outfit/action bar are not enforced by this build (docs/areas/character-creation.md)");
    }

    /// <summary>The effective MaxPlayerLevel (the progression options once loaded, else the vanilla 60).</summary>
    public int MaxPlayerLevel => (int)Math.Min(_services.GetService<ProgressionFeature>()?.Progression?.Options.MaxPlayerLevel ?? 60u, 255u);
}
