namespace Workbench.Windows;

/// <summary>Validates Annex B framing and repeats actual parameter sets on independently decodable IDRs.</summary>
public sealed class AnnexBAccessUnits
{
    private byte[]? sps;
    private byte[]? pps;
    private long lastTimestamp = -1;
    private bool started;
    public string? CodecString { get; private set; }

    public EncodedAccessUnit Normalize(byte[] data, long timestampUs)
    {
        if (timestampUs < 0 || timestampUs <= lastTimestamp) throw new InvalidDataException("Non-increasing access unit timestamp.");
        if (data.Length is < 4 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid access unit size.");
        var units = Split(data);
        foreach (var unit in units)
        {
            int kind = unit[0] & 31;
            if ((unit[0] & 128) != 0 || kind is 0 or > 23) throw new InvalidDataException("Invalid NAL header.");
            if (kind is 7 or 8 && unit.Length > 65536) throw new InvalidDataException("Parameter set too large.");
            if (kind == 7)
            {
                if (unit.Length < 4) throw new InvalidDataException("Truncated SPS.");
                var codec = $"avc1.{unit[1]:X2}{unit[2]:X2}{unit[3]:X2}";
                if (CodecString is not null && CodecString != codec)
                    throw new InvalidDataException("Codec profile changed; a new stream configuration is required.");
                sps = unit;
                CodecString = codec;
            }
            if (kind == 8) pps = unit;
        }
        bool key = units.Any(n => (n[0] & 31) == 5);
        if (!started && !key) throw new InvalidDataException("Stream must begin with an IDR.");
        if (!units.Any(n => (n[0] & 31) is 1 or 5)) throw new InvalidDataException("Access unit contains no coded picture.");
        if (key)
        {
            if (sps is null || pps is null) throw new InvalidDataException("IDR missing required SPS/PPS.");
            if (!units.Any(n => (n[0] & 31) == 8)) units.Insert(0, pps);
            if (!units.Any(n => (n[0] & 31) == 7)) units.Insert(0, sps);
        }
        using var normalized = new MemoryStream();
        foreach (var unit in units) { normalized.Write([0, 0, 0, 1]); normalized.Write(unit); }
        if (normalized.Length > 8 * 1024 * 1024) throw new InvalidDataException("Normalized access unit too large.");
        lastTimestamp = timestampUs;
        started = true;
        return new(timestampUs, key, normalized.ToArray(), units.Select(n => n[0] & 31).ToArray(),
            CodecString ?? throw new InvalidDataException("Stream must begin with SPS configuration."));
    }

    private static List<byte[]> Split(byte[] data)
    {
        var starts = new List<(int Offset, int Size)>();
        for (int i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int size = data[i + 2] == 1 ? 3 : i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (size == 0) continue;
            starts.Add((i, size));
            i += size - 1;
        }
        if (starts.Count == 0 || starts[0].Offset != 0) throw new InvalidDataException("Expected Annex B start code, not AVCC/MP4.");
        var result = new List<byte[]>();
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Offset + starts[i].Size;
            int end = i + 1 < starts.Count ? starts[i + 1].Offset : data.Length;
            if (end <= start) throw new InvalidDataException("Empty NAL unit.");
            result.Add(data[start..end]);
        }
        return result;
    }
}
