using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Login;
using Konscious.Security.Cryptography;

namespace ArcaneCore.Cryptography.Login;

public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    public const int MemoryKiB = 65536, Iterations = 3, Parallelism = 1, SaltBytes = 16, HashBytes = 32;
    public string Hash(string password)
    {
        ValidatePassword(password); byte[] pass = Encoding.UTF8.GetBytes(password), salt = RandomNumberGenerator.GetBytes(SaltBytes);
        try { byte[] output = Derive(pass, salt, MemoryKiB, Iterations, Parallelism); try { return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${B64(salt)}${B64(output)}"; } finally { CryptographicOperations.ZeroMemory(output); } }
        finally { CryptographicOperations.ZeroMemory(pass); CryptographicOperations.ZeroMemory(salt); }
    }
    public bool Verify(string password, string encodedHash)
    {
        if (password is null || encodedHash is null || password.Length < 10 || password.Length > 256 || encodedHash.Length > 255) return false;
        string[] parts = encodedHash.Split('$'); if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19" || !Parse(parts[3], out int memory, out int iterations, out int parallelism)) return false;
        if (memory is < 8 or > MemoryKiB || iterations is < 1 or > 10 || parallelism is < 1 or > 8 || memory < 8 * parallelism) return false;
        byte[]? salt = Decode(parts[4]), expected = Decode(parts[5]); if (salt is null || expected is null || salt.Length is < 8 or > 64 || expected.Length != HashBytes) return false;
        byte[] pass = Encoding.UTF8.GetBytes(password);
        try { byte[] actual = Derive(pass, salt, memory, iterations, parallelism); try { return CryptographicOperations.FixedTimeEquals(actual, expected); } finally { CryptographicOperations.ZeroMemory(actual); } }
        finally { CryptographicOperations.ZeroMemory(pass); CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(expected); }
    }
    private static byte[] Derive(byte[] pass, byte[] salt, int memory, int iterations, int parallelism) { using var argon = new Argon2id(pass) { Salt = salt, MemorySize = memory, Iterations = iterations, DegreeOfParallelism = parallelism }; return argon.GetBytes(HashBytes); }
    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=');
    private static byte[]? Decode(string value) { try { return Convert.FromBase64String(value + new string('=', (4 - value.Length % 4) % 4)); } catch (FormatException) { return null; } }
    private static bool Parse(string value, out int memory, out int iterations, out int parallelism) { memory = iterations = parallelism = 0; string[] fields = value.Split(','); if (fields.Length != 3) return false; foreach (string field in fields) { string[] pair = field.Split('='); if (pair.Length != 2 || !int.TryParse(pair[1], out int number) || number <= 0) return false; switch (pair[0]) { case "m": memory = number; break; case "t": iterations = number; break; case "p": parallelism = number; break; default: return false; } } return true; }
    private static void ValidatePassword(string password) { if (password is null || password.Length < 10 || password.Length > 256) throw new ArgumentException("Password length must be 10-256.", nameof(password)); }
}
