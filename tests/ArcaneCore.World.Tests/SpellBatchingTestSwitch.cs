using System.Runtime.CompilerServices;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Tests;

/// <summary>
/// <c>ARCANECORE_TEST_SPELL_BATCH_MS</c> runs this whole test assembly with spell batching on: it becomes the window every
/// <see cref="SpellSystem"/> starts with (SpellSystem.Batching.cs). Unset or 0 leaves batching off, the server default. A test that is
/// about something else and assumes a cast resolves in the same call pins its own system to 0.
/// </summary>
internal static class SpellBatchingTestSwitch
{
    /// <summary>The environment variable that turns spell batching on for the whole run, in milliseconds.</summary>
    public const string Variable = "ARCANECORE_TEST_SPELL_BATCH_MS";

#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (uint.TryParse(Environment.GetEnvironmentVariable(Variable), out uint windowMs))
        {
            SpellSystem.DefaultSpellBatchWindowMs = windowMs;
        }
    }
#pragma warning restore CA2255
}
