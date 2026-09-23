namespace AxePower.Core.Services.Uninstaller;

/// <summary>A registry uninstall command split into the program and its arguments.</summary>
/// <param name="Raw">The command as stored in the registry.</param>
/// <param name="FileName">Full path of the program when it could be resolved; otherwise the best guess.</param>
/// <param name="Arguments">Everything after the program, trimmed.</param>
/// <param name="FileExists">
/// True when the program exists and, for <c>rundll32</c>, the DLL it loads exists too.
/// </param>
public sealed record UninstallCommand(string Raw, string FileName, string Arguments, bool FileExists)
{
    /// <summary>For rundll32 commands: the DLL the command loads (empty otherwise).</summary>
    public string DllPath { get; init; } = string.Empty;

    /// <summary>The program's file name without its folder, e.g. "unins000.exe".</summary>
    public string ExeName => Path.GetFileName(FileName);
}

/// <summary>
/// Splits registry uninstall strings such as <c>MsiExec.exe /X{…}</c>,
/// <c>"C:\Program Files\App\uninst.exe" /S</c> or an unquoted path with spaces into the program
/// and its arguments. It never checks a relative path against the current directory: bare names
/// are resolved through System32, the Windows folder and PATH.
/// </summary>
public static class UninstallCommandParser
{
    public static bool TryParse(string? raw, IFileSystemProbe fs, out UninstallCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (text.Length == 0) return false;

        var (fileName, arguments, exists) = text[0] == '"'
            ? ParseQuoted(text, fs)
            : ParseUnquoted(text, fs);

        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var result = new UninstallCommand(raw, fileName, arguments, exists);

        if (IsRunDll32(fileName))
        {
            var dll = ResolveRunDllTarget(arguments, fs);
            result = result with
            {
                DllPath = dll ?? string.Empty,
                FileExists = exists && dll is not null && fs.FileExists(dll),
            };
        }

        command = result;
        return true;
    }

    /// <summary>Resolves a bare program name (no folder) through System32, Windows and PATH.</summary>
    public static string? ResolveBareName(string name, IFileSystemProbe fs)
    {
        if (string.IsNullOrWhiteSpace(name) || !IsBareName(name)) return null;

        string[] candidates = Path.HasExtension(name) ? [name] : [name + ".exe", name];

        foreach (var dir in SearchDirectories())
        {
            foreach (var candidate in candidates)
            {
                string full;
                try { full = Path.Combine(dir, candidate); }
                catch { continue; }
                if (fs.FileExists(full)) return full;
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static (string FileName, string Arguments, bool Exists) ParseQuoted(string text, IFileSystemProbe fs)
    {
        var end = text.IndexOf('"', 1);
        var fileName = (end > 0 ? text[1..end] : text[1..]).Trim();
        var arguments = end > 0 && end + 1 < text.Length ? text[(end + 1)..].Trim() : string.Empty;

        if (IsBareName(fileName))
        {
            var resolved = ResolveBareName(fileName, fs);
            return resolved is null ? (fileName, arguments, false) : (resolved, arguments, true);
        }

        var normalized = Normalize(fileName);
        return (normalized, arguments, fs.FileExists(normalized));
    }

    private static (string FileName, string Arguments, bool Exists) ParseUnquoted(string text, IFileSystemProbe fs)
    {
        var tokens = text.Split(' ');

        // An unquoted path may contain spaces: grow the prefix until it names an existing file.
        if (Path.IsPathFullyQualified(tokens[0]) || tokens[0].Length >= 2 && tokens[0][1] == ':')
        {
            for (int i = 1; i <= tokens.Length; i++)
            {
                var prefix = string.Join(' ', tokens, 0, i).TrimEnd();
                if (prefix.Length == 0 || !Path.IsPathFullyQualified(prefix)) continue;

                var arguments = string.Join(' ', tokens, i, tokens.Length - i).Trim();
                if (fs.FileExists(prefix)) return (Normalize(prefix), arguments, true);
                if (!Path.HasExtension(prefix) && fs.FileExists(prefix + ".exe"))
                    return (Normalize(prefix + ".exe"), arguments, true);
            }
        }
        else if (IsBareName(tokens[0]))
        {
            var resolved = ResolveBareName(tokens[0], fs);
            var arguments = string.Join(' ', tokens, 1, tokens.Length - 1).Trim();
            if (resolved is not null) return (resolved, arguments, true);
            return (tokens[0], arguments, false);
        }

        // Nothing exists: best guess is the first ".exe"-terminated prefix, else the first token.
        var exeEnd = FindExeEnd(text);
        if (exeEnd > 0)
            return (Normalize(text[..exeEnd]), text[exeEnd..].Trim(), false);

        return (tokens[0], string.Join(' ', tokens, 1, tokens.Length - 1).Trim(), false);
    }

    /// <summary>Index just past the first ".exe" that ends a token, or -1.</summary>
    private static int FindExeEnd(string text)
    {
        var index = 0;
        while ((index = text.IndexOf(".exe", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + 4;
            if (end == text.Length || char.IsWhiteSpace(text[end])) return end;
            index = end;
        }
        return -1;
    }

    /// <summary>The DLL from <c>"C:\x\y.dll",Entry args</c> or <c>y.dll,Entry</c>; null if absent.</summary>
    private static string? ResolveRunDllTarget(string arguments, IFileSystemProbe fs)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var args = arguments.Trim();

        string dll;
        if (args[0] == '"')
        {
            var end = args.IndexOf('"', 1);
            dll = end > 0 ? args[1..end] : args[1..];
        }
        else
        {
            var comma = args.IndexOf(',');
            var space = args.IndexOf(' ');
            var cut = comma >= 0 && (space < 0 || comma < space) ? comma : space;
            dll = cut >= 0 ? args[..cut] : args;
        }

        dll = dll.Trim().TrimEnd(',');
        if (dll.Length == 0) return null;
        if (IsBareName(dll)) return ResolveBareName(dll, fs) ?? dll;
        return Path.IsPathFullyQualified(dll) ? Normalize(dll) : null;
    }

    private static bool IsRunDll32(string fileName) =>
        Path.GetFileName(fileName).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsBareName(string name) =>
        name.IndexOfAny(['\\', '/', ':']) < 0;

    private static IEnumerable<string> SearchDirectories()
    {
        yield return Environment.SystemDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
            if (Path.IsPathFullyQualified(dir)) yield return dir;
        }
    }

    /// <summary>Collapses doubled separators and "..", keeping the path's casing.</summary>
    private static string Normalize(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path; }
        catch { return path; }
    }
}
