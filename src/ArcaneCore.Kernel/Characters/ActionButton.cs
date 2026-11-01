namespace ArcaneCore.Kernel.Characters;

/// <summary>One action-bar slot: packed as action | type &lt;&lt; 24 on the wire (vmangos ACTION_BUTTON_*).</summary>
public sealed record ActionButton(byte Button, uint Action, byte Type);
