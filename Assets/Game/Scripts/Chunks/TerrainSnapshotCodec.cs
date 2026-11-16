using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Compact wire format for the late-join terrain snapshot. Pure data, no Unity objects,
/// so sorting and encoding can run off the main thread.
///
/// Samples are sorted by position and each position is written as the difference from
/// the previous one (zig-zag varints), followed by the exact 4-byte density. Edited
/// samples sit next to each other, so most cost about 7 bytes instead of 16.
/// Density values are sent bit-exact: clients must reproduce the host's terrain.
/// </summary>
public static class TerrainSnapshotCodec
{
    // Worst case for one sample: three 5-byte varints and a 4-byte float.
    public const int MaxBytesPerSample = 19;
    private const int HeaderBytes = 5;

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Value;
        [FieldOffset(0)] public uint Bits;
    }

    public static int Compare(KeyValuePair<Vector3Int, float> a, KeyValuePair<Vector3Int, float> b)
    {
        Vector3Int p = a.Key, q = b.Key;
        if (p.x != q.x) return p.x < q.x ? -1 : 1;
        if (p.y != q.y) return p.y < q.y ? -1 : 1;
        if (p.z != q.z) return p.z < q.z ? -1 : 1;
        return 0;
    }

    public static void Sort(KeyValuePair<Vector3Int, float>[] samples) => Array.Sort(samples, Compare);

    /// <summary>
    /// Encodes samples starting at <paramref name="offset"/> until <paramref name="maxBytes"/> is
    /// reached. Always encodes at least one sample when any remain. Each part is self-contained.
    /// </summary>
    public static byte[] Encode(KeyValuePair<Vector3Int, float>[] samples, int offset, int maxBytes, out int consumed)
    {
        consumed = 0;
        int remaining = samples.Length - offset;
        if (remaining <= 0) return new byte[0];
        int capacity = Math.Max(maxBytes, HeaderBytes + MaxBytesPerSample);
        byte[] buffer = new byte[capacity];
        int at = HeaderBytes; // Sample count is written last, at the front.
        Vector3Int previous = Vector3Int.zero;
        while (consumed < remaining && at + MaxBytesPerSample <= capacity)
        {
            KeyValuePair<Vector3Int, float> sample = samples[offset + consumed];
            Vector3Int p = sample.Key;
            at = WriteVarint(buffer, at, ZigZag(p.x - previous.x));
            at = WriteVarint(buffer, at, ZigZag(p.y - previous.y));
            at = WriteVarint(buffer, at, ZigZag(p.z - previous.z));
            uint bits = new FloatBits { Value = sample.Value }.Bits;
            buffer[at++] = (byte)bits; buffer[at++] = (byte)(bits >> 8);
            buffer[at++] = (byte)(bits >> 16); buffer[at++] = (byte)(bits >> 24);
            previous = p;
            consumed++;
        }
        // Fixed-width count so the header size never depends on the count.
        uint count = (uint)consumed;
        buffer[0] = 1; // Format version.
        buffer[1] = (byte)count; buffer[2] = (byte)(count >> 8);
        buffer[3] = (byte)(count >> 16); buffer[4] = (byte)(count >> 24);
        byte[] result = new byte[at];
        Buffer.BlockCopy(buffer, 0, result, 0, at);
        return result;
    }

    /// <summary>Calls <paramref name="apply"/> for every sample in a part. Returns the sample count.</summary>
    public static int Decode(byte[] data, Action<Vector3Int, float> apply)
    {
        if (data == null || data.Length == 0) return 0;
        if (data.Length < HeaderBytes || data[0] != 1) throw new ArgumentException("Unknown terrain snapshot format.");
        long count = (uint)(data[1] | data[2] << 8 | data[3] << 16 | data[4] << 24);
        int at = HeaderBytes;
        Vector3Int previous = Vector3Int.zero;
        for (long i = 0; i < count; i++)
        {
            uint dx, dy, dz;
            at = ReadVarint(data, at, out dx);
            at = ReadVarint(data, at, out dy);
            at = ReadVarint(data, at, out dz);
            if (at + 4 > data.Length) throw new ArgumentException("Truncated terrain snapshot part.");
            uint bits = (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24);
            at += 4;
            previous = new Vector3Int(previous.x + UnZigZag(dx), previous.y + UnZigZag(dy), previous.z + UnZigZag(dz));
            apply(previous, new FloatBits { Bits = bits }.Value);
        }
        return (int)count;
    }

    private static uint ZigZag(int value) => unchecked((uint)((value << 1) ^ (value >> 31)));
    private static int UnZigZag(uint value) => unchecked((int)(value >> 1) ^ -(int)(value & 1));

    private static int WriteVarint(byte[] buffer, int at, uint value)
    {
        while (value >= 0x80) { buffer[at++] = (byte)(value | 0x80); value >>= 7; }
        buffer[at++] = (byte)value;
        return at;
    }

    private static int ReadVarint(byte[] data, int at, out uint value)
    {
        value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (at >= data.Length) throw new ArgumentException("Truncated terrain snapshot part.");
            byte b = data[at++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return at;
        }
        throw new ArgumentException("Corrupt terrain snapshot part.");
    }
}
