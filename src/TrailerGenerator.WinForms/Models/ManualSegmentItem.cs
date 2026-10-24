namespace TrailerGenerator.WinForms.Models;

/// <summary>
/// Represents a manual segment slice for trailer generation.
/// </summary>
public sealed class ManualSegmentItem
{
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; } = 5.0;
    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>
    /// Creates a shallow clone of this segment item.
    /// </summary>
    public ManualSegmentItem Clone() => new()
    {
        StartSeconds = StartSeconds,
        DurationSeconds = DurationSeconds
    };
}
