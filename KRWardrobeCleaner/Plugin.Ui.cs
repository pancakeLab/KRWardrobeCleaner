using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

/// <summary>Main window. Every tab follows the same skeleton: summary card → action row → status → list.</summary>
public sealed partial class Plugin
{
    private static readonly (string Group, string[] Jobs)[] JobGroups =
    [
        ("탱커", ["PLD", "WAR", "DRK", "GNB"]),
        ("힐러", ["WHM", "SCH", "AST", "SGE"]),
        ("근접", ["MNK", "DRG", "NIN", "SAM", "RPR", "VPR"]),
        ("원거리", ["BRD", "MCH", "DNC"]),
        ("마법", ["BLM", "SMN", "RDM", "PCT", "BLU"]),
        ("기본 클래스", ["GLA", "MRD", "PGL", "LNC", "ROG", "ARC", "CNJ", "THM", "ACN"]),
        ("제작", ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"]),
        ("채집", ["MIN", "BTN", "FSH"]),
    ];

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

    // ------------------------------------------------------------------ window

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(960, 800), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(720, 480), new Vector2(float.MaxValue, float.MaxValue));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        var visible = ImGui.Begin("히메짱 옷장 정리기###KRWardrobeCleaner", ref windowOpen, ImGuiWindowFlags.NoScrollbar);
        if (visible)
        {
            gearsets.Refresh();
            DrawHeader();
            ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6f);
            if (ImGui.BeginTabBar("KWCStages", ImGuiTabBarFlags.FittingPolicyScroll))
            {
                DrawTab("1 · 옷장 → 가방·장비함", DrawStage1Tab, "환상의 옷장에서 아이템을 꺼냅니다: 추억의 보관함 후보, 또는 필터로 고른 아이템(세트 조각 포함)을 장비함으로.");
                DrawTab("1-2 · 옷장 중복", DrawDuplicates, "환상의 옷장에 두 벌 이상 든 아이템의 중복분을 가방으로 꺼냅니다.");
                DrawTab("2 · 가방 → 옷장", DrawStage2, "장비함·가방의 장비를 환상의 옷장(세트 우선)에 넣습니다.");
                DrawTab("3 · 장비함 ↔ 가방", DrawTransfer, "장비함과 가방 사이에서 장비를 옮깁니다.");
                DrawTab("4 · 자동 파기", DrawStage4, "체크한 장비 또는 4번 가방 전체를 파기합니다.");
                ImGui.EndTabBar();
            }
            ImGui.PopStyleVar();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
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

    /// Title line with version and status, plus the diagnostic toggles and cache state on the right.
    private void DrawHeader()
    {
        UiKit.Icon(FontAwesomeIcon.ShoppingBag, UiKit.Lavender);
        ImGui.SameLine();
        ImGui.TextUnformatted("히메짱 옷장 정리기");
        UiKit.Chip(VersionLabel, UiKit.Muted, true);
        if (AnyRunning)
        {
            UiKit.Chip("● 작업 진행 중", UiKit.Busy, true);
            ImGui.SameLine();
            if (UiKit.Button(FontAwesomeIcon.Stop, "전체 중지", "stopall", 2))
                StopAll();
        }

        // right side: caches + toggles
        var rightWidth = ImGui.GetFrameHeight() * 2 + 8 + 240;
        UiKit.AlignRight(rightWidth);
        var plates = glamourCache.HasPlateData ? $"투영세트 {glamourCache.PlateItemCount}종" : "투영세트 캐시 없음";
        UiKit.Chip(plates, glamourCache.HasPlateData ? UiKit.Muted : UiKit.Warn);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("1번 탭의 '투영세트 등록중' 판정에 쓰입니다. 투영세트 편집 화면을 한 번 열면 채워집니다.");
        var dresser = dresserSnapshot.HasData ? $"옷장 {dresserSnapshot.CapturedAt:HH:mm:ss}" : "옷장 캐시 없음";
        UiKit.Chip(dresser, dresserSnapshot.HasData ? UiKit.Muted : UiKit.Warn, true);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("필터의 '환상의 옷장 보유' 판정에 쓰입니다. 환상의 옷장을 열면 1초마다 갱신되고 닫아도 유지됩니다.");
        ImGui.SameLine();
        if (UiKit.IconButton(FontAwesomeIcon.Bug, "dbg", "디버그 로그: /xllog 에 [KWC] 접두어로 후보 판정·요청·검증 결과를 남깁니다.", config.DebugLog))
        {
            config.DebugLog = !config.DebugLog;
            SaveConfig();
        }
        ImGui.SameLine();
        if (UiKit.IconButton(FontAwesomeIcon.Microphone, "rec",
                recorder.HookOk
                    ? "콜백 기록: 켜 둔 채로 환상의 옷장을 손으로 조작하면 게임이 보내는 콜백 값이 /xllog 에 [KWC] callback 으로 남습니다."
                    : "이 게임 버전에서는 기록기를 걸 수 없습니다.", recorder.Enabled))
            recorder.Enabled = !recorder.Enabled && recorder.HookOk;

        ImGui.Spacing();
    }

    // ------------------------------------------------------------------ shared pieces

    /// Summary card: one-line description with a tooltip holding the long explanation.
    private static void Summary(string text, string? details = null)
    {
        if (UiKit.BeginCard("summary"))
        {
            ImGui.TextWrapped(text);
            if (details != null) UiKit.HelpMarker(details);
        }
        UiKit.EndCard();
    }

    private static void StatusBlock(string status, string? last, bool running, int done, int total)
    {
        if (running && total > 0) UiKit.Progress(done, total);
        var color = running ? UiKit.Busy
            : status.StartsWith("완료") ? UiKit.Ok
            : status.Contains("없습니다") || status.Contains("실패") || status.Contains("중지") || status.Contains("주세요") ? UiKit.Warn
            : UiKit.Muted;
        if (running) UiKit.Icon(FontAwesomeIcon.Spinner, color);
        else if (status.StartsWith("완료")) UiKit.Icon(FontAwesomeIcon.CheckCircle, color);
        else UiKit.Icon(FontAwesomeIcon.InfoCircle, color);
        ImGui.SameLine();
        ImGui.TextColored(color, running && total > 0 ? $"{status}  ({done} / {total})" : status);
        if (!string.IsNullOrWhiteSpace(last))
            UiKit.MutedText($"└ {last}", true);
    }

    private static void ListToolbar(ListView view, int checkedCount, int total, int matched = -1)
    {
        UiKit.Icon(FontAwesomeIcon.Search, UiKit.Muted);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "이름으로 찾기", ref view.Search, 64);
        ImGui.SameLine();
        ImGui.Checkbox("체크한 것만##onlychecked", ref view.OnlyChecked);
        UiKit.AlignRight(matched >= 0 ? 260 : 170);
        UiKit.Chip($"전체 {total}", UiKit.Muted);
        UiKit.Chip($"체크 {checkedCount}", UiKit.Lavender, true);
        if (matched >= 0) UiKit.Chip($"조건 일치 {matched}", UiKit.Ok, true);
    }

    private static bool BeginList(string id, params (string Title, float Width)[] columns)
    {
        var avail = ImGui.GetContentRegionAvail();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerH;
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.03f));
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, new Vector4(1f, 1f, 1f, 0.05f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(1f, 1f, 1f, 0.06f));
        if (!ImGui.BeginTable(id, columns.Length, flags, new Vector2(0, Math.Max(avail.Y, 120))))
        {
            ImGui.PopStyleColor(3);
            return false;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var (title, width) in columns)
        {
            if (width > 0) ImGui.TableSetupColumn(title, ImGuiTableColumnFlags.WidthFixed, width);
            else ImGui.TableSetupColumn(title, ImGuiTableColumnFlags.WidthStretch, -width);
        }
        ImGui.TableHeadersRow();
        return true;
    }

    private static void EndList()
    {
        ImGui.EndTable();
        ImGui.PopStyleColor(3);
    }

    /// Draws the item's game icon (frame-height square) and leaves the cursor on the same line for the name.
    private void ItemIcon(uint itemId)
    {
        var size = ImGui.GetFrameHeight();
        var iconId = excel.IconOf(itemId);
        if (iconId != 0)
        {
            var wrap = Textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        else
        {
            ImGui.Dummy(new Vector2(size, size));
        }
        ImGui.SameLine(0, 6);
        ImGui.AlignTextToFramePadding();
    }

    /// Beige "장비 세트 사용중: N번" chip when any gear set references the item.
    private void GearsetChip(uint itemId, bool sameLine, string? suffix = null)
    {
        var label = gearsets.Label(itemId);
        if (label == null) return;
        UiKit.SolidChip(label + suffix, UiKit.BeigeBg, UiKit.BeigeText, sameLine);
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

    private static bool StopButton(string id)
    {
        ImGui.SameLine();
        return UiKit.Button(FontAwesomeIcon.Stop, "중지", id, 2);
    }

    /// Opens a small options popup from a gear button; returns true while the popup is open (caller draws the body).
    private static bool BeginOptions(string id)
    {
        ImGui.SameLine();
        if (UiKit.IconButton(FontAwesomeIcon.Cog, id, "옵션"))
            ImGui.OpenPopup($"popup-{id}");
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        var open = ImGui.BeginPopup($"popup-{id}");
        if (!open) ImGui.PopStyleVar();
        return open;
    }

    private static void EndOptions()
    {
        ImGui.EndPopup();
        ImGui.PopStyleVar();
    }

    // ------------------------------------------------------------------ 1. 옷장 → 가방·장비함

    private void DrawStage1Tab()
    {
        var busy = stage1.IsRunning || pull.IsRunning;
        ImGui.BeginDisabled(busy);
        ImGui.AlignTextToFramePadding();
        UiKit.MutedText("꺼낼 기준");
        ImGui.SameLine();
        if (ImGui.RadioButton("추억의 보관함 후보##s1m0", config.Stage1Mode == 0)) { config.Stage1Mode = 0; SaveConfig(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("필터로 고르기 (세트 조각 포함, 장비함으로)##s1m1", config.Stage1Mode == 1)) { config.Stage1Mode = 1; SaveConfig(); }
        ImGui.EndDisabled();
        ImGui.Spacing();

        if (config.Stage1Mode == 1) DrawDresserPull();
        else DrawStage1();
    }

    private void DrawDresserPull()
    {
        Summary("환상의 옷장에 든 아이템(의상 세트 안의 조각 포함)을 필터로 골라 꺼내고, 옷장을 닫은 뒤 장비함으로 옮깁니다.",
            "예: 직업 필터 '탱커' + 레벨 50~80을 켜면 레벨업용 탱커 장비가 세트 안에 있어도 전부 체크됩니다. 꺼낸 아이템은 먼저 가방으로 들어오므로 가방 빈칸이 필요합니다. " +
            "장비함으로 옮기려면 게임이 옷장을 닫아야 해서 꺼낸 뒤 자동으로 닫습니다. 다 쓰고 나면 2번 탭에서 다시 세트로 넣을 수 있습니다.");

        var running = pull.IsRunning;
        ImGui.BeginDisabled(running);
        DrawFilterPanel();

        if (UiKit.Button(FontAwesomeIcon.Search, "옷장 검색", "pullscan")) pull.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "pullall")) pull.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "pullnone")) pull.SelectAll(false);
        if (BeginOptions("pullopt"))
        {
            var toArmory = config.PullToArmory;
            if (ImGui.Checkbox("꺼낸 뒤 옷장을 닫고 장비함으로 옮기기##pullarm", ref toArmory))
                pull.SetPullToArmory(toArmory);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("끄면 가방에 둡니다. 켜면 옷장이 닫힌 뒤 부위별 장비함 칸으로 옮깁니다(칸이 가득 차면 가방에 남김).");
            EndOptions();
        }
        var goLabel = config.PullToArmory ? $"꺼내서 장비함으로 ({pull.Selected.Count}개)" : $"가방으로 꺼내기 ({pull.Selected.Count}개)";
        UiKit.AlignRight(UiKit.ButtonSize(FontAwesomeIcon.Play, goLabel).X);
        if (UiKit.Button(FontAwesomeIcon.Play, goLabel, "pullgo", 1) && CanStart(running))
            pull.Start();
        ImGui.EndDisabled();
        if (running && StopButton("pullstop")) pull.Stop();

        StatusBlock(pull.Status, pull.LastItemStatus, running, pull.Done, pull.Total);
        ListToolbar(listPull, pull.Selected.Count, pull.Entries.Count, pull.MatchedCount);

        if (!BeginList("PullList", ("", 28), ("아이템", -3), ("세트", -1.8f), ("Lv", 40), ("직업", -1.6f), ("비고", -1f))) return;
        foreach (var entry in pull.Entries)
        {
            var selected = pull.Selected.Contains(entry.Id);
            if (!listPull.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##pull-{entry.Id}", ref selected))
                pull.SetSelected(entry.Id, selected);
            ImGui.TableNextColumn();
            ItemIcon(entry.ItemId);
            if (pull.IsMatched(entry.Id)) ImGui.TextColored(UiKit.Ok, entry.Name);
            else ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (entry.SetName != null) ImGui.TextColored(UiKit.Lavender, entry.SetName);
            else UiKit.MutedText("단벌");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText(entry.JobText);
            ImGui.TableNextColumn();
            UiKit.Chips((entry.IsDyed, "염색됨", UiKit.Tag));
        }
        EndList();
    }

    private void DrawStage1()
    {
        Summary("추억의 보관함에 넣을 수 있는 아이템을 환상의 옷장에서 가방으로 꺼냅니다.",
            "환상의 옷장을 연 상태에서 쓰세요. 염색된 아이템과 투영세트에 등록 중인 아이템은 기본적으로 제외합니다(옵션에서 변경).");

        var running = stage1.IsRunning;
        ImGui.BeginDisabled(running);
        if (UiKit.Button(FontAwesomeIcon.Search, "후보 검색", "s1scan")) stage1.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "s1all")) stage1.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "s1none")) stage1.SelectAll(false);
        if (BeginOptions("s1opt"))
        {
            var dyed = config.Stage1IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 꺼내기##s1dyed", ref dyed))
                stage1.UpdateOptions(dyed, config.Stage1IncludePlateRegistered);
            var plate = config.Stage1IncludePlateRegistered;
            if (ImGui.Checkbox("투영세트에 등록 중인 아이템도 꺼내기##s1plate", ref plate))
                stage1.UpdateOptions(config.Stage1IncludeDyed, plate);
            EndOptions();
        }
        var goLabel = $"가방으로 꺼내기 ({stage1.Selected.Count}개)";
        UiKit.AlignRight(UiKit.ButtonSize(FontAwesomeIcon.Play, goLabel).X);
        if (UiKit.Button(FontAwesomeIcon.Play, goLabel, "s1go", 1) && CanStart(running))
            stage1.Start();
        ImGui.EndDisabled();
        if (running && StopButton("s1stop")) stage1.Stop();

        StatusBlock(stage1.Status, stage1.LastItemStatus, running, stage1.Done, stage1.Total);
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
            ItemIcon(entry.ItemId);
            ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            UiKit.Chips((entry.IsDyed, "염색됨", UiKit.Tag), (entry.UsedOnPlate, "투영세트 등록중", UiKit.Busy));
        }
        EndList();
    }

    // ------------------------------------------------------------------ 1-2. 옷장 중복

    private void DrawDuplicates()
    {
        Summary("환상의 옷장에 같은 아이템이 두 벌 이상 있거나 세트에도 따로 든 조각을 찾아, 중복분을 가방으로 꺼냅니다.",
            "'중복분만 체크'는 아이템마다 한 벌(염색된 것 우선, 세트에 있으면 세트 쪽)을 남기고 나머지를 체크합니다. 환상의 옷장을 연 상태에서 쓰세요.");

        var running = duplicates.IsRunning;
        ImGui.BeginDisabled(running);
        if (UiKit.Button(FontAwesomeIcon.Search, "중복 검색", "dupscan")) duplicates.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Clone, "중복분만 체크", "dupsurplus")) duplicates.SelectSurplusOnly();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "dupall")) duplicates.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "dupnone")) duplicates.SelectAll(false);
        if (BeginOptions("dupopt"))
        {
            var keepDyed = config.DuplicateKeepDyedFirst;
            if (ImGui.Checkbox("염색된 쪽을 남기고 염색 안 된 쪽을 중복분으로 본다##dupdyed", ref keepDyed))
                duplicates.UpdateKeepDyedFirst(keepDyed);
            EndOptions();
        }
        var goLabel = $"가방으로 꺼내기 ({duplicates.Selected.Count}개)";
        UiKit.AlignRight(UiKit.ButtonSize(FontAwesomeIcon.Play, goLabel).X);
        if (UiKit.Button(FontAwesomeIcon.Play, goLabel, "dupgo", 1) && CanStart(running))
            duplicates.Start();
        ImGui.EndDisabled();
        if (running && StopButton("dupstop")) duplicates.Stop();

        StatusBlock(duplicates.Status, duplicates.LastItemStatus, running, duplicates.Done, duplicates.Total);
        ListToolbar(listDup, duplicates.Selected.Count, duplicates.Entries.Count);

        if (!BeginList("DupList", ("", 28), ("아이템", -3), ("판정", 80), ("벌 수", 70), ("비고", -1.2f))) return;
        foreach (var entry in duplicates.Entries)
        {
            var selected = duplicates.Selected.Contains(entry.DresserSlot);
            if (!listDup.Show(entry.Name, selected)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##dup-{entry.DresserSlot}", ref selected))
                duplicates.SetSelected(entry.DresserSlot, selected);
            ImGui.TableNextColumn();
            ItemIcon(entry.ItemId);
            ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            if (entry.IsKeep) UiKit.Chip("남길 것", UiKit.Ok);
            else UiKit.Chip("중복분", UiKit.Warn);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{entry.GroupSize}{(entry.AlsoInSet ? " +세트" : "")}");
            ImGui.TableNextColumn();
            UiKit.Chips((entry.IsDyed, "염색됨", UiKit.Tag), (entry.AlsoInSet, "세트에도 있음", UiKit.Lavender));
        }
        EndList();
    }

    // ------------------------------------------------------------------ 2. 가방 → 옷장

    private void DrawStage2()
    {
        Summary("장비함·가방의 장비를 환상의 옷장에 넣습니다. 기존 세트 보충 → 새 세트 → 단벌 순입니다.",
            "환상의 옷장과 '아이템 맡기기' 목록을 연 상태에서 쓰세요. 내구도 100% 미만은 게임이 받지 않아 자동 제외합니다.\n" +
            "게임은 가방에 있는 장비만 맡길 수 있고 옷장이 열린 동안은 장비함에서 꺼낼 수 없어서, 가방 장비를 먼저 다 넣은 뒤 장비함 장비는 옷장을 잠시 닫고 가방으로 옮겨 다시 열어 넣기를 빈칸이 생기는 만큼 반복합니다.");

        var running = stage2.IsRunning;
        ImGui.BeginDisabled(running);
        DrawFilterPanel();

        if (UiKit.Button(FontAwesomeIcon.Search, "후보 검색", "s2scan")) stage2.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Tshirt, "기존 세트 보충분만 체크", "s2sets", 0, "옷장에 이미 있는 의상 세트의 빈 조각만 체크합니다.")) stage2.SelectExistingSetCandidates();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "s2all")) stage2.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "s2none")) stage2.SelectAll(false);
        if (BeginOptions("s2opt"))
        {
            var dyed = config.Stage2IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 넣기##s2dyed", ref dyed))
            {
                stage2.UpdateIncludeDyed(dyed);
                stage2.ApplyFilters();
            }
            var materia = config.Stage2IncludeMateria;
            if (ImGui.Checkbox("마테리아 장착 아이템도 시도##s2materia", ref materia))
                stage2.UpdateIncludeMateria(materia);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("게임이 거절하면 실패로 기록됩니다.");
            var reopen = config.Stage2AutoReopenDresser;
            if (ImGui.Checkbox("옷장 자동 다시 열기##s2reopen", ref reopen)) { config.Stage2AutoReopenDresser = reopen; SaveConfig(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("장비함 조각이 있으면 옷장을 잠시 닫고 가방으로 옮긴 뒤, 근처의 환상의 옷장과 상호작용해 다시 엽니다. 끄면 옮긴 뒤 직접 열어 주세요(열리면 자동으로 이어서 저장).");

            ImGui.Separator();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText("단벌 맡기기 방식");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200);
            var methodLabel = config.Stage2SingleMethod switch { 1 => "에이전트 플래그", 2 => "콜백 값 직접 지정", _ => "기록된 콜백 (권장)" };
            if (ImGui.BeginCombo("##s2method", methodLabel))
            {
                if (ImGui.Selectable("기록된 콜백 (권장)", config.Stage2SingleMethod == 0)) { config.Stage2SingleMethod = 0; SaveConfig(); }
                if (ImGui.Selectable("에이전트 플래그", config.Stage2SingleMethod == 1)) { config.Stage2SingleMethod = 1; SaveConfig(); }
                if (ImGui.Selectable("콜백 값 직접 지정", config.Stage2SingleMethod == 2)) { config.Stage2SingleMethod = 2; SaveConfig(); }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("기록된 콜백: 손으로 맡길 때 게임이 보내는 값(14, 행, 0)을 그대로 보냅니다.\n에이전트 플래그: 목록 선택 인덱스와 맡기기 대기 플래그를 직접 써서 게임이 확인 창을 띄우게 합니다.\n직접 지정: '콜백 기록'으로 얻은 값을 입력합니다. u 접두어는 uint, {row}는 목록 행 번호.");
            if (config.Stage2SingleMethod == 2)
            {
                ImGui.SetNextItemWidth(200);
                var cb = config.Stage2SingleCallback;
                if (ImGui.InputTextWithHint("##s2cb", "예: u14,{row},u0", ref cb, 64)) { config.Stage2SingleCallback = cb; SaveConfig(); }
            }
            EndOptions();
        }

        // run buttons: the recommended one is accented
        var mainLabel = $"세트 우선, 나머지는 단벌로 넣기 ({stage2.Selected.Count}개)";
        var setsLabel = $"세트화 가능한 것만 ({stage2.SelectedWithOutfit}개)";
        var fillLabel = $"기존 세트에만 ({stage2.Selected.Count}개)";
        var width = UiKit.ButtonSize(FontAwesomeIcon.Play, mainLabel).X + UiKit.ButtonSize(null, setsLabel).X + UiKit.ButtonSize(null, fillLabel).X + 16;
        UiKit.AlignRight(width);
        if (UiKit.Button(null, fillLabel, "s2go1", 0, "옷장에 이미 있는 의상 세트의 빈 조각만 채웁니다.") && CanStart(running))
            stage2.StartExistingSetsOnly();
        ImGui.SameLine();
        if (UiKit.Button(null, setsLabel, "s2go3", 0, "의상 세트가 있는 장비만 넣습니다(기존 세트 보충 + 새 세트, 조각 1개부터). 세트가 없는 장비는 그대로 둡니다.") && CanStart(running))
            stage2.StartSetsOnly();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Play, mainLabel, "s2go2", 1, "세트로 넣을 수 있는 것은 세트로, 나머지는 한 벌씩 넣습니다.") && CanStart(running))
            stage2.StartSetsThenSingles();
        ImGui.EndDisabled();
        if (running && StopButton("s2stop")) stage2.Stop();

        StatusBlock(stage2.Status, stage2.LastItemStatus, running, stage2.Done, stage2.Total);
        ListToolbar(list2, stage2.Selected.Count, stage2.Entries.Count, stage2.Entries.Count(e => stage2.IsMatched(e.Key)));

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
            ItemIcon(entry.ItemId);
            if (reason != null) ImGui.TextDisabled(entry.Name);
            else if (stage2.IsMatched(entry.Key)) ImGui.TextColored(UiKit.Ok, entry.Name);
            else ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText(Where(entry.Key.Container));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            var s2chips = (reason != null ? 1 : 0) + (stage2.HasOutfit(entry.ItemId) ? 1 : 0) + (entry.IsDyed ? 1 : 0) + (reason == null && entry.MateriaCount > 0 ? 1 : 0);
            UiKit.Chips((reason != null, reason ?? string.Empty, UiKit.Warn), (stage2.HasOutfit(entry.ItemId), "세트 가능", UiKit.Lavender),
                (entry.IsDyed, "염색됨", UiKit.Tag), (reason == null && entry.MateriaCount > 0, $"마테리아 {entry.MateriaCount}", UiKit.Tag));
            GearsetChip(entry.ItemId, s2chips > 0);
        }
        EndList();
    }

    // ------------------------------------------------------------------ 3. 장비함 ↔ 가방

    private void DrawTransfer()
    {
        Summary("장비함의 장비를 가방으로, 또는 가방의 장비를 부위에 맞는 장비함 칸으로 옮깁니다.",
            "필터를 켜면 조건에 맞는 장비만 자동으로 체크되고 목록 맨 위로 정렬됩니다. 가방 → 장비함은 부위 칸이 가득 차면 그 장비를 건너뜁니다.");

        var running = transfer.IsRunning;
        ImGui.BeginDisabled(running);

        if (UiKit.BeginCard("trdir"))
        {
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText("방향");
            ImGui.SameLine();
            if (ImGui.RadioButton("장비함 → 가방##dirA", transfer.Direction == TransferDirection.ArmoryToBag))
                SetTransferDirection(0);
            ImGui.SameLine();
            if (ImGui.RadioButton("가방 → 장비함##dirB", transfer.Direction == TransferDirection.BagToArmory))
                SetTransferDirection(1);

            if (transfer.Direction == TransferDirection.ArmoryToBag)
            {
                ImGui.SameLine(0, 18);
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
                if (ImGui.Checkbox("4번 가방에만##bag4", ref bag4)) { config.TransferToBag4Only = bag4; transfer.Save(); }
                ImGui.SameLine();
                var excl = config.TransferExcludeGearset;
                if (ImGui.Checkbox("장비세트 등록 장비 제외##exgs", ref excl)) { config.TransferExcludeGearset = excl; transfer.ApplyFilters(); transfer.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("아이템 종류 기준입니다. 같은 장비가 둘인데 하나만 세트에 있어도 둘 다 제외됩니다.");
            }
        }
        UiKit.EndCard();

        DrawFilterPanel();

        if (UiKit.Button(FontAwesomeIcon.Search, "검색", "trscan")) transfer.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "trall")) transfer.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "trnone")) transfer.SelectAll(false);
        var free = transfer.FreeDestinationSlots;
        var goLabel = free >= 0
            ? $"옮기기: {transfer.Selected.Count}개 → {transfer.DestinationLabel} (빈칸 {free})"
            : $"옮기기: {transfer.Selected.Count}개 → {transfer.DestinationLabel}";
        UiKit.AlignRight(UiKit.ButtonSize(FontAwesomeIcon.ExchangeAlt, goLabel).X);
        if (UiKit.Button(FontAwesomeIcon.ExchangeAlt, goLabel, "trgo", 1) && CanStart(running))
            transfer.Start();
        ImGui.EndDisabled();
        if (running && StopButton("trstop")) transfer.Stop();

        StatusBlock(transfer.Status, transfer.LastItemStatus, running, transfer.Done, transfer.Total);
        ListToolbar(list3, transfer.Selected.Count, transfer.Entries.Count, transfer.Entries.Count(e => e.Matched));

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
            ItemIcon(gear.ItemId);
            if (excluded) ImGui.TextDisabled(gear.Name);
            else if (entry.Matched) ImGui.TextColored(UiKit.Ok, gear.Name);
            else ImGui.TextUnformatted(gear.Name);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText(Where(gear.Key.Container));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(gear.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText(entry.JobText);
            ImGui.TableNextColumn();
            var trchips = (entry.InDresser ? 1 : 0) + (gear.IsDyed ? 1 : 0) + (gear.IsCrafterGatherer ? 1 : 0);
            UiKit.Chips((entry.InDresser, "옷장 보유", UiKit.Lavender), (gear.IsDyed, "염색됨", UiKit.Tag), (gear.IsCrafterGatherer, "제작/채집", UiKit.Tag));
            if (entry.InGearset && gearsets.Label(gear.ItemId) == null) UiKit.Chip(excluded ? "장비세트 (제외)" : "장비세트", UiKit.Busy, trchips > 0);
            else GearsetChip(gear.ItemId, trchips > 0, excluded ? " (제외)" : null);
        }
        EndList();
    }

    // ------------------------------------------------------------------ shared filter panel (2 · 3)

    private void DrawFilterPanel()
    {
        if (!UiKit.BeginCard("filtercard"))
        {
            UiKit.EndCard();
            return;
        }

        UiKit.Icon(FontAwesomeIcon.Filter, UiKit.Lavender);
        ImGui.SameLine();
        ImGui.SetNextItemOpen(filterPanelOpen, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx($"필터: {filter.Describe()}###filterpanel", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        if (ImGui.IsItemToggledOpen()) filterPanelOpen = !filterPanelOpen;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("1(필터 모드)·2·3·4번 탭이 같은 필터를 씁니다. 켜진 필터를 위에서 아래로 '그리고(AND)' 또는 '또는(OR)'로 묶고, 조건에 맞는 장비는 초록색으로 표시되며 자동으로 체크됩니다.");
        if (!open)
        {
            UiKit.EndCard();
            return;
        }

        ImGui.Spacing();

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
                // group checkbox: checked when every job of the group is selected; toggles the whole group
                var allOn = jobs.All(config.FilterJobs.Contains);
                var someOn = !allOn && jobs.Any(config.FilterJobs.Contains);
                if (someOn) ImGui.PushStyleColor(ImGuiCol.CheckMark, UiKit.Muted);
                var groupOn = allOn || someOn;
                if (ImGui.Checkbox($"{group}##f1g-{group}", ref groupOn))
                {
                    if (allOn) foreach (var abbr in jobs) config.FilterJobs.Remove(abbr);
                    else foreach (var abbr in jobs) config.FilterJobs.Add(abbr);
                    FilterChanged();
                }
                if (someOn) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(allOn ? "그룹 전체 해제" : "그룹 전체 선택");
                ImGui.SameLine(110);
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
        UiKit.MutedText("레벨");

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
        if (dresserSnapshot.HasData) UiKit.MutedText($"(옷장 데이터 {dresserSnapshot.CapturedAt:HH:mm:ss} 기준)");
        else ImGui.TextColored(UiKit.Warn, "(옷장 데이터 없음 — 환상의 옷장을 한 번 열어 주세요)");

        if (ImGui.SmallButton("필터 모두 끄기##fclear"))
        {
            config.FilterJobEnabled = config.FilterLevelEnabled = config.FilterDresserEnabled = false;
            FilterChanged();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("필터로 다시 체크##fapply"))
            FilterChanged();

        UiKit.EndCard();
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
        Summary("장비함과 가방의 장비를 체크해 자동으로 파기합니다. 되돌릴 수 없으니 확인 창을 거칩니다.",
            "확인 창은 자동으로 '예'를 누릅니다. 필터를 켜면 조건에 맞는 장비(예: 환상의 옷장에 이미 있는 장비)만 체크됩니다. '4번 가방 전체 파기'는 장비가 아닌 아이템도 포함해 4번 가방의 모든 칸을 비웁니다. '스마트 선택'은 착용 레벨 이하의 장비를 고릅니다.");

        var running = stage4.IsRunning;
        ImGui.BeginDisabled(running);
        DrawFilterPanel();
        if (UiKit.Button(FontAwesomeIcon.Search, "후보 검색", "s4scan")) stage4.Scan();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Magic, "스마트 선택", "s4smart")) stage4.SmartSelect();
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.CheckSquare, "전체", "s4all")) stage4.SelectAll(true);
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Square, "해제", "s4none")) stage4.SelectAll(false);
        if (BeginOptions("s4opt"))
        {
            var include = config.Stage4SmartIncludeCrafterGatherer;
            if (ImGui.Checkbox("스마트 선택에 제작/채집 장비 포함##s4craft", ref include))
                stage4.UpdateSmartSettings(include, config.Stage4SmartMaxLevel);
            ImGui.SetNextItemWidth(90);
            var maxLevel = config.Stage4SmartMaxLevel;
            if (ImGui.InputInt("착용 레벨 이하##s4lvl", ref maxLevel))
                stage4.UpdateSmartSettings(config.Stage4SmartIncludeCrafterGatherer, maxLevel);
            EndOptions();
        }

        var bag4Count = stage4.PreviewInventory4Count();
        var goLabel = $"체크한 장비 파기 ({stage4.Selected.Count}개)";
        var bagLabel = $"4번 가방 전체 파기 ({bag4Count}칸)";
        UiKit.AlignRight(UiKit.ButtonSize(FontAwesomeIcon.TrashAlt, goLabel).X + UiKit.ButtonSize(FontAwesomeIcon.Trash, bagLabel).X + 8);
        if (UiKit.Button(FontAwesomeIcon.TrashAlt, goLabel, "s4go", 2) && CanStart(running) && stage4.Selected.Count > 0)
        {
            pendingDiscard = 1;
            ImGui.OpenPopup("파기 확인##discard");
        }
        ImGui.SameLine();
        if (UiKit.Button(FontAwesomeIcon.Trash, bagLabel, "s4bag4", 2) && CanStart(running) && bag4Count > 0)
        {
            pendingDiscard = 2;
            ImGui.OpenPopup("파기 확인##discard");
        }
        DrawDiscardConfirm();
        ImGui.EndDisabled();
        if (running && StopButton("s4stop")) stage4.Stop();

        StatusBlock(stage4.Status, stage4.LastItemStatus, running, stage4.Done, stage4.Total);
        ListToolbar(list4, stage4.Selected.Count, stage4.Entries.Count, stage4.MatchedCount);

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
            ItemIcon(entry.ItemId);
            if (stage4.IsMatched(entry.Key)) ImGui.TextColored(UiKit.Ok, entry.Name);
            else ImGui.TextUnformatted(entry.Name);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            UiKit.MutedText(Where(entry.Key.Container));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.RequiredLevel.ToString());
            ImGui.TableNextColumn();
            var s4chips = (entry.IsDyed ? 1 : 0) + (entry.IsCrafterGatherer ? 1 : 0) + (entry.HasMateria ? 1 : 0);
            UiKit.Chips((entry.IsDyed, "염색됨", UiKit.Tag), (entry.IsCrafterGatherer, "제작/채집", UiKit.Tag), (entry.HasMateria, "마테리아", UiKit.Tag));
            GearsetChip(entry.ItemId, s4chips > 0);
        }
        EndList();
    }

    /// Modal confirmation for the two discard buttons (opened from the same ID scope as the popup).
    private void DrawDiscardConfirm()
    {
        if (!ImGui.BeginPopupModal("파기 확인##discard", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var what = pendingDiscard == 2
            ? $"4번 가방의 모든 아이템 {stage4.PreviewInventory4Count()}칸"
            : $"체크한 장비 {stage4.Selected.Count}개";
        UiKit.Icon(FontAwesomeIcon.ExclamationTriangle, UiKit.Warn);
        ImGui.SameLine();
        ImGui.TextColored(UiKit.Warn, $"{what}을(를) 파기합니다. 되돌릴 수 없습니다.");
        ImGui.Spacing();
        if (UiKit.Button(FontAwesomeIcon.Trash, "파기 시작", "confirmyes", 2))
        {
            if (pendingDiscard == 2) stage4.StartInventory4All();
            else stage4.StartSelected();
            pendingDiscard = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (UiKit.Button(null, "취소", "confirmno"))
        {
            pendingDiscard = 0;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
