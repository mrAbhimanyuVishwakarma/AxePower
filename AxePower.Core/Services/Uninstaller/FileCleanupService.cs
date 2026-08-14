using System.Runtime.InteropServices;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Deletes leftover files and folders, preferring Recycle Bin when enabled.
/// Uses SHFileOperation (Windows Shell) for Recycle Bin support.
/// </summary>
public sealed class FileCleanupService : IFileCleanupService
{
    public async Task<bool> DeleteFileAsync(string path, UninstallerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return true; // Already gone — treat as success

        if (settings.DryRunMode)
            return true; // Dry run — no actual deletion

        return await Task.Run(() =>
        {
            try
            {
                if (settings.MoveToRecycleBin)
                    return SendToRecycleBin(path);
                else
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> DeleteFolderAsync(string path, UninstallerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return true; // Already gone

        if (settings.DryRunMode)
            return true;

        return await Task.Run(() =>
        {
            try
            {
                if (settings.MoveToRecycleBin)
                    return SendToRecycleBin(path);
                else
                {
                    Directory.Delete(path, recursive: true);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Windows Shell Recycle Bin
    // ─────────────────────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE   = 0x0003;
    private const ushort FOF_ALLOWUNDO   = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT      = 0x0004;
    private const ushort FOF_NOERRORUI   = 0x0400;

    private static bool SendToRecycleBin(string path)
    {
        try
        {
            // SHFileOperation requires double-null terminated string
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0',
                pTo = null!,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            int result = SHFileOperation(ref op);
            return result == 0;
        }
        catch
        {
            // Fall back to permanent deletion if Shell operation fails
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
                return true;
            }
            catch { return false; }
        }
    }
}
