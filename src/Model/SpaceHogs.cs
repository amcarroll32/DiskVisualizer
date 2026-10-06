using System.IO.Enumeration;

namespace DiskVisualizer.Model;

public enum HogSafety
{
    Safe,        // can be cleared; it's rebuilt or re-downloaded as needed
    Caution,     // your data or a trade-off – review before removing
    DoNotDelete, // never delete by hand; use the named tool instead
}

public enum HogAction
{
    None,
    StorageSettings,
    DiskCleanup,
    RecycleBin,
}

/// <summary>
/// A well-known space consumer. <see cref="Paths"/> are drive-relative, '\'-separated, and may
/// use * and ? per segment. <see cref="Names"/> match a file or folder name anywhere (outermost
/// match only), optionally only when its parent folder is <see cref="NamesParent"/>.
/// </summary>
public sealed record HogRule(
    string Title,
    HogSafety Safety,
    string Advice,
    HogAction Action = HogAction.None,
    string[]? Paths = null,
    string[]? Names = null,
    string? NamesParent = null,
    string? SearchTerm = null);

public sealed record HogResult(HogRule Rule, IReadOnlyList<FsNode> Locations, long Size);

public static class SpaceHogs
{
    /// <summary>Hogs smaller than this aren't worth a callout.</summary>
    public const long MinSize = 10L * 1024 * 1024;

    /// <summary>Individual locations smaller than this are left off a card.</summary>
    public const long MinLocationSize = 1024 * 1024;

    private const string U = @"Users\*\";

    public static readonly IReadOnlyList<HogRule> Rules =
    [
        new("Previous Windows installation", HogSafety.Safe,
            "Left behind by a Windows upgrade. Remove it with Storage settings → Temporary files → " +
            "\"Previous Windows installation(s)\" – it can't be deleted by hand.",
            HogAction.StorageSettings, Paths: ["Windows.old", "$WINDOWS.~BT", "$WINDOWS.~WS"]),

        new("Recycle Bin", HogSafety.Safe,
            "Deleted files still taking up space. Empty the Recycle Bin once you're sure you don't need them.",
            HogAction.RecycleBin, Paths: ["$Recycle.Bin"]),

        new("Windows Update downloads", HogSafety.Safe,
            "Already-installed update packages. Clear them with Storage settings → Temporary files → \"Windows Update Cleanup\".",
            HogAction.StorageSettings,
            Paths: [@"Windows\SoftwareDistribution\Download", @"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"]),

        new("Temporary files", HogSafety.Safe,
            "Scratch files apps forgot to clean up. Use Storage settings → Temporary files, or close your apps and delete the folder's contents.",
            HogAction.StorageSettings, Paths: [@"Windows\Temp", U + @"AppData\Local\Temp"]),

        new("Crash dumps & error reports", HogSafety.Safe,
            "Only useful for diagnosing past crashes. Clear with Storage settings → Temporary files → \"System error memory dump files\".",
            HogAction.StorageSettings,
            Paths: [@"Windows\MEMORY.DMP", @"Windows\Minidump", @"Windows\LiveKernelReports", U + @"AppData\Local\CrashDumps",
                    @"ProgramData\Microsoft\Windows\WER", U + @"AppData\Local\Microsoft\Windows\WER"]),

        new("GPU shader caches", HogSafety.Safe,
            "Compiled shaders from your graphics driver. Rebuilt automatically (games may stutter briefly the first time). " +
            "Clear via Disk Cleanup → \"DirectX Shader Cache\", or delete the contents while games are closed.",
            HogAction.DiskCleanup,
            Paths: [U + @"AppData\Local\NVIDIA\DXCache", U + @"AppData\Local\NVIDIA\GLCache", U + @"AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache",
                    U + @"AppData\Local\NVIDIA Corporation\NV_Cache", @"ProgramData\NVIDIA Corporation\NV_Cache", U + @"AppData\Local\D3DSCache",
                    U + @"AppData\Local\AMD\DxCache", U + @"AppData\Local\AMD\DxcCache", U + @"AppData\Local\AMD\GLCache", U + @"AppData\Local\AMD\VkCache",
                    U + @"AppData\LocalLow\Intel\ShaderCache", U + @"AppData\Local\Intel\ShaderCache"]),

        new("Steam shader pre-caches", HogSafety.Safe,
            "Per-game shader caches downloaded by Steam. Safe to delete; Steam rebuilds them for games you play.",
            Names: ["shadercache"], NamesParent: "steamapps"),

        new("Steam partial downloads", HogSafety.Safe,
            "Leftovers from interrupted game downloads and updates. Safe to delete while Steam isn't downloading.",
            Names: ["downloading", "temp"], NamesParent: "steamapps"),

        new("Browser caches", HogSafety.Safe,
            "Cached web pages and images. Clear from the browser: Settings → Privacy → Clear browsing data → \"Cached images and files\".",
            Paths: [U + @"AppData\Local\Google\Chrome\User Data\*\Cache", U + @"AppData\Local\Google\Chrome\User Data\*\Code Cache",
                    U + @"AppData\Local\Microsoft\Edge\User Data\*\Cache", U + @"AppData\Local\Microsoft\Edge\User Data\*\Code Cache",
                    U + @"AppData\Local\BraveSoftware\Brave-Browser\User Data\*\Cache", U + @"AppData\Local\Mozilla\Firefox\Profiles\*\cache2",
                    U + @"AppData\Local\Microsoft\Windows\INetCache"]),

        new("Python package caches (pip, uv)", HogSafety.Safe,
            "Downloaded wheels kept for reinstalls. Run `pip cache purge` and/or `uv cache clean`.",
            Paths: [U + @"AppData\Local\pip\Cache", U + @"AppData\Local\uv\cache", U + @".cache\pip"]),

        new("JavaScript package caches (npm, Yarn, pnpm)", HogSafety.Safe,
            "Package download caches. Run `npm cache clean --force`, `yarn cache clean`, or `pnpm store prune`.",
            Paths: [U + @"AppData\Local\npm-cache", U + @"AppData\Roaming\npm-cache", U + @"AppData\Local\Yarn\Cache", U + @"AppData\Local\pnpm\store", U + @"AppData\Local\pnpm-cache"]),

        new("NuGet & Gradle caches", HogSafety.Safe,
            "Build dependency caches; packages are re-downloaded on the next build. " +
            "Run `dotnet nuget locals all --clear`, or delete .gradle\\caches.",
            Paths: [U + @".nuget\packages", U + @".gradle\caches"]),

        new("NVIDIA driver installers", HogSafety.Safe,
            "Old driver packages kept after installation. Safe to delete.",
            Paths: [@"ProgramData\NVIDIA Corporation\Downloader", @"Program Files\NVIDIA Corporation\Installer2"]),

        new("Gameplay recordings", HogSafety.Caution,
            "Clips saved by NVIDIA Instant Replay / ShadowPlay and Xbox Game Bar. Keep the ones you want, delete the rest.",
            Paths: [U + @"Videos\NVIDIA", U + @"Videos\Captures"]),

        new("AI model downloads (Hugging Face)", HogSafety.Caution,
            "Downloaded models; deleting them means re-downloading later. Use `hf cache delete` to pick which to remove.",
            Paths: [U + @".cache\huggingface"]),

        new("node_modules folders", HogSafety.Caution,
            "Project dependencies, restorable with `npm install`. Delete them for projects you're not working on.",
            Names: ["node_modules"], SearchTerm: "node_modules"),

        new("WSL & Docker virtual disks", HogSafety.Caution,
            "Linux virtual disks grow but never shrink on their own. Clean up inside (e.g. `docker system prune`), " +
            "then compact the disk with `wsl --manage <distro> --set-sparse true` or Optimize-VHD.",
            Names: ["ext4.vhdx", "docker_data.vhdx"], SearchTerm: ".vhdx"),

        new("Downloads folder", HogSafety.Caution,
            "Installers and files you downloaded. Review and delete what you no longer need.",
            Paths: [U + "Downloads"]),

        new("iPhone / iPad backups", HogSafety.Caution,
            "Local device backups made by iTunes or Apple Devices. Delete old backups from the app's backup settings.",
            Paths: [U + @"AppData\Roaming\Apple Computer\MobileSync\Backup", U + @"Apple\MobileSync\Backup"]),

        new("Hibernation file", HogSafety.Caution,
            "Used by Hibernate and Fast Startup. If you don't use them, run `powercfg /h off` in an admin terminal to remove it.",
            Paths: ["hiberfil.sys"]),

        new("Page file", HogSafety.DoNotDelete,
            "Virtual memory managed by Windows. Don't delete it; its size can be changed under " +
            "Advanced system settings → Performance → Virtual memory.",
            Paths: ["pagefile.sys", "swapfile.sys"]),

        new("Windows component store (WinSxS)", HogSafety.DoNotDelete,
            "Never delete files here by hand. Run `Dism /Online /Cleanup-Image /StartComponentCleanup` as admin, or Disk Cleanup → " +
            "\"Windows Update Cleanup\". The size shown is overstated because many files are hard links.",
            HogAction.DiskCleanup, Paths: [@"Windows\WinSxS"]),

        new("Windows Installer cache", HogSafety.DoNotDelete,
            "Needed to repair, update and uninstall programs. Don't delete anything here by hand.",
            Paths: [@"Windows\Installer"]),

        new("Installer package cache", HogSafety.DoNotDelete,
            "Setup files for Visual Studio, .NET and other redistributables, needed to repair or uninstall them. Don't delete by hand.",
            Paths: [@"ProgramData\Package Cache"]),
    ];

    public static List<HogResult> Find(IReadOnlyList<FsNode> drives, CancellationToken ct)
    {
        var found = Rules.ToDictionary(r => r, _ => new List<FsNode>());

        foreach (var drive in drives)
        {
            foreach (var rule in Rules)
                foreach (var path in rule.Paths ?? [])
                    MatchPath(drive, path.Split('\\'), 0, found[rule]);
            ct.ThrowIfCancellationRequested();
        }

        FindByName(drives, found, ct);

        return found
            .Select(kv => new HogResult(
                kv.Key,
                kv.Value.Where(n => n.Size >= MinLocationSize).OrderByDescending(n => n.Size).ToList(),
                kv.Value.Sum(n => n.Size)))
            .Where(h => h.Size >= MinSize)
            .OrderByDescending(h => h.Size)
            .ToList();
    }

    private static void MatchPath(FsNode node, string[] segments, int index, List<FsNode> found)
    {
        if (index == segments.Length)
        {
            found.Add(node);
            return;
        }
        if (node.Children == null)
            return;

        string segment = segments[index];
        bool wildcard = segment.IndexOfAny(['*', '?']) >= 0;
        foreach (var child in node.Children)
        {
            if (child.Kind is not (NodeKind.Directory or NodeKind.File))
                continue;
            bool match = wildcard
                ? FileSystemName.MatchesSimpleExpression(segment, child.Name, ignoreCase: true)
                : child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase);
            if (match)
                MatchPath(child, segments, index + 1, found);
        }
    }

    /// <summary>Full-tree pass for name-anywhere rules; a bit per rule stops nested repeats.</summary>
    private static void FindByName(IReadOnlyList<FsNode> drives, Dictionary<HogRule, List<FsNode>> found, CancellationToken ct)
    {
        var rules = Rules.Where(r => r.Names != null).ToArray();
        var names = rules.Select(r => new HashSet<string>(r.Names!, StringComparer.OrdinalIgnoreCase)).ToArray();

        var stack = new Stack<(FsNode Node, int Inside)>();
        foreach (var d in drives)
            stack.Push((d, 0));

        int visited = 0;
        while (stack.Count > 0)
        {
            if ((++visited & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();

            var (node, inside) = stack.Pop();
            if (node.Kind is NodeKind.Directory or NodeKind.File)
            {
                for (int i = 0; i < rules.Length; i++)
                {
                    int bit = 1 << i;
                    if ((inside & bit) != 0 || !names[i].Contains(node.Name))
                        continue;
                    if (rules[i].NamesParent is { } parent && !string.Equals(node.Parent?.Name, parent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    found[rules[i]].Add(node);
                    inside |= bit;
                }
            }

            if (node.Children != null && node.Kind != NodeKind.File)
                foreach (var c in node.Children)
                    if (c.Kind is NodeKind.Directory or NodeKind.File)
                        stack.Push((c, inside));
        }
    }
}
