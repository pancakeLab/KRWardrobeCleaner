using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace KRWardrobeCleaner;

/// <summary>
/// Outfit (의상 세트) definitions from the MirageStoreSetItem sheet.
/// Slot index == sheet column index (0 주무기 … 10 반지). This is the index the game uses for
/// MirageManager.IsSetSlotUnlocked / RestorePrismBoxSetItem bits, so it must not be shifted.
/// (v0.8.1 read columns 2..10 and used 0..8 as the slot index, which was off by two.)
/// </summary>
public sealed class OutfitCatalog
{
    public const int SlotCount = 11;
    public static readonly string[] SlotNames =
        ["주무기", "보조무기", "머리", "상의", "손", "하의", "발", "귀걸이", "목걸이", "팔찌", "반지"];

    public sealed record SetInfo(uint RowId, string Name, IReadOnlyList<uint> ItemIds);

    private readonly Dictionary<uint, SetInfo> sets = [];
    private readonly Dictionary<uint, List<SetInfo>> byPiece = [];

    public int SheetColumns { get; private set; }

    public OutfitCatalog(IDataManager data, ExcelIndex excel)
    {
        var sheet = data.GetExcelSheet<MirageStoreSetItem>();
        if (sheet == null) return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;

            // Column order of the sheet (EXDSchema): MainHand, OffHand, Head, Body, Hands, Legs, Feet, Earrings, Necklace, Bracelets, Ring.
            uint[] ids =
            [
                row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId, row.Hands.RowId, row.Legs.RowId,
                row.Feet.RowId, row.Earrings.RowId, row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
            ];
            SheetColumns = SlotCount;

            if (ids.All(x => x == 0)) continue;

            var set = new SetInfo(row.RowId, excel.NameOf(row.RowId), ids);
            sets[row.RowId] = set;
            foreach (var id in ids.Where(x => x != 0))
            {
                if (!byPiece.TryGetValue(id, out var list))
                    byPiece[id] = list = [];
                list.Add(set);
            }
        }
    }

    public static string SlotName(int slot)
        => slot >= 0 && slot < SlotNames.Length ? SlotNames[slot] : $"슬롯{slot}";

    public int Count => sets.Count;

    public bool TryGet(uint setItemId, out SetInfo set) => sets.TryGetValue(setItemId, out set!);

    public IEnumerable<SetInfo> SetsFor(uint itemId)
        => byPiece.TryGetValue(itemId, out var list) ? list : [];

    public IEnumerable<SetInfo> All => sets.Values;
}

/// Reads which base item ids the dresser currently holds (standalone slots plus pieces inside outfit sets).
public static unsafe class DresserContents
{
    public static HashSet<uint> StoredItemIds(MirageManager* manager, OutfitCatalog outfits)
    {
        var result = new HashSet<uint>();
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var raw = manager->PrismBoxItemIds[i];
            var id = raw >= 1_000_000 ? raw % 1_000_000 : raw;
            if (id == 0) continue;

            if (!outfits.TryGet(id, out var set))
            {
                result.Add(id);
                continue;
            }

            for (var slot = 0; slot < set.ItemIds.Count; slot++)
                if (set.ItemIds[slot] != 0 && manager->IsSetSlotUnlocked((uint)i, slot))
                    result.Add(set.ItemIds[slot]);
        }
        return result;
    }
}

public sealed unsafe class Stage2InventoryToDresser
{
    private const ushort FullCondition = 30000;
    private const int MaxBatchPieces = 9;          // pieces per store request (same cap as other implementations)
    private const int BatchVerifyTimeoutMs = 6000; // how long we wait for the game to actually take the pieces

    private readonly ExcelIndex excel;
    private readonly InventoryScanner scanner;
    private readonly OutfitCatalog outfits;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly System.Action saveConfig;

    private readonly List<PhysicalGearEntry> entries = [];
    private readonly Dictionary<ItemLocationKey, string> blocked = [];
    private readonly HashSet<ItemLocationKey> matched = [];
    private readonly GearFilter filter;
    private readonly HashSet<ItemLocationKey> selected = [];
    private readonly Queue<SetBatch> setQueue = new();
    private readonly Queue<PhysicalGearEntry> singleQueue = new();

    private PendingBatch? pendingBatch;
    private PhysicalGearEntry? pendingSingle;
    private long pendingSingleAt;
    private long nextSetActionAt;
    private SingleStoreState singleState;

    public IReadOnlyList<PhysicalGearEntry> Entries => entries;
    public IReadOnlySet<ItemLocationKey> Selected => selected;
    public IReadOnlyDictionary<ItemLocationKey, string> Blocked => blocked;
    public bool IsRunning { get; private set; }
    public int Stored { get; private set; }
    public int Skipped { get; private set; }
    public int Failed { get; private set; }
    public int Total { get; private set; }
    public int Done => Stored + Skipped + Failed;
    public bool IsMatched(ItemLocationKey key) => matched.Contains(key);
    public int MatchedCount => matched.Count;
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage2InventoryToDresser(ExcelIndex excel, InventoryScanner scanner, OutfitCatalog outfits, GearFilter filter, IGameGui gameGui, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.filter = filter;
        this.scanner = scanner;
        this.outfits = outfits;
        this.gameGui = gameGui;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
    }

    public void UpdateIncludeDyed(bool value)
    {
        config.Stage2IncludeDyed = value;
        saveConfig();
    }

    public void UpdateIncludeMateria(bool value)
    {
        config.Stage2IncludeMateria = value;
        saveConfig();
        RefreshBlockReasons();
    }

    public string? BlockReason(ItemLocationKey key) => blocked.TryGetValue(key, out var reason) ? reason : null;

    public int Scan()
    {
        if (IsRunning) return entries.Count;
        entries.Clear();
        selected.Clear();
        blocked.Clear();

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장을 연 상태에서 검색해 주세요.";
            return 0;
        }

        var storedPieces = BuildStoredPieceSet(manager);
        var gameList = ReadCrystallizeList();
        var hiddenStored = 0;

        foreach (var entry in scanner.Scan(includeBags: true, includeArmory: true, requireGlamourable: true))
        {
            if (storedPieces.Contains(entry.ItemId))
            {
                hiddenStored++;
                Debug("s2 skip (already in dresser) {Name} #{Id} {Loc}", entry.Name, entry.ItemId, entry.Key.Id);
                continue;
            }

            entries.Add(entry);
            var reason = GetBlockReason(entry);
            if (reason != null)
                blocked[entry.Key] = reason;

            var inGameList = gameList == null ? "?" : gameList.Contains(entry.Key) ? "y" : "n";
            Debug("s2 candidate {Name} #{Id} {Loc} cond={Cond}% materia={Materia} glamour={Glam} dyed={Dyed} inGameDepositList={List} block={Block}",
                entry.Name, entry.ItemId, entry.Key.Id, entry.ConditionPercent, entry.MateriaCount, entry.IsGlamoured, entry.IsDyed, inGameList, reason ?? "-");
        }

        if (config.DebugLog)
            LogExistingSets(manager);

        ApplyFilters();
        Status = $"검색 완료: 넣을 수 있는 장비 {entries.Count}개 (조건 미달 {blocked.Count}개 · 이미 옷장에 있어 숨김 {hiddenStored}개)"
               + (filter.AnyEnabled ? $" · 필터 일치 {matched.Count}개 중 {selected.Count}개 체크" : string.Empty) + ".";
        return entries.Count;
    }

    /// Applies the shared gear filter: matching rows are checked (unless blocked) and sorted to the top.
    public void ApplyFilters()
    {
        if (IsRunning) return;
        matched.Clear();
        if (!filter.AnyEnabled)
        {
            SortEntries();
            return;
        }

        selected.Clear();
        foreach (var entry in entries)
        {
            if (!filter.Matches(entry.ItemId, entry.RequiredLevel)) continue;
            matched.Add(entry.Key);
            if (!blocked.ContainsKey(entry.Key) && (config.Stage2IncludeDyed || !entry.IsDyed))
                selected.Add(entry.Key);
        }

        SortEntries();
        var warning = filter.DresserWarning is { } w ? " " + w : string.Empty;
        Status = $"필터 일치 {matched.Count}개 중 {selected.Count}개 체크 (조건 미달·염색 제외).{warning}";
    }

    private void SortEntries()
        => entries.Sort((a, b) =>
        {
            var ma = matched.Contains(a.Key);
            var mb = matched.Contains(b.Key);
            if (ma != mb) return ma ? -1 : 1;
            var c = StringComparer.CurrentCulture.Compare(a.Name, b.Name);
            return c != 0 ? c : a.Key.Id.CompareTo(b.Key.Id);
        });

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
            if (!blocked.ContainsKey(entry.Key))
                selected.Add(entry.Key);
    }

    public void SelectExistingSetCandidates()
    {
        selected.Clear();
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
        {
            Status = "환상의 옷장 데이터를 읽을 수 없습니다.";
            return;
        }

        var existing = GetExistingSets(manager);
        var blockedCandidates = 0;
        foreach (var entry in entries)
        {
            if (entry.IsDyed && !config.Stage2IncludeDyed)
                continue;

            var canFill = false;
            foreach (var set in outfits.SetsFor(entry.ItemId))
            {
                if (!existing.TryGetValue(set.RowId, out var boxIndex))
                    continue;

                for (var slot = 0; slot < set.ItemIds.Count; slot++)
                {
                    if (set.ItemIds[slot] == entry.ItemId && !manager->IsSetSlotUnlocked(boxIndex, slot))
                    {
                        canFill = true;
                        break;
                    }
                }

                if (canFill) break;
            }

            if (!canFill) continue;
            if (blocked.ContainsKey(entry.Key))
            {
                blockedCandidates++;
                Debug("s2 existing-set candidate blocked {Name} #{Id} {Loc}: {Reason}", entry.Name, entry.ItemId, entry.Key.Id, blocked[entry.Key]);
                continue;
            }

            selected.Add(entry.Key);
        }

        Status = blockedCandidates == 0
            ? $"기존 세트에 추가 가능한 항목 {selected.Count}개를 체크했습니다."
            : $"기존 세트에 추가 가능한 항목 {selected.Count}개를 체크했습니다. 조건 미달(수리/마테리아) {blockedCandidates}개는 제외.";
    }

    public bool StartExistingSetsOnly() => Start(storeRemainingSingles: false, existingOnly: true);
    public bool StartSetsThenSingles() => Start(storeRemainingSingles: true, existingOnly: false);

    private bool Start(bool storeRemainingSingles, bool existingOnly)
    {
        if (IsRunning) return false;
        if (!IsDresserReady())
        {
            Status = "환상의 옷장과 '아이템 맡기기' 목록을 연 상태에서 실행해 주세요.";
            return false;
        }

        var picked = entries
            .Where(x => selected.Contains(x.Key))
            .Where(x => config.Stage2IncludeDyed || !x.IsDyed)
            .ToList();
        var chosen = picked.Where(x => !blocked.ContainsKey(x.Key)).ToList();
        var skippedBlocked = picked.Count - chosen.Count;

        if (chosen.Count == 0)
        {
            Status = skippedBlocked > 0
                ? $"체크 항목 {skippedBlocked}개가 모두 조건 미달(수리/마테리아)이라 처리할 것이 없습니다."
                : "현재 옵션 조건에서 처리할 체크 항목이 없습니다.";
            return false;
        }

        setQueue.Clear();
        singleQueue.Clear();
        pendingBatch = null;
        pendingSingle = null;
        nextSetActionAt = 0;
        singleState = SingleStoreState.None;

        var manager = MirageManager.Instance()!;
        var existing = GetExistingSets(manager);
        var consumed = new HashSet<ItemLocationKey>();

        foreach (var set in outfits.All.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            var batch = new List<SetPiece>();
            var localUsed = new HashSet<ItemLocationKey>();
            var hasExisting = existing.TryGetValue(set.RowId, out var existingIndex);

            for (var slotIndex = 0; slotIndex < set.ItemIds.Count; slotIndex++)
            {
                var requiredId = set.ItemIds[slotIndex];
                if (requiredId == 0)
                    continue;

                if (hasExisting && manager->IsSetSlotUnlocked(existingIndex, slotIndex))
                    continue;

                var piece = chosen.FirstOrDefault(x =>
                    x.ItemId == requiredId &&
                    !consumed.Contains(x.Key) &&
                    !localUsed.Contains(x.Key));

                if (piece is null)
                    continue;

                batch.Add(new(piece, slotIndex));
                localUsed.Add(piece.Key);
            }

            if (batch.Count == 0)
                continue;

            if (hasExisting)
            {
                setQueue.Enqueue(new(set, batch, existingIndex));
                foreach (var piece in batch)
                    consumed.Add(piece.Entry.Key);
            }
            else if (!existingOnly && batch.Count >= 2)
            {
                setQueue.Enqueue(new(set, batch, null));
                foreach (var piece in batch)
                    consumed.Add(piece.Entry.Key);
            }
        }

        if (storeRemainingSingles)
        {
            foreach (var entry in chosen.Where(x => !consumed.Contains(x.Key)))
                singleQueue.Enqueue(entry);
        }

        if (setQueue.Count == 0 && singleQueue.Count == 0)
        {
            Status = existingOnly
                ? "체크 항목 중 기존 세트에 추가할 수 있는 아이템이 없습니다."
                : "저장할 수 있는 체크 항목이 없습니다.";
            return false;
        }

        if (config.DebugLog)
        {
            foreach (var b in setQueue)
                Debug("s2 plan set '{Set}' #{SetId} existingIndex={Idx} pieces=[{Pieces}]",
                    b.Set.Name, b.Set.RowId, b.ExistingIndex?.ToString() ?? "new",
                    string.Join(", ", b.Pieces.Select(p => $"{OutfitCatalog.SlotName(p.SetSlot)}:{p.Entry.Name}@{p.Entry.Key.Id}")));
            foreach (var s in singleQueue)
                Debug("s2 plan single {Name} #{Id} {Loc}", s.Name, s.ItemId, s.Key.Id);
        }

        Stored = Failed = 0;
        Skipped = skippedBlocked;
        Total = setQueue.Sum(b => b.Pieces.Count) + singleQueue.Count + skippedBlocked;
        LastItemStatus = skippedBlocked > 0 ? $"조건 미달(수리/마테리아) {skippedBlocked}개는 제외했습니다." : null;
        IsRunning = true;
        Status = $"저장 시작: 세트 작업 {setQueue.Count}개 · 단벌 {singleQueue.Count}개.";
        return true;
    }

    public void Stop(string reason = "2단계 작업을 중지했습니다.")
    {
        IsRunning = false;
        pendingBatch = null;
        pendingSingle = null;
        singleState = SingleStoreState.None;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        if (!IsDresserReady())
        {
            Stop("환상의 옷장 또는 아이템 맡기기 목록이 닫혀 작업을 중지했습니다.");
            return;
        }

        if (pendingBatch is not null)
        {
            TickBatch();
            return;
        }

        if (pendingSingle is not null)
        {
            TickSingle();
            return;
        }

        if (Environment.TickCount64 < nextSetActionAt)
            return;

        if (setQueue.TryDequeue(out var batch))
        {
            StoreSetBatch(batch);
            return;
        }

        if (singleQueue.TryDequeue(out var single))
        {
            pendingSingle = single;
            pendingSingleAt = Environment.TickCount64;
            singleState = SingleStoreState.Select;
            TickSingle();
            return;
        }

        IsRunning = false;
        var completed = $"완료: 저장 {Stored} · 제외 {Skipped} · 실패 {Failed}";
        Scan();
        Status = completed;
    }

    private void StoreSetBatch(SetBatch batch)
    {
        var manager = MirageManager.Instance();
        var inventory = InventoryManager.Instance();
        if (manager == null || inventory == null)
        {
            Stop("환상의 옷장 또는 인벤토리 데이터를 읽을 수 없습니다.");
            return;
        }

        // Arrays are packed from the front (same as other known implementations); the game resolves
        // the set slot from each item. Unused entries stay Invalid / 0.
        Span<InventoryType> containers = stackalloc InventoryType[OutfitCatalog.SlotCount];
        Span<ushort> slots = stackalloc ushort[OutfitCatalog.SlotCount];
        containers.Fill(InventoryType.Invalid);
        slots.Clear();

        var sent = new List<SetPiece>();
        foreach (var setPiece in batch.Pieces)
        {
            if (sent.Count >= MaxBatchPieces)
                break;

            var piece = setPiece.Entry;
            if (!scanner.TryResolve(piece.Key, piece.ItemId, out var item))
            {
                Skipped++;
                LastItemStatus = $"{piece.Name}: 위치가 바뀌어 건너뜁니다.";
                Debug("s2 batch '{Set}' drop {Name} {Loc}: not at expected location", batch.Set.Name, piece.Name, piece.Key.Id);
                continue;
            }

            if (item->Condition < FullCondition)
            {
                Skipped++;
                LastItemStatus = $"{piece.Name}: 내구도 {item->Condition * 100 / FullCondition}%라 건너뜁니다 (수리 필요).";
                Debug("s2 batch '{Set}' drop {Name} {Loc}: condition {Cond}", batch.Set.Name, piece.Name, piece.Key.Id, item->Condition);
                continue;
            }

            if (batch.ExistingIndex is uint idx && manager->IsSetSlotUnlocked(idx, setPiece.SetSlot))
            {
                Skipped++;
                Debug("s2 batch '{Set}' drop {Name}: slot {Slot} already unlocked", batch.Set.Name, piece.Name, OutfitCatalog.SlotName(setPiece.SetSlot));
                continue;
            }

            containers[sent.Count] = piece.Key.Container;
            slots[sent.Count] = (ushort)piece.Key.Slot;
            sent.Add(setPiece);
        }

        var minimum = batch.ExistingIndex.HasValue ? 1 : 2;
        if (sent.Count < minimum)
        {
            Skipped += sent.Count;
            LastItemStatus = $"{batch.Set.Name}: 저장 가능한 체크 항목이 부족해 건너뜁니다.";
            nextSetActionAt = Environment.TickCount64 + 300;
            return;
        }

        bool ok;
        fixed (InventoryType* cp = containers)
        fixed (ushort* sp = slots)
        {
            ok = batch.ExistingIndex is uint idx
                ? manager->StoreExistingOutfit(idx, cp, sp)
                : manager->StoreNewOutfit(batch.Set.RowId, cp, sp);
        }

        Debug("s2 store {Kind} '{Set}' index={Idx} pieces={Count} [{Pieces}] -> {Result}",
            batch.ExistingIndex.HasValue ? "existing" : "new", batch.Set.Name, batch.ExistingIndex?.ToString() ?? "-", sent.Count,
            string.Join(", ", sent.Select(p => $"{OutfitCatalog.SlotName(p.SetSlot)}:{p.Entry.Name}@{p.Entry.Key.Id}")), ok);

        if (ok)
        {
            pendingBatch = new(batch.Set, sent, Environment.TickCount64);
            LastItemStatus = $"{batch.Set.Name}: {sent.Count}개 세트 저장 요청, 게임 반영을 확인하는 중…";
        }
        else
        {
            Failed += sent.Count;
            LastItemStatus = $"{batch.Set.Name}: 세트 저장 요청이 거절되었습니다 ({sent.Count}개).";
            log.Warning("[KWC] s2 store request rejected by client for '{Set}' ({Count} pieces)", batch.Set.Name, sent.Count);
            nextSetActionAt = Environment.TickCount64 + 700;
        }
    }

    /// Waits until the pieces of the last set request have left the inventory. Anything still
    /// present after the timeout is reported as a failure instead of being silently counted as stored.
    private void TickBatch()
    {
        var pb = pendingBatch!;
        var now = Environment.TickCount64;
        var remaining = pb.Pieces.Where(p => scanner.TryResolve(p.Entry.Key, p.Entry.ItemId, out _)).ToList();

        if (remaining.Count == 0)
        {
            Stored += pb.Pieces.Count;
            LastItemStatus = $"{pb.Set.Name}: {pb.Pieces.Count}개 세트 저장 확인.";
            Debug("s2 verified '{Set}': all {Count} pieces stored in {Ms} ms", pb.Set.Name, pb.Pieces.Count, now - pb.SentAt);
            pendingBatch = null;
            nextSetActionAt = now + 500;
            return;
        }

        if (now - pb.SentAt <= BatchVerifyTimeoutMs)
            return;

        var done = pb.Pieces.Count - remaining.Count;
        Stored += done;
        Failed += remaining.Count;
        var names = string.Join(", ", remaining.Select(p => p.Entry.Name));
        LastItemStatus = $"{pb.Set.Name}: {done}개 저장 확인, {remaining.Count}개는 게임이 처리하지 않았습니다 ({names}). 로그를 확인해 주세요.";
        foreach (var p in remaining)
        {
            scanner.TryResolve(p.Entry.Key, p.Entry.ItemId, out var item);
            log.Warning("[KWC] s2 NOT stored '{Set}' slot {Slot} {Name} #{Id} {Loc} cond={Cond} materia={Materia} glamour={Glam} flags={Flags}",
                pb.Set.Name, OutfitCatalog.SlotName(p.SetSlot), p.Entry.Name, p.Entry.ItemId, p.Entry.Key.Id,
                item == null ? -1 : item->Condition, p.Entry.MateriaCount, p.Entry.IsGlamoured, item == null ? "?" : item->Flags.ToString());
        }

        pendingBatch = null;
        nextSetActionAt = now + 500;
    }

    private void TickSingle()
    {
        var entry = pendingSingle!;
        var now = Environment.TickCount64;

        if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Stored++;
            LastItemStatus = $"{entry.Name}: 인벤토리에서 사라져 저장 완료로 확인했습니다.";
            Debug("s2 single stored {Name} {Loc}", entry.Name, entry.Key.Id);
            pendingSingle = null;
            singleState = SingleStoreState.None;
            return;
        }

        if (now - pendingSingleAt > 12000)
        {
            Failed++;
            LastItemStatus = $"{entry.Name}: 단벌 저장 완료를 확인하지 못해 건너뜁니다.";
            log.Warning("[KWC] s2 single NOT stored {Name} #{Id} {Loc} state={State}", entry.Name, entry.ItemId, entry.Key.Id, singleState);
            pendingSingle = null;
            singleState = SingleStoreState.None;
            return;
        }

        switch (singleState)
        {
            case SingleStoreState.Select:
            {
                var agent = AgentMiragePrismPrismBox.Instance();
                var data = agent == null ? null : agent->Data;
                if (data == null)
                {
                    Stop("환상의 옷장 맡기기 데이터를 읽을 수 없습니다.");
                    return;
                }

                var row = -1;
                for (var i = 0; i < data->CrystallizeItemCount; i++)
                {
                    var candidate = data->CrystallizeItems[i];
                    if (candidate.Inventory == entry.Key.Container &&
                        candidate.Slot == entry.Key.Slot &&
                        Normalize(candidate.ItemId) == entry.ItemId)
                    {
                        row = i;
                        break;
                    }
                }

                if (row < 0)
                {
                    Skipped++;
                    LastItemStatus = $"{entry.Name}: 게임의 맡기기 목록에서 찾지 못해 건너뜁니다.";
                    Debug("s2 single {Name} {Loc} not in game deposit list (count={Count})", entry.Name, entry.Key.Id, data->CrystallizeItemCount);
                    pendingSingle = null;
                    singleState = SingleStoreState.None;
                    return;
                }

                if (!FireCallback("MiragePrismPrismBoxCrystallize", 0, row))
                    return;

                singleState = SingleStoreState.Confirm;
                return;
            }

            case SingleStoreState.Confirm:
                if (TryConfirmYesNoForItem(entry.Name))
                    singleState = SingleStoreState.Wait;
                return;

            case SingleStoreState.Wait:
                return;
        }
    }

    private void RefreshBlockReasons()
    {
        blocked.Clear();
        foreach (var entry in entries)
        {
            var reason = GetBlockReason(entry);
            if (reason != null)
            {
                blocked[entry.Key] = reason;
                selected.Remove(entry.Key);
            }
        }
    }

    private string? GetBlockReason(PhysicalGearEntry entry)
    {
        if (entry.ConditionPercent < 100)
            return $"수리 필요 ({entry.ConditionPercent}%)";
        if (entry.MateriaCount > 0 && !config.Stage2IncludeMateria)
            return $"마테리아 {entry.MateriaCount}개 장착";
        return null;
    }

    /// Items the game itself lists in the '아이템 맡기기' window (null when that data is not available).
    private HashSet<ItemLocationKey>? ReadCrystallizeList()
    {
        var agent = AgentMiragePrismPrismBox.Instance();
        var data = agent == null ? null : agent->Data;
        if (data == null || !IsAddonReady("MiragePrismPrismBoxCrystallize"))
            return null;

        var result = new HashSet<ItemLocationKey>();
        for (var i = 0; i < data->CrystallizeItemCount; i++)
        {
            var c = data->CrystallizeItems[i];
            result.Add(new(c.Inventory, c.Slot));
        }
        return result;
    }

    private void LogExistingSets(MirageManager* manager)
    {
        Debug("s2 outfit sheet: {Sets} sets, {Cols} columns per set", outfits.Count, outfits.SheetColumns);
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var id = Normalize(manager->PrismBoxItemIds[i]);
            if (id == 0 || !outfits.TryGet(id, out var set)) continue;

            var parts = new List<string>();
            for (var slot = 0; slot < set.ItemIds.Count; slot++)
            {
                if (set.ItemIds[slot] == 0) continue;
                var unlocked = manager->IsSetSlotUnlocked((uint)i, slot);
                parts.Add($"{OutfitCatalog.SlotName(slot)}:{excel.NameOf(set.ItemIds[slot])}={(unlocked ? "있음" : "없음")}");
            }

            Debug("s2 dresser set '{Set}' #{SetId} boxIndex={Idx}: {Slots}", set.Name, set.RowId, i, string.Join(" | ", parts));
        }
    }

    private HashSet<uint> BuildStoredPieceSet(MirageManager* manager) => DresserContents.StoredItemIds(manager, outfits);

    private Dictionary<uint, uint> GetExistingSets(MirageManager* manager)
    {
        var result = new Dictionary<uint, uint>();
        for (var i = 0; i < manager->PrismBoxItemIds.Length; i++)
        {
            var id = Normalize(manager->PrismBoxItemIds[i]);
            if (id != 0 && outfits.TryGet(id, out _) && !result.ContainsKey(id))
                result[id] = (uint)i;
        }
        return result;
    }

    private bool IsDresserReady()
    {
        var manager = MirageManager.Instance();
        return manager != null &&
               manager->PrismBoxLoaded &&
               IsAddonReady("MiragePrismPrismBox") &&
               IsAddonReady("MiragePrismPrismBoxCrystallize");
    }

    private bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    private bool FireCallback(string addonName, params int[] values)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
            atk[i].SetInt(values[i]);
        addon->FireCallback((uint)values.Length, atk);
        return true;
    }

    private bool TryConfirmYesNoForItem(string expectedItemName)
    {
        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible || !yesno->AtkUnitBase.IsReady)
                continue;

            var prompt = yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(prompt) ||
                !prompt.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase))
                continue;

            yesno->AtkUnitBase.FireCallbackInt(0);
            return true;
        }

        return false;
    }

    private void Debug(string template, params object[] args)
    {
        if (config.DebugLog)
            log.Information("[KWC] " + template, args);
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;

    private sealed record SetPiece(PhysicalGearEntry Entry, int SetSlot);
    private sealed record SetBatch(OutfitCatalog.SetInfo Set, IReadOnlyList<SetPiece> Pieces, uint? ExistingIndex);
    private sealed record PendingBatch(OutfitCatalog.SetInfo Set, IReadOnlyList<SetPiece> Pieces, long SentAt);
    private enum SingleStoreState { None, Select, Confirm, Wait }
}
