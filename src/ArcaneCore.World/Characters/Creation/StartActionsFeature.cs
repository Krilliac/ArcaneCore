using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters.Creation;

/// <summary>
/// Gives a new character its starting action bar (vmangos <c>MasterPlayer::Create</c> adds every
/// <c>playercreateinfo_action</c> row of the race and class, MasterPlayer.cpp:30-39; the rows were
/// checked at load by <c>Player::IsActionButtonDataValid</c>, ObjectMgr.cpp:4783). The bar is stored
/// with the character, so the first login sends it in SMSG_ACTION_BUTTONS. A host without a
/// <see cref="IStartActionSource"/> (no world database) leaves the bar empty.
/// </summary>
public sealed class StartActionsFeature : IWorldFeature, ICharacterHooks
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StartActionsFeature> _logger;

    public StartActionsFeature(IServiceProvider services, ILogger<StartActionsFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Attach(ArcaneCore.Game.Maps.WorldRuntime world)
    {
        // Nothing to attach: the bar is written from the creation hook.
    }

    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        CharacterCreationOptions options = _services.GetService<CharacterCreationFeature>()?.Options ?? new CharacterCreationOptions();
        if (options.Mode == CharacterCreationMode.Legacy || !options.StartActions
            || session.Services.GetService<IStartActionSource>() is not { } source)
        {
            return;
        }

        IReadOnlyList<ActionButton> rows = await source.GetAsync(character.Race, character.Class).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        IItemTemplateStore? items = null;
        if (_services.GetService<ItemsFeature>() is { } itemsFeature)
        {
            items = await itemsFeature.EnsureLoadedAsync().ConfigureAwait(false);
        }

        var spells = _services.GetService<SpellFeature>()?.System.Store;
        (IReadOnlyList<ActionButton> valid, int dropped) = Filter(
            rows,
            spells is { Count: > 0 } ? id => spells.Get(id) is not null : null,
            items is { Count: > 0 } ? id => items.Find(id) is not null : null);
        if (dropped > 0)
        {
            _logger.LogWarning("playercreateinfo_action: race {Race} class {Class}: {Dropped} row(s) name a spell or item that does not exist and are not given",
                character.Race, character.Class, dropped);
        }

        if (valid.Count == 0)
        {
            return;
        }

        // A state with only the buttons set: the other columns are written back as they are.
        await session.Services.GetRequiredService<ICharacterStore>().SaveStateAsync(new CharacterState(
            character.Id, character.MapId, character.ZoneId, character.X, character.Y, character.Z, character.Orientation,
            character.Level, character.PlayedTime, character.LevelPlayedTime, character.Money, character.ActionBarToggles,
            ActionButtons: valid)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Player::IsActionButtonDataValid</c> with no player (Player.cpp:5900-5933): the button and
    /// action ranges, a spell action must name an existing spell, an item action an existing item;
    /// other types (macros) are not checked. A null existence test means that content is not loaded,
    /// so that kind of row is kept rather than judged against nothing.
    /// </summary>
    public static (IReadOnlyList<ActionButton> Valid, int Dropped) Filter(
        IReadOnlyList<ActionButton> rows, Func<uint, bool>? spellExists, Func<uint, bool>? itemExists)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var valid = new List<ActionButton>(rows.Count);
        foreach (ActionButton row in rows)
        {
            bool ok = StartActionRules.IsInRange(row.Button, row.Action)
                && row.Type switch
                {
                    StartActionRules.TypeSpell => spellExists is null || spellExists(row.Action),
                    StartActionRules.TypeItem => itemExists is null || itemExists(row.Action),
                    _ => true,
                };
            if (ok)
            {
                valid.Add(row);
            }
        }

        return (valid, rows.Count - valid.Count);
    }
}
