using System.IO;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using DiskVisualizer.Model;

namespace DiskVisualizer.Scanning;

/// <summary>
/// Scans one drive with a pool of worker threads. Each worker lists one directory
/// at a time and queues its subdirectories, so the tree is built breadth-first
/// without locking (a directory's child list is only written by the worker that
/// listed it). Sizes are rolled up in a single pass once every worker is done.
/// </summary>
public sealed class DriveScanner
{
    /// <summary>Files smaller than this are grouped into one node per folder to keep memory down.</summary>
    public const long SmallFileThreshold = 64 * 1024;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,   // we want the exception so we can flag the folder
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,         // include hidden and system files
        BufferSize = 64 * 1024,
    };

    private readonly ConcurrentQueue<(FsNode Node, string Path)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _pending;
    private volatile bool _done;

    private long _bytes;
    private long _files;
    private long _dirs;
    private long _denied;

    public DriveScanner(FsNode driveNode, long totalSize, long freeSize)
    {
        Drive = driveNode;
        TotalSize = totalSize;
        FreeSize = freeSize;
    }

    public FsNode Drive { get; }
    public long TotalSize { get; }
    public long FreeSize { get; }
    public long UsedSize => TotalSize - FreeSize;

    public long ScannedBytes => Interlocked.Read(ref _bytes);
    public long ScannedFiles => Interlocked.Read(ref _files);
    public long ScannedDirs => Interlocked.Read(ref _dirs);
    public long DeniedDirs => Interlocked.Read(ref _denied);
    public volatile string? CurrentPath;

    public Task ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    private void Scan(CancellationToken ct)
    {
        int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 12);
        Enqueue(Drive, Drive.Name);

        var workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = new Thread(() => Worker(workerCount, ct))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"Scan {Drive.Name} #{i}",
            };
            workers[i].Start();
        }
        foreach (var w in workers)
            w.Join();

        ct.ThrowIfCancellationRequested();
        _totals = RollUp(Drive);
    }

    private void Enqueue(FsNode node, string path)
    {
        Interlocked.Increment(ref _pending);
        _queue.Enqueue((node, path));
        _signal.Release();
    }

    private void Worker(int workerCount, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                _signal.Wait(ct);
                if (_done)
                    return;
                if (!_queue.TryDequeue(out var item))
                    continue;

                ListDirectory(item.Node, item.Path);

                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _done = true;
                    _signal.Release(workerCount);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct Entry(string? Name, long Length, FileAttributes Attributes, bool IsDirectory, FileCategory Category, long Modified);

    private static Entry Transform(ref FileSystemEntry e)
    {
        var attributes = e.Attributes;
        if (e.IsDirectory)
            return new Entry(e.FileName.ToString(), 0, attributes, true, default, 0);

        // Cloud placeholders (OneDrive etc.) report their full size but use no local space.
        long length = (attributes & CloudOnlyMask) != 0 ? 0 : e.Length;
        long modified = e.LastWriteTimeUtc.UtcTicks;
        if (length < SmallFileThreshold)
            return new Entry(null, length, attributes, false, default, modified);

        return new Entry(e.FileName.ToString(), length, attributes, false, FileCategories.FromFileName(e.FileName), modified);
    }

    private void ListDirectory(FsNode dir, string path)
    {
        CurrentPath = path;
        var children = new List<FsNode>();
        var subdirs = new List<FsNode>();
        long bytes = 0, files = 0, smallBytes = 0, smallCount = 0, smallNewest = 0;

        try
        {
            var entries = new FileSystemEnumerable<Entry>(path, Transform, Options);
            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    // Junctions and symlinks point at space that is counted elsewhere (or on another drive).
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var sub = new FsNode(e.Name!, NodeKind.Directory, dir);
                    children.Add(sub);
                    subdirs.Add(sub);
                    continue;
                }

                files++;
                bytes += e.Length;
                if (e.Name != null)
                {
                    children.Add(new FsNode(e.Name, NodeKind.File, dir)
                    {
                        Size = e.Length,
                        FileCount = 1,
                        Category = e.Category,
                        LastWrite = e.Modified,
                    });
                }
                else
                {
                    smallBytes += e.Length;
                    smallCount++;
                    smallNewest = Math.Max(smallNewest, e.Modified);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            dir.AccessDenied = true;
            Interlocked.Increment(ref _denied);
        }

        if (smallCount > 0)
        {
            string name = smallCount == 1 ? "1 small file" : $"{Format.Count(smallCount)} small files";
            children.Add(new FsNode(name, NodeKind.SmallFiles, dir)
            {
                Size = smallBytes,
                FileCount = smallCount,
                LastWrite = smallNewest,
            });
        }

        dir.Children = children;

        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _dirs, subdirs.Count);

        foreach (var sub in subdirs)
            Enqueue(sub, Path.Join(path, sub.Name));
    }

    /// <summary>Iterative post-order pass: sums sizes and counts, sorts children largest first.</summary>
    private static Totals RollUp(FsNode root)
    {
        var stack = new Stack<(FsNode Node, bool ChildrenDone)>();
        stack.Push((root, false));
        Comparison<FsNode> bySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();
            var children = node.Children;
            if (children == null)
                continue;

            if (!childrenDone)
            {
                stack.Push((node, true));
                foreach (var c in children)
                    if (c.Kind == NodeKind.Directory)
                        stack.Push((c, false));
                continue;
            }

            long size = 0, files = 0, dirs = 0, denied = node.AccessDenied ? 1 : 0, newest = 0;
            foreach (var c in children)
            {
                size += c.Size;
                files += c.FileCount;
                newest = Math.Max(newest, c.LastWrite);
                if (c.Kind == NodeKind.Directory)
                {
                    dirs += 1 + c.DirCount;
                    denied += c.DeniedCount;
                }
            }
            children.Sort(bySizeDesc);
            children.TrimExcess();
            if (node == root)
                return new Totals(size, files, dirs, denied, newest);

            node.Size = size;
            node.LastWrite = newest;
            node.FileCount = files;
            node.DirCount = dirs;
            node.DeniedCount = denied;
        }
        return default;
    }

    private readonly record struct Totals(long Size, long Files, long Dirs, long Denied, long Newest);
    private Totals _totals;

    /// <summary>
    /// Called on the UI thread after <see cref="ScanAsync"/> completes: adds the free-space
    /// and unreadable blocks so the drive's area matches its real capacity.
    /// </summary>
    public void Finish()
    {
        var children = Drive.Children ??= [];
        long scanned = _totals.Size;
        Drive.FileCount = _totals.Files;
        Drive.DirCount = _totals.Dirs;
        Drive.DeniedCount = _totals.Denied;
        Drive.LastWrite = _totals.Newest;
        long unreadable = Math.Max(0, UsedSize - scanned);

        if (unreadable > 0)
            children.Add(new FsNode("Unreadable / other", NodeKind.Unreadable, Drive) { Size = unreadable });
        if (FreeSize > 0)
            children.Add(new FsNode("Free space", NodeKind.FreeSpace, Drive) { Size = FreeSize });

        children.Sort((a, b) => b.Size.CompareTo(a.Size));
        Drive.Size = scanned + unreadable + FreeSize;
        Drive.FreeSize = FreeSize;
        Drive.IsScanning = false;
        Drive.ScanProgress = 1;
    }
}
