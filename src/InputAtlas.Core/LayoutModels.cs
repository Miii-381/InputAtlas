namespace InputAtlas.Core;

public sealed record KeyboardLayoutDefinition(
    string Id,
    string DisplayName,
    double WidthUnits,
    double HeightUnits,
    IReadOnlyList<KeyDefinition> Keys);

public sealed record KeyDefinition(
    InputId Input,
    string Label,
    string? SecondaryLabel,
    double X,
    double Y,
    double Width,
    double Height,
    bool Observable = true,
    string Group = "main");

