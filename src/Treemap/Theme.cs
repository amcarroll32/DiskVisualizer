using System.Windows;
using System.Windows.Media;
using DiskVisualizer.Model;

namespace DiskVisualizer.Treemap;

public enum ColorMode
{
    Type,
    Age,
}

/// <summary>
/// Dark-surface palette. The eight category colors are the validated dark categorical
/// slots (CVD-checked in this order); neutrals and textures cover the synthetic blocks.
/// </summary>
public static class Theme
{
    public static readonly Color Surface = Rgb(0x1e1e1c);

    private static readonly Color[] CategoryColors =
    [
        Rgb(0x3987e5), // Video        – blue
        Rgb(0xd95926), // Images       – orange
        Rgb(0x199e70), // Audio        – aqua
        Rgb(0xc98500), // Documents    – yellow
        Rgb(0xd55181), // Archives     – magenta
        Rgb(0x008300), // Programs     – green
        Rgb(0x9085e9), // Disk images  – violet
        Rgb(0xe66767), // Data/caches  – red
        Rgb(0x898781), // Other        – muted neutral
    ];

    // Sequential blue ramp for age: recent = deep, old = pale, so stale data stands out on the dark surface.
    private static readonly Color[] AgeColors =
    [
        Rgb(0x184f95), Rgb(0x256abf), Rgb(0x3987e5), Rgb(0x6da7ec), Rgb(0x9ec5f4), Rgb(0xcde2fb),
    ];

    private static readonly Color SmallFilesColor = Rgb(0x5e5d58);
    private static readonly Color FreeColor = Rgb(0x2a2a28);
    private static readonly Color UnreadableColor = Rgb(0x3a3533);

    private static readonly Brush[] CategoryFills = CategoryColors.Select(Cushion).ToArray();
    private static readonly Brush[] CategorySwatches = CategoryColors.Select(Solid).ToArray();
    private static readonly Brush[] CategoryText = CategoryColors.Select(TextFor).ToArray();
    private static readonly Brush[] AgeFills = AgeColors.Select(Cushion).ToArray();
    private static readonly Brush[] AgeSwatches = AgeColors.Select(Solid).ToArray();
    private static readonly Brush[] AgeText = AgeColors.Select(TextFor).ToArray();

    public static readonly Brush SurfaceBrush = Solid(Surface);
    public static readonly Brush SmallFilesFill = Cushion(SmallFilesColor);
    public static readonly Brush SmallFilesSwatch = Solid(SmallFilesColor);
    public static readonly Brush FreeFill = Hatch(FreeColor, Rgb(0x383835), 45);
    public static readonly Brush FreeSwatch = Solid(Rgb(0x383835));
    public static readonly Brush UnreadableFill = Hatch(UnreadableColor, Rgb(0x5a4f4b), 135);
    public static readonly Brush UnreadableSwatch = Solid(Rgb(0x5a4f4b));

    public static readonly Brush UnknownAgeFill = Cushion(Rgb(0x5e5d58));
    public static readonly Brush UnknownAgeSwatch = Solid(Rgb(0x5e5d58));

    /// <summary>Leaves that don't match the search.</summary>
    public static readonly Brush DimFill = Solid(Rgb(0x2a2a28));

    // Status colors for the space-hog badges (always paired with an icon and a label).
    public static readonly Brush StatusGood = Solid(Rgb(0x0ca30c));
    public static readonly Brush StatusWarning = Solid(Rgb(0xfab219));
    public static readonly Brush StatusCritical = Solid(Rgb(0xe66767));

    public static readonly Brush FolderBorder = Solid(Rgb(0x383835));
    private static readonly Brush[] FolderFills =
        [Solid(Rgb(0x262624)), Solid(Rgb(0x2e2e2b)), Solid(Rgb(0x353532)), Solid(Rgb(0x2b2b29)), Solid(Rgb(0x32322f))];

    public static readonly Brush PrimaryText = Solid(Colors.White);
    public static readonly Brush SecondaryText = Solid(Rgb(0xc3c2b7));
    public static readonly Brush MutedText = Solid(Rgb(0x898781));
    public static readonly Brush DarkText = Solid(Rgb(0x0b0b0b));

    public static readonly Brush Accent = Solid(Rgb(0x6da7ec));
    public static readonly Pen HoverPen = Frozen(new Pen(Solid(Colors.White), 2));
    public static readonly Pen TargetPen = Frozen(new Pen(Solid(Rgb(0x6da7ec)), 2));
    public static readonly Pen SelectPen = Frozen(new Pen(Solid(Rgb(0xfab219)), 2));
    public static readonly Pen MatchPen = Frozen(new Pen(Solid(Rgb(0xf0efec)), 1.5));
    public static readonly Brush SelectWash = Solid(Color.FromArgb(0x40, 0xfa, 0xb2, 0x19));
    public static readonly Brush ProgressTrack = Solid(Rgb(0x383835));

    public static Brush FolderFill(int depth) => FolderFills[depth % FolderFills.Length];

    public static Brush Fill(FsNode node, ColorMode mode, long nowTicks) => node.Kind switch
    {
        NodeKind.File or NodeKind.SmallFiles when mode == ColorMode.Age => AgeBrush(AgeFills, UnknownAgeFill, node, nowTicks),
        NodeKind.File => CategoryFills[(int)node.Category],
        NodeKind.SmallFiles => SmallFilesFill,
        NodeKind.FreeSpace => FreeFill,
        NodeKind.Unreadable => UnreadableFill,
        _ => FolderFill(0),
    };

    public static Brush LabelBrush(FsNode node, ColorMode mode, long nowTicks) => node.Kind switch
    {
        NodeKind.File or NodeKind.SmallFiles when mode == ColorMode.Age => AgeBrush(AgeText, PrimaryText, node, nowTicks),
        NodeKind.File => CategoryText[(int)node.Category],
        NodeKind.SmallFiles => PrimaryText,
        _ => SecondaryText,
    };

    public static Brush Swatch(FileCategory c) => CategorySwatches[(int)c];

    public static Brush AgeSwatch(int bucket) => bucket >= 0 ? AgeSwatches[bucket] : UnknownAgeSwatch;

    private static Brush AgeBrush(Brush[] brushes, Brush unknown, FsNode node, long nowTicks)
    {
        int bucket = AgeBuckets.Of(node.LastWrite, nowTicks);
        return bucket >= 0 ? brushes[bucket] : unknown;
    }

    public static Brush Swatch(FsNode node, ColorMode mode, long nowTicks) => node.Kind switch
    {
        NodeKind.File or NodeKind.SmallFiles when mode == ColorMode.Age => AgeBrush(AgeSwatches, UnknownAgeSwatch, node, nowTicks),
        NodeKind.File => CategorySwatches[(int)node.Category],
        NodeKind.SmallFiles => SmallFilesSwatch,
        NodeKind.FreeSpace => FreeSwatch,
        NodeKind.Unreadable => UnreadableSwatch,
        _ => FolderBorder,
    };

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Brush Solid(Color c) => Frozen(new SolidColorBrush(c));

    /// <summary>Subtle top-left highlight so adjacent same-colored files read as separate blocks.</summary>
    private static Brush Cushion(Color c)
    {
        var light = Blend(c, Colors.White, 0.18);
        var dark = Blend(c, Colors.Black, 0.18);
        return Frozen(new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 1)));
    }

    private static Brush Hatch(Color background, Color line, double angle)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(new SolidColorBrush(line), null, new Rect(0, 0, 8, 1.5));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(angle),
        };
        return Frozen(brush);
    }

    private static Brush TextFor(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        // Pick whichever ink has higher contrast against the fill.
        double vsWhite = 1.05 / (lum + 0.05);
        double vsBlack = (lum + 0.05) / 0.05;
        return vsBlack >= vsWhite ? Solid(Rgb(0x0b0b0b)) : Solid(Colors.White);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
