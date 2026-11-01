namespace ArcaneCore.Kernel.Characters;

/// <summary>The identity fields of a character, for name queries and /who of offline players.</summary>
public sealed record CharacterIdentity(int Id, int AccountId, string Name, byte Race, byte Gender, byte Class, byte Level = 1, uint ZoneId = 0);
