namespace Hygieia.Tests;

public class AlgorithmTests
{
    [Fact]
    public void Names_are_unique_and_the_dropdown_has_a_variety()
    {
        var names = Algorithms.All.Select(algorithm => algorithm.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.Count >= 8);
        Assert.Equal(35, Algorithms.ByKey["gutmann"].PassCount);
        Assert.Equal(3, Algorithms.ByKey["dod-3"].PassCount);
        Assert.Equal(7, Algorithms.ByKey["schneier"].PassCount);
        Assert.Equal(7, Algorithms.ByKey["rcmp"].PassCount);
        Assert.Equal(7, Algorithms.ByKey["vsitr"].PassCount);
    }

    [Fact]
    public void Gutmann_fixed_pattern()
    {
        var passes = Algorithms.ByKey["gutmann"].Passes;
        Assert.Equal(PassKind.Random, passes[0].Kind);
        Assert.Equal(new byte[] { 0x92, 0x49, 0x24 }, passes[6].Payload);
        Assert.Equal(new byte[] { 0x00 }, passes[9].Payload);
        Assert.Equal(PassKind.Random, passes[^1].Kind);
    }

    [Fact]
    public void Cycle_respects_offset()
    {
        var pattern = Algorithms.ByKey["gutmann"].Passes[6];
        Assert.Equal(new byte[] { 0x92, 0x49, 0x24, 0x92, 0x49, 0x24, 0x92 }, Algorithms.Materialize(pattern, 0, 7));
        Assert.Equal(new byte[] { 0x49, 0x24, 0x92, 0x49 }, Algorithms.Materialize(pattern, 1, 4));
        Assert.Equal(new byte[] { 0x24, 0x92 }, Algorithms.Materialize(pattern, 2, 2));
    }

    [Fact]
    public void Random_length()
    {
        var pattern = Algorithms.ByKey["nist-random"].Passes[0];
        var first = Algorithms.Materialize(pattern, 0, 64);
        var second = Algorithms.Materialize(pattern, 0, 64);
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_flags()
    {
        Assert.True(Algorithms.ByKey["dod-3"].Passes[^1].Verify);
        Assert.False(Algorithms.ByKey["nist-zeros"].Passes[0].Verify);
        Assert.Equal(new byte[] { 0xAA }, Algorithms.ByKey["vsitr"].Passes[^1].Payload);
    }
}
