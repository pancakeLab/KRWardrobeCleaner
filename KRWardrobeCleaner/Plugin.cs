using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string VersionLabel = "v0.12.2";
    private static readonly string[] CommandNames = ["/히메짱가방"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;

    private readonly Configuration config;
    private readonly ExcelIndex excel;
    private readonly GlamourStateCache glamourCache;
    private readonly DresserSnapshot dresserSnapshot;
    private readonly GearFilter filter;
    private readonly Stage1DresserToInventory stage1;
    private readonly DresserDuplicateCleaner duplicates;
    private readonly Stage2InventoryToDresser stage2;
    private readonly GearTransfer transfer;
    private readonly Stage4Discard stage4;
    private readonly CallbackRecorder recorder;
    private readonly GearsetIndex gearsets = new();

    private bool windowOpen = true;
    private bool filterPanelOpen = true;
    private readonly ListView list1 = new(), listDup = new(), list2 = new(), list3 = new(), list4 = new();
    private int pendingDiscard; // 0 none, 1 selected, 2 bag4

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        excel = new ExcelIndex(Data);
        var scanner = new InventoryScanner(excel);
        var outfits = new OutfitCatalog(Data, excel);

        glamourCache = new GlamourStateCache();
        dresserSnapshot = new DresserSnapshot(outfits);
        filter = new GearFilter(excel, config, dresserSnapshot);
        stage1 = new Stage1DresserToInventory(excel, glamourCache, GameGui, config, SaveConfig);
        duplicates = new DresserDuplicateCleaner(excel, outfits, GameGui, Log, config, SaveConfig);
        stage2 = new Stage2InventoryToDresser(excel, scanner, outfits, filter, GameGui, Objects, Condition, Log, config, SaveConfig);
        transfer = new GearTransfer(excel, scanner, filter, Log, config, SaveConfig);
        stage4 = new Stage4Discard(scanner, filter, GameGui, config, SaveConfig);
        recorder = new CallbackRecorder(Interop, Log);

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
        recorder.Dispose();
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
        stage4.ApplyFilters();
    }

}
