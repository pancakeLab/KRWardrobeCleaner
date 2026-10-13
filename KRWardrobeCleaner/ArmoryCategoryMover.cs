using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace KRWardrobeCleaner;

public sealed record ArmoryCategory(InventoryType Container, string Label);

/// <summary>
/// 장비함 → 4번 가방: moves the checked items of one armoury category into empty slots of
/// Inventory4 only. Items referenced by any gear set are excluded automatically.
/// </summary>
public sealed unsafe class ArmoryCategoryMover
{
    public static readonly ArmoryCategory[] Categories =
    [
        new(InventoryType.ArmoryMainHand, "주무기"),
        new(InventoryType.ArmoryOffHand, "보조무기"),
        new(InventoryType.ArmoryHead, "머리"),
        new(InventoryType.ArmoryBody, "상의"),
        new(InventoryType.ArmoryHands, "손"),
        new(InventoryType.ArmoryLegs, "하의"),
        new(InventoryType.ArmoryFeets, "발"),
        new(InventoryType.ArmoryEar, "귀걸이"),
        new(InventoryType.ArmoryNeck, "목걸이"),
        new(InventoryType.ArmoryWrist, "팔찌"),
        new(InventoryType.ArmoryRings, "반지"),
        new(InventoryType.ArmorySoulCrystal, "소울 크리스탈"),
    ];

    public sealed record Entry(PhysicalGearEntry Gear, bool InGearset);

    private readonly InventoryScanner scanner;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<Entry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<PhysicalGearEntry> queue = new();
    private long nextAt;

    public IReadOnlyList<Entry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public int ExcludedByGearset { get; private set; }
    public bool IsRunning { get; private set; }
    public int Moved { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "카테고리를 고르고 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public ArmoryCategoryMover(InventoryScanner scanner, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.scanner = scanner;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public int CategoryIndex => Math.Clamp(config.ArmoryMoveCategoryIndex, 0, Categories.Length - 1);
    public ArmoryCategory Category => Categories[CategoryIndex];
    public int FreeBag4Slots => scanner.CountFreeSlotsIn(InventoryType.Inventory4);

    public void SetCategory(int idx)
    {
        if (IsRunning) return;
        config.ArmoryMoveCategoryIndex = Math.Clamp(idx, 0, Categories.Length - 1);
        saveConfig();
        entries.Clear();
        selected.Clear();
        Status = $"{Category.Label}: 검색해 주세요.";
    }

    /// Scans the chosen category and checks every item that is not part of a gear set.
    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        ExcludedByGearset = 0;

        var gearsetItems = CollectGearsetItemIds();
        foreach (var gear in scanner.ScanContainer(Category.Container))
        {
            var inGearset = gearsetItems.Contains(gear.ItemId);
            entries.Add(new(gear, inGearset));
            if (inGearset) ExcludedByGearset++;
            else selected.Add(gear.Key);
        }

        Status = $"{Category.Label}: 장비 {entries.Count}개 · 장비세트 등록 {ExcludedByGearset}개 제외 · {selected.Count}개 체크됨 · 4번 가방 빈칸 {FreeBag4Slots}칸.";
        return entries.Count;
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
            if (!entry.InGearset)
                selected.Add(entry.Gear.Key);
    }

    public bool Start()
    {
        if (IsRunning) return false;
        var chosen = entries
            .Where(x => !x.InGearset && selected.Contains(x.Gear.Key))
            .Select(x => x.Gear)
            .ToList();

        if (chosen.Count == 0)
        {
            Status = "옮길 장비를 체크해 주세요 (장비세트에 등록된 장비는 옮기지 않습니다).";
            return false;
        }

        var free = FreeBag4Slots;
        if (free == 0)
        {
            Status = "4번 가방에 빈칸이 없습니다.";
            return false;
        }

        LastItemStatus = null;
        if (chosen.Count > free)
        {
            LastItemStatus = $"4번 가방 빈칸이 {free}칸뿐이라 {free}개만 옮깁니다 (나머지 {chosen.Count - free}개는 장비함에 남김).";
            chosen = chosen.Take(free).ToList();
        }

        queue.Clear();
        foreach (var entry in chosen) queue.Enqueue(entry);
        Moved = Skipped = Failed = 0;
        nextAt = 0;
        IsRunning = true;
        Status = $"{Category.Label} 장비 {queue.Count}개를 4번 가방으로 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "장비함 → 4번 가방 작업을 중지했습니다.")
    {
        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;
        if (now < nextAt) return;

        if (!queue.TryDequeue(out var entry))
        {
            IsRunning = false;
            var completed = $"완료: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed}";
            Scan();
            Status = completed;
            return;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Skipped++;
            LastItemStatus = $"{entry.Name}: 장비함 위치가 바뀌어 건너뜁니다.";
        }
        else if (!scanner.TryFindFirstEmptySlotIn(InventoryType.Inventory4, out var destination))
        {
            Stop($"4번 가방이 가득 차 중지했습니다. 남은 장비 {queue.Count + 1}개는 장비함에 그대로 있습니다.");
            return;
        }
        else
        {
            var rc = inventory->MoveItemSlot(entry.Key.Container, (ushort)entry.Key.Slot, destination.Container, (ushort)destination.Slot, true);
            if (rc == 0)
            {
                Moved++;
                LastItemStatus = $"{entry.Name}: 4번 가방 {destination.Slot + 1}번 칸으로 이동 요청 완료.";
            }
            else
            {
                Failed++;
                LastItemStatus = $"{entry.Name}: 이동 실패 ({rc}).";
            }

            if (config.DebugLog)
                log.Information("[KWC] armory move {Name} #{Id} {From} -> {To}: rc={Rc}", entry.Name, entry.ItemId, entry.Key.Id, destination.Id, rc);
        }

        nextAt = now + 500;
        Status = $"진행: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed} · 남음 {queue.Count}";
    }

    /// Base item ids referenced by any existing gear set. Matching by id (not by physical slot) is
    /// conservative: if two identical items exist and one is in a gear set, both are left alone.
    private static HashSet<uint> CollectGearsetItemIds()
    {
        var result = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return result;

        for (var i = 0; i < module->Entries.Length; i++)
        {
            var entry = module->Entries[i];
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            for (var j = 0; j < entry.Items.Length; j++)
            {
                var id = Normalize(entry.Items[j].ItemId);
                if (id != 0) result.Add(id);
            }
        }

        return result;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}
