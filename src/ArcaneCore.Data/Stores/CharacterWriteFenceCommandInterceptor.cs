namespace ArcaneCore.Data.Stores;

/// <summary>A write reached the characters database outside a transaction while the ownership write fence is on.</summary>
public sealed class CharacterWriteFenceException(string message) : InvalidOperationException(message);
