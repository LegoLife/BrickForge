namespace BrickForge.Core;

public enum PartKind { Brick, Plate, Tile }

/// <summary>Yaw in 90° steps.</summary>
public enum Rotation { R0, R90, R180, R270 }

/// <summary>A rectangular part: <see cref="Width"/> studs along x, <see cref="Depth"/> along z when unrotated.</summary>
public sealed record PartType(string Id, string Name, PartKind Kind, int Width, int Depth)
{
    public int HeightPlates => Kind == PartKind.Brick ? LegoUnits.PlatesPerBrick : 1;

    /// <summary>Tiles are smooth on top, so nothing can attach to them from above.</summary>
    public bool HasStuds => Kind != PartKind.Tile;

    public (int SizeX, int SizeZ) Footprint(Rotation rotation) =>
        rotation is Rotation.R90 or Rotation.R270 ? (Depth, Width) : (Width, Depth);
}
