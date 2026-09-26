namespace BrickForge.Core;

/// <summary>A grid cell: x and z in studs from the baseplate's corner, y in plates above its surface.</summary>
public readonly record struct GridPos(int X, int Y, int Z);

/// <summary>A part in the build. <see cref="Position"/> is its minimum corner after rotation.</summary>
public sealed record PlacedPart(int Id, PartType Part, GridPos Position, Rotation Rotation, int ColorId)
{
    public IEnumerable<GridPos> Cells() => Build.CellsOf(Part, Position, Rotation);
}

public enum PlacementError { None, OutOfBounds, Overlaps, NotConnected }

public readonly record struct PlaceResult(PlacedPart? Part, PlacementError Error)
{
    public bool Success => Error == PlacementError.None;
}

/// <summary>
/// The build model and its placement rules: parts stay on the baseplate, never overlap,
/// and each new part must join at least one stud — onto a part below, or into the underside of one above.
/// </summary>
public sealed class Build(Baseplate baseplate)
{
    private readonly Dictionary<int, PlacedPart> _parts = new();
    private readonly Dictionary<GridPos, PlacedPart> _occupied = new();
    private int _nextId = 1;

    public Baseplate Baseplate { get; private set; } = baseplate;

    /// <summary>Swaps the baseplate. Only allowed while empty; callers re-add parts afterwards.</summary>
    internal void SetBaseplate(Baseplate baseplate)
    {
        if (_parts.Count > 0)
            throw new InvalidOperationException("The baseplate can only be changed while the build is empty.");
        Baseplate = baseplate;
    }

    public IReadOnlyCollection<PlacedPart> Parts => _parts.Values;

    public PlacedPart? PartAt(GridPos cell) => _occupied.GetValueOrDefault(cell);

    public PlacedPart? Find(int partId) => _parts.GetValueOrDefault(partId);

    /// <param name="ignorePartId">A part being moved: its own cells neither block nor support the check.</param>
    public PlacementError Check(PartType part, GridPos position, Rotation rotation, int? ignorePartId = null) =>
        CheckGroup(
            [new PartPlacement(part, position, rotation, 0)],
            ignorePartId is { } id ? new HashSet<int> { id } : null);

    /// <summary>
    /// Checks placing several parts together: each must be on the baseplate and overlap nothing
    /// (nor each other), and at least one must join the existing build or the baseplate. Parts in
    /// the group need not join the build individually — they hold on to each other.
    /// </summary>
    /// <param name="ignore">Parts being moved: their own cells neither block nor support the check.</param>
    public PlacementError CheckGroup(IReadOnlyList<PartPlacement> placements, IReadOnlySet<int>? ignore = null)
    {
        foreach (var p in placements)
        {
            var (sizeX, sizeZ) = p.Part.Footprint(p.Rotation);
            if (p.Position.X < 0 || p.Position.Z < 0 || p.Position.Y < 0 ||
                p.Position.X + sizeX > Baseplate.WidthStuds || p.Position.Z + sizeZ > Baseplate.DepthStuds)
                return PlacementError.OutOfBounds;
        }

        var claimed = new HashSet<GridPos>();
        foreach (var p in placements)
        foreach (var cell in CellsOf(p.Part, p.Position, p.Rotation))
        {
            if (OccupantOf(cell, ignore) is not null || !claimed.Add(cell))
                return PlacementError.Overlaps;
        }

        return placements.Any(p => IsConnected(p.Part, p.Position, p.Rotation, ignore))
            ? PlacementError.None
            : PlacementError.NotConnected;
    }

    /// <summary>Adds parts already validated with <see cref="CheckGroup"/>, giving each a new id.</summary>
    public IReadOnlyList<PlacedPart> AddGroup(IReadOnlyList<PartPlacement> placements)
    {
        if (CheckGroup(placements) is var error and not PlacementError.None)
            throw new InvalidOperationException($"Cannot add group: {error}.");
        var added = placements
            .Select(p => new PlacedPart(_nextId++, p.Part, p.Position, p.Rotation, p.ColorId))
            .ToList();
        foreach (var part in added) Insert(part);
        return added;
    }

    public PlaceResult TryPlace(PartType part, GridPos position, Rotation rotation, int colorId)
    {
        var error = Check(part, position, rotation);
        if (error != PlacementError.None) return new PlaceResult(null, error);

        var placed = new PlacedPart(_nextId++, part, position, rotation, colorId);
        Insert(placed);
        return new PlaceResult(placed, PlacementError.None);
    }

    /// <summary>Moves, rotates and/or recolours a part in one step, keeping its id. The part stays put if rejected.</summary>
    public PlaceResult Modify(int partId, GridPos position, Rotation rotation, int colorId)
    {
        if (!_parts.TryGetValue(partId, out var current))
            throw new KeyNotFoundException($"No part with id {partId}.");

        var error = Check(current.Part, position, rotation, ignorePartId: partId);
        if (error != PlacementError.None) return new PlaceResult(null, error);

        var updated = current with { Position = position, Rotation = rotation, ColorId = colorId };
        Remove(partId);
        Insert(updated);
        return new PlaceResult(updated, PlacementError.None);
    }

    /// <summary>Removes a part. Parts that were only held by it are left in place.</summary>
    public bool Remove(int partId)
    {
        if (!_parts.Remove(partId, out var placed)) return false;
        foreach (var cell in placed.Cells()) _occupied.Remove(cell);
        return true;
    }

    /// <summary>
    /// Puts a part in exactly as given, keeping its id and skipping the connection rule: for undo/redo
    /// and loading saved builds, where parts may legitimately float.
    /// </summary>
    /// <exception cref="InvalidOperationException">The part is off the baseplate or overlaps another.</exception>
    internal void Restore(PlacedPart part)
    {
        var (sizeX, sizeZ) = part.Part.Footprint(part.Rotation);
        var p = part.Position;
        if (p.X < 0 || p.Y < 0 || p.Z < 0 || p.X + sizeX > Baseplate.WidthStuds || p.Z + sizeZ > Baseplate.DepthStuds)
            throw new InvalidOperationException("it is outside the baseplate.");
        if (part.Cells().Any(_occupied.ContainsKey))
            throw new InvalidOperationException("it overlaps another part.");
        if (_parts.ContainsKey(part.Id))
            throw new InvalidOperationException($"part id {part.Id} is already in use.");
        Insert(part);
        _nextId = Math.Max(_nextId, part.Id + 1);
    }

    private void Insert(PlacedPart part)
    {
        _parts.Add(part.Id, part);
        foreach (var cell in part.Cells()) _occupied.Add(cell, part);
    }

    private PlacedPart? OccupantOf(GridPos cell, IReadOnlySet<int>? ignore) =>
        _occupied.TryGetValue(cell, out var p) && ignore?.Contains(p.Id) != true ? p : null;

    private bool IsConnected(PartType part, GridPos position, Rotation rotation, IReadOnlySet<int>? ignore)
    {
        var (sizeX, sizeZ) = part.Footprint(rotation);
        if (position.Y == 0) return true; // baseplate studs

        var below = position.Y - 1;
        var above = position.Y + part.HeightPlates;
        for (var x = position.X; x < position.X + sizeX; x++)
        for (var z = position.Z; z < position.Z + sizeZ; z++)
        {
            // The cell directly below is necessarily the top layer of whatever occupies it.
            if (OccupantOf(new GridPos(x, below, z), ignore) is { Part.HasStuds: true })
                return true;
            // Every part has anti-studs underneath; ours needs studs to push into them.
            if (part.HasStuds && OccupantOf(new GridPos(x, above, z), ignore) is not null)
                return true;
        }
        return false;
    }

    internal static IEnumerable<GridPos> CellsOf(PartType part, GridPos position, Rotation rotation)
    {
        var (sizeX, sizeZ) = part.Footprint(rotation);
        for (var x = 0; x < sizeX; x++)
        for (var y = 0; y < part.HeightPlates; y++)
        for (var z = 0; z < sizeZ; z++)
            yield return new GridPos(position.X + x, position.Y + y, position.Z + z);
    }
}
