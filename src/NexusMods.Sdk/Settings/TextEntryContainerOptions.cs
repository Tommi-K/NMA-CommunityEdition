using JetBrains.Annotations;

namespace NexusMods.Sdk.Settings;

/// <summary>
/// Options for a setting that is edited as free text.
/// </summary>
[PublicAPI]
public class TextEntryContainerOptions : IContainerOptions
{
    /// <summary>
    /// Text shown while the value is empty.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Whether the value is edited as multiple lines.
    /// </summary>
    public bool IsMultiLine { get; init; }

    /// <summary>
    /// Width of the text box in device independent pixels.
    /// </summary>
    public double ControlWidth { get; init; } = 320;

    /// <summary>
    /// Maximum height of the text box in device independent pixels, only used
    /// when <see cref="IsMultiLine"/> is set.
    /// </summary>
    public double MaxControlHeight { get; init; } = 180;
}
