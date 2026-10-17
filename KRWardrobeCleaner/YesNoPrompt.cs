using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KRWardrobeCleaner;

/// <summary>
/// Reads and answers the game's yes/no dialog (SelectYesno) without relying on a single struct field.
/// The prompt is read from the addon's AtkValues first (slot 0 is the prompt string on every client;
/// see FFXIVClientStructs AddonSelectYesno.StandardAtkValues) and only then from the PromptText node,
/// which was empty on the Korean client. "Yes" is sent exactly as the game's own button does it:
/// FireCallback with one int value 0 and close=true (confirmed with the callback recorder).
/// </summary>
internal static unsafe class YesNoPrompt
{
    /// Words that identify the glamour-dresser deposit prompt in Korean ("~을(를) 환영화 하겠습니까?").
    private static readonly string[] DresserKeywords = ["환영화", "맡기", "옷장", "환영"];

    public static bool IsOpen(AddonSelectYesno* yesno)
        => yesno != null && yesno->AtkUnitBase.IsVisible;

    public static string Read(AddonSelectYesno* yesno, out string source)
    {
        source = "-";
        if (yesno == null) return string.Empty;

        try
        {
            var values = yesno->AtkUnitBase.AtkValues;
            var count = yesno->AtkUnitBase.AtkValuesCount;
            if (values != null && count > 0)
            {
                var v = values[0];
                if (v.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString && v.String.Value != null)
                {
                    var text = v.String.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        source = "atkvalue";
                        return text;
                    }
                }
            }
        }
        catch
        {
            // fall through to the text node
        }

        try
        {
            if (yesno->PromptText != null)
            {
                var text = yesno->PromptText->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    source = "node";
                    return text;
                }
            }
        }
        catch
        {
            // unreadable
        }

        return string.Empty;
    }

    public static bool LooksLikeDresserPrompt(string prompt)
        => DresserKeywords.Any(k => prompt.Contains(k, StringComparison.Ordinal));

    public static string Flatten(string prompt)
        => prompt.Replace("\r", string.Empty).Replace("\n", " ");

    /// Presses "Yes" the way the game's own button does (int 0, close=true).
    public static bool Yes(AddonSelectYesno* yesno)
    {
        if (yesno == null || !yesno->AtkUnitBase.IsVisible) return false;
        var value = new AtkValue();
        value.SetInt(0);
        yesno->AtkUnitBase.FireCallback(1, &value, true);
        return true;
    }
}
