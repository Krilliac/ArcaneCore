using ArcaneCore.World.Features;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Pets;

/// <summary>Installs the world name-rule seam without coupling the game pet controller to world catalogs.</summary>
public sealed class PetNamingFeature(PetsFeature pets, IServiceProvider services) : IWorldFeature
{
    private readonly PetNameRules _rules = services.GetService<PetNameRules>() ?? new PetNameRules();
    public PetNameRules Rules => _rules;
    public void Attach(WorldRuntime world)
    {
        IConfiguration? configuration = services.GetService<IConfiguration>();
        IConfigurationSection? section = configuration?.GetSection(PetNameOptions.SectionName);
        if (section?.Exists() == true)
        {
            PetNameOptions options = new();
            section.Bind(options);
            _rules.Apply(options);
        }
        pets.Controller.PetNameNormalizer = _rules.NormalizeAndValidate;
    }
}
