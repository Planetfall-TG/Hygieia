using System.Security.Cryptography;

namespace Hygieia;

public enum PassKind
{
    Random,
    Cycle,
}

public sealed class Pass
{
    public PassKind Kind { get; }
    public byte[] Payload { get; }
    public bool Verify { get; }

    public Pass(PassKind kind, byte[]? payload = null, bool verify = false)
    {
        Kind = kind;
        Payload = payload ?? Array.Empty<byte>();
        Verify = verify;
    }

    public string Phrase()
    {
        string text;
        if (Kind == PassKind.Random)
            text = "random";
        else if (Payload.Length == 1)
            text = $"0x{Payload[0]:X2}";
        else
            text = string.Join(" ", Payload.Select(b => $"{b:X2}"));
        if (Verify)
            text += ", then read back";
        return text;
    }
}

public sealed class Algorithm
{
    public string Key { get; }
    public string Name { get; }
    public string Summary { get; }
    public string Note { get; }
    public IReadOnlyList<Pass> Passes { get; }
    public int PassCount => Passes.Count;

    public Algorithm(string key, string name, string summary, string note, IReadOnlyList<Pass> passes)
    {
        Key = key;
        Name = name;
        Summary = summary;
        Note = note;
        Passes = passes;
    }
}

public static class Algorithms
{
    const string Ssd =
        "On a solid-state drive, wear leveling can keep copies this overwrite never sees. " +
        "One pass is enough to stop ordinary undelete tools on a hard disk.";

    public static IReadOnlyList<Algorithm> All { get; } = Build();
    public static IReadOnlyDictionary<string, Algorithm> ByName { get; } = All.ToDictionary(a => a.Name);
    public static IReadOnlyDictionary<string, Algorithm> ByKey { get; } = All.ToDictionary(a => a.Key);
    public static Algorithm Default => All[0];

    public static byte[] Materialize(Pass pass, long offset, int length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0)
            return Array.Empty<byte>();
        if (pass.Kind == PassKind.Random)
        {
            var random = new byte[length];
            RandomNumberGenerator.Fill(random);
            return random;
        }
        var cycle = pass.Payload;
        if (cycle.Length == 0)
            throw new InvalidOperationException("pattern is empty");
        var start = (int)(offset % cycle.Length);
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = cycle[(start + i) % cycle.Length];
        return data;
    }

    static Pass Byte(byte value, bool verify = false) => new(PassKind.Cycle, new[] { value }, verify);
    static Pass Cycle(byte[] values, bool verify = false) => new(PassKind.Cycle, values, verify);
    static Pass Random(bool verify = false) => new(PassKind.Random, Array.Empty<byte>(), verify);

    static string Summary(IReadOnlyList<Pass> passes, string lead) =>
        $"{lead} Passes: {string.Join(", ", passes.Select(p => p.Phrase()))}.";

    static IReadOnlyList<Pass> Gutmann()
    {
        var fixedPasses = new List<Pass>
        {
            Cycle(new byte[] { 0x55, 0x55, 0x55 }),
            Cycle(new byte[] { 0xAA, 0xAA, 0xAA }),
            Cycle(new byte[] { 0x92, 0x49, 0x24 }),
            Cycle(new byte[] { 0x49, 0x24, 0x92 }),
            Cycle(new byte[] { 0x24, 0x92, 0x49 }),
        };
        for (var value = 0; value < 0x100; value += 0x11)
            fixedPasses.Add(Byte((byte)value));
        fixedPasses.Add(Cycle(new byte[] { 0x92, 0x49, 0x24 }));
        fixedPasses.Add(Cycle(new byte[] { 0x49, 0x24, 0x92 }));
        fixedPasses.Add(Cycle(new byte[] { 0x24, 0x92, 0x49 }));
        fixedPasses.Add(Cycle(new byte[] { 0x6D, 0xB6, 0xDB }));
        fixedPasses.Add(Cycle(new byte[] { 0xB6, 0xDB, 0x6D }));
        fixedPasses.Add(Cycle(new byte[] { 0xDB, 0x6D, 0xB6 }));
        var passes = new List<Pass>();
        for (var i = 0; i < 4; i++)
            passes.Add(Random());
        passes.AddRange(fixedPasses);
        for (var i = 0; i < 4; i++)
            passes.Add(Random());
        return passes;
    }

    static IReadOnlyList<Algorithm> Build()
    {
        return new List<Algorithm>
        {
            new("nist-random", "NIST SP 800-88 Clear — random (1 pass)",
                Summary(new[] { Random() }, "One pass of random data."),
                "This is the current NIST clear method for media you can overwrite. " + Ssd,
                new[] { Random() }),
            new("nist-zeros", "NIST SP 800-88 Clear — zeros (1 pass)",
                Summary(new[] { Byte(0x00) }, "One pass of zeros."),
                "The same NIST clear idea, using 0x00 instead of random data. " + Ssd,
                new[] { Byte(0x00) }),
            new("ones", "Single pass — ones (1 pass)",
                Summary(new[] { Byte(0xFF) }, "One pass of 0xFF."),
                Ssd,
                new[] { Byte(0xFF) }),
            new("random-3", "Random data (3 passes)",
                Summary(new[] { Random(), Random(), Random() }, "Three passes of new random data."),
                "A margin beyond the single pass NIST describes. " + Ssd,
                new[] { Random(), Random(), Random() }),
            new("hmg-baseline", "HMG IS5 Baseline (1 pass)",
                Summary(new[] { Byte(0x00, true) }, "British baseline pattern."),
                "One pass of 0x00, then a read-back check. " + Ssd,
                new[] { Byte(0x00, true) }),
            new("hmg-enhanced", "HMG IS5 Enhanced (3 passes)",
                Summary(new[] { Byte(0x00), Byte(0xFF), Random(true) }, "British enhanced pattern."),
                "0x00, then 0xFF, then random, then a read-back check. " + Ssd,
                new[] { Byte(0x00), Byte(0xFF), Random(true) }),
            new("dod-3", "DoD 5220.22-M (3 passes)",
                Summary(new[] { Byte(0x00), Byte(0xFF), Random(true) }, "The 3-pass pattern published as DoD 5220.22-M."),
                "Zeros, ones, then random, with a read-back check. " + Ssd,
                new[] { Byte(0x00), Byte(0xFF), Random(true) }),
            new("dod-7", "DoD 5220.22-M ECE (7 passes)",
                Summary(new[] { Byte(0x00), Byte(0xFF), Random(), Byte(0x00), Byte(0xFF), Random(), Random(true) },
                    "A 7-pass pattern tools publish as DoD 5220.22-M ECE."),
                "The original manual is less specific than the patterns tools ship. This is the common 7-pass version. " + Ssd,
                new[] { Byte(0x00), Byte(0xFF), Random(), Byte(0x00), Byte(0xFF), Random(), Random(true) }),
            new("schneier", "Bruce Schneier (7 passes)",
                Summary(new[] { Byte(0xFF), Byte(0x00), Random(), Random(), Random(), Random(), Random() },
                    "Bruce Schneier's method from Applied Cryptography."),
                "0xFF, then 0x00, then five random passes. " + Ssd,
                new[] { Byte(0xFF), Byte(0x00), Random(), Random(), Random(), Random(), Random() }),
            new("rcmp", "RCMP TSSIT OPS-II (7 passes)",
                Summary(new[] { Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Random(true) },
                    "The RCMP TSSIT OPS-II pattern."),
                "Alternating 0x00 and 0xFF, then random, then a read-back check. " + Ssd,
                new[] { Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Random(true) }),
            new("vsitr", "German VSITR (7 passes)",
                Summary(new[] { Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0xAA) },
                    "The German VSITR pattern."),
                "Alternating 0x00 and 0xFF, then 0xAA. " + Ssd,
                new[] { Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0x00), Byte(0xFF), Byte(0xAA) }),
            new("gutmann", "Peter Gutmann (35 passes)",
                "Thirty-five passes from Peter Gutmann's 1996 paper: four random, 27 fixed patterns, four random.",
                "Those fixed patterns targeted very old magnetic encodings. On a modern drive this is slow and no stronger than one random pass. A free-space run can take days.",
                Gutmann()),
        };
    }
}
