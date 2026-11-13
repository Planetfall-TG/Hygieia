namespace Hygieia.Tests;

public class PathTests
{
    [Fact]
    public void Prune_nested_paths()
    {
        var kept = PathRules.PruneNested(new[] { @"C:\a\b", @"C:\a", @"C:\a\b\c.txt", @"D:\x" });
        var normalized = kept.Select(PathRules.Normalize).ToHashSet();
        Assert.Equal(new HashSet<string> { PathRules.Normalize(@"C:\a"), PathRules.Normalize(@"D:\x") }, normalized);
    }

    [Fact]
    public void Within_same_drive_only()
    {
        Assert.True(PathRules.IsWithin(@"C:\a\b", @"C:\a"));
        Assert.False(PathRules.IsWithin(@"C:\ab", @"C:\a"));
        Assert.False(PathRules.IsWithin(@"D:\a\b", @"C:\a"));
    }

    [Fact]
    public void Drive_root_is_protected()
    {
        Assert.True(PathRules.IsDriveRoot(@"C:\"));
        var reason = PathRules.ProtectedReason(@"C:\");
        Assert.NotNull(reason);
        Assert.Contains("entire drive", reason);
    }

    [Fact]
    public void Windows_and_pagefile_are_protected()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        Assert.NotNull(windir);
        Assert.NotNull(PathRules.ProtectedReason(windir!));
        Assert.NotNull(PathRules.ProtectedReason(@"C:\pagefile.sys"));
        Assert.Null(PathRules.ProtectedReason(@"C:\Users\someone\pagefile.sys"));
    }

    [Fact]
    public void Ordinary_file_is_allowed()
    {
        var folder = Directory.CreateTempSubdirectory("hygieia-paths-");
        try
        {
            var path = Path.Combine(folder.FullName, "notes.txt");
            File.WriteAllBytes(path, "ok"u8.ToArray());
            Assert.Null(PathRules.ProtectedReason(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void Reserve_is_larger_on_the_windows_drive()
    {
        Assert.Equal(64 * 1024 * 1024, Native.ReserveBytes(@"D:\", safetyMargin: true, systemDrive: "C:"));
        Assert.Equal(512 * 1024 * 1024, Native.ReserveBytes(@"C:\", safetyMargin: true, systemDrive: "C:"));
        Assert.Equal(8 * 1024 * 1024, Native.ReserveBytes(@"C:\", safetyMargin: false, systemDrive: "C:"));
    }

    [Fact]
    public void Format_bytes()
    {
        Assert.Equal("0 bytes", Text.FormatBytes(0));
        Assert.Equal("1.0 KB", Text.FormatBytes(1024));
        Assert.Equal("1.5 KB", Text.FormatBytes(1536));
    }
}
