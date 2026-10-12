using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Scripting;

/// <summary>
/// Loads the enabled <see cref="IScriptModule"/>s into the world's <see cref="ScriptHookRegistry"/> and bridges the hooks whose events the
/// Game layer already raises (group and guild membership, finished casts, equipment changes). A bridge is only subscribed once some hook for it exists (at
/// start-up, or when a hot module adds the first one), so with no hook registered none of this runs. docs/integration/script-hooks.md.
/// </summary>
public sealed class ScriptHooksFeature(IServiceProvider services) : IWorldFeature
{
    private readonly List<IScriptModule> _modules = [];
    private readonly Dictionary<Player, Action<Item, byte, Game.Items.EquipmentChange>> _equipBindings = [];
    private WorldRuntime? _world;
    private ILogger _logger = NullLogger<ScriptHooksFeature>.Instance;
    private bool _equipSubscribed, _socialSubscribed, _spellSubscribed, _socialWarned, _spellWarned;

    /// <summary>Every module type in this assembly, ordered by full name.</summary>
    public static IReadOnlyList<Type> ModuleTypes { get; } = AssemblyDiscovery.FindTypes<IScriptModule>();

    /// <summary>The modules that were enabled and registered, in load order.</summary>
    public IReadOnlyList<IScriptModule> Loaded => _modules;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        ILogger logger = _logger = services.GetService<ILoggerFactory>()?.CreateLogger<ScriptHooksFeature>() ?? NullLogger<ScriptHooksFeature>.Instance;
        IConfiguration? configuration = services.GetService<IConfiguration>();
        foreach (IScriptModule module in AssemblyDiscovery.CreateAll<IScriptModule>())
        {
            string path = SectionOf(module);
            IConfiguration section = (IConfiguration?)configuration?.GetSection(path) ?? new ConfigurationBuilder().Build();
            if (!section.GetValue("Enabled", false))
            {
                continue;
            }

            module.Register(new ScriptModuleContext(world, services, section));
            _modules.Add(module);
            logger.LogInformation("script module {Module} loaded", module.Name);
        }

        ScriptHookRegistry scripts = world.Scripts;
        EnsureEquipmentBridge();
        if (scripts.Group.HasHooks || scripts.Guild.HasHooks || scripts.Spell.HasHooks)
        {
            // The social and spell runtimes are complete once the world thread runs its first commands (features attach in name order).
            world.Post(EnsureBridges);
        }

        // A hot module can add the first hook of a kind later: subscribe then (world thread, tick boundary).
        scripts.HooksChanged += EnsureBridges;
    }

    private void EnsureBridges()
    {
        EnsureEquipmentBridge();
        EnsureSocialBridge();
        EnsureSpellBridge();
    }

    private void EnsureEquipmentBridge()
    {
        if (_equipSubscribed || _world is not { } world || !world.Scripts.Item.HasEquipHooks)
        {
            return;
        }

        _equipSubscribed = true;
        world.PlayerLoggedIn += BindEquipment;
        world.PlayerLoggingOut += UnbindEquipment;
        foreach (Player player in world.OnlinePlayers.ToArray())
        {
            BindEquipment(player);
        }
    }

    private void EnsureSocialBridge()
    {
        ScriptHookRegistry? scripts = _world?.Scripts;
        if (_socialSubscribed || scripts is null || (!scripts.Group.HasHooks && !scripts.Guild.HasHooks))
        {
            return;
        }

        if (services.GetService<SocialFeature>()?.Context is { } social)
        {
            _socialSubscribed = true;
            GroupManager groups = social.Groups;
            groups.MemberAdded += scripts.Group.OnMemberAdded;
            groups.MemberRemoved += scripts.Group.OnMemberRemoved;
            groups.Disbanding += (group, _) => scripts.Group.OnDisband(group);
            GuildManager guilds = social.Guilds;
            guilds.MemberJoined += (guild, characterId, _) => scripts.Guild.OnMemberAdded(guild, characterId);
            guilds.MemberLeft += scripts.Guild.OnMemberRemoved;
        }
        else
        {
            if (!_socialWarned)
            {
                _socialWarned = true;
                _logger.LogWarning("No social feature: the group and guild script hooks never run");
            }
        }
    }

    private void EnsureSpellBridge()
    {
        ScriptHookRegistry? scripts = _world?.Scripts;
        if (_spellSubscribed || scripts is null || !scripts.Spell.HasHooks)
        {
            return;
        }

        if (services.GetService<SpellFeature>()?.System is { } spells)
        {
            _spellSubscribed = true;
            spells.RegisterObserver(new SpellBridge(scripts.Spell));
        }
        else
        {
            if (!_spellWarned)
            {
                _spellWarned = true;
                _logger.LogWarning("No spell feature: the spell script hooks never run");
            }
        }
    }

    /// <summary><c>.reload config</c>: each loaded module gets its re-read section, then the world hooks run (world thread).</summary>
    public void ReloadConfig(IConfiguration modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (IScriptModule module in _modules)
        {
            string path = SectionOf(module);
            module.ReloadConfig(modules.GetSection(path));
        }

        _world?.Scripts.World.OnConfigReload();
    }

    private static string SectionOf(IScriptModule module) => $"{ScriptModuleContext.ModulesSectionName}:{module.Name}";

    private void BindEquipment(Player player)
    {
        UnbindEquipment(player);
        ItemHookDispatch items = _world!.Scripts.Item;
        Action<Item, byte, Game.Items.EquipmentChange> handler = (item, slot, change) =>
        {
            if (change == Game.Items.EquipmentChange.Worn) items.OnEquip(player, item, slot);
            else if (change == Game.Items.EquipmentChange.Removed) items.OnUnequip(player, item, slot);
        };
        player.Inventory.EquipmentChanged += handler;
        _equipBindings[player] = handler;
    }

    private void UnbindEquipment(Player player)
    {
        if (_equipBindings.Remove(player, out var handler))
        {
            player.Inventory.EquipmentChanged -= handler;
        }
    }

    private sealed class SpellBridge(SpellHookDispatch hooks) : ISpellCastObserver
    {
        public void OnFinished(SpellCast cast, bool completed) => hooks.OnCastFinished(cast, completed);
    }
}
