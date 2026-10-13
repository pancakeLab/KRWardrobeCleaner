using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace KRWardrobeCleaner;

public sealed class Plugin : IDalamudPlugin
{
    private const string VersionLabel = "v0.9.0";

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
    private readonly Stage3ArmoryToInventory stage3;
    private readonly ArmoryCategoryMover armoryMover;
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
        stage3 = new Stage3ArmoryToInventory(scanner, config, SaveConfig);
        armoryMover = new ArmoryCategoryMover(scanner, Log, config, SaveConfig);
        stage4 = new Stage4Discard(scanner, GameGui, config, SaveConfig);

        Commands.AddHandler("/kwc", new CommandInfo((_, _) => windowOpen = !windowOpen)
        {
            HelpMessage = "히메짱 옷장 정리기를 엽니다.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += OnFrameworkUpdate;

        Log.Information("[KWC] 히메짱 옷장 정리기 {Version} loaded. outfit sets={Sets} (sheet columns {Cols}), debugLog={Debug}",
            VersionLabel, outfits.Count, outfits.SheetColumns, config.DebugLog);
    }

    public void Dispose()
    {
        const string reason = "플러그인이 종료되어 작업을 중지했습니다.";
        stage1.Stop(reason);
        duplicates.Stop(reason);
        stage2.Stop(reason);
        stage3.Stop(reason);
        armoryMover.Stop(reason);
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
        stage3.Tick();
        armoryMover.Tick();
        stage4.Tick();
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private bool AnyRunning =>
        stage1.IsRunning || duplicates.IsRunning || stage2.IsRunning ||
        stage3.IsRunning || armoryMover.IsRunning || stage4.IsRunning;

    private void StopAll()
    {
        stage1.Stop();
        duplicates.Stop();
        stage2.Stop();
        stage3.Stop();
        armoryMover.Stop();
        stage4.Stop();
    }

    private void Draw()
    {
        if (!windowOpen) return;

        ImGui.SetNextWindowSize(new Vector2(960, 840), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"히메짱 옷장 정리기 {VersionLabel}###KRWardrobeCleaner", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        var debug = config.DebugLog;
        if (ImGui.Checkbox("디버그 로그 (/xllog 에 [KWC] 접두어로 기록)", ref debug))
        {
            config.DebugLog = debug;
            SaveConfig();
        }

        if (ImGui.BeginTabBar("KWCStages", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("1. 옷장 → 인벤토리"))
            {
                DrawStage1();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("1-2. 옷장 중복 정리"))
            {
                DrawDuplicates();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("2. 환상의 옷장에 넣기"))
            {
                DrawStage2();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("3. 장비함 → 인벤토리"))
            {
                DrawStage3();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("3-2. 장비함 → 4번 가방"))
            {
                DrawArmoryMover();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("4. 자동 파기"))
            {
                DrawStage4();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (AnyRunning)
        {
            ImGui.Separator();
            if (ImGui.Button("진행 중인 작업 전체 중지"))
                StopAll();
        }

        ImGui.End();
    }

    private void DrawStage1()
    {
        ImGui.TextWrapped("추억의 보관함에 넣을 수 있는 아이템이 환상의 옷장에 들어 있는 경우를 찾아 인벤토리로 복원합니다.");
        ImGui.TextWrapped($"투영세트 캐시: {(glamourCache.HasPlateData ? $"{glamourCache.PlateItemCount}종" : "대기")}");

        if (!stage1.IsRunning)
        {
            if (ImGui.Button("후보 검색##s1scan"))
                stage1.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s1all"))
                stage1.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##s1none"))
                stage1.SelectAll(false);

            var dyed = config.Stage1IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 옮기기", ref dyed))
                stage1.UpdateOptions(dyed, config.Stage1IncludePlateRegistered);

            var plate = config.Stage1IncludePlateRegistered;
            if (ImGui.Checkbox("투영 세트에 등록 중인 아이템도 옮기기", ref plate))
                stage1.UpdateOptions(config.Stage1IncludeDyed, plate);

            if (ImGui.Button($"체크 항목 인벤토리로 이동 ({stage1.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(stage1.IsRunning))
                    stage1.Start();
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: 복원 {stage1.Restored} · 제외 {stage1.Skipped} · 실패 {stage1.Failed}");
            if (ImGui.Button("1단계 중지"))
                stage1.Stop();
        }

        ImGui.TextWrapped($"상태: {stage1.Status}");
        if (!string.IsNullOrWhiteSpace(stage1.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {stage1.LastItemStatus}");

        ImGui.BeginChild("Stage1List", new Vector2(0, 560), true);
        foreach (var entry in stage1.Entries)
        {
            var selected = stage1.Selected.Contains(entry.DresserSlot);
            var suffix = string.Empty;
            if (entry.IsDyed) suffix += " [염색됨]";
            if (entry.UsedOnPlate) suffix += " [투영 세트 등록중]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##s1-{entry.DresserSlot}", ref selected))
                stage1.SetSelected(entry.DresserSlot, selected);
        }
        ImGui.EndChild();
    }

    private void DrawDuplicates()
    {
        ImGui.TextWrapped("환상의 옷장에 같은 아이템이 두 벌 이상 들어 있거나, 의상 세트에 이미 들어 있는 조각이 따로 또 들어 있는 경우를 찾습니다.");
        ImGui.TextWrapped("'중복분만 체크'는 아이템마다 한 벌(염색된 것 우선, 세트에 있으면 세트 쪽)을 남기고 나머지를 체크합니다. 체크 항목은 환상의 옷장에서 가방으로 되돌립니다.");

        if (!duplicates.IsRunning)
        {
            if (ImGui.Button("중복 검색##dupscan"))
                duplicates.Scan();
            ImGui.SameLine();
            if (ImGui.Button("중복분만 체크##dupsurplus"))
                duplicates.SelectSurplusOnly();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##dupall"))
                duplicates.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##dupnone"))
                duplicates.SelectAll(false);

            var keepDyed = config.DuplicateKeepDyedFirst;
            if (ImGui.Checkbox("염색된 쪽을 남기고 염색 안 된 쪽을 중복분으로 본다", ref keepDyed))
                duplicates.UpdateKeepDyedFirst(keepDyed);

            if (ImGui.Button($"체크 항목 가방으로 되돌리기 ({duplicates.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(duplicates.IsRunning))
                    duplicates.Start();
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: 되돌림 {duplicates.Restored} · 제외 {duplicates.Skipped} · 실패 {duplicates.Failed}");
            if (ImGui.Button("중복 정리 중지"))
                duplicates.Stop();
        }

        ImGui.TextWrapped($"상태: {duplicates.Status}");
        if (!string.IsNullOrWhiteSpace(duplicates.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {duplicates.LastItemStatus}");

        ImGui.BeginChild("DupList", new Vector2(0, 540), true);
        foreach (var entry in duplicates.Entries)
        {
            var selected = duplicates.Selected.Contains(entry.DresserSlot);
            var suffix = $" ({entry.GroupSize}벌";
            if (entry.AlsoInSet) suffix += " + 세트에도 있음";
            suffix += ")";
            if (entry.IsDyed) suffix += " [염색됨]";
            suffix += entry.IsKeep ? " [남길 것]" : " [중복분]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##dup-{entry.DresserSlot}", ref selected))
                duplicates.SetSelected(entry.DresserSlot, selected);
        }
        ImGui.EndChild();
    }

    private void DrawStage2()
    {
        ImGui.TextWrapped("장비함과 일반 인벤토리에서 환상의 옷장에 넣을 수 있는 장비를 찾아, 기존 의상 세트 보충 또는 세트 우선 저장 후 단벌 저장을 수행합니다.");
        ImGui.TextWrapped("내구도가 100%가 아닌 장비는 게임이 받지 않으므로 자동 제외합니다. 마테리아가 장착된 장비는 기본적으로 제외합니다(아래 옵션).");

        if (!stage2.IsRunning)
        {
            if (ImGui.Button("후보 검색##s2scan"))
                stage2.Scan();
            ImGui.SameLine();
            if (ImGui.Button("기존 세트에 포함해서 넣을 수 있는 아이템 전체 체크"))
                stage2.SelectExistingSetCandidates();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s2all"))
                stage2.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##s2none"))
                stage2.SelectAll(false);

            var dyed = config.Stage2IncludeDyed;
            if (ImGui.Checkbox("염색된 아이템도 옮기기##s2dyed", ref dyed))
                stage2.UpdateIncludeDyed(dyed);

            var materia = config.Stage2IncludeMateria;
            if (ImGui.Checkbox("마테리아 장착 아이템도 시도 (게임이 거절하면 실패로 기록)##s2materia", ref materia))
                stage2.UpdateIncludeMateria(materia);

            if (ImGui.Button($"체크 → 기존 세트에 포함해서 넣기 ({stage2.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(stage2.IsRunning))
                    stage2.StartExistingSetsOnly();
            }

            ImGui.SameLine();
            if (ImGui.Button($"체크 → 세트 우선, 이후 단벌로 넣기 ({stage2.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(stage2.IsRunning))
                    stage2.StartSetsThenSingles();
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: 저장 {stage2.Stored} · 제외 {stage2.Skipped} · 실패 {stage2.Failed}");
            if (ImGui.Button("2단계 중지"))
                stage2.Stop();
        }

        ImGui.TextWrapped($"상태: {stage2.Status}");
        if (!string.IsNullOrWhiteSpace(stage2.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {stage2.LastItemStatus}");

        ImGui.BeginChild("Stage2List", new Vector2(0, 520), true);
        foreach (var entry in stage2.Entries)
        {
            var suffix = entry.IsDyed ? " [염색됨]" : string.Empty;
            suffix += IsBag(entry.Key.Container) ? " [인벤토리]" : " [장비함]";
            var reason = stage2.BlockReason(entry.Key);
            if (reason != null)
            {
                ImGui.TextDisabled($"    {entry.Name}{suffix} [{reason}]");
                continue;
            }

            var selected = stage2.Selected.Contains(entry.Key);
            if (entry.MateriaCount > 0) suffix += $" [마테리아 {entry.MateriaCount}개]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##s2-{entry.Key.Id}", ref selected))
                stage2.SetSelected(entry.Key, selected);
        }
        ImGui.EndChild();
    }

    private void DrawStage3()
    {
        ImGui.TextWrapped("장비함의 장비를 일반 인벤토리로 옮깁니다. 스마트 선택은 요구 착용 레벨과 제작자/채집가 장비 포함 여부만 사용합니다.");

        if (!stage3.IsRunning)
        {
            if (ImGui.Button("후보 검색##s3scan"))
                stage3.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s3all"))
                stage3.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##s3none"))
                stage3.SelectAll(false);

            var include = config.Stage3SmartIncludeCrafterGatherer;
            if (ImGui.Checkbox("스마트 선택에 제작자/채집가 장비 포함", ref include))
                stage3.UpdateSmartSettings(include, config.Stage3SmartMaxLevel);

            var maxLevel = config.Stage3SmartMaxLevel;
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputInt("요구 착용 레벨 이하", ref maxLevel))
                stage3.UpdateSmartSettings(config.Stage3SmartIncludeCrafterGatherer, maxLevel);

            if (ImGui.Button("스마트 선택"))
                stage3.SmartSelect();

            ImGui.SameLine();
            if (ImGui.Button($"체크 항목 인벤토리로 이동 ({stage3.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(stage3.IsRunning))
                    stage3.Start();
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: 이동 {stage3.Moved} · 제외 {stage3.Skipped} · 실패 {stage3.Failed}");
            if (ImGui.Button("3단계 중지"))
                stage3.Stop();
        }

        ImGui.TextWrapped($"상태: {stage3.Status}");
        if (!string.IsNullOrWhiteSpace(stage3.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {stage3.LastItemStatus}");

        ImGui.BeginChild("Stage3List", new Vector2(0, 540), true);
        foreach (var entry in stage3.Entries)
        {
            var selected = stage3.Selected.Contains(entry.Key);
            var suffix = $" [Lv.{entry.RequiredLevel}]";
            if (entry.IsCrafterGatherer) suffix += " [제작/채집]";
            if (entry.IsDyed) suffix += " [염색됨]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##s3-{entry.Key.Id}", ref selected))
                stage3.SetSelected(entry.Key, selected);
        }
        ImGui.EndChild();
    }

    private void DrawArmoryMover()
    {
        ImGui.TextWrapped("장비함에서 카테고리 하나를 골라 체크된 장비를 전부 4번 가방의 빈칸으로 옮깁니다. 장비세트에 등록된 장비는 자동으로 제외되며, 검색하면 나머지는 모두 체크됩니다.");
        ImGui.TextWrapped("4번 가방이 꽉 차면 빈칸 수만큼만 옮기고 나머지는 장비함에 남깁니다.");

        if (!armoryMover.IsRunning)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("카테고리##armcat", armoryMover.Category.Label))
            {
                for (var i = 0; i < ArmoryCategoryMover.Categories.Length; i++)
                {
                    var isSelected = i == armoryMover.CategoryIndex;
                    if (ImGui.Selectable($"{ArmoryCategoryMover.Categories[i].Label}##armcat{i}", isSelected))
                        armoryMover.SetCategory(i);
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("검색##armscan"))
                armoryMover.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##armall"))
                armoryMover.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##armnone"))
                armoryMover.SelectAll(false);

            if (ImGui.Button($"[옮기기] 체크한 {armoryMover.Category.Label} 장비 {armoryMover.Selected.Count}개 → 4번 가방 (빈칸 {armoryMover.FreeBag4Slots}칸)"))
            {
                if (!AnyOtherRunning(armoryMover.IsRunning))
                    armoryMover.Start();
            }
        }
        else
        {
            ImGui.TextUnformatted($"진행: 이동 {armoryMover.Moved} · 제외 {armoryMover.Skipped} · 실패 {armoryMover.Failed}");
            if (ImGui.Button("옮기기 중지"))
                armoryMover.Stop();
        }

        ImGui.TextWrapped($"상태: {armoryMover.Status}");
        if (!string.IsNullOrWhiteSpace(armoryMover.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {armoryMover.LastItemStatus}");

        ImGui.BeginChild("ArmoryMoveList", new Vector2(0, 520), true);
        foreach (var entry in armoryMover.Entries)
        {
            var gear = entry.Gear;
            var suffix = $" [Lv.{gear.RequiredLevel}]";
            if (gear.IsCrafterGatherer) suffix += " [제작/채집]";
            if (gear.IsDyed) suffix += " [염색됨]";
            if (entry.InGearset)
            {
                ImGui.TextDisabled($"    {gear.Name}{suffix} [장비세트 등록 — 제외]");
                continue;
            }

            var selected = armoryMover.Selected.Contains(gear.Key);
            if (ImGui.Checkbox($"{gear.Name}{suffix}##arm-{gear.Key.Id}", ref selected))
                armoryMover.SetSelected(gear.Key, selected);
        }
        ImGui.EndChild();
    }

    private void DrawStage4()
    {
        ImGui.TextWrapped("장비함과 일반 인벤토리의 장비를 체크해서 자동 파기합니다. 마테리아 장착 및 투영 여부는 무시합니다.");
        ImGui.TextWrapped("스마트 선택은 3번 탭과 동일하게 요구 착용 레벨과 제작자/채집가 장비 포함 여부를 사용합니다.");
        ImGui.TextWrapped("'4번 인벤토리 전체 파기'는 Inventory4의 모든 슬롯을 대상으로 하며, 장비가 아닌 일반 아이템도 포함합니다.");

        if (!stage4.IsRunning)
        {
            if (ImGui.Button("후보 검색##s4scan"))
                stage4.Scan();
            ImGui.SameLine();
            if (ImGui.Button("전체 체크##s4all"))
                stage4.SelectAll(true);
            ImGui.SameLine();
            if (ImGui.Button("전체 체크 해제##s4none"))
                stage4.SelectAll(false);

            var include = config.Stage4SmartIncludeCrafterGatherer;
            if (ImGui.Checkbox("스마트 선택에 제작자/채집가 장비 포함##s4smartcraft", ref include))
                stage4.UpdateSmartSettings(include, config.Stage4SmartMaxLevel);

            var maxLevel = config.Stage4SmartMaxLevel;
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputInt("요구 착용 레벨 이하##s4smartlevel", ref maxLevel))
                stage4.UpdateSmartSettings(config.Stage4SmartIncludeCrafterGatherer, maxLevel);

            if (ImGui.Button("스마트 선택##s4smart"))
                stage4.SmartSelect();

            ImGui.SameLine();
            if (ImGui.Button($"체크한 아이템 자동 파기 ({stage4.Selected.Count}개)"))
            {
                if (!AnyOtherRunning(stage4.IsRunning))
                    stage4.StartSelected();
            }

            ImGui.Separator();
            var bag4Count = stage4.PreviewInventory4Count();
            if (ImGui.Button($"4번 인벤토리에 있는 모든 아이템 파기 ({bag4Count}슬롯)"))
            {
                if (!AnyOtherRunning(stage4.IsRunning))
                    stage4.StartInventory4All();
            }
            ImGui.TextWrapped("이 버튼은 4번 인벤토리(Inventory4)만 대상으로 하며, 현재 들어 있는 모든 아이템 스택을 처리합니다.");
        }
        else
        {
            var mode = stage4.IsBag4Mode ? "4번 인벤토리 전체 파기" : "선택 장비 파기";
            ImGui.TextUnformatted($"{mode} 진행: 파기 {stage4.Discarded} · 제외 {stage4.Skipped} · 실패 {stage4.Failed}");
            if (ImGui.Button("4단계 중지"))
                stage4.Stop();
        }

        ImGui.TextWrapped($"상태: {stage4.Status}");
        if (!string.IsNullOrWhiteSpace(stage4.LastItemStatus))
            ImGui.TextWrapped($"최근 처리: {stage4.LastItemStatus}");

        ImGui.BeginChild("Stage4List", new Vector2(0, 570), true);
        foreach (var entry in stage4.Entries)
        {
            var selected = stage4.Selected.Contains(entry.Key);
            var suffix = IsBag(entry.Key.Container) ? " [인벤토리]" : " [장비함]";
            if (entry.IsDyed) suffix += " [염색됨]";
            if (ImGui.Checkbox($"{entry.Name}{suffix}##s4-{entry.Key.Id}", ref selected))
                stage4.SetSelected(entry.Key, selected);
        }
        ImGui.EndChild();
    }

    private bool AnyOtherRunning(bool currentRunning)
        => !currentRunning && AnyRunning;

    private static bool IsBag(FFXIVClientStructs.FFXIV.Client.Game.InventoryType type)
        => type is FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1
            or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2
            or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3
            or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4;
}
