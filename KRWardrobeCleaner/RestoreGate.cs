using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KRWardrobeCleaner;

/// <summary>
/// Paces glamour-dresser restore requests. The game refuses a new RestorePrismBoxItem call while the
/// previous one is still in flight (seen in game: 10 requests → 1 success, 9 refusals), so after every
/// accepted request we wait until the dresser slot actually changes (or the client stops reporting the
/// dresser as busy) before sending the next one, and a refusal is retried a few times instead of being
/// counted as a failure straight away.
/// </summary>
public sealed unsafe class RestoreGate
{
    public const int ConfirmTimeoutMs = 8000;
    public const int RetryIntervalMs = 800;
    public const int MaxRetries = 6;

    private int slot = -1;
    private int setSlot = -1;
    private uint itemId;
    private long since;

    public int Retries { get; private set; }
    public bool Waiting => slot >= 0;

    public void Reset()
    {
        slot = -1;
        setSlot = -1;
        Retries = 0;
    }

    /// Remember the request we just sent so Ready() can wait for its effect.
    public void Arm(int dresserSlot, int dresserSetSlot, uint expectedItemId)
    {
        slot = dresserSlot;
        setSlot = dresserSetSlot;
        itemId = expectedItemId;
        since = Environment.TickCount64;
        Retries = 0;
    }

    /// True when a new request may be sent. While waiting, returns false until the previous request's effect
    /// is visible (slot emptied / set piece locked) and the client is no longer busy, or the wait times out.
    public bool Ready(MirageManager* manager, IPluginLog? log, bool debug)
    {
        if (manager == null) return false;
        if (slot < 0) return true;

        var elapsed = Environment.TickCount64 - since;
        var changed = setSlot < 0
            ? Normalize(manager->PrismBoxItemIds[slot]) != itemId
            : !manager->IsSetSlotUnlocked((uint)slot, setSlot);
        var busy = IsBusy(manager);

        if (changed)
        {
            if (debug) log?.Information("[KWC] restore confirmed slot {Slot}/{SetSlot} after {Ms}ms", slot, setSlot, elapsed);
            slot = -1;
            return true;
        }

        if (elapsed > ConfirmTimeoutMs)
        {
            log?.Warning("[KWC] restore of slot {Slot}/{SetSlot} not confirmed after {Ms}ms (changed={Changed}, busy={Busy}); continuing", slot, setSlot, elapsed, changed, busy);
            slot = -1;
            return true;
        }

        return false;
    }

    /// A refusal right after another request is usually "still busy": allow a few retries before giving up.
    public bool AllowRetry()
    {
        if (Retries >= MaxRetries) return false;
        Retries++;
        return true;
    }

    public static bool IsBusy(MirageManager* manager) => manager == null || !manager->PrismBoxLoaded || Requested(manager);

    private static bool Requested(MirageManager* manager)
    {
        try { return ReadRequested(manager); }
        catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadRequested(MirageManager* manager) => manager->PrismBoxRequested;

    private static uint Normalize(uint id) => id >= 1_000_000 ? id % 1_000_000 : id;
}
