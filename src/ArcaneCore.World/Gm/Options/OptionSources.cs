using ArcaneCore.World.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// Where <see cref="OptionCatalog"/> gets its keys: adapters over the existing, already classified sources, so no option is declared twice
/// (<see cref="WorldConfigKeySource"/> for the reload keys, <see cref="LegacyOptionSources"/> for the rates, anticheat, packet-heuristics
/// and movement settings); packs and features add theirs through <see cref="IOptionSource"/>. All run on the world thread.
/// </summary>
internal static class OptionSources
{
    internal static IReadOnlyList<OptionKey> Collect(OptionScope scope, out int skipped)
    {
        var keys = new List<OptionKey>();
        skipped = 0;
        WorldConfigKeySource.Add(scope, keys, ref skipped);
        LegacyOptionSources.AddRates(scope, keys);
        LegacyOptionSources.AddAntiCheat(scope, keys);
        LegacyOptionSources.AddPacketHeuristics(scope, keys);
        AddProviders(scope, keys);
        return keys;
    }

    // --- packs -------------------------------------------------------------------------------------------------------

    private static void AddProviders(OptionScope scope, List<OptionKey> keys)
    {
        foreach (IOptionSource provider in scope.Services.GetServices<IOptionSource>())
        {
            try
            {
                provider.RegisterOptions(new Registry(keys));
            }
            catch (Exception ex)
            {
                scope.Logger.LogWarning(ex, "Option provider {Provider} failed to register its keys", provider.GetType().Name);
            }
        }
    }

    private sealed class Registry(List<OptionKey> keys) : IOptionRegistry
    {
        private const string Group = "Pack";

        public void AddBool(string path, string description, OptionApplyClass apply, Func<bool?> get, Action<bool>? set = null, Func<bool, string?>? check = null)
            => keys.Add(new OptionKey(path, description, OptionType.Bool, apply, Group, _ => get(), Setter(set, check)));

        public void AddInteger(string path, string description, OptionApplyClass apply, long min, long max, Func<long?> get, Action<long>? set = null, Func<long, string?>? check = null)
            => keys.Add(new OptionKey(path, description, OptionType.Integer, apply, Group, _ => get(), Setter(set, check), min: min, max: max));

        public void AddNumber(string path, string description, OptionApplyClass apply, double min, double max, Func<double?> get, Action<double>? set = null, Func<double, string?>? check = null)
            => keys.Add(new OptionKey(path, description, OptionType.Number, apply, Group, _ => get(), Setter(set, check), min: min, max: max));

        public void AddText(string path, string description, OptionApplyClass apply, Func<string?> get, Action<string>? set = null, bool secret = false, Func<string, string?>? check = null)
            => keys.Add(new OptionKey(path, description, OptionType.Text, apply, Group, _ => get(), Setter(set, check), secret: secret));

        // The key's own cross-check refuses with its own text before the setter runs.
        private static Func<OptionScope, object, OptionActor, string?>? Setter<T>(Action<T>? set, Func<T, string?>? check)
            => set is null
                ? null
                : (_, v, _) =>
                {
                    T value = (T)v;
                    if (check?.Invoke(value) is { } problem)
                    {
                        return problem;
                    }

                    set(value);
                    return null;
                };
    }
}
