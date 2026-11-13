namespace Hygieia;

public static class PathRules
{
    static readonly HashSet<string> LockedRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "dumpstack.log.tmp", "bootmgr",
    };

    public static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();

    public static bool IsDriveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWithin(string child, string parent)
    {
        var childNorm = Normalize(child);
        var parentNorm = Normalize(parent);
        if (childNorm == parentNorm)
            return true;
        var prefix = parentNorm.EndsWith(Path.DirectorySeparatorChar) ? parentNorm : parentNorm + Path.DirectorySeparatorChar;
        return childNorm.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static List<string> PruneNested(IEnumerable<string> paths)
    {
        var ordered = paths
            .Select(Path.GetFullPath)
            .OrderBy(path => Normalize(path).Length)
            .ToList();
        var kept = new List<string>();
        foreach (var path in ordered)
        {
            if (kept.Any(parent => IsWithin(path, parent)))
                continue;
            kept.Add(path);
        }
        return kept;
    }

    public static string? ProtectedReason(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (IsDriveRoot(absolute))
            return "Refusing to scrub an entire drive. Choose files and folders, or scrub unused space on that drive.";

        var parent = Path.GetDirectoryName(absolute);
        var name = Path.GetFileName(absolute);
        if (parent != null && LockedRootNames.Contains(name) && IsDriveRoot(parent))
            return $"Refusing to scrub {name}. Windows needs that file.";

        foreach (var (root, label) in WindowsRoots())
        {
            if (IsWithin(absolute, root))
                return $"Refusing to scrub {label}.";
        }

        var drive = Path.GetPathRoot(absolute);
        if (!string.IsNullOrEmpty(drive))
        {
            if (IsWithin(absolute, Path.Combine(drive, "System Volume Information")))
                return "Refusing to scrub System Volume Information.";
            if (IsWithin(absolute, Path.Combine(drive, "Recovery")))
                return "Refusing to scrub the Recovery folder.";
        }

        foreach (var appPath in AppPaths())
        {
            if (IsWithin(absolute, appPath) || IsWithin(appPath, absolute))
                return "The selection includes Hygieia.";
        }
        return null;
    }

    static IEnumerable<(string Root, string Label)> WindowsRoots()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrEmpty(windir))
            yield return (windir, "Windows");
        foreach (var (env, label) in new[]
        {
            ("ProgramFiles", "Program Files"),
            ("ProgramFiles(x86)", "Program Files"),
            ("ProgramData", "ProgramData"),
        })
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(value))
                yield return (value, label);
        }
    }

    public static IEnumerable<string> AppPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Keep(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                    list.Add(full);
            }
            catch (Exception)
            {
                // Ignore a path the process cannot resolve.
            }
        }

        Keep(AppContext.BaseDirectory);
        Keep(Environment.ProcessPath);
        return list;
    }
}
