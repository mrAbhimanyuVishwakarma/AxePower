using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace AxePower.Core.Services;

/// <summary>
/// The exceptions each kind of best-effort Windows call is expected to throw, for catch filters:
/// <c>catch (Exception caught) when (ExpectedFailures.Registry(caught))</c>. Anything outside the set
/// (a null reference, a bad cast in our own code) is a defect and is not swallowed. See
/// docs/engineering/EXCEPTION_DEBT.md for when to use which.
/// </summary>
public static class ExpectedFailures
{
    /// <summary>Registry access: no rights, a key deleted underneath us, a damaged or unloaded hive.</summary>
    public static bool Registry(Exception e) =>
        e is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;

    /// <summary>File-system access: missing, locked, denied, too long, or on a drive that went away.</summary>
    public static bool FileSystem(Exception e) =>
        e is IOException or UnauthorizedAccessException or SecurityException;

    /// <summary>Inspecting or signalling another process, which may have exited or be protected.</summary>
    public static bool Process(Exception e) =>
        e is InvalidOperationException or Win32Exception or ArgumentException or NotSupportedException;

    /// <summary>Shell and COM automation (Task Scheduler, Windows Firewall, shell links, WMI).</summary>
    public static bool Com(Exception e) =>
        e is COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException
            or FileNotFoundException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidComObjectException;

    /// <summary>WMI queries and method calls: a class this PC does not have, no rights, a busy or broken provider.</summary>
    public static bool Wmi(Exception e) =>
        e is System.Management.ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>Turning untrusted text into a path.</summary>
    public static bool Path(Exception e) =>
        e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException;

    /// <summary>Parsing untrusted text or JSON.</summary>
    public static bool Parse(Exception e) =>
        e is FormatException or OverflowException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException;
}
