using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.FirstLogin;

/// <summary>
/// Adds the reviewed build-5875 GM/developer spellbook entries to an explicitly enabled,
/// never-played staff character. This feature only changes the loading spellbook; it never
/// enters GM mode, grants account permissions, or casts a spell.
/// </summary>
public sealed class GmFirstLoginToolsFeature : IWorldFeature, ISpellbookLoadObserver
{
    private const int ClientBuild = 5875;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GmFirstLoginToolsFeature> _logger;
    private readonly ConditionalWeakTable<Player, GrantState> _grants = [];
    private WorldRuntime? _world;
    private GmFirstLoginToolsOptions _options = new();
    private string _revision = "disabled";

    public GmFirstLoginToolsFeature(IConfiguration configuration, ILogger<GmFirstLoginToolsFeature> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GmFirstLoginToolsOptions Options => _options;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _options = GmFirstLoginToolsOptions.Bind(_configuration);
        _options.Validate();
        _revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{_options.Enabled}|{(byte)_options.MinimumSecurity}|{_options.IncludeDeveloperSpells}|{_options.ShowToolGuide}|"
            + string.Join(',', _options.SpellIds.Order()) + "|" + string.Join(',', _options.ExcludedSpellIds.Order()))));
        world.PlayerLoggedIn += OnPlayerLoggedIn;
    }

    public Task StopAsync()
    {
        if (_world is not null)
        {
            _world.PlayerLoggedIn -= OnPlayerLoggedIn;
        }

        return Task.CompletedTask;
    }

    public async Task OnSpellbookLoadedAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);

        if (!_options.Enabled)
            return;
        if (character.PlayedTime != 0 || session.Security < _options.MinimumSecurity)
        {
            _logger.LogDebug("GM first-login tools skipped: CharacterId={CharacterId} Security={Security} PlayedTime={PlayedTime} Revision={Revision}",
                character.Id, (byte)session.Security, character.PlayedTime, _revision);
            return;
        }

        // Validate the entire configured set before the first cache mutation. A bad DBC/config
        // entry therefore fails this login without leaving a partially granted book.
        IReadOnlyList<GmToolSpell> selected;
        try
        {
            selected = GmFirstLoginToolCatalog.Select(_options, session.Security, ResolveSpellStore(session));
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning("GM first-login tools rejected before mutation: CharacterId={CharacterId} Security={Security} Revision={Revision}",
                character.Id, (byte)session.Security, _revision);
            throw;
        }
        if (selected.Count == 0)
        {
            return;
        }

        SpellFeature spells = session.Services.GetRequiredService<SpellFeature>();
        int learned = 0;
        var added = new HashSet<uint>();
        foreach (GmToolSpell spell in selected)
        {
            if (spells.Spellbook.LearnSpell(player, spell.Id))
            {
                learned++;
                added.Add(spell.Id);
            }
        }

        // The single barrier is deliberately after the complete batch. A failure propagates
        // through the loading hook and prevents entry to the world.
        try
        {
            await spells.Spellbook.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning("GM first-login tools persistence failed: CharacterId={CharacterId} Security={Security} ErrorType={ErrorType} Revision={Revision}",
                character.Id, (byte)session.Security, error.GetType().Name, _revision);
            throw;
        }
        _grants.Remove(player);
        _grants.Add(player, new GrantState(learned, selected.Select(s => s.Id).ToArray()));

        _logger.LogInformation(
            "GM first-login tools ready: CharacterId={CharacterId} Security={Security} ClientBuild={ClientBuild} EligibleCount={EligibleCount} AddedCount={AddedCount} SpellIds={SpellIds}",
            character.Id, (byte)session.Security, ClientBuild, selected.Count, learned,
            string.Join(',', selected.Select(s => s.Id)));
        foreach (GmToolSpell spell in selected)
            _logger.LogInformation("GM first-login spell ready: CharacterId={CharacterId} Security={Security} SpellId={SpellId} SpellName={SpellName} ClientBuild={ClientBuild} Result={Result} Revision={Revision}",
                character.Id, (byte)session.Security, spell.Id, spell.Name, ClientBuild,
                added.Contains(spell.Id) ? "Added" : "AlreadyKnown", _revision);
    }

    private SpellStore ResolveSpellStore(WorldSession session)
        => session.Services.GetRequiredService<SpellFeature>().System.Store;

    private void OnPlayerLoggedIn(Player player)
    {
        if (!_options.ShowToolGuide || !_grants.TryGetValue(player, out GrantState? state))
        {
            return;
        }

        player.Session.Send(
            WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildSystemMessage(
                $"GM tools ready ({state.SpellIds.Count} spells; {state.Count} newly learned). Use .commands, .help, and .lookup spell."));
        _grants.Remove(player);
    }

    private sealed record GrantState(int Count, IReadOnlyList<uint> SpellIds);
}
