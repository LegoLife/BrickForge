namespace BrickForge.Core.Tests;

public class AimingTests
{
    private static readonly PartType Brick2x4 = PartCatalog.Get("brick-2x4");
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private const double Inset = LegoUnits.PartInset;

    private readonly Build _build = new(new Baseplate(16, 16));

    /// <summary>
    /// The only part in the build: by default a 2x4 brick at (4, 0, 4), covering x 4..6, z 4..8, plates 0..3.
    /// It may float — aiming doesn't care what holds it up.
    /// </summary>
    private PlacedPart Target(int x = 4, int y = 0, int z = 4, Rotation rotation = Rotation.R0)
    {
        var part = new PlacedPart(1, Brick2x4, new GridPos(x, y, z), rotation, 0);
        new Editor(_build).ReplaceAll(_build.Baseplate, [part]);
        return part;
    }

    private GridPos? Anchor(double x, double y, double z, int? partId, (int X, int Y, int Z) size) =>
        Aiming.Anchor(_build, new PointerHit(x, y, z, partId), size.X, size.Y, size.Z);

    private static readonly (int, int, int) Brick2x4Size = (2, 3, 4);
    private static readonly (int, int, int) Brick2x4Turned = (4, 3, 2);
    private static readonly (int, int, int) Brick2x2Size = (2, 3, 2);

    // ---- baseplate ----------------------------------------------------------------------------

    [Fact]
    public void On_the_baseplate_an_odd_size_centres_on_the_stud_under_the_cursor()
    {
        Assert.Equal(new GridPos(5, 0, 2), Anchor(5.9, 0, 2.1, null, (1, 3, 1)));
        Assert.Equal(new GridPos(4, 0, 1), Anchor(5.9, 0, 2.1, null, (3, 3, 3)));
    }

    [Fact]
    public void On_the_baseplate_an_even_size_centres_on_the_nearest_line_between_studs()
    {
        // x 4..6 is centred on 5, the line nearest 5.3; z 6..10 on 8, the line nearest 7.6.
        Assert.Equal(new GridPos(4, 0, 6), Anchor(5.3, 0, 7.6, null, Brick2x4Size));
        Assert.Equal(new GridPos(5, 0, 5), Anchor(5.7, 0, 7.4, null, Brick2x4Size));
    }

    [Fact]
    public void At_the_baseplate_edge_the_ghost_slides_back_on()
    {
        Assert.Equal(new GridPos(0, 0, 12), Anchor(0.2, 0, 15.8, null, Brick2x4Size));
    }

    [Fact]
    public void A_ghost_wider_than_the_baseplate_goes_nowhere()
    {
        Assert.Null(Anchor(8, 0, 8, null, (20, 3, 2)));
    }

    // ---- top and underside --------------------------------------------------------------------

    [Fact]
    public void On_top_of_a_part_the_ghost_sits_on_it_centred_on_the_cursor()
    {
        var target = Target();

        Assert.Equal(new GridPos(5, 3, 5), Anchor(5.5, 3, 6.2, target.Id, Brick2x2Size));
    }

    [Fact]
    public void Near_the_edge_of_a_top_face_it_still_counts_as_the_top()
    {
        var target = Target();

        Assert.Equal(3, Anchor(6 - Inset - 0.001, 3, 6, target.Id, Brick2x2Size)?.Y);
    }

    [Fact]
    public void Under_a_part_the_ghost_hangs_from_it_centred_on_the_cursor()
    {
        var target = Target(y: 6);

        Assert.Equal(new GridPos(5, 5, 5), Anchor(5.5, 6, 6.2, target.Id, (2, 1, 2)));
        Assert.Equal(new GridPos(5, 3, 5), Anchor(5.5, 6, 6.2, target.Id, Brick2x2Size));
    }

    [Fact]
    public void Under_a_part_too_low_to_fit_the_ghost_goes_nowhere()
    {
        var target = Target(y: 1);

        Assert.Null(Anchor(5.5, 1, 6.2, target.Id, Brick2x2Size));
    }

    // ---- sides --------------------------------------------------------------------------------

    [Fact]
    public void On_each_side_the_ghost_sits_flush_against_the_face_without_overlapping()
    {
        var target = Target(); // x 4..6, z 4..8

        var cases = new (double X, double Z, GridPos Expected)[]
        {
            (4 + Inset, 6.2, new GridPos(0, 0, 5)),  // -x face: a turned 2x4 is 4 long in x
            (6 - Inset, 6.2, new GridPos(6, 0, 5)),  // +x face
            (5.3, 4 + Inset, new GridPos(3, 0, 2)),  // -z face
            (5.3, 8 - Inset, new GridPos(3, 0, 8)),  // +z face
        };
        foreach (var (x, z, expected) in cases)
        {
            var anchor = Anchor(x, 1.5, z, target.Id, Brick2x4Turned);

            Assert.Equal(expected, anchor);
            var ghost = PartGroup.Single(Brick2x4, Rotation.R90, 0);
            Assert.Equal(PlacementError.None, _build.CheckGroup([.. ghost.At(anchor!.Value)]));
        }
    }

    [Fact]
    public void On_a_side_the_ghost_is_level_with_the_parts_base_wherever_the_cursor_is_on_it()
    {
        var target = Target(y: 3);

        Assert.Equal(new GridPos(6, 3, 4), Anchor(6 - Inset, 3.2, 6, target.Id, Brick2x4Size));
        Assert.Equal(new GridPos(6, 3, 4), Anchor(6 - Inset, 5.8, 6, target.Id, Brick2x4Size));
    }

    [Fact]
    public void On_a_side_the_ghost_slides_along_the_face_to_stay_on_the_baseplate()
    {
        var target = Target(x: 0, z: 12); // x 0..2, z 12..16

        Assert.Equal(new GridPos(2, 0, 12), Anchor(2 - Inset, 1.5, 15.9, target.Id, Brick2x4Size));
    }

    [Fact]
    public void On_a_side_with_no_room_before_the_baseplate_edge_the_ghost_goes_nowhere()
    {
        var target = Target(x: 0);

        Assert.Null(Anchor(Inset, 1.5, 6, target.Id, Brick2x4Size));
    }

    [Fact]
    public void Faces_follow_the_parts_rotation()
    {
        var target = Target(rotation: Rotation.R90); // turned: x 4..8, z 4..6

        Assert.Equal(new GridPos(8, 0, 4), Anchor(8 - Inset, 1.5, 5.1, target.Id, Brick2x2Size));
        Assert.Equal(new GridPos(5, 0, 6), Anchor(6.4, 1.5, 6 - Inset, target.Id, Brick2x2Size));
    }

    [Fact]
    public void A_part_that_is_gone_gives_nowhere()
    {
        Assert.Null(Anchor(5, 3, 5, partId: 99, Brick2x2Size));
    }

    // ---- editor -------------------------------------------------------------------------------

    [Fact]
    public void The_editor_aims_with_the_ghosts_rotated_size()
    {
        var editor = new Editor(_build);
        editor.SelectPart(Brick2x4);
        editor.Rotate(1); // 4 along x, 2 along z

        Assert.Equal(new GridPos(3, 0, 7), editor.Aim(new PointerHit(5.3, 0, 7.6, null)));
    }

    [Fact]
    public void The_editor_aims_a_held_group_by_its_bounding_box()
    {
        var target = Target(); // x 4..6
        var editor = new Editor(_build);
        editor.Hold(PartGroup.From([
            new PlacedPart(1, Brick2x4, new GridPos(0, 0, 0), Rotation.R0, 0),
            new PlacedPart(2, Brick2x2, new GridPos(0, 3, 6), Rotation.R0, 0),
        ])); // 2 x 6 plates x 8

        Assert.Equal(new GridPos(6, 0, 2), editor.Aim(new PointerHit(6 - Inset, 1.5, 6.2, target.Id)));
    }
}
