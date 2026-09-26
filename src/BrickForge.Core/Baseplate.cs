namespace BrickForge.Core;

/// <summary>The fixed ground the build sits on, measured in studs.</summary>
public sealed record Baseplate(int WidthStuds, int DepthStuds)
{
    public static Baseplate Default { get; } = new(48, 48);
}
