using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Scripting;

/// <summary>
/// A script module (the AzerothCore <c>modules/mod-*</c> unit: a loader that registers its scripts, <c>Addmod_*Scripts</c>). Every
/// non-abstract implementation in this assembly with a parameterless constructor is discovered; it runs only when
/// <c>Modules:&lt;Name&gt;:Enabled</c> is true. docs/integration/script-hooks.md.
/// </summary>
public interface IScriptModule
{
    /// <summary>The configuration name: the module's settings are the <c>Modules:&lt;Name&gt;</c> section.</summary>
    string Name { get; }

    /// <summary>Read the settings and register the module's hooks (before the world thread starts).</summary>
    void Register(ScriptModuleContext context);

    /// <summary><c>.reload config</c>: <paramref name="section"/> is the re-read <c>Modules:&lt;Name&gt;</c> section (world thread).</summary>
    void ReloadConfig(IConfiguration section) { }
}

/// <summary>What a module sees while it registers.</summary>
public sealed class ScriptModuleContext(WorldRuntime world, IServiceProvider services, IConfiguration section)
{
    /// <summary>The configuration section that holds every module's settings (each module documents its own <c>SectionName</c>).</summary>
    public const string ModulesSectionName = "Modules";

    public WorldRuntime World { get; } = world;

    /// <summary>The world's services: other features are found here, but many are only complete once the world thread runs.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>This module's <c>Modules:&lt;Name&gt;</c> section.</summary>
    public IConfiguration Configuration { get; } = section;

    /// <summary>Register a hook object with the world (<see cref="ScriptHookRegistry.Register"/>).</summary>
    public void AddHooks(IScriptHooks hooks) => World.Scripts.Register(hooks);
}
