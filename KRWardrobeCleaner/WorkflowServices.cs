using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace KRWardrobeCleaner;

public readonly record struct ItemLocationKey(InventoryType Container, int Slot)
{
    public string Id => $"{(int)Container}:{Slot}";
}

public sealed record PhysicalGearEntry(
    ItemLocationKey Key,
    uint ItemId,
    string Name,
    int RequiredLevel,
    bool IsCrafterGatherer,
    bool IsDyed,
    bool IsGlamoured,
    bool HasMateria,
    int ConditionPercent,
    int MateriaCount);

public sealed record DresserArmoireEntry(
    int DresserSlot,
    uint ItemId,
    string Name,
    bool IsDyed,
    bool UsedOnPlate);

public sealed record DiscardEntry(
    ItemLocationKey Key,
    uint ItemId,
    string Name,
    int Quantity,
    bool IsEquipment,
    int RequiredLevel,
    bool IsCrafterGatherer,
    bool IsDyed);

public sealed unsafe class InventoryScanner
{
    public static readonly InventoryType[] ArmoryContainers =
    [
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal,
    ];

    public static readonly InventoryType[] BagContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly ExcelIndex excel;

    public InventoryScanner(ExcelIndex excel) => this.excel = excel;

    public List<PhysicalGearEntry> Scan(bool includeBags, bool includeArmory, bool requireGlamourable = false)
    {
        var result = new List<PhysicalGearEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        IEnumerable<InventoryType> types = [];
        if (includeBags) types = types.Concat(BagContainers);
        if (includeArmory) types = types.Concat(ArmoryContainers);

        foreach (var type in types)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item == null || item->ItemId == 0)
                    continue;

                var itemId = item->GetBaseItemId();
                if (itemId == 0 || !excel.IsEquipment(itemId))
                    continue;
                if (requireGlamourable && !excel.IsGlamourable(itemId))
                    continue;

                result.Add(Describe(type, slotIndex, item, itemId));
            }
        }

        return result
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => (int)x.Key.Container)
            .ThenBy(x => x.Key.Slot)
            .ToList();
    }

    public List<DiscardEntry> ScanContainerAll(InventoryType type)
    {
        var result = new List<DiscardEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return result;

        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var item = container->GetInventorySlot(slotIndex);
            if (item == null || item->ItemId == 0)
                continue;

            var itemId = item->GetBaseItemId();
            if (itemId == 0)
                continue;

            var equipment = excel.IsEquipment(itemId);
            result.Add(new(
                new(type, slotIndex),
                itemId,
                excel.NameOf(itemId),
                item->Quantity,
                equipment,
                equipment ? excel.RequiredLevelOf(itemId) : 0,
                equipment && excel.IsCrafterGathererOnly(itemId),
                item->Stains[0] != 0 || item->Stains[1] != 0));
        }

        return result.OrderBy(x => x.Key.Slot).ToList();
    }

    public bool TryResolve(ItemLocationKey key, uint expectedItemId, out InventoryItem* item)
    {
        item = null;
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return false;

        var container = inventory->GetInventoryContainer(key.Container);
        if (container == null || !container->IsLoaded || key.Slot < 0 || key.Slot >= container->Size)
            return false;

        item = container->GetInventorySlot(key.Slot);
        return item != null && item->ItemId != 0 && item->GetBaseItemId() == expectedItemId;
    }

    public bool TryFindFirstEmptyBag(out ItemLocationKey key)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var type in BagContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                    {
                        key = new(type, i);
                        return true;
                    }
                }
            }
        }

        key = default;
        return false;
    }

    public int CountFreeBagSlots()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : checked((int)inventory->GetEmptySlotsInBag());
    }

    /// Equipment in a single container (e.g. one armoury category). Same shape as Scan().
    public List<PhysicalGearEntry> ScanContainer(InventoryType type)
    {
        var result = new List<PhysicalGearEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;

        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return result;

        for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
        {
            var item = container->GetInventorySlot(slotIndex);
            if (item == null || item->ItemId == 0)
                continue;

            var itemId = item->GetBaseItemId();
            if (itemId == 0 || !excel.IsEquipment(itemId))
                continue;

            result.Add(Describe(type, slotIndex, item, itemId));
        }

        return result
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Key.Slot)
            .ToList();
    }

    public int CountFreeSlotsIn(InventoryType type)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return 0;
        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded) return 0;

        var free = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0) free++;
        }
        return free;
    }

    public bool TryFindFirstEmptySlotIn(InventoryType type, out ItemLocationKey key)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container != null && container->IsLoaded)
            {
                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                    {
                        key = new(type, i);
                        return true;
                    }
                }
            }
        }

        key = default;
        return false;
    }

    private PhysicalGearEntry Describe(InventoryType type, int slotIndex, InventoryItem* item, uint itemId)
        => new(
            new(type, slotIndex),
            itemId,
            excel.NameOf(itemId),
            excel.RequiredLevelOf(itemId),
            excel.IsCrafterGathererOnly(itemId),
            item->Stains[0] != 0 || item->Stains[1] != 0,
            item->GlamourId != 0,
            HasMateria(item),
            item->Condition * 100 / 30000,   // 30000 = 100% durability
            MateriaCount(item));

    private static int MateriaCount(InventoryItem* item)
    {
        var n = 0;
        for (var i = 0; i < item->Materia.Length; i++)
            if (item->Materia[i] != 0)
                n++;
        return n;
    }

    private static bool HasMateria(InventoryItem* item)
    {
        for (var i = 0; i < item->Materia.Length; i++)
            if (item->Materia[i] != 0)
                return true;
        return false;
    }
}

public sealed unsafe class Stage1DresserToInventory
{
    private readonly ExcelIndex excel;
    private readonly GlamourStateCache glamourCache;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly System.Action saveConfig;
    private readonly List<DresserArmoireEntry> entries = [];
    private readonly HashSet<int> selected = [];
    private readonly List<(int Slot, uint ItemId)> queue = [];
    private int index;
    private long nextAt;

    public IReadOnlyList<DresserArmoireEntry> Entries => entries;
    public IReadOnlySet<int> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Restored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int Total => queue.Count;
    public int Done => index;
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage1DresserToInventory(ExcelIndex excel, GlamourStateCache glamourCache, IGameGui gameGui, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.glamourCache = glamourCache;
        this.gameGui = gameGui;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateOptions(bool includeDyed, bool includePlate)
    {
        config.Stage1IncludeDyed = includeDyed;
        config.Stage1IncludePlateRegistered = includePlate;
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;

        entries.Clear();
        selected.Clear();
        glamourCache.Observe();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다. 환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        if (!glamourCache.HasPlateData)
        {
            Status = "투영세트 등록 여부를 판정하려면 투영세트 편집 화면을 한 번 열어 캐시한 뒤 다시 검색해 주세요.";
            return 0;
        }

        for (var slot = 0; slot < manager->PrismBoxItemIds.Length; slot++)
        {
            var raw = manager->PrismBoxItemIds[slot];
            if (raw == 0) continue;

            var itemId = Normalize(raw);
            if (itemId == 0 || excel.IsOutfitSetToken(itemId) || !excel.TryGetCabinetRow(itemId, out _))
                continue;

            var dyed = manager->PrismBoxStain0Ids[slot] != 0 || manager->PrismBoxStain1Ids[slot] != 0;
            glamourCache.TryGetPlateState(itemId, out var usedOnPlate, out _);
            entries.Add(new(slot, itemId, excel.NameOf(itemId), dyed, usedOnPlate));
        }

        entries.Sort((a, b) =>
        {
            var c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            return c != 0 ? c : a.DresserSlot.CompareTo(b.DresserSlot);
        });

        Status = $"검색 완료: 추억의 보관함 대응 환상의 옷장 아이템 {entries.Count}개.";
        return entries.Count;
    }

    public void SetSelected(int slot, bool value)
    {
        if (value) selected.Add(slot);
        else selected.Remove(slot);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.DresserSlot);
    }

    public bool Start()
    {
        if (IsRunning) return false;
        if (!IsDresserOpen())
        {
            Status = "환상의 옷장을 연 상태에서 실행해 주세요.";
            return false;
        }

        queue.Clear();
        foreach (var entry in entries.Where(x => selected.Contains(x.DresserSlot)))
        {
            if (entry.IsDyed && !config.Stage1IncludeDyed)
                continue;
            if (entry.UsedOnPlate && !config.Stage1IncludePlateRegistered)
                continue;
            queue.Add((entry.DresserSlot, entry.ItemId));
        }

        if (queue.Count == 0)
        {
            Status = "현재 옵션 조건에서 옮길 체크 항목이 없습니다.";
            return false;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null || inventory->GetEmptySlotsInBag() == 0)
        {
            Status = "인벤토리 빈칸이 없습니다.";
            return false;
        }

        index = Restored = Skipped = Failed = 0;
        nextAt = 0;
        LastItemStatus = null;
        IsRunning = true;
        Status = $"체크한 항목 {queue.Count}개를 인벤토리로 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "1단계 작업을 중지했습니다.")
    {
        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;
        if (now < nextAt) return;

        if (index >= queue.Count)
        {
            IsRunning = false;
            var completed = $"완료: 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
            Scan();
            Status = completed;
            return;
        }

        if (!IsDresserOpen())
        {
            Stop("환상의 옷장이 닫혀 작업을 중지했습니다.");
            return;
        }

        var manager = MirageManager.Instance();
        var inventory = InventoryManager.Instance();
        if (manager == null || inventory == null || !manager->PrismBoxLoaded)
        {
            Stop("환상의 옷장 또는 인벤토리 데이터를 잃어 작업을 중지했습니다.");
            return;
        }

        if (inventory->GetEmptySlotsInBag() == 0)
        {
            Stop("인벤토리 빈칸이 없어 작업을 중지했습니다.");
            return;
        }

        var (slot, expectedItemId) = queue[index];
        if (slot < 0 || slot >= manager->PrismBoxItemIds.Length || Normalize(manager->PrismBoxItemIds[slot]) != expectedItemId)
        {
            Skipped++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 옷장 슬롯 내용이 바뀌어 건너뜁니다.";
        }
        else if (manager->RestorePrismBoxItem((uint)slot))
        {
            Restored++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 인벤토리 복원 요청 완료.";
        }
        else
        {
            Failed++;
            LastItemStatus = $"{excel.NameOf(expectedItemId)}: 게임이 복원 요청을 거절했습니다.";
        }

        index++;
        nextAt = now + 700;
        Status = $"진행 {index}/{queue.Count} · 복원 {Restored} · 제외 {Skipped} · 실패 {Failed}";
    }

    private bool IsDresserOpen()
    {
        var addon = gameGui.GetAddonByName("MiragePrismPrismBox");
        return addon != null && addon.IsVisible;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}

public sealed unsafe class Stage4Discard
{
    private readonly InventoryScanner scanner;
    private readonly GearFilter filter;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<PhysicalGearEntry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly HashSet<ItemLocationKey> matched = [];
    private readonly Queue<DiscardEntry> queue = new();

    private DiscardEntry? pending;
    private long pendingAt;
    private long nextAt;
    private DiscardMode mode;

    public IReadOnlyList<PhysicalGearEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public bool IsMatched(ItemLocationKey key) => matched.Contains(key);
    public int MatchedCount => matched.Count;
    public bool IsRunning { get; private set; }
    public bool IsBag4Mode => IsRunning && mode == DiscardMode.Inventory4All;
    public int Discarded { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int Total { get; private set; }
    public int Done => Discarded + Skipped + Failed;
    public string Status { get; private set; } = "파기 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage4Discard(InventoryScanner scanner, GearFilter filter, IGameGui gameGui, Configuration config, System.Action saveConfig)
    {
        this.scanner = scanner;
        this.filter = filter;
        this.gameGui = gameGui;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateSmartSettings(bool includeCrafterGatherer, int maxLevel)
    {
        config.Stage4SmartIncludeCrafterGatherer = includeCrafterGatherer;
        config.Stage4SmartMaxLevel = Math.Clamp(maxLevel, 1, 100);
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        entries.AddRange(scanner.Scan(includeBags: true, includeArmory: true));
        ApplyFilters();
        return entries.Count;
    }

    /// Re-evaluates the shared filter: matching rows are checked and sorted to the top. Discarding is
    /// irreversible, so with no filter nothing is checked automatically.
    public void ApplyFilters()
    {
        if (IsRunning) return;
        var anyFilter = filter.AnyEnabled;
        matched.Clear();
        selected.Clear();
        if (anyFilter)
        {
            foreach (var entry in entries)
            {
                if (!filter.Matches(entry.ItemId, entry.RequiredLevel)) continue;
                matched.Add(entry.Key);
                selected.Add(entry.Key);
            }
        }

        entries.Sort((a, b) =>
        {
            var ma = matched.Contains(a.Key);
            var mb = matched.Contains(b.Key);
            if (ma != mb) return ma ? -1 : 1;
            var c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            return c != 0 ? c : a.Key.Id.CompareTo(b.Key.Id);
        });

        var warning = filter.DresserWarning is { } w ? " " + w : string.Empty;
        Status = anyFilter
            ? $"장비함+가방 장비 {entries.Count}개 중 필터 일치 {matched.Count}개를 체크했습니다. 확인 후 파기하세요.{warning}"
            : $"검색 완료: 장비함+가방 장비 {entries.Count}개. 필터를 켜면 조건에 맞는 것만 체크됩니다.";
    }

    public void SetSelected(ItemLocationKey key, bool value)
    {
        if (value) selected.Add(key);
        else selected.Remove(key);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries)
            selected.Add(entry.Key);
    }

    public void SmartSelect()
    {
        selected.Clear();
        foreach (var entry in entries)
        {
            if (entry.RequiredLevel > config.Stage4SmartMaxLevel)
                continue;
            if (entry.IsCrafterGatherer && !config.Stage4SmartIncludeCrafterGatherer)
                continue;
            selected.Add(entry.Key);
        }

        Status = $"스마트 선택 완료: {selected.Count}개.";
    }

    public int PreviewInventory4Count()
        => scanner.ScanContainerAll(InventoryType.Inventory4).Count;

    public bool StartSelected()
    {
        if (IsRunning) return false;

        var chosen = entries
            .Where(x => selected.Contains(x.Key))
            .Select(ToDiscardEntry)
            .ToList();

        if (chosen.Count == 0)
        {
            Status = "파기할 아이템을 체크해 주세요.";
            return false;
        }

        PrepareQueue(chosen, DiscardMode.SelectedGear);
        Status = $"체크한 장비 {queue.Count}개를 순차 파기합니다.";
        return true;
    }

    public bool StartInventory4All()
    {
        if (IsRunning) return false;

        var chosen = scanner.ScanContainerAll(InventoryType.Inventory4);
        if (chosen.Count == 0)
        {
            Status = "4번 인벤토리가 비어 있습니다.";
            return false;
        }

        PrepareQueue(chosen, DiscardMode.Inventory4All);
        Status = $"4번 인벤토리의 모든 아이템 {queue.Count}개 슬롯을 순차 파기합니다.";
        return true;
    }

    public void Stop(string reason = "4단계 파기를 중지했습니다.")
    {
        IsRunning = false;
        pending = null;
        queue.Clear();
        mode = DiscardMode.None;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;

        if (pending is not null)
        {
            AutoConfirmDiscard(pending);
            if (!scanner.TryResolve(pending.Key, pending.ItemId, out _))
            {
                Discarded++;
                LastItemStatus = $"{pending.Name}: 파기 완료.";
                pending = null;
                nextAt = now + 500;
            }
            else if (now - pendingAt > 8000)
            {
                Failed++;
                LastItemStatus = $"{pending.Name}: 파기 완료를 확인하지 못해 다음 항목으로 넘어갑니다.";
                pending = null;
                nextAt = now + 500;
            }
            return;
        }

        if (now < nextAt) return;

        if (!queue.TryDequeue(out var entry))
        {
            IsRunning = false;
            var completed = $"완료: 파기 {Discarded} · 제외 {Skipped} · 실패 {Failed}";
            mode = DiscardMode.None;
            Scan();
            Status = completed;
            return;
        }

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Skipped++;
            LastItemStatus = $"{entry.Name}: 위치가 바뀌어 건너뜁니다.";
            nextAt = now + 250;
            return;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        var rc = inventory->DiscardItem(entry.Key.Container, (ushort)entry.Key.Slot);
        if (rc != 0)
        {
            Failed++;
            LastItemStatus = $"{entry.Name}: 파기 요청이 거절되었습니다. ({rc})";
            nextAt = now + 500;
            return;
        }

        pending = entry;
        pendingAt = now;
        LastItemStatus = $"{entry.Name}: 파기 요청 완료. 확인 창이 뜨면 자동으로 '예'를 선택합니다.";
        Status = $"파기 진행: 완료 {Discarded} · 제외 {Skipped} · 실패 {Failed} · 남음 {queue.Count + 1}";
    }

    private void PrepareQueue(IEnumerable<DiscardEntry> chosen, DiscardMode discardMode)
    {
        queue.Clear();
        foreach (var entry in chosen)
            queue.Enqueue(entry);

        Total = queue.Count;
        Discarded = Skipped = Failed = 0;
        pending = null;
        nextAt = 0;
        LastItemStatus = null;
        mode = discardMode;
        IsRunning = true;
    }

    private void AutoConfirmDiscard(DiscardEntry expected)
    {
        if (Environment.TickCount64 - pendingAt > 3000)
            return;

        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible)
                continue;

            var prompt = YesNoPrompt.Read(yesno, out _);
            if (string.IsNullOrWhiteSpace(prompt) ||
                !prompt.Contains(expected.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            YesNoPrompt.Yes(yesno);
            return;
        }
    }

    private static DiscardEntry ToDiscardEntry(PhysicalGearEntry entry)
        => new(
            entry.Key,
            entry.ItemId,
            entry.Name,
            1,
            true,
            entry.RequiredLevel,
            entry.IsCrafterGatherer,
            entry.IsDyed);

    private enum DiscardMode
    {
        None,
        SelectedGear,
        Inventory4All,
    }
}

