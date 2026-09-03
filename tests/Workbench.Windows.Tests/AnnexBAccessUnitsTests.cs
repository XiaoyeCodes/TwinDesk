using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class AnnexBAccessUnitsTests
{
    // Minimal framing fixtures, not complete decodable H264. Real decode evidence comes from MediaProbe.
    private static readonly byte[] Key = [0,0,0,1,0x67,0x42,0x40,0x1F,0x11, 0,0,1,0x68,0x11, 0,0,0,1,0x65,0x11];

    [Fact]
    public void MixedStartCodesBecomeFourByteStartCodes()
    {
        var unit = new AnnexBAccessUnits().Normalize(Key, 0);
        Assert.Equal("avc1.42401F", unit.CodecString);
        Assert.Equal([7,8,5], unit.NalTypes);
        Assert.True(unit.KeyFrame);
        Assert.Equal(Key.Length + 1, unit.Data.Length);
    }

    [Fact]
    public void LaterIdrRepeatsActualParameterSets()
    {
        var normalizer = new AnnexBAccessUnits();
        normalizer.Normalize(Key, 0);
        var next = normalizer.Normalize([0,0,1,0x65,0x33], 33333);
        Assert.Equal([7,8,5], next.NalTypes);
        Assert.True(next.KeyFrame);
        Assert.Equal("avc1.42401F", next.CodecString);
    }

    [Fact]
    public void DeltaRetainsItsTimestampWithoutAddingHeaders()
    {
        var normalizer = new AnnexBAccessUnits();
        normalizer.Normalize(Key, 0);
        var next = normalizer.Normalize([0,0,1,0x41,0x33], 33333);
        Assert.Equal([1], next.NalTypes);
        Assert.False(next.KeyFrame);
        Assert.Equal(33333, next.TimestampUs);
    }

    [Theory]
    [InlineData(new byte[] {})]
    [InlineData(new byte[] {0,0,0,2,0x65,0x11})]
    [InlineData(new byte[] {0,0,1})]
    [InlineData(new byte[] {0,0,1,0x65,0x11})]
    [InlineData(new byte[] {0,0,1,0x67,0x42})]
    [InlineData(new byte[] {0,0,1,0x41,0x11})]
    [InlineData(new byte[] {0,0,1,0xE7,0x42,0x40,0x1F})]
    public void MalformedOrNonIndependentStartIsRejected(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => new AnnexBAccessUnits().Normalize(bytes, 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void OldTimestampsAreRejected(long timestamp)
    {
        var normalizer = new AnnexBAccessUnits();
        normalizer.Normalize(Key, 0);
        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(Key, timestamp));
    }

    [Fact]
    public void OversizedInputRejectedBeforeParsing() =>
        Assert.Throws<InvalidDataException>(() => new AnnexBAccessUnits().Normalize(new byte[8 * 1024 * 1024 + 1], 0));

    [Theory]
    [InlineData(0,1280,720)]
    [InlineData(18001,1280,720)]
    [InlineData(30,64,720)]
    [InlineData(30,1281,720)]
    [InlineData(30,1280,63)]
    public void InvalidProbeSettingsFailBeforeActivatingNativeEncoder(int frames, int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => H264Probe.Run(frames,true,_ => {},CancellationToken.None,width,height));

    [Fact]
    public void AlreadyCancelledProbeDoesNotActivateEncoder() =>
        Assert.Throws<OperationCanceledException>(() => H264Probe.Run(30,true,_ => {},new CancellationToken(true)));

    [Fact]
    public void CancelledRunDoesNotConstructLiveSource()
    {
        bool constructed = false;
        Assert.Throws<OperationCanceledException>(() => H264Probe.Run(30, true, _ => {}, new CancellationToken(true),
            sourceFactory: () => { constructed = true; throw new InvalidOperationException(); }));
        Assert.False(constructed);
    }

    [Fact]
    public void MissingFrameCallbackFailsBeforeNativeStartup() =>
        Assert.Throws<ArgumentNullException>(() => H264Probe.Run(30, true, null!, CancellationToken.None));

    [Fact]
    public void NullLiveSourceNeverBecomesGeneratedPattern() =>
        Assert.Throws<InvalidOperationException>(() => H264Probe.Run(30, true, _ => {}, CancellationToken.None,
            sourceFactory: () => null!));

    [Fact]
    public void ChangedProfileRequiresASeparateDecoderConfiguration()
    {
        var normalizer = new AnnexBAccessUnits();
        normalizer.Normalize(Key, 0);
        var changed = (byte[])Key.Clone();
        changed[5] = 0x64;
        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(changed, 33333));
    }
}
