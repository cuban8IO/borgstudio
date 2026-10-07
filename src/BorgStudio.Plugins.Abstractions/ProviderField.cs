namespace BorgStudio.Plugins;

/// <summary>How a <see cref="ProviderField"/> is edited.</summary>
public enum ProviderFieldKind
{
    /// <summary>Single line of text.</summary>
    Text,

    /// <summary>Whole number, e.g. a port.</summary>
    Number,

    /// <summary>A folder on this computer, with a folder picker.</summary>
    FolderPath,

    /// <summary>One of <see cref="ProviderField.Options"/>, picked from a list.</summary>
    Choice,
}

/// <summary>One input a <see cref="IRepositoryProvider"/> needs from the user.</summary>
/// <param name="Key">Key of the value in the dictionaries passed to the provider. Stable, never shown.</param>
/// <param name="Label">Label shown next to the input.</param>
/// <param name="Kind">How the value is edited.</param>
public sealed record ProviderField(string Key, string Label, ProviderFieldKind Kind = ProviderFieldKind.Text)
{
    /// <summary>Whether the user has to enter a value.</summary>
    public bool Required { get; init; } = true;

    /// <summary>
    /// Value pre-filled for new repositories. For <see cref="ProviderFieldKind.Choice"/> the
    /// <see cref="ProviderFieldOption.Value"/> of an option; without one, the first option is pre-selected.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>Short help text or example shown with the input.</summary>
    public string? Hint { get; init; }

    /// <summary>The options of a <see cref="ProviderFieldKind.Choice"/> field, in display order.</summary>
    public IReadOnlyList<ProviderFieldOption> Options { get; init; } = [];
}

/// <summary>One option of a <see cref="ProviderFieldKind.Choice"/> field.</summary>
/// <param name="Value">What the provider gets as the field's value. Stable, never shown.</param>
/// <param name="Label">What the user sees.</param>
public sealed record ProviderFieldOption(string Value, string Label);
