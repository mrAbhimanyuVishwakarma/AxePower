namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// One signed piece of evidence tying a leftover to the removed application, e.g.
/// "points into the install folder" (+4) or "another app still uses this publisher" (−4).
/// A leftover's confidence score is derived from the sum of its records.
/// </summary>
public sealed class ConfidenceRecord
{
    public string Name { get; set; } = string.Empty;
    public int Points { get; set; }
    public string Description { get; set; } = string.Empty;

    public ConfidenceRecord() { }

    public ConfidenceRecord(string name, int points, string description)
    {
        Name = name;
        Points = points;
        Description = description;
    }

    public override string ToString() =>
        $"{(Points >= 0 ? "+" : string.Empty)}{Points} {Description}";
}
