using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KRWardrobeCleaner;

/// One dresser item that can be taken out: a standalone item (SetSlot = -1) or one piece of an outfit set.
public sealed record DresserPullEntry(
    int DresserSlot,
    int SetSlot,
    uint ItemId,
    string Name,
    bool IsDyed,
    string? SetName,
    int RequiredLevel,
    string JobText)
{
    public string Id => $"{DresserSlot}:{SetSlot}";
}

/// <summary>
/// 1. 옷장 → 가방·장비함 (필터 모드): lists everything in the glamour dresser, including pieces inside outfit
/// sets, checks what the shared filter matches, restores the checked items to the bags and then (optionally)
/// closes the dresser and moves them into the armoury. Typical use: pull a job's level 50~80 set gear to
/// level with, then store it again as sets from tab 2.
/// </summary>
public sealed unsafe class DresserPull
{
    private const int RestoreIntervalMs = 700;
    private const int MoveIntervalMs = 450;
    private const int SettleAfterRestoreMs = 1200;   // let the last restored item arrive in the bag
    private const int EventEndTimeoutMs = 5000;      // how long we wait for the dresser "event" to end after closing

    private enum Phase { None, Restore, Settle, CloseDresser, WaitEvent, Move }

    private readonly ExcelIndex excel;
    private readonly OutfitCatalog outfits;
    private readonly InventoryScanner scanner;
    private readonly GearFilter filter;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<DresserPullEntry> entries = [];
    private readonly HashSet<string> selected = [];
    private readonly HashSet<string> matched = [];
    private readonly List<DresserPullEntry> queue = [];
    private readonly Dictionary<uint, int> restoredCounts = [];
    private readonly HashSet<ItemLocationKey> bagKeysBefore = [];
    private readonly Queue<PhysicalGearEntry> moveQueue = new();

    private Phase phase;
    private int index;
    private long nextAt;
    private long phaseAt;

    public IReadOnlyList<DresserPullEntry> Entries => entries;
    public IReadOnlySet<string> Selected => selected;
    public bool IsMatched(string id) => matched.Contains(id);
    public int MatchedCount => matched.Count;
    public bool IsRunning { get; private set; }
    public int Restored { get; private set; }
    public int Moved { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int Total { get; private set; }
    public int Done => phase is Phase.Restore ? index : phase == Phase.None ? Total : Total - moveQueue.Count;
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public DresserPull(ExcelIndex excel, OutfitCatalog outfits, InventoryScanner scanner, GearFilter filter, IGameGui gameGui,
        ICondition condition, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.outfits = outfits;
        this.scanner = scanner;
        this.filter = filter;
        this.gameGui = gameGui;
        this.condition = condition;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void SetPullToArmory(bool value)
    {
        config.PullToArmory = value;
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        matched.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다. 환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        for (var slot = 0; slot < manager->PrismBoxItemIds.Length; slot++)
        {
            var raw = manager->PrismBoxItemIds[slot];
            if (raw == 0) continue;
            var itemId = Normalize(raw);

            if (outfits.TryGet(itemId, out var set))
            {
                for (var s = 0; s < set.ItemIds.Count; s++)
                {
                    var pieceId = set.ItemIds[s];
                    if (pieceId == 0 || !manager->IsSetSlotUnlocked((uint)slot, s)) continue;
                    entries.Add(new DresserPullEntry(slot, s, pieceId, excel.NameOf(pieceId), false, set.Name,
                        excel.RequiredLevelOf(pieceId), excel.JobCategoryNameOf(pieceId)));
                }
                continue;
            }

            var dyed = manager->PrismBoxStain0Ids[slot] != 0 || manager->PrismBoxStain1Ids[slot] != 0;
            entries.Add(new DresserPullEntry(slot, -1, itemId, excel.NameOf(itemId), dyed, null,
                excel.RequiredLevelOf(itemId), excel.JobCategoryNameOf(itemId)));
        }

        ApplyFilters();
        return entries.Count;
    }

    /// Re-evaluates the shared filter: matching rows are checked and sorted to the top. With no filter nothing
    /// is checked automatically (the dresser holds hundreds of items).
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
                matched.Add(entry.Id);
                selected.Add(entry.Id);
            }
        }

        entries.Sort((a, b) =>
        {
            var ma = matched.Contains(a.Id);
            var mb = matched.Contains(b.Id);
            if (ma != mb) return ma ? -1 : 1;
            var c = StringComparer.CurrentCulture.Compare(a.SetName ?? string.Empty, b.SetName ?? string.Empty);
            if (c != 0) return c;
            c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        if (entries.Count == 0)
        {
            Status = "환상의 옷장이 비어 있거나 아직 읽지 못했습니다.";
            return;
        }

        var sets = entries.Count(e => e.SetSlot >= 0);
        var warning = filter.DresserWarning is { } w ? " " + w : string.Empty;
        Status = anyFilter
            ? $"옷장 아이템 {entries.Count}개(세트 조각 {sets}개) 중 필터 일치 {matched.Count}개를 체크했습니다.{warning}"
            : $"옷장 아이템 {entries.Count}개(세트 조각 {sets}개). 필터를 켜면 조건에 맞는 것만 체크됩니다.";
    }

    public void SetSelected(string id, bool value)
    {
        if (value) selected.Add(id);
        else selected.Remove(id);
    }

    public void SelectAll(bool value)
    {
        selected.Clear();
        if (!value) return;
        foreach (var entry in entries) selected.Add(entry.Id);
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
        queue.AddRange(entries.Where(e => selected.Contains(e.Id)));
        if (queue.Count == 0)
        {
            Status = "꺼낼 아이템을 체크해 주세요.";
            return false;
        }

        var inventory = InventoryManager.Instance();
        var free = inventory == null ? 0 : (int)inventory->GetEmptySlotsInBag();
        if (free == 0)
        {
            Status = "가방 빈칸이 없습니다. 옷장에서 꺼낸 아이템은 먼저 가방으로 들어옵니다.";
            return false;
        }

        LastItemStatus = null;
        if (free < queue.Count)
        {
            queue.RemoveRange(free, queue.Count - free);
            LastItemStatus = $"가방 빈칸이 {free}칸뿐이라 {free}개만 꺼냅니다. 나머지는 다시 실행해 주세요.";
        }

        bagKeysBefore.Clear();
        foreach (var gear in scanner.Scan(includeBags: true, includeArmory: false))
            bagKeysBefore.Add(gear.Key);
        restoredCounts.Clear();
        moveQueue.Clear();

        index = Restored = Moved = Skipped = Failed = 0;
        Total = queue.Count;
        nextAt = 0;
        phase = Phase.Restore;
        IsRunning = true;
        Status = $"체크한 아이템 {queue.Count}개를 옷장에서 꺼냅니다." + (config.PullToArmory ? " 꺼낸 뒤 옷장을 닫고 장비함으로 옮깁니다." : string.Empty);
        return true;
    }

    public void Stop(string reason = "옷장 꺼내기를 중지했습니다.")
    {
        IsRunning = false;
        phase = Phase.None;
        moveQueue.Clear();
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        var now = Environment.TickCount64;
        if (now < nextAt) return;

        switch (phase)
        {
            case Phase.Restore:
                TickRestore(now);
                return;

            case Phase.Settle:
                if (now - phaseAt < SettleAfterRestoreMs) return;
                if (!config.PullToArmory)
                {
                    Finish();
                    return;
                }
                CloseDresser();
                phase = Phase.WaitEvent;
                phaseAt = now;
                Status = "옷장을 닫고 장비함으로 옮길 준비를 합니다…";
                return;

            case Phase.WaitEvent:
            {
                var occupied = condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied];
                if (occupied && now - phaseAt < EventEndTimeoutMs) return;
                if (occupied)
                {
                    LastItemStatus = "옷장이 닫히지 않아 장비함 이동을 건너뜁니다. 옷장을 닫은 뒤 3번 탭(가방 → 장비함)으로 옮겨 주세요.";
                    Finish();
                    return;
                }

                BuildMoveQueue();
                if (moveQueue.Count == 0)
                {
                    LastItemStatus = "가방에서 꺼낸 아이템을 찾지 못해 장비함 이동을 건너뜁니다.";
                    Finish();
                    return;
                }

                Total = moveQueue.Count;
                phase = Phase.Move;
                Status = $"꺼낸 아이템 {moveQueue.Count}개를 장비함으로 옮깁니다.";
                return;
            }

            case Phase.Move:
                TickMove(now);
                return;
        }
    }

    private void TickRestore(long now)
    {
        if (index >= queue.Count)
        {
            phase = Phase.Settle;
            phaseAt = now;
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
            Stop("가방 빈칸이 없어 작업을 중지했습니다.");
            return;
        }

        var entry = queue[index];
        var slot = entry.DresserSlot;
        var expected = entry.SetSlot >= 0 ? (outfits.TryGet(Normalize(manager->PrismBoxItemIds[slot]), out var set) ? set.RowId : 0) : entry.ItemId;
        var current = Normalize(manager->PrismBoxItemIds[slot]);
        bool ok;
        string result;
        if (slot < 0 || slot >= manager->PrismBoxItemIds.Length || current != expected ||
            (entry.SetSlot >= 0 && !manager->IsSetSlotUnlocked((uint)slot, entry.SetSlot)))
        {
            Skipped++;
            result = "옷장 슬롯 내용이 바뀌어 건너뜁니다.";
        }
        else
        {
            try
            {
                if (entry.SetSlot < 0)
                {
                    ok = manager->RestorePrismBoxItem((uint)slot);
                }
                else
                {
                    // 11 bits, one per MirageStoreSetItem column; set only the piece we want out.
                    var bits = stackalloc byte[2];
                    bits[0] = 0;
                    bits[1] = 0;
                    bits[entry.SetSlot / 8] |= (byte)(1 << (entry.SetSlot % 8));
                    ok = manager->RestorePrismBoxSetItem((uint)slot, bits);
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[KWC] pull: restore call failed for {Name} slot {Slot}/{SetSlot}", entry.Name, slot, entry.SetSlot);
                ok = false;
            }

            if (ok)
            {
                Restored++;
                restoredCounts[entry.ItemId] = restoredCounts.GetValueOrDefault(entry.ItemId) + 1;
                result = entry.SetSlot >= 0 ? $"세트 '{entry.SetName}'에서 꺼내기 요청 완료." : "가방으로 꺼내기 요청 완료.";
            }
            else
            {
                Failed++;
                result = "게임이 꺼내기 요청을 거절했습니다 (고유 아이템 중복 소지 또는 빈칸 부족).";
            }
        }

        LastItemStatus = $"{entry.Name}: {result}";
        if (config.DebugLog)
            log.Information("[KWC] pull restore slot {Slot} set-slot {SetSlot} {Name} #{Id}: {Result}", slot, entry.SetSlot, entry.Name, entry.ItemId, result);

        index++;
        nextAt = now + RestoreIntervalMs;
        Status = $"꺼내는 중 {index}/{queue.Count} · 꺼냄 {Restored} · 제외 {Skipped} · 실패 {Failed}";
    }

    /// Bag items that were not there before the run and whose id we restored: those are the ones to move.
    private void BuildMoveQueue()
    {
        var counts = new Dictionary<uint, int>(restoredCounts);
        foreach (var gear in scanner.Scan(includeBags: true, includeArmory: false))
        {
            if (bagKeysBefore.Contains(gear.Key)) continue;
            if (!counts.TryGetValue(gear.ItemId, out var left) || left <= 0) continue;
            counts[gear.ItemId] = left - 1;
            moveQueue.Enqueue(gear);
        }
    }

    private void TickMove(long now)
    {
        if (!moveQueue.TryDequeue(out var gear))
        {
            Finish();
            return;
        }

        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Stop("인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        if (!scanner.TryResolve(gear.Key, gear.ItemId, out _))
        {
            Skipped++;
            LastItemStatus = $"{gear.Name}: 위치가 바뀌어 건너뜁니다.";
        }
        else
        {
            var container = excel.ArmoryContainerOf(gear.ItemId);
            if (container is null || !scanner.TryFindFirstEmptySlotIn(container.Value, out var dest))
            {
                Skipped++;
                LastItemStatus = container is null
                    ? $"{gear.Name}: 장비함 부위를 알 수 없어 가방에 둡니다."
                    : $"{gear.Name}: 장비함 {GearTransfer.CategoryLabel(container.Value)} 칸이 가득 차 가방에 둡니다.";
            }
            else
            {
                var rc = inventory->MoveItemSlot(gear.Key.Container, (ushort)gear.Key.Slot, dest.Container, (ushort)dest.Slot, true);
                if (rc == 0)
                {
                    Moved++;
                    LastItemStatus = $"{gear.Name}: 장비함 {GearTransfer.CategoryLabel(dest.Container)}(으)로 이동 요청 완료.";
                }
                else
                {
                    Failed++;
                    LastItemStatus = $"{gear.Name}: 장비함 이동 실패 ({rc}).";
                }

                if (config.DebugLog)
                    log.Information("[KWC] pull move {Name} #{Id} {From} -> {To}: rc={Rc}", gear.Name, gear.ItemId, gear.Key.Id, dest.Id, rc);
            }
        }

        nextAt = now + MoveIntervalMs;
        Status = $"장비함으로 옮기는 중 · 이동 {Moved} · 제외 {Skipped} · 실패 {Failed} · 남음 {moveQueue.Count}";
    }

    private void Finish()
    {
        IsRunning = false;
        phase = Phase.None;
        var completed = config.PullToArmory
            ? $"완료: 꺼냄 {Restored} · 장비함 이동 {Moved} · 제외 {Skipped} · 실패 {Failed}"
            : $"완료: 꺼냄 {Restored} · 제외 {Skipped} · 실패 {Failed}";
        var last = LastItemStatus;
        if (IsDresserOpen()) Scan();
        else entries.Clear();
        Status = completed + (IsDresserOpen() ? string.Empty : " (옷장을 다시 열면 목록이 갱신됩니다.)");
        LastItemStatus = last;
    }

    private void CloseDresser()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("MiragePrismPrismBox").Address;
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    private bool IsDresserOpen()
    {
        var addon = gameGui.GetAddonByName("MiragePrismPrismBox");
        return addon != null && addon.IsVisible;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}
