namespace BrickForge.Core;

/// <summary>
/// What the view must redraw: remove <see cref="Removed"/> first, then draw <see cref="Added"/>.
/// A modified part appears in both, under the same id. <see cref="Baseplate"/> is set when the
/// baseplate changed, in which case every part is removed and re-added.
/// </summary>
public sealed record BuildChange(IReadOnlyList<PlacedPart> Added, IReadOnlyList<int> Removed, Baseplate? Baseplate = null)
{
    public static BuildChange None { get; } = new([], []);

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Baseplate is null;
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
            Commit(Step.Of(carried, moved.Part));
            // The view hid the part on pick-up, so there is nothing of it left to remove.
            Raise(new BuildChange([moved.Part!], []));
            return;
        }

        var placed = Build.TryPlace(Part, position, Rotation, Color.Id);
        if (!placed.Success) return;
        Commit(Step.Of(null, placed.Part));
        Raise(new BuildChange([placed.Part!], []));
    }

    public void Remove(int partId)
    {
        if (Carrying?.Id == partId || !TryGetPart(partId, out var part)) return;
        Build.Remove(partId);
        Commit(Step.Of(part, null));
        Raise(new BuildChange([], [partId]));
    }

    /// <summary>Recolours a part with the selected colour.</summary>
    public void Paint(int partId)
    {
        if (!TryGetPart(partId, out var part) || part.ColorId == Color.Id || Carrying?.Id == partId) return;
        var painted = Build.Modify(partId, part.Position, part.Rotation, Color.Id).Part!;
        Commit(Step.Of(part, painted));
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

    /// <summary>
    /// Swaps the whole build — baseplate and parts — for another (e.g. an imported file), as one undo step.
    /// The parts must fit <paramref name="baseplate"/> without overlapping.
    /// </summary>
    public void ReplaceAll(Baseplate baseplate, IReadOnlyList<PlacedPart> parts)
    {
        CancelCarryFirst();
        var before = Build.Parts.ToList();
        if (before.Count == 0 && parts.Count == 0 && baseplate == Build.Baseplate) return;
        var step = new Step(before, parts, Build.Baseplate, baseplate);
        Commit(step);
        Raise(Apply(from: step.Before, to: step.After, step.PlateAfter));
    }

    /// <summary>Starts an empty build on a baseplate of the given size, as one undo step.</summary>
    public void NewBuild(Baseplate baseplate) => ReplaceAll(baseplate, []);

    /// <summary>Removes every part, keeping the baseplate, as one undo step.</summary>
    public void Clear() => NewBuild(Build.Baseplate);

    /// <summary>
    /// Changes the baseplate size, growing or shrinking evenly around the centre so parts keep their
    /// place relative to it. Refused (returns false) if any part would fall off the edge.
    /// </summary>
    public bool Resize(Baseplate baseplate)
    {
        if (baseplate == Build.Baseplate) return true;

        // Integer division: an odd difference leaves the extra stud on the far side.
        var dx = (baseplate.WidthStuds - Build.Baseplate.WidthStuds) / 2;
        var dz = (baseplate.DepthStuds - Build.Baseplate.DepthStuds) / 2;
        var shifted = Build.Parts
            .Select(p => p with { Position = p.Position with { X = p.Position.X + dx, Z = p.Position.Z + dz } })
            .ToList();
        if (shifted.Any(p => !Fits(p, baseplate))) return false;

        ReplaceAll(baseplate, shifted);
        return true;
    }

    /// <summary>Drops undo/redo, e.g. after restoring an autosave so the first undo can't empty the build.</summary>
    public void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        Raise(BuildChange.None);
    }

    public void Undo()
    {
        CancelCarryFirst();
        if (!_undo.TryPop(out var step)) return;
        _redo.Push(step);
        Raise(Apply(from: step.After, to: step.Before, step.PlateBefore));
    }

    public void Redo()
    {
        CancelCarryFirst();
        if (!_redo.TryPop(out var step)) return;
        _undo.Push(step);
        Raise(Apply(from: step.Before, to: step.After, step.PlateAfter));
    }

    // ---- internals ----------------------------------------------------------------------------

    /// <summary>
    /// One undoable action: a set of parts replaced by another (either may be empty). When the baseplate
    /// changes too, <see cref="Before"/> holds every part, so the build is empty while it is swapped.
    /// </summary>
    private sealed record Step(
        IReadOnlyList<PlacedPart> Before,
        IReadOnlyList<PlacedPart> After,
        Baseplate? PlateBefore = null,
        Baseplate? PlateAfter = null)
    {
        public static Step Of(PlacedPart? before, PlacedPart? after) =>
            new(before is null ? [] : [before], after is null ? [] : [after]);
    }

    private void Commit(Step step)
    {
        _undo.Push(step);
        _redo.Clear();
    }

    private BuildChange Apply(IReadOnlyList<PlacedPart> from, IReadOnlyList<PlacedPart> to, Baseplate? plate = null)
    {
        foreach (var part in from) Build.Remove(part.Id);
        var plateChanged = plate is not null && plate != Build.Baseplate;
        if (plateChanged) Build.SetBaseplate(plate!);
        foreach (var part in to) Build.Restore(part);
        return new BuildChange(to, [.. from.Select(p => p.Id)], plateChanged ? plate : null);
    }

    private static bool Fits(PlacedPart part, Baseplate baseplate)
    {
        var (sizeX, sizeZ) = part.Part.Footprint(part.Rotation);
        var p = part.Position;
        return p.X >= 0 && p.Z >= 0 && p.X + sizeX <= baseplate.WidthStuds && p.Z + sizeZ <= baseplate.DepthStuds;
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
