using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Pets;

public sealed partial class SummonService
{
    private CharmService? _charms;

    /// <summary>Charm and possession (docs/areas/unit-control.md); installed on the spell system together with the summon effects.</summary>
    public CharmService Charms => _charms ??= new CharmService(_systems, NextPetNumber, _random) { Summons = this };
}
