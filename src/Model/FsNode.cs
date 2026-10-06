using System.IO;

namespace DiskVisualizer.Model;

public enum NodeKind
{
    Root,        // "This PC" – parent of all drives
    Drive,
    Directory,
    File,
    SmallFiles,  // files under the per-file threshold, grouped per folder
    FreeSpace,
    Unreadable,  // used space the scan could not attribute to any readable file
}

/// <summary>
/// One box in the treemap. Directory trees are built by the scanner; sizes are
/// filled in bottom-up once a drive finishes.
/// </summary>
public sealed class FsNode
{
    public FsNode(string name, NodeKind kind, FsNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public NodeKind Kind { get; }
    public FsNode? Parent { get; }

    public long Size { get; set; }

    /// <summary>Free space contained in this node (drives and the root only).</summary>
    public long FreeSize { get; set; }

    /// <summary>
    /// Last-modified time (UTC ticks). For files it's the file's own; for folders and grouped
    /// small files it's the newest file inside. 0 = unknown.
    /// </summary>
    public long LastWrite { get; set; }

    public long FileCount { get; set; }
    public long DirCount { get; set; }
    public List<FsNode>? Children { get; set; }
    public FileCategory Category { get; set; }

    /// <summary>The directory itself could not be listed.</summary>
    public bool AccessDenied { get; set; }

    /// <summary>Number of unreadable folders anywhere below this node.</summary>
    public long DeniedCount { get; set; }

    public string? Label { get; set; }

    /// <summary>Drive capacity as reported by Windows (drives only).</summary>
    public long Capacity { get; set; }

    public bool IsRemovable { get; set; }

    /// <summary>Removable volume with a DCIM folder at its root, i.e. almost certainly a camera card.</summary>
    public bool HasDcimFolder { get; set; }

    /// <summary>Physical disk, device kind and health (drives only; null until the disk query finishes).</summary>
    public DriveHardware? Hardware { get; set; }

    // Drive scan state (UI thread only).
    public bool IsScanning { get; set; }
    public double ScanProgress { get; set; }
    public string? ScanError { get; set; }

    public bool IsContainer => Kind is NodeKind.Root or NodeKind.Drive or NodeKind.Directory;

    public string DisplayName => Kind switch
    {
        NodeKind.Drive when !string.IsNullOrEmpty(Label) => $"{Name.TrimEnd('\\')} {Label}",
        NodeKind.Drive => Name.TrimEnd('\\'),
        _ => Name,
    };

    /// <summary>Real filesystem path, or the nearest real ancestor for synthetic nodes.</summary>
    public string FullPath => Kind switch
    {
        NodeKind.Root => Name,
        NodeKind.Drive => Name,
        NodeKind.Directory or NodeKind.File => Path.Join(Parent!.FullPath, Name),
        _ => Parent?.FullPath ?? Name,
    };

    public bool HasRealPath => Kind is NodeKind.Drive or NodeKind.Directory or NodeKind.File;

    public IEnumerable<FsNode> Ancestors()
    {
        for (var n = Parent; n != null; n = n.Parent)
            yield return n;
    }

    public bool IsAncestorOf(FsNode node) => node.Ancestors().Contains(this);
}
