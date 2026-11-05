namespace ArcaneCore.World.Net;

/// <summary>
/// The configured world endpoint is taken by another listener (another world process): thrown by the early probe in <see cref="WorldHost"/> and by the real bind in
/// <see cref="WorldServer"/>. <see cref="WorldStartup.RunAsync"/> turns it into a logged failure and a non-zero exit code instead of an unhandled exception,
/// which the crash handler would turn into a fail-fast with a dump.
/// </summary>
internal sealed class WorldPortInUseException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);
