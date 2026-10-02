namespace BrickForge.Core.Tests;

public class BuildTests
{
    private static readonly PartType Brick1x1 = PartCatalog.Get("brick-1x1");
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private static readonly PartType Brick2x4 = PartCatalog.Get("brick-2x4");
    private static readonly PartType Tile2x2 = PartCatalog.Get("tile-2x2");

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
    public void Part_can_float_with_nothing_holding_it_up()
    {
        var build = NewBuild();

        Place(build, Brick2x2, 4, 6, 4);
        Place(build, Brick1x1, 9, 1, 9); // under nothing, over nothing
    }

    [Fact]
    public void Part_stacked_on_studs_is_connected()
    {
        var build = NewBuild();
        Place(build, Brick2x2, 4, 0, 4);

        Place(build, Brick2x2, 4, 3, 4);
    }

    [Fact]
    public void Parts_can_go_on_top_of_a_tile()
    {
        var build = NewBuild();
        Place(build, Tile2x2, 4, 0, 4);

        Place(build, Brick1x1, 4, 1, 4);
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
    public void Modify_moves_recolours_and_rotates_keeping_the_id()
    {
        var build = NewBuild();
        var part = Place(build, Brick2x4, 0, 0, 0);

        var result = build.Modify(part.Id, new GridPos(1, 0, 0), Rotation.R90, colorId: 7);

        Assert.True(result.Success);
        Assert.Equal(part with { Position = new GridPos(1, 0, 0), Rotation = Rotation.R90, ColorId = 7 }, result.Part);
        Assert.Null(build.PartAt(new GridPos(0, 0, 3)));
        Assert.Same(result.Part, build.PartAt(new GridPos(4, 0, 1)));
    }

    [Fact]
    public void Rejected_modify_leaves_the_part_where_it_was()
    {
        var build = NewBuild();
        var part = Place(build, Brick2x4, 0, 0, 0);

        Place(build, Brick2x4, 4, 0, 0);

        var result = build.Modify(part.Id, new GridPos(3, 0, 0), Rotation.R0, part.ColorId);

        Assert.Equal(PlacementError.Overlaps, result.Error);
        Assert.Same(part, build.PartAt(new GridPos(0, 0, 0)));
    }

    [Fact]
    public void Check_can_ignore_a_part_being_moved()
    {
        var build = NewBuild();
        var part = Place(build, Brick2x2, 0, 0, 0);
        Place(build, Brick2x2, 0, 3, 0);

        // Sliding the bottom brick sideways under the top one: it overlaps its own old cells only.
        Assert.Equal(PlacementError.Overlaps, build.Check(Brick2x2, new GridPos(1, 0, 0), Rotation.R0));
        Assert.Equal(PlacementError.None, build.Check(Brick2x2, new GridPos(1, 0, 0), Rotation.R0, ignorePartId: part.Id));
    }

    [Fact]
    public void Group_can_float()
    {
        var build = NewBuild();
        var group = PartGroup.From([new PlacedPart(1, Brick2x2, new GridPos(0, 0, 0), Rotation.R0, 0)]);

        Assert.Equal(PlacementError.None, build.CheckGroup(group.At(new GridPos(4, 6, 4)).ToList()));
    }

    [Fact]
    public void Group_with_any_part_off_the_baseplate_is_rejected()
    {
        var group = PartGroup.From([
            new PlacedPart(1, Brick2x2, new GridPos(0, 0, 0), Rotation.R0, 0),
            new PlacedPart(2, Brick2x2, new GridPos(4, 0, 0), Rotation.R0, 0),
        ]);

        Assert.Equal(PlacementError.OutOfBounds, NewBuild().CheckGroup(group.At(new GridPos(11, 0, 0)).ToList()));
    }

    [Fact]
    public void Group_overlapping_the_build_is_rejected_unless_those_parts_are_moving()
    {
        var build = NewBuild();
        var existing = Place(build, Brick2x2, 0, 0, 0);
        var group = PartGroup.From([existing]);
        var shifted = group.At(new GridPos(1, 0, 0)).ToList();

        Assert.Equal(PlacementError.Overlaps, build.CheckGroup(shifted));
        Assert.Equal(PlacementError.None, build.CheckGroup(shifted, ignore: new HashSet<int> { existing.Id }));
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
