namespace BrickForge.Core;

/// <summary>A part as it would be placed: shape, minimum corner, rotation and colour.</summary>
public sealed record PartPlacement(PartType Part, GridPos Position, Rotation Rotation, int ColorId);

/// <summary>A part within a <see cref="PartGroup"/>, positioned relative to the group's minimum corner.</summary>
public sealed record GroupPart(PartType Part, GridPos Offset, Rotation Rotation, int ColorId);

/// <summary>
/// Parts that are placed, rotated and moved together: the clipboard, a picked-up selection, or the
/// single part the user is about to place. Offsets are relative to the group's minimum corner, so
/// <see cref="At"/> takes the same kind of anchor as placing one part.
/// </summary>
public sealed class PartGroup
{
    private PartGroup(IReadOnlyList<GroupPart> parts)
    {
        Parts = parts;
        foreach (var p in parts)
        {
            var (sx, sz) = p.Part.Footprint(p.Rotation);
            SizeX = Math.Max(SizeX, p.Offset.X + sx);
            SizeY = Math.Max(SizeY, p.Offset.Y + p.Part.HeightPlates);
            SizeZ = Math.Max(SizeZ, p.Offset.Z + sz);
        }
    }

    /// <summary>In the order they were given, which <see cref="At"/> preserves.</summary>
    public IReadOnlyList<GroupPart> Parts { get; }

    public int SizeX { get; }
    /// <summary>Height in plates.</summary>
    public int SizeY { get; }
    public int SizeZ { get; }

    public static PartGroup Single(PartType part, Rotation rotation, int colorId) =>
        new([new GroupPart(part, new GridPos(0, 0, 0), rotation, colorId)]);

    /// <exception cref="ArgumentException">No parts given.</exception>
    public static PartGroup From(IEnumerable<PlacedPart> parts)
    {
        var list = parts.ToList();
        if (list.Count == 0) throw new ArgumentException("A group needs at least one part.", nameof(parts));

        var min = new GridPos(list.Min(p => p.Position.X), list.Min(p => p.Position.Y), list.Min(p => p.Position.Z));
        return new PartGroup(list
            .Select(p => new GroupPart(
                p.Part,
                new GridPos(p.Position.X - min.X, p.Position.Y - min.Y, p.Position.Z - min.Z),
                p.Rotation,
                p.ColorId))
            .ToList());
    }

    /// <summary>
    /// Turns the group about the vertical axis. One quarter turn maps the footprint's z axis onto x
    /// (a part at the far z end ends up at the near x end) and advances each part's own rotation.
    /// </summary>
    /// <param name="quarterTurns">Any integer; negative turns the other way.</param>
    public PartGroup Rotated(int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var group = this;
        for (var i = 0; i < turns; i++) group = group.QuarterTurn();
        return group;
    }

    /// <summary>The same group with every part in one colour.</summary>
    public PartGroup WithColor(int colorId) => new(Parts.Select(p => p with { ColorId = colorId }).ToList());

    public IEnumerable<PartPlacement> At(GridPos anchor) =>
        Parts.Select(p => new PartPlacement(
            p.Part,
            new GridPos(anchor.X + p.Offset.X, anchor.Y + p.Offset.Y, anchor.Z + p.Offset.Z),
            p.Rotation,
            p.ColorId));

    private PartGroup QuarterTurn() =>
        new(Parts
            .Select(p =>
            {
                var (_, sz) = p.Part.Footprint(p.Rotation);
                return p with
                {
                    Offset = new GridPos(SizeZ - (p.Offset.Z + sz), p.Offset.Y, p.Offset.X),
                    Rotation = (Rotation)(((int)p.Rotation + 1) % 4),
                };
            })
            .ToList());
}
