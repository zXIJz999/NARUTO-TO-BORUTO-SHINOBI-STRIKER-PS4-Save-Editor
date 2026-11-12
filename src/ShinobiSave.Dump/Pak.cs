using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ShinobiSave.Dump;

/// <summary>
/// Minimal reader for the game's UE4 pak files (version 4, AES-256-ECB encrypted index),
/// used only to pull display-name tables out of a local install for names.tsv.
/// </summary>
public sealed class Pak : IDisposable
{
    public sealed class Entry
    {
        public string Name = "";
        public long Offset, Size, UncompressedSize;
        public int Compression;
        public bool Encrypted;
        public uint BlockSize;
        public (long start, long end)[] Blocks = Array.Empty<(long, long)>();
        public int HeaderSize;
    }

    private readonly FileStream _fs;
    private readonly Aes _aes;
    public string MountPoint = "";
    public List<Entry> Entries = new();

    public Pak(string path, byte[] key)
    {
        _fs = File.OpenRead(path);
        _aes = Aes.Create();
        _aes.Key = key;
        var (offset, size) = Footer(_fs);
        var index = new byte[size];
        _fs.Seek(offset, SeekOrigin.Begin);
        _fs.ReadExactly(index);
        index = _aes.DecryptEcb(index, PaddingMode.None);

        int p = 0;
        MountPoint = Str(index, ref p);
        int count = BitConverter.ToInt32(index, p); p += 4;
        for (int i = 0; i < count; i++)
        {
            var e = new Entry { Name = Str(index, ref p) };
            int start = p;
            e.Offset = BitConverter.ToInt64(index, p); p += 8;
            e.Size = BitConverter.ToInt64(index, p); p += 8;
            e.UncompressedSize = BitConverter.ToInt64(index, p); p += 8;
            e.Compression = BitConverter.ToInt32(index, p); p += 4;
            p += 20; // SHA1
            if (e.Compression != 0)
            {
                int n = BitConverter.ToInt32(index, p); p += 4;
                e.Blocks = new (long, long)[n];
                for (int b = 0; b < n; b++) { e.Blocks[b] = (BitConverter.ToInt64(index, p), BitConverter.ToInt64(index, p + 8)); p += 16; }
            }
            e.Encrypted = index[p++] != 0;
            e.BlockSize = BitConverter.ToUInt32(index, p); p += 4;
            e.HeaderSize = p - start;
            Entries.Add(e);
        }
    }

    public static (long offset, long size) Footer(FileStream fs)
    {
        var tail = new byte[44];
        fs.Seek(-44, SeekOrigin.End);
        fs.ReadExactly(tail);
        if (BitConverter.ToUInt32(tail, 0) != 0x5A6F12E1) throw new Exception("pak magic not found");
        return (BitConverter.ToInt64(tail, 8), BitConverter.ToInt64(tail, 16));
    }

    /// <summary>Searches an executable for the 32-byte AES key that decrypts the pak index.</summary>
    public static byte[]? FindKey(string pakPath, string exePath)
    {
        byte[] first = new byte[16];
        using (var fs = File.OpenRead(pakPath))
        {
            var (offset, _) = Footer(fs);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(first);
        }
        byte[] exe = File.ReadAllBytes(exePath);
        byte[]? found = null;

        bool Try(byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptEcb(first, PaddingMode.None);
            // The index starts with the mount point FString: a small length, then "../../../".
            return plain[1] == 0 && plain[2] == 0 && plain[3] == 0 && plain[4] == '.' && plain[5] == '.' && plain[6] == '/';
        }

        // UE 4.16 compiles the key in as a 32-character ASCII string; try those windows first.
        Parallel.For(0, exe.Length - 32, (i, state) =>
        {
            for (int k = 0; k < 32; k++) if (exe[i + k] < 0x21 || exe[i + k] > 0x7E) return;
            var key = exe.AsSpan(i, 32).ToArray();
            if (Try(key)) { found = key; state.Stop(); }
        });
        return found;
    }

    public byte[] Read(Entry e)
    {
        if (e.Compression == 0)
        {
            long len = e.Encrypted ? (e.Size + 15) & ~15L : e.Size;
            var data = new byte[len];
            _fs.Seek(e.Offset + e.HeaderSize, SeekOrigin.Begin);
            _fs.ReadExactly(data);
            if (e.Encrypted) data = _aes.DecryptEcb(data, PaddingMode.None);
            return data.AsSpan(0, (int)e.Size).ToArray();
        }
        var output = new MemoryStream((int)e.UncompressedSize);
        foreach (var (start, end) in e.Blocks)
        {
            int clen = (int)(end - start);
            int rlen = e.Encrypted ? (clen + 15) & ~15 : clen;
            var block = new byte[rlen];
            _fs.Seek(start, SeekOrigin.Begin);
            _fs.ReadExactly(block);
            if (e.Encrypted) block = _aes.DecryptEcb(block, PaddingMode.None);
            using var z = new ZLibStream(new MemoryStream(block, 0, clen), CompressionMode.Decompress);
            z.CopyTo(output);
        }
        return output.ToArray();
    }

    private static string Str(byte[] d, ref int p)
    {
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len == 0) return "";
        if (len > 0) { var s = Encoding.Latin1.GetString(d, p, len - 1); p += len; return s; }
        var w = Encoding.Unicode.GetString(d, p, (-len - 1) * 2); p += -len * 2; return w;
    }

    public void Dispose() { _fs.Dispose(); _aes.Dispose(); }
}
