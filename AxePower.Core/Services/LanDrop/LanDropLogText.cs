namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// File names sent between the phone and the PC are the user's content, so the log describes a
/// file by its kind (".pdf") and size only.
/// </summary>
internal static class LanDropLogText
{
    public static string Kind(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        // A short, plain extension says what kind of file it was; anything else says nothing more.
        return extension.Length is > 1 and <= 8 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant()
            : "untyped";
    }
}
