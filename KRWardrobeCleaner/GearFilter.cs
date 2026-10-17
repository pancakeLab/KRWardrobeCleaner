using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

public enum FilterOp { And = 0, Or = 1 }

/// <summary>
/// Keeps a copy of the glamour dresser contents whenever the game has them loaded, so the
/// "환상의 옷장 보유" judgement still works after the dresser window is closed.
/// </summary>
public sealed unsafe class DresserSnapshot
{
    private const int IntervalMs = 1000;
    private readonly OutfitCatalog outfits;
    private HashSet<uint>? items;
    private long nextAt;

    public DresserSnapshot(OutfitCatalog outfits) => this.outfits = outfits;

    public bool HasData => items != null;
    public DateTimeOffset? CapturedAt { get; private set; }

    public void Observe()
    {
        var now = Environment.TickCount64;
        if (now < nextAt) return;
        nextAt = now + IntervalMs;

        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded) return;

        items = DresserContents.StoredItemIds(manager, outfits);
        CapturedAt = DateTimeOffset.Now;
    }

    public bool Contains(uint itemId) => items?.Contains(itemId) ?? false;
}

/// <summary>
/// The shared "직업 / 착용 레벨 / 환상의 옷장 보유" filter. Conditions are combined top to bottom:
/// the first enabled one starts the result, each following one is joined with its own AND / OR.
/// Used by 2. 가방 → 옷장 and 3. 장비함 ↔ 가방 (same settings in both tabs).
/// </summary>
public sealed class GearFilter
{
    private readonly ExcelIndex excel;
    private readonly Configuration config;
    private readonly DresserSnapshot dresser;

    public GearFilter(ExcelIndex excel, Configuration config, DresserSnapshot dresser)
    {
        this.excel = excel;
        this.config = config;
        this.dresser = dresser;
    }

    public bool AnyEnabled => config.FilterJobEnabled || config.FilterLevelEnabled || config.FilterDresserEnabled;

    /// Warning to show when the dresser condition is on but no dresser data has been seen yet.
    public string? DresserWarning => config.FilterDresserEnabled && !dresser.HasData
        ? "옷장 데이터가 없어 옷장 필터는 모두 '미보유'로 판정됩니다. 환상의 옷장을 한 번 열어 주세요."
        : null;

    public bool InDresser(uint itemId) => dresser.Contains(itemId);

    public bool Matches(uint itemId, int requiredLevel)
    {
        bool? result = null;
        var jobSet = ActiveJobSet();

        if (config.FilterJobEnabled)
            Combine(ref result, FilterOp.And, excel.JobsOf(itemId).Overlaps(jobSet));

        if (config.FilterLevelEnabled)
            Combine(ref result, (FilterOp)config.FilterLevelOp, requiredLevel >= config.FilterLevelMin && requiredLevel <= config.FilterLevelMax);

        if (config.FilterDresserEnabled)
            Combine(ref result, (FilterOp)config.FilterDresserOp, dresser.Contains(itemId) == config.FilterDresserOwned);

        return result ?? false;
    }

    /// Short human-readable description of the active conditions, e.g. "직업(백마도사) 그리고 레벨 1~60".
    public string Describe()
    {
        var parts = new List<string>();
        if (config.FilterJobEnabled)
            parts.Add($"직업({(config.FilterJobs.Count == 0 ? "선택 없음" : string.Join("·", config.FilterJobs.Select(JobName)))})");
        if (config.FilterLevelEnabled)
            parts.Add($"{OpWord(parts.Count, config.FilterLevelOp)}레벨 {config.FilterLevelMin}~{config.FilterLevelMax}");
        if (config.FilterDresserEnabled)
            parts.Add($"{OpWord(parts.Count, config.FilterDresserOp)}옷장 {(config.FilterDresserOwned ? "보유중" : "미보유")}");
        return parts.Count == 0 ? "필터 없음" : string.Join(" ", parts);
    }

    public string JobName(string abbreviation)
        => excel.Jobs.FirstOrDefault(x => x.Abbreviation == abbreviation)?.Name ?? abbreviation;

    private static string OpWord(int index, int op) => index == 0 ? string.Empty : op == 1 ? "또는 " : "그리고 ";

    private HashSet<string> activeJobSetCache = [];
    private string activeJobSetKey = string.Empty;

    private HashSet<string> ActiveJobSet()
    {
        var key = string.Join(",", config.FilterJobs);
        if (key == activeJobSetKey) return activeJobSetCache;

        var set = new HashSet<string>(config.FilterJobs, StringComparer.Ordinal);
        foreach (var job in excel.Jobs)
            if (set.Contains(job.Abbreviation) && !string.IsNullOrEmpty(job.ParentAbbreviation))
                set.Add(job.ParentAbbreviation);   // a job's gear includes its base class' gear

        activeJobSetCache = set;
        activeJobSetKey = key;
        return set;
    }

    private static void Combine(ref bool? result, FilterOp op, bool value)
    {
        if (result is null) result = value;
        else result = op == FilterOp.Or ? result.Value || value : result.Value && value;
    }
}
