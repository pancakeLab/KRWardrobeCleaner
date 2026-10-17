using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KRWardrobeCleaner;

/// <summary>
/// Debug aid: logs the AtkValues the game UI sends through AtkUnitBase.FireCallback for the
/// glamour-dresser windows and the yes/no dialog. Turn it on, operate the dresser by hand once,
/// and /xllog shows the exact callback the plugin has to replay.
/// </summary>
public sealed unsafe class CallbackRecorder : IDisposable
{
    private static readonly string[] Prefixes = ["MiragePrism", "SelectYesno", "Cabinet"];

    private readonly IPluginLog log;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback>? hook;

    public bool Enabled { get; set; }
    public bool HookOk => hook != null;

    public CallbackRecorder(IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;
        try
        {
            hook = interop.HookFromAddress<AtkUnitBase.Delegates.FireCallback>((nint)AtkUnitBase.MemberFunctionPointers.FireCallback, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[KWC] callback recorder could not hook FireCallback");
            hook = null;
        }
    }

    private bool Detour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        if (Enabled && addon != null)
        {
            try
            {
                var name = addon->NameString;
                if (Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                    log.Information("[KWC] callback {Addon} close={Close} values=[{Values}]", name, close, Format(valueCount, values));
            }
            catch
            {
                // never let diagnostics break the game call
            }
        }

        return hook!.Original(addon, valueCount, values, close);
    }

    private static string Format(uint count, AtkValue* values)
    {
        if (values == null || count == 0) return string.Empty;
        var parts = new List<string>();
        for (var i = 0; i < count && i < 32; i++)
        {
            var v = values[i];
            parts.Add(v.Type switch
            {
                AtkValueType.Int => $"int:{v.Int}",
                AtkValueType.UInt => $"uint:{v.UInt}",
                AtkValueType.Bool => $"bool:{v.Bool}",
                AtkValueType.Float => $"float:{v.Float}",
                AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString => $"str:\"{v.String.ToString()}\"",
                _ => $"{v.Type}:{v.Int}",
            });
        }
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
