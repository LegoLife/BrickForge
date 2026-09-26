namespace BrickForge.Core;

public sealed record BrickColor(int Id, string Name, string Hex, bool IsTransparent = false);

public static class Palette
{
    public static IReadOnlyList<BrickColor> All { get; } =
    [
        new(0, "Red", "#C91A09"),
        new(1, "Dark Red", "#720E0F"),
        new(2, "Orange", "#FE8A18"),
        new(3, "Yellow", "#F2CD37"),
        new(4, "Lime", "#BBE90B"),
        new(5, "Green", "#237841"),
        new(6, "Dark Green", "#184632"),
        new(7, "Medium Azure", "#36AEBF"),
        new(8, "Blue", "#0055BF"),
        new(9, "Dark Blue", "#0A3463"),
        new(10, "Lavender", "#AC78BA"),
        new(11, "Magenta", "#923978"),
        new(12, "Pink", "#E4ADC8"),
        new(13, "Tan", "#E4CD9E"),
        new(14, "Reddish Brown", "#582A12"),
        new(15, "White", "#F4F4F4"),
        new(16, "Light Gray", "#A0A5A9"),
        new(17, "Dark Gray", "#6C6E68"),
        new(18, "Black", "#1B2A34"),
        new(19, "Trans Clear", "#FCFCFC", IsTransparent: true),
        new(20, "Trans Red", "#C91A09", IsTransparent: true),
        new(21, "Trans Light Blue", "#AEEFEC", IsTransparent: true),
        new(22, "Trans Yellow", "#F5CD2F", IsTransparent: true),
    ];
}
