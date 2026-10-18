using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace KRWardrobeCleaner;

/// <summary>
/// Which gear sets (장비 세트, 1-based numbers as shown in game) reference an item id. Refreshed at most
/// once a second; matching is by base item id, so two identical items share the same answer.
/// </summary>
public sealed unsafe class GearsetIndex
{
    private readonly Dictionary<uint, List<int>> sets = [];
    private long refreshedAt;

    public void Refresh(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - refreshedAt < 1000) return;
        refreshedAt = now;
        sets.Clear();

        var module = RaptureGearsetModule.Instance();
        if (module == null) return;

        for (var i = 0; i < module->Entries.Length; i++)
        {
            var entry = module->Entries[i];
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            for (var j = 0; j < entry.Items.Length; j++)
            {
                var id = Normalize(entry.Items[j].ItemId);
                if (id == 0) continue;
                if (!sets.TryGetValue(id, out var list)) sets[id] = list = [];
                if (!list.Contains(i + 1)) list.Add(i + 1);
            }
        }
    }

    public bool Contains(uint itemId) => sets.ContainsKey(itemId);

    public IReadOnlyList<int> SetsOf(uint itemId) => sets.TryGetValue(itemId, out var list) ? list : [];

    /// "장비 세트 사용중: 2번" / "장비 세트 사용중: 2, 5번", or null when no set uses the item.
    public string? Label(uint itemId)
    {
        var list = SetsOf(itemId);
        return list.Count == 0 ? null : $"장비 세트 사용중: {string.Join(", ", list)}번";
    }

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}
