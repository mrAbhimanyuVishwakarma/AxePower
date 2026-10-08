using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AxePower.Core.Models.Skins;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services.Skins;

public sealed record SkinEntry(SkinDefinition Definition, CompiledSkin Compiled, string Source, bool IsBuiltIn)
{
    public string Id => Compiled.Id;
    public string Name => Compiled.Name;
}

/// <summary>
/// Every skin AxePower can apply. The built-in dark, light and glass skins are embedded in the
/// assembly, so a theme always exists. More are read from <c>skins\&lt;id&gt;\skin.json</c> next
/// to AxePower.exe (shipped) and in <c>%APPDATA%\AxePower\Skins</c> (the user's own). A folder
/// whose name starts with "_" (such as _template) is ignored. A skin that fails to load is
/// skipped and its reason kept in <see cref="Errors"/>.
/// </summary>
public sealed class SkinCatalog
{
    public const string DefaultId = "dark";
    public static readonly string[] BuiltInIds = { "dark", "light", "glass" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly LogService? _log;
    private readonly IReadOnlyList<string> _folders;
    private List<SkinEntry> _skins = new();
    private List<string> _errors = new();

    public SkinCatalog(LogService? log = null) : this(log, DefaultFolders()) { }

    internal SkinCatalog(LogService? log, IReadOnlyList<string> folders)
    {
        _log = log;
        _folders = folders;
        Reload();
    }

    public static string UserSkinsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower", "Skins");

    public static string ShippedSkinsFolder => Path.Combine(AppContext.BaseDirectory, "skins");

    public IReadOnlyList<SkinEntry> Skins => _skins;
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Folders => _folders;

    public SkinEntry? Find(string? id) =>
        id == null ? null : _skins.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The skin to show for an id, falling back to the default when it has gone.</summary>
    public SkinEntry Resolve(string? id) => Find(id) ?? Find(DefaultId)!;

    /// <summary>Re-reads the folders, so a skin dropped in while AxePower runs shows up.</summary>
    public void Reload()
    {
        var skins = new List<SkinEntry>();
        var errors = new List<string>();

        var builtIns = BuiltInIds.ToDictionary(id => id, LoadEmbedded);
        foreach (var id in BuiltInIds)
            skins.Add(new SkinEntry(builtIns[id], SkinCompiler.Compile(builtIns[id]), "built-in", true));

        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(dir).StartsWith('_')) continue;
                var file = Path.Combine(dir, "skin.json");
                if (!File.Exists(file)) continue;

                try
                {
                    var def = Read(File.ReadAllText(file), file);
                    // The shipped folder carries copies of the built-ins; the embedded ones win
                    if (skins.Any(s => string.Equals(s.Id, def.Id, StringComparison.OrdinalIgnoreCase))) continue;

                    var baseSkin = builtIns[def.Base.Trim().ToLowerInvariant() == "light" ? "light" : "dark"];
                    skins.Add(new SkinEntry(def, SkinCompiler.Compile(def, baseSkin), file, false));
                }
                catch (Exception ex) when (ex is SkinException or JsonException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: {ex.Message}");
                    _log?.LogWarning($"Skin skipped: {file}: {ex.Message}");
                }
            }
        }

        _skins = skins;
        _errors = errors;
    }

    public static SkinDefinition Read(string json, string source)
    {
        var skin = JsonSerializer.Deserialize<SkinDefinition>(json, Json) ?? throw new SkinException($"{source} is empty.");
        // "colors": null and the like fall back to the defaults; the compiler then reports what is missing.
        var repair = JsonNullRepair.Repair(skin);
        if (!repair.IsComplete) throw new SkinException($"{source} is incomplete ({repair.Describe()}).");
        return skin;
    }

    private static SkinDefinition LoadEmbedded(string id)
    {
        var name = $"AxePower.Skins.{id}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Built-in skin {name} is not embedded.");
        using var reader = new StreamReader(stream);
        return Read(reader.ReadToEnd(), name);
    }

    private static IReadOnlyList<string> DefaultFolders() => new[] { ShippedSkinsFolder, UserSkinsFolder };
}
