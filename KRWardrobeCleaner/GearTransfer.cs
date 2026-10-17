using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace KRWardrobeCleaner;

public enum TransferDirection { ArmoryToBag = 0, BagToArmory = 1 }
public enum FilterOp { And = 0, Or = 1 }

public sealed record ArmoryCategory(InventoryType Container, string Label);

/// One candidate row of the transfer list.
public sealed class TransferEntry
{
    public required PhysicalGearEntry Gear { get; init; }
    public required string JobText { get; init; }
    public required bool InDresser { get; init; }
    public required bool InGearset { get; init; }
    public bool Matched { get; set; }
}

/// <summary>
/// 3. 장비함 ↔ 가방: moves gear between the armoury chest and the bags in either direction.
/// Candidates can be auto-checked (and sorted to the top) by job / level / dresser-ownership filters
/// that are combined left to right with AND / OR.
/// </summary>
public sealed unsafe class GearTransfer
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

    private const int SnapshotIntervalMs = 1000;

    private readonly ExcelIndex excel;
    private readonly InventoryScanner scanner;
    private readonly OutfitCatalog outfits;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<TransferEntry> entries = [];
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<PhysicalGearEntry> queue = new();
    private HashSet<uint>? dresserSnapshot;
    private long nextSnapshotAt;
    private long nextAt;

    public IReadOnlyList<TransferEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public bool IsRunning { get; private set; }
    public int Moved { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public string Status { get; private set; } = "방향과 필터를 정한 뒤 검색해 주세요.";
    public string? LastItemStatus { get; private set; }
    public int MatchedCount => entries.Count(x => x.Matched);
    public int ExcludedByGearset => entries.Count(x => x.InGearset && ExcludesGearset);

    public bool HasDresserData => dresserSnapshot != null;
    public DateTimeOffset? DresserSnapshotAt { get; private set; }

    public GearTransfer(ExcelIndex excel, InventoryScanner scanner, OutfitCatalog outfits, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.scanner = scanner;
        this.outfits = outfits;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public TransferDirection Direction => config.TransferDirection == 1 ? TransferDirection.BagToArmory : TransferDirection.ArmoryToBag;
    public bool ExcludesGearset => Direction == TransferDirection.ArmoryToBag && config.TransferExcludeGearset;
    public int SourceCategory => config.TransferSourceCategory < 0 || config.TransferSourceCategory >= Categories.Length ? -1 : config.TransferSourceCategory;
    public string SourceLabel => Direction == TransferDirection.BagToArmory ? "가방 1~4" : SourceCategory < 0 ? "장비함 전체" : Categories[SourceCategory].Label;
    public string DestinationLabel => Direction == TransferDirection.BagToArmory ? "장비함(부위별 자동)" : config.TransferToBag4Only ? "4번 가방" : "가방 빈칸";
    public int FreeDestinationSlots => Direction == TransferDirection.BagToArmory ? -1 : config.TransferToBag4Only ? scanner.CountFreeSlotsIn(InventoryType.Inventory4) : scanner.CountFreeBagSlots();
    public IReadOnlyList<JobInfo> Jobs => excel.Jobs;

    public void Save() => saveConfig();

    /// Keeps a copy of the dresser contents whenever the game has them loaded, so the dresser filter
    /// still works after the dresser window is closed.
    public void Observe()
    {
        var now = Environment.TickCount64;
        if (now < nextSnapshotAt) return;
        nextSnapshotAt = now + SnapshotIntervalMs;

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded) return;

        dresserSnapshot = DresserContents.StoredItemIds(manager, outfits);
        DresserSnapshotAt = DateTimeOffset.Now;
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        Observe();

        var gearsetIds = CollectGearsetItemIds();
        IEnumerable<PhysicalGearEntry> source = Direction switch
        {
            TransferDirection.BagToArmory => scanner.Scan(includeBags: true, includeArmory: false),
            _ when SourceCategory >= 0 => scanner.ScanContainer(Categories[SourceCategory].Container),
            _ => scanner.Scan(includeBags: false, includeArmory: true),
        };

        foreach (var gear in source)
        {
            entries.Add(new TransferEntry
            {
                Gear = gear,
                JobText = excel.JobCategoryNameOf(gear.ItemId),
                InDresser = dresserSnapshot?.Contains(gear.ItemId) ?? false,
                InGearset = gearsetIds.Contains(gear.ItemId),
            });
        }

        ApplyFilters();
        return entries.Count;
    }

    /// Re-evaluates the filters, checks matching rows and sorts them to the top.
    public void ApplyFilters()
    {
        var anyFilter = config.FilterJobEnabled || config.FilterLevelEnabled || config.FilterDresserEnabled;
        var jobSet = BuildActiveJobSet();

        selected.Clear();
        foreach (var entry in entries)
        {
            entry.Matched = anyFilter && Evaluate(entry, jobSet);
            // No filter: everything eligible is checked (one-click move); with filters only the matches.
            if ((anyFilter ? entry.Matched : true) && !(entry.InGearset && ExcludesGearset))
                selected.Add(entry.Gear.Key);
        }

        entries.Sort((a, b) =>
        {
            if (a.Matched != b.Matched) return a.Matched ? -1 : 1;
            var c = StringComparer.CurrentCulture.Compare(a.Gear.Name, b.Gear.Name);
            return c != 0 ? c : a.Gear.Key.Id.CompareTo(b.Gear.Key.Id);
        });

        var dresserNote = config.FilterDresserEnabled && dresserSnapshot == null
            ? " 옷장 데이터가 없어 옷장 필터는 '미보유'로 판정됩니다(환상의 옷장을 한 번 열어 주세요)."
            : string.Empty;
        Status = anyFilter
            ? $"{SourceLabel}: 장비 {entries.Count}개 중 필터 일치 {MatchedCount}개, {selected.Count}개 체크됨 (장비세트 제외 {ExcludedByGearset}개).{dresserNote}"
            : $"{SourceLabel}: 장비 {entries.Count}개. 필터를 켜면 자동으로 체크됩니다 (장비세트 제외 {ExcludedByGearset}개).";
    }

    private HashSet<string> BuildActiveJobSet()
    {
        var set = new HashSet<string>(config.FilterJobs, StringComparer.Ordinal);
        foreach (var job in excel.Jobs)
            if (set.Contains(job.Abbreviation) && !string.IsNullOrEmpty(job.ParentAbbreviation))
                set.Add(job.ParentAbbreviation);   // a job's gear includes its base class' gear
        return set;
    }

    private bool Evaluate(TransferEntry entry, HashSet<string> jobSet)
    {
        bool? result = null;

        if (config.FilterJobEnabled)
            Combine(ref result, FilterOp.And, excel.JobsOf(entry.Gear.ItemId).Overlaps(jobSet));

        if (config.FilterLevelEnabled)
        {
            var level = entry.Gear.RequiredLevel;
            Combine(ref result, (FilterOp)config.FilterLevelOp, level >= config.FilterLevelMin && level <= config.FilterLevelMax);
        }

        if (config.FilterDresserEnabled)
            Combine(ref result, (FilterOp)config.FilterDresserOp, entry.InDresser == config.FilterDresserOwned);

        return result ?? false;
    }

    private static void Combine(ref bool? result, FilterOp op, bool value)
    {
        if (result is null) result = value;
        else result = op == FilterOp.Or ? result.Value || value : result.Value && value;
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
            if (!(entry.InGearset && ExcludesGearset))
                selected.Add(entry.Gear.Key);
    }

    public bool Start()
    {
        if (IsRunning) return false;

        var chosen = entries
            .Where(x => selected.Contains(x.Gear.Key))
            .Where(x => !(x.InGearset && ExcludesGearset))
            .Select(x => x.Gear)
            .ToList();

        if (chosen.Count == 0)
        {
            Status = "옮길 장비를 체크해 주세요.";
            return false;
        }

        LastItemStatus = null;
        if (Direction == TransferDirection.ArmoryToBag)
        {
            var free = FreeDestinationSlots;
            if (free == 0)
            {
                Status = $"{DestinationLabel}에 빈칸이 없습니다.";
                return false;
            }

            if (chosen.Count > free)
            {
                LastItemStatus = $"{DestinationLabel} 빈칸이 {free}칸뿐이라 {free}개만 옮깁니다 (나머지 {chosen.Count - free}개는 그대로 둡니다).";
                chosen = chosen.Take(free).ToList();
            }
        }
        else
        {
            // Armoury capacity is per slot type; keep only what fits in each container.
            var freeByContainer = new Dictionary<InventoryType, int>();
            var kept = new List<PhysicalGearEntry>();
            var dropped = 0;
            foreach (var gear in chosen)
            {
                var container = excel.ArmoryContainerOf(gear.ItemId);
                if (container is null) { dropped++; continue; }
                if (!freeByContainer.TryGetValue(container.Value, out var free))
                    freeByContainer[container.Value] = free = scanner.CountFreeSlotsIn(container.Value);
                if (free <= 0) { dropped++; continue; }
                freeByContainer[container.Value] = free - 1;
                kept.Add(gear);
            }

            if (kept.Count == 0)
            {
                Status = "장비함의 해당 부위 칸이 모두 차 있어 옮길 수 없습니다.";
                return false;
            }

            if (dropped > 0)
                LastItemStatus = $"장비함 칸이 부족한 {dropped}개는 가방에 그대로 둡니다.";
            chosen = kept;
        }

        queue.Clear();
        foreach (var gear in chosen) queue.Enqueue(gear);
        Moved = Skipped = Failed = 0;
        nextAt = 0;
        IsRunning = true;
        Status = $"{SourceLabel} → {DestinationLabel}: 장비 {queue.Count}개를 옮깁니다.";
        return true;
    }

    public void Stop(string reason = "장비함 ↔ 가방 작업을 중지했습니다.")
    {
        IsRunning = false;
        Status = reason;
    }

    public void Tick()
    {
        Observe();
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
            LastItemStatus = $"{entry.Name}: 위치가 바뀌어 건너뜁니다.";
        }
        else if (!TryFindDestination(entry, out var destination, out var why))
        {
            if (Direction == TransferDirection.ArmoryToBag)
            {
                Stop($"{DestinationLabel}이(가) 가득 차 중지했습니다. 남은 장비 {queue.Count + 1}개는 그대로 있습니다.");
                return;
            }

            Skipped++;
            LastItemStatus = $"{entry.Name}: {why}";
        }
        else
        {
            var rc = inventory->MoveItemSlot(entry.Key.Container, (ushort)entry.Key.Slot, destination.Container, (ushort)destination.Slot, true);
            if (rc == 0)
            {
                Moved++;
                LastItemStatus = $"{entry.Name}: {Describe(destination)}으로 이동 요청 완료.";
            }
            else
            {
                Failed++;
                LastItemStatus = $"{entry.Name}: 이동 실패 ({rc}).";
            }

            if (config.DebugLog)
                log.Information("[KWC] transfer {Dir} {Name} #{Id} {From} -> {To}: rc={Rc}", Direction, entry.Name, entry.ItemId, entry.Key.Id, destination.Id, rc);
        }

        nextAt = now + 500;
        Status = $"진행: 이동 {Moved} · 제외 {Skipped} · 실패 {Failed} · 남음 {queue.Count}";
    }

    private bool TryFindDestination(PhysicalGearEntry entry, out ItemLocationKey destination, out string why)
    {
        why = string.Empty;
        if (Direction == TransferDirection.ArmoryToBag)
        {
            var ok = config.TransferToBag4Only
                ? scanner.TryFindFirstEmptySlotIn(InventoryType.Inventory4, out destination)
                : scanner.TryFindFirstEmptyBag(out destination);
            if (!ok) why = "빈칸 없음";
            return ok;
        }

        var container = excel.ArmoryContainerOf(entry.ItemId);
        if (container is null)
        {
            destination = default;
            why = "장비함 부위를 알 수 없어 건너뜁니다.";
            return false;
        }

        if (!scanner.TryFindFirstEmptySlotIn(container.Value, out destination))
        {
            why = $"장비함 {CategoryLabel(container.Value)} 칸이 가득 차 건너뜁니다.";
            return false;
        }

        return true;
    }

    private static string CategoryLabel(InventoryType container)
        => Categories.FirstOrDefault(x => x.Container == container)?.Label ?? container.ToString();

    private static string Describe(ItemLocationKey key)
        => key.Container switch
        {
            InventoryType.Inventory1 => $"1번 가방 {key.Slot + 1}칸",
            InventoryType.Inventory2 => $"2번 가방 {key.Slot + 1}칸",
            InventoryType.Inventory3 => $"3번 가방 {key.Slot + 1}칸",
            InventoryType.Inventory4 => $"4번 가방 {key.Slot + 1}칸",
            _ => $"장비함 {CategoryLabel(key.Container)} {key.Slot + 1}칸",
        };

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
