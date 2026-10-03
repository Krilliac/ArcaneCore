namespace ArcaneCore.Game.Ranged;

/// <summary>How ranged ammunition is treated (configuration key <c>Ranged:Ammo:Mode</c>).</summary>
public enum AmmoMode
{
    /// <summary>Retail 1.12.1: ranged attacks need compatible ammunition (or a thrown weapon) and consume one per shot.</summary>
    Retail = 0,

    /// <summary>Developer switch: ranged attacks neither require nor consume ammunition. The ammo slot itself still works.</summary>
    Infinite = 1,
}

/// <summary>How the spell range leeway of moving casters is treated (<c>Ranged:Range:Leeway</c>).</summary>
public enum RangeLeewayMode
{
    /// <summary>Retail 1.12.1 (vmangos Object.cpp GetLeewayBonusRange): +2.66 yd when a mover is involved and both run.</summary>
    Retail = 0,

    /// <summary>Deviation: no movement leeway; only the fixed 1.25 / 6.25 yd player allowance applies.</summary>
    None = 1,
}

/// <summary>Where a hunter trap takes its trigger radius from (<c>Ranged:Traps:RadiusSource</c>).</summary>
public enum TrapRadiusSource
{
    /// <summary>Retail 1.12.1 as vmangos ships it (GameObject.cpp:482-497): the twelve hunter trap templates trigger at 2.5 yd, every other trap at its template radius (data2).</summary>
    Vmangos = 0,

    /// <summary>Deviation: always the template radius (data2; the classic-db rows carry 5 yd for the hunter traps).</summary>
    Template = 1,
}

/// <summary>
/// Hunter / ranged-combat settings (configuration section "Ranged"). Every default is the
/// retail 1.12.1 behaviour (vmangos); a value that differs is a deliberate, documented deviation.
/// </summary>
public sealed class RangedOptions
{
    public const string SectionName = "Ranged";

    public AmmoOptions Ammo { get; } = new();

    public RangeOptions Range { get; } = new();

    public TrapOptions Traps { get; } = new();

    /// <summary>Ammunition settings (<c>Ranged:Ammo</c>).</summary>
    public sealed class AmmoOptions
    {
        public AmmoMode Mode { get; set; } = AmmoMode.Retail;
    }

    /// <summary>Trap settings (<c>Ranged:Traps</c>).</summary>
    public sealed class TrapOptions
    {
        public TrapRadiusSource RadiusSource { get; set; } = TrapRadiusSource.Vmangos;
    }

    /// <summary>Range settings (<c>Ranged:Range</c>).</summary>
    public sealed class RangeOptions
    {
        public RangeLeewayMode Leeway { get; set; } = RangeLeewayMode.Retail;
    }
}
