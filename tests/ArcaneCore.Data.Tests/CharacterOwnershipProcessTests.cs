using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CharacterOwnershipProcessTests
{
    [Fact]
    public async Task OverlappingProcessesFenceStaleSaveAndRelease()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "characters.sqlite");
        try
        {
            await using (CharacterDbContext setup = Context(database))
            {
                await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
                await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
                {
                    AccountId = 1, Name = "ProcessFence", Race = 1, Class = 1, Level = 1,
                });
            }

            await using ChildProcess first = ChildProcess.Start(database);
            await using ChildProcess second = ChildProcess.Start(database);
            Guid firstIncarnation = Guid.NewGuid();
            Guid secondIncarnation = Guid.NewGuid();

            JsonElement firstClaim = await first.SendAsync(new
            {
                op = "acquire", scope = "world", owner = "first", incarnation = firstIncarnation,
            });
            JsonElement tokenA = firstClaim.GetProperty("token").Clone();
            Assert.Equal("ok", firstClaim.GetProperty("status").GetString());

            JsonElement denied = await second.SendAsync(new
            {
                op = "acquire", scope = "world", owner = "second", incarnation = secondIncarnation,
            });
            Assert.Equal("denied", denied.GetProperty("status").GetString());

            JsonElement takeover = await second.SendAsync(new
            {
                op = "takeover", observed = tokenA, owner = "second", incarnation = secondIncarnation,
            });
            JsonElement tokenB = takeover.GetProperty("token").Clone();
            Assert.Equal("ok", takeover.GetProperty("status").GetString());
            Assert.NotEqual(tokenA.GetProperty("epoch").GetInt64(), tokenB.GetProperty("epoch").GetInt64());

            JsonElement staleSave = await first.SendAsync(new
            {
                op = "save", characterId = 1, map = 11, token = tokenA,
            });
            Assert.Equal("error", staleSave.GetProperty("status").GetString());
            Assert.Equal(nameof(CharacterOwnershipRejectedException), staleSave.GetProperty("error").GetString());

            JsonElement staleRelease = await first.SendAsync(new { op = "release", token = tokenA });
            Assert.Equal("released", staleRelease.GetProperty("status").GetString());

            JsonElement currentSave = await second.SendAsync(new
            {
                op = "save", characterId = 1, map = 22, token = tokenB,
            });
            Assert.Equal("saved", currentSave.GetProperty("status").GetString());

            JsonElement release = await second.SendAsync(new { op = "release", token = tokenB });
            Assert.Equal("released", release.GetProperty("status").GetString());
            Assert.Equal("exiting", (await second.SendAsync(new { op = "exit" })).GetProperty("status").GetString());
            Assert.Equal("exiting", (await first.SendAsync(new { op = "exit" })).GetProperty("status").GetString());

            await using CharacterDbContext verify = Context(database);
            CharacterRecord saved = (await new EfCharacterStore(verify).GetByIdAsync(1))!;
            Assert.Equal(22u, saved.MapId);
            Assert.False(await new EfCharacterOwnershipStore(verify).IsOwnedAsync("world"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static CharacterDbContext Context(string database)
        => new(new DbContextOptionsBuilder<CharacterDbContext>()
            .UseSqlite($"Data Source={database}")
            .Options);

    private sealed class ChildProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;

        private ChildProcess(Process process)
        {
            _process = process;
            _input = process.StandardInput;
            _output = process.StandardOutput;
        }

        public static ChildProcess Start(string database)
        {
            var info = new ProcessStartInfo(DotnetMuxer())
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Data.ProcessRunner.dll"));
            info.ArgumentList.Add(database);
            Process process = Process.Start(info) ?? throw new InvalidOperationException("ownership process did not start");
            return new ChildProcess(process);
        }

        public async Task<JsonElement> SendAsync(object command)
        {
            await _input.WriteLineAsync(JsonSerializer.Serialize(command));
            await _input.FlushAsync();
            string? line = await _output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (line is null)
            {
                string error = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"ownership process exited before replying: {error}");
            }

            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            _process.Dispose();
        }

        private static string DotnetMuxer()
        {
            string? current = Environment.ProcessPath;
            if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
            string muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            return File.Exists(muxer) ? muxer : "dotnet";
        }
    }
}
