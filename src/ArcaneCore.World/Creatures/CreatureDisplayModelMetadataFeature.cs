using ArcaneCore.Data.Content.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>Optional developer-supplied build-5875 display/model metadata; absent or malformed files fail closed.</summary>
public sealed class CreatureDisplayModelMetadataFeature(
    IServiceProvider services,
    ILogger<CreatureDisplayModelMetadataFeature> logger) : IWorldFeature
{
    public CreatureDisplayModelMetadataContent Content { get; private set; } = CreatureDisplayModelMetadataContent.Empty;

    public void Attach(WorldRuntime world)
    {
        var options = new CreatureDisplayModelOptions();
        services.GetService<IConfiguration>()?.GetSection(CreatureDisplayModelOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.CreatureDisplayInfoDbcPath)
            || string.IsNullOrWhiteSpace(options.CreatureModelDataDbcPath))
        {
            if (!string.IsNullOrWhiteSpace(options.CreatureDisplayInfoDbcPath)
                || !string.IsNullOrWhiteSpace(options.CreatureModelDataDbcPath))
                throw new InvalidOperationException("Creature display/model metadata requires both DBC paths");
            return;
        }

        Content = CreatureDisplayModelDbcReader.Load(options.CreatureDisplayInfoDbcPath, options.CreatureModelDataDbcPath);
        logger.LogInformation("Loaded optional creature display/model metadata {Provenance}", Content.Provenance);
    }
}
