using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AxePower.Core.Services.Uninstaller.Leftovers;

/// <summary>Reads the target of a .lnk file through the shell's IShellLinkW.</summary>
public static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private const uint SlgpRawPath = 0x4;
    private const int StgmRead = 0;

    /// <summary>The shortcut's target path with environment variables expanded, or null.</summary>
    public static string? GetTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnkPath, StgmRead);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString().Trim());
            return target.Length == 0 ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
        }
    }
}
