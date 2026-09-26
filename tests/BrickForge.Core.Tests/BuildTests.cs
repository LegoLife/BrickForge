namespace BrickForge.Core.Tests;

public class BuildTests
{
    private static readonly PartType Brick1x1 = PartCatalog.Get("brick-1x1");
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private static readonly PartType Brick2x4 = PartCatalog.Get("brick-2x4");
    private static readonly PartType Plate1x1 = PartCatalog.Get("plate-1x1");
    private static readonly PartType Tile2x2 = PartCatalog.Get("tile-2x2");
    private static readonly PartType Tile1x1 = PartCatalog.Get("tile-1x1");

    private static Build NewBuild() => new(new Baseplate(16, 16));

    private static PlacedPart Place(Build build, PartType part, int x, int y, int z, Rotation rotation = Rotation.R0)
    {
        var result = build.TryPlace(part, new GridPos(x, y, z), rotation, colorId: 0);
        Assert.True(result.Success, $"Expected {part.Id} at ({x},{y},{z}) to place, got {result.Error}");
        return result.Part!;
    }

    [Fact]
    public void Part_on_empty_baseplate_is_placed()
    {
        var build = NewBuild();

        var part = Place(build, Brick2x4, 3, 0, 5);

        Assert.Equal(new GridPos(3, 0, 5), part.Position);
        Assert.Single(build.Parts);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(15, 0, 0)] // 2 wide from x=15 runs off a 16-stud plate
    [InlineData(0, 0, 13)] // 4 deep from z=13 runs off
    [InlineData(0, -1, 0)]
    public void Part_outside_baseplate_is_rejected(int x, int y, int z)
    {
        var result = NewBuild().TryPlace(Brick2x4, new GridPos(x, y, z), Rotation.R0, 0);

        Assert.Equal(PlacementError.OutOfBounds, result.Error);
    }

    [Fact]
    public void Rotation_swaps_footprint_for_bounds()
    {
        var build = NewBuild();

        // Unrotated 2x4 at z=13 overflows; rotated 90° it is 4 wide, 2 deep and fits.
        Assert.Equal(PlacementError.None, build.Check(Brick2x4, new GridPos(0, 0, 13), Rotation.R90));
        Assert.Equal(PlacementError.OutOfBounds, build.Check(Brick2x4, new GridPos(13, 0, 0), Rotation.R90));
    }

    [Fact]
    public void Overlapping_part_is_rejected()
    {
        var build = NewBuild();
        Place(build, Brick2x4, 0, 0, 0);

        var result = build.TryPlace(Brick1x1, new GridPos(1, 2, 3), Rotation.R0, 0);

        Assert.Equal(PlacementError.Overlaps, result.Error);
    }

    [Fact]
    public void Parts_can_sit_side_by_side()
    {
        var build = NewBuild();
        Place(build, Brick2x4, 0, 0, 0);

        Place(build, Brick2x4, 2, 0, 0);

        Assert.Equal(2, build.Parts.Count);
    }

    [Fact]
    public void Floating_part_is_rejected()
    {
        var result = NewBuild().TryPlace(Brick2x2, new GridPos(4, 3, 4), Rotation.R0, 0);

        Assert.Equal(PlacementError.NotConnected, result.Error);
    }

    [Fact]
    public void Part_stacked_on_studs_is_connected()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 4, 0, 4);

        Place(build, Brick2x2, 4, 3, 4);
    }

    [Fact]
    public void Single_stud_overlap_is_enough_to_connect()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 4, 0, 4);

        Place(build, Brick2x4, 5, 3, 5); // only cell (5,5) sits over the lower brick
    }

    [Fact]
    public void Part_beside_but_not_on_a_brick_is_not_connected()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 4, 0, 4);

        Assert.Equal(PlacementError.NotConnected, build.Check(Brick2x2, new GridPos(6, 3, 4), Rotation.R0));
    }

    [Fact]
    public void Part_can_hang_below_an_overhang()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 0, 0, 0);
        Place(build, Brick2x4, 0, 3, 0); // overhangs z = 2..3 with empty space beneath

        Place(build, Plate1x1, 0, 2, 3); // its studs push into the overhang's underside
    }

    [Fact]
    public void Nothing_connects_on_top_of_a_tile()
    {
        var build = NewBuild();
        Place(build, Tile2x2, 4, 0, 4);

        Assert.Equal(PlacementError.NotConnected, build.Check(Brick1x1, new GridPos(4, 1, 4), Rotation.R0));
    }

    [Fact]
    public void Tile_cannot_hang_below_a_part()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 0, 0, 0);
        Place(build, Brick2x4, 0, 3, 0);

        Assert.Equal(PlacementError.NotConnected, build.Check(Tile1x1, new GridPos(0, 2, 3), Rotation.R0));
    }

    [Fact]
    public void Removing_a_part_frees_its_space()
    {
        var build = NewBuild();
        var part = Place(build, Brick2x4, 0, 0, 0);

        Assert.True(build.Remove(part.Id));

        Assert.Empty(build.Parts);
        Place(build, Brick2x4, 0, 0, 0);
    }

    [Fact]
    public void Removing_unknown_part_returns_false()
    {
        Assert.False(NewBuild().Remove(42));
    }

    [Fact]
    public void Part_at_finds_the_part_occupying_a_cell()
    {
        var build = NewBuild();
        var part = Place(build, Brick2x4, 2, 0, 2, Rotation.R90);

        Assert.Same(part, build.PartAt(new GridPos(5, 2, 3)));
        Assert.Null(build.PartAt(new GridPos(5, 3, 3)));
    }
}
