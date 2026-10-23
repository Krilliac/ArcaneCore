namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>File helpers shared by the QA exporters.</summary>
internal static class QaFiles
{
    /// <summary>
    /// Write <paramref name="text"/> to <paramref name="path"/> through a sibling <c>.tmp</c> file, so a reader never sees a half-written file.
    /// An <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> surfaces to the caller, which logs it and retries.
    /// </summary>
    internal static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
