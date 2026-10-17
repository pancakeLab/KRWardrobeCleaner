using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed class Plugin : IDalamudPlugin
{
    private const string VersionLabel = "v0.11.4";
    private static readonly string[] CommandNames = ["/kwc", "/가방"];

    private static readonly Vector4 ColorOk = new(0.60f, 0.90f, 0.60f, 1f);
    private static readonly Vector4 ColorBusy = new(1.00f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 ColorWarn = new(1.00f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ColorMuted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 ColorTag = new(0.55f, 0.80f, 1.00f, 1f);

    private static readonly (string Group, string[] Jobs)[] JobGroups =
    [
        ("탱커", ["PLD", "WAR", "DRK", "GNB"]),
        ("힐러", ["WHM", "SCH", "AST", "SGE"]),
        ("근접", ["MNK", "DRG", "NIN", "SAM", "RPR", "VPR"]),
        ("원거리", ["BRD", "MCH", "DNC"]),
        ("마법", ["BLM", "SMN", "RDM", "PCT", "BLU"]),
        ("제작", ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"]),
        ("채집", ["MIN", "BTN", "FSH"]),
    ];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private readonly Configuration config;
    private readonly GlamourStateCache glamourCache;
    private readonly DresserSnapshot dresserSnapshot;
    private readonly GearFilter filter;
    private readonly Stage1DresserToInventory stage1;
    private readonly DresserDuplicateCleaner duplicates;
    private readonly Stage2InventoryToDresser stage2;
    private readonly GearTransfer transfer;
    private readonly Stage4Discard stage4;

    private bool windowOpen = true;
    private bool filterPanelOpen = true;
    private readonly ListView list1 = new(), listDup = new(), list2 = new(), list3 = new(), list4 = new();
    private int pendingDiscard; // 0 none, 1 selected, 2 bag4

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var excel = new ExcelIndex(Data);
        var scanner = new InventoryScanner(excel);
        var outfits = new OutfitCatalog(Data, excel);

        glamourCache = new GlamourStateCache();
        dresserSnapshot = new DresserSnapshot(outfits);
        filter = new GearFilter(excel, config, dresserSnapshot);
        stage1 = new Stage1DresserToInventory(excel, glamourCache, GameGui, config, SaveConfig);
        duplicates = new DresserDuplicateCleaner(excel, outfits, GameGui, Log, config, SaveConfig);
        stage2 = new Stage2InventoryToDresser(excel, scanner, outfits, filter, GameGui, Objects, Condition, Log, config, SaveConfig);
        transfer = new GearTransfer(excel, scanner, filter, Log, config, SaveConfig);
        stage4 = new Stage4Discard(scanner, GameGui, config, SaveConfig);

        foreach (var command in CommandNames)
        {
            Commands.AddHandler(command, new CommandInfo((_, _) => windowOpen = !windowOpen)
            {
                HelpMessage = "히메짱 옷장 정리기를 엽니다.",
            });
        }

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += OnFrameworkUpdate;

        Log.Information("[KWC] 히메짱 옷장 정리기 {Version} loaded. outfit sets={Sets} (sheet columns {Cols}), jobs={Jobs}, debugLog={Debug}",
            VersionLabel, outfits.Count, outfits.SheetColumns, excel.Jobs.Count, config.DebugLog);
    }

    public void Dispose()
    {
        const string reason = "플러그인이 종료되어 작업을 중지했습니다.";
        stage1.Stop(reason);
        duplicates.Stop(reason);
        stage2.Stop(reason);
        transfer.Stop(reason);
        stage4.Stop(reason);

        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        foreach (var command in CommandNames)
            Commands.RemoveHandler(command);
    }

    private void Open() => windowOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        glamourCache.Observe();
        dresserSnapshot.Observe();
        stage1.Tick();
        duplicates.Tick();
        stage2.Tick();
        transfer.Tick();
        stage4.Tick();
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private bool AnyRunning =>
        stage1.IsRunning || duplicates.IsRunning || stage2.IsRunning || transfer.IsRunning || stage4.IsRunning;

    private void StopAll()
    {
        stage1.Stop();
        duplicates.Stop();
        stage2.Stop();
        transfer.Stop();
        stage4.Stop();
    }

    private void FilterChanged()
    {
        SaveConfig();
        stage2.ApplyFilters();
        transfer.ApplyFilters();
    }

    // ------------------------------------------------------------------ window

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(980, 840), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(720, 480), new Vector2(float.MaxValue, float.MaxValue));
        if (!ImGui.Begin($"히메짱 옷장 정리기 {VersionLabel}###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawTopBar();

        if (ImGui.BeginTabBar("KWCStages", ImGuiTabBarFlags.None))
        {
            DrawTab("1. 옷장 → 가방", DrawStage1, "환상의 옷장에서 추억의 보관함 대상 아이템을 가방으로 꺼냅니다.");
            DrawTab("1-2. 옷장 중복", DrawDuplicates, "환상의 옷장에 두 벌 이상 든 아이템을 찾아 중복분을 가방으로 꺼냅니다.");
            DrawTab("2. 가방 → 옷장", DrawStage2, "장비함·가방의 장비를 환상의 옷장(세트 우선)에 넣습니다.");
            DrawTab("3. 장비함 ↔ 가방", DrawTransfer, "장비함과 가방 사이에서 장비를 옮깁니다.");
            DrawTab("4. 자동 파기", DrawStage4, "체크한 장비 또는 4번 가방 전체를 파기합니다.");
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static void DrawTab(string title, System.Action body, string tooltip)
    {
        var open = ImGui.BeginTabItem(title);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        if (!open) return;
        ImGui.Spacing();
        body();
        ImGui.EndTabItem();
    }

    private void DrawTopBar()
    {
        var debug = config.DebugLog;
        if (ImGui.Checkbox("디버그 로그##dbg", ref debug))
        {
            config.DebugLog = debug;
            SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("/xllog 에 [KWC] 접두어로 후보 판정·요청·검증 결과를 남깁니다.");

        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "|");
        ImGui.SameLine();
        ImGui.TextColored(glamourCache.HasPlateData ? ColorMuted : ColorWarn, $"투영세트 캐시 {(glamourCache.HasPlateData ? $"{glamourCache.PlateItemCount}종" : "없음")}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("1번 탭의 '투영세트 등록중' 판정에 쓰입니다. 투영세트 편집 화면을 한 번 열면 채워집니다.");
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "·");
        ImGui.SameLine();
        ImGui.TextColored(dresserSnapshot.HasData ? ColorMuted : ColorWarn, $"옷장 캐시 {(dresserSnapshot.HasData ? $"{dresserSnapshot.CapturedAt:HH:mm:ss}" : "없음")}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("필터의 '환상의 옷장 보유' 판정에 쓰입니다. 환상의 옷장을 열면 1초마다 갱신되고 닫아도 유지됩니다.");

        if (AnyRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorBusy, "● 작업 진행 중");
            ImGui.SameLine();
            if (ImGui.Button("전체 중지##stopall"))
                StopAll();
        }

        ImGui.Separator();
    }

    // ------------------------------------------------------------------ shared pieces

    private sealed class ListView
    {
        public string Search = string.Empty;
        public bool OnlyChecked;

        public bool Show(string name, bool isChecked)
        {
            if (OnlyChecked && !isChecked) return false;
            return Search.Length == 0 || name.Contains(Search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void Help(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void StatusLine(string status, string? last, bool running, int done = 0, int total = 0)
    {
        if (running && total > 0)
            ImGui.ProgressBar(Math.Clamp(done / (float)total, 0f, 1f), new Vector2(-1, 0), $"{done} / {total}");

        var color = running ? ColorBusy
            : status.StartsWith("완료") ? ColorOk
            : status.Contains("없습니다") || status.Contains("실패") || status.Contains("중지") || status.Contains("주세요") ? ColorWarn
            : ColorMuted;
        ImGui.TextColored(color, status);
        if (!string.IsNullOrWhiteSpace(last))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextWrapped($"└ {last}");
            ImGui.PopStyleColor();
        }
    }

    private static void ListToolbar(ListView view, int checkedCount, int total)
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "이름으로 찾기", ref view.Search, 64);
        ImGui.SameLine();
        ImGui.Checkbox("체크한 것만 보기##onlychecked", ref view.OnlyChecked);
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, $"체크 {checkedCount} / 전체 {total}");
    }

    private static bool BeginList(string id, params (string Title, float Width)[] columns)
    {
        var avail = ImGui.GetContentRegionAvail();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersOuter;
        if (!ImGui.BeginTable(id, columns.Length, flags, new Vector2(0, Math.Max(avail.Y, 120))))
            return false;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var (title, width) in columns)
        {
            if (width > 0) ImGui.TableSetupColumn(title, ImGuiTableColumnFlags.WidthFixed, width);
            else ImGui.TableSetupColumn(title, ImGuiTableColumnFlags.WidthStretch, -width);
        }
        ImGui.TableHeadersRow();
        return true;
    }

    private static void Tags(params (bool Show, string Text)[] tags)
    {
        var first = true;
        foreach (var (show, text) in tags)
        {
            if (!show) continue;
            if (!first) ImGui.SameLine();
            ImGui.TextColored(ColorTag, text);
            first = false;
        }
    }

    private static string Where(InventoryType type)
        => type switch
        {
            InventoryType.Inventory1 => "가방1",
            InventoryType.Inventory2 => "가방2",
            InventoryType.Inventory3 => "가방3",
            InventoryType.Inventory4 => "가방4",
            _ => $"장비함·{GearTransfer.CategoryLabel(type)}",
        };

    private bool CanStart(bool thisRunning) => !thisRunning && !AnyRunning;

    private static bool PrimaryButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.70f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.28f, 0.55f, 0.82f, 1f));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    private static bool DangerButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.20f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.25f, 0.25f, 1f));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    // ------------------------------------------------------------------ 1. 옷장 → 가방

    private void DrawStage1()
    {
        Help("추억의 보관함에 넣을 수 있는 아이템이 환상의 옷장에 들어 있으면 가방으로 꺼냅니다. 환상의 옷장을 연 상태에서 쓰세요.");

        if (!stage1.IsRunning)
        {
            if (ImGui.Button("후보 검색##s1scan")) stage1.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s1all")) stage1.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 해제##s1none")) stage1.SelectAll(false);
            ImGui.SameLine();
            if (PrimaryButton($"가방으로 꺼내기 ({stage1.Selected.Count}개)##s1go") && CanStart(stage1.IsRunning))
                stage1.Start();

            var dyed = config.Stage1IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 꺼내기##s1dyed", ref dyed))
                stage1.UpdateOptions(dyed, config.Stage1IncludePlateRegistered);
            ImGui.SameLine();
            var plate = config.Stage1IncludePlateRegistered;
            if (ImGui.Checkbox("투영세트에 등록 중인 아이템도 꺼내기##s1plate", ref plate))
                stage1.UpdateOptions(config.Stage1IncludeDyed, plate);
        }
        else if (ImGui.Button("중지##s1stop"))
        {
            stage1.Stop();
        }

        StatusLine(stage1.Status, stage1.LastItemStatus, stage1.IsRunning, stage1.Done, stage1.Total);
        ListToolbar(list1, stage1.Selected.Count, stage1.Entries.Count);

        if (!BeginList("Stage1List", ("", 28), ("아이템", -3), ("비고", -1.2f))) return;
        foreach (var entry in stage1.Entries)
        {
            var selected = stage1.Selected.Contains(entry.DresserSlot);
            if (!list1.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##s1-{entry.DresserSlot}", ref selected))
                stage1.SetSelected(entry.DresserSlot, selected);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            Tags((entry.IsDyed, "염색됨"), (entry.UsedOnPlate, "투영세트 등록중"));
        }
        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ 1-2. 옷장 중복

    private void DrawDuplicates()
    {
        Help("환상의 옷장에 같은 아이템이 두 벌 이상 있거나, 의상 세트에 든 조각이 따로 또 들어 있는 경우를 찾아 중복분을 가방으로 꺼냅니다. '중복분만 체크'는 아이템마다 한 벌(염색된 것 우선, 세트에 있으면 세트 쪽)을 남깁니다.");

        if (!duplicates.IsRunning)
        {
            if (ImGui.Button("중복 검색##dupscan")) duplicates.Scan();
            ImGui.SameLine();
            if (ImGui.Button("중복분만 체크##dupsurplus")) duplicates.SelectSurplusOnly();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##dupall")) duplicates.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 해제##dupnone")) duplicates.SelectAll(false);
            ImGui.SameLine();
            if (PrimaryButton($"가방으로 꺼내기 ({duplicates.Selected.Count}개)##dupgo") && CanStart(duplicates.IsRunning))
                duplicates.Start();

            var keepDyed = config.DuplicateKeepDyedFirst;
            if (ImGui.Checkbox("염색된 쪽을 남기고 염색 안 된 쪽을 중복분으로 본다##dupdyed", ref keepDyed))
                duplicates.UpdateKeepDyedFirst(keepDyed);
        }
        else if (ImGui.Button("중지##dupstop"))
        {
            duplicates.Stop();
        }

        StatusLine(duplicates.Status, duplicates.LastItemStatus, duplicates.IsRunning, duplicates.Done, duplicates.Total);
        ListToolbar(listDup, duplicates.Selected.Count, duplicates.Entries.Count);

        if (!BeginList("DupList", ("", 28), ("아이템", -3), ("벌 수", 60), ("판정", 90), ("비고", -1.2f))) return;
        foreach (var entry in duplicates.Entries)
        {
            var selected = duplicates.Selected.Contains(entry.DresserSlot);
            if (!listDup.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##dup-{entry.DresserSlot}", ref selected))
                duplicates.SetSelected(entry.DresserSlot, selected);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{entry.GroupSize}{(entry.AlsoInSet ? "+세트" : "")}");
            ImGui.TableNextColumn();
            if (entry.IsKeep) ImGui.TextColored(ColorOk, "남길 것");
            else ImGui.TextColored(ColorWarn, "중복분");
            ImGui.TableNextColumn();
            Tags((entry.IsDyed, "염색됨"), (entry.AlsoInSet, "세트에도 있음"));
        }
        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ 2. 가방 → 옷장

    private void DrawStage2()
    {
        Help("장비함과 가방에서 환상의 옷장에 넣을 수 있는 장비를 찾아, 기존 의상 세트 보충 → 세트 우선 저장 → 단벌 저장 순으로 넣습니다. 환상의 옷장과 '아이템 맡기기' 목록을 연 상태에서 쓰세요. 내구도 100% 미만은 게임이 받지 않아 자동 제외합니다. 게임은 가방에 있는 장비만 맡길 수 있고 옷장이 열린 동안은 장비함에서 꺼낼 수도 없어서, 가방에 있는 장비를 먼저 다 넣은 뒤, 장비함 장비는 옷장을 잠시 닫고 가방으로 옮겨 다시 열어 넣기를 빈칸이 생기는 만큼 반복합니다.");

        var running = stage2.IsRunning;
        if (running) ImGui.BeginDisabled();

        DrawFilterPanel();

        if (ImGui.Button("후보 검색##s2scan")) stage2.Scan();
        ImGui.SameLine();
        if (ImGui.Button("기존 세트에 넣을 수 있는 것만 체크##s2sets")) stage2.SelectExistingSetCandidates();
        ImGui.SameLine();
        if (ImGui.Button("전체 체크##s2all")) stage2.SelectAll(true);
        ImGui.SameLine();
        if (ImGui.Button("전체 해제##s2none")) stage2.SelectAll(false);

        var dyed = config.Stage2IncludeDyed;
        if (ImGui.Checkbox("염색된 아이템도 넣기##s2dyed", ref dyed))
        {
            stage2.UpdateIncludeDyed(dyed);
            stage2.ApplyFilters();
        }
        ImGui.SameLine();
        var materia = config.Stage2IncludeMateria;
        if (ImGui.Checkbox("마테리아 장착 아이템도 시도##s2materia", ref materia))
            stage2.UpdateIncludeMateria(materia);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("게임이 거절하면 실패로 기록됩니다.");
        ImGui.SameLine();
        var reopen = config.Stage2AutoReopenDresser;
        if (ImGui.Checkbox("옷장 자동 다시 열기##s2reopen", ref reopen)) { config.Stage2AutoReopenDresser = reopen; SaveConfig(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("장비함 조각이 있으면 옷장을 잠시 닫고 가방으로 옮긴 뒤, 근처의 환상의 옷장과 상호작용해 다시 엽니다. 끄면 옮긴 뒤 직접 열어 주세요(열리면 자동으로 이어서 저장).");

        if (PrimaryButton($"기존 세트에만 넣기 ({stage2.Selected.Count}개)##s2go1") && CanStart(stage2.IsRunning))
            stage2.StartExistingSetsOnly();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("옷장에 이미 있는 의상 세트의 빈 조각만 채웁니다.");
        ImGui.SameLine();
        if (PrimaryButton($"세트화 가능한 것만 넣기 ({stage2.SelectedWithOutfit}개)##s2go3") && CanStart(stage2.IsRunning))
            stage2.StartSetsOnly();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("의상 세트가 있는 장비만 넣습니다(기존 세트 보충 + 새 세트, 조각 1개부터). 세트가 없는 장비는 그대로 둡니다.");
        ImGui.SameLine();
        if (PrimaryButton($"세트 우선, 나머지는 단벌로 넣기 ({stage2.Selected.Count}개)##s2go2") && CanStart(stage2.IsRunning))
            stage2.StartSetsThenSingles();

        if (running) ImGui.EndDisabled();
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("중지##s2stop")) stage2.Stop();
        }

        StatusLine(stage2.Status, stage2.LastItemStatus, stage2.IsRunning, stage2.Done, stage2.Total);
        ListToolbar(list2, stage2.Selected.Count, stage2.Entries.Count);

        if (!BeginList("Stage2List", ("", 28), ("아이템", -3), ("위치", 110), ("Lv", 40), ("비고", -1.6f))) return;
        foreach (var entry in stage2.Entries)
        {
            var reason = stage2.BlockReason(entry.Key);
            var selected = reason == null && stage2.Selected.Contains(entry.Key);
            if (!list2.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (reason == null && ImGui.Checkbox($"##s2-{entry.Key.Id}", ref selected))
                stage2.SetSelected(entry.Key, selected);
            ImGui.TableNextColumn();
            if (reason != null) ImGui.TextDisabled(entry.Name);
            else if (stage2.IsMatched(entry.Key)) ImGui.TextColored(ColorOk, entry.Name);
            else ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Where(entry.Key.Container));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            if (reason != null) ImGui.TextColored(ColorWarn, reason);
            Tags((stage2.HasOutfit(entry.ItemId), "세트 가능"), (entry.IsDyed, "염색됨"), (reason == null && entry.MateriaCount > 0, $"마테리아 {entry.MateriaCount}"));
        }
        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ 3. 장비함 ↔ 가방

    private void DrawTransfer()
    {
        Help("장비함의 장비를 가방으로, 또는 가방의 장비를 장비함(부위별 자동)으로 옮깁니다. 필터를 켜면 조건에 맞는 장비만 자동으로 체크되고 목록 맨 위로 정렬됩니다.");

        var running = transfer.IsRunning;
        if (running) ImGui.BeginDisabled();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("방향");
        ImGui.SameLine();
        if (ImGui.RadioButton("장비함 → 가방##dirA", transfer.Direction == TransferDirection.ArmoryToBag))
            SetTransferDirection(0);
        ImGui.SameLine();
        if (ImGui.RadioButton("가방 → 장비함##dirB", transfer.Direction == TransferDirection.BagToArmory))
            SetTransferDirection(1);

        if (transfer.Direction == TransferDirection.ArmoryToBag)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "|");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            var sourceLabel = transfer.SourceCategory < 0 ? "장비함 전체" : GearTransfer.Categories[transfer.SourceCategory].Label;
            if (ImGui.BeginCombo("##src", sourceLabel))
            {
                if (ImGui.Selectable("장비함 전체", transfer.SourceCategory < 0)) SetTransferSource(-1);
                for (var i = 0; i < GearTransfer.Categories.Length; i++)
                    if (ImGui.Selectable($"{GearTransfer.Categories[i].Label}##src{i}", transfer.SourceCategory == i))
                        SetTransferSource(i);
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            var bag4 = config.TransferToBag4Only;
            if (ImGui.Checkbox("4번 가방에만 넣기##bag4", ref bag4)) { config.TransferToBag4Only = bag4; transfer.Save(); }
            ImGui.SameLine();
            var excl = config.TransferExcludeGearset;
            if (ImGui.Checkbox("장비세트 등록 장비 제외##exgs", ref excl)) { config.TransferExcludeGearset = excl; transfer.ApplyFilters(); transfer.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("아이템 종류 기준입니다. 같은 장비가 둘인데 하나만 세트에 있어도 둘 다 제외됩니다.");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "| 가방 1~4의 장비를 부위에 맞는 장비함 칸으로 넣습니다. 부위 칸이 가득 차면 그 장비는 건너뜁니다.");
        }

        DrawFilterPanel();

        if (ImGui.Button("검색##trscan")) transfer.Scan();
        ImGui.SameLine();
        if (ImGui.Button("전체 체크##trall")) transfer.SelectAll(true);
        ImGui.SameLine();
        if (ImGui.Button("전체 해제##trnone")) transfer.SelectAll(false);
        ImGui.SameLine();
        var free = transfer.FreeDestinationSlots;
        var goLabel = free >= 0
            ? $"옮기기: {transfer.Selected.Count}개 → {transfer.DestinationLabel} (빈칸 {free})##trgo"
            : $"옮기기: {transfer.Selected.Count}개 → {transfer.DestinationLabel}##trgo";
        if (PrimaryButton(goLabel) && CanStart(transfer.IsRunning))
            transfer.Start();

        if (running) ImGui.EndDisabled();
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("중지##trstop")) transfer.Stop();
        }

        StatusLine(transfer.Status, transfer.LastItemStatus, transfer.IsRunning, transfer.Done, transfer.Total);
        ListToolbar(list3, transfer.Selected.Count, transfer.Entries.Count);

        if (!BeginList("TransferList", ("", 28), ("아이템", -3), ("위치", 110), ("Lv", 40), ("직업", -1.6f), ("비고", -1.4f))) return;
        foreach (var entry in transfer.Entries)
        {
            var gear = entry.Gear;
            var excluded = entry.InGearset && transfer.ExcludesGearset;
            var selected = !excluded && transfer.Selected.Contains(gear.Key);
            if (!list3.Show(gear.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (!excluded && ImGui.Checkbox($"##tr-{gear.Key.Id}", ref selected))
                transfer.SetSelected(gear.Key, selected);
            ImGui.TableNextColumn();
            if (excluded) ImGui.TextDisabled(gear.Name);
            else if (entry.Matched) ImGui.TextColored(ColorOk, gear.Name);
            else ImGui.TextUnformatted(gear.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Where(gear.Key.Container));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(gear.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(ColorMuted, entry.JobText);
            ImGui.TableNextColumn();
            Tags((entry.InDresser, "옷장 보유"), (entry.InGearset, excluded ? "장비세트 (제외)" : "장비세트"), (gear.IsDyed, "염색됨"), (gear.IsCrafterGatherer, "제작/채집"));
        }
        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ shared filter panel (2 · 3)

    private void DrawFilterPanel()
    {
        ImGui.Separator();
        var header = $"필터 (2·3번 탭 공유): {filter.Describe()}";
        ImGui.SetNextItemOpen(filterPanelOpen, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx($"{header}###filterpanel", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.Framed);
        if (ImGui.IsItemToggledOpen()) filterPanelOpen = !filterPanelOpen;
        if (!open)
        {
            ImGui.Spacing();
            return;
        }

        Help("켜진 필터를 위에서 아래로 '그리고(AND)' 또는 '또는(OR)'로 묶습니다. 조건에 맞는 장비는 초록색으로 표시되고 자동으로 체크됩니다.");

        // 1) 직업
        var jobOn = config.FilterJobEnabled;
        if (ImGui.Checkbox("직업##f1on", ref jobOn)) { config.FilterJobEnabled = jobOn; FilterChanged(); }
        ImGui.SameLine();
        var jobSummary = config.FilterJobs.Count == 0 ? "선택 없음" : string.Join(", ", config.FilterJobs.Select(filter.JobName));
        if (ImGui.TreeNodeEx($"사용 가능한 장비: {jobSummary}###f1tree", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            if (ImGui.SmallButton("모두 해제##f1clear")) { config.FilterJobs.Clear(); FilterChanged(); }
            foreach (var (group, jobs) in JobGroups)
            {
                ImGui.TextColored(ColorMuted, group);
                ImGui.SameLine(80);
                var first = true;
                foreach (var abbr in jobs)
                {
                    if (!first) ImGui.SameLine();
                    first = false;
                    var on = config.FilterJobs.Contains(abbr);
                    if (ImGui.Checkbox($"{filter.JobName(abbr)}##f1-{abbr}", ref on))
                    {
                        if (on) config.FilterJobs.Add(abbr); else config.FilterJobs.Remove(abbr);
                        FilterChanged();
                    }
                }
            }
            ImGui.TreePop();
        }

        // 2) 레벨
        var levelOp = config.FilterLevelOp;
        if (OpCombo("##f2op", ref levelOp)) { config.FilterLevelOp = levelOp; FilterChanged(); }
        ImGui.SameLine();
        var levelOn = config.FilterLevelEnabled;
        if (ImGui.Checkbox("착용 레벨##f2on", ref levelOn)) { config.FilterLevelEnabled = levelOn; FilterChanged(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var min = config.FilterLevelMin;
        if (ImGui.InputInt("##f2min", ref min)) { config.FilterLevelMin = Math.Clamp(min, 1, 100); FilterChanged(); }
        ImGui.SameLine();
        ImGui.TextUnformatted("~");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var max = config.FilterLevelMax;
        if (ImGui.InputInt("##f2max", ref max)) { config.FilterLevelMax = Math.Clamp(max, 1, 100); FilterChanged(); }
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "레벨");

        // 3) 옷장
        var dresserOp = config.FilterDresserOp;
        if (OpCombo("##f3op", ref dresserOp)) { config.FilterDresserOp = dresserOp; FilterChanged(); }
        ImGui.SameLine();
        var dresserOn = config.FilterDresserEnabled;
        if (ImGui.Checkbox("환상의 옷장##f3on", ref dresserOn)) { config.FilterDresserEnabled = dresserOn; FilterChanged(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("보유중##f3a", config.FilterDresserOwned)) { config.FilterDresserOwned = true; FilterChanged(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("미보유##f3b", !config.FilterDresserOwned)) { config.FilterDresserOwned = false; FilterChanged(); }
        ImGui.SameLine();
        if (dresserSnapshot.HasData)
            ImGui.TextColored(ColorMuted, $"(옷장 데이터 {dresserSnapshot.CapturedAt:HH:mm:ss} 기준)");
        else
            ImGui.TextColored(ColorWarn, "(옷장 데이터 없음 — 환상의 옷장을 한 번 열어 주세요)");

        if (ImGui.SmallButton("필터 모두 끄기##fclear"))
        {
            config.FilterJobEnabled = config.FilterLevelEnabled = config.FilterDresserEnabled = false;
            FilterChanged();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("필터로 다시 체크##fapply"))
            FilterChanged();

        ImGui.TreePop();
        ImGui.Spacing();
    }

    private static bool OpCombo(string id, ref int op)
    {
        ImGui.SetNextItemWidth(70);
        var changed = false;
        if (ImGui.BeginCombo(id, op == 1 ? "또는" : "그리고"))
        {
            if (ImGui.Selectable("그리고", op == 0)) { op = 0; changed = true; }
            if (ImGui.Selectable("또는", op == 1)) { op = 1; changed = true; }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("앞의 켜진 필터와 이 필터를 '그리고(AND)' 또는 '또는(OR)'로 묶습니다. 첫 번째로 켜진 필터에서는 무시됩니다.");
        return changed;
    }

    private void SetTransferDirection(int value)
    {
        if (config.TransferDirection == value) return;
        config.TransferDirection = value;
        transfer.Save();
        transfer.Scan();
    }

    private void SetTransferSource(int value)
    {
        if (config.TransferSourceCategory == value) return;
        config.TransferSourceCategory = value;
        transfer.Save();
        transfer.Scan();
    }

    // ------------------------------------------------------------------ 4. 자동 파기

    private void DrawStage4()
    {
        Help("장비함과 가방의 장비를 체크해 자동으로 파기합니다(확인 창은 자동으로 '예'). '4번 가방 전체 파기'는 장비가 아닌 아이템도 포함해 4번 가방의 모든 칸을 비웁니다. 되돌릴 수 없으니 확인 창을 거칩니다.");

        if (!stage4.IsRunning)
        {
            if (ImGui.Button("후보 검색##s4scan")) stage4.Scan();
            ImGui.SameLine();
            if (ImGui.Button("스마트 선택##s4smart")) stage4.SmartSelect();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s4all")) stage4.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 해제##s4none")) stage4.SelectAll(false);

            var include = config.Stage4SmartIncludeCrafterGatherer;
            if (ImGui.Checkbox("스마트 선택에 제작/채집 장비 포함##s4craft", ref include))
                stage4.UpdateSmartSettings(include, config.Stage4SmartMaxLevel);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var maxLevel = config.Stage4SmartMaxLevel;
            if (ImGui.InputInt("착용 레벨 이하##s4lvl", ref maxLevel))
                stage4.UpdateSmartSettings(config.Stage4SmartIncludeCrafterGatherer, maxLevel);

            if (DangerButton($"체크한 장비 파기 ({stage4.Selected.Count}개)##s4go") && CanStart(stage4.IsRunning) && stage4.Selected.Count > 0)
            {
                pendingDiscard = 1;
                ImGui.OpenPopup("파기 확인##discard");
            }
            ImGui.SameLine();
            var bag4Count = stage4.PreviewInventory4Count();
            if (DangerButton($"4번 가방 전체 파기 ({bag4Count}칸)##s4bag4") && CanStart(stage4.IsRunning) && bag4Count > 0)
            {
                pendingDiscard = 2;
                ImGui.OpenPopup("파기 확인##discard");
            }

            DrawDiscardConfirm();
        }
        else if (ImGui.Button("중지##s4stop"))
        {
            stage4.Stop();
        }

        StatusLine(stage4.Status, stage4.LastItemStatus, stage4.IsRunning, stage4.Done, stage4.Total);
        ListToolbar(list4, stage4.Selected.Count, stage4.Entries.Count);

        if (!BeginList("Stage4List", ("", 28), ("아이템", -3), ("위치", 110), ("Lv", 40), ("비고", -1.2f))) return;
        foreach (var entry in stage4.Entries)
        {
            var selected = stage4.Selected.Contains(entry.Key);
            if (!list4.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##s4-{entry.Key.Id}", ref selected))
                stage4.SetSelected(entry.Key, selected);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Where(entry.Key.Container));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            Tags((entry.IsDyed, "염색됨"), (entry.IsCrafterGatherer, "제작/채집"), (entry.HasMateria, "마테리아"));
        }
        ImGui.EndTable();
    }

    /// Modal confirmation for the two discard buttons (opened from the same ID scope as the popup).
    private void DrawDiscardConfirm()
    {
        if (!ImGui.BeginPopupModal("파기 확인##discard", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var what = pendingDiscard == 2
            ? $"4번 가방의 모든 아이템 {stage4.PreviewInventory4Count()}칸"
            : $"체크한 장비 {stage4.Selected.Count}개";
        ImGui.TextColored(ColorWarn, $"{what}을(를) 파기합니다. 되돌릴 수 없습니다.");
        ImGui.Spacing();
        if (DangerButton("파기 시작##confirmyes"))
        {
            if (pendingDiscard == 2) stage4.StartInventory4All();
            else stage4.StartSelected();
            pendingDiscard = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("취소##confirmno"))
        {
            pendingDiscard = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
