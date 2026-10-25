using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Microsoft.Data.Sqlite before 10.0.13 can lend one native handle to two live <see cref="SqliteConnection"/>s
/// when a pool grows under concurrent opens (dotnet/efcore#39008). The symptom is a fast
/// "database is locked" from Open(), and shared transaction state between unrelated contexts.
/// </summary>
public sealed class SqlitePoolConcurrencyTests
{
    private const int Rounds = 300;
    private const int Threads = 32;

    [Fact]
    public void SqliteDriver_IsAtLeastTheVersionThatFixesPoolHandleSharing()
    {
        string raw = typeof(SqliteConnection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        int plus = raw.IndexOf('+', StringComparison.Ordinal);
        string text = plus >= 0 ? raw[..plus] : raw;
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            text = text[..dash];
        }

        Assert.True(Version.TryParse(text, out Version? version), $"Unparseable driver version '{raw}'.");
        Assert.True(version >= new Version(10, 0, 13), $"Microsoft.Data.Sqlite {raw} is older than 10.0.13 (efcore#39008).");
    }

    [Fact]
    public void ConcurrentPoolGrowth_NeverSharesANativeHandleBetweenLiveConnections()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane_pool_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                string cs = $"Data Source={Path.Combine(dir, round + ".db")};Pooling=True";
                var owners = new ConcurrentDictionary<nint, int>();
                var failures = new ConcurrentQueue<string>();
                using var start = new Barrier(Threads);
                using var allOpen = new Barrier(Threads);

                var threads = new Thread[Threads];
                for (int t = 0; t < Threads; t++)
                {
                    int id = t;
                    threads[t] = new Thread(() =>
                    {
                        SqliteConnection? connection = null;
                        bool registered = false;
                        bool startSignaled = false;
                        nint handle = 0;
                        try
                        {
                            try
                            {
                                connection = new SqliteConnection(cs);
                            }
                            finally
                            {
                                // Always release the start barrier exactly once, even if construction failed.
                                startSignaled = true;
                                start.SignalAndWait();
                            }

                            connection.Open();
                            handle = connection.Handle!.DangerousGetHandle();
                            registered = owners.TryAdd(handle, id);
                            if (!registered)
                            {
                                failures.Enqueue($"round {round}: handle shared by threads {owners[handle]} and {id}");
                            }
                        }
                        catch (Exception ex)
                        {
                            // Nothing may escape a raw Thread: it would crash the test host.
                            failures.Enqueue($"round {round}: thread {id} failed: {ex.GetType().Name}: {ex.Message}");
                        }
                        finally
                        {
                            try
                            {
                                if (!startSignaled)
                                {
                                    startSignaled = true;
                                    start.SignalAndWait();
                                }

                                // Hold every connection open until all threads have opened (or failed), then release.
                                allOpen.SignalAndWait();
                            }
                            catch (Exception ex)
                            {
                                failures.Enqueue($"round {round}: thread {id} barrier failed: {ex.GetType().Name}: {ex.Message}");
                            }

                            if (registered)
                            {
                                owners.TryRemove(handle, out _);
                            }

                            try
                            {
                                connection?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                failures.Enqueue($"round {round}: thread {id} dispose failed: {ex.GetType().Name}: {ex.Message}");
                            }

                        }
                    }) { IsBackground = true };
                    threads[t].Start();
                }

                foreach (Thread thread in threads)
                {
                    thread.Join();
                }

                TestDatabases.ClearSqlitePool(cs);
                Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
