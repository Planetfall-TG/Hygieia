using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Hygieia;

public sealed class NativeException : IOException
{
    public int Win32Error { get; }

    public NativeException(string path, int error)
        : base($"{path}: {new Win32Exception(error).Message}", new Win32Exception(error))
    {
        Win32Error = error;
    }
}

public sealed class DirectIoUnavailableException : Exception
{
    public DirectIoUnavailableException() : base("Unbuffered writes are not available for this file.") { }
}

public readonly record struct DiskGeometry(int Sector, int Cluster);

public readonly record struct DiskUsage(long Available, long Total, long Free);

public sealed record DiskVolume(string Root, string Label, string Kind, string FileSystem, long Total, long Free, bool Ready)
{
    public string Letter => Root.Length >= 2 ? Root[..2] : Root;

    public string MenuText()
    {
        if (!Ready)
            return $"{Letter}  {Kind}  ·  checking free space";
        var name = string.IsNullOrEmpty(Label) ? Kind : Label;
        return $"{Letter}  {name}  ·  {Text.FormatBytes(Free)} free";
    }
}

public static class Native
{
    public const int ErrorInvalidParameter = 87;
    public const int ErrorAccessDenied = 5;
    public const int ErrorHandleEof = 38;
    public const int ErrorInvalidFunction = 1;

    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareRead = 1;
    const uint FileShareWrite = 2;
    const uint FileShareDelete = 4;
    const uint CreateNew = 1;
    const uint OpenExisting = 3;
    const uint FileAttributeNormal = 0x80;
    const uint FileFlagWriteThrough = 0x80000000;
    const uint FileFlagNoBuffering = 0x20000000;
    const uint FileBegin = 0;
    const uint DriveRemovable = 2;
    const uint DriveFixed = 3;
    const uint DriveRemote = 4;
    const uint DriveRamdisk = 6;
    const uint MemCommitReserve = 0x3000;
    const uint PageReadWrite = 0x04;
    const uint MemRelease = 0x8000;

    public const string MarkerName = "wipe-marker.json";
    public const string MarkerPurpose = "hygieia-free-space-wipe";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool WriteFile(IntPtr handle, IntPtr buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadFile(IntPtr handle, IntPtr buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetFilePointerEx(IntPtr handle, long distance, out long newPointer, uint method);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FlushFileBuffers(IntPtr handle);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetFileAttributesW(string path, uint attributes);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileExW(string existing, string destination, uint flags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteFileW(string path);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool RemoveDirectoryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong total, out ulong free);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string root, StringBuilder label, int labelSize, IntPtr serial, IntPtr maxComponent, IntPtr flags, StringBuilder fileSystem, int fileSystemSize);

    [DllImport("kernel32", SetLastError = true)]
    static extern uint GetLogicalDrives();

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileNameW(string file, uint flags, ref uint length, StringBuilder name);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFileNameW(IntPtr find, ref uint length, StringBuilder name);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstStreamW(string file, int level, out FindStreamData data, int flags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextStreamW(IntPtr find, ref FindStreamData data);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FindClose(IntPtr find);

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("shell32")]
    static extern bool IsUserAnAdmin();

    [DllImport("shell32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string? args, string? dir, int show);

    public static string LongPath(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (absolute.StartsWith(@"\\?\", StringComparison.Ordinal))
            return absolute;
        if (absolute.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + absolute[2..];
        return @"\\?\" + absolute;
    }

    static bool Invalid(IntPtr handle) => handle == IntPtr.Zero || handle == new IntPtr(-1);

    static void Throw(string path)
    {
        var error = Marshal.GetLastWin32Error();
        throw new NativeException(path, error);
    }

    public static bool IsAdmin()
    {
        try { return IsUserAnAdmin(); }
        catch (Exception) { return false; }
    }

    public static bool PidExists(int pid)
    {
        if (pid <= 0)
            return false;
        var handle = OpenProcess(0x1000, false, (uint)pid);
        if (!Invalid(handle))
        {
            CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastWin32Error() != ErrorInvalidParameter;
    }

    public static DiskGeometry Geometry(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        if (!GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _) || bps == 0)
            return new DiskGeometry(4096, 4096);
        var sector = (int)bps;
        var cluster = sector * Math.Max(1, (int)spc);
        return new DiskGeometry(sector, cluster);
    }

    public static DiskUsage? Usage(string path)
    {
        if (!GetDiskFreeSpaceExW(path, out var available, out var total, out var free))
            return null;
        return new DiskUsage((long)available, (long)total, (long)free);
    }

    public static (string Label, string FileSystem) VolumeLabel(string root)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationW(root, label, label.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fs, fs.Capacity))
            return ("", "");
        return (label.ToString(), fs.ToString());
    }

    public static (List<DiskVolume> Drives, List<string> Network) ListDrives()
    {
        var mask = GetLogicalDrives();
        var ready = new List<DiskVolume>();
        var network = new List<string>();
        for (var index = 0; index < 26; index++)
        {
            if ((mask & (1u << index)) == 0)
                continue;
            var root = $"{(char)('A' + index)}:\\";
            var kindId = GetDriveTypeW(root);
            if (kindId == DriveRemote)
            {
                network.Add(root);
                ready.Add(new DiskVolume(root, "", "Network", "", 0, 0, false));
                continue;
            }
            if (kindId is not (DriveFixed or DriveRemovable or DriveRamdisk))
                continue;
            var drive = FromRoot(root, KindName(kindId), query: true);
            if (drive != null)
                ready.Add(drive);
        }
        return (ready, network);
    }

    public static DiskVolume? Probe(string root)
    {
        var kindId = GetDriveTypeW(root);
        return FromRoot(root, KindName(kindId), query: true);
    }

    static string KindName(uint kind) => kind switch
    {
        DriveRemovable => "Removable",
        DriveFixed => "Local disk",
        DriveRemote => "Network",
        DriveRamdisk => "RAM disk",
        _ => "Disk",
    };

    static DiskVolume? FromRoot(string root, string kind, bool query)
    {
        if (!query)
            return new DiskVolume(root, "", kind, "", 0, 0, false);
        var usage = Usage(root);
        if (usage == null)
            return null;
        var (label, fs) = VolumeLabel(root);
        return new DiskVolume(root, label, kind, fs, usage.Value.Total, usage.Value.Available, true);
    }

    public static long ReserveBytes(string driveRoot, bool safetyMargin, string? systemDrive = null)
    {
        const long mb = 1024 * 1024;
        if (!safetyMargin)
            return 8 * mb;
        systemDrive ??= Path.GetPathRoot(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows") ?? "C:";
        var left = driveRoot.Length >= 2 ? driveRoot[..2] : driveRoot;
        var right = systemDrive.Length >= 2 ? systemDrive[..2] : systemDrive;
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ? 512 * mb : 64 * mb;
    }

    public static void ClearAttributes(string path)
    {
        SetFileAttributesW(LongPath(path), FileAttributeNormal);
    }

    public static bool IsReparse(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Truncate(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(size);
    }

    public static void Rename(string source, string destination)
    {
        if (!MoveFileExW(LongPath(source), LongPath(destination), 0))
            Throw(source);
    }

    public static void DeleteFile(string path)
    {
        if (!DeleteFileW(LongPath(path)))
            Throw(path);
    }

    public static void DeleteDirectory(string path)
    {
        if (!RemoveDirectoryW(LongPath(path)))
            Throw(path);
    }

    public static List<string> ExtraHardLinks(string path)
    {
        var buffer = new StringBuilder(32768);
        var size = (uint)buffer.Capacity;
        var handle = FindFirstFileNameW(LongPath(path), 0, ref size, buffer);
        if (Invalid(handle))
            return new List<string>();
        var drive = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        if (drive.EndsWith('\\'))
            drive = drive[..^1];
        var names = new List<string>();
        try
        {
            while (true)
            {
                var relative = buffer.ToString();
                if (relative.Length > 0)
                    names.Add(drive + relative);
                buffer.Clear();
                size = (uint)buffer.Capacity;
                if (!FindNextFileNameW(handle, ref size, buffer))
                    break;
            }
        }
        finally
        {
            FindClose(handle);
        }
        var own = Path.GetFullPath(path);
        return names.Where(name => !string.Equals(Path.GetFullPath(name), own, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static List<(string Suffix, long Size)>? ListStreams(string path)
    {
        var handle = FindFirstStreamW(LongPath(path), 0, out var data, 0);
        if (Invalid(handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorHandleEof or ErrorInvalidFunction)
                return new List<(string, long)>();
            return null;
        }
        var found = new List<(string Suffix, long Size)>();
        try
        {
            while (true)
            {
                var suffix = StreamSuffix(data.Name);
                if (suffix != null)
                    found.Add((suffix, data.StreamSize));
                if (!FindNextStreamW(handle, ref data))
                    break;
            }
        }
        finally
        {
            FindClose(handle);
        }
        return found;
    }

    static string? StreamSuffix(string? streamName)
    {
        if (string.IsNullOrEmpty(streamName))
            return null;
        var body = streamName.StartsWith(':') ? streamName[1..] : streamName;
        const string data = ":$DATA";
        if (body.EndsWith(data, StringComparison.OrdinalIgnoreCase))
            body = body[..^data.Length];
        if (body.Length == 0)
            return null;
        return ":" + body;
    }

    public static long LinkCount(string path)
    {
        try
        {
            using var handle = Open(path, create: false, direct: false);
            return handle.LinkCount();
        }
        catch (Exception)
        {
            return 2;
        }
    }

    public sealed class FileHandle : IDisposable
    {
        IntPtr _handle;
        readonly string _path;
        bool _owns;

        internal FileHandle(IntPtr handle, string path, bool owns)
        {
            _handle = handle;
            _path = path;
            _owns = owns;
        }

        public static FileHandle Open(string path, bool create, bool direct)
        {
            var flags = FileAttributeNormal | FileFlagWriteThrough;
            if (direct)
                flags |= FileFlagNoBuffering;
            var handle = CreateFileW(
                LongPath(path),
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                create ? CreateNew : OpenExisting,
                flags,
                IntPtr.Zero);
            if (Invalid(handle))
            {
                var error = Marshal.GetLastWin32Error();
                if (direct && error == ErrorInvalidParameter)
                    throw new DirectIoUnavailableException();
                throw new NativeException(path, error);
            }
            return new FileHandle(handle, path, owns: true);
        }

        public void Seek(long offset)
        {
            if (!SetFilePointerEx(_handle, offset, out _, FileBegin))
                Throw(_path);
        }

        public void Write(IntPtr buffer, int size)
        {
            if (!WriteFile(_handle, buffer, size, out var written, IntPtr.Zero))
                Throw(_path);
            if (written != size)
                throw new IOException($"Short write on {_path}");
        }

        public int Read(IntPtr buffer, int size)
        {
            if (!ReadFile(_handle, buffer, size, out var read, IntPtr.Zero))
                Throw(_path);
            return read;
        }

        public void Flush()
        {
            if (!FlushFileBuffers(_handle))
                Throw(_path);
        }

        public long LinkCount()
        {
            if (!GetFileInformationByHandle(_handle, out var info))
                return 2;
            return info.NumberOfLinks;
        }

        public void Dispose()
        {
            if (_owns && _handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation information);

    public static FileHandle Open(string path, bool create, bool direct) => FileHandle.Open(path, create, direct);

    public sealed class AlignedBuffer : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        public int Size { get; }

        public AlignedBuffer(int size)
        {
            Pointer = VirtualAlloc(IntPtr.Zero, (nuint)size, MemCommitReserve, PageReadWrite);
            if (Pointer == IntPtr.Zero)
                Throw("VirtualAlloc");
            Size = size;
        }

        public void Fill(byte[] data)
        {
            if (data.Length > Size)
                throw new ArgumentException("data is larger than the buffer");
            Marshal.Copy(data, 0, Pointer, data.Length);
        }

        public byte[] ReadBytes(int size)
        {
            var data = new byte[size];
            Marshal.Copy(Pointer, data, 0, size);
            return data;
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                VirtualFree(Pointer, 0, MemRelease);
                Pointer = IntPtr.Zero;
            }
        }
    }

    public sealed class Volume : IDisposable
    {
        IntPtr _handle;
        readonly string _root;

        public Volume(string driveRoot)
        {
            _root = driveRoot.Length >= 2 ? driveRoot[..2] : driveRoot;
            _handle = CreateFileW(
                $@"\\.\{_root}",
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);
            if (Invalid(_handle))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorAccessDenied)
                    throw new UnauthorizedAccessException(driveRoot);
                throw new NativeException(driveRoot, error);
            }
        }

        public byte[] Read(long offset, int size)
        {
            const int align = 4096;
            var start = offset - (offset % align);
            var end = offset + size;
            end = (end + align - 1) / align * align;
            var length = (int)(end - start);
            using var buffer = new AlignedBuffer(length);
            using var handle = new FileHandle(_handle, "volume", owns: false);
            handle.Seek(start);
            var got = handle.Read(buffer.Pointer, length);
            if (got < (offset - start) + size)
                throw new IOException("Short read while reading the volume.");
            var data = buffer.ReadBytes(got);
            var inner = (int)(offset - start);
            var slice = new byte[size];
            Buffer.BlockCopy(data, inner, slice, 0, size);
            return slice;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    public static List<string> WorkspaceCandidates(string driveRoot)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(driveRoot)) ?? driveRoot;
        var folders = new List<string> { Path.Combine(root, "HygieiaWipe") };
        var temp = Path.GetTempPath();
        if (string.Equals(Path.GetPathRoot(temp), root, StringComparison.OrdinalIgnoreCase))
            folders.Add(Path.Combine(temp, "HygieiaWipe"));
        return folders;
    }

    public static string CreateWorkspace(string driveRoot)
    {
        var errors = new List<string>();
        foreach (var folder in WorkspaceCandidates(driveRoot))
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ".write-test");
                File.WriteAllBytes(probe, "ok"u8.ToArray());
                File.Delete(probe);
                WriteMarker(folder, Environment.ProcessId);
                return folder;
            }
            catch (Exception ex)
            {
                errors.Add($"{folder}: {ex.Message}");
            }
        }
        throw new UnauthorizedAccessException($"Hygieia cannot write temporary wipe files on {driveRoot}. {string.Join(" ", errors)}");
    }

    public static void WriteMarker(string folder, int pid)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["purpose"] = MarkerPurpose,
            ["pid"] = pid,
        });
        File.WriteAllText(Path.Combine(folder, MarkerName), json, Encoding.UTF8);
    }

    public static bool MarkerOwned(string folder)
    {
        var marker = Path.Combine(folder, MarkerName);
        if (!File.Exists(marker))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            return doc.RootElement.TryGetProperty("purpose", out var purpose) &&
                   purpose.GetString() == MarkerPurpose;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool OurWipeFile(string path)
    {
        var name = Path.GetFileName(path);
        return File.Exists(path) && name.StartsWith("wipe-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
    }

    public static void RemoveCreated(string folder, IEnumerable<string> files)
    {
        if (!MarkerOwned(folder))
            return;
        foreach (var path in files)
        {
            try
            {
                if (OurWipeFile(path))
                {
                    ClearAttributes(path);
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Leave a file another program is holding. Startup cleanup retries it.
            }
        }
        try
        {
            var marker = Path.Combine(folder, MarkerName);
            if (File.Exists(marker))
                File.Delete(marker);
            Directory.Delete(folder);
        }
        catch (Exception)
        {
            // The folder stays if anything else is still in it.
        }
    }

    public static List<string> CleanupStaleWorkspaces()
    {
        var removed = new List<string>();
        List<DiskVolume> drives;
        try
        {
            drives = ListDrives().Drives;
        }
        catch (Exception)
        {
            return removed;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            foreach (var folder in WorkspaceCandidates(drive.Root))
            {
                if (!seen.Add(folder) || !Directory.Exists(folder) || !MarkerOwned(folder))
                    continue;
                int pid;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, MarkerName)));
                    pid = doc.RootElement.TryGetProperty("pid", out var value) ? value.GetInt32() : 0;
                }
                catch (Exception)
                {
                    continue;
                }
                if (PidExists(pid))
                    continue;
                var files = Directory.EnumerateFiles(folder).Where(OurWipeFile).ToList();
                RemoveCreated(folder, files);
                removed.Add(folder);
            }
        }
        return removed;
    }

    public static bool RelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;
        var result = ShellExecuteW(IntPtr.Zero, "runas", exe, null, Path.GetDirectoryName(exe), 1);
        return result.ToInt64() > 32;
    }
}
