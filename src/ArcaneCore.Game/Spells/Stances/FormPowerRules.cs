namespace ArcaneCore.Game.Spells;

/// <summary>Power type facts the form handler needs.</summary>
public static class FormPowerRules
{
    /// <summary>
    /// The power type of a class (client ChrClasses.dbc, build 5875, powerType column: Warrior 1 = rage, Rogue 3 = energy,
    /// every other class 0 = mana). Player::InitDataForForm (Player.cpp:18271-18312) returns a player to it when a form
    /// that has its own power type ends.
    /// </summary>
    public static PowerType ClassPowerType(Class playerClass) => playerClass switch
    {
        Class.Warrior => PowerType.Rage,
        Class.Rogue => PowerType.Energy,
        _ => PowerType.Mana,
    };
}
