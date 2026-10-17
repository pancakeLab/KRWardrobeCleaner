using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
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
    private const int SingleTimeoutMs = 15000;     // whole single-item flow (staging + deposit + confirm)
    private const int MoveTimeoutMs = 3000;        // armoury -> bag move confirmation
    private const int ListWaitMs = 2500;           // wait for the game deposit list to show a bag item
    private const int ListRefreshAfterMs = 800;    // ask the game to rebuild the list after this long

    private readonly ExcelIndex excel;
    private readonly InventoryScanner scanner;
    private readonly OutfitCatalog outfits;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
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
    private ItemLocationKey singleMoveDest;
    private long singleMoveAt;
    private long listWaitStart;
    private bool listRefreshRequested;
    private long confirmStartedAt;
    private string? lastPromptLogged;

    private PrestageState? prestage;
    private List<SetBatch> plannedSets = [];
    private List<PhysicalGearEntry> plannedSingles = [];

    private SetBatch? stagingBatch;
    private List<SetPiece> stagedPieces = [];
    private readonly Queue<SetPiece> stagingToMove = new();
    private (SetPiece Piece, ItemLocationKey Dest, long At)? stagingMove;

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
    public bool HasOutfit(uint itemId) => outfits.SetsFor(itemId).Any();
    public int SelectedWithOutfit => entries.Count(e => selected.Contains(e.Key) && HasOutfit(e.ItemId));
    public string Status { get; private set; } = "환상의 옷장을 연 뒤 후보를 검색해 주세요.";
    public string? LastItemStatus { get; private set; }

    public Stage2InventoryToDresser(ExcelIndex excel, InventoryScanner scanner, OutfitCatalog outfits, GearFilter filter, IGameGui gameGui, IObjectTable objects, ICondition condition, IPluginLog log, Configuration config, System.Action saveConfig)
    {
        this.excel = excel;
        this.filter = filter;
        this.objects = objects;
        this.condition = condition;
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
    public bool StartSetsOnly() => Start(storeRemainingSingles: false, existingOnly: false);
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
            else if (!existingOnly)
            {
                // A new (partial) outfit can start from a single piece. If the game refuses the set
                // request, the pieces are retried as singles when that mode is on.
                setQueue.Enqueue(new(set, batch, null, storeRemainingSingles));
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
        DeferArmoryWork();
        Status = $"저장 시작: 가방에서 바로 넣을 세트 {setQueue.Count}개 · 단벌 {singleQueue.Count}개, 장비함에서 꺼낸 뒤 넣을 조각 {DeferredCount}개.";
        return true;
    }

    public void Stop(string reason = "2단계 작업을 중지했습니다.")
    {
        if (IsRunning)
            log.Information("[KWC] s2 stop: {Reason} (stored {Stored} skipped {Skipped} failed {Failed}, sets {Sets} singles {Singles} left)", reason, Stored, Skipped, Failed, setQueue.Count, singleQueue.Count);
        IsRunning = false;
        prestage = null;
        plannedSets = [];
        plannedSingles = [];
        pendingBatch = null;
        stagingBatch = null;
        stagingMove = null;
        stagingToMove.Clear();
        pendingSingle = null;
        singleState = SingleStoreState.None;
        Status = reason;
    }

    public void Tick()
    {
        if (!IsRunning) return;
        if (prestage is not null)
        {
            TickPrestage();
            return;
        }

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

        if (stagingBatch is not null)
        {
            TickStaging();
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
            BeginStaging(batch);
            return;
        }

        if (singleQueue.TryDequeue(out var single))
        {
            pendingSingle = single;
            pendingSingleAt = Environment.TickCount64;
            listWaitStart = pendingSingleAt;
            listRefreshRequested = false;
            singleState = SingleStoreState.Select;
            TickSingle();
            return;
        }

        if (DeferredCount > 0)
        {
            if (BeginRound())
                return;

            var dropped = DeferredCount;
            Skipped += dropped;
            plannedSets = [];
            plannedSingles = [];
            LastItemStatus = $"가방 빈칸이 없어 장비함 조각 {dropped}개는 넣지 못했습니다. 가방을 비운 뒤 다시 실행해 주세요.";
            Debug("s2 rounds finished with {Dropped} armoury pieces left (no free bag slot)", dropped);
        }

        IsRunning = false;
        var completed = $"완료: 저장 {Stored} · 제외 {Skipped} · 실패 {Failed}";
        Scan();
        Status = completed;
    }

    // ---- pre-stage: the game refuses armoury -> bag moves while the dresser is open (MoveItemSlot rc=10,
    // "이벤트 중"), so the dresser is closed first, every armoury piece is moved into the bags, and the
    // dresser is reopened by interacting with the nearby dresser object. Storing then continues as usual.

    private const uint GlamourDresserEObj = 2009439;   // EObjName "환상의 옷장"
    private const int PrestageMoveIntervalMs = 350;
    private const int ReopenRetryMs = 8000;

    private enum PrestagePhase { Closing, Moving, Reopen, WaitDresser }

    private sealed class PrestageState
    {
        public PrestagePhase Phase = PrestagePhase.Closing;
        public long PhaseAt;
        public int CloseAttempts;
        public readonly Queue<PhysicalGearEntry> ToMove = new();
        public readonly Dictionary<ItemLocationKey, ItemLocationKey> Moved = [];
        public (PhysicalGearEntry Entry, ItemLocationKey Dest, long At)? Current;
        public ulong DresserObjectId;
        public bool ReopenRequested;
        public int ReopenAttempts;
        public int Total;
        public long ClosedAt;
        public long ReadySince;
        public long LastWaitLog;
    }

    public bool IsPrestaging => prestage != null;

    /// Work that still needs armoury pieces is deferred: everything already in the bags is stored first
    /// (which frees bag space), then rounds of "close dresser → move pieces → reopen" bring the armoury
    /// pieces in. Set batches are moved as a whole so a set is never split across rounds.
    private void DeferArmoryWork()
    {
        plannedSets = [];
        plannedSingles = [];

        var keepSets = new List<SetBatch>();
        foreach (var b in setQueue)
        {
            if (b.Pieces.Any(p => IsArmory(p.Entry.Key.Container))) plannedSets.Add(b);
            else keepSets.Add(b);
        }
        setQueue.Clear();
        foreach (var b in keepSets) setQueue.Enqueue(b);

        var keepSingles = new List<PhysicalGearEntry>();
        foreach (var s in singleQueue)
        {
            if (IsArmory(s.Key.Container)) plannedSingles.Add(s);
            else keepSingles.Add(s);
        }
        singleQueue.Clear();
        foreach (var s in keepSingles) singleQueue.Enqueue(s);

        if (DeferredCount > 0)
            Debug("s2 deferred armoury work: sets={Sets} singles={Singles} (bag work first: sets={BagSets} singles={BagSingles})",
                plannedSets.Count, plannedSingles.Count, setQueue.Count, singleQueue.Count);
    }

    public int DeferredCount => plannedSets.Sum(b => b.Pieces.Count) + plannedSingles.Count;

    /// Starts one close → move → reopen round for as many deferred pieces as the bags can take.
    /// Returns false when nothing fits (no free bag slot).
    private bool BeginRound()
    {
        var free = scanner.CountFreeBagSlots();
        var state = new PrestageState { PhaseAt = Environment.TickCount64, DresserObjectId = FindDresserObjectId() };
        var remaining = free;

        foreach (var b in plannedSets)
        {
            var pieces = b.Pieces.Where(p => IsArmory(p.Entry.Key.Container)).Select(p => p.Entry).ToList();
            if (pieces.Count == 0 || pieces.Count > remaining) continue;
            foreach (var e in pieces) state.ToMove.Enqueue(e);
            remaining -= pieces.Count;
        }

        foreach (var s in plannedSingles)
        {
            if (remaining <= 0) break;
            state.ToMove.Enqueue(s);
            remaining--;
        }

        state.Total = state.ToMove.Count;
        if (state.Total == 0)
            return false;

        prestage = state;
        CloseDresser();
        Status = $"가방 아이템은 다 넣었습니다. 장비함 조각 {state.Total}개를 가방으로 옮기기 위해 환상의 옷장을 잠시 닫습니다… (장비함 대기 {DeferredCount}개)";
        Debug("s2 round: moving {Count} of {Deferred} deferred armoury pieces (free bag slots {Free}), dresser object {Obj}",
            state.Total, DeferredCount, free, state.DresserObjectId);
        return true;
    }

    private void TickPrestage()
    {
        var st = prestage!;
        var now = Environment.TickCount64;

        switch (st.Phase)
        {
            case PrestagePhase.Closing:
                if (IsAddonVisible("MiragePrismPrismBox"))
                {
                    if (now - st.PhaseAt > 2000)
                    {
                        if (++st.CloseAttempts > 3)
                        {
                            Stop("환상의 옷장을 닫지 못했습니다. 옷장을 직접 닫고 3번 탭으로 장비를 가방에 옮긴 뒤 다시 시도해 주세요.");
                            return;
                        }

                        CloseDresser();
                        st.PhaseAt = now;
                    }
                    return;
                }

                if (st.ClosedAt == 0)
                {
                    st.ClosedAt = now;
                    return;
                }

                // The window is gone, but the game may still be finishing the event; moving too early is refused.
                var occupied = condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied];
                if (occupied && now - st.ClosedAt < 4000)
                    return;
                if (now - st.ClosedAt < 800)
                    return;

                Debug("s2 prestage: event ended {Ms} ms after close (occupied={Occ})", now - st.ClosedAt, occupied);
                st.Phase = PrestagePhase.Moving;
                st.PhaseAt = now;
                Status = $"환상의 옷장을 닫았습니다. 장비함 조각 {st.Total}개를 가방으로 옮기는 중…";
                return;

            case PrestagePhase.Moving:
            {
                if (st.Current is { } cur)
                {
                    if (scanner.TryResolve(cur.Dest, cur.Entry.ItemId, out _))
                    {
                        st.Moved[cur.Entry.Key] = cur.Dest;
                        Debug("s2 prestage moved {Name} {From} -> {To}", cur.Entry.Name, cur.Entry.Key.Id, cur.Dest.Id);
                        st.Current = null;
                        st.PhaseAt = now;
                    }
                    else if (now - cur.At > MoveTimeoutMs)
                    {
                        Skipped++;
                        LastItemStatus = $"{cur.Entry.Name}: 가방으로 옮긴 결과를 확인하지 못해 건너뜁니다.";
                        log.Warning("[KWC] s2 prestage move not confirmed {Name} {From} -> {To}", cur.Entry.Name, cur.Entry.Key.Id, cur.Dest.Id);
                        st.Current = null;
                        st.PhaseAt = now;
                    }
                    return;
                }

                if (now - st.PhaseAt < PrestageMoveIntervalMs)
                    return;

                if (st.ToMove.TryDequeue(out var next))
                {
                    var inventory = InventoryManager.Instance();
                    if (inventory == null || !scanner.TryResolve(next.Key, next.ItemId, out _))
                    {
                        Skipped++;
                        LastItemStatus = $"{next.Name}: 위치가 바뀌어 건너뜁니다.";
                        return;
                    }

                    if (!scanner.TryFindFirstEmptyBag(out var dest))
                    {
                        Skipped++;
                        LastItemStatus = $"{next.Name}: 가방 빈칸이 없어 건너뜁니다.";
                        return;
                    }

                    var rc = inventory->MoveItemSlot(next.Key.Container, (ushort)next.Key.Slot, dest.Container, (ushort)dest.Slot, true);
                    Debug("s2 prestage move {Name} {From} -> {To}: rc={Rc}", next.Name, next.Key.Id, dest.Id, rc);
                    if (rc == 10)
                    {
                        Stop("게임이 아직 아이템 이동을 막고 있습니다(옷장이나 다른 창이 열려 있음). 창을 닫고 다시 시도해 주세요.");
                        return;
                    }

                    if (rc != 0)
                    {
                        Failed++;
                        LastItemStatus = $"{next.Name}: 가방으로 옮기지 못했습니다 ({rc}).";
                        return;
                    }

                    st.Current = (next, dest, now);
                    Status = $"가방으로 옮기는 중: {st.Moved.Count + 1}/{st.Total}";
                    return;
                }

                RebuildQueuesAfterPrestage(st.Moved);
                st.Phase = PrestagePhase.Reopen;
                st.PhaseAt = now;
                return;
            }

            case PrestagePhase.Reopen:
                if (config.Stage2AutoReopenDresser && TryInteractDresser(st))
                {
                    st.ReopenRequested = true;
                    st.ReopenAttempts++;
                    Status = $"장비함 조각 {st.Moved.Count}개를 가방으로 옮겼습니다. 환상의 옷장을 다시 여는 중… (장비함 대기 {DeferredCount}개)";
                }
                else
                {
                    Status = $"장비함 조각 {st.Moved.Count}개를 가방으로 옮겼습니다. 환상의 옷장을 다시 열어 주세요 — 열리면 자동으로 이어서 저장합니다.";
                }

                st.Phase = PrestagePhase.WaitDresser;
                st.PhaseAt = now;
                return;

            case PrestagePhase.WaitDresser:
                if (now - st.LastWaitLog > 2000)
                {
                    st.LastWaitLog = now;
                    var mm = MirageManager.Instance();
                    Debug("s2 prestage waiting: prismBoxLoaded={Loaded} dresserWindow={Box} depositList={List} occupied={Occ}",
                        mm != null && mm->PrismBoxLoaded, IsAddonVisible("MiragePrismPrismBox"), IsAddonVisible("MiragePrismPrismBoxCrystallize"),
                        condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied]);
                }

                if (!IsDresserReady())
                    st.ReadySince = 0;
                else if (st.ReadySince == 0)
                {
                    st.ReadySince = now;
                    return;
                }

                if (IsDresserReady() && now - st.ReadySince >= 600)
                {
                    Debug("s2 prestage done: dresser ready again, sets={Sets} singles={Singles}", setQueue.Count, singleQueue.Count);
                    prestage = null;
                    nextSetActionAt = now + 500;
                    Status = $"환상의 옷장이 열렸습니다. 이어서 저장합니다 (세트 {setQueue.Count}개 · 단벌 {singleQueue.Count}개).";
                    return;
                }

                if (st.ReopenRequested && now - st.PhaseAt > ReopenRetryMs)
                {
                    st.PhaseAt = now;
                    if (config.Stage2AutoReopenDresser && st.ReopenAttempts < 2 && TryInteractDresser(st))
                        st.ReopenAttempts++;
                    else
                        Status = "환상의 옷장이 열리지 않았습니다. 직접 열어 주세요 — 열리면 자동으로 이어서 저장합니다.";
                }
                return;
        }
    }

    /// Re-queues the deferred work whose armoury pieces were moved in this round; the rest stays deferred.
    private void RebuildQueuesAfterPrestage(Dictionary<ItemLocationKey, ItemLocationKey> moved)
    {
        SetPiece Remap(SetPiece p) => moved.TryGetValue(p.Entry.Key, out var d) ? p with { Entry = p.Entry with { Key = d } } : p;

        var stillSets = new List<SetBatch>();
        foreach (var b in plannedSets)
        {
            var armory = b.Pieces.Where(p => IsArmory(p.Entry.Key.Container)).ToList();
            var movedCount = armory.Count(p => moved.ContainsKey(p.Entry.Key));
            if (movedCount == 0)
            {
                stillSets.Add(b);
                continue;
            }

            // Batches are moved as a whole; a piece can only be missing here when its move failed.
            var pieces = b.Pieces.Where(p => !IsArmory(p.Entry.Key.Container) || moved.ContainsKey(p.Entry.Key)).Select(Remap).ToList();
            var lost = armory.Count - movedCount;
            if (lost > 0)
            {
                Skipped += lost;
                Debug("s2 round: set '{Set}' lost {Lost} pieces whose move failed", b.Set.Name, lost);
            }

            if (pieces.Count >= 1) setQueue.Enqueue(new SetBatch(b.Set, pieces, b.ExistingIndex, b.FallbackToSingles));
        }
        plannedSets = stillSets;

        var stillSingles = new List<PhysicalGearEntry>();
        foreach (var s in plannedSingles)
        {
            if (moved.TryGetValue(s.Key, out var d)) singleQueue.Enqueue(s with { Key = d });
            else stillSingles.Add(s);
        }
        plannedSingles = stillSingles;

        Debug("s2 round: queued sets={Sets} singles={Singles}, still deferred {Deferred}", setQueue.Count, singleQueue.Count, DeferredCount);
    }

    private void CloseDresser()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("MiragePrismPrismBox").Address;
        if (addon != null && addon->IsVisible)
        {
            Debug("s2 prestage: closing dresser window");
            addon->Close(true);
        }
    }

    private bool IsAddonVisible(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible;
    }

    /// Nearest glamour dresser object (EObj 2009439, or any event object named like "옷장") within reach.
    private ulong FindDresserObjectId()
    {
        var player = objects.LocalPlayer;
        if (player == null) return 0;

        IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in objects)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
            var isDresser = obj.BaseId == GlamourDresserEObj || obj.Name.TextValue.Contains("옷장", StringComparison.Ordinal);
            if (!isDresser) continue;

            var d = Vector3.Distance(obj.Position, player.Position);
            if (d < bestDist) { bestDist = d; best = obj; }
        }

        if (best == null || bestDist > 12f)
        {
            Debug("s2 prestage: no dresser object nearby (best {Dist})", bestDist);
            return 0;
        }

        Debug("s2 prestage: dresser object {Name} id={Id} dataId={DataId} dist={Dist:F1}", best.Name.TextValue, best.GameObjectId, best.BaseId, bestDist);
        return best.GameObjectId;
    }

    private bool TryInteractDresser(PrestageState st)
    {
        if (st.DresserObjectId == 0) return false;
        var obj = objects.SearchById(st.DresserObjectId);
        if (obj == null) return false;

        var target = TargetSystem.Instance();
        if (target == null) return false;

        var result = target->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
        Debug("s2 prestage: interact with dresser {Id} -> {Result}", st.DresserObjectId, result);
        return true;
    }

    // ---- staging: the dresser only takes items from the bags, so armoury pieces are moved first ----

    private void BeginStaging(SetBatch batch)
    {
        stagingBatch = batch;
        stagedPieces = [];
        stagingToMove.Clear();
        stagingMove = null;

        var free = scanner.CountFreeBagSlots();
        foreach (var piece in batch.Pieces)
        {
            if (!IsArmory(piece.Entry.Key.Container))
            {
                stagedPieces.Add(piece);
                continue;
            }

            if (free <= 0)
            {
                Skipped++;
                LastItemStatus = $"{piece.Entry.Name}: 장비함에서 가방으로 옮길 빈칸이 없어 건너뜁니다.";
                Debug("s2 staging '{Set}' drop {Name} {Loc}: no free bag slot", batch.Set.Name, piece.Entry.Name, piece.Entry.Key.Id);
                continue;
            }

            free--;
            stagingToMove.Enqueue(piece);
        }

        if (stagingToMove.Count > 0)
            LastItemStatus = $"{batch.Set.Name}: 장비함 조각 {stagingToMove.Count}개를 가방으로 옮기는 중…";
        TickStaging();
    }

    private void TickStaging()
    {
        var batch = stagingBatch!;
        var now = Environment.TickCount64;

        if (stagingMove is { } move)
        {
            if (scanner.TryResolve(move.Dest, move.Piece.Entry.ItemId, out _))
            {
                stagedPieces.Add(move.Piece with { Entry = move.Piece.Entry with { Key = move.Dest } });
                Debug("s2 staging '{Set}' moved {Name} {From} -> {To}", batch.Set.Name, move.Piece.Entry.Name, move.Piece.Entry.Key.Id, move.Dest.Id);
                stagingMove = null;
                nextSetActionAt = now + 250;
            }
            else if (now - move.At > MoveTimeoutMs)
            {
                Skipped++;
                LastItemStatus = $"{move.Piece.Entry.Name}: 가방으로 옮긴 결과를 확인하지 못해 건너뜁니다.";
                log.Warning("[KWC] s2 staging move not confirmed {Name} {From} -> {To}", move.Piece.Entry.Name, move.Piece.Entry.Key.Id, move.Dest.Id);
                stagingMove = null;
            }
            return;
        }

        if (now < nextSetActionAt)
            return;

        if (stagingToMove.TryDequeue(out var next))
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null || !scanner.TryResolve(next.Entry.Key, next.Entry.ItemId, out _))
            {
                Skipped++;
                LastItemStatus = $"{next.Entry.Name}: 위치가 바뀌어 건너뜁니다.";
                return;
            }

            if (!scanner.TryFindFirstEmptyBag(out var dest))
            {
                Skipped++;
                LastItemStatus = $"{next.Entry.Name}: 가방 빈칸이 없어 건너뜁니다.";
                return;
            }

            var rc = inventory->MoveItemSlot(next.Entry.Key.Container, (ushort)next.Entry.Key.Slot, dest.Container, (ushort)dest.Slot, true);
            Debug("s2 staging '{Set}' move {Name} {From} -> {To}: rc={Rc}", batch.Set.Name, next.Entry.Name, next.Entry.Key.Id, dest.Id, rc);
            if (rc != 0)
            {
                Failed++;
                LastItemStatus = rc == 10
                    ? $"{next.Entry.Name}: 옷장이 열린 상태라 게임이 이동을 막았습니다. 가방 빈칸을 늘리고 다시 시도해 주세요."
                    : $"{next.Entry.Name}: 가방으로 옮기지 못했습니다 ({rc}).";
                return;
            }

            stagingMove = (next, dest, now);
            return;
        }

        // Everything that could be staged is in the bags now.
        stagingBatch = null;
        StoreSetBatch(new SetBatch(batch.Set, stagedPieces, batch.ExistingIndex, batch.FallbackToSingles));
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

        const int minimum = 1;
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
            pendingBatch = new(batch.Set, sent, Environment.TickCount64, batch.FallbackToSingles);
            LastItemStatus = $"{batch.Set.Name}: {sent.Count}개 세트 저장 요청, 게임 반영을 확인하는 중…";
        }
        else if (batch.FallbackToSingles)
        {
            foreach (var p in sent) singleQueue.Enqueue(p.Entry);
            LastItemStatus = $"{batch.Set.Name}: 세트 저장 요청이 거절되어 {sent.Count}개를 단벌로 다시 시도합니다.";
            Debug("s2 store rejected for '{Set}', {Count} pieces requeued as singles", batch.Set.Name, sent.Count);
            nextSetActionAt = Environment.TickCount64 + 300;
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
        var names = string.Join(", ", remaining.Select(p => p.Entry.Name));
        if (pb.FallbackToSingles)
        {
            foreach (var p in remaining) singleQueue.Enqueue(p.Entry);
            LastItemStatus = $"{pb.Set.Name}: {done}개 저장 확인, {remaining.Count}개는 세트로 받지 않아 단벌로 다시 시도합니다 ({names}).";
            Debug("s2 set '{Set}' not accepted for {Count} pieces, requeued as singles", pb.Set.Name, remaining.Count);
            pendingBatch = null;
            nextSetActionAt = now + 300;
            return;
        }

        Failed += remaining.Count;
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

        // Only after the deposit was requested does "gone from the bag" mean "stored".
        if (singleState is SingleStoreState.Confirm or SingleStoreState.Wait && !scanner.TryResolve(entry.Key, entry.ItemId, out _))
        {
            Stored++;
            LastItemStatus = $"{entry.Name}: 인벤토리에서 사라져 저장 완료로 확인했습니다.";
            Debug("s2 single stored {Name} {Loc}", entry.Name, entry.Key.Id);
            FinishSingle();
            return;
        }

        if (now - pendingSingleAt > SingleTimeoutMs)
        {
            Failed++;
            LastItemStatus = $"{entry.Name}: 단벌 저장 완료를 확인하지 못해 건너뜁니다.";
            log.Warning("[KWC] s2 single NOT stored {Name} #{Id} {Loc} state={State}", entry.Name, entry.ItemId, entry.Key.Id, singleState);
            FinishSingle();
            return;
        }

        switch (singleState)
        {
            case SingleStoreState.Select:
            {
                if (!scanner.TryResolve(entry.Key, entry.ItemId, out _))
                {
                    Skipped++;
                    LastItemStatus = $"{entry.Name}: 위치가 바뀌어 건너뜁니다.";
                    FinishSingle();
                    return;
                }

                // The deposit list only contains bag items: move armoury gear to a bag first.
                if (IsArmory(entry.Key.Container))
                {
                    var inventory = InventoryManager.Instance();
                    if (inventory == null || !scanner.TryFindFirstEmptyBag(out var dest))
                    {
                        Skipped++;
                        LastItemStatus = $"{entry.Name}: 장비함에서 가방으로 옮길 빈칸이 없어 건너뜁니다.";
                        Debug("s2 single {Name} {Loc}: no free bag slot for staging", entry.Name, entry.Key.Id);
                        FinishSingle();
                        return;
                    }

                    var rc = inventory->MoveItemSlot(entry.Key.Container, (ushort)entry.Key.Slot, dest.Container, (ushort)dest.Slot, true);
                    Debug("s2 single stage {Name} {From} -> {To}: rc={Rc}", entry.Name, entry.Key.Id, dest.Id, rc);
                    if (rc != 0)
                    {
                        Failed++;
                        LastItemStatus = rc == 10
                            ? $"{entry.Name}: 옷장이 열린 상태라 게임이 이동을 막았습니다. 가방 빈칸을 늘리고 다시 시도해 주세요."
                            : $"{entry.Name}: 가방으로 옮기지 못했습니다 ({rc}).";
                        FinishSingle();
                        return;
                    }

                    singleMoveDest = dest;
                    singleMoveAt = now;
                    singleState = SingleStoreState.WaitMove;
                    LastItemStatus = $"{entry.Name}: 장비함 → 가방으로 옮기는 중…";
                    return;
                }

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
                    // The game refreshes the list a little after the inventory changes; nudge it once, then give up.
                    if (now - listWaitStart < ListWaitMs)
                    {
                        if (!listRefreshRequested && now - listWaitStart > ListRefreshAfterMs)
                        {
                            listRefreshRequested = true;
                            agent->PopulateCrystallizeAndFireRefresh();
                            Debug("s2 single {Name} {Loc}: requested deposit list refresh (count={Count})", entry.Name, entry.Key.Id, data->CrystallizeItemCount);
                        }
                        return;
                    }

                    Skipped++;
                    LastItemStatus = $"{entry.Name}: 게임의 맡기기 목록에서 찾지 못해 건너뜁니다.";
                    Debug("s2 single {Name} {Loc} not in game deposit list (count={Count})", entry.Name, entry.Key.Id, data->CrystallizeItemCount);
                    FinishSingle();
                    return;
                }

                if (!FireCallback("MiragePrismPrismBoxCrystallize", 0, row))
                    return;

                confirmStartedAt = now;
                singleState = SingleStoreState.Confirm;
                return;
            }

            case SingleStoreState.WaitMove:
                if (scanner.TryResolve(singleMoveDest, entry.ItemId, out _))
                {
                    pendingSingle = entry with { Key = singleMoveDest };
                    listWaitStart = now;
                    listRefreshRequested = false;
                    singleState = SingleStoreState.Select;
                    Debug("s2 single staged {Name} now at {Loc}", entry.Name, singleMoveDest.Id);
                }
                else if (now - singleMoveAt > MoveTimeoutMs)
                {
                    Failed++;
                    LastItemStatus = $"{entry.Name}: 가방으로 옮긴 결과를 확인하지 못해 건너뜁니다.";
                    log.Warning("[KWC] s2 single staging not confirmed {Name} {From} -> {To}", entry.Name, entry.Key.Id, singleMoveDest.Id);
                    FinishSingle();
                }
                return;

            case SingleStoreState.Confirm:
                if (TryConfirmYesNoForItem(entry.Name, now - confirmStartedAt))
                    singleState = SingleStoreState.Wait;
                return;

            case SingleStoreState.Wait:
                return;
        }
    }

    private void FinishSingle()
    {
        pendingSingle = null;
        singleState = SingleStoreState.None;
    }

    private static bool IsArmory(InventoryType type) => Array.IndexOf(InventoryScanner.ArmoryContainers, type) >= 0;

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

    /// Confirms the deposit prompt. Prefers a prompt that names the item; after a short grace period a
    /// deposit prompt ("맡기") that appeared for our request is accepted too. Unmatched prompts are logged.
    private bool TryConfirmYesNoForItem(string expectedItemName, long elapsedMs)
    {
        for (var i = 1; i <= 4; i++)
        {
            var yesno = (AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno", i).Address;
            if (yesno == null || !yesno->AtkUnitBase.IsVisible || !yesno->AtkUnitBase.IsReady)
                continue;

            var prompt = yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(prompt))
                continue;

            var byName = prompt.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase);
            var byKeyword = elapsedMs > 600 && prompt.Contains("맡기", StringComparison.Ordinal);
            if (!byName && !byKeyword)
            {
                if (lastPromptLogged != prompt)
                {
                    lastPromptLogged = prompt;
                    Debug("s2 yes/no prompt not matched for {Name}: \"{Prompt}\"", expectedItemName, prompt.Replace("\n", " "));
                }
                continue;
            }

            Debug("s2 confirm {Name} via {How}: \"{Prompt}\"", expectedItemName, byName ? "name" : "keyword", prompt.Replace("\n", " "));
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
    private sealed record SetBatch(OutfitCatalog.SetInfo Set, IReadOnlyList<SetPiece> Pieces, uint? ExistingIndex, bool FallbackToSingles = false);
    private sealed record PendingBatch(OutfitCatalog.SetInfo Set, IReadOnlyList<SetPiece> Pieces, long SentAt, bool FallbackToSingles = false);
    private enum SingleStoreState { None, Select, WaitMove, Confirm, Wait }
}
