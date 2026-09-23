using System.IO;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// The parser never resolves a relative path against the working directory; bare names go
/// through System32, the Windows folder and PATH (D1).
/// </summary>
public class UninstallCommandParserTests
{
    private static readonly string MsiExec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    [Fact]
    public void BareMsiExec_ResolvesToSystem32()
    {
        Assert.True(UninstallCommandParser.TryParse("MsiExec.exe /X{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}", new FakeFileSystemProbe(), out var cmd));

        Assert.Equal(MsiExec, cmd.FileName, ignoreCase: true);
        Assert.Equal("/X{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}", cmd.Arguments);
        Assert.True(cmd.FileExists);
    }

    [Fact]
    public void LowercaseMsiExecWithSpaceAndProperty_KeepsArguments()
    {
        Assert.True(UninstallCommandParser.TryParse(
            "msiexec.exe /x {2BC23EF7-7EF5-317D-9967-2AE583956171} AI_UNINSTALLER_CTP=1", new FakeFileSystemProbe(), out var cmd));

        Assert.Equal(MsiExec, cmd.FileName, ignoreCase: true);
        Assert.Equal("/x {2BC23EF7-7EF5-317D-9967-2AE583956171} AI_UNINSTALLER_CTP=1", cmd.Arguments);
    }

    [Fact]
    public void BareNameWithoutExtension_GetsExeAppended()
    {
        Assert.True(UninstallCommandParser.TryParse("msiexec /x {2BC23EF7-7EF5-317D-9967-2AE583956171}", new FakeFileSystemProbe(), out var cmd));
        Assert.Equal(MsiExec, cmd.FileName, ignoreCase: true);
        Assert.True(cmd.FileExists);
    }

    [Fact]
    public void QuotedPackageCachePath_SplitsAtClosingQuote()
    {
        var exe = @"C:\ProgramData\Package Cache\{042d26ef-3dbe-4c25-95d3-4c1b11b235a7}\vcredist_x64.exe";
        var fs = new FakeFileSystemProbe(exe);

        Assert.True(UninstallCommandParser.TryParse($"\"{exe}\"  /uninstall", fs, out var cmd));

        Assert.Equal(exe, cmd.FileName, ignoreCase: true);
        Assert.Equal("/uninstall", cmd.Arguments);
        Assert.True(cmd.FileExists);
    }

    [Fact]
    public void UnquotedPathWithSpaces_FindsTheExistingFile()
    {
        using var temp = new TempFolder();
        var exe = temp.CreateFile(@"Drive File Stream\1.0\uninstall.exe");

        Assert.True(UninstallCommandParser.TryParse($"{exe} --silent", new FileSystemProbe(), out var cmd));

        Assert.Equal(exe, cmd.FileName, ignoreCase: true);
        Assert.Equal("--silent", cmd.Arguments);
        Assert.True(cmd.FileExists);
    }

    [Fact]
    public void UnquotedPathWithSpaces_Missing_BestGuessIsTheExePrefix()
    {
        var command = @"C:\Program Files\Google\Drive File Stream\131.0.2.0\uninstall.exe";

        Assert.True(UninstallCommandParser.TryParse(command, new FakeFileSystemProbe(), out var cmd));

        Assert.Equal(command, cmd.FileName, ignoreCase: true);
        Assert.False(cmd.FileExists);
    }

    [Fact]
    public void EnvironmentVariables_AreExpanded()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var exe = Path.Combine(programFiles, "X", "uninst.exe");
        var fs = new FakeFileSystemProbe(exe);

        Assert.True(UninstallCommandParser.TryParse(@"%ProgramFiles%\X\uninst.exe /S", fs, out var cmd));

        Assert.Equal(exe, cmd.FileName, ignoreCase: true);
        Assert.Equal("/S", cmd.Arguments);
        Assert.True(cmd.FileExists);
    }

    [Fact]
    public void RunDll32WithMissingDll_IsNotAnExistingCommand()
    {
        Assert.True(UninstallCommandParser.TryParse(
            "RunDll32.exe \"C:\\DoesNotExist\\gone.dll\",Uninstall", new FakeFileSystemProbe(), out var cmd));

        Assert.EndsWith("rundll32.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\DoesNotExist\gone.dll", cmd.DllPath, ignoreCase: true);
        Assert.False(cmd.FileExists);
    }

    [Fact]
    public void RunDll32WithExistingDll_Exists()
    {
        var dll = @"C:\Program Files\NVIDIA Corporation\Installer2\InstallerCore\NVI2.DLL";
        var fs = new FakeFileSystemProbe(dll);

        Assert.True(UninstallCommandParser.TryParse(
            $"\"C:\\WINDOWS\\SysWOW64\\RunDll32.EXE\" \"{dll}\",UninstallPackage Display.NvApp", fs, out var cmd));

        Assert.True(cmd.FileExists);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    public void BareSystemTools_Resolve(string name)
    {
        Assert.True(UninstallCommandParser.TryParse($"{name} /c echo", new FakeFileSystemProbe(), out var cmd));
        Assert.True(Path.IsPathFullyQualified(cmd.FileName));
        Assert.True(cmd.FileExists);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCommands_AreRejected(string? raw)
    {
        Assert.False(UninstallCommandParser.TryParse(raw, new FakeFileSystemProbe(), out _));
    }

    [Fact]
    public void WorkingDirectory_NeverChangesTheResult()
    {
        using var temp = new TempFolder();
        // A decoy msiexec.exe in the working directory must not be picked up
        temp.CreateFile("MsiExec.exe");
        var original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = temp.Path;
            Assert.True(UninstallCommandParser.TryParse("MsiExec.exe /X{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}", new FileSystemProbe(), out var cmd));
            Assert.Equal(MsiExec, cmd.FileName, ignoreCase: true);

            Assert.True(UninstallCommandParser.TryParse("uninst.exe /S", new FileSystemProbe(), out var relative));
            Assert.False(relative.FileExists);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }
    }
}
