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

public enum EditorMode { Build, Paint, Select }

/// <summary>
/// A group following the cursor instead of the plain part tool: a paste (<see cref="Moving"/> empty,
/// stamped until cancelled) or parts picked up to be moved (hidden from the view until dropped).
/// </summary>
public sealed record HeldGroup(PartGroup Group, IReadOnlyList<PlacedPart> Moving)
{
    public bool IsPaste => Moving.Count == 0;
}

/// <summary>
/// Everything the user can do to a build: the current tool (part, colour, rotation, mode), placing,
/// removing, painting, the eyedropper, selecting, the clipboard, moving parts, and undo/redo.
/// Every call raises <see cref="Changed"/> — with an empty change when only tool or selection state moved.
/// </summary>
public sealed class Editor(Build build)
{
    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private readonly HashSet<int> _selection = [];

    private PartGroup? _clipboard;
    private PartGroup? _heldBase;                // the held group before rotation
    private IReadOnlyList<PlacedPart> _moving = [];
    private int _heldColorId;
    private Rotation _toolRotation;              // the part tool's rotation, restored when a hold ends

    public event Action<BuildChange>? Changed;

    public Build Build { get; } = build;
    public PartType Part { get; private set; } = PartCatalog.Get("brick-2x4");
    public BrickColor Color { get; private set; } = Palette.All[0];

    /// <summary>The part tool's rotation, or — while a group is held — the held group's turn from how it was picked up.</summary>
    public Rotation Rotation { get; private set; } = Rotation.R0;

    public EditorMode Mode { get; private set; } = EditorMode.Build;

    public IReadOnlySet<int> Selection => _selection;
    public bool HasClipboard => _clipboard is not null;

    public HeldGroup? Held => _heldBase is null ? null : new HeldGroup(CurrentHeldGroup(), _moving);

    /// <summary>What a click in Build mode would place: the held group, or the selected part.</summary>
    public PartGroup Ghost => _heldBase is null ? PartGroup.Single(Part, Rotation, Color.Id) : CurrentHeldGroup();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---- tool state ---------------------------------------------------------------------------

    public void SelectPart(PartType part)
    {
        var change = EndHold();
        Part = part;
        Mode = EditorMode.Build;
        Raise(change);
    }

    /// <summary>While holding a single part, this also recolours it when dropped.</summary>
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
        var change = mode == EditorMode.Build ? BuildChange.None : EndHold();
        Mode = mode;
        Raise(change);
    }

    public void Eyedrop(int partId)
    {
        if (_heldBase is not null || !TryGetPart(partId, out var source)) return;
        Part = source.Part;
        Color = Palette.All.FirstOrDefault(c => c.Id == source.ColorId) ?? Color;
        Rotation = source.Rotation;
        Mode = EditorMode.Build;
        Raise(BuildChange.None);
    }

    // ---- selection ----------------------------------------------------------------------------

    /// <summary>Selects one part, or with <paramref name="additive"/> toggles it. Null clears (unless additive).</summary>
    public void Select(int? partId, bool additive)
    {
        if (!additive) _selection.Clear();
        if (partId is { } id && Build.Find(id) is not null && !_selection.Remove(id))
            _selection.Add(id);
        Raise(BuildChange.None);
    }

    /// <summary>Box-select: replaces the selection, or with <paramref name="additive"/> adds to it.</summary>
    public void SelectMany(IEnumerable<int> partIds, bool additive)
    {
        if (!additive) _selection.Clear();
        _selection.UnionWith(partIds.Where(id => Build.Find(id) is not null));
        Raise(BuildChange.None);
    }

    public void SelectAll() => SelectMany(Build.Parts.Select(p => p.Id), additive: false);

    public void ClearSelection() => Select(null, additive: false);

    // ---- editing ------------------------------------------------------------------------------

    public bool CanPlaceAt(GridPos position) =>
        Mode == EditorMode.Build && Build.CheckGroup([.. Ghost.At(position)], MovingIds()) == PlacementError.None;

    /// <summary>Places the selected part, stamps the pasted group, or drops the moving parts.</summary>
    public void PlaceAt(GridPos position)
    {
        if (Mode != EditorMode.Build) return;

        var placements = Ghost.At(position).ToList();
        if (Build.CheckGroup(placements, MovingIds()) != PlacementError.None) return;

        if (_moving.Count > 0)
        {
            // Placements are in the same order as the parts that were picked up, so ids carry over.
            var before = _moving;
            var after = placements
                .Select((p, i) => new PlacedPart(before[i].Id, p.Part, p.Position, p.Rotation, p.ColorId))
                .ToList();
            _moving = [];            // already hidden in the view; the drop re-adds them
            EndHold();
            var step = new Step(before, after);
            Commit(step);
            Raise(Apply(step.Before, step.After));
            return;
        }

        var added = Build.AddGroup(placements);
        Commit(new Step([], added));
        if (_heldBase is not null) // a paste: keep stamping, and select what was just placed
        {
            _selection.Clear();
            _selection.UnionWith(added.Select(p => p.Id));
        }
        Raise(new BuildChange(added, []));
    }

    public void Remove(int partId)
    {
        if (IsMoving(partId) || !TryGetPart(partId, out var part)) return;
        Build.Remove(partId);
        Commit(Step.Of(part, null));
        Raise(new BuildChange([], [partId]));
    }

    public void DeleteSelection()
    {
        CancelHoldFirst();
        var parts = SelectedParts();
        if (parts.Count == 0) return;
        var step = new Step(parts, []);
        Commit(step);
        Raise(Apply(step.Before, step.After));
    }

    /// <summary>
    /// Recolours a part with the selected colour — or, if the part is selected, every selected part.
    /// </summary>
    public void Paint(int partId)
    {
        if (IsMoving(partId) || !TryGetPart(partId, out var part)) return;
        var targets = _selection.Contains(partId) ? SelectedParts() : [part];
        var before = targets.Where(p => p.ColorId != Color.Id).ToList();
        if (before.Count == 0) return;
        var step = new Step(before, [.. before.Select(p => p with { ColorId = Color.Id })]);
        Commit(step);
        Raise(Apply(step.Before, step.After));
    }

    // ---- clipboard ----------------------------------------------------------------------------

    public void Copy()
    {
        var parts = SelectedParts();
        if (parts.Count == 0) return;
        _clipboard = PartGroup.From(parts);
        Raise(BuildChange.None);
    }

    /// <summary>Copies the selection, then deletes it as one undo step.</summary>
    public void Cut()
    {
        if (_selection.Count == 0) return;
        Copy();
        DeleteSelection();
    }

    /// <summary>Holds the clipboard under the cursor; each <see cref="PlaceAt"/> stamps a copy until <see cref="Cancel"/>.</summary>
    public void Paste()
    {
        if (_clipboard is null) return;
        CancelHoldFirst();
        BeginHold(_clipboard, []);
        Raise(BuildChange.None);
    }

    // ---- moving -------------------------------------------------------------------------------

    /// <summary>
    /// Lifts parts so the next <see cref="PlaceAt"/> moves them: the whole selection if the part is
    /// selected, otherwise just this part. They stay in the model, hidden, until dropped or cancelled.
    /// </summary>
    public void PickUp(int partId)
    {
        if (IsMoving(partId) || !TryGetPart(partId, out var part)) return;
        CancelHoldFirst();
        var parts = _selection.Contains(partId) ? SelectedParts() : [part];
        BeginHold(PartGroup.From(parts), parts);
        Raise(new BuildChange([], [.. parts.Select(p => p.Id)]));
    }

    /// <summary>Stops pasting or puts moving parts back; with nothing held, clears the selection.</summary>
    public void Cancel()
    {
        if (_heldBase is not null)
        {
            Raise(EndHold());
            return;
        }
        ClearSelection();
    }

    // ---- whole-build operations ---------------------------------------------------------------

    /// <summary>
    /// Swaps the whole build — baseplate and parts — for another (e.g. an imported file), as one undo step.
    /// The parts must fit <paramref name="baseplate"/> without overlapping.
    /// </summary>
    public void ReplaceAll(Baseplate baseplate, IReadOnlyList<PlacedPart> parts)
    {
        CancelHoldFirst();
        var before = Build.Parts.ToList();
        if (before.Count == 0 && parts.Count == 0 && baseplate == Build.Baseplate) return;
        var step = new Step(before, parts, Build.Baseplate, baseplate);
        Commit(step);
        Raise(Apply(step.Before, step.After, step.PlateAfter));
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
        CancelHoldFirst();
        if (!_undo.TryPop(out var step)) return;
        _redo.Push(step);
        Raise(Apply(step.After, step.Before, step.PlateBefore));
    }

    public void Redo()
    {
        CancelHoldFirst();
        if (!_redo.TryPop(out var step)) return;
        _undo.Push(step);
        Raise(Apply(step.Before, step.After, step.PlateAfter));
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

    private void BeginHold(PartGroup group, IReadOnlyList<PlacedPart> moving)
    {
        _toolRotation = Rotation;
        Rotation = Rotation.R0;
        _heldBase = group;
        _moving = moving;
        _heldColorId = Color.Id;
        Mode = EditorMode.Build;
    }

    /// <summary>Ends a hold, returning the change that re-shows any parts that were being moved.</summary>
    private BuildChange EndHold()
    {
        if (_heldBase is null) return BuildChange.None;
        var reshow = new BuildChange(_moving, []);
        _heldBase = null;
        _moving = [];
        Rotation = _toolRotation;
        return reshow;
    }

    /// <summary>
    /// Ends a hold as its own change. Kept separate because the view applies removals before
    /// additions, and the action that follows may remove the very parts being re-shown.
    /// </summary>
    private void CancelHoldFirst()
    {
        if (_heldBase is not null) Raise(EndHold());
    }

    private PartGroup CurrentHeldGroup()
    {
        var group = _heldBase!.Rotated((int)Rotation);
        // Picking a colour while holding a single part recolours it; groups keep their colours.
        return group.Parts.Count == 1 && Color.Id != _heldColorId ? group.WithColor(Color.Id) : group;
    }

    private IReadOnlySet<int>? MovingIds() => _moving.Count == 0 ? null : _moving.Select(p => p.Id).ToHashSet();

    private bool IsMoving(int partId) => _moving.Any(p => p.Id == partId);

    private List<PlacedPart> SelectedParts() =>
        _selection.Select(Build.Find).OfType<PlacedPart>().OrderBy(p => p.Id).ToList();

    private static bool Fits(PlacedPart part, Baseplate baseplate)
    {
        var (sizeX, sizeZ) = part.Part.Footprint(part.Rotation);
        var p = part.Position;
        return p.X >= 0 && p.Z >= 0 && p.X + sizeX <= baseplate.WidthStuds && p.Z + sizeZ <= baseplate.DepthStuds;
    }

    private bool TryGetPart(int partId, out PlacedPart part)
    {
        part = Build.Find(partId)!;
        return part is not null;
    }

    private void Raise(BuildChange change)
    {
        _selection.RemoveWhere(id => Build.Find(id) is null); // removed parts leave the selection
        Changed?.Invoke(change);
    }
}
