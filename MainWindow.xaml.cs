using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskVisualizer.Model;
using DiskVisualizer.Scanning;
using DiskVisualizer.Treemap;

namespace DiskVisualizer;

public partial class MainWindow : Window
{
    private const int MaxListRows = 1000;
    private const int MaxLargest = 200;
    private const int MaxHogLocations = 4;

    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly bool _ready;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _hogsCts;
    private List<DriveScanner> _scanners = [];
    private FsNode? _pc;
    private FsNode? _current;
    private SearchMatcher? _matcher;
    private ColorMode _colorMode = ColorMode.Type;
    private Stopwatch _elapsed = new();
    private TimeSpan _lastScanFinished;
    private readonly List<string> _probing = [];
    private readonly List<string> _notReady = [];

    public MainWindow()
    {
        InitializeComponent();

        Map.HoverChanged += Map_HoverChanged;
        Map.NodeClicked += Map_NodeClicked;
        Map.NodeRightClicked += Map_NodeRightClicked;
        Map.MouseMove += Map_MouseMove;

        LargestTypeBox.Items.Add("All types");
        foreach (var category in Enum.GetValues<FileCategory>())
            LargestTypeBox.Items.Add(FileCategories.DisplayName(category));
        LargestTypeBox.SelectedIndex = 0;

        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, ProgressTick, Dispatcher);
        _progressTimer.Stop();
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Input, (_, _) => ApplySearch(), Dispatcher);
        _searchTimer.Stop();

        Loaded += (_, _) => StartScan();
        Closed += (_, _) => _cts?.Cancel();
        _ready = true;
    }

    // ---- Scanning ----

    private async void StartScan()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var pc = new FsNode("This PC", NodeKind.Root, null) { Children = [] };
        _pc = pc;
        _scanners = [];
        _probing.Clear();
        _notReady.Clear();
        _elapsed = Stopwatch.StartNew();
        _lastScanFinished = TimeSpan.Zero;
        _hogsCts?.Cancel();
        HogsList.ItemsSource = null;
        HogsTab.Header = "Space hogs";
        HogsSummary.Text = "Space hogs are listed as drives finish scanning…";
        NavigateTo(pc);
        _progressTimer.Start();
        UpdateStatus();

        // Even listing drive types can stall on a misbehaving device, so stay off the UI thread.
        var drives = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .ToList());
        if (cts.IsCancellationRequested)
            return;

        _probing.AddRange(drives.Select(d => d.Name));
        UpdateStatus();

        await Task.WhenAll(drives.Select(d => ProbeAndScan(d, pc, cts.Token)));

        if (cts.IsCancellationRequested)
            return;
        _elapsed.Stop();
        _progressTimer.Stop();
        UpdateStatus();
        if (_scanners.Count == 0)
            HogsSummary.Text = "No drives were scanned.";
    }

    private sealed record DriveProbe(string Name, string Label, long TotalSize, long FreeSize);

    /// <summary>
    /// Checks a drive on a background thread (an unready drive can take 20+ seconds to say so),
    /// then adds it to the overview and scans it. Drives appear as soon as they respond.
    /// </summary>
    private async Task ProbeAndScan(DriveInfo drive, FsNode pc, CancellationToken ct)
    {
        var probe = await Task.Run(() =>
        {
            try
            {
                return drive.IsReady
                    ? new DriveProbe(drive.Name, drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // vanished or locked (e.g. BitLocker)
            }
        });

        if (ct.IsCancellationRequested || pc != _pc)
            return;

        _probing.Remove(drive.Name);
        if (probe == null)
        {
            _notReady.Add(drive.Name);
            UpdateStatus();
            return;
        }

        var node = new FsNode(probe.Name, NodeKind.Drive, pc)
        {
            Label = probe.Label,
            Size = probe.TotalSize,
            FreeSize = probe.FreeSize,
            IsScanning = true,
        };
        pc.Children!.Add(node);
        var scanner = new DriveScanner(node, probe.TotalSize, probe.FreeSize);
        _scanners.Add(scanner);
        RecomputeRoot(pc);
        Map.Invalidate();
        RefreshSidePanel();
        UpdateStatus();

        await RunScanner(scanner, pc, ct);
    }

    private async Task RunScanner(DriveScanner scanner, FsNode pc, CancellationToken ct)
    {
        try
        {
            await scanner.ScanAsync(ct);
            scanner.Finish();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            scanner.Drive.ScanError = ex.Message;
        }

        if (ct.IsCancellationRequested || pc != _pc)
            return;

        _lastScanFinished = _elapsed.Elapsed;
        RecomputeRoot(pc);
        Map.Invalidate();
        RefreshSidePanel();
        RefreshQuery();
        RefreshHogs();
        UpdateStatus();
    }

    private static void RecomputeRoot(FsNode pc)
    {
        var drives = pc.Children!;
        drives.Sort((a, b) => b.Size.CompareTo(a.Size));
        pc.Size = drives.Sum(d => d.Size);
        pc.FreeSize = drives.Sum(d => d.FreeSize);
        pc.FileCount = drives.Sum(d => d.FileCount);
        pc.DirCount = drives.Sum(d => d.DirCount);
        pc.DeniedCount = drives.Sum(d => d.DeniedCount);
        pc.LastWrite = drives.Count > 0 ? drives.Max(d => d.LastWrite) : 0;
    }

    /// <summary>Finished drives under the view (scanning drives have no tree yet).</summary>
    private static List<FsNode> ViewRoots(FsNode view) =>
        view.Kind == NodeKind.Root
            ? view.Children!.Where(d => !d.IsScanning && d.ScanError == null).ToList()
            : [view];

    private void ProgressTick(object? sender, EventArgs e)
    {
        bool anyScanning = false;
        foreach (var s in _scanners)
        {
            if (!s.Drive.IsScanning || s.Drive.ScanError != null)
                continue;
            anyScanning = true;
            s.Drive.ScanProgress = s.UsedSize > 0 ? Math.Min(0.99, (double)s.ScannedBytes / s.UsedSize) : 0;
        }

        // Only the overview shows live progress tiles.
        if (anyScanning && _current == _pc)
            Map.Invalidate();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        long files = _scanners.Sum(s => s.ScannedFiles);
        long dirs = _scanners.Sum(s => s.ScannedDirs);
        long bytes = _scanners.Sum(s => s.ScannedBytes);
        long denied = _scanners.Sum(s => s.DeniedDirs);
        var active = _scanners.Where(s => s.Drive.IsScanning && s.Drive.ScanError == null).ToList();
        string waiting = _probing.Count > 0 ? $"  ·  waiting for {string.Join(", ", _probing)} to respond" : "";

        if (_scanners.Count == 0)
        {
            StatusText.Text = _probing.Count > 0 || _elapsed.IsRunning
                ? "Looking for drives…" + waiting
                : "No ready fixed or removable drives found.";
        }
        else if (active.Count > 0)
        {
            string where = active[0].CurrentPath ?? active[0].Drive.Name;
            StatusText.Text = $"Scanning {active.Count} of {_scanners.Count} drives…  {Format.Count(files)} files  ·  " +
                              $"{Format.Bytes(bytes)}  ·  {Format.Duration(_elapsed.Elapsed)}{waiting}  ·  {where}";
        }
        else
        {
            string text = $"Scanned {Format.Count(_scanners.Count, "drive", "drives")} in {Format.Duration(_lastScanFinished)}  ·  " +
                          $"{Format.Count(files)} files  ·  {Format.Count(dirs)} folders";
            if (denied > 0)
                text += $"  ·  {Format.Count(denied, "folder", "folders")} couldn't be read (counted as Unreadable / other)";
            var failed = _scanners.Where(s => s.Drive.ScanError != null).ToList();
            if (failed.Count > 0)
                text += $"  ·  failed: {string.Join(", ", failed.Select(f => f.Drive.Name))}";
            if (_notReady.Count > 0)
                text += $"  ·  not ready: {string.Join(", ", _notReady)}";
            StatusText.Text = text + waiting;
        }
    }

    // ---- Navigation ----

    private void NavigateTo(FsNode node)
    {
        _current = node;
        Map.Selected = null;
        Map.Root = node;
        UpButton.IsEnabled = node.Parent != null;
        Tip.Visibility = Visibility.Collapsed;
        BuildBreadcrumbs();
        RefreshSidePanel();
        RefreshQuery();
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
            NavigateTo(parent);
    }

    private bool CanOpen(FsNode node) => node.IsContainer && !node.IsScanning && node.ScanError == null;

    private void TryOpen(FsNode node)
    {
        if (node == _current)
            return;
        if (CanOpen(node))
            NavigateTo(node);
        else if (node.IsScanning)
            StatusText.Text = $"{node.DisplayName} is still being scanned – it will open once the scan finishes.";
    }

    /// <summary>Opens the node's folder and highlights the node in it.</summary>
    private void ShowOnMap(FsNode node)
    {
        var parent = node.Parent;
        if (parent == null)
        {
            NavigateTo(node);
            return;
        }
        if (parent != _current)
        {
            if (!CanOpen(parent))
                return;
            NavigateTo(parent);
        }
        Map.Selected = node;
    }

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Children.Clear();
        if (_current == null)
            return;

        var chain = _current.Ancestors().Reverse().Append(_current).ToList();
        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0)
            {
                Breadcrumbs.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("Icons"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("InkMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0),
                });
            }

            var button = new Button
            {
                Content = node.Kind == NodeKind.Root ? "This PC" : node.DisplayName,
                Style = (Style)FindResource("CrumbButton"),
                FontWeight = node == _current ? FontWeights.SemiBold : FontWeights.Normal,
                Tag = node,
            };
            button.Click += (s, _) => NavigateTo((FsNode)((Button)s).Tag);
            Breadcrumbs.Children.Add(button);
        }
        CrumbScroller.ScrollToRightEnd();
    }

    // ---- Side panel: Contents + legend ----

    private sealed record RowItem(
        FsNode Node,
        string Name,
        string SizeText,
        string Detail,
        Brush SwatchFill,
        Brush SwatchStroke,
        Thickness SwatchThickness,
        FontWeight NameWeight,
        GridLength BarFilled,
        GridLength BarEmpty,
        Brush BarBrush);

    private sealed record LegendItem(string Name, Brush Swatch, string SizeText, string PercentText);

    private RowItem MakeRow(FsNode node, long size, double fraction, string detail, long now)
    {
        bool folder = node.IsContainer;
        var swatch = Theme.Swatch(node, _colorMode, now);
        fraction = Math.Clamp(fraction, 0, 1);
        return new RowItem(
            node,
            node.DisplayName,
            Format.Bytes(size),
            detail,
            folder ? Brushes.Transparent : swatch,
            folder ? Theme.SecondaryText : Brushes.Transparent,
            new Thickness(folder ? 1.5 : 0),
            folder ? FontWeights.SemiBold : FontWeights.Normal,
            new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star),
            new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star),
            folder ? Theme.Accent : swatch);
    }

    private void RefreshSidePanel()
    {
        var node = _current;
        if (node == null)
            return;

        long now = DateTime.UtcNow.Ticks;
        long viewSize = Map.DisplaySize(node);
        ViewTitle.Text = node.Kind == NodeKind.Root ? "This PC" : node.DisplayName;
        ViewSubtitle.Text = DescribeView(node);

        var rows = new List<RowItem>();
        if (node.Children != null)
        {
            var visible = node.Children
                .Where(c => FreeSpaceCheck.IsChecked == true || c.Kind != NodeKind.FreeSpace)
                .Where(c => Map.DisplaySize(c) > 0 || c.AccessDenied || c.IsScanning)
                .OrderByDescending(Map.DisplaySize)
                .Take(MaxListRows);

            foreach (var c in visible)
            {
                long size = Map.DisplaySize(c);
                double fraction = viewSize > 0 ? (double)size / viewSize : 0;
                rows.Add(MakeRow(c, size, fraction, $"{Format.Percent(fraction)}  ·  {DescribeShort(c, now)}", now));
            }
        }
        ContentsList.ItemsSource = rows;

        LegendTitle.Text = _colorMode == ColorMode.Age ? "SPACE BY AGE (LAST MODIFIED)" : "SPACE BY TYPE";
        Legend.ItemsSource = BuildLegend(node, viewSize, now);
    }

    private string DescribeView(FsNode node)
    {
        switch (node.Kind)
        {
            case NodeKind.Root:
            {
                long total = node.Children!.Sum(d => d.Size);
                string text = $"{Format.Bytes(total - node.FreeSize)} used of {Format.Bytes(total)} across {Format.Count(node.Children!.Count, "drive", "drives")}";
                return node.FileCount > 0 ? text + $"\n{Format.Count(node.FileCount)} files  ·  {Format.Count(node.DirCount)} folders" : text;
            }
            case NodeKind.Drive:
                return $"{Format.Bytes(node.Size - node.FreeSize)} used of {Format.Bytes(node.Size)}  ·  {Format.Bytes(node.FreeSize)} free\n" +
                       $"{Format.Count(node.FileCount)} files  ·  {Format.Count(node.DirCount)} folders" + DeniedSuffix(node);
            default:
                if (node.AccessDenied)
                    return "Access denied – this folder couldn't be read.";
                return $"{Format.Bytes(node.Size)}  ·  {Format.Count(node.FileCount)} files  ·  {Format.Count(node.DirCount)} folders" + DeniedSuffix(node);
        }
    }

    private static string DeniedSuffix(FsNode node) =>
        node.DeniedCount > 0 ? $"\n{Format.Count(node.DeniedCount, "folder", "folders")} inside couldn't be read" : "";

    private string DescribeShort(FsNode node, long now)
    {
        bool age = _colorMode == ColorMode.Age;
        return node.Kind switch
        {
            NodeKind.Drive when node.IsScanning => node.ScanError != null ? "scan failed" : "scanning…",
            NodeKind.Drive or NodeKind.Directory when node.AccessDenied => "access denied",
            NodeKind.Drive or NodeKind.Directory when age => $"{Format.Count(node.FileCount)} files  ·  last change {Format.Ago(node.LastWrite, now)}",
            NodeKind.Drive or NodeKind.Directory => $"{Format.Count(node.FileCount)} files",
            NodeKind.File when age => $"modified {Format.Ago(node.LastWrite, now)}",
            NodeKind.File => FileCategories.DisplayName(node.Category),
            NodeKind.SmallFiles => $"files under {Format.Bytes(DriveScanner.SmallFileThreshold)}",
            NodeKind.FreeSpace => "unused",
            NodeKind.Unreadable => "protected / system / metadata",
            _ => "",
        };
    }

    private List<LegendItem> BuildLegend(FsNode root, long viewSize, long now)
    {
        var byCategory = new long[Enum.GetValues<FileCategory>().Length];
        var byAge = new long[AgeBuckets.Count];
        long small = 0, free = 0, unreadable = 0, unknownAge = 0;

        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            switch (n.Kind)
            {
                case NodeKind.File or NodeKind.SmallFiles when _colorMode == ColorMode.Age:
                    int bucket = AgeBuckets.Of(n.LastWrite, now);
                    if (bucket >= 0) byAge[bucket] += n.Size;
                    else unknownAge += n.Size;
                    break;
                case NodeKind.File: byCategory[(int)n.Category] += n.Size; break;
                case NodeKind.SmallFiles: small += n.Size; break;
                case NodeKind.FreeSpace: free += n.Size; break;
                case NodeKind.Unreadable: unreadable += n.Size; break;
                default:
                    if (n.Children != null && !n.IsScanning)
                        foreach (var c in n.Children)
                            stack.Push(c);
                    break;
            }
        }

        var items = new List<(string Name, Brush Swatch, long Size)>();
        if (_colorMode == ColorMode.Age)
        {
            for (int b = 0; b < AgeBuckets.Count; b++)
                items.Add((AgeBuckets.Label(b), Theme.AgeSwatch(b), byAge[b]));
            items.Add((AgeBuckets.Label(AgeBuckets.Unknown), Theme.AgeSwatch(AgeBuckets.Unknown), unknownAge));
        }
        else
        {
            foreach (var category in Enum.GetValues<FileCategory>())
                items.Add((FileCategories.DisplayName(category), Theme.Swatch(category), byCategory[(int)category]));
            items.Add(($"Small files (< {Format.Bytes(DriveScanner.SmallFileThreshold)})", Theme.SmallFilesSwatch, small));
        }
        items.Add(("Unreadable / other", Theme.UnreadableSwatch, unreadable));
        if (FreeSpaceCheck.IsChecked == true)
            items.Add(("Free space", Theme.FreeSwatch, free));

        // Age is ordinal, so keep bucket order; types read best largest first.
        var visible = items.Where(i => i.Size > 0);
        if (_colorMode == ColorMode.Type)
            visible = visible.OrderByDescending(i => i.Size);
        return visible
            .Select(i => new LegendItem(i.Name, i.Swatch, Format.Bytes(i.Size), viewSize > 0 ? Format.Percent((double)i.Size / viewSize) : ""))
            .ToList();
    }

    // ---- Side panel: Largest (shares its traversal with search statistics) ----

    private async void RefreshQuery()
    {
        var view = _current;
        if (view == null)
            return;

        _queryCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        var roots = ViewRoots(view);
        var matcher = _matcher;
        bool viewLit = SearchMatcher.IsInsideMatch(view, matcher);
        var mode = LargestModeBox.SelectedIndex == 1 ? LargestMode.Folders : LargestMode.Files;
        FileCategory? category = mode == LargestMode.Files && LargestTypeBox.SelectedIndex > 0
            ? (FileCategory)(LargestTypeBox.SelectedIndex - 1)
            : null;
        LargestTypeBox.IsEnabled = mode == LargestMode.Files;

        QueryResult result;
        try
        {
            result = await Task.Run(() => ViewQuery.Run(roots, viewLit, matcher, mode, category, MaxLargest, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || view != _current)
            return;

        ShowQueryResult(view, result, matcher, viewLit, mode, category);
    }

    private void ShowQueryResult(FsNode view, QueryResult result, SearchMatcher? matcher, bool viewLit, LargestMode mode, FileCategory? category)
    {
        string viewName = view.Kind == NodeKind.Root ? "all drives" : view.DisplayName;
        bool partial = view.Kind == NodeKind.Root && view.Children!.Any(d => d.IsScanning && d.ScanError == null);

        // Search summary in the toolbar.
        if (matcher == null)
        {
            SearchSummary.Text = "";
        }
        else if (viewLit)
        {
            SearchSummary.Text = $"All of {view.DisplayName} matches";
        }
        else if (result.MatchFiles + result.MatchFolders == 0)
        {
            SearchSummary.Text = partial ? "No matches yet…" : "No matches";
        }
        else
        {
            var parts = new List<string>();
            if (result.MatchFolders > 0) parts.Add(Format.Count(result.MatchFolders, "folder", "folders"));
            if (result.MatchFiles > 0) parts.Add(Format.Count(result.MatchFiles, "file", "files"));
            SearchSummary.Text = $"{string.Join(", ", parts)}  ·  {Format.Bytes(result.MatchBytes)}" + (partial ? " so far" : "");
        }

        // Keep the path to each hit readable on the map.
        Map.FilterTrail = matcher != null && !viewLit ? result.MatchAncestors : null;

        // Largest list.
        long now = DateTime.UtcNow.Ticks;
        long top = result.Largest.Count > 0 ? result.Largest[0].Key : 0;
        var rows = new List<RowItem>(result.Largest.Count);
        foreach (var (node, key) in result.Largest)
        {
            string where = RelativeLocation(node, view);
            string detail = node.Kind == NodeKind.File
                ? $"{(_colorMode == ColorMode.Age ? "modified " + Format.Ago(node.LastWrite, now) : FileCategories.DisplayName(node.Category))}  ·  {where}"
                : matcher == null
                    ? $"{Format.Bytes(node.Size)} including subfolders  ·  {where}"
                    : $"{Format.Count(node.FileCount)} files  ·  {where}";
            rows.Add(MakeRow(node, key, top > 0 ? (double)key / top : 0, detail, now));
        }
        LargestList.ItemsSource = rows;

        string scope = partial ? $"{viewName} (drives still scanning aren't included yet)" : viewName;
        string typeNote = category is { } c ? $" ({FileCategories.DisplayName(c)})" : "";
        LargestCaption.Text = (mode, matcher) switch
        {
            (LargestMode.Files, null) =>
                $"Largest files{typeNote} in {scope}. Files under {Format.Bytes(DriveScanner.SmallFileThreshold)} aren't listed individually.",
            (LargestMode.Files, _) =>
                $"Largest files{typeNote} matching \"{matcher.Text}\" (or inside a matching folder) in {scope}.",
            (LargestMode.Folders, null) =>
                $"Folders in {scope} ranked by the files directly inside them, so parents don't just repeat their subfolders.",
            _ => $"Folders matching \"{matcher!.Text}\" in {scope}, by total size.",
        } + " Double-click to show it on the map.";

        if (rows.Count == 0)
            LargestCaption.Text = matcher != null ? "Nothing here matches the search." : "Nothing to list here.";
    }

    private static string RelativeLocation(FsNode node, FsNode view)
    {
        string parentPath = node.Parent?.FullPath ?? "";
        if (view.Kind == NodeKind.Root)
            return parentPath;
        string viewPath = view.FullPath;
        if (parentPath.Length <= viewPath.Length)
            return "in this folder";
        return "…\\" + parentPath[viewPath.Length..].TrimStart('\\');
    }

    private void SideTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The legend describes the map; the hog cards need the room.
        if (e.OriginalSource == SideTabs)
            LegendPanel.Visibility = SideTabs.SelectedItem == HogsTab ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LargestOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            RefreshQuery();
    }

    // ---- Search ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer?.Stop();
        _searchTimer?.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            ApplySearch();
            Map.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ApplySearch();
            e.Handled = true;
        }
    }

    private void ApplySearch()
    {
        _searchTimer.Stop();
        var matcher = SearchMatcher.Parse(SearchBox.Text);
        if (matcher?.Text == _matcher?.Text)
            return;

        bool wasSearching = _matcher != null;
        _matcher = matcher;
        Map.Filter = matcher;
        Map.FilterTrail = null;
        if (matcher != null && !wasSearching)
            SideTabs.SelectedItem = LargestTab;
        RefreshQuery();
    }

    // ---- Space hogs ----

    private sealed record HogLocationRow(FsNode Node, string Path, string SizeText);

    private sealed record HogRow(
        HogResult Hog,
        string Title,
        string SizeText,
        string BadgeGlyph,
        string BadgeText,
        Brush BadgeBrush,
        string Advice,
        List<HogLocationRow> Locations,
        string MoreText,
        Visibility MoreVisibility,
        string ActionText,
        Visibility ActionVisibility,
        Visibility FindAllVisibility);

    private async void RefreshHogs()
    {
        var pc = _pc;
        if (pc == null)
            return;

        _hogsCts?.Cancel();
        var cts = _hogsCts = new CancellationTokenSource();
        var drives = ViewRoots(pc);
        List<HogResult> hogs;
        try
        {
            hogs = await Task.Run(() => SpaceHogs.Find(drives, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || pc != _pc)
            return;

        int scanning = pc.Children!.Count(d => d.IsScanning && d.ScanError == null) + _probing.Count;
        long safe = hogs.Where(h => h.Rule.Safety == HogSafety.Safe).Sum(h => h.Size);
        string summary = hogs.Count == 0
            ? "No notable space hogs found."
            : $"About {Format.Bytes(safe)} could be freed from items marked \"Safe to clear\". " +
              "This app never deletes anything – use the tools named on each card.";
        if (scanning > 0)
            summary += $"\nStill scanning {Format.Count(scanning, "drive", "drives")}; this list will update.";
        HogsSummary.Text = summary;
        HogsList.ItemsSource = hogs.Select(MakeHogRow).ToList();
        HogsTab.Header = hogs.Count > 0 ? $"Space hogs ({hogs.Count})" : "Space hogs";
    }

    private static HogRow MakeHogRow(HogResult hog)
    {
        var (glyph, badge, brush) = hog.Rule.Safety switch
        {
            HogSafety.Safe => ("", "Safe to clear", Theme.StatusGood),
            HogSafety.Caution => ("", "Review before deleting", Theme.StatusWarning),
            _ => ("", "Don't delete manually", Theme.StatusCritical),
        };
        string action = hog.Rule.Action switch
        {
            HogAction.StorageSettings => "Storage settings",
            HogAction.DiskCleanup => "Disk Cleanup",
            HogAction.RecycleBin => "Open Recycle Bin",
            _ => "",
        };

        var locations = hog.Locations
            .Take(MaxHogLocations)
            .Select(n => new HogLocationRow(n, n.FullPath, Format.Bytes(n.Size)))
            .ToList();
        int more = hog.Locations.Count - locations.Count;

        return new HogRow(
            hog,
            hog.Rule.Title,
            Format.Bytes(hog.Size),
            glyph,
            badge,
            brush,
            hog.Rule.Advice,
            locations,
            more > 0 ? $"+ {Format.Count(more, "more location", "more locations")}" : "",
            more > 0 ? Visibility.Visible : Visibility.Collapsed,
            action,
            action.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            hog.Rule.SearchTerm != null && hog.Locations.Count > 1 ? Visibility.Visible : Visibility.Collapsed);
    }

    private void HogLocation_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is FsNode node)
            ShowOnMap(node);
    }

    private void HogExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is HogRow row && row.Hog.Locations.Count > 0)
            OpenInExplorer(row.Hog.Locations[0]);
    }

    private void HogAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not HogRow row)
            return;
        try
        {
            switch (row.Hog.Rule.Action)
            {
                case HogAction.StorageSettings:
                    Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
                    break;
                case HogAction.DiskCleanup:
                    string drive = row.Hog.Locations.Count > 0 ? Path.GetPathRoot(row.Hog.Locations[0].FullPath)!.TrimEnd('\\') : "C:";
                    Process.Start(new ProcessStartInfo("cleanmgr.exe", $"/d {drive}") { UseShellExecute = true });
                    break;
                case HogAction.RecycleBin:
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open it: {ex.Message}";
        }
    }

    private void HogFindAll_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not HogRow { Hog.Rule.SearchTerm: { } term } || _pc == null)
            return;
        LargestModeBox.SelectedIndex = term.StartsWith('.') ? 0 : 1;
        NavigateTo(_pc);
        SearchBox.Text = term;
        ApplySearch();
        SideTabs.SelectedItem = LargestTab;
    }

    // ---- Treemap interaction ----

    private void Map_HoverChanged(object? sender, FsNode? node)
    {
        if (node == null || _current == null || node == _current)
        {
            Tip.Visibility = Visibility.Collapsed;
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        long viewSize = Map.DisplaySize(_current);
        long size = Map.DisplaySize(node);
        string viewName = _current.Kind == NodeKind.Root ? "all drives" : _current.DisplayName;

        TipTitle.Text = node.DisplayName;
        TipSize.Text = $"{Format.Bytes(size)}   ·   {Format.Percent(viewSize > 0 ? (double)size / viewSize : 0)} of {viewName}";
        TipDetail.Text = node.Kind switch
        {
            NodeKind.Drive when node.IsScanning => node.ScanError != null ? $"Scan failed: {node.ScanError}" : $"Scanning… {node.ScanProgress:P0}",
            NodeKind.Directory or NodeKind.Drive when node.AccessDenied => "Access denied – this folder's contents couldn't be read.",
            NodeKind.Directory or NodeKind.Drive => $"{Format.Count(node.FileCount)} files  ·  {Format.Count(node.DirCount)} folders"
                                                   + (node.DeniedCount > 0 ? $"  ·  {Format.Count(node.DeniedCount)} unreadable" : ""),
            NodeKind.File => FileCategories.DisplayName(node.Category),
            NodeKind.SmallFiles => $"{Format.Count(node.FileCount)} files under {Format.Bytes(DriveScanner.SmallFileThreshold)}, grouped together.",
            NodeKind.FreeSpace => "Unused space on this drive.",
            NodeKind.Unreadable => "Used space that couldn't be traced to readable files: protected system folders, " +
                                   "other users' profiles, NTFS metadata, restore points, and similar.",
            _ => "",
        };
        TipAge.Text = node.Kind switch
        {
            NodeKind.File => $"Modified {Format.Date(node.LastWrite)}  ·  {Format.Ago(node.LastWrite, now)}",
            NodeKind.SmallFiles when node.LastWrite > 0 => $"Newest modified {Format.Ago(node.LastWrite, now)}",
            NodeKind.Directory or NodeKind.Drive when node.LastWrite > 0 && !node.IsScanning =>
                $"Last change inside: {Format.Ago(node.LastWrite, now)} ({Format.Date(node.LastWrite)})",
            _ => "",
        };
        TipAge.Visibility = TipAge.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TipPath.Text = node.HasRealPath ? node.FullPath : node.Parent?.FullPath ?? "";

        var target = Map.ClickTarget(node);
        TipHint.Text = target == null ? "" : target.IsScanning ? "Still scanning…" : $"Click to open {target.DisplayName}";
        TipHint.Visibility = TipHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        Tip.Visibility = Visibility.Visible;
        PositionTip(Mouse.GetPosition(Map));
    }

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        if (Tip.Visibility == Visibility.Visible)
            PositionTip(e.GetPosition(Map));
    }

    private void PositionTip(Point p)
    {
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Tip.DesiredSize;
        double x = p.X + 16, y = p.Y + 20;
        if (x + size.Width > Map.ActualWidth)
            x = p.X - size.Width - 12;
        if (y + size.Height > Map.ActualHeight)
            y = p.Y - size.Height - 12;
        Canvas.SetLeft(Tip, Math.Max(0, x));
        Canvas.SetTop(Tip, Math.Max(0, y));
    }

    private void Map_NodeClicked(object? sender, FsNode node)
    {
        Map.Focus();
        if (Map.ClickTarget(node) is { } target)
            TryOpen(target);
    }

    private void Map_NodeRightClicked(object? sender, FsNode node)
    {
        var menu = new ContextMenu();
        var target = Map.ClickTarget(node);

        if (target != null && CanOpen(target))
            menu.Items.Add(MenuItem($"Zoom into “{target.DisplayName}”", "", () => NavigateTo(target)));

        if (node.HasRealPath && !node.IsScanning)
        {
            menu.Items.Add(MenuItem(node.Kind == NodeKind.File ? "Show in Explorer" : "Open in Explorer", "", () => OpenInExplorer(node)));
            menu.Items.Add(MenuItem("Copy path", "", () => CopyPath(node)));
        }
        else if (!node.HasRealPath && node.Parent is { HasRealPath: true } parent)
        {
            menu.Items.Add(MenuItem("Open containing folder", "", () => OpenInExplorer(parent)));
        }

        if (_current?.Parent != null)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Up one level", "", GoUp));
        }

        if (menu.Items.Count == 0)
            return;
        menu.PlacementTarget = Map;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        Tip.Visibility = Visibility.Collapsed;
        menu.IsOpen = true;
    }

    private MenuItem MenuItem(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenInExplorer(FsNode node)
    {
        try
        {
            string path = node.FullPath;
            string args = node.Kind == NodeKind.File ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open Explorer: {ex.Message}";
        }
    }

    private void CopyPath(FsNode node)
    {
        try
        {
            Clipboard.SetText(node.FullPath);
            StatusText.Text = $"Copied {node.FullPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't copy to clipboard: {ex.Message}";
        }
    }

    // ---- List interaction (Contents and Largest) ----

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RowItem row })
            Map.Selected = row.Node;
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        var list = ItemsControl.ItemsControlFromItemContainer((DependencyObject)sender) as ListBox;
        Map.Selected = (list?.SelectedItem as RowItem)?.Node;
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RowItem row })
            OpenRow(row);
    }

    private void OpenRow(RowItem row)
    {
        if (row.Node.IsContainer)
            TryOpen(row.Node);
        else
            ShowOnMap(row.Node);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Map.Selected = (((ListBox)sender).SelectedItem as RowItem)?.Node;
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ((ListBox)sender).SelectedItem is RowItem row)
        {
            OpenRow(row);
            e.Handled = true;
        }
    }

    // ---- Toolbar & keyboard ----

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void Rescan_Click(object sender, RoutedEventArgs e) => StartScan();

    private void FreeSpace_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return; // fires during InitializeComponent
        Map.ShowFreeSpace = FreeSpaceCheck.IsChecked == true;
        RefreshSidePanel();
    }

    private void ColorMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        _colorMode = ColorModeBox.SelectedIndex == 1 ? ColorMode.Age : ColorMode.Type;
        Map.ColorMode = _colorMode;
        RefreshSidePanel();
        RefreshQuery();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool inText = Keyboard.FocusedElement is TextBox;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (!inText && (e.Key == Key.Back || (e.Key == Key.System && e.SystemKey == Key.Left)))
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            StartScan();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !inText)
        {
            ContentsList.SelectedItem = null;
            LargestList.SelectedItem = null;
            Map.Selected = null;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.XButton1)
        {
            GoUp();
            e.Handled = true;
        }
    }
}
