using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

/// One class/job row from the ClassJob sheet (names come from the client language, i.e. Korean on the KR client).
public sealed record JobInfo(uint RowId, string Abbreviation, string Name, int Role, string ParentAbbreviation);

public sealed class ExcelIndex
{
    private static readonly HashSet<string> CrafterGathererJobs = new(StringComparer.Ordinal)
    {
        "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL", "MIN", "BTN", "FSH",
    };

    private static readonly HashSet<string> CombatJobs = new(StringComparer.Ordinal)
    {
        "GLA", "PGL", "MRD", "LNC", "ARC", "CNJ", "THM", "PLD", "MNK", "WAR", "DRG", "BRD",
        "WHM", "BLM", "ACN", "SMN", "SCH", "ROG", "NIN", "MCH", "DRK", "AST", "SAM", "RDM",
        "GNB", "DNC", "RPR", "SGE", "VPR", "PCT", "BLU",
    };

    // EquipSlotCategory column -> armoury chest container. Order matters: the first column with a
    // positive value decides (e.g. a body piece that also blocks the head slot has Head = -1).
    private static readonly (string Column, InventoryType Container)[] EquipSlotColumns =
    [
        ("MainHand", InventoryType.ArmoryMainHand),
        ("OffHand", InventoryType.ArmoryOffHand),
        ("Head", InventoryType.ArmoryHead),
        ("Body", InventoryType.ArmoryBody),
        ("Gloves", InventoryType.ArmoryHands),
        ("Legs", InventoryType.ArmoryLegs),
        ("Feet", InventoryType.ArmoryFeets),
        ("Ears", InventoryType.ArmoryEar),
        ("Neck", InventoryType.ArmoryNeck),
        ("Wrists", InventoryType.ArmoryWrist),
        ("FingerL", InventoryType.ArmoryRings),
        ("FingerR", InventoryType.ArmoryRings),
        ("SoulCrystal", InventoryType.ArmorySoulCrystal),
    ];

    private readonly IDataManager data;
    private Dictionary<uint, string>? names;
    private Dictionary<uint, uint>? cabinetRows;
    private HashSet<uint>? equipmentItems;
    private HashSet<uint>? glamourableItems;
    private HashSet<uint>? crafterGathererOnlyItems;
    private Dictionary<uint, int>? requiredLevels;
    private HashSet<uint>? outfitSetIds;
    private Dictionary<uint, uint>? itemJobCategory;
    private Dictionary<uint, (string Name, HashSet<string> Jobs)>? jobCategories;
    private Dictionary<uint, uint>? itemEquipSlotCategory;
    private Dictionary<uint, InventoryType>? equipSlotContainers;
    private List<JobInfo>? jobs;
    private Dictionary<uint, uint>? icons;

    public ExcelIndex(IDataManager data) => this.data = data;

    public IReadOnlyDictionary<uint, string> Names => names ??= BuildItemNames();
    public IReadOnlyDictionary<uint, uint> CabinetRows => cabinetRows ??= BuildCabinetRows();
    public IReadOnlySet<uint> CabinetItems => CabinetRows.Keys.ToHashSet();
    public IReadOnlySet<uint> EquipmentItems => equipmentItems ??= BuildEquipmentItems();
    public IReadOnlySet<uint> GlamourableItems => glamourableItems ??= BuildGlamourableItems();
    public IReadOnlySet<uint> CrafterGathererOnlyItems => crafterGathererOnlyItems ??= BuildCrafterGathererOnlyItems();
    public IReadOnlyDictionary<uint, int> RequiredLevels => requiredLevels ??= BuildRequiredLevels();
    public IReadOnlySet<uint> OutfitSetIds => outfitSetIds ??= BuildOutfitSetIds();
    public IReadOnlyList<JobInfo> Jobs => jobs ??= BuildJobs();

    public IReadOnlyDictionary<uint, uint> Icons => icons ??= BuildItemIcons();
    public uint IconOf(uint itemId) => Icons.TryGetValue(itemId, out var icon) ? icon : 0;

    public string NameOf(uint id)
        => Names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"아이템 #{id}";

    public bool TryGetCabinetRow(uint itemId, out uint rowId) => CabinetRows.TryGetValue(itemId, out rowId);
    public bool IsEquipment(uint itemId) => EquipmentItems.Contains(itemId);
    public bool IsGlamourable(uint itemId) => GlamourableItems.Contains(itemId);
    public bool IsCrafterGathererOnly(uint itemId) => CrafterGathererOnlyItems.Contains(itemId);
    public int RequiredLevelOf(uint itemId) => RequiredLevels.TryGetValue(itemId, out var level) ? level : 0;
    public bool IsOutfitSetToken(uint itemId) => OutfitSetIds.Contains(itemId);

    /// Display name of the item's ClassJobCategory (e.g. "백마도사 점성술사", "모든 클래스").
    public string JobCategoryNameOf(uint itemId)
    {
        EnsureJobCategories();
        return itemJobCategory!.TryGetValue(itemId, out var cat) && jobCategories!.TryGetValue(cat, out var info)
            ? info.Name
            : string.Empty;
    }

    /// Class/job abbreviations that may equip the item (empty when unknown).
    public IReadOnlySet<string> JobsOf(uint itemId)
    {
        EnsureJobCategories();
        return itemJobCategory!.TryGetValue(itemId, out var cat) && jobCategories!.TryGetValue(cat, out var info)
            ? info.Jobs
            : new HashSet<string>();
    }

    /// Armoury chest container the item belongs to, or null when it is not armoury gear.
    public InventoryType? ArmoryContainerOf(uint itemId)
    {
        itemEquipSlotCategory ??= BuildItemEquipSlotCategories();
        equipSlotContainers ??= BuildEquipSlotContainers();
        return itemEquipSlotCategory.TryGetValue(itemId, out var cat) && equipSlotContainers.TryGetValue(cat, out var container)
            ? container
            : null;
    }

    private IEnumerable GetSheet(string rowTypeName)
    {
        var rowType = Type.GetType($"Lumina.Excel.Sheets.{rowTypeName}, Lumina.Excel")
            ?? throw new InvalidOperationException($"Lumina 시트 행 형식을 찾지 못했습니다: {rowTypeName}");

        var getSheet = typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .First(m => m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

        var closed = getSheet.MakeGenericMethod(rowType);
        return closed.Invoke(data, [null, null]) as IEnumerable
            ?? throw new InvalidOperationException($"Excel 시트를 불러오지 못했습니다: {rowTypeName}");
    }

    private Dictionary<uint, string> BuildItemNames()
    {
        var result = new Dictionary<uint, string>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;
            result[id] = row.GetType().GetProperty("Name")?.GetValue(row)?.ToString() ?? string.Empty;
        }
        return result;
    }

    private Dictionary<uint, uint> BuildItemIcons()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;
            var icon = row.GetType().GetProperty("Icon")?.GetValue(row);
            if (icon is null) continue;
            try { result[id] = Convert.ToUInt32(icon); } catch { }
        }
        return result;
    }

    private Dictionary<uint, uint> BuildCabinetRows()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var row in GetSheet("Cabinet"))
        {
            if (row is null) continue;
            var cabinetRowId = ReadRowId(row);
            if (cabinetRowId == 0) continue;

            var itemRef = row.GetType().GetProperty("Item")?.GetValue(row);
            if (itemRef is null) continue;

            var itemId = ReadRowId(itemRef);
            if (itemId != 0)
                result[itemId] = cabinetRowId;
        }
        return result;
    }

    private HashSet<uint> BuildEquipmentItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var equipRef = row.GetType().GetProperty("EquipSlotCategory")?.GetValue(row);
            if (equipRef is not null && ReadRowId(equipRef) != 0)
                result.Add(itemId);
        }
        return result;
    }

    private HashSet<uint> BuildGlamourableItems()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var prop = row.GetType().GetProperty("IsGlamorous");
            if (prop?.PropertyType == typeof(bool) && (bool)(prop.GetValue(row) ?? false))
                result.Add(itemId);
        }
        return result;
    }

    private Dictionary<uint, int> BuildRequiredLevels()
    {
        var result = new Dictionary<uint, int>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;

            var raw = row.GetType().GetProperty("LevelEquip")?.GetValue(row);
            try { result[itemId] = Convert.ToInt32(raw); }
            catch { result[itemId] = 0; }
        }
        return result;
    }

    private HashSet<uint> BuildCrafterGathererOnlyItems()
    {
        EnsureJobCategories();
        var result = new HashSet<uint>();
        foreach (var (itemId, cat) in itemJobCategory!)
        {
            if (!jobCategories!.TryGetValue(cat, out var info)) continue;
            var hasCrafterGatherer = info.Jobs.Overlaps(CrafterGathererJobs);
            var hasCombat = info.Jobs.Overlaps(CombatJobs);
            if (hasCrafterGatherer && !hasCombat)
                result.Add(itemId);
        }
        return result;
    }

    private HashSet<uint> BuildOutfitSetIds()
    {
        var result = new HashSet<uint>();
        foreach (var row in GetSheet("MirageStoreSetItem"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id != 0)
                result.Add(id);
        }
        return result;
    }

    private void EnsureJobCategories()
    {
        if (itemJobCategory is not null && jobCategories is not null)
            return;

        var categories = new Dictionary<uint, (string Name, HashSet<string> Jobs)>();
        foreach (var row in GetSheet("ClassJobCategory"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            var name = row.GetType().GetProperty("Name")?.GetValue(row)?.ToString() ?? string.Empty;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(bool) || !prop.CanRead) continue;
                bool enabled;
                try { enabled = (bool)(prop.GetValue(row) ?? false); }
                catch { continue; }
                if (enabled && (CombatJobs.Contains(prop.Name) || CrafterGathererJobs.Contains(prop.Name)))
                    set.Add(prop.Name);
            }
            categories[id] = (name, set);
        }

        var byItem = new Dictionary<uint, uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;
            var categoryRef = row.GetType().GetProperty("ClassJobCategory")?.GetValue(row);
            if (categoryRef is null) continue;
            var cat = ReadRowId(categoryRef);
            if (cat != 0) byItem[itemId] = cat;
        }

        jobCategories = categories;
        itemJobCategory = byItem;
    }

    private Dictionary<uint, uint> BuildItemEquipSlotCategories()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var row in GetSheet("Item"))
        {
            if (row is null) continue;
            var itemId = ReadRowId(row);
            if (itemId == 0) continue;
            var equipRef = row.GetType().GetProperty("EquipSlotCategory")?.GetValue(row);
            if (equipRef is null) continue;
            var cat = ReadRowId(equipRef);
            if (cat != 0) result[itemId] = cat;
        }
        return result;
    }

    private Dictionary<uint, InventoryType> BuildEquipSlotContainers()
    {
        var result = new Dictionary<uint, InventoryType>();
        foreach (var row in GetSheet("EquipSlotCategory"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;

            var type = row.GetType();
            foreach (var (column, container) in EquipSlotColumns)
            {
                var raw = type.GetProperty(column)?.GetValue(row);
                int value;
                try { value = Convert.ToInt32(raw); }
                catch { continue; }
                if (value > 0)
                {
                    result[id] = container;
                    break;
                }
            }
        }
        return result;
    }

    private List<JobInfo> BuildJobs()
    {
        var rows = new List<(uint Id, string Abbr, string Name, int Role, uint ParentId)>();
        foreach (var row in GetSheet("ClassJob"))
        {
            if (row is null) continue;
            var id = ReadRowId(row);
            if (id == 0) continue;
            var type = row.GetType();
            var abbr = type.GetProperty("Abbreviation")?.GetValue(row)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(abbr)) continue;
            var name = type.GetProperty("Name")?.GetValue(row)?.ToString() ?? abbr;
            int role;
            try { role = Convert.ToInt32(type.GetProperty("Role")?.GetValue(row)); }
            catch { role = 0; }
            var parentRef = type.GetProperty("ClassJobParent")?.GetValue(row);
            var parentId = parentRef is null ? 0 : ReadRowId(parentRef);
            rows.Add((id, abbr, name, role, parentId));
        }

        var abbrById = rows.ToDictionary(x => x.Id, x => x.Abbr);
        return rows
            .Select(x => new JobInfo(x.Id, x.Abbr, x.Name, x.Role, abbrById.TryGetValue(x.ParentId, out var p) ? p : string.Empty))
            .ToList();
    }

    private static uint ReadRowId(object value)
    {
        var type = value.GetType();
        foreach (var name in new[] { "RowId", "RowID", "Row" })
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;
            try { return Convert.ToUInt32(prop.GetValue(value)); }
            catch { }
        }
        return 0;
    }
}
