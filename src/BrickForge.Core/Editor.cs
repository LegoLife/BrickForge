namespace BrickForge.Core;

/// <summary>
/// What the view must redraw: remove <see cref="Removed"/> first, then draw <see cref="Added"/>.
/// A modified part appears in both, under the same id.
/// </summary>
public sealed record BuildChange(IReadOnlyList<PlacedPart> Added, IReadOnlyList<int> Removed)
{
    public static BuildChange None { get; } = new([], []);

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}

public enum EditorMode { Build, Paint }

/// <summary>
/// Everything the user can do to a build: the current tool (part, colour, rotation, mode), placing,
/// removing, painting, the eyedropper, picking a part up to move it, and undo/redo.
/// Every call raises <see cref="Changed"/> — with an empty change when only tool state moved.
/// </summary>
public sealed class Editor(Build build)
{
    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();

    public event Action<BuildChange>? Changed;

    public Build Build { get; } = build;
    public PartType Part { get; private set; } = PartCatalog.Get("brick-2x4");
    public BrickColor Color { get; private set; } = Palette.All[0];
    public Rotation Rotation { get; private set; } = Rotation.R0;
    public EditorMode Mode { get; private set; } = EditorMode.Build;

    /// <summary>A part lifted off the build to be dropped elsewhere. It stays in the model until dropped.</summary>
    public PlacedPart? Carrying { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---- tool state ---------------------------------------------------------------------------

    public void SelectPart(PartType part)
    {
        var change = DropCarry();
        Part = part;
        Mode = EditorMode.Build;
        Raise(change);
    }

    /// <summary>Also recolours a carried part when it is dropped.</summary>
    public void SelectColor(BrickColor color)
    {
        Color = color;
        Raise(BuildChange.None);
    }

    /// <param name="quarterTurns">+1 clockwise, -1 anticlockwise.</param>
    public void Rotate(int quarterTurns)
    {
        Rotation = (Rotation)((((int)Rotation + quarterTurns) % 4 + 4) % 4);
        Raise(BuildChange.None);
    }

    public void SetMode(EditorMode mode)
    {
        var change = mode == EditorMode.Paint ? DropCarry() : BuildChange.None;
        Mode = mode;
        Raise(change);
    }

    public void Eyedrop(int partId)
    {
        if (Carrying is not null || !TryGetPart(partId, out var source)) return;
        Part = source.Part;
        Color = Palette.All.FirstOrDefault(c => c.Id == source.ColorId) ?? Color;
        Rotation = source.Rotation;
        Mode = EditorMode.Build;
        Raise(BuildChange.None);
    }

    // ---- editing ------------------------------------------------------------------------------

    public bool CanPlaceAt(GridPos position) =>
        Mode == EditorMode.Build && Build.Check(Part, position, Rotation, Carrying?.Id) == PlacementError.None;

    /// <summary>Places the selected part, or drops the carried one.</summary>
    public void PlaceAt(GridPos position)
    {
        if (Mode != EditorMode.Build) return;

        if (Carrying is { } carried)
        {
            var moved = Build.Modify(carried.Id, position, Rotation, Color.Id);
            if (!moved.Success) return;
            Carrying = null;
            Commit(new Step(Before: carried, After: moved.Part));
            // The view hid the part on pick-up, so there is nothing of it left to remove.
            Raise(new BuildChange([moved.Part!], []));
            return;
        }

        var placed = Build.TryPlace(Part, position, Rotation, Color.Id);
        if (!placed.Success) return;
        Commit(new Step(Before: null, After: placed.Part));
        Raise(new BuildChange([placed.Part!], []));
    }

    public void Remove(int partId)
    {
        if (Carrying?.Id == partId || !TryGetPart(partId, out var part)) return;
        Build.Remove(partId);
        Commit(new Step(Before: part, After: null));
        Raise(new BuildChange([], [partId]));
    }

    /// <summary>Recolours a part with the selected colour.</summary>
    public void Paint(int partId)
    {
        if (!TryGetPart(partId, out var part) || part.ColorId == Color.Id || Carrying?.Id == partId) return;
        var painted = Build.Modify(partId, part.Position, part.Rotation, Color.Id).Part!;
        Commit(new Step(Before: part, After: painted));
        Raise(new BuildChange([painted], [partId]));
    }

    /// <summary>Lifts a part so the next <see cref="PlaceAt"/> moves it. Takes on its shape, colour and rotation.</summary>
    public void PickUp(int partId)
    {
        if (!TryGetPart(partId, out var part)) return;
        CancelCarryFirst();
        Carrying = part;
        Part = part.Part;
        Color = Palette.All.FirstOrDefault(c => c.Id == part.ColorId) ?? Color;
        Rotation = part.Rotation;
        Mode = EditorMode.Build;
        Raise(new BuildChange([], [partId]));
    }

    /// <summary>Puts a carried part back where it was.</summary>
    public void Cancel() => Raise(DropCarry());

    public void Undo()
    {
        CancelCarryFirst();
        if (!_undo.TryPop(out var step)) return;
        _redo.Push(step);
        Raise(Apply(from: step.After, to: step.Before));
    }

    public void Redo()
    {
        CancelCarryFirst();
        if (!_redo.TryPop(out var step)) return;
        _undo.Push(step);
        Raise(Apply(from: step.Before, to: step.After));
    }

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>One undoable action: a part going from one state to another (null = absent).</summary>
    private sealed record Step(PlacedPart? Before, PlacedPart? After);

    private void Commit(Step step)
    {
        _undo.Push(step);
        _redo.Clear();
    }

    private BuildChange Apply(PlacedPart? from, PlacedPart? to)
    {
        if (from is not null) Build.Remove(from.Id);
        if (to is not null) Build.Restore(to);
        return new BuildChange(to is null ? [] : [to], from is null ? [] : [from.Id]);
    }

    /// <summary>Ends a carry, returning the change that re-shows the part in its original place.</summary>
    private BuildChange DropCarry()
    {
        if (Carrying is not { } carried) return BuildChange.None;
        Carrying = null;
        return new BuildChange([carried], []);
    }

    /// <summary>
    /// Re-shows a carried part as its own change. Kept separate because the view applies removals
    /// before additions, and the action that follows may remove this very part.
    /// </summary>
    private void CancelCarryFirst()
    {
        if (Carrying is not null) Raise(DropCarry());
    }

    private bool TryGetPart(int partId, out PlacedPart part)
    {
        part = Build.Find(partId)!;
        return part is not null;
    }

    private void Raise(BuildChange change) => Changed?.Invoke(change);
}
