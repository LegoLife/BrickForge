namespace BrickForge.Core;

/// <summary>A face of a part's body, named by the direction it faces.</summary>
public enum Face { Top, Bottom, MinX, MaxX, MinZ, MaxZ }

/// <summary>
/// The point under the cursor, in grid units: x and z in studs from the baseplate's corner, y in plates
/// above its surface. <see cref="PartId"/> is the part it lies on, or null for the baseplate.
/// </summary>
public readonly record struct PointerHit(double X, double Y, double Z, int? PartId);

/// <summary>
/// Where the ghost goes for what the cursor is over. On the baseplate, or on the top or underside of a
/// part, it is centred on the cursor and slid back onto the baseplate if it would hang off. On a side,
/// it sits flush against that face, level with the part's base, centred on the cursor along the face.
/// </summary>
public static class Aiming
{
    /// <summary>The ghost's minimum corner, or null when there is nowhere sensible to put it.</summary>
    /// <param name="sizeX">The ghost's extent in studs along x; <paramref name="sizeY"/> is in plates.</param>
    public static GridPos? Anchor(Build build, PointerHit hit, int sizeX, int sizeY, int sizeZ)
    {
        var plate = build.Baseplate;
        int? SlideX() => Slide(Centre(hit.X, sizeX), sizeX, plate.WidthStuds);
        int? SlideZ() => Slide(Centre(hit.Z, sizeZ), sizeZ, plate.DepthStuds);
        GridPos? Centred(int y) => y >= 0 && SlideX() is { } x && SlideZ() is { } z ? new GridPos(x, y, z) : null;

        if (hit.PartId is not { } id) return Centred(0);
        if (build.Find(id) is not { } target) return null;

        var t = target.Position;
        var (tx, tz) = target.Part.Footprint(target.Rotation);
        GridPos? Flush(int? x, int? z) =>
            x is { } fx && z is { } fz && fx >= 0 && fz >= 0 &&
            fx + sizeX <= plate.WidthStuds && fz + sizeZ <= plate.DepthStuds
                ? new GridPos(fx, t.Y, fz)
                : null;

        return FaceAt(target, hit.X, hit.Y, hit.Z) switch
        {
            Face.Top => Centred(t.Y + target.Part.HeightPlates),
            Face.Bottom => Centred(t.Y - sizeY),
            Face.MinX => Flush(t.X - sizeX, SlideZ()),
            Face.MaxX => Flush(t.X + tx, SlideZ()),
            Face.MinZ => Flush(SlideX(), t.Z - sizeZ),
            _ => Flush(SlideX(), t.Z + tz),
        };
    }

    /// <summary>The face of <paramref name="part"/>'s body nearest the point (x, z in studs, y in plates).</summary>
    public static Face FaceAt(PlacedPart part, double x, double y, double z)
    {
        var p = part.Position;
        var (sizeX, sizeZ) = part.Part.Footprint(part.Rotation);
        var inset = LegoUnits.PartInset; // the drawn body is trimmed by this on every side
        // Heights in plates are converted to studs, so all six distances are in the same unit.
        (Face Face, double Distance)[] faces =
        [
            (Face.Top, Math.Abs(y - (p.Y + part.Part.HeightPlates)) * LegoUnits.PlateHeight),
            (Face.Bottom, Math.Abs(y - p.Y) * LegoUnits.PlateHeight),
            (Face.MinX, Math.Abs(x - (p.X + inset))),
            (Face.MaxX, Math.Abs(x - (p.X + sizeX - inset))),
            (Face.MinZ, Math.Abs(z - (p.Z + inset))),
            (Face.MaxZ, Math.Abs(z - (p.Z + sizeZ - inset))),
        ];
        return faces.MinBy(f => f.Distance).Face; // ties go to the first listed: top and bottom win on edges
    }

    /// <summary>
    /// The minimum corner that centres <paramref name="size"/> studs on the cursor: an odd size on the stud
    /// under it, an even size on the nearest line between studs.
    /// </summary>
    private static int Centre(double cursor, int size) => (int)Math.Floor(cursor - size / 2.0 + 0.5);

    private static int? Slide(int anchor, int size, int span) => size > span ? null : Math.Clamp(anchor, 0, span - size);
}
