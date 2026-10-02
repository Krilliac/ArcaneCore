using ArcaneCore.Data.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Characters;

/// <summary>Brings the characters schema to the current version.</summary>
public sealed class CharacterDbInitializer(IServiceProvider services, ILogger<CharacterDbInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, logger, cancellationToken).ConfigureAwait(false);
    }
}
