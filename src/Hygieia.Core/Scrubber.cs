namespace Hygieia;

public sealed class Plan
{
    public List<string> Roots { get; }
    public List<string> Files { get; } = new();
    public List<string> Dirs { get; } = new();
    public List<string> Links { get; } = new();
    public List<(string Path, string Reason)> Skipped { get; } = new();
    public long TotalBytes { get; set; }

    public Plan(IEnumerable<string> roots) => Roots = roots.ToList();

    public bool HasWork => Files.Count > 0 || Dirs.Count > 0 || Links.Count > 0;

    public string CountText()
    {
        var parts = new List<string>();
        if (Files.Count > 0)
            parts.Add($"{Text.Comma(Files.Count)} files");
        if (Dirs.Count > 0)
            parts.Add($"{Text.Comma(Dirs.Count)} folders");
        if (Links.Count > 0)
            parts.Add($"{Text.Comma(Links.Count)} links");
        return parts.Count == 0 ? "Nothing to scrub" : string.Join(", ", parts);
    }
}

public static class Scrubber
{
    const int Chunk = 1024 * 1024;
    const int FillChunk = 64 * 1024 * 1024;

    public static long CoverSize(long size, long unit)
    {
        if (size <= 0 || unit <= 0)
            return 0;
        return ((size + unit - 1) / unit) * unit;
    }

    public static Plan Walk(IReadOnlyList<string> roots, IReporter reporter)
    {
        var plan = new Plan(roots);
        var seen = 0;

        void Note() => reporter.Progress(0, $"Counting… {Text.Comma(plan.Files.Count)} files, {Text.FormatBytes(plan.TotalBytes)}");

        void WalkOne(string path, int depth)
        {
            if (reporter.IsCancelled)
                throw new CancelledException();
            seen++;
            if (seen % 200 == 0)
                Note();
            var kind = Classify(path);
            if (kind == "link")
            {
                plan.Links.Add(path);
                return;
            }
            if (kind == "missing")
            {
                plan.Skipped.Add((path, "The item no longer exists."));
                return;
            }
            if (kind == "other")
            {
                plan.Skipped.Add((path, "This is not a regular file or folder."));
                return;
            }
            if (kind == "file")
            {
                try
                {
                    var size = new FileInfo(path).Length;
                    plan.Files.Add(path);
                    plan.TotalBytes += Math.Max(0, size);
                }
                catch (Exception ex)
                {
                    plan.Skipped.Add((path, Text.Friendly(ex)));
                }
                return;
            }
            if (depth > 200)
            {
                plan.Skipped.Add((path, "The folder is nested too deeply."));
                return;
            }
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                }).ToList();
            }
            catch (Exception ex)
            {
                plan.Skipped.Add((path, Text.Friendly(ex)));
                return;
            }
            foreach (var child in children)
                WalkOne(child, depth + 1);
            plan.Dirs.Add(path);
        }

        foreach (var root in roots)
            WalkOne(Path.GetFullPath(root), 0);
        Note();
        return plan;
    }

    static string Classify(string path)
    {
        if (Native.IsReparse(path))
            return "link";
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                return "dir";
            return "file";
        }
        catch (Exception)
        {
            return "missing";
        }
    }

    public static void OverwriteFile(string path, Algorithm algorithm, IReporter reporter, Action<long, int>? onLogical, bool? direct = null)
    {
        Native.ClearAttributes(path);
        var logical = new FileInfo(path).Length;
        var streams = Native.ListStreams(path);
        if (streams == null)
        {
            reporter.Log($"Could not list extra streams on {path}.", error: true);
            streams = new List<(string Suffix, long Size)>();
        }
        foreach (var (suffix, size) in streams)
            OverwriteOne(path + suffix, size, algorithm, reporter, null, direct, create: false);
        OverwriteOne(path, logical, algorithm, reporter, onLogical, direct, create: false);
    }

    public static void ScrubPlan(Plan plan, Algorithm algorithm, IReporter reporter)
    {
        var started = Environment.TickCount64;
        reporter.Log($"Scrubbing with {algorithm.Name}.");
        var originalSkips = plan.Skipped.ToList();
        var totalSteps = Math.Max(plan.TotalBytes, 1) * algorithm.PassCount;
        long doneSteps = 0;
        var failed = 0;
        var scrubbed = 0;

        void OnLogical(long logicalDone, int passIndex, long fileSize, string name)
        {
            var step = doneSteps + (passIndex * fileSize) + Math.Min(logicalDone, fileSize);
            reporter.Progress(Math.Min(1, step / (double)totalSteps), $"Pass {passIndex + 1} of {algorithm.PassCount} · {name}");
        }

        foreach (var path in plan.Files)
        {
            if (reporter.IsCancelled)
                throw new CancelledException();
            var block = HardLinkBlock(path);
            if (block != null)
            {
                plan.Skipped.Add((path, block));
                reporter.Log($"Skipped {path}. {block}", error: true);
                continue;
            }
            long links;
            long fileSize;
            try
            {
                links = Native.LinkCount(path);
                fileSize = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                failed++;
                reporter.Log($"Could not scrub {path}. {Text.Friendly(ex)}", error: true);
                continue;
            }
            if (links > 1)
                reporter.Log($"{path} has {links} names. Overwriting it changes every name.");
            fileSize = Math.Max(0, fileSize);
            var name = Path.GetFileName(path);
            reporter.Log($"Scrubbing {path}");
            try
            {
                OverwriteFile(path, algorithm, reporter, (done, index) => OnLogical(done, index, fileSize, name));
            }
            catch (CancelledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                reporter.Log($"Could not scrub {path}. {Text.Friendly(ex)}", error: true);
                continue;
            }
            try
            {
                ObscureAndDeleteFile(path);
            }
            catch (Exception ex)
            {
                failed++;
                reporter.Log($"Overwrote {path} but could not remove the name. {Text.Friendly(ex)}", error: true);
                continue;
            }
            scrubbed++;
            doneSteps += fileSize * algorithm.PassCount;
        }

        foreach (var path in plan.Links)
        {
            if (reporter.IsCancelled)
                throw new CancelledException();
            try
            {
                RemoveLink(path);
                reporter.Log($"Removed link {path}. Its target was not changed.");
            }
            catch (Exception ex)
            {
                reporter.Log($"Could not remove link {path}. {Text.Friendly(ex)}", error: true);
            }
        }

        var removedDirs = 0;
        foreach (var path in plan.Dirs)
        {
            if (reporter.IsCancelled)
                throw new CancelledException();
            try
            {
                ObscureAndDeleteDir(path);
                removedDirs++;
            }
            catch (Exception ex)
            {
                reporter.Log($"Could not remove folder {path}. {Text.Friendly(ex)}", error: true);
            }
        }
        foreach (var (path, reason) in originalSkips)
            reporter.Log($"Skipped {path}. {reason}");
        var elapsed = Text.FormatDuration((Environment.TickCount64 - started) / 1000.0);
        reporter.Log($"Finished in {elapsed}. Scrubbed {Text.Comma(scrubbed)} files and removed {Text.Comma(removedDirs)} folders. {Text.Comma(failed)} failed.");
        reporter.Progress(1, "Finished");
    }

    public static void ScrubUnused(string drive, Algorithm algorithm, IReporter reporter, long reserve, string? workspace = null, long? maxBytes = null, bool keepFiles = false)
    {
        var started = Environment.TickCount64;
        var ownWorkspace = workspace == null;
        var folder = workspace ?? Native.CreateWorkspace(drive);
        if (ownWorkspace)
            reporter.Log($"Writing temporary files in {folder}. They are removed at the end.");
        else
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(Path.Combine(folder, Native.MarkerName)))
                Native.WriteMarker(folder, 0);
        }
        var created = new List<string>();
        try
        {
            var usage = Native.Usage(folder);
            if (usage == null)
                throw new IOException($"Could not read free space on {drive}.");
            var budget = maxBytes ?? usage.Value.Available - reserve;
            if (budget <= 0)
            {
                reporter.Log($"{drive} has {Text.Comma(usage.Value.Available)} bytes free. Hygieia leaves {Text.Comma(reserve)} bytes free, so there is nothing to scrub.");
                reporter.Progress(1, "Nothing to scrub");
                return;
            }
            reporter.Log($"Scrubbing unused space on {drive} with {algorithm.Name}.");
            var geo = Native.Geometry(folder);
            long written = 0;
            while (true)
            {
                if (reporter.IsCancelled)
                    throw new CancelledException();
                usage = Native.Usage(folder);
                if (usage == null)
                    break;
                var remaining = budget - written;
                var hold = maxBytes != null ? 0 : reserve;
                var size = NextFillSize(remaining, usage.Value.Available, hold, geo.Cluster);
                if (size <= 0)
                    break;
                var path = Path.Combine(folder, $"wipe-{Guid.NewGuid():N}.bin");
                try
                {
                    WriteNewFile(path, size, algorithm, reporter, written, budget);
                }
                catch (CancelledException)
                {
                    if (File.Exists(path))
                        created.Add(path);
                    throw;
                }
                catch (Exception ex)
                {
                    if (File.Exists(path))
                        created.Add(path);
                    if (Text.IsDiskFull(ex))
                    {
                        reporter.Log("The disk filled before the reserve. Stopping the fill.");
                        break;
                    }
                    throw;
                }
                created.Add(path);
                written += size;
            }
            var elapsed = Text.FormatDuration((Environment.TickCount64 - started) / 1000.0);
            reporter.Log($"Overwrote {Text.Comma(written)} bytes in {elapsed} and {(keepFiles ? "left the temporary files in place." : "removed the temporary files.")}");
            reporter.Progress(1, "Finished");
        }
        finally
        {
            if (!keepFiles)
                Native.RemoveCreated(folder, created);
        }
    }

    static long NextFillSize(long budget, long freeBytes, long reserve, int cluster)
    {
        if (budget <= 0 || cluster <= 0)
            return 0;
        var room = freeBytes - reserve;
        var target = Math.Min(FillChunk, Math.Min(budget, room));
        if (target < cluster)
            return 0;
        return target - (target % cluster);
    }

    static void WriteNewFile(string path, long size, Algorithm algorithm, IReporter reporter, long already, long budget)
    {
        var passes = algorithm.PassCount;
        OverwriteOne(path, size, algorithm, reporter, (logicalDone, passIndex) =>
        {
            var step = (already * passes) + (passIndex * size) + Math.Min(logicalDone, size);
            reporter.Progress(Math.Min(1, step / Math.Max(budget * passes, 1)), $"Pass {passIndex + 1} of {passes} · unused space");
        }, direct: null, create: true);
    }

    static string? HardLinkBlock(string path)
    {
        long links;
        try
        {
            links = Native.LinkCount(path);
        }
        catch (Exception)
        {
            return null;
        }
        if (links <= 1)
            return null;
        foreach (var link in Native.ExtraHardLinks(path))
        {
            var reason = PathRules.ProtectedReason(link);
            if (reason != null)
                return $"It is also named {link}, which is protected.";
        }
        return null;
    }

    static void OverwriteOne(string path, long logical, Algorithm algorithm, IReporter reporter, Action<long, int>? onLogical, bool? direct, bool create)
    {
        if (direct == false)
        {
            WritePasses(path, logical, algorithm, reporter, create, direct: false, onLogical);
            return;
        }
        try
        {
            WritePasses(path, logical, algorithm, reporter, create, direct: true, onLogical);
        }
        catch (DirectIoUnavailableException)
        {
            if (direct == true)
                throw;
            WritePasses(path, logical, algorithm, reporter, create && !File.Exists(path), direct: false, onLogical);
        }
    }

    static void WritePasses(string path, long logical, Algorithm algorithm, IReporter reporter, bool create, bool direct, Action<long, int>? onLogical)
    {
        if (logical < 0)
            throw new IOException("The file size is invalid.");
        var geo = Native.Geometry(path);
        var unit = geo.Cluster % geo.Sector == 0 ? geo.Cluster : geo.Sector;
        var cover = logical == 0 ? 0 : CoverSize(logical, unit);
        if (direct && cover % geo.Sector != 0)
            throw new DirectIoUnavailableException();
        if (logical == 0)
        {
            if (create)
                Native.Open(path, create: true, direct: false).Dispose();
            return;
        }

        Native.FileHandle? handle = null;
        Native.AlignedBuffer? buffer = null;
        try
        {
            try
            {
                handle = Native.Open(path, create, direct);
            }
            catch (NativeException ex) when (direct && ex.Win32Error == Native.ErrorInvalidParameter)
            {
                throw new DirectIoUnavailableException();
            }
            var chunk = Chunk;
            if (direct && geo.Cluster > chunk)
                chunk = geo.Cluster;
            buffer = new Native.AlignedBuffer(Math.Max(chunk, unit));
            var passes = algorithm.Passes;
            for (var index = 0; index < passes.Count; index++)
            {
                if (reporter.IsCancelled)
                    throw new CancelledException();
                var pass = passes[index];
                long offset = 0;
                while (offset < cover)
                {
                    if (reporter.IsCancelled)
                        throw new CancelledException();
                    var amount = (int)Math.Min(chunk, cover - offset);
                    if (direct && (amount % geo.Sector != 0 || offset % geo.Sector != 0))
                        throw new IOException("Internal alignment error while scrubbing.");
                    var data = Algorithms.Materialize(pass, offset, amount);
                    buffer.Fill(data);
                    try
                    {
                        handle.Seek(offset);
                        handle.Write(buffer.Pointer, amount);
                    }
                    catch (NativeException ex) when (direct && offset == 0 && ex.Win32Error == Native.ErrorInvalidParameter)
                    {
                        throw new DirectIoUnavailableException();
                    }
                    if (pass.Verify && !Verified(handle, buffer, data, offset, amount))
                        throw new IOException("The drive did not return the data just written.");
                    offset += amount;
                    onLogical?.Invoke(Math.Min(offset, logical), index);
                }
                handle.Flush();
            }
        }
        finally
        {
            handle?.Dispose();
            buffer?.Dispose();
        }
        if (cover != logical)
            Native.Truncate(path, logical);
    }

    static bool Verified(Native.FileHandle handle, Native.AlignedBuffer buffer, byte[] data, long offset, int amount)
    {
        handle.Flush();
        handle.Seek(offset);
        var got = handle.Read(buffer.Pointer, amount);
        if (got == amount && buffer.ReadBytes(amount).AsSpan().SequenceEqual(data))
            return true;
        buffer.Fill(data);
        handle.Seek(offset);
        handle.Write(buffer.Pointer, amount);
        handle.Flush();
        handle.Seek(offset);
        got = handle.Read(buffer.Pointer, amount);
        return got == amount && buffer.ReadBytes(amount).AsSpan().SequenceEqual(data);
    }

    static void ObscureAndDeleteFile(string path)
    {
        Native.ClearAttributes(path);
        var current = path;
        var directory = Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? "";
        for (var i = 0; i < 2; i++)
        {
            var target = Path.Combine(directory, Guid.NewGuid().ToString("N")[..16]);
            Native.Rename(current, target);
            current = target;
        }
        // Truncating a hard link sets every name's length to zero.
        var links = Native.LinkCount(current);
        if (links <= 1)
            Native.Truncate(current, 0);
        Native.DeleteFile(current);
    }

    static void RemoveLink(string path)
    {
        Native.ClearAttributes(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
            Native.DeleteDirectory(path);
        else
            Native.DeleteFile(path);
    }

    static void ObscureAndDeleteDir(string path)
    {
        Native.ClearAttributes(path);
        var current = path;
        var parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        for (var i = 0; i < 2; i++)
        {
            var target = Path.Combine(parent, Guid.NewGuid().ToString("N")[..16]);
            Native.Rename(current, target);
            current = target;
        }
        Native.DeleteDirectory(current);
    }
}
