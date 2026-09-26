namespace BrickForge.Core;

/// <summary>
/// Real LEGO proportions. The grid is integer studs (x, z) by integer plate heights (y);
/// these constants convert to world units, where 1 unit = 1 stud pitch (8mm).
/// </summary>
public static class LegoUnits
{
    public const double StudPitchMm = 8.0;
    public const double PlateHeightMm = 3.2;
    public const int PlatesPerBrick = 3;

    public const double PlateHeight = PlateHeightMm / StudPitchMm; // 0.4
    public const double StudDiameter = 4.8 / StudPitchMm;          // 0.6
    public const double StudHeight = 1.7 / StudPitchMm;            // ~0.21

    /// <summary>Gap inset on each side of a part so neighbouring pieces read as separate.</summary>
    public const double PartInset = 0.1 / StudPitchMm;
}
