using ArcaneCore.World.OptionKeys;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The playerbot pack's own options for <c>.server config</c> (the core editor), registered through <see cref="IOptionKeyProvider"/> so the
/// core names no pack type. The playerbot options of <see cref="PlayerbotOptions"/> are in the core's reload keys already; these are the
/// ones the pack owns: bot QA (<see cref="PlayerbotQaOptions"/>), which is read once at start, so every key is restart-only and read-only
/// here (the editor shows the value in force; the configuration file and a restart change it). The folder and the GitHub reporter keys are
/// not listed (a folder path and a repository are not for chat).
/// </summary>
public sealed class PlayerbotOptionRegistration(IOptions<PlayerbotQaOptions> qa) : IOptionKeyProvider
{
    /// <inheritdoc/>
    public void RegisterOptions(IOptionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        PlayerbotQaOptions options = qa.Value;
        const string root = PlayerbotQaOptions.SectionName + ":";
        registry.AddBool(root + nameof(PlayerbotQaOptions.Enabled), "aggregate the bot warnings into QA findings", OptionApplyClass.Restart, () => options.Enabled);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.ExportIntervalSeconds), "seconds between two QA save, export and publish passes", OptionApplyClass.Restart, 10, 86_400, () => options.ExportIntervalSeconds);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.MaxFindings), "the most QA findings kept", OptionApplyClass.Restart, 10, 100_000, () => options.MaxFindings);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.StaleHours), "hours after which an unseen finding is dropped (0 keeps them)", OptionApplyClass.Restart, 0, 8_760, () => options.StaleHours);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.AutoPruneHours), "hours after which an unseen finding is forgotten on every pass (0 is off)", OptionApplyClass.Restart, 0, 8_760, () => options.AutoPruneHours);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.TickHangWarmupMinutes), "minutes after start during which a tick hang is not reported to QA", OptionApplyClass.Restart, 0, 1_440, () => options.TickHangWarmupMinutes);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.MaxSamples), "evidence samples kept per QA finding", OptionApplyClass.Restart, 1, 20, () => options.MaxSamples);
        registry.AddInteger(root + nameof(PlayerbotQaOptions.SignalQueueCapacity), "warnings waiting for the QA aggregator at once", OptionApplyClass.Restart, 64, 1_000_000, () => options.SignalQueueCapacity);
    }
}
