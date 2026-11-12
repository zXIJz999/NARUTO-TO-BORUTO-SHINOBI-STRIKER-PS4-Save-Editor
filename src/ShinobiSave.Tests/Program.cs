using System.Data;
using ShinobiSave.Core;
using ShinobiSave.Editor;

// Edits copies of real saves through the same classes the editor uses, saves, reloads and
// checks that exactly the intended values changed.

if (args.Length == 0) { Console.Error.WriteLine("usage: ShinobiSave.Tests <saves folder>"); return 2; }
string dir = args[0];
string tmp = Path.Combine(Path.GetTempPath(), "shinobi-save-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
int failed = 0, passed = 0;

void Check(bool ok, string what)
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
}

string Copy(string relative)
{
    string dst = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".sav");
    File.Copy(Path.Combine(dir, relative), dst);
    return dst;
}

try
{
    // 1. Untouched saves must come back byte-identical.
    foreach (var rel in new[] { "Customize_v002.sav", "System_v002.sav", @"backup\Customize_v002.sav", @"backup\System_v002.sav" })
    {
        var original = File.ReadAllBytes(Path.Combine(dir, rel));
        Check(ShinobiSaveFile.Load(Path.Combine(dir, rel)).ToBytes().AsSpan().SequenceEqual(original), $"round trip {rel}");
    }

    // 2. Progressed save: edit one of everything.
    {
        string path = Copy("Customize_v002.sav");
        var before = ShinobiSaveFile.Load(path);
        var save = ShinobiSaveFile.Load(path);
        var schema = SaveSchema.FromSeed();
        bool dirty = false;

        save.Prop("m_playerBaseData", "m_playerName")!.Value = "TestName";

        var counters = new MapTable(save.Map("m_gameCounters")!, () => dirty = true);
        counters.Find("ID_Counter_Money")!["Value"] = 777;

        var flags = new MapTable(save.Map("m_gameFlags")!, () => dirty = true);
        int flagCount = flags.Table.Rows.Count;
        flags.Table.Rows.Add("ID_Flag_Test_Added", true, -1);
        flags.Find("ID_Flag_R8480")!.Delete();

        var strings = new MapTable(save.Map("m_gameStringDatas")!, () => dirty = true);
        strings.Find("ID_GameStringData_R1")!["Value"] = "Edited";

        var loadouts = new StructArrayTable((GvasArray)save.Prop("m_playerSettings")!.Value!, schema, () => dirty = true);
        loadouts.Table.Rows[0]["JacketItemIndex"] = 14;
        loadouts.Table.Rows[0]["IDNameJutsu_0"] = "ID_NJT_ENTRY_00";

        var empty = save.Items!.First(i => i.IsEmpty);
        int emptyIndex = empty.Index;
        empty.Id = "ID_CustomJacket_M_16"; empty.Type = "Jacket"; empty.Skill1 = "ID_Skill_Top_25";

        counters.Commit(); flags.Commit(); strings.Commit();
        save.RecountEquipped();
        string? backup = save.Save(path);
        Check(dirty, "edits raise the dirty callback");
        Check(backup != null && File.ReadAllBytes(backup).AsSpan().SequenceEqual(before.ToBytes()), "backup holds the previous file");

        var after = ShinobiSaveFile.Load(path);
        Check(after.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(path)), "edited file round trips");
        Check((string)after.Prop("m_playerBaseData", "m_playerName")!.Value! == "TestName", "player name changed");

        int Counter(ShinobiSaveFile s, string key) => (int)s.Map("m_gameCounters")!.Entries.First(e => (string)e.Key! == key).Value!;
        Check(Counter(after, "ID_Counter_Money") == 777, "money changed");
        Check(Counter(after, "ID_Counter_TotalMoney") == Counter(before, "ID_Counter_TotalMoney"), "other counter untouched");
        Check(after.Map("m_gameCounters")!.Entries.Count == before.Map("m_gameCounters")!.Entries.Count, "counter count unchanged");

        var afterFlags = after.Map("m_gameFlags")!.Entries;
        Check(afterFlags.Count == flagCount, "flag count: one added, one deleted");
        Check(afterFlags.Any(e => (string?)e.Key == "ID_Flag_Test_Added" && e.Value is true), "flag added");
        Check(afterFlags.All(e => (string?)e.Key != "ID_Flag_R8480"), "flag deleted");

        var afterStrings = after.Map("m_gameStringDatas")!.Entries;
        Check((string?)afterStrings.First(e => (string)e.Key! == "ID_GameStringData_R1").Value == "Edited", "string changed");
        Check(afterStrings.First(e => (string)e.Key! == "ID_GameStringData_R3").Value == null, "untouched null string still null");

        var lo = after.Loadouts[0].Properties!;
        Check((int)lo.First(p => p.Name == "JacketItemIndex").Value! == 14, "loadout item index changed");
        Check((string)lo.First(p => p.Name == "IDNameJutsu_0").Value! == "ID_NJT_ENTRY_00", "loadout jutsu changed");

        var added = after.Items![emptyIndex];
        Check(added.Id == "ID_CustomJacket_M_16" && added.Type == "Jacket" && added.Skill1 == "ID_Skill_Top_25", "item added to empty slot");
        Check(after.Items[14].EquipCount == before.Items![14].EquipCount + 1, "equip count follows the loadout edit");
        Check(after.Items[2].Skill1 == before.Items[2].Skill1 && after.Items[2].Id == before.Items[2].Id, "untouched UTF-16 item strings preserved");
        Check(after.Items.Count == before.Items.Count, "slot count unchanged");

        // Everything outside the edited properties must be byte-identical.
        string[] touched = { "m_playerBaseData", "m_playerSettings", "m_compressedDataV2", "m_gameCounters", "m_gameFlags", "m_gameStringDatas" };
        bool restSame = true;
        foreach (var p in before.Root.Where(p => !touched.Contains(p.Name)))
        {
            var q = after.Root.FirstOrDefault(x => x.Name == p.Name);
            if (q == null || !Bytes(p).AsSpan().SequenceEqual(Bytes(q))) { restSame = false; Console.WriteLine($"     differs: {p.Name}"); }
        }
        Check(restSame && after.Root.Count == before.Root.Count, "all other properties byte-identical");
    }

    // 3. Fresh save: add a field the game left at its default, and drop it again.
    {
        string path = Copy(@"backup\Customize_v002.sav");
        var original = File.ReadAllBytes(path);
        var save = ShinobiSaveFile.Load(path);
        var schema = SaveSchema.FromSeed();
        schema.Harvest(save.Gvas);
        Check(save.Prop("m_remakeTicketCount") == null, "fresh save has no remake ticket field");

        var def = schema.Fields("NN_GameData_ForSave").First(f => f.Name == "m_remakeTicketCount");
        var prop = SaveSchema.Create(def);
        prop.Value = 5;
        schema.InsertInOrder("NN_GameData_ForSave", save.Root, prop);
        save.Save(path);

        var after = ShinobiSaveFile.Load(path);
        Check(after.Prop("m_remakeTicketCount")?.Value is 5, "added field survives a reload");
        int at = after.Root.FindIndex(p => p.Name == "m_remakeTicketCount");
        Check(at > 0 && after.Root[at - 1].Name == "m_mapScrolls", "added field sits in game order");

        after.Root.RemoveAt(at);
        Check(after.ToBytes().AsSpan().SequenceEqual(original), "removing it restores the original bytes");
    }

    // 4. Friendly names.
    {
        var names = GameNames.Load();
        Check(names.Count > 8000, "name table is embedded");
        Check(names.Display("ID_NJT_D31NJ3") == "Kamui Shuriken", "jutsu ID resolves to its game name");
        Check(names.Display("ID_Something_New_07") == "Something New 07", "unknown ID is tidied instead of shown raw");
        var tops = names.Entries("DT_CustomJacket");
        Check(tops.Select(e => e.Name).Distinct().Count() == tops.Count, "drop-down names are unique");
        Check(names.Flag("ID_Flag_Acquisition_CustomJacket_M_00") is { group: "Tops", what: "Owned" }, "unlock flag is described");
        Check(names.Counter("ID_Counter_Money").label == "Ryo (money)", "counter is described");

        // Describing a map must not change what gets written back.
        string path = Copy("Customize_v002.sav");
        var original = File.ReadAllBytes(path);
        var save = ShinobiSaveFile.Load(path);
        bool dirty = false;
        var flags = new MapTable(save.Map("m_gameFlags")!, () => dirty = true, new[] { "Group", "Name" },
            k => { var f = names.Flag(k); return new[] { f.group, f.label }; });
        flags.Commit();
        Check(!dirty && save.ToBytes().AsSpan().SequenceEqual(original), "named map view leaves the save untouched");
    }
}
finally
{
    Directory.Delete(tmp, true);
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static byte[] Bytes(GvasProperty p) => new GvasFile { Properties = { p } }.ToBytes();
