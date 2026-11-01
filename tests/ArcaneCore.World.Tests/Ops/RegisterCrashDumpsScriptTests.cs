using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// tools/ops/diag/register-crash-dumps.ps1 under PowerShell 7. The script never touches the real registry here: every run happens
/// inside a wrapper that replaces Test-Path, New-Item, Set-ItemProperty, Get-ItemProperty, Get-ChildItem and Remove-Item with functions
/// that keep HKLM paths in an in-memory table (parent key included; each write is logged), and that supplies the elevation answer
/// through the script's test hook (honoured only with ARCANECORE_TEST_CRASHDUMPS_SHIM=1). Any non-HKLM path outside the temporary
/// directory throws instead of reaching a real cmdlet. A source scan rejects registry forms these shims cannot intercept.
/// </summary>
public sealed class RegisterCrashDumpsScriptTests : IDisposable
{
    private const string Parent = @"HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
    private const string Marker = "tools/ops/diag/register-crash-dumps.ps1";

    private static readonly string Script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "register-crash-dumps.ps1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-crashdumps-" + Guid.NewGuid().ToString("N"));

    public RegisterCrashDumpsScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LiveHome => Path.Combine(_root, "home");

    private static Dictionary<string, string> Ours(params (string, string)[] more) => new(more.Append(("ArcaneCoreRegisteredBy", Marker)).ToDictionary(p => p.Item1, p => p.Item2));

    // ---- -Plan ----------------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void Plan_Default_ListsTheThreeKeysWithTheirValues_AndWritesNothing_EvenWhenNotElevated()
    {
        Result result = Run(["-Plan"], elevated: false, liveHome: LiveHome);

        Assert.Equal(0, result.Code);
        Assert.StartsWith("PLAN register-crash-dumps (nothing was changed)", result.Output, StringComparison.Ordinal);
        foreach (string exe in new[] { "ArcaneCore.World.exe", "ArcaneCore.Realm.exe", "ArcaneCore.Gateway.exe" })
        {
            Assert.Contains($@"key: {Parent}\{exe} (new key)", result.Output, StringComparison.Ordinal);
        }

        string wer = Path.Combine(LiveHome, "dumps", "wer");
        Assert.Contains($"{Parent} is present: not modified", result.Output, StringComparison.Ordinal);
        Assert.Contains($"dump folder: {wer} (created if missing)", result.Output, StringComparison.Ordinal);
        Assert.Contains($"DumpFolder  REG_EXPAND_SZ  {wer}", result.Output, StringComparison.Ordinal);
        Assert.Contains("DumpType    REG_DWORD      2 (Full)", result.Output, StringComparison.Ordinal);
        Assert.Contains("DumpCount   REG_DWORD      5", result.Output, StringComparison.Ordinal);
        Assert.Contains($"ArcaneCoreRegisteredBy  REG_SZ  {Marker}", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.Empty(result.RegistryKeys);
        Assert.False(Directory.Exists(Path.Combine(LiveHome, "dumps")), "the plan created the dump folder");
    }

    [PwshFact]
    public void Plan_WhenElevated_StillWritesNothing()
    {
        string folder = Path.Combine(_root, "dumps-plan-elevated");
        Result result = Run(["-Plan", "-DumpFolder", $"'{Q(folder)}'"], elevated: true);

        Assert.Equal(0, result.Code);
        Assert.StartsWith("PLAN register-crash-dumps (nothing was changed)", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.Empty(result.RegistryKeys);
        Assert.False(Directory.Exists(folder));
    }

    [PwshFact]
    public void Plan_WithParameters_AndWhatIf_PrintTheChosenValues_AndWriteNothing()
    {
        string custom = Path.Combine(_root, "elsewhere");
        Result result = Run(["-WhatIf", "-DumpFolder", $"'{Q(custom)}'", "-DumpType", "Mini", "-DumpCount", "3", "-Executables", "ArcaneCore.World.exe"], elevated: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("DumpType    REG_DWORD      1 (Mini)", result.Output, StringComparison.Ordinal);
        Assert.Contains("DumpCount   REG_DWORD      3", result.Output, StringComparison.Ordinal);
        Assert.Contains($"DumpFolder  REG_EXPAND_SZ  {custom}", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ArcaneCore.Realm.exe", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.False(Directory.Exists(custom));
    }

    [PwshFact]
    public void Plan_ParentKeyAbsent_WithoutTheSwitch_ShowsTheRefusal_AndFails()
    {
        Result result = Run(["-Plan", "-DumpFolder", $"'{Q(Path.Combine(_root, "d"))}'"], elevated: false, parent: false);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("ABSENT: REFUSED without -CreateParentKey", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.False(result.ParentPresent);
    }

    [PwshFact]
    public void Plan_ParentKeyAbsent_WithTheSwitch_SaysWhatCreatingItMeans_AndWritesNothing()
    {
        Result result = Run(["-Plan", "-CreateParentKey", "-DumpFolder", $"'{Q(Path.Combine(_root, "d"))}'"], elevated: false, parent: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("is ABSENT: -CreateParentKey would create it and mark it", result.Flat, StringComparison.Ordinal);
        Assert.Contains("WER's global defaults then apply to EVERY program that crashes", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.False(result.ParentPresent);
    }

    [PwshFact]
    public void Plan_Unregister_ShowsWhichKeysItWouldRemoveAndWhichItSkips_AndWritesNothing()
    {
        Result result = Run(["-Plan", "-Unregister"], elevated: false, preset:
        [
            ("ArcaneCore.World.exe", Ours(("DumpType", "2"))),
            ("ArcaneCore.Realm.exe", new Dictionary<string, string> { ["DumpType"] = "1" }),
        ]);

        Assert.Equal(0, result.Code);
        Assert.StartsWith("PLAN register-crash-dumps -Unregister (nothing was changed)", result.Output, StringComparison.Ordinal);
        Assert.Contains($@"{Parent}\ArcaneCore.World.exe -> remove this key (created by this script)", result.Output, StringComparison.Ordinal);
        Assert.Contains($@"{Parent}\ArcaneCore.Realm.exe -> SKIP: exists but was not created by this script", result.Output, StringComparison.Ordinal);
        Assert.Contains($@"{Parent}\ArcaneCore.Gateway.exe -> absent: nothing to remove", result.Output, StringComparison.Ordinal);
        Assert.Contains($"{Parent} -> left in place: not created by this script", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.Equal(2, result.RegistryKeys.Count);
    }

    [PwshFact]
    public void Plan_Unregister_SaysWhenItWouldRemoveTheParentKeyToo()
    {
        Result result = Run(["-Plan", "-Unregister"], elevated: false, parentValues: Ours(), preset: [("ArcaneCore.World.exe", Ours())]);

        Assert.Equal(0, result.Code);
        Assert.Contains($"{Parent} -> remove this key (created by this script, and nothing else is left in it)", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
    }

    // ---- refusals -------------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void Register_WithoutFolderOrLiveHome_Refuses_AndNamesTheVariable()
    {
        Result result = Run([], elevated: true);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("ARCANECORE_LIVE_HOME", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
    }

    [PwshFact]
    public void Register_WhenNotElevated_Refuses_BeforeTheFolderOrTheRegistryIsTouched()
    {
        string folder = Path.Combine(_root, "dumps-notelevated");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: false);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("elevated", result.Output, StringComparison.Ordinal);
        Assert.Contains("never elevates itself", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.False(Directory.Exists(folder));
    }

    [PwshFact]
    public void Unregister_WhenNotElevated_Refuses_AndRemovesNothing()
    {
        Result result = Run(["-Unregister"], elevated: false, preset: [("ArcaneCore.World.exe", Ours())]);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("elevated", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.Single(result.RegistryKeys);
    }

    [PwshTheory]
    [InlineData("-DumpFolder relative\\dumps", "not an absolute path")]
    [InlineData("-Executables ..\\evil", "not a bare executable file name")]
    [InlineData("-Executables 'a.exe','b'", "not a bare executable file name")]
    [InlineData("-Status -Unregister", "cannot be combined")]
    [InlineData("-Unregister -CreateParentKey", "only applies when registering")]
    [InlineData("-DumpCount 0", "DumpCount")]
    [InlineData("-DumpType Huge", "Huge")]
    public void Register_RefusesBadParameters_BeforeWriting(string arguments, string message)
    {
        // Pass the folder explicitly unless the case sets one, so the failure is the one under test.
        string args = arguments.Contains("-DumpFolder", StringComparison.Ordinal) ? arguments : $"{arguments} -DumpFolder '{Q(Path.Combine(_root, "d"))}'";
        Result result = Run([args], elevated: true);

        Assert.NotEqual(0, result.Code);
        Assert.Contains(message, result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
    }

    [PwshFact]
    public void Register_RefusesToChangeAKeyItDidNotCreate_AndChangesNothingElse()
    {
        Result result = Run(["-DumpFolder", $"'{Q(Path.Combine(_root, "d"))}'"], elevated: true, preset:
        [
            ("ArcaneCore.Realm.exe", new Dictionary<string, string> { ["DumpFolder"] = "somebody-elses" }),
        ]);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("did not create", result.Flat, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.Realm.exe", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.Equal("somebody-elses", result.Values($@"{Parent}\ArcaneCore.Realm.exe")["DumpFolder"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "d")));
    }

    [PwshFact]
    public void Register_WithoutTheParentKey_Refuses_UnlessCreateParentKeyIsGiven()
    {
        string folder = Path.Combine(_root, "d");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: true, parent: false);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("-CreateParentKey", result.Flat, StringComparison.Ordinal);
        Assert.Contains("every program that crashes", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
        Assert.False(result.ParentPresent);
        Assert.False(Directory.Exists(folder));
    }

    // ---- elevated runs (against the in-memory registry) -----------------------------------------------------------------------------

    [PwshFact]
    public void Register_Elevated_WritesTheMarkerFirstThenTypedValues_ForEachExecutable_AndCreatesTheFolder_LeavingTheParentAlone()
    {
        string folder = Path.Combine(_root, "dumps-new");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'", "-DumpCount", "7"], elevated: true);

        Assert.Equal(0, result.Code);
        Assert.True(Directory.Exists(folder), "the dump folder was not created");
        Assert.Equal(3, result.RegistryKeys.Count);
        foreach (string exe in new[] { "ArcaneCore.World.exe", "ArcaneCore.Realm.exe", "ArcaneCore.Gateway.exe" })
        {
            string key = $@"{Parent}\{exe}";
            Dictionary<string, string> values = result.Values(key);
            Assert.Equal(folder, values["DumpFolder"]);
            Assert.Equal("2", values["DumpType"]);
            Assert.Equal("7", values["DumpCount"]);
            Assert.Equal(Marker, values["ArcaneCoreRegisteredBy"]);
            Assert.Contains($"Set-ItemProperty {key} DumpFolder [ExpandString]", result.RegistryLog);
            Assert.Contains($"Set-ItemProperty {key} DumpType [DWord]", result.RegistryLog);
            Assert.Contains($"Set-ItemProperty {key} DumpCount [DWord]", result.RegistryLog);
            Assert.True(
                result.RegistryLog.IndexOf($"Set-ItemProperty {key} ArcaneCoreRegisteredBy [String]") < result.RegistryLog.IndexOf($"Set-ItemProperty {key} DumpFolder [ExpandString]"),
                "the marker must be written before the other values");
        }

        Assert.DoesNotContain(result.RegistryLog, l => l == $"New-Item {Parent}" || l.StartsWith($"Set-ItemProperty {Parent} ", StringComparison.Ordinal));
        Assert.Empty(result.Values(Parent));
    }

    [PwshFact]
    public void Register_Elevated_WithCreateParentKey_CreatesAndMarksTheParent()
    {
        Result result = Run(["-CreateParentKey", "-DumpFolder", $"'{Q(Path.Combine(_root, "d"))}'"], elevated: true, parent: false);

        Assert.Equal(0, result.Code);
        Assert.True(result.ParentPresent);
        Assert.Equal(Marker, result.Values(Parent)["ArcaneCoreRegisteredBy"]);
        Assert.Equal(3, result.RegistryKeys.Count);
        Assert.True(result.RegistryLog.IndexOf($"New-Item {Parent}") < result.RegistryLog.IndexOf($@"New-Item {Parent}\ArcaneCore.World.exe"));
    }

    [PwshFact]
    public void Register_Elevated_UpdatesAKeyItCreatedBefore_AndMiniIsDumpTypeOne()
    {
        string folder = Path.Combine(_root, "dumps-upd");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'", "-DumpType", "Mini", "-Executables", "ArcaneCore.World.exe"], elevated: true, preset:
        [
            ("ArcaneCore.World.exe", Ours(("DumpType", "2"), ("DumpCount", "9"))),
        ]);

        Assert.Equal(0, result.Code);
        Dictionary<string, string> values = result.Values($@"{Parent}\ArcaneCore.World.exe");
        Assert.Equal("1", values["DumpType"]);
        Assert.Equal("5", values["DumpCount"]);
        Assert.DoesNotContain(result.RegistryLog, line => line.StartsWith("New-Item", StringComparison.Ordinal));
    }

    // ---- a failed write -------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void Register_WhenAWriteFailsOnTheSecondExecutable_RollsBackWhatThisRunCreated_AndReportsWhatWasCompleted()
    {
        string folder = Path.Combine(_root, "dumps-fail");
        Result result = Run(["-CreateParentKey", "-DumpFolder", $"'{Q(folder)}'"], elevated: true, parent: false, failSet: "*ArcaneCore.Realm.exe|DumpType");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("injected failure", result.Flat, StringComparison.Ordinal);
        Assert.Contains("Completed before the failure: ArcaneCore.World.exe.", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryKeys);
        Assert.False(result.ParentPresent, "the parent key this run created was not removed");
        Assert.False(Directory.Exists(folder), "the folder this run created was not removed");
        Assert.Contains($@"Remove-Item {Parent}\ArcaneCore.World.exe", result.RegistryLog);
        Assert.Contains($@"Remove-Item {Parent}\ArcaneCore.Realm.exe", result.RegistryLog);
    }

    [PwshFact]
    public void Register_WhenTheRollbackCannotRemoveEither_TheLeftoverKeysCarryTheMarker_AndUnregisterCleansThem()
    {
        string folder = Path.Combine(_root, "dumps-fail2");
        Result failed = Run(["-CreateParentKey", "-DumpFolder", $"'{Q(folder)}'"], elevated: true, parent: false, failSet: "*ArcaneCore.Realm.exe|DumpType", failRemove: true);

        Assert.NotEqual(0, failed.Code);
        Assert.Contains("-Unregister", failed.Flat, StringComparison.Ordinal);
        Assert.True(failed.ParentPresent);
        Assert.Equal(Marker, failed.Values(Parent)["ArcaneCoreRegisteredBy"]);
        Assert.Equal(Marker, failed.Values($@"{Parent}\ArcaneCore.World.exe")["ArcaneCoreRegisteredBy"]);
        Assert.Equal(Marker, failed.Values($@"{Parent}\ArcaneCore.Realm.exe")["ArcaneCoreRegisteredBy"]);

        // The same state as a fresh registry (a separate process): -Unregister removes both keys and the parent.
        Result cleaned = Run(["-Unregister"], elevated: true, parentValues: Ours(), preset:
        [
            ("ArcaneCore.World.exe", Ours()),
            ("ArcaneCore.Realm.exe", Ours()),
        ]);
        Assert.Equal(0, cleaned.Code);
        Assert.Empty(cleaned.RegistryKeys);
        Assert.False(cleaned.ParentPresent);
    }

    [PwshFact]
    public void Register_WhenAWriteFails_NeverDeletesAFolderThatExistedBefore_NorItsDumps()
    {
        string folder = Path.Combine(_root, "dumps-existing");
        Directory.CreateDirectory(folder);
        string dump = Path.Combine(folder, "old.dmp");
        File.WriteAllText(dump, "dump");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: true, failSet: "*ArcaneCore.Realm.exe|DumpType");

        Assert.NotEqual(0, result.Code);
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(dump));
        Assert.Empty(result.RegistryKeys);
    }

    [PwshFact]
    public void Register_WhenAWriteFails_LeavesAFolderItCreatedIfItHoldsAFile_AndSaysSo()
    {
        string folder = Path.Combine(_root, "dumps-created");
        string dropped = Path.Combine(folder, "crash.dmp");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: true, failSet: "*ArcaneCore.Realm.exe|DumpType", dropOnFail: dropped);

        Assert.NotEqual(0, result.Code);
        Assert.True(File.Exists(dropped), "a non-empty folder created by the run was deleted");
        Assert.Contains("is not empty, so it was left in place", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.RegistryKeys);
    }

    [PwshFact]
    public void Register_WhenAWriteFails_DoesNotRemoveAKeyThatAlreadyCarriedTheMarker()
    {
        string folder = Path.Combine(_root, "dumps-keep");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: true, failSet: "*ArcaneCore.Realm.exe|DumpType", preset:
        [
            ("ArcaneCore.World.exe", Ours(("DumpType", "2"), ("DumpCount", "9"))),
        ]);

        Assert.NotEqual(0, result.Code);
        Assert.DoesNotContain($@"Remove-Item {Parent}\ArcaneCore.World.exe", result.RegistryLog);
        Assert.Equal([$@"{Parent}\ArcaneCore.World.exe"], result.RegistryKeys);
        Assert.Equal(Marker, result.Values($@"{Parent}\ArcaneCore.World.exe")["ArcaneCoreRegisteredBy"]);
    }

    [PwshFact]
    public void Register_WhenTheMarkerWriteAndTheRemovalBothFail_ReportsTheKeyAsUnmarked_AndDoesNotPromiseUnregister()
    {
        string folder = Path.Combine(_root, "dumps-unmarked");
        Result result = Run(["-DumpFolder", $"'{Q(folder)}'"], elevated: true, failSet: "*ArcaneCore.Realm.exe|ArcaneCoreRegisteredBy", failRemove: true);

        Assert.NotEqual(0, result.Code);
        Assert.Contains($@"UNMARKED, could not remove: {Parent}\ArcaneCore.Realm.exe.", result.Flat, StringComparison.Ordinal);
        Assert.Contains("delete them by hand", result.Flat, StringComparison.Ordinal);
        Assert.Contains($@"could not remove: {Parent}\ArcaneCore.World.exe. They carry the marker", result.Flat, StringComparison.Ordinal);
        Assert.DoesNotContain("Realm.exe. They carry the marker", result.Flat, StringComparison.Ordinal);
        Assert.Empty(result.Values($@"{Parent}\ArcaneCore.Realm.exe"));
    }

    [PwshFact]
    public void Register_WhenTheParentMarkerWriteAndTheRemovalBothFail_WarnsThatGlobalDumpsStayOn()
    {
        string folder = Path.Combine(_root, "dumps-unmarked-parent");
        Result result = Run(["-CreateParentKey", "-DumpFolder", $"'{Q(folder)}'"], elevated: true, parent: false, failSet: @"*\LocalDumps|ArcaneCoreRegisteredBy", failRemove: true);

        Assert.NotEqual(0, result.Code);
        Assert.Contains($"UNMARKED, could not remove: {Parent}.", result.Flat, StringComparison.Ordinal);
        Assert.Contains("leaves WER's global dumps switched on for every program that crashes", result.Flat, StringComparison.Ordinal);
        Assert.True(result.ParentPresent);
        Assert.Empty(result.Values(Parent));
    }

    [PwshFact]
    public void Status_IgnoresOnlyTheProvidersOwnProperties_NotEveryNameStartingWithPS()
    {
        Result result = Run(["-Status"], elevated: false, parentValues: new Dictionary<string, string> { ["PSCustomValue"] = "kept" });

        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("global PSCustomValue kept", result.Flat, StringComparison.Ordinal);
    }

    // ---- -Unregister ----------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void Unregister_Elevated_RemovesOnlyKeysItCreated_AndLeavesAParentItDidNotCreate()
    {
        Result result = Run(["-Unregister"], elevated: true, preset:
        [
            ("ArcaneCore.World.exe", Ours()),
            ("ArcaneCore.Realm.exe", new Dictionary<string, string> { ["DumpFolder"] = "somebody-elses" }),
        ]);

        Assert.Equal(0, result.Code);
        Assert.Contains($@"Remove-Item {Parent}\ArcaneCore.World.exe", result.RegistryLog);
        Assert.DoesNotContain(result.RegistryLog, line => line.Contains("ArcaneCore.Realm.exe", StringComparison.Ordinal));
        Assert.DoesNotContain($"Remove-Item {Parent}", result.RegistryLog);
        Assert.Equal([$@"{Parent}\ArcaneCore.Realm.exe"], result.RegistryKeys);
        Assert.True(result.ParentPresent);
    }

    [PwshFact]
    public void Unregister_Elevated_RemovesTheParentToo_WhenItCarriesTheMarkerAndNothingElseIsLeft()
    {
        Result result = Run(["-Unregister"], elevated: true, parentValues: Ours(), preset: [("ArcaneCore.World.exe", Ours())]);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.RegistryKeys);
        Assert.False(result.ParentPresent);
        Assert.Contains($"Remove-Item {Parent}", result.RegistryLog);
    }

    [PwshFact]
    public void Unregister_Elevated_KeepsAMarkedParent_WhenAnotherSubkeyRemains()
    {
        Result result = Run(["-Unregister"], elevated: true, parentValues: Ours(), preset:
        [
            ("ArcaneCore.World.exe", Ours()),
            ("Other.exe", new Dictionary<string, string> { ["DumpType"] = "1" }),
        ]);

        Assert.Equal(0, result.Code);
        Assert.Equal([$@"{Parent}\Other.exe"], result.RegistryKeys);
        Assert.True(result.ParentPresent);
        Assert.DoesNotContain($"Remove-Item {Parent}", result.RegistryLog);
    }

    [PwshFact]
    public void Unregister_Elevated_KeepsAMarkedParent_WhenItHoldsAnotherValue()
    {
        Result result = Run(["-Unregister"], elevated: true, parentValues: Ours(("DumpCount", "20")), preset: [("ArcaneCore.World.exe", Ours())]);

        Assert.Equal(0, result.Code);
        Assert.True(result.ParentPresent);
        Assert.Equal("20", result.Values(Parent)["DumpCount"]);
    }

    // ---- -Status --------------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void Status_IsReadOnly_AndShowsTheParentGlobalsAndTheCurrentValues_WithoutElevation()
    {
        Result result = Run(["-Status"], elevated: false, parentValues: new Dictionary<string, string> { ["DumpFolder"] = "global-folder", ["DumpType"] = "1", ["DumpCount"] = "10" }, preset:
        [
            ("ArcaneCore.World.exe", Ours(("DumpFolder", "X-folder"), ("DumpType", "2"), ("DumpCount", "5"))),
            ("ArcaneCore.Realm.exe", new Dictionary<string, string> { ["DumpType"] = "1" }),
        ]);

        Assert.Equal(0, result.Code);
        Assert.StartsWith("STATUS register-crash-dumps (read-only)", result.Output, StringComparison.Ordinal);
        Assert.Contains("global DumpFolder global-folder", result.Flat, StringComparison.Ordinal);
        Assert.Contains("global DumpType 1 (Mini)", result.Flat, StringComparison.Ordinal);
        Assert.Contains("global DumpCount 10", result.Flat, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.World.exe : registered by this script", result.Output, StringComparison.Ordinal);
        Assert.Contains("DumpFolder  X-folder", result.Output, StringComparison.Ordinal);
        Assert.Contains("DumpType    2 (Full)", result.Output, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.Realm.exe : present, NOT created by this script", result.Output, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.Gateway.exe : not registered", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
    }

    [PwshFact]
    public void Status_WithoutTheParentKey_SaysLocalDumpsIsNotConfigured()
    {
        Result result = Run(["-Status"], elevated: false, parent: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("absent: LocalDumps is not configured", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.RegistryLog);
    }

    // ---- source scan ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Script_UsesOnlyTheShimmedRegistryCmdlets_AndGatesItsTestHook()
    {
        string source = File.ReadAllText(Script);
        string[] forbidden =
        [
            @"New-ItemProperty", @"Remove-ItemProperty", @"Rename-ItemProperty", @"Copy-ItemProperty", @"Move-ItemProperty", @"Clear-ItemProperty",
            @"Microsoft\.Win32", @"\[Registry\]", @"\breg\.exe\b", @"\breg\s+(add|delete|import|export|save|query|copy|load|unload|restore)\b",
            @"Microsoft\.PowerShell\.Management\\", @"Registry::", @"HKCU", @"HKEY_", @"Start-Process", @"ExecutionPolicy", @"New-Item\s+-Path[^\r\n]*-Force",
            @"\bSet-Item\b", @"\bClear-Item\b", @"\bCopy-Item\b", @"\bMove-Item\b", @"\bRename-Item\b", @"New-PSDrive", @"Invoke-Expression", @"\biex\b",
            @"\.SetValue\(", @"\.CreateSubKey\(", @"\.DeleteSubKey", @"Remove-Item[^\r\n]*-Recurse",
        ];
        foreach (string pattern in forbidden)
        {
            Assert.False(Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase), $"the script contains a registry form the test shims cannot intercept: {pattern}");
        }

        // The elevation hook is honoured only behind an environment variable an operator's shell does not have.
        Assert.Matches(@"ARCANECORE_TEST_CRASHDUMPS_SHIM'\) -eq '1' -and \(Get-Command Test-CrashDumpElevatedShim", source);
    }

    // ---- harness --------------------------------------------------------------------------------------------------------------------

    private static string Q(string path) => path.Replace("'", "''");

    private Result Run(
        IReadOnlyList<string> arguments,
        bool elevated,
        string? liveHome = null,
        (string Exe, Dictionary<string, string> Values)[]? preset = null,
        bool parent = true,
        Dictionary<string, string>? parentValues = null,
        string? failSet = null,
        bool failRemove = false,
        string? dropOnFail = null)
    {
        var presetText = new StringBuilder();
        if (parent)
        {
            presetText.Append($"$global:Reg['{Q(Parent)}'] = [ordered]@{{ ");
            foreach ((string name, string value) in parentValues ?? [])
            {
                presetText.Append($"'{name}' = '{Q(value)}'; ");
            }

            presetText.AppendLine("}");
        }

        foreach ((string exe, Dictionary<string, string> values) in preset ?? [])
        {
            presetText.Append($"$global:Reg['{Q(Parent)}\\{exe}'] = [ordered]@{{ ");
            foreach ((string name, string value) in values)
            {
                presetText.Append($"'{name}' = '{Q(value)}'; ");
            }

            presetText.AppendLine("}");
        }

        string wrapper = Path.Combine(_root, "wrapper-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(wrapper, $$"""
            $ErrorActionPreference = 'Stop'
            $ErrorView = 'NormalView'
            $global:Reg = @{}
            $global:RegLog = [System.Collections.Generic.List[string]]::new()
            $global:ParentKey = '{{Q(Parent)}}'
            $global:AllowedRoot = '{{Q(_root)}}'
            $global:FailSet = '{{Q(failSet ?? "")}}'
            $global:FailRemove = ${{(failRemove ? "true" : "false")}}
            $global:DropOnFail = '{{Q(dropOnFail ?? "")}}'
            {{presetText}}
            function Test-CrashDumpElevatedShim { return ${{(elevated ? "true" : "false")}} }
            function Assert-Allowed([string]$p) { if (-not $p.StartsWith($global:AllowedRoot, [StringComparison]::Ordinal)) { throw "path outside the fake registry and the temporary directory: $p" } }
            function Test-Path { param([string]$LiteralPath, [string]$Path)
                $p = if ($LiteralPath) { $LiteralPath } else { $Path }
                if ($p -like 'HKLM:*') { return $global:Reg.ContainsKey($p) }
                Assert-Allowed $p
                Microsoft.PowerShell.Management\Test-Path @PSBoundParameters }
            function New-Item { param([string]$Path, [string]$ItemType, [switch]$Force)
                if ($Path -like 'HKLM:*') {
                    if ($Force) { throw 'New-Item -Force on a registry key' }
                    $par = $Path.Substring(0, $Path.LastIndexOf('\'))
                    if ($Path -ne $global:ParentKey -and -not $global:Reg.ContainsKey($par)) { throw "New-Item: the parent key does not exist: $par" }
                    $global:RegLog.Add("New-Item $Path"); $global:Reg[$Path] = [ordered]@{}; return }
                Assert-Allowed $Path
                Microsoft.PowerShell.Management\New-Item @PSBoundParameters }
            function Set-ItemProperty { param([string]$LiteralPath, [string]$Name, $Value, [string]$Type)
                if ($LiteralPath -notlike 'HKLM:*') { throw "Set-ItemProperty outside the fake registry: $LiteralPath" }
                if (-not $global:Reg.ContainsKey($LiteralPath)) { throw "Set-ItemProperty: no such key: $LiteralPath" }
                if ($global:FailSet -and ("$LiteralPath|$Name" -like $global:FailSet)) { if ($global:DropOnFail) { [IO.File]::WriteAllText($global:DropOnFail, 'x') }; throw 'injected failure' }
                $global:RegLog.Add("Set-ItemProperty $LiteralPath $Name [$Type]"); $global:Reg[$LiteralPath][$Name] = [string]$Value }
            function Get-ItemProperty { param([string]$LiteralPath)
                if ($LiteralPath -notlike 'HKLM:*') { throw "Get-ItemProperty outside the fake registry: $LiteralPath" }
                $o = [pscustomobject]$global:Reg[$LiteralPath]
                $o | Add-Member -NotePropertyName PSPath -NotePropertyValue $LiteralPath -PassThru }
            function Get-ChildItem { param([string]$LiteralPath)
                if ($LiteralPath -notlike 'HKLM:*') { throw "Get-ChildItem outside the fake registry: $LiteralPath" }
                foreach ($k in @($global:Reg.Keys)) { if ($k.StartsWith("$LiteralPath\", [StringComparison]::Ordinal)) { [pscustomobject]@{ PSChildName = $k.Substring($LiteralPath.Length + 1) } } } }
            function Remove-Item { param([string]$LiteralPath, [switch]$Force, [switch]$Recurse)
                if ($LiteralPath -like 'HKLM:*') {
                    if ($global:FailRemove) { throw 'injected remove failure' }
                    if (@($global:Reg.Keys | Where-Object { $_.StartsWith("$LiteralPath\", [StringComparison]::Ordinal) }).Count -gt 0) { throw "Remove-Item: the key has subkeys: $LiteralPath" }
                    $global:RegLog.Add("Remove-Item $LiteralPath"); $global:Reg.Remove($LiteralPath); return }
                Assert-Allowed $LiteralPath
                Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters }
            try {
                & '{{Q(Script)}}' {{string.Join(' ', arguments)}}
            } finally {
                foreach ($line in $global:RegLog) { "REGLOG $line" }
                foreach ($key in $global:Reg.Keys) {
                    "REGKEY $key"
                    foreach ($name in $global:Reg[$key].Keys) { "REGVAL $key|$name|$($global:Reg[$key][$name])" }
                }
            }
            """, new UTF8Encoding(false));

        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(wrapper);
        start.Environment.Remove("ARCANECORE_LIVE_HOME");
        start.Environment["ARCANECORE_TEST_CRASHDUMPS_SHIM"] = "1";
        if (liveHome is not null)
        {
            start.Environment["ARCANECORE_LIVE_HOME"] = liveHome;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pwsh did not finish within 120 s");
        }

        return new Result(process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed class Result(int code, string output)
    {
        private readonly string[] _lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        public int Code { get; } = code;

        public string Output { get; } = output;

        /// <summary>Output with every white-space run (including PowerShell's error-wrapping line breaks) folded to one space.</summary>
        public string Flat { get; } = Regex.Replace(output, @"\s+", " ");

        public List<string> RegistryLog => [.. _lines.Where(l => l.StartsWith("REGLOG ", StringComparison.Ordinal)).Select(l => l["REGLOG ".Length..])];

        /// <summary>The per-executable keys left in the in-memory registry (the LocalDumps parent key is reported by <see cref="ParentPresent"/>).</summary>
        public List<string> RegistryKeys => [.. AllKeys.Where(k => k != Parent)];

        public bool ParentPresent => AllKeys.Contains(Parent);

        private IEnumerable<string> AllKeys => _lines.Where(l => l.StartsWith("REGKEY ", StringComparison.Ordinal)).Select(l => l["REGKEY ".Length..]);

        public Dictionary<string, string> Values(string key)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string prefix = "REGVAL " + key + "|";
            foreach (string line in _lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string[] parts = line[prefix.Length..].Split('|', 2);
                result[parts[0]] = parts[1];
            }

            return result;
        }
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (!PwshProbe.Available)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private sealed class PwshTheoryAttribute : TheoryAttribute
    {
        public PwshTheoryAttribute()
        {
            if (!PwshProbe.Available)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private static class PwshProbe
    {
        public static readonly bool Available = Probe();

        private static bool Probe()
        {
            try
            {
                var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process process = Process.Start(start)!;
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && int.TryParse(text, out int major) && major >= 7;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
