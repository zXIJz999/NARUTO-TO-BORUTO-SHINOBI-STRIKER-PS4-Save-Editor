using System.Text;
using ShinobiSave.Core;

namespace ShinobiSave.Dump;

/// <summary>Throwaway probing of the decompressed m_compressedDataV2 stream.</summary>
public static class Explore
{
    public static byte[] Concat(GvasFile file, string propName)
    {
        var prop = Extract.Find(file.Properties, propName) ?? throw new Exception("not found");
        var bytes = (prop.Value as GvasArray)!.Bytes!;
        using var ms = new MemoryStream();
        int offset = 0;
        while (offset < bytes.Length && UeCompressed.LooksCompressed(bytes, offset))
            ms.Write(UeCompressed.DecompressBlock(bytes, ref offset, out _));
        return ms.ToArray();
    }

    public static void Items(GvasFile file, int show)
    {
        var d = Concat(file, "m_compressedDataV2");
        int p = 0;
        int total = BitConverter.ToInt32(d, p); p += 4;
        int count = BitConverter.ToInt32(d, p); p += 4;
        Console.WriteLine($"stream {d.Length} bytes, header length {total} (stream-4 = {d.Length - 4}), count {count}");

        var types = new Dictionary<string, int>();
        var cStats = new Dictionary<string, int>();
        var flagStats = new Dictionary<int, int>();
        var prefixes = new Dictionary<string, int>();
        var skills = new Dictionary<string, int>();
        long lastA = -1; int nonSeq = 0;
        int parsed = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                int start = p;
                string id = Str(d, ref p);
                int a = BitConverter.ToInt32(d, p); p += 4;
                int b = BitConverter.ToInt32(d, p); p += 4;
                string type = Str(d, ref p);
                int c = BitConverter.ToInt32(d, p); p += 4;
                int e = BitConverter.ToInt32(d, p); p += 4;
                string s1 = Str(d, ref p), s2 = Str(d, ref p), s3 = Str(d, ref p);
                int flag = d[p++];
                parsed++;

                Bump(types, type);
                Bump(cStats, $"b={b} c={c} e={e}");
                flagStats[flag] = flagStats.GetValueOrDefault(flag) + 1;
                Bump(prefixes, Prefix(id));
                foreach (var s in new[] { s1, s2, s3 }) Bump(skills, s);
                if (a != lastA + 1) nonSeq++;
                lastA = a;
                if (b != 0 || c != 0 || e != 0 || flag != 0 || (s1 != "None" && i % 9 == 0))
                    Console.WriteLine($"  * [{i}] id={id} b={b} type={Short(type)} c={c} e={e} s=({s1}|{s2}|{s3}) flag={flag}");
                if (i < show || i >= count - 3)
                    Console.WriteLine($"  [{i}] @0x{start:X} id={id} a={a} b={b} type={Short(type)} c={c} e={e} s=({s1}|{s2}|{s3}) flag={flag}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  STOPPED after {parsed} records at 0x{p:X}: {ex.Message}");
        }
        Console.WriteLine($"parsed {parsed}, end 0x{p:X}, remaining {d.Length - p}, non-sequential a: {nonSeq}");
        Print("types", types, 40);
        Print("b/c/e combos", cStats, 30);
        Console.WriteLine("flags: " + string.Join(", ", flagStats.Select(k => $"{k.Key}:{k.Value}")));
        Print("id prefixes", prefixes, 60);
        Print("skill names", skills, 60);
        Console.WriteLine("next bytes:");
        Hex(d, p, 512);
    }

    public static void Hex(byte[] d, int p, int n)
    {
        for (int o = p; o < Math.Min(d.Length, p + n); o += 16)
        {
            var sb = new StringBuilder($"{o:X6}  ");
            var asc = new StringBuilder();
            for (int i = 0; i < 16 && o + i < d.Length; i++)
            {
                sb.Append(d[o + i].ToString("X2")).Append(' ');
                asc.Append(d[o + i] is >= 32 and < 127 ? (char)d[o + i] : '.');
            }
            Console.WriteLine(sb.ToString().PadRight(58) + asc);
        }
    }

    public static string Str(byte[] d, ref int p)
    {
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len == 0) return "<null>";
        if (len > 0)
        {
            if (len > 4096) throw new Exception($"string len {len}");
            var s = Encoding.Latin1.GetString(d, p, len - 1); p += len; return s;
        }
        if (len < -4096) throw new Exception($"string len {len}");
        var w = Encoding.Unicode.GetString(d, p, (-len - 1) * 2); p += -len * 2; return "L:" + w;
    }

    private static string Prefix(string id)
    {
        var parts = id.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[1] : id;
    }

    private static string Short(string t) => t.Contains("::") ? t[(t.IndexOf("::") + 2)..] : t;

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    private static void Print(string title, Dictionary<string, int> d, int max)
    {
        Console.WriteLine($"{title} ({d.Count} distinct):");
        foreach (var kv in d.OrderByDescending(k => k.Value).Take(max)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    }
}
