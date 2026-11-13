using System.Runtime.InteropServices;

namespace Hygieia.Tests;

public class ScrubTests
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr security);

    [Fact]
    public void Cover_size_rounds_up_and_leaves_empty_files_alone()
    {
        Assert.Equal(0, Scrubber.CoverSize(0, 4096));
        Assert.Equal(4096, Scrubber.CoverSize(10, 4096));
        Assert.Equal(4096, Scrubber.CoverSize(4096, 4096));
    }

    [Fact]
    public void Overwrite_and_delete_removes_the_bytes()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-scrub-");
        try
        {
            var path = Path.Combine(folder.FullName, "secret.txt");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)'A', 5000).ToArray());
            var plan = Scrubber.Walk(new[] { path }, new ListReporter());
            Scrubber.ScrubPlan(plan, Algorithms.ByKey["nist-zeros"], new ListReporter());
            Assert.False(File.Exists(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Buffered_and_direct_leave_the_pattern(bool direct)
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-direct-");
        try
        {
            var path = Path.Combine(folder.FullName, $"direct-{direct}.bin");
            File.WriteAllBytes(path, RandomNumber(100_000));
            Scrubber.OverwriteFile(path, Algorithms.ByKey["nist-zeros"], new ListReporter(), null, direct);
            Assert.Equal(new byte[100_000], File.ReadAllBytes(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Verified_small_file_keeps_its_length()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-tiny-");
        try
        {
            var path = Path.Combine(folder.FullName, "tiny.txt");
            File.WriteAllBytes(path, "0123456789"u8.ToArray());
            Scrubber.OverwriteFile(path, Algorithms.ByKey["hmg-baseline"], new ListReporter(), null, direct: false);
            Assert.Equal(new byte[10], File.ReadAllBytes(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Folder_scrub_removes_nested_files_and_not_symlink_targets()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-folder-");
        try
        {
            var outside = Path.Combine(folder.FullName, "outside.txt");
            File.WriteAllBytes(outside, "keep-me"u8.ToArray());
            var target = Path.Combine(folder.FullName, "box");
            var nested = Path.Combine(target, "inner");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "secret.txt"), "hidden"u8.ToArray());
            var link = Path.Combine(target, "link.txt");
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception)
            {
                // This machine does not allow symlinks. Nested removal is still checked.
            }
            Scrubber.ScrubPlan(Scrubber.Walk(new[] { target }, new ListReporter()), Algorithms.ByKey["nist-zeros"], new ListReporter());
            Assert.False(Directory.Exists(target));
            Assert.Equal("keep-me"u8.ToArray(), File.ReadAllBytes(outside));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Hard_link_content_is_overwritten_for_every_name()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-link-");
        try
        {
            var first = Path.Combine(folder.FullName, "first.txt");
            var second = Path.Combine(folder.FullName, "second.txt");
            var payload = "secret"u8.ToArray();
            File.WriteAllBytes(first, payload);
            Assert.True(CreateHardLinkW(second, first, IntPtr.Zero), $"CreateHardLinkW failed ({Marshal.GetLastWin32Error()})");
            Scrubber.ScrubPlan(Scrubber.Walk(new[] { first }, new ListReporter()), Algorithms.ByKey["nist-zeros"], new ListReporter());
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Equal(new byte[payload.Length], File.ReadAllBytes(second));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Readonly_file_is_scrubbed()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-ro-");
        try
        {
            var path = Path.Combine(folder.FullName, "locked.txt");
            File.WriteAllBytes(path, "nope"u8.ToArray());
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Scrubber.ScrubPlan(Scrubber.Walk(new[] { path }, new ListReporter()), Algorithms.ByKey["ones"], new ListReporter());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(folder.FullName))
            {
                foreach (var file in Directory.EnumerateFiles(folder.FullName, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                folder.Delete(true);
            }
        }
    }

    [Fact]
    public void Alternate_stream_is_overwritten()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-ads-");
        try
        {
            var path = Path.Combine(folder.FullName, "main.txt");
            File.WriteAllBytes(path, "visible"u8.ToArray());
            File.WriteAllText(path + ":hidden", "stream-secret");
            Scrubber.OverwriteFile(path, Algorithms.ByKey["nist-zeros"], new ListReporter(), null, direct: false);
            Assert.Equal(new byte["visible".Length], File.ReadAllBytes(path));
            var hidden = File.ReadAllBytes(path + ":hidden");
            Assert.True(hidden.AsSpan().IndexOf("stream-secret"u8) < 0);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Cancel_leaves_the_file_in_place()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-cancel-");
        try
        {
            var path = Path.Combine(folder.FullName, "big.bin");
            File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);
            var reporter = new ListReporter();
            Assert.ThrowsAny<Exception>(() =>
                Scrubber.OverwriteFile(path, Algorithms.ByKey["random-3"], reporter, (_, _) => reporter.Cancel(), direct: false));
            Assert.True(File.Exists(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Unused_space_writer_is_bounded()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-free-");
        try
        {
            var workspace = Path.Combine(folder.FullName, "workspace");
            var drive = Path.GetPathRoot(folder.FullName)!;
            Scrubber.ScrubUnused(drive, Algorithms.ByKey["nist-zeros"], new ListReporter(), reserve: 0, workspace: workspace, maxBytes: 200_000, keepFiles: true);
            var files = Directory.GetFiles(workspace, "wipe-*.bin");
            Assert.NotEmpty(files);
            long total = 0;
            foreach (var path in files)
            {
                var data = File.ReadAllBytes(path);
                total += data.Length;
                Assert.Equal(new byte[data.Length], data);
            }
            Assert.True(total <= 200_000);
            Assert.True(total > 0);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    static byte[] RandomNumber(int length)
    {
        var data = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }
}
