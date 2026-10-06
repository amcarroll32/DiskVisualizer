namespace DiskVisualizer.Model;

public enum LargestMode
{
    Files,
    Folders,
}

public sealed record RankedNode(FsNode Node, long Key);

/// <param name="MatchAncestors">Folders that contain a search hit (so the map can keep their titles readable).</param>
public sealed record QueryResult(
    long MatchFiles,
    long MatchFolders,
    long MatchBytes,
    IReadOnlyList<RankedNode> Largest,
    IReadOnlySet<FsNode>? MatchAncestors);

/// <summary>
/// One pass over the current view that produces both the search statistics and the
/// "Largest" list. Runs on a background thread over finished (immutable) subtrees.
/// </summary>
public static class ViewQuery
{
    public static QueryResult Run(
        IReadOnlyList<FsNode> roots,
        bool rootsLit,
        SearchMatcher? matcher,
        LargestMode mode,
        FileCategory? category,
        int top,
        CancellationToken ct)
    {
        var heap = new PriorityQueue<FsNode, long>();
        var stack = new Stack<(FsNode Node, bool Lit)>();
        foreach (var r in roots)
            stack.Push((r, rootsLit));

        long matchFiles = 0, matchFolders = 0, matchBytes = 0;
        var ancestors = matcher != null ? new HashSet<FsNode>() : null;
        int visited = 0;

        void Offer(FsNode node, long key)
        {
            if (key <= 0)
                return;
            if (heap.Count < top)
                heap.Enqueue(node, key);
            else if (heap.TryPeek(out _, out long smallest) && key > smallest)
                heap.DequeueEnqueue(node, key);
        }

        while (stack.Count > 0)
        {
            if ((++visited & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();

            var (node, lit) = stack.Pop();
            bool self = matcher != null && matcher.Matches(node);
            bool nodeLit = lit || self;

            // Count only outermost hits so a matching folder isn't added twice with its contents.
            if (self && !lit)
            {
                matchBytes += node.Size;
                if (node.Kind == NodeKind.File) matchFiles++;
                else matchFolders++;
                for (var a = node.Parent; a != null && ancestors!.Add(a); a = a.Parent) { }
            }

            switch (node.Kind)
            {
                case NodeKind.File:
                    if (mode == LargestMode.Files && (matcher == null || nodeLit) && (category == null || node.Category == category))
                        Offer(node, node.Size);
                    break;

                case NodeKind.Directory:
                    if (mode == LargestMode.Folders)
                    {
                        if (matcher == null)
                        {
                            // Ranked by files directly inside, so parents don't just repeat their children.
                            long direct = 0;
                            foreach (var c in node.Children ?? [])
                                if (c.Kind is NodeKind.File or NodeKind.SmallFiles)
                                    direct += c.Size;
                            Offer(node, direct);
                        }
                        else if (self && !lit)
                        {
                            Offer(node, node.Size);
                        }

                        // Inside a hit there's nothing further to rank in folder mode.
                        if (matcher != null && nodeLit)
                            break;
                    }
                    PushChildren(stack, node, nodeLit);
                    break;

                case NodeKind.Drive:
                case NodeKind.Root:
                    if (!node.IsScanning)
                        PushChildren(stack, node, nodeLit);
                    break;
            }
        }

        var ranked = new List<RankedNode>(heap.Count);
        foreach (var (node, key) in heap.UnorderedItems)
            ranked.Add(new RankedNode(node, key));
        ranked.Sort((a, b) => b.Key.CompareTo(a.Key));
        return new QueryResult(matchFiles, matchFolders, matchBytes, ranked, ancestors);
    }

    private static void PushChildren(Stack<(FsNode, bool)> stack, FsNode node, bool lit)
    {
        if (node.Children == null)
            return;
        foreach (var c in node.Children)
            stack.Push((c, lit));
    }
}
