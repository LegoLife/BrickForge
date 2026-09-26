namespace BrickForge.Core.Tests;

public class EditorTests
{
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private static readonly PartType Brick2x4 = PartCatalog.Get("brick-2x4");
    private static readonly BrickColor Red = Palette.All.Single(c => c.Name == "Red");
    private static readonly BrickColor Blue = Palette.All.Single(c => c.Name == "Blue");

    private readonly Editor _editor = new(new Build(new Baseplate(16, 16)));
    private readonly List<BuildChange> _changes = [];

    public EditorTests()
    {
        _editor.SelectPart(Brick2x4);
        _editor.SelectColor(Red);
        _editor.Changed += _changes.Add;
    }

    private PlacedPart PlaceAt(int x, int y, int z)
    {
        _editor.PlaceAt(new GridPos(x, y, z));
        return _editor.Build.Parts.Single(p => p.Position == new GridPos(x, y, z));
    }

    [Fact]
    public void Placing_uses_the_selected_part_colour_and_rotation()
    {
        _editor.Rotate(1);

        var part = PlaceAt(2, 0, 2);

        Assert.Equal((Brick2x4, Red.Id, Rotation.R90), (part.Part, part.ColorId, part.Rotation));
        Assert.Equal([part], _changes.Last().Added);
    }

    [Fact]
    public void Rotate_wraps_in_both_directions()
    {
        _editor.Rotate(-1);
        Assert.Equal(Rotation.R270, _editor.Rotation);

        _editor.Rotate(1);
        Assert.Equal(Rotation.R0, _editor.Rotation);
    }

    [Fact]
    public void Invalid_placement_changes_nothing()
    {
        _editor.PlaceAt(new GridPos(0, 5, 0));

        Assert.Empty(_editor.Build.Parts);
        Assert.False(_editor.CanUndo);
    }

    [Fact]
    public void Undo_and_redo_a_placement()
    {
        var part = PlaceAt(0, 0, 0);

        _editor.Undo();
        Assert.Empty(_editor.Build.Parts);
        Assert.Equal([part.Id], _changes.Last().Removed);

        _editor.Redo();
        Assert.Equal([part], _editor.Build.Parts);
        Assert.Equal([part], _changes.Last().Added);
    }

    [Fact]
    public void Undo_a_removal_restores_the_same_part()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.Remove(part.Id);

        _editor.Undo();

        Assert.Equal([part], _editor.Build.Parts);
    }

    [Fact]
    public void A_new_action_clears_redo()
    {
        PlaceAt(0, 0, 0);
        _editor.Undo();

        PlaceAt(4, 0, 4);

        Assert.False(_editor.CanRedo);
    }

    [Fact]
    public void Paint_recolours_and_is_undoable()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.SelectColor(Blue);

        _editor.Paint(part.Id);
        Assert.Equal(Blue.Id, _editor.Build.Parts.Single().ColorId);

        _editor.Undo();
        Assert.Equal(Red.Id, _editor.Build.Parts.Single().ColorId);
    }

    [Fact]
    public void Painting_the_same_colour_is_not_an_undo_step()
    {
        var part = PlaceAt(0, 0, 0);

        _editor.Paint(part.Id); // already red

        _editor.Undo();
        Assert.Empty(_editor.Build.Parts); // the only step was the placement
    }

    [Fact]
    public void Eyedropper_copies_part_colour_and_rotation()
    {
        _editor.SelectPart(Brick2x2);
        _editor.SelectColor(Blue);
        _editor.Rotate(1);
        var source = PlaceAt(0, 0, 0);
        _editor.SelectPart(Brick2x4);
        _editor.SelectColor(Red);
        _editor.Rotate(1);

        _editor.Eyedrop(source.Id);

        Assert.Equal((Brick2x2, Blue, Rotation.R90), (_editor.Part, _editor.Color, _editor.Rotation));
    }

    [Fact]
    public void Picking_up_hides_the_part_but_keeps_it_in_the_build()
    {
        var part = PlaceAt(0, 0, 0);

        _editor.PickUp(part.Id);

        Assert.Same(part, _editor.Carrying);
        Assert.Single(_editor.Build.Parts);
        Assert.Equal([part.Id], _changes.Last().Removed);
    }

    [Fact]
    public void Dropping_a_carried_part_moves_it_as_one_undo_step()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.PickUp(part.Id);

        _editor.PlaceAt(new GridPos(6, 0, 6));

        var moved = _editor.Build.Parts.Single();
        Assert.Equal((part.Id, new GridPos(6, 0, 6)), (moved.Id, moved.Position));
        Assert.Null(_editor.Carrying);

        _editor.Undo();
        Assert.Equal(new GridPos(0, 0, 0), _editor.Build.Parts.Single().Position);
    }

    [Fact]
    public void A_carried_part_can_move_onto_its_own_old_cells()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.PickUp(part.Id);

        Assert.True(_editor.CanPlaceAt(new GridPos(1, 0, 0)));
    }

    [Fact]
    public void A_carried_part_takes_rotation_and_colour_changes()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.PickUp(part.Id);
        _editor.Rotate(1);
        _editor.SelectColor(Blue);

        _editor.PlaceAt(new GridPos(4, 0, 4));

        var moved = _editor.Build.Parts.Single();
        Assert.Equal((Rotation.R90, Blue.Id), (moved.Rotation, moved.ColorId));
    }

    [Fact]
    public void Cancel_puts_a_carried_part_back()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.PickUp(part.Id);

        _editor.Cancel();

        Assert.Null(_editor.Carrying);
        Assert.Equal([part], _changes.Last().Added);
        Assert.Equal([part], _editor.Build.Parts);
    }

    [Fact]
    public void Selecting_another_part_cancels_the_carry()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.PickUp(part.Id);

        _editor.SelectPart(Brick2x2);

        Assert.Null(_editor.Carrying);
        Assert.Equal(Brick2x2, _editor.Part);
    }

    [Fact]
    public void Replace_all_swaps_the_build_and_is_one_undo_step()
    {
        var old = PlaceAt(0, 0, 0);
        var incoming = new PlacedPart(1, Brick2x2, new GridPos(8, 0, 8), Rotation.R0, Blue.Id);

        _editor.ReplaceAll(_editor.Build.Baseplate, [incoming]);

        Assert.Equal([incoming], _editor.Build.Parts);
        Assert.Equal([old.Id], _changes.Last().Removed);
        Assert.Equal([incoming], _changes.Last().Added);

        _editor.Undo();
        Assert.Equal([old], _editor.Build.Parts);
    }

    [Fact]
    public void Replace_all_can_change_the_baseplate()
    {
        PlaceAt(0, 0, 0);
        var incoming = new PlacedPart(1, Brick2x2, new GridPos(90, 0, 10), Rotation.R0, Blue.Id);

        _editor.ReplaceAll(new Baseplate(96, 32), [incoming]);

        Assert.Equal(new Baseplate(96, 32), _editor.Build.Baseplate);
        Assert.Equal(new Baseplate(96, 32), _changes.Last().Baseplate);

        _editor.Undo();
        Assert.Equal(new Baseplate(16, 16), _editor.Build.Baseplate);
        Assert.Equal(new Baseplate(16, 16), _changes.Last().Baseplate);
    }

    [Fact]
    public void New_build_empties_and_resizes_as_one_undo_step()
    {
        var part = PlaceAt(0, 0, 0);

        _editor.NewBuild(new Baseplate(64, 64));

        Assert.Empty(_editor.Build.Parts);
        Assert.Equal(new Baseplate(64, 64), _editor.Build.Baseplate);

        _editor.Undo();
        Assert.Equal([part], _editor.Build.Parts);
        Assert.Equal(new Baseplate(16, 16), _editor.Build.Baseplate);
    }

    [Fact]
    public void Growing_keeps_parts_centred()
    {
        PlaceAt(0, 0, 0);

        Assert.True(_editor.Resize(new Baseplate(20, 24)));

        Assert.Equal(new GridPos(2, 0, 4), _editor.Build.Parts.Single().Position);
    }

    [Fact]
    public void Shrinking_keeps_parts_centred()
    {
        PlaceAt(6, 0, 6);

        Assert.True(_editor.Resize(new Baseplate(8, 8)));

        Assert.Equal(new GridPos(2, 0, 2), _editor.Build.Parts.Single().Position);
    }

    [Fact]
    public void Shrinking_is_refused_if_a_part_would_fall_off()
    {
        var part = PlaceAt(0, 0, 0);

        Assert.False(_editor.Resize(new Baseplate(8, 8)));

        Assert.Equal(new Baseplate(16, 16), _editor.Build.Baseplate);
        Assert.Equal([part], _editor.Build.Parts);
        _editor.Undo();
        Assert.Empty(_editor.Build.Parts); // the only undo step was the placement
    }

    [Fact]
    public void Resize_is_undoable()
    {
        var part = PlaceAt(0, 0, 0);
        _editor.Resize(new Baseplate(32, 32));

        _editor.Undo();

        Assert.Equal(new Baseplate(16, 16), _editor.Build.Baseplate);
        Assert.Equal([part], _editor.Build.Parts);
    }

    [Fact]
    public void Resizing_to_the_same_size_is_not_an_undo_step()
    {
        Assert.True(_editor.Resize(new Baseplate(16, 16)));

        Assert.False(_editor.CanUndo);
    }

    [Fact]
    public void Clear_is_undoable()
    {
        PlaceAt(0, 0, 0);
        PlaceAt(4, 0, 4);

        _editor.Clear();
        Assert.Empty(_editor.Build.Parts);

        _editor.Undo();
        Assert.Equal(2, _editor.Build.Parts.Count);
    }

    [Fact]
    public void Clearing_an_empty_build_is_not_an_undo_step()
    {
        _editor.Clear();

        Assert.False(_editor.CanUndo);
    }

    [Fact]
    public void Forget_history_drops_undo_and_redo()
    {
        PlaceAt(0, 0, 0);
        PlaceAt(4, 0, 4);
        _editor.Undo();

        _editor.ForgetHistory();

        Assert.False(_editor.CanUndo);
        Assert.False(_editor.CanRedo);
    }

    [Fact]
    public void Undo_while_carrying_cancels_first()
    {
        PlaceAt(0, 0, 0);
        var second = PlaceAt(4, 0, 4);
        _editor.PickUp(second.Id);
        _changes.Clear();

        _editor.Undo();

        Assert.Null(_editor.Carrying);
        Assert.Single(_editor.Build.Parts); // second placement undone
        // The view must re-show the carried part before removing it, not in one remove-then-add change.
        Assert.Equal(
            [new BuildChange([second], []), new BuildChange([], [second.Id])],
            _changes,
            (a, b) => a.Added.SequenceEqual(b.Added) && a.Removed.SequenceEqual(b.Removed));
    }
}
