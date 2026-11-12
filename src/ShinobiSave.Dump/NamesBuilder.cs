using System.Text;
using System.Text.RegularExpressions;

namespace ShinobiSave.Dump;

/// <summary>
/// Builds the editor's names.tsv (game ID -> English display name and a few useful facts)
/// from data tables converted by --tables.
/// Columns: Id, Name, Table, Rarity, Skill0, Skill1, Skill2, Extra (key=value;...).
/// </summary>
public static class NamesBuilder
{
    public static void Build(string tablesDir, string outFile)
    {
        // English text tables: table name -> key -> text.
        var text = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(tablesDir, "*~L10N~en~Localize~DataTable~*.tsv"))
        {
            string table = Path.GetFileNameWithoutExtension(f);
            table = table[(table.LastIndexOf('~') + 1)..];
            var map = text[table] = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(f))
            {
                var p = line.Split('\t');
                string? id = Field(p, "Id"), value = Field(p, "Text");
                if (id != null && value != null) map[id] = value;
            }
        }

        string? Resolve(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            int bar = reference.IndexOf('|');
            if (bar < 0) return null;
            if (!text.TryGetValue(reference[..bar], out var map) || !map.TryGetValue(reference[(bar + 1)..], out var s)) return null;
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length == 0 || s.StartsWith('[') ? null : s;
        }

        var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var coverage = new SortedDictionary<string, (int named, int total)>();
        foreach (var f in Directory.EnumerateFiles(tablesDir, "*.tsv").OrderBy(x => x, StringComparer.Ordinal))
        {
            if (f.Contains("~Localize~")) continue;
            string table = Path.GetFileNameWithoutExtension(f);
            table = table[(table.LastIndexOf('~') + 1)..];
            if (table.StartsWith("DT_Scenario", StringComparison.Ordinal) || table.StartsWith("DT_VoiceSet", StringComparison.Ordinal)
                || table.StartsWith("DT_AttackInfo", StringComparison.Ordinal)) continue;

            foreach (var line in File.ReadLines(f))
            {
                var p = line.Split('\t');
                string id = p[0];
                if (!id.StartsWith("ID_", StringComparison.Ordinal) || !Wanted(table)) continue;

                string? name = Resolve(Field(p, "DisplayNameKey")) ?? Resolve(Field(p, "NameKey")) ?? Resolve(Field(p, "TitleKey"));
                var extra = new List<string>();
                void Extra(string key, string? value) { if (!string.IsNullOrEmpty(value) && value != "None") extra.Add($"{key}={value}"); }

                switch (table)
                {
                    case "DT_PartsColors":
                        if (Field(p, "BaseColor") is { Length: 8 } c) Extra("color", "#" + c.Substring(4, 2) + c.Substring(2, 2) + c.Substring(0, 2));
                        break;
                    case "DT_CustomFacePaint":
                        name ??= FromAsset(Field(p, "RefMaterial"), "Face paint");
                        break;
                    case "DT_CustomEye":
                        name ??= FromAsset(Field(p, "EyeMaterialList_L[0]"), "Eyes");
                        break;
                    case "DT_MatchGrade":
                        var grade = Regex.Match(Field(p, "IconTexture") ?? "", @"RankGrade_Icon_([A-Za-z]+)_(\d+)");
                        if (grade.Success) name ??= $"{grade.Groups[1].Value} {int.Parse(grade.Groups[2].Value)}";
                        break;
                    case "DT_GameCounters":
                        Extra("min", Field(p, "minValue")); Extra("max", Field(p, "maxValue"));
                        break;
                    case "DT_NinjutsuData":
                        Extra("category", Short(Field(p, "NinjutsuCategory"))); Extra("type", Short(Field(p, "NinjutsuType")));
                        Extra("group", Field(p, "CategoryName"));
                        break;
                    case "DT_WeaponData":
                        Extra("category", Short(Field(p, "WeaponCategory")));
                        break;
                }
                Extra("sex", Short(Field(p, "EquipEnable"))?.Replace("ENN_EquipEnable_", ""));
                if (table != "DT_Nickname") Extra("desc", Resolve(Field(p, "DescriptionKey")));

                // The same ID can appear in several tables; keep the row that actually names it.
                if (rows.TryGetValue(id, out var existing) && (existing[1].Length > 0 || name == null)) continue;
                rows[id] = new[]
                {
                    id, name ?? "", table, Field(p, "IDRarity")?.Replace("ID_Rarity_", "") ?? "",
                    Field(p, "IDSkill0") ?? "", Field(p, "IDSkill1") ?? "", Field(p, "IDSkill2") ?? "",
                    string.Join(";", extra).Replace('\t', ' '),
                };
                coverage.TryGetValue(table, out var cv);
                coverage[table] = (cv.named + (name != null ? 1 : 0), cv.total + 1);
            }
        }

        File.WriteAllLines(outFile, rows.Values.Select(r => string.Join("\t", r)), new UTF8Encoding(false));
        Console.WriteLine($"{rows.Count} ids -> {outFile}");
        foreach (var kv in coverage.Where(k => k.Value.total >= 3))
            Console.WriteLine($"  {kv.Value.named,5}/{kv.Value.total,-5} {kv.Key}");
    }

    private static readonly string[] Tables =
    {
        "DT_CountryInfo", "DT_CustomAccessories", "DT_CustomBase", "DT_CustomeInfo", "DT_CustomEye", "DT_CustomEyeBall", "DT_CustomEyebrows",
        "DT_CustomFacePaint", "DT_CustomForehead", "DT_CustomHair", "DT_CustomJacket", "DT_CustomMouth", "DT_CustomNose", "DT_CustomPants",
        "DT_EventMatch", "DT_GameCounters", "DT_MasterData", "DT_DlcMasterAnnouncement", "DT_MatchGrade", "DT_Nickname", "DT_NinjaWeaponData",
        "DT_NinjutsuData", "DT_PartsColors", "DT_PartsScale", "DT_RoleData", "DT_Scroll", "DT_ShinobiRank", "DT_SkillTable", "DT_VoiceInfo", "DT_WeaponData",
    };

    private static bool Wanted(string table) => Array.IndexOf(Tables, table) >= 0;

    private static string? Field(string[] parts, string key)
    {
        string prefix = key + "=";
        foreach (var p in parts)
            if (p.StartsWith(prefix, StringComparison.Ordinal)) return p[prefix.Length..];
        return null;
    }

    private static string? Short(string? enumValue) =>
        enumValue == null ? null : enumValue[(enumValue.IndexOf("::", StringComparison.Ordinal) + 2)..];

    /// <summary>Turns an asset path like .../MI_CHR_FacePaint_Naruto.MI_CHR_FacePaint_Naruto into "Face paint: Naruto".</summary>
    private static string? FromAsset(string? path, string label)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string leaf = path[(path.LastIndexOf('.') + 1)..];
        leaf = Regex.Replace(leaf, "^(MI|M|T|SK)_", "");
        leaf = Regex.Replace(leaf, "^(CHR|UI)_", "");
        leaf = Regex.Replace(leaf, "(?i)^(FacePaint|eyes|Face)_?", "");
        leaf = Regex.Replace(leaf, "(?i)_(L|R|BC|01_L)$", "");
        leaf = leaf.Replace('_', ' ').Trim();
        if (leaf.Length == 0) return $"{label}: None";
        return $"{label}: {char.ToUpperInvariant(leaf[0])}{leaf[1..]}";
    }
}
