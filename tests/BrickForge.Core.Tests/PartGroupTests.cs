namespace BrickForge.Core.Tests;

public class PartGroupTests
{
    private static readonly PartType Brick2x4 = PartCatalog.Get("brick-2x4");
    private static readonly PartType Brick1x1 = PartCatalog.Get("brick-1x1");
    private static readonly PartType Plate2x2 = PartCatalog.Get("plate-2x2");

    private static PlacedPart At(int id, PartType part, int x, int y, int z, Rotation r = Rotation.R0, int color = 0) =>
        new(id, part, new GridPos(x, y, z), r, color);

    [Fact]
    public void Offsets_are_relative_to_the_groups_minimum_corner()
    {
        var group = PartGroup.From([At(1, Brick2x4, 5, 3, 7), At(2, Brick1x1, 6, 6, 9)]);

        Assert.Equal([new GridPos(0, 0, 0), new GridPos(1, 3, 2)], group.Parts.Select(p => p.Offset));
    }

    [Fact]
    public void Size_covers_every_part()
    {
        var group = PartGroup.From([At(1, Brick2x4, 5, 3, 7), At(2, Plate2x2, 6, 6, 10)]);

        // x: 5..8 (the plate at x=6 is 2 wide), y: 3..7 (plate on top ends at 7), z: 7..12
        Assert.Equal((3, 4, 5), (group.SizeX, group.SizeY, group.SizeZ));
    }

    [Fact]
    public void Rotating_turns_the_footprint_and_each_part()
    {
        // A 2x4 brick with a 1x1 at its far-z end: 2 wide (x), 4 deep (z).
        var group = PartGroup.From([At(1, Brick2x4, 0, 0, 0), At(2, Brick1x1, 0, 3, 3)]);

        var turned = group.Rotated(1);

        Assert.Equal((4, 2), (turned.SizeX, turned.SizeZ));
        Assert.Equal(Rotation.R90, turned.Parts[0].Rotation);
        Assert.Equal(new GridPos(0, 0, 0), turned.Parts[0].Offset);
        Assert.Equal(new GridPos(0, 3, 0), turned.Parts[1].Offset); // far z end becomes near x end
    }

    [Fact]
    public void Four_quarter_turns_are_the_identity()
    {
        var group = PartGroup.From([At(1, Brick2x4, 0, 0, 0), At(2, Brick1x1, 1, 3, 3, Rotation.R90)]);

        Assert.Equal(group.Parts, group.Rotated(4).Parts);
        Assert.Equal(group.Parts, group.Rotated(1).Rotated(-1).Parts);
    }

    [Fact]
    public void Placing_at_an_anchor_offsets_every_part()
    {
        var group = PartGroup.From([At(1, Brick2x4, 5, 0, 7, color: 3), At(2, Brick1x1, 6, 3, 9)]);

        var placed = group.At(new GridPos(10, 3, 1)).ToList();

        Assert.Equal([new GridPos(10, 3, 1), new GridPos(11, 6, 3)], placed.Select(p => p.Position));
        Assert.Equal(3, placed[0].ColorId);
    }
}
