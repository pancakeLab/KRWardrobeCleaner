using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

/// One standalone dresser slot that holds an item which is stored more than once.
public sealed record DresserDuplicateEntry(
    int DresserSlot,
    uint ItemId,
    string Name,
    bool IsDyed,
    int GroupSize,
    bool AlsoInSet,
    bool IsKeep);

/// <summary>
/// 환상의 옷장 중복 정리: finds items stored more than once (standalone slots, plus standalone
/// copies of a piece that an outfit set already contains) and restores the checked ones to the bags.
/// </summary>
public sealed unsafe class DresserDuplicateCleaner
{
    private readonly ExcelIndex excel;
    private readonly OutfitCatalog outfits;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<DresserDuplicateEntry> entries = [];
    private readonly HashSet<int> selected = [];
    private readonly List<(int Slot, uint ItemId, string Name)> queue = [];
    private readonly RestoreGate gate = new();
    private int index;
    private long nextAt;

    public IReadOnlyList<DresserDuplicateEntry> Entries => entries;
    public IReadOnlySet<int> Selected => selected;
    public int DuplicateGroups { get; private set; }
    public bool IsRunning { get; private set; }
    public int Restored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int Total => queue.Count;
    public int Done => index;
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public DresserDuplicateCleaner(ExcelIndex excel, OutfitCatalog outfits, IGameGui gameGui, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.outfits = outfits;
        this.gameGui = gameGui;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateKeepDyedFirst(bool value)
    {
        config.DuplicateKeepDyedFirst = value;
        saveConfig();
    }

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        DuplicateGroups = 0;

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다. 환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        var inSets = new HashSet<uint>();
        var groups = new Dictionary<uint, List<(int Slot, bool Dyed)>>();

        for (var slot = 0; slot < manager->PrismBoxItemIds.Length; slot++)
        {
            var raw = manager->PrismBoxItemIds[slot];
            if (raw == 0) continue;

            var itemId = Normalize(raw);
            if (outfits.TryGet(itemId, out var set))
            {
                // Pieces held inside an outfit set count as one stored copy of that piece.
                for (var s = 0; s < set.ItemIds.Count; s++)
                    if (set.ItemIds[s] != 0 && manager->IsSetSlotUnlocked((uint)slot, s))
                        inSets.Add(set.ItemIds[s]);
                continue;
            }

            var dyed = manager->PrismBoxStain0Ids[slot] != 0 || manager->PrismBoxStain1Ids[slot] != 0;
            if (!groups.TryGetValue(itemId, out var list))
                groups[itemId] = list = [];
            list.Add((slot, dyed));
        }

        foreach (var (itemId, list) in groups)
        {
            var alsoInSet = inSets.Contains(itemId);
            if (list.Count < 2 && !alsoInSet)
                continue;

            DuplicateGroups++;
            var name = excel.NameOf(itemId);
            var ordered = list
                .OrderByDescending(x => config.DuplicateKeepDyedFirst && x.Dyed)
                .ThenBy(x => x.Slot)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                // The copy inside an outfit set is the one we keep; every standalone copy is surplus.
                var keep = !alsoInSet && i == 0;
                entries.Add(new(ordered[i].Slot, itemId, name, ordered[i].Dyed, list.Count, alsoInSet, keep));
            }

            if (config.DebugLog)
                log.Information("[KWC] dup {Name} #{Id}: standalone slots=[{Slots}] inSet={InSet}",
                    name, itemId, string.Join(",", ordered.Select(x => $"{x.Slot}{(x.Dyed ? "d" : "")}")), alsoInSet);
        }

        entries.Sort((a, b) =>
        {
            var c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            if (c != 0) return c;
            if (a.IsKeep != b.IsKeep) return a.IsKeep ? -1 : 1;
            return a.DresserSlot.CompareTo(b.DresserSlot);
        });

        var surplus = entries.Count(x => !x.IsKeep);
        Status = DuplicateGroups == 0
            ? "중복 등록된 아이템이 없습니다."
            : $"검색 완료: 중복 아이템 {DuplicateGroups}종, 가방으로 되돌릴 수 있는 중복분 {surplus}개.";
        return entries.Count;
    }

    public void SetSelected(int slot, bool value)
    {
        if (value) selected.Add(slot);
        else selected.Remove(slot);
    }

    public void SelectSurplusOnly()
    {
        selected.Clear();
        foreach (var entry in entries)
            if (!entry.IsKeep)
                selected.Add(entry.DresserSlot);
        Status = $"중복분 {selected.Count}개를 체크했습니다 (각 아이템의 한 벌은 남김).";
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
            queue.Add((entry.DresserSlot, entry.ItemId, entry.Name));

        if (queue.Count == 0)
        {
            Status = "가방으로 되돌릴 항목을 체크해 주세요.";
            return false;
        }

        var inventory = InventoryManager.Instance();
        var free = inventory == null ? 0 : (int)inventory->GetEmptySlotsInBag();
        if (free == 0)
        {
            Status = "인벤토리 빈칸이 없습니다.";
            return false;
        }

        if (free < queue.Count)
        {
            queue.RemoveRange(free, queue.Count - free);
            LastItemStatus = $"인벤토리 빈칸이 {free}칸뿐이라 {free}개만 되돌립니다.";
        }
        else
        {
            LastItemStatus = null;
        }

        index = Restored = Skipped = Failed = 0;
        nextAt = 0;
        gate.Reset();
        IsRunning = true;
        Status = $"체크한 중복분 {queue.Count}개를 가방으로 되돌립니다.";
        return true;
    }

    public void Stop(string reason = "옷장 중복 정리를 중지했습니다.")
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
            var completed = $"완료: 되돌림 {Restored} · 제외 {Skipped} · 실패 {Failed}";
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

        if (!gate.Ready(manager, log, config.DebugLog))
        {
            nextAt = now + 100;
            return;
        }

        var (slot, expectedItemId, name) = queue[index];
        if (slot < 0 || slot >= manager->PrismBoxItemIds.Length || Normalize(manager->PrismBoxItemIds[slot]) != expectedItemId)
        {
            Skipped++;
            LastItemStatus = $"{name}: 옷장 슬롯 내용이 바뀌어 건너뜁니다.";
        }
        else if (manager->RestorePrismBoxItem((uint)slot))
        {
            Restored++;
            gate.Arm(slot, -1, expectedItemId);
            LastItemStatus = $"{name}: 가방으로 되돌리기 요청 완료.";
        }
        else if (gate.AllowRetry())
        {
            LastItemStatus = $"{name}: 게임이 이전 요청을 아직 처리 중이라 다시 시도합니다 ({gate.Retries}/{RestoreGate.MaxRetries}).";
            nextAt = now + RestoreGate.RetryIntervalMs;
            return;
        }
        else
        {
            Failed++;
            LastItemStatus = $"{name}: 게임이 되돌리기 요청을 거절했습니다 (고유 아이템 중복 소지 또는 빈칸 부족).";
        }

        if (config.DebugLog)
            log.Information("[KWC] dup restore slot {Slot} {Name} #{Id}: {Result}", slot, name, expectedItemId, LastItemStatus);

        index++;
        nextAt = now + 700;
        Status = $"진행 {index}/{queue.Count} · 되돌림 {Restored} · 제외 {Skipped} · 실패 {Failed}";
    }

    private bool IsDresserOpen()
    {
        var addon = gameGui.GetAddonByName("MiragePrismPrismBox");
        return addon != null && addon.IsVisible;
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}
