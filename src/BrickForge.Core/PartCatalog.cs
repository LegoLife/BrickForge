namespace BrickForge.Core;

public static class PartCatalog
{
    private static readonly (int W, int D)[] BrickSizes =
        [(1, 1), (1, 2), (1, 3), (1, 4), (1, 6), (1, 8), (2, 2), (2, 3), (2, 4), (2, 6), (2, 8)];

    private static readonly (int W, int D)[] PlateSizes =
        [.. BrickSizes, (4, 4), (4, 6), (4, 8), (6, 6), (6, 8)];

    private static readonly (int W, int D)[] TileSizes =
        [(1, 1), (1, 2), (1, 3), (1, 4), (1, 6), (1, 8), (2, 2), (2, 4)];

    public static IReadOnlyList<PartType> All { get; } =
    [
        .. BrickSizes.Select(s => Create(PartKind.Brick, s)),
        .. PlateSizes.Select(s => Create(PartKind.Plate, s)),
        .. TileSizes.Select(s => Create(PartKind.Tile, s)),
    ];

    private static readonly Dictionary<string, PartType> ById = All.ToDictionary(p => p.Id);

    public static PartType Get(string id) =>
        ById.TryGetValue(id, out var part) ? part : throw new KeyNotFoundException($"Unknown part '{id}'.");

    public static bool TryGet(string id, out PartType part) => ById.TryGetValue(id, out part!);

    private static PartType Create(PartKind kind, (int W, int D) size) =>
        new($"{kind.ToString().ToLowerInvariant()}-{size.W}x{size.D}", $"{kind} {size.W}×{size.D}", kind, size.W, size.D);
}
