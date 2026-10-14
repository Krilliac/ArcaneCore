using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ArcaneCore.Data.ProcessRunner <sqlite-path>");
    return 2;
}

string databasePath = Path.GetFullPath(args[0]);
if (Directory.Exists(databasePath))
{
    Console.Error.WriteLine("the database path must be a file");
    return 2;
}

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonElement command = default;
    try
    {
        using JsonDocument document = JsonDocument.Parse(line);
        command = document.RootElement.Clone();
        string operation = command.GetProperty("op").GetString() ?? string.Empty;
        object result = operation switch
        {
            "acquire" => await AcquireAsync(command),
            "takeover" => await TakeoverAsync(command),
            "save" => await SaveAsync(command),
            "release" => await ReleaseAsync(command),
            "exit" => new { pid = Environment.ProcessId, status = "exiting" },
            _ => throw new InvalidOperationException($"unknown operation '{operation}'"),
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        Console.Out.Flush();
        if (operation == "exit")
        {
            return 0;
        }
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        JsonElement? commandToken = command.ValueKind == JsonValueKind.Object
            && command.TryGetProperty("token", out JsonElement token) ? token.Clone() : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            status = "error",
            error = error.GetType().Name,
            message = error.Message,
            token = commandToken,
        }));
        Console.Out.Flush();
    }
}

return 0;

async Task<object> AcquireAsync(JsonElement command)
{
    await using CharacterDbContext db = Context();
    CharacterOwnershipToken? token = await new EfCharacterOwnershipStore(db).TryAcquireAsync(
        command.GetProperty("scope").GetString() ?? "world",
        command.GetProperty("owner").GetString() ?? throw new InvalidOperationException("owner is required"),
        Guid.Parse(command.GetProperty("incarnation").GetString() ?? throw new InvalidOperationException("incarnation is required")));
    return TokenResult(token);
}

async Task<object> TakeoverAsync(JsonElement command)
{
    await using CharacterDbContext db = Context();
    CharacterOwnershipToken observed = ReadToken(command.GetProperty("observed"));
    CharacterOwnershipToken? token = await new EfCharacterOwnershipStore(db).TryTakeoverAsync(
        observed,
        command.GetProperty("owner").GetString() ?? throw new InvalidOperationException("owner is required"),
        Guid.Parse(command.GetProperty("incarnation").GetString() ?? throw new InvalidOperationException("incarnation is required")));
    return TokenResult(token);
}

async Task<object> SaveAsync(JsonElement command)
{
    await using CharacterDbContext db = Context();
    CharacterOwnershipToken token = ReadToken(command.GetProperty("token"));
    int id = command.GetProperty("characterId").GetInt32();
    uint map = command.GetProperty("map").GetUInt32();
    await new EfCharacterStore(db).SaveStateAsync(new CharacterState(
        id, map, 0, 0, 0, 0, 0, 1, 0, Ownership: token));
    return new { pid = Environment.ProcessId, status = "saved", map, token = TokenObject(token) };
}

async Task<object> ReleaseAsync(JsonElement command)
{
    await using CharacterDbContext db = Context();
    CharacterOwnershipToken token = ReadToken(command.GetProperty("token"));
    await new EfCharacterOwnershipStore(db).ReleaseAsync(token);
    return new { pid = Environment.ProcessId, status = "released", token = TokenObject(token) };
}

CharacterDbContext Context()
{
    var options = new DbContextOptionsBuilder<CharacterDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options;
    return new CharacterDbContext(options);
}

object TokenResult(CharacterOwnershipToken? token) => new
{
    pid = Environment.ProcessId,
    status = token is null ? "denied" : "ok",
    token = token is null ? null : TokenObject(token),
};

static CharacterOwnershipToken ReadToken(JsonElement value) => new(
    value.GetProperty("scope").GetString() ?? throw new InvalidOperationException("token scope is required"),
    value.GetProperty("epoch").GetInt64(),
    Guid.Parse(value.GetProperty("incarnation").GetString() ?? throw new InvalidOperationException("token incarnation is required")));

static object TokenObject(CharacterOwnershipToken token) => new
{
    scope = token.Scope,
    epoch = token.Epoch,
    incarnation = token.Incarnation,
};
