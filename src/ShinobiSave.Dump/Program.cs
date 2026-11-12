using System.Text;
using ShinobiSave.Core;

// Usage: ShinobiSave.Dump <save.sav> [--max N] [--depth N]
// Prints the GVAS tree, checks that re-serialising reproduces the file byte-for-byte.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ShinobiSave.Dump <save.sav> [--max N] [--depth N]");
    return 2;
}

// Pak research commands (need a local game install):
//   --pak-key <pak> <game exe> <keyfile>          recover the AES key into keyfile (hex)
//   --pak-list <pak> <keyfile> <out.txt>          write the file list
//   --pak-extract <pak> <keyfile> <outDir> <text>...   extract files whose path contains any text
if (args[0] == "--pak-key")
{
    var key = ShinobiSave.Dump.Pak.FindKey(args[1], args[2]);
    if (key == null) { Console.WriteLine("key not found"); return 1; }
    File.WriteAllText(args[3], Convert.ToHexString(key));
    Console.WriteLine($"key found, written to {args[3]}");
    return 0;
}
if (args[0] is "--pak-list" or "--pak-extract")
{
    using var pak = new ShinobiSave.Dump.Pak(args[1], Convert.FromHexString(File.ReadAllText(args[2]).Trim()));
    Console.WriteLine($"mount {pak.MountPoint}, {pak.Entries.Count} files");
    if (args[0] == "--pak-list")
    {
        File.WriteAllLines(args[3], pak.Entries.Select(e => $"{e.UncompressedSize}\t{e.Compression}\t{(e.Encrypted ? 1 : 0)}\t{e.Name}"));
        return 0;
    }
    foreach (var e in pak.Entries.Where(e => args.Skip(4).Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase))))
    {
        string dst = Path.Combine(args[3], e.Name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, pak.Read(e));
        Console.WriteLine($"  {e.UncompressedSize,10}  {e.Name}");
    }
    return 0;
}

//   --tables <extracted dir> <outDir>             convert every DataTable .uasset under dir to .tsv
if (args[0] == "--tables")
{
    Directory.CreateDirectory(args[2]);
    int ok = 0, bad = 0;
    foreach (var f in Directory.EnumerateFiles(args[1], "*.uasset", SearchOption.AllDirectories))
    {
        try
        {
            var table = ShinobiSave.Dump.UAssetTable.Load(f);
            string rel = Path.GetRelativePath(args[1], f).Replace(Path.DirectorySeparatorChar, '~');
            File.WriteAllLines(Path.Combine(args[2], Path.ChangeExtension(rel, ".tsv")), table.ToLines());
            ok++;
        }
        catch (Exception tableError)
        {
            bad++;
            if (args.Contains("--verbose")) Console.WriteLine($"  skip {Path.GetFileName(f)}: {tableError}");
        }
    }
    Console.WriteLine($"{ok} tables written, {bad} skipped");
    return 0;
}

//   --names <tsv dir from --tables> <names.tsv>   build the editor's ID -> display name table
if (args[0] == "--names")
{
    ShinobiSave.Dump.NamesBuilder.Build(args[1], args[2]);
    return 0;
}

string path = args[0];
int maxItems = 3, maxDepth = 64;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--max") maxItems = int.Parse(args[i + 1]);
    if (args[i] == "--depth") maxDepth = int.Parse(args[i + 1]);
}

// --seed <ids.txt> <schema.txt> <save> [<save>...]: regenerate the editor's embedded ID list
// and field schema from one or more saves.
if (args.Length >= 4 && args[0] == "--seed")
{
    var cat = new IdCatalog();
    var schema = new SaveSchema();
    foreach (var savePath in args.Skip(3))
    {
        var loaded = ShinobiSaveFile.Load(savePath);
        cat.Harvest(loaded);
        schema.Harvest(loaded.Gvas);
    }
    // Map keys (flags, rewards, counters) are read from the loaded save at run time; leave them out.
    string[] skip = { "ID_Flag_", "ID_Reward_", "ID_Counter_", "ID_GameStringData_" };
    var ids = cat.All.Where(id => !skip.Any(s => id.StartsWith(s, StringComparison.Ordinal))).Concat(cat.AllEnumValues).ToList();
    File.WriteAllLines(args[1], ids);
    var lines = schema.ToLines().ToList();
    File.WriteAllLines(args[2], lines);
    Console.WriteLine($"{ids.Count} ids -> {args[1]}; {lines.Count} schema fields -> {args[2]}");
    return 0;
}

byte[] original = File.ReadAllBytes(path);
var file = GvasFile.Load(original);

// --selftest: load through the editor's model, check equip counts and a full save round trip.
if (args.Contains("--selftest"))
{
    var sf = ShinobiSaveFile.Load(path);
    Console.WriteLine($"kind {sf.Kind}, items {(sf.Items?.Count.ToString() ?? "none")} {sf.ItemBoxError}");
    Console.WriteLine($"untouched ToBytes identical: {sf.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(path))}");
    if (sf.Items != null)
    {
        var before = sf.Items.Select(i => i.EquipCount).ToArray();
        int changed = sf.RecountEquipped();
        Console.WriteLine($"loadouts {sf.Loadouts.Count}, equip recount changed {changed} items");
        for (int i = 0; i < before.Length; i++)
            if (before[i] != sf.Items[i].EquipCount)
                Console.WriteLine($"  slot {i} {sf.Items[i].Id}: stored {before[i]} recount {sf.Items[i].EquipCount}");
        // Force a recompress and confirm the result decodes to the same items.
        sf.Items[^1].Unknown2 ^= 1; sf.Items[^1].Unknown2 ^= 1;
        var stream = InventoryCodec.Serialize(sf.Items);
        var re = InventoryCodec.Parse(InventoryCodec.Decompress(InventoryCodec.Compress(stream)));
        Console.WriteLine($"recompress round trip: {InventoryCodec.Serialize(re).AsSpan().SequenceEqual(stream)} ({re.Count} items)");
    }
    return 0;
}

// --maps: print every map entry in full (counters, flags, strings, scrolls).
if (args.Contains("--maps"))
{
    Console.OutputEncoding = Encoding.UTF8;
    foreach (var name in new[] { "m_gameCounters", "m_gameStringDatas", "m_mapScrolls", "m_gameFlags" })
    {
        var mp = ShinobiSave.Dump.Extract.Find(file.Properties, name);
        if (mp?.Value is not GvasMap map) continue;
        Console.WriteLine($"## {name} ({map.Entries.Count})");
        foreach (var e in map.Entries) Console.WriteLine($"{e.Key}\t{e.Value ?? "<null>"}");
    }
    return 0;
}

if (args.Contains("--items"))
{
    Console.OutputEncoding = Encoding.UTF8;
    ShinobiSave.Dump.Explore.Items(file, 12);
    return 0;
}

// --extract <property> <outDir>: decompress a byte-array property and stop.
int ex = Array.IndexOf(args, "--extract");
if (ex >= 0)
{
    ShinobiSave.Dump.Extract.CompressedBlocks(file, args[ex + 1], args[ex + 2]);
    return 0;
}
var sb = new StringBuilder();

sb.AppendLine($"file: {path} ({original.Length} bytes)");
sb.AppendLine($"save version {file.SaveGameVersion}, package {file.PackageVersion}, engine {file.EngineMajor}.{file.EngineMinor}.{file.EnginePatch} build {file.EngineBuild} '{file.EngineBranch}'");
sb.AppendLine($"custom format {file.CustomFormatVersion}, {file.CustomVersions.Count} custom versions, class '{file.SaveGameClass}'");
sb.AppendLine($"trailer: {BitConverter.ToString(file.Trailer)}");

byte[] again = file.ToBytes();
bool same = again.AsSpan().SequenceEqual(original);
sb.AppendLine($"round-trip: {(same ? "IDENTICAL" : "DIFFERENT")} ({again.Length} bytes)");
if (!same)
{
    int n = Math.Min(again.Length, original.Length), at = -1;
    for (int i = 0; i < n; i++) if (again[i] != original[i]) { at = i; break; }
    sb.AppendLine($"first difference at 0x{(at < 0 ? n : at):X}");
}

var raws = new List<string>();
sb.AppendLine();
DumpList(file.Properties, 0, "");
if (raws.Count > 0)
{
    sb.AppendLine();
    sb.AppendLine("UNPARSED (kept raw):");
    foreach (var r in raws) sb.AppendLine("  " + r);
}
Console.OutputEncoding = Encoding.UTF8;
Console.Write(sb.ToString());
return same ? 0 : 1;

void DumpList(List<GvasProperty> props, int depth, string pathPrefix)
{
    foreach (var p in props) DumpProp(p, depth, pathPrefix);
}

void DumpProp(GvasProperty p, int depth, string pathPrefix)
{
    string ind = new string(' ', depth * 2);
    string full = pathPrefix + p.Name;
    string idx = p.ArrayIndex != 0 ? $"[{p.ArrayIndex}]" : "";
    switch (p.Value)
    {
        case GvasStruct s:
            sb.AppendLine($"{ind}{p.Name}{idx} : Struct<{p.StructName}> @0x{p.Offset:X} size {p.SizeOnDisk}{StructInline(s)}");
            if (s.Properties != null && depth < maxDepth) DumpList(s.Properties, depth + 1, full + ".");
            break;
        case GvasArray a:
            string inner = a.InnerTag != null ? $"Struct<{a.InnerTag.StructName}>" : a.InnerType;
            sb.AppendLine($"{ind}{p.Name}{idx} : Array<{inner}> count {a.Count} @0x{p.Offset:X} size {p.SizeOnDisk}");
            if (a.Bytes != null) { sb.AppendLine($"{ind}  bytes: {Hex(a.Bytes, 32)}"); break; }
            if (depth >= maxDepth) break;
            DumpItems(a.Items, depth + 1, full);
            break;
        case GvasMap m:
            sb.AppendLine($"{ind}{p.Name}{idx} : Map<{m.KeyType},{m.ValueType}> count {m.Entries.Count} @0x{p.Offset:X} size {p.SizeOnDisk}");
            for (int i = 0; i < m.Entries.Count && i < maxItems; i++)
            {
                sb.AppendLine($"{ind}  key: {Scalar(m.Entries[i].Key)}");
                if (m.Entries[i].Key is GvasStruct ks && ks.Properties != null) DumpList(ks.Properties, depth + 3, full + ".key.");
                sb.AppendLine($"{ind}  val: {Scalar(m.Entries[i].Value)}");
                if (m.Entries[i].Value is GvasStruct vs && vs.Properties != null) DumpList(vs.Properties, depth + 3, full + ".val.");
            }
            if (m.Entries.Count > maxItems) sb.AppendLine($"{ind}  ... {m.Entries.Count - maxItems} more");
            break;
        case GvasSet set:
            sb.AppendLine($"{ind}{p.Name}{idx} : Set<{set.InnerType}> count {set.Items.Count} @0x{p.Offset:X}");
            DumpItems(set.Items, depth + 1, full);
            break;
        case GvasRaw raw:
            sb.AppendLine($"{ind}{p.Name}{idx} : {p.Type} RAW {raw.Data.Length} bytes @0x{p.Offset:X} ({raw.Reason}) {Hex(raw.Data, 24)}");
            if (raw.Reason != "FText") raws.Add($"{full} : {p.Type} ({raw.Reason})");
            break;
        default:
            string en = p.EnumName is null or "None" ? "" : $"<{p.EnumName}>";
            sb.AppendLine($"{ind}{p.Name}{idx} : {p.Type}{en} = {Scalar(p.Value)}");
            break;
    }
}

void DumpItems(List<object?> items, int depth, string full)
{
    string ind = new string(' ', depth * 2);
    if (items.Count == 0) return;
    if (items[0] is GvasStruct)
    {
        // Collapse runs of identical elements so default-filled arrays stay readable.
        int shown = 0;
        string? prev = null; int runStart = 0;
        for (int i = 0; i <= items.Count; i++)
        {
            string? key = i < items.Count ? Fingerprint((GvasStruct)items[i]!) : null;
            if (i > 0 && key != prev)
            {
                if (shown < maxItems)
                {
                    var s = (GvasStruct)items[runStart]!;
                    string range = i - 1 == runStart ? $"[{runStart}]" : $"[{runStart}..{i - 1}] ({i - runStart} identical)";
                    sb.AppendLine($"{ind}{range}{StructInline(s)}");
                    if (s.Properties != null) DumpList(s.Properties, depth + 1, full + "[].");
                }
                shown++;
                runStart = i;
            }
            prev = key;
        }
        if (shown > maxItems) sb.AppendLine($"{ind}... {shown - maxItems} more distinct runs ({shown} runs total)");
        return;
    }
    var parts = items.Take(Math.Max(maxItems * 8, 16)).Select(Scalar);
    string more = items.Count > Math.Max(maxItems * 8, 16) ? $" ... (+{items.Count - Math.Max(maxItems * 8, 16)})" : "";
    sb.AppendLine($"{ind}{string.Join(", ", parts)}{more}");
    int distinct = items.Select(Scalar).Distinct().Count();
    sb.AppendLine($"{ind}({distinct} distinct values)");
}

string Fingerprint(GvasStruct s)
{
    if (s.Native != null) return Convert.ToHexString(s.Native);
    var f = new StringBuilder();
    Append(s.Properties!, f);
    return f.ToString();

    static void Append(List<GvasProperty> props, StringBuilder f)
    {
        foreach (var p in props)
        {
            f.Append(p.Name).Append('=');
            switch (p.Value)
            {
                case GvasStruct st:
                    if (st.Native != null) f.Append(Convert.ToHexString(st.Native));
                    else { f.Append('{'); Append(st.Properties!, f); f.Append('}'); }
                    break;
                case GvasArray a:
                    f.Append('[').Append(a.Count).Append(':');
                    if (a.Bytes != null) f.Append(Convert.ToHexString(a.Bytes));
                    foreach (var it in a.Items)
                    {
                        if (it is GvasStruct ist) { if (ist.Native != null) f.Append(Convert.ToHexString(ist.Native)); else { f.Append('{'); Append(ist.Properties!, f); f.Append('}'); } }
                        else f.Append(it).Append(',');
                    }
                    f.Append(']');
                    break;
                case GvasRaw r: f.Append(Convert.ToHexString(r.Data)); break;
                case GvasMap m: f.Append("map").Append(m.Entries.Count); break;
                default: f.Append(p.Value); break;
            }
            f.Append(';');
        }
    }
}

string StructInline(GvasStruct s)
{
    if (s.Native == null) return "";
    return " = " + s.StructName switch
    {
        "Vector" or "Rotator" => $"({F(s.Native, 0)}, {F(s.Native, 4)}, {F(s.Native, 8)})",
        "Vector2D" => $"({F(s.Native, 0)}, {F(s.Native, 4)})",
        "Quat" or "LinearColor" or "Vector4" => $"({F(s.Native, 0)}, {F(s.Native, 4)}, {F(s.Native, 8)}, {F(s.Native, 12)})",
        "DateTime" or "Timespan" => BitConverter.ToInt64(s.Native, 0).ToString(),
        _ => Convert.ToHexString(s.Native),
    };
}

static string F(byte[] b, int o) => BitConverter.ToSingle(b, o).ToString("0.###");

static string Scalar(object? v) => v switch
{
    null => "<null>",
    string s => "\"" + s + "\"",
    GvasWideString w => "L\"" + w.Value + "\"",
    GvasStruct st => st.Native != null ? $"{st.StructName}:{Convert.ToHexString(st.Native)}" : "{struct}",
    float f => f.ToString("0.######"),
    _ => v.ToString() ?? "",
};

static string Hex(byte[] b, int max) =>
    BitConverter.ToString(b, 0, Math.Min(max, b.Length)).Replace('-', ' ') + (b.Length > max ? " ..." : "");
