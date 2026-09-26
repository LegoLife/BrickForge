namespace BrickForge.Core;

/// <summary>The ground the build sits on, measured in studs.</summary>
public sealed record Baseplate
{
    public const int MinSize = 8;
    public const int MaxSize = 128;

    public Baseplate(int widthStuds, int depthStuds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(widthStuds, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(widthStuds, MaxSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(depthStuds, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depthStuds, MaxSize);
        (WidthStuds, DepthStuds) = (widthStuds, depthStuds);
    }

    public int WidthStuds { get; }
    public int DepthStuds { get; }

    public static Baseplate Default { get; } = new(48, 48);

    public static bool IsValidSize(int studs) => studs is >= MinSize and <= MaxSize;

    public override string ToString() => $"{WidthStuds}×{DepthStuds}";
}
