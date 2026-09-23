using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>The exit-code table (§6.6) and the command each kind runs, interactive and quiet.</summary>
public class ExitCodeAndCommandTests
{
    // ── Exit codes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(UninstallerKind.Unknown, 0, ExecutionVerdict.Finished)]
    [InlineData(UninstallerKind.Msi, 3010, ExecutionVerdict.FinishedRebootRequired)]
    [InlineData(UninstallerKind.Msi, 1641, ExecutionVerdict.FinishedRebootRequired)]
    [InlineData(UninstallerKind.BurnBundle, 3010, ExecutionVerdict.FinishedRebootRequired)]
    [InlineData(UninstallerKind.Msi, 1605, ExecutionVerdict.UnknownProduct)]
    [InlineData(UninstallerKind.Msi, 1602, ExecutionVerdict.Cancelled)]
    [InlineData(UninstallerKind.BurnBundle, 1602, ExecutionVerdict.Cancelled)]
    [InlineData(UninstallerKind.Msi, 1618, ExecutionVerdict.Busy)]
    [InlineData(UninstallerKind.Msi, 1603, ExecutionVerdict.Failed)]
    [InlineData(UninstallerKind.Nsis, 1, ExecutionVerdict.Cancelled)]
    [InlineData(UninstallerKind.Nsis, 2, ExecutionVerdict.Cancelled)]
    [InlineData(UninstallerKind.Nsis, 1627, ExecutionVerdict.Finished)]
    [InlineData(UninstallerKind.InnoSetup, -1073741510, ExecutionVerdict.Cancelled)]
    [InlineData(UninstallerKind.Unknown, 5, ExecutionVerdict.UnknownError)]
    [InlineData(UninstallerKind.InnoSetup, 1, ExecutionVerdict.UnknownError)]
    public void ExitCodes_MapToVerdicts(UninstallerKind kind, int code, ExecutionVerdict expected)
    {
        Assert.Equal(expected, ExitCodeInterpreter.Interpret(kind, code).Verdict);
    }

    [Fact]
    public void UnknownExitCode_StillFinishes()
    {
        Assert.Equal(ExecutionVerdict.Finished, ExitCodeInterpreter.Interpret(UninstallerKind.Nsis, null).Verdict);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private const string Pc = "{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}";

    private static InstalledApplication App(UninstallerKind kind, string exe, string args = "", string quiet = "") => new()
    {
        DisplayName = "App",
        UninstallerKind = kind,
        UninstallerPath = exe,
        UninstallerArguments = args,
        QuietUninstallString = quiet,
        ProductCode = kind == UninstallerKind.Msi ? Pc : string.Empty,
        PackageFullName = kind == UninstallerKind.StoreApp ? "Vendor.App_1.0.0.0_x64__abcdefghijklm" : string.Empty,
    };

    [Theory]
    [InlineData(UninstallerKind.BurnBundle, false, "/uninstall", false)]
    [InlineData(UninstallerKind.BurnBundle, true, "/uninstall /quiet /norestart", true)]
    [InlineData(UninstallerKind.InnoSetup, false, "", false)]
    [InlineData(UninstallerKind.InnoSetup, true, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", true)]
    [InlineData(UninstallerKind.Nsis, false, "/allusers", false)]
    [InlineData(UninstallerKind.Nsis, true, "/allusers /S", true)]
    [InlineData(UninstallerKind.Squirrel, false, "--uninstall", false)]
    [InlineData(UninstallerKind.Squirrel, true, "--uninstall -s", true)]
    [InlineData(UninstallerKind.Unknown, false, "/allusers", false)]
    [InlineData(UninstallerKind.Unknown, true, "/allusers", false)]
    [InlineData(UninstallerKind.InstallShield, true, "/allusers", false)]
    [InlineData(UninstallerKind.PowerShell, true, "/allusers", false)]
    public void ExeKinds_BuildTheExpectedCommand(UninstallerKind kind, bool preferQuiet, string expectedArgs, bool expectedQuiet)
    {
        var exe = @"C:\Program Files\App\uninstall.exe";
        var plan = UninstallCommandBuilder.Build(App(kind, exe, "/allusers"), preferQuiet, new FakeFileSystemProbe(exe));

        Assert.True(plan.CanRun);
        Assert.Equal(exe, plan.FileName);
        Assert.Equal(expectedArgs, plan.Arguments);
        Assert.Equal(expectedQuiet, plan.IsQuiet);
    }

    [Fact]
    public void Nsis_NeverGetsUnderscoreQuestionMark()
    {
        var exe = @"C:\Program Files\App\uninstall.exe";
        var plan = UninstallCommandBuilder.Build(App(UninstallerKind.Nsis, exe, "/S"), true, new FakeFileSystemProbe(exe));
        Assert.Equal("/S", plan.Arguments);
        Assert.DoesNotContain("_?=", plan.Arguments);
    }

    [Theory]
    [InlineData(false, "/qb")]
    [InlineData(true, "/qn")]
    public void Msi_UsesMsiexecWithTheProductCode(bool quiet, string ui)
    {
        var plan = UninstallCommandBuilder.Build(App(UninstallerKind.Msi, string.Empty), quiet, new FakeFileSystemProbe(), @"C:\logs\x.log");

        Assert.Equal(UninstallCommandBuilder.MsiExecPath, plan.FileName);
        Assert.Equal($"/x {Pc} {ui} /norestart /l*v \"C:\\logs\\x.log\"", plan.Arguments);
        Assert.Equal(quiet, plan.IsQuiet);
        Assert.Equal(@"C:\logs\x.log", plan.LogPath);
    }

    [Fact]
    public void Msi_WithInvalidProductCode_CannotRun()
    {
        var app = App(UninstallerKind.Msi, string.Empty);
        app.ProductCode = "{bad}";
        Assert.False(UninstallCommandBuilder.Build(app, false, new FakeFileSystemProbe()).CanRun);
    }

    [Fact]
    public void VendorQuietCommand_IsPreferredInQuietMode()
    {
        var exe = @"C:\Program Files\App\uninstall.exe";
        var app = App(UninstallerKind.Unknown, exe, quiet: $"\"{exe}\" /SILENT /VENDOR");

        var plan = UninstallCommandBuilder.Build(app, true, new FakeFileSystemProbe(exe));

        Assert.True(plan.IsQuiet);
        Assert.Equal("/SILENT /VENDOR", plan.Arguments);
        Assert.Contains("vendor command", plan.Description);
    }

    [Fact]
    public void MissingUninstaller_CannotRun()
    {
        var plan = UninstallCommandBuilder.Build(App(UninstallerKind.InnoSetup, @"C:\Gone\unins000.exe"), false, new FakeFileSystemProbe());
        Assert.False(plan.CanRun);
        Assert.NotNull(plan.Error);
    }

    [Fact]
    public void StoreApp_UsesRemoveAppxPackage()
    {
        var plan = UninstallCommandBuilder.Build(App(UninstallerKind.StoreApp, string.Empty), false, new FakeFileSystemProbe());
        Assert.Equal("Remove-AppxPackage -Package 'Vendor.App_1.0.0.0_x64__abcdefghijklm'", plan.Arguments);
    }

    [Fact]
    public void StoreApp_WithUnsafeName_CannotRun()
    {
        var app = App(UninstallerKind.StoreApp, string.Empty);
        app.PackageFullName = "x'; Remove-Item C:\\ -Recurse; '";
        Assert.False(UninstallCommandBuilder.Build(app, false, new FakeFileSystemProbe()).CanRun);
    }

    [Fact]
    public void BrokenEntry_HasNoCommand_AndSaysFilesAreNotTouched()
    {
        var plan = UninstallCommandBuilder.Build(App(UninstallerKind.BrokenEntry, string.Empty), false, new FakeFileSystemProbe());
        Assert.False(plan.CanRun);
        Assert.Contains("files are not touched", plan.Description);
    }

    [Theory]
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}", true)]
    [InlineData("{a1b2c3d4-e5f6-7890-abcd-ef1234567890}", true)]
    [InlineData("A1B2C3D4-E5F6-7890-ABCD-EF1234567890", false)]
    [InlineData("{A1B2C3D4-E5F6-7890-ABCD-EF123456789Z}", false)]
    public void ProductCodeExtraction_ValidatesTheGuid(string code, bool valid)
    {
        Assert.Equal(valid, UninstallerKindDetector.ExtractProductCode($"/X{code}").Length > 0);
    }
}
