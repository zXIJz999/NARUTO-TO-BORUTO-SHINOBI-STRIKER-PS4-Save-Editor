using ShinobiSave.Core;

namespace ShinobiSave.Dump;

/// <summary>Diagnostic helpers used while mapping the save. Not needed by the editor.</summary>
public static class Extract
{
    /// <summary>Writes every decompressed block found in a named byte-array property to outDir.</summary>
    public static void CompressedBlocks(GvasFile file, string propName, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var prop = Find(file.Properties, propName) ?? throw new Exception($"property {propName} not found");
        var bytes = (prop.Value as GvasArray)?.Bytes ?? throw new Exception($"{propName} is not a byte array");
        File.WriteAllBytes(Path.Combine(outDir, propName + ".raw.bin"), bytes);

        int offset = 0, index = 0;
        while (offset < bytes.Length)
        {
            if (!UeCompressed.LooksCompressed(bytes, offset))
            {
                Console.WriteLine($"  0x{offset:X}: no block tag, {bytes.Length - offset} trailing bytes: {BitConverter.ToString(bytes, offset, Math.Min(32, bytes.Length - offset))}");
                break;
            }
            int start = offset;
            var plain = UeCompressed.DecompressBlock(bytes, ref offset, out int chunk);
            string name = Path.Combine(outDir, $"{propName}.block{index}.bin");
            File.WriteAllBytes(name, plain);
            Console.WriteLine($"  block {index}: 0x{start:X}..0x{offset:X} chunk 0x{chunk:X} -> {plain.Length} bytes  {name}");
            index++;
        }
    }

    public static GvasProperty? Find(List<GvasProperty> props, string name)
    {
        foreach (var p in props)
        {
            if (p.Name == name) return p;
            if (p.Value is GvasStruct s && s.Properties != null)
            {
                var hit = Find(s.Properties, name);
                if (hit != null) return hit;
            }
        }
        return null;
    }
}
