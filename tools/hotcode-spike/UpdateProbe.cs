using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(UpdateProbe))]

/// <summary>Records the callbacks the runtime makes after a metadata update (the path the server's refresh relies on).</summary>
internal static class UpdateProbe
{
    private static int s_clears;
    private static int s_updates;
    private static volatile string s_lastThread = "-";
    private static volatile int s_lastTypeCount = -2;

    public static int Clears => Volatile.Read(ref s_clears);

    public static int Updates => Volatile.Read(ref s_updates);

    public static string LastThread => s_lastThread;

    public static int LastTypeCount => s_lastTypeCount;

    internal static void ClearCache(Type[]? updatedTypes) => Interlocked.Increment(ref s_clears);

    internal static void UpdateApplication(Type[]? updatedTypes)
    {
        s_lastTypeCount = updatedTypes?.Length ?? -1;
        s_lastThread = $"pool{(Thread.CurrentThread.IsThreadPoolThread ? 1 : 0)}main{(Environment.CurrentManagedThreadId == 1 ? 1 : 0)}";
        Interlocked.Increment(ref s_updates);
    }
}
