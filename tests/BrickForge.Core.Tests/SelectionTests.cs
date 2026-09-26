namespace BrickForge.Core.Tests;

public class SelectionTests
{
    private static readonly PartType Brick2x2 = PartCatalog.Get("brick-2x2");
    private static readonly BrickColor Red = Palette.All.Single(c => c.Name == "Red");
    private static readonly BrickColor Blue = Palette.All.Single(c => c.Name == "Blue");

    private readonly Editor _editor = new(new Build(new Baseplate(32, 32)));
    private readonly List<BuildChange> _changes = [];

    public SelectionTests()
    {
        _editor.SelectPart(Brick2x2);
        _editor.SelectColor(Red);
        _editor.Changed += _changes.Add;
    }

    private PlacedPart PlaceAt(int x, int y, int z)
    {
        _editor.PlaceAt(new GridPos(x, y, z));
        return _editor.Build.Parts.Single(p => p.Position == new GridPos(x, y, z));
    }

    /// <summary>Two stacked bricks at (0,0,0) and (0,3,0), plus a lone one at (10,0,10).</summary>
    private (PlacedPart Bottom, PlacedPart Top, PlacedPart Lone) Scene() =>
        (PlaceAt(0, 0, 0), PlaceAt(0, 3, 0), PlaceAt(10, 0, 10));

    private IEnumerable<GridPos> Positions() =>
        _editor.Build.Parts.Select(p => p.Position).OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z);

    // ---- selecting ----------------------------------------------------------------------------

    [Fact]
    public void Clicking_selects_one_part_and_ctrl_click_toggles()
    {
        var (bottom, top, _) = Scene();

        _editor.Select(bottom.Id, additive: false);
        _editor.Select(top.Id, additive: true);
        Assert.Equal([bottom.Id, top.Id], _editor.Selection.Order());

        _editor.Select(bottom.Id, additive: true);
        Assert.Equal([top.Id], _editor.Selection);

        _editor.Select(null, additive: false);
        Assert.Empty(_editor.Selection);
    }

    [Fact]
    public void Box_select_replaces_or_adds_and_ignores_unknown_ids()
    {
        var (bottom, top, lone) = Scene();
        _editor.Select(lone.Id, additive: false);

        _editor.SelectMany([bottom.Id, 999], additive: true);
        Assert.Equal([bottom.Id, lone.Id], _editor.Selection.Order());

        _editor.SelectMany([top.Id], additive: false);
        Assert.Equal([top.Id], _editor.Selection);
    }

    [Fact]
    public void Select_all_and_escape_clears()
    {
        Scene();

        _editor.SelectAll();
        Assert.Equal(3, _editor.Selection.Count);

        _editor.Cancel();
        Assert.Empty(_editor.Selection);
    }

    [Fact]
    public void Removed_parts_leave_the_selection()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);

        _editor.Remove(top.Id);

        Assert.Equal([bottom.Id], _editor.Selection);
    }

    // ---- acting on a selection ----------------------------------------------------------------

    [Fact]
    public void Deleting_the_selection_is_one_undo_step()
    {
        var (bottom, top, lone) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);

        _editor.DeleteSelection();
        Assert.Equal([lone], _editor.Build.Parts);
        Assert.Empty(_editor.Selection);

        _editor.Undo();
        Assert.Equal(3, _editor.Build.Parts.Count);
    }

    [Fact]
    public void Painting_a_selected_part_paints_the_whole_selection()
    {
        var (bottom, top, lone) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);
        _editor.SelectColor(Blue);

        _editor.Paint(top.Id);

        Assert.All(_editor.Build.Parts.Where(p => p.Id != lone.Id), p => Assert.Equal(Blue.Id, p.ColorId));
        Assert.Equal(Red.Id, _editor.Build.Find(lone.Id)!.ColorId);
        _editor.Undo();
        Assert.All(_editor.Build.Parts, p => Assert.Equal(Red.Id, p.ColorId));
    }

    // ---- clipboard ----------------------------------------------------------------------------

    [Fact]
    public void Paste_holds_the_copied_group_and_stamps_until_cancelled()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);
        _editor.Copy();

        _editor.Paste();
        Assert.Equal(2, _editor.Held!.Group.Parts.Count);

        _editor.PlaceAt(new GridPos(20, 0, 0));
        _editor.PlaceAt(new GridPos(20, 0, 20));
        Assert.Equal(7, _editor.Build.Parts.Count);
        Assert.NotNull(_editor.Held); // still stamping

        _editor.Cancel();
        Assert.Null(_editor.Held);
    }

    [Fact]
    public void Each_stamp_is_its_own_undo_step()
    {
        var (bottom, _, _) = Scene();
        _editor.Select(bottom.Id, additive: false);
        _editor.Copy();
        _editor.Paste();
        _editor.PlaceAt(new GridPos(20, 0, 0));
        _editor.PlaceAt(new GridPos(24, 0, 0));

        _editor.Undo();

        Assert.Equal(4, _editor.Build.Parts.Count);
    }

    [Fact]
    public void Pasted_parts_become_the_selection()
    {
        var (bottom, _, _) = Scene();
        _editor.Select(bottom.Id, additive: false);
        _editor.Copy();
        _editor.Paste();

        _editor.PlaceAt(new GridPos(20, 0, 0));

        Assert.Equal(new GridPos(20, 0, 0), _editor.Build.Find(_editor.Selection.Single())!.Position);
    }

    [Fact]
    public void Pasting_somewhere_invalid_does_nothing()
    {
        var (bottom, _, _) = Scene();
        _editor.Select(bottom.Id, additive: false);
        _editor.Copy();
        _editor.Paste();

        _editor.PlaceAt(new GridPos(20, 9, 0)); // floating

        Assert.Equal(3, _editor.Build.Parts.Count);
    }

    [Fact]
    public void Paste_with_an_empty_clipboard_does_nothing()
    {
        _editor.Paste();

        Assert.Null(_editor.Held);
    }

    [Fact]
    public void Rotating_while_pasting_turns_the_group()
    {
        _editor.SelectPart(PartCatalog.Get("brick-2x4"));
        var brick = PlaceAt(0, 0, 0);
        _editor.Select(brick.Id, additive: false);
        _editor.Copy();
        _editor.Paste();

        _editor.Rotate(1);

        Assert.Equal((4, 2), (_editor.Held!.Group.SizeX, _editor.Held.Group.SizeZ));
    }

    [Fact]
    public void Cut_removes_as_one_step_and_fills_the_clipboard()
    {
        var (bottom, top, lone) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);

        _editor.Cut();
        Assert.Equal([lone], _editor.Build.Parts);

        _editor.Paste();
        _editor.PlaceAt(new GridPos(0, 0, 0));
        Assert.Equal(3, _editor.Build.Parts.Count);

        _editor.Cancel();
        _editor.Undo(); // the paste
        _editor.Undo(); // the cut
        Assert.Equal(3, _editor.Build.Parts.Count);
    }

    // ---- moving ------------------------------------------------------------------------------

    [Fact]
    public void Picking_up_a_selected_part_moves_the_whole_selection()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);

        _editor.PickUp(top.Id);
        Assert.Equal(2, _editor.Held!.Moving.Count);
        Assert.Equal([bottom.Id, top.Id], _changes.Last().Removed.Order()); // hidden while held

        _editor.PlaceAt(new GridPos(4, 0, 4));

        Assert.Equal([new GridPos(4, 0, 4), new GridPos(4, 3, 4), new GridPos(10, 0, 10)], Positions());
        Assert.Equal([bottom.Id, top.Id], _editor.Selection.Order()); // same parts, still selected
        Assert.Null(_editor.Held);
    }

    [Fact]
    public void A_group_move_is_one_undo_step()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);
        _editor.PickUp(bottom.Id);
        _editor.PlaceAt(new GridPos(4, 0, 4));

        _editor.Undo();

        Assert.Equal([new GridPos(0, 0, 0), new GridPos(0, 3, 0), new GridPos(10, 0, 10)], Positions());
    }

    [Fact]
    public void A_moving_group_can_overlap_its_own_old_place()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);
        _editor.PickUp(bottom.Id);

        Assert.True(_editor.CanPlaceAt(new GridPos(1, 0, 0)));
    }

    [Fact]
    public void Cancelling_a_group_move_puts_everything_back()
    {
        var (bottom, top, _) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);
        _editor.PickUp(bottom.Id);

        _editor.Cancel();

        Assert.Null(_editor.Held);
        Assert.Equal([bottom.Id, top.Id], _changes.Last().Added.Select(p => p.Id).Order());
        Assert.Equal([bottom.Id, top.Id], _editor.Selection.Order()); // Esc cancels the move, not the selection
    }

    [Fact]
    public void Picking_up_an_unselected_part_moves_only_that_part()
    {
        var (bottom, top, lone) = Scene();
        _editor.SelectMany([bottom.Id, top.Id], additive: false);

        _editor.PickUp(lone.Id);

        Assert.Equal([lone], _editor.Held!.Moving);
    }

    [Fact]
    public void Finishing_a_hold_restores_the_part_tool_rotation()
    {
        var (bottom, _, _) = Scene();
        _editor.Rotate(1);
        _editor.Select(bottom.Id, additive: false);
        _editor.Copy();
        _editor.Paste();
        _editor.Rotate(1);
        _editor.Rotate(1);

        _editor.Cancel();

        Assert.Equal(Rotation.R90, _editor.Rotation);
    }
}
