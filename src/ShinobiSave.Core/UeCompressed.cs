using System.IO.Compression;

namespace ShinobiSave.Core;

/// <summary>
/// UE4 FArchive::SerializeCompressed block: a tag header, a summary, a chunk table and
/// zlib-compressed chunks. A byte array may hold several of these back to back.
/// </summary>
public static class UeCompressed
{
    public const uint PackageFileTag = 0x9E2A83C1;
    public const int DefaultChunkSize = 0x20000;

    public static bool LooksCompressed(byte[] data, int offset = 0) =>
        data.Length - offset >= 32 && BitConverter.ToUInt64(data, offset) == PackageFileTag;

    /// <summary>Decompresses one block starting at <paramref name="offset"/> and advances it.</summary>
    public static byte[] DecompressBlock(byte[] data, ref int offset, out int chunkSize)
    {
        int p = offset;
        long tag = ReadI64(data, ref p);
        if ((ulong)tag != PackageFileTag) throw new GvasFormatException($"Bad compressed block tag at 0x{offset:X}");
        chunkSize = checked((int)ReadI64(data, ref p));
        long totalComp = ReadI64(data, ref p);
        long totalUncomp = ReadI64(data, ref p);
        if (chunkSize <= 0 || totalUncomp < 0 || totalUncomp > 1 << 30 || totalComp < 0)
            throw new GvasFormatException($"Bad compressed block summary at 0x{offset:X}");

        int chunks = (int)((totalUncomp + chunkSize - 1) / chunkSize);
        var sizes = new (int comp, int uncomp)[chunks];
        for (int i = 0; i < chunks; i++)
            sizes[i] = (checked((int)ReadI64(data, ref p)), checked((int)ReadI64(data, ref p)));

        var output = new byte[totalUncomp];
        int o = 0;
        foreach (var (comp, uncomp) in sizes)
        {
            if (p + comp > data.Length) throw new GvasFormatException("Compressed chunk runs past end of data");
            using var src = new MemoryStream(data, p, comp, false);
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            int got = 0;
            while (got < uncomp)
            {
                int n = z.Read(output, o + got, uncomp - got);
                if (n == 0) throw new GvasFormatException("Compressed chunk ended early");
                got += n;
            }
            o += uncomp;
            p += comp;
        }
        offset = p;
        return output;
    }

    /// <summary>Compresses <paramref name="plain"/> into one block in the same layout.</summary>
    public static byte[] CompressBlock(byte[] plain, int chunkSize = DefaultChunkSize)
    {
        var parts = new List<byte[]>();
        var sizes = new List<int>();
        for (int o = 0; o < plain.Length; o += chunkSize)
        {
            int n = Math.Min(chunkSize, plain.Length - o);
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(plain, o, n);
            parts.Add(ms.ToArray());
            sizes.Add(n);
        }

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        w.Write((long)PackageFileTag);
        w.Write((long)chunkSize);
        w.Write((long)parts.Sum(x => x.Length));
        w.Write((long)plain.Length);
        for (int i = 0; i < parts.Count; i++) { w.Write((long)parts[i].Length); w.Write((long)sizes[i]); }
        foreach (var part in parts) w.Write(part);
        w.Flush();
        return outMs.ToArray();
    }

    private static long ReadI64(byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new GvasFormatException("Compressed block header truncated");
        long v = BitConverter.ToInt64(d, p);
        p += 8;
        return v;
    }
}
