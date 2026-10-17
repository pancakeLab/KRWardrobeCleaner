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
    private const string VersionLabel = "v0.10.0";

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

    private readonly Configuration config;
    private readonly GlamourStateCache glamourCache;
    private readonly Stage1DresserToInventory stage1;
    private readonly DresserDuplicateCleaner duplicates;
    private readonly Stage2InventoryToDresser stage2;
    private readonly GearTransfer transfer;
    private readonly Stage4Discard stage4;

    private bool windowOpen = true;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var excel = new ExcelIndex(Data);
        var scanner = new InventoryScanner(excel);
        var outfits = new OutfitCatalog(Data, excel);

        glamourCache = new GlamourStateCache();
        stage1 = new Stage1DresserToInventory(excel, glamourCache, GameGui, config, SaveConfig);
        duplicates = new DresserDuplicateCleaner(excel, outfits, GameGui, Log, config, SaveConfig);
        stage2 = new Stage2InventoryToDresser(excel, scanner, outfits, GameGui, Log, config, SaveConfig);
        transfer = new GearTransfer(excel, scanner, outfits, Log, config, SaveConfig);
        stage4 = new Stage4Discard(scanner, GameGui, config, SaveConfig);

        Commands.AddHandler("/kwc", new CommandInfo((_, _) => windowOpen = !windowOpen)
        {
            HelpMessage = "히메짱 옷장 정리기를 엽니다.",
        });

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
        Commands.RemoveHandler("/kwc");
    }

    private void Open() => windowOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        glamourCache.Observe();
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

    // ------------------------------------------------------------------ window

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(980, 820), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(720, 480), new Vector2(float.MaxValue, float.MaxValue));
        if (!ImGui.Begin($"히메짱 옷장 정리기 {VersionLabel}###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawTopBar();

        if (ImGui.BeginTabBar("KWCStages", ImGuiTabBarFlags.None))
        {
            DrawTab("1. 옷장 → 가방", DrawStage1);
            DrawTab("1-2. 옷장 중복", DrawDuplicates);
            DrawTab("2. 가방 → 옷장", DrawStage2);
            DrawTab("3. 장비함 ↔ 가방", DrawTransfer);
            DrawTab("4. 자동 파기", DrawStage4);
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static void DrawTab(string title, System.Action body)
    {
        if (!ImGui.BeginTabItem(title)) return;
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
        ImGui.TextColored(ColorMuted, $"투영세트 캐시 {(glamourCache.HasPlateData ? $"{glamourCache.PlateItemCount}종" : "없음")} · 옷장 캐시 {(transfer.HasDresserData ? "있음" : "없음")}");

        if (AnyRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorBusy, "작업 진행 중");
            ImGui.SameLine();
            if (ImGui.Button("전체 중지##stopall"))
                StopAll();
        }

        ImGui.Separator();
    }

    // ------------------------------------------------------------------ shared pieces

    private static void Help(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void StatusLine(string status, string? last, bool running)
    {
        var color = running ? ColorBusy
            : status.StartsWith("완료") ? ColorOk
            : status.Contains("없습니다") || status.Contains("실패") || status.Contains("중지") ? ColorWarn
            : ColorMuted;
        ImGui.TextColored(color, status);
        if (!string.IsNullOrWhiteSpace(last))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextWrapped($"└ {last}");
            ImGui.PopStyleColor();
        }
    }

    private static void Progress(string label, int a, string na, int b, string nb, int c, string nc)
        => ImGui.TextColored(ColorBusy, $"{label}: {na} {a} · {nb} {b} · {nc} {c}");

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
            _ => GearTransfer.Categories.FirstOrDefault(x => x.Container == type)?.Label is { } label ? $"장비함·{label}" : "장비함",
        };

    private bool CanStart(bool thisRunning) => !thisRunning && !AnyRunning;

    // ------------------------------------------------------------------ 1. 옷장 → 가방

    private void DrawStage1()
    {
        Help("추억의 보관함에 넣을 수 있는 아이템이 환상의 옷장에 들어 있으면 가방으로 꺼냅니다. 환상의 옷장을 연 상태에서 쓰세요. 투영세트 등록 여부는 투영세트 편집 화면을 한 번 열어야 판정됩니다.");

        if (!stage1.IsRunning)
        {
            if (ImGui.Button("후보 검색##s1scan")) stage1.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s1all")) stage1.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 해제##s1none")) stage1.SelectAll(false);
            ImGui.SameLine();
            if (ImGui.Button($"가방으로 꺼내기 ({stage1.Selected.Count}개)##s1go") && CanStart(stage1.IsRunning))
                stage1.Start();

            var dyed = config.Stage1IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 꺼내기##s1dyed", ref dyed))
                stage1.UpdateOptions(dyed, config.Stage1IncludePlateRegistered);
            ImGui.SameLine();
            var plate = config.Stage1IncludePlateRegistered;
            if (ImGui.Checkbox("투영세트에 등록 중인 아이템도 꺼내기##s1plate", ref plate))
                stage1.UpdateOptions(config.Stage1IncludeDyed, plate);
        }
        else
        {
            Progress("진행", stage1.Restored, "복원", stage1.Skipped, "제외", stage1.Failed, "실패");
            ImGui.SameLine();
            if (ImGui.Button("중지##s1stop")) stage1.Stop();
        }

        StatusLine(stage1.Status, stage1.LastItemStatus, stage1.IsRunning);

        if (!BeginList("Stage1List", ("", 28), ("아이템", -3), ("비고", -1.2f))) return;
        foreach (var entry in stage1.Entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = stage1.Selected.Contains(entry.DresserSlot);
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
            if (ImGui.Button($"가방으로 꺼내기 ({duplicates.Selected.Count}개)##dupgo") && CanStart(duplicates.IsRunning))
                duplicates.Start();

            var keepDyed = config.DuplicateKeepDyedFirst;
            if (ImGui.Checkbox("염색된 쪽을 남기고 염색 안 된 쪽을 중복분으로 본다##dupdyed", ref keepDyed))
                duplicates.UpdateKeepDyedFirst(keepDyed);
        }
        else
        {
            Progress("진행", duplicates.Restored, "꺼냄", duplicates.Skipped, "제외", duplicates.Failed, "실패");
            ImGui.SameLine();
            if (ImGui.Button("중지##dupstop")) duplicates.Stop();
        }

        StatusLine(duplicates.Status, duplicates.LastItemStatus, duplicates.IsRunning);

        if (!BeginList("DupList", ("", 28), ("아이템", -3), ("벌 수", 60), ("판정", 90), ("비고", -1.2f))) return;
        foreach (var entry in duplicates.Entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = duplicates.Selected.Contains(entry.DresserSlot);
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
        Help("장비함과 가방에서 환상의 옷장에 넣을 수 있는 장비를 찾아, 기존 의상 세트 보충 → 세트 우선 저장 → 단벌 저장 순으로 넣습니다. 환상의 옷장과 '아이템 맡기기' 목록을 연 상태에서 쓰세요. 내구도 100% 미만은 게임이 받지 않아 자동 제외합니다.");

        if (!stage2.IsRunning)
        {
            if (ImGui.Button("후보 검색##s2scan")) stage2.Scan();
            ImGui.SameLine();
            if (ImGui.Button("기존 세트에 넣을 수 있는 것만 체크##s2sets")) stage2.SelectExistingSetCandidates();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s2all")) stage2.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 해제##s2none")) stage2.SelectAll(false);

            var dyed = config.Stage2IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 넣기##s2dyed", ref dyed))
                stage2.UpdateIncludeDyed(dyed);
            ImGui.SameLine();
            var materia = config.Stage2IncludeMateria;
            if (ImGui.Checkbox("마테리아 장착 아이템도 시도##s2materia", ref materia))
                stage2.UpdateIncludeMateria(materia);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("게임이 거절하면 실패로 기록됩니다.");

            if (ImGui.Button($"기존 세트에만 넣기 ({stage2.Selected.Count}개)##s2go1") && CanStart(stage2.IsRunning))
                stage2.StartExistingSetsOnly();
            ImGui.SameLine();
            if (ImGui.Button($"세트 우선, 나머지는 단벌로 넣기 ({stage2.Selected.Count}개)##s2go2") && CanStart(stage2.IsRunning))
                stage2.StartSetsThenSingles();
        }
        else
        {
            Progress("진행", stage2.Stored, "저장", stage2.Skipped, "제외", stage2.Failed, "실패");
            ImGui.SameLine();
            if (ImGui.Button("중지##s2stop")) stage2.Stop();
        }

        StatusLine(stage2.Status, stage2.LastItemStatus, stage2.IsRunning);

        if (!BeginList("Stage2List", ("", 28), ("아이템", -3), ("위치", 100), ("비고", -1.6f))) return;
        foreach (var entry in stage2.Entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var reason = stage2.BlockReason(entry.Key);
            if (reason == null)
            {
                var selected = stage2.Selected.Contains(entry.Key);
                if (ImGui.Checkbox($"##s2-{entry.Key.Id}", ref selected))
                    stage2.SetSelected(entry.Key, selected);
            }
            ImGui.TableNextColumn();
            if (reason == null) ImGui.TextUnformatted(entry.Name);
            else ImGui.TextDisabled(entry.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Where(entry.Key.Container));
            ImGui.TableNextColumn();
            if (reason != null) ImGui.TextColored(ColorWarn, reason);
            Tags((entry.IsDyed, "염색됨"), (reason == null && entry.MateriaCount > 0, $"마테리아 {entry.MateriaCount}"));
        }
        ImGui.EndTable();
    }

    // ------------------------------------------------------------------ 3. 장비함 ↔ 가방

    private void DrawTransfer()
    {
        Help("장비함의 장비를 가방으로, 또는 가방의 장비를 장비함(부위별 자동)으로 옮깁니다. 필터를 켜면 조건에 맞는 장비가 자동으로 체크되고 목록 맨 위로 정렬됩니다.");

        var running = transfer.IsRunning;
        if (running) ImGui.BeginDisabled();

        // 방향
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
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "| 가방 1~4의 장비를 부위에 맞는 장비함 칸으로 넣습니다. 부위 칸이 가득 차면 그 장비는 건너뜁니다.");
        }

        DrawTransferFilters();

        // 동작
        if (ImGui.Button("검색##trscan")) transfer.Scan();
        ImGui.SameLine();
        if (ImGui.Button("필터로 다시 체크##trapply")) transfer.ApplyFilters();
        ImGui.SameLine();
        if (ImGui.Button("전체 체크##trall")) transfer.SelectAll(true);
        ImGui.SameLine();
        if (ImGui.Button("전체 해제##trnone")) transfer.SelectAll(false);
        ImGui.SameLine();
        var free = transfer.FreeDestinationSlots;
        var goLabel = free >= 0
            ? $"옮기기 ({transfer.Selected.Count}개 → {transfer.DestinationLabel}, 빈칸 {free})##trgo"
            : $"옮기기 ({transfer.Selected.Count}개 → {transfer.DestinationLabel})##trgo";
        if (ImGui.Button(goLabel) && CanStart(transfer.IsRunning))
            transfer.Start();

        if (running) ImGui.EndDisabled();

        if (running)
        {
            Progress("진행", transfer.Moved, "이동", transfer.Skipped, "제외", transfer.Failed, "실패");
            ImGui.SameLine();
            if (ImGui.Button("중지##trstop")) transfer.Stop();
        }

        StatusLine(transfer.Status, transfer.LastItemStatus, transfer.IsRunning);

        if (!BeginList("TransferList", ("", 28), ("아이템", -3), ("위치", 110), ("Lv", 40), ("직업", -1.6f), ("비고", -1.4f))) return;
        foreach (var entry in transfer.Entries)
        {
            var gear = entry.Gear;
            var excluded = entry.InGearset && transfer.ExcludesGearset;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (!excluded)
            {
                var selected = transfer.Selected.Contains(gear.Key);
                if (ImGui.Checkbox($"##tr-{gear.Key.Id}", ref selected))
                    transfer.SetSelected(gear.Key, selected);
            }
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

    private void DrawTransferFilters()
    {
        ImGui.Separator();
        ImGui.TextColored(ColorTag, "필터 (자동 체크 · 위에서 아래로 결합)");

        // 1) 직업
        var jobOn = config.FilterJobEnabled;
        if (ImGui.Checkbox("직업##f1on", ref jobOn)) { config.FilterJobEnabled = jobOn; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        var jobSummary = config.FilterJobs.Count == 0 ? "선택 없음" : string.Join(", ", config.FilterJobs.Select(JobName));
        if (ImGui.TreeNodeEx($"사용 가능한 장비: {jobSummary}##f1tree", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            if (ImGui.SmallButton("모두 해제##f1clear")) { config.FilterJobs.Clear(); transfer.ApplyFilters(); transfer.Save(); }
            foreach (var (group, jobs) in JobGroups)
            {
                ImGui.TextColored(ColorMuted, group);
                ImGui.SameLine(70);
                var first = true;
                foreach (var abbr in jobs)
                {
                    if (!first) ImGui.SameLine();
                    first = false;
                    var on = config.FilterJobs.Contains(abbr);
                    if (ImGui.Checkbox($"{JobName(abbr)}##f1-{abbr}", ref on))
                    {
                        if (on) config.FilterJobs.Add(abbr); else config.FilterJobs.Remove(abbr);
                        transfer.ApplyFilters();
                        transfer.Save();
                    }
                }
            }
            ImGui.TreePop();
        }

        // 2) 레벨
        var levelOp = config.FilterLevelOp;
        if (OpCombo("##f2op", ref levelOp)) { config.FilterLevelOp = levelOp; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        var levelOn = config.FilterLevelEnabled;
        if (ImGui.Checkbox("착용 레벨##f2on", ref levelOn)) { config.FilterLevelEnabled = levelOn; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var min = config.FilterLevelMin;
        if (ImGui.InputInt("##f2min", ref min)) { config.FilterLevelMin = Math.Clamp(min, 1, 100); transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        ImGui.TextUnformatted("~");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        var max = config.FilterLevelMax;
        if (ImGui.InputInt("##f2max", ref max)) { config.FilterLevelMax = Math.Clamp(max, 1, 100); transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "레벨");

        // 3) 옷장
        var dresserOp = config.FilterDresserOp;
        if (OpCombo("##f3op", ref dresserOp)) { config.FilterDresserOp = dresserOp; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        var dresserOn = config.FilterDresserEnabled;
        if (ImGui.Checkbox("환상의 옷장##f3on", ref dresserOn)) { config.FilterDresserEnabled = dresserOn; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("보유중##f3a", config.FilterDresserOwned)) { config.FilterDresserOwned = true; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("미보유##f3b", !config.FilterDresserOwned)) { config.FilterDresserOwned = false; transfer.ApplyFilters(); transfer.Save(); }
        ImGui.SameLine();
        if (transfer.HasDresserData)
            ImGui.TextColored(ColorMuted, $"(옷장 데이터 {transfer.DresserSnapshotAt:HH:mm:ss} 기준)");
        else
            ImGui.TextColored(ColorWarn, "(옷장 데이터 없음 — 환상의 옷장을 한 번 열어 주세요)");

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

    private string JobName(string abbr)
        => transfer.Jobs.FirstOrDefault(x => x.Abbreviation == abbr)?.Name ?? abbr;

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
        Help("장비함과 가방의 장비를 체크해 자동으로 파기합니다(확인 창은 자동으로 '예'). 마테리아·투영 여부는 무시합니다. '4번 가방 전체 파기'는 장비가 아닌 아이템도 포함해 4번 가방의 모든 칸을 비웁니다.");

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

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.20f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.25f, 0.25f, 1f));
            if (ImGui.Button($"체크한 장비 파기 ({stage4.Selected.Count}개)##s4go") && CanStart(stage4.IsRunning))
                stage4.StartSelected();
            ImGui.SameLine();
            var bag4Count = stage4.PreviewInventory4Count();
            if (ImGui.Button($"4번 가방 전체 파기 ({bag4Count}칸)##s4bag4") && CanStart(stage4.IsRunning))
                stage4.StartInventory4All();
            ImGui.PopStyleColor(2);
        }
        else
        {
            var mode = stage4.IsBag4Mode ? "4번 가방 전체 파기" : "선택 장비 파기";
            Progress(mode, stage4.Discarded, "파기", stage4.Skipped, "제외", stage4.Failed, "실패");
            ImGui.SameLine();
            if (ImGui.Button("중지##s4stop")) stage4.Stop();
        }

        StatusLine(stage4.Status, stage4.LastItemStatus, stage4.IsRunning);

        if (!BeginList("Stage4List", ("", 28), ("아이템", -3), ("위치", 110), ("Lv", 40), ("비고", -1.2f))) return;
        foreach (var entry in stage4.Entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = stage4.Selected.Contains(entry.Key);
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
}
