using System.Text.RegularExpressions;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Source sweep behind the characters write fence: every set-based write (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/<c>ExecuteSql*</c>/raw
/// commands) in a file that uses <c>CharacterDbContext</c> must sit in a member that runs it in a verified transaction
/// (<c>CharacterWrites.InTransactionAsync</c>, or a transaction it begins itself) or be named in <see cref="Exemptions"/> with the reason.
/// With <c>Cluster:Fencing:Enabled</c> an unlisted bare write is refused at run time (<c>CharacterWriteFenceCommandInterceptor</c>), so a new
/// one that nobody wrapped would break a fenced node; this test turns that into a build-time failure instead.
/// </summary>
public sealed class CharacterWriteSweepTests
{
    private static readonly Regex SetBasedWrite = new(@"\b(ExecuteUpdate(Async)?|ExecuteDelete(Async)?|ExecuteSql\w*|ExecuteNonQuery(Async)?)\s*\(", RegexOptions.Compiled);
    private static readonly Regex MemberHeader = new(@"^\s{0,8}(?:public|internal|private|protected)\b[^;=]*\(", RegexOptions.Compiled);
    private static readonly Regex TypeHeader = new(@"\b(class|record|struct|interface|enum)\b", RegexOptions.Compiled);

    private static readonly string[] SafeMarkers =
    [
        "CharacterWrites.InTransactionAsync", // the helper: verified transaction when the fence is on, plain when off
        "BeginTransactionAsync(",             // a transaction the member opens: the fence verifies it when it starts
        "CharacterWriteFence.Exempt()",        // marked exempt in the member itself (the reason is in a comment at the site)
    ];

    /// <summary>"relative/path.cs::Member" -> why a bare write there is correct. Anything not listed and not safe fails the sweep.</summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        // The fence itself and the stores that must work before or without a verified owner.
        ["ArcaneCore.Data/Stores/CharacterOwnershipGuard.cs::*"] = "the guard: it is the verification, it cannot verify itself",
        ["ArcaneCore.Data/Characters/Ownership/CharacterOwnershipDataModule.cs::*"] = "the ownership store claims, renews and releases the fence row; a stale owner must be able to read the takeover and a new one to claim",
        ["ArcaneCore.Data/Resilience/SqliteConnectionPragmaInterceptor.cs::*"] = "connection PRAGMA setup, no row data",
        ["ArcaneCore.Data/Schema/*"] = "schema bootstrap/upgrade: runs before the ownership row can exist or be read",

        // ICharacterDataCleanup.DeleteCharacterDataAsync runs inside EfCharacterStore.DeleteAsync, which owns (or joins) a transaction started through the fence.
        ["*::DeleteCharacterDataAsync"] = "runs inside the verified transaction of EfCharacterStore.DeleteAsync",
        ["ArcaneCore.Data/Economy/EconomyCharacterCleanup.cs::StageAsync"] = "called from EconomyDataModule.DeleteCharacterDataAsync, inside the deletion transaction",

        // Private/internal helpers whose only callers already hold a verified transaction (the sweep is per member, so the helper looks bare).
        ["ArcaneCore.Data/Loot/EfLootStateStore.cs::DeleteStateAsync"] = "only called from CommitAsync, after its Serializable BeginTransactionAsync",
        ["ArcaneCore.Data/Characters/Spells/CharacterSpellStateDataModule.cs::DeleteRowsAsync"] = "only called from SaveAsync, after its BeginTransactionAsync",
        ["ArcaneCore.Data/Social/SocialDataModule.cs::DeleteReferencesAsync"] = "only called from DeleteCharacterDataAsync, inside the deletion transaction",
        ["ArcaneCore.Data/Characters/Pets/PersistentPetDataModule.cs::WriteRowsAsync"] = "only called from EfPersistentPetStore.SaveCoreAsync and EfPetStableStore.UnstableAsync, after their BeginTransactionAsync",

        // The transfer journal (characters schema 62): these helpers run only inside EfTransferDecisionStore.InTransactionAsync, which begins its
        // own transaction under CharacterWriteFence.Exempt; the journal's compare-and-swap on the ownership row is the handoff's own fence.
        ["ArcaneCore.Data/Characters/Transfers/EfTransferDecisionStore.cs::CreateReservationCoreAsync"] = "the claim CAS runs inside the store's InTransactionAsync lambda",
        ["ArcaneCore.Data/Characters/Transfers/EfTransferDecisionStore.cs::CommitRowAsync"] = "only called inside the store's InTransactionAsync (commit and epoch takeover)",
        ["ArcaneCore.Data/Characters/Transfers/EfTransferDecisionStore.cs::RebindCommittedTargetAsync"] =
            "the owner-row CAS runs inside the store's InTransactionAsync lambda (its own transaction under CharacterWriteFence.Exempt), as CommitRowAsync's; the compare-and-swap on scope, epoch, incarnation and active transfer is the journal's own fence",
        ["ArcaneCore.Data/Characters/Transfers/EfTransferDecisionStore.cs::ReleaseClaimAsync"] = "only called inside the store's InTransactionAsync (complete and abort)",
        ["ArcaneCore.Data/Characters/Transfers/EfTransferDecisionStore.cs::ClaimInTransactionAsync"] = "the claim and dead-owner reap run inside the store's InTransactionAsync lambda (the method only exists to pass that lambda)",

        // Not the characters database at all (the file also holds a characters store, which is why the file-level scan sees it).
        ["ArcaneCore.Data/Economy/AuctionBotDataModules.cs::DeleteAsync"] = "EfAuctionBotItemStore writes the world database (WorldDbContext)",

        // Deletion lifetime fence: removes only rows of an id that has no characters row (a race with deletion), whoever owns the database.
        ["ArcaneCore.Data/Characters/Cluster/RealmAuthorityLeaseDataModule.cs::RelaxCommitDurability"] = "SET LOCAL changes a transaction setting, it writes no row; the renewal UPDATE it precedes is the lease's own compare on owner and token",
        ["ArcaneCore.Data/Characters/Spells/CharacterSpellStateDataModule.cs::DeleteCharacterAsync"] = "lifetime fence, exempt by CharacterWriteFence.Exempt at the call",
    };

    [Fact]
    public void EverySetBasedCharactersWrite_RunsVerified_OrIsListedWithAReason()
    {
        string root = RepositorySource.FindRoot() ?? throw new InvalidOperationException("repository root not found");
        string src = Path.Combine(root, "src");
        var offenders = new List<string>();
        int sites = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            string text = File.ReadAllText(file);
            if (!text.Contains("CharacterDbContext", StringComparison.Ordinal))
                continue;
            string relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!SetBasedWrite.IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                    continue;
                sites++;
                (string member, int start, int end) = EnclosingMember(lines, i);
                if (IsExempt(relative, member))
                    continue;
                string body = string.Join('\n', lines[start..end]);
                if (SafeMarkers.Any(marker => body.Contains(marker, StringComparison.Ordinal)))
                    continue;
                offenders.Add($"{relative}:{i + 1} in {member}");
            }
        }

        Assert.True(sites > 50, $"the sweep found only {sites} set-based write sites; the scan has stopped scanning");
        Assert.True(offenders.Count == 0,
            "set-based characters writes outside a verified transaction (route through CharacterWrites.InTransactionAsync, or add an exemption with a reason):\n"
            + string.Join('\n', offenders));
    }

    /// <summary>A synchronous <c>SaveChanges(</c> or <c>Database.BeginTransaction(</c> (the async forms end in <c>Async(</c> and do not match).</summary>
    private static readonly Regex SyncWrite = new(@"(?<![\w])(SaveChanges|Database\.BeginTransaction)\s*\(", RegexOptions.Compiled);

    /// <summary>"relative/path.cs::Member" -> why a synchronous transaction there is correct.</summary>
    private static readonly Dictionary<string, string> SyncExemptions = new(StringComparer.Ordinal)
    {
        ["ArcaneCore.Data/Characters/Cluster/RealmAuthorityLeaseDataModule.cs::BeginLeaseTransaction"] = "helper whose three callers (TryAcquire and the renew and release paths) each open it under CharacterWriteFence.Exempt()",
    };

    [Fact]
    public void NoSourceFile_UsesASynchronousSaveChangesOrBeginTransaction_OnACharactersContext_OutsideAnExemptMember()
    {
        string root = RepositorySource.FindRoot() ?? throw new InvalidOperationException("repository root not found");
        string src = Path.Combine(root, "src");
        var offenders = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            string text = File.ReadAllText(file);
            if (!text.Contains("CharacterDbContext", StringComparison.Ordinal))
                continue;
            files++;
            string relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!SyncWrite.IsMatch(lines[i]) || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                (string member, int start, int end) = EnclosingMember(lines, i);
                string body = string.Join('\n', lines[start..end]);
                if (SyncExemptions.ContainsKey($"{relative}::{member}"))
                    continue; // named member with a recorded reason
                if (body.Contains("CharacterWriteFence.Exempt()", StringComparison.Ordinal))
                    continue; // the fence skips exempt work, so a synchronous transaction there is allowed
                offenders.Add($"{relative}:{i + 1} in {member}");
            }
        }

        Assert.True(files > 50, $"the sweep saw only {files} characters-context files; the scan has stopped scanning");
        Assert.True(offenders.Count == 0,
            "synchronous SaveChanges/BeginTransaction on a characters context (the write fence refuses it; use the async API, or mark the member CharacterWriteFence.Exempt):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void TheSynchronousWritePattern_MatchesTheSyncFormsAndNotTheAsyncOnes()
    {
        Assert.Matches(SyncWrite, "        db.SaveChanges();");
        Assert.Matches(SyncWrite, "        using var t = db.Database.BeginTransaction();");
        Assert.DoesNotMatch(SyncWrite, "        await db.SaveChangesAsync(ct);");
        Assert.DoesNotMatch(SyncWrite, "        await db.Database.BeginTransactionAsync(ct);");
        Assert.DoesNotMatch(SyncWrite, "        await CharacterWriteFence.BeginTransactionAsync(db, token, ct);");
    }

    [Fact]
    public void EveryExemption_NamesAFileThatStillExists()
    {
        string root = RepositorySource.FindRoot() ?? throw new InvalidOperationException("repository root not found");
        string src = Path.Combine(root, "src");
        foreach (string key in Exemptions.Keys)
        {
            int cut = key.IndexOf("::", StringComparison.Ordinal);
            string path = cut >= 0 ? key[..cut] : key;
            if (path.Length == 0 || path.EndsWith('*'))
            {
                Assert.True(path.Length == 0 || Directory.Exists(Path.Combine(src, path.TrimEnd('*').TrimEnd('/'))), $"stale exemption {key}");
                continue;
            }

            Assert.True(File.Exists(Path.Combine(src, path)), $"stale exemption {key}: the file is gone");
        }
    }

    private static bool IsExempt(string relative, string member)
    {
        foreach (string key in Exemptions.Keys)
        {
            int split = key.IndexOf("::", StringComparison.Ordinal);
            if (split < 0)
            {
                if (key.EndsWith('*') && relative.StartsWith(key[..^1], StringComparison.Ordinal))
                    return true;
                continue;
            }

            string path = key[..split];
            string name = key[(split + 2)..];
            bool pathMatches = path.Length == 0 || path == "*" || path == relative;
            if (pathMatches && (name == "*" || name == member))
                return true;
        }

        return false;
    }

    /// <summary>The declaration line at or above <paramref name="index"/> and the range of lines up to the next declaration.</summary>
    private static (string Name, int Start, int End) EnclosingMember(string[] lines, int index)
    {
        int start = index;
        while (start > 0 && !(MemberHeader.IsMatch(lines[start]) && !TypeHeader.IsMatch(lines[start])))
            start--;
        int end = index + 1;
        while (end < lines.Length && !(MemberHeader.IsMatch(lines[end]) && !TypeHeader.IsMatch(lines[end])))
            end++;
        string header = lines[start];
        int paren = header.IndexOf('(');
        string before = paren > 0 ? header[..paren].TrimEnd() : header.Trim();
        int space = Math.Max(before.LastIndexOf(' '), before.LastIndexOf('.'));
        string name = space >= 0 ? before[(space + 1)..] : before;
        int generic = name.IndexOf('<');
        return (generic > 0 ? name[..generic] : name, start, end);
    }
}
