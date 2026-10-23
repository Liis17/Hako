namespace Hako.Core;

public sealed record UserSettings(
    string? Language,
    string? JavaPath = null,
    string? JavaArguments = null,
    string? MinecraftArguments = null,
    bool? Fullscreen = null,
    int? WindowWidth = null,
    int? WindowHeight = null,
    int? MaximumMemoryMiB = null);
