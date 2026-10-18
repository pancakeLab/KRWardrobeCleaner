using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace KRWardrobeCleaner;

/// <summary>
/// Small, dependency-free widgets that give the window a consistent look: tinted rounded cards,
/// icon buttons (FontAwesome through Dalamud's icon font), coloured chips and a muted text style.
/// </summary>
internal static class UiKit
{
    public static readonly Vector4 Accent = new(0.49f, 0.42f, 0.82f, 1f);
    public static readonly Vector4 AccentHover = new(0.58f, 0.51f, 0.90f, 1f);
    public static readonly Vector4 AccentActive = new(0.40f, 0.33f, 0.72f, 1f);
    public static readonly Vector4 Danger = new(0.62f, 0.24f, 0.26f, 1f);
    public static readonly Vector4 DangerHover = new(0.74f, 0.30f, 0.32f, 1f);
    public static readonly Vector4 Ok = new(0.56f, 0.86f, 0.60f, 1f);
    public static readonly Vector4 Busy = new(1.00f, 0.82f, 0.42f, 1f);
    public static readonly Vector4 Warn = new(1.00f, 0.55f, 0.55f, 1f);
    public static readonly Vector4 Muted = new(0.66f, 0.66f, 0.70f, 1f);
    public static readonly Vector4 Tag = new(0.62f, 0.78f, 1.00f, 1f);
    public static readonly Vector4 Lavender = new(0.76f, 0.70f, 0.98f, 1f);
    public static readonly Vector4 BeigeBg = new(0.91f, 0.84f, 0.70f, 1f);
    public static readonly Vector4 BeigeText = new(0.32f, 0.23f, 0.10f, 1f);
    public static readonly Vector4 CardBg = new(1f, 1f, 1f, 0.035f);
    public static readonly Vector4 CardBorder = new(1f, 1f, 1f, 0.10f);

    private static readonly Stack<(Vector2 Start, Vector4 Background)> Cards = new();
    private static readonly Vector2 CardPadding = new(12, 10);

    /// Starts a tinted, rounded card that grows with its content (drawn with a split draw list, so it works on
    /// every ImGui version). Always pair with EndCard().
    public static bool BeginCard(string id, Vector4? background = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + CardPadding);
        ImGui.PushID(id);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - CardPadding.X);
        Cards.Push((start, background ?? CardBg));
        return true;
    }

    public static void EndCard()
    {
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.PopID();
        var (start, background) = Cards.Pop();
        var max = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X, ImGui.GetItemRectMax().Y + CardPadding.Y);
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, max, ImGui.GetColorU32(background), 8f);
        drawList.AddRect(start, max, ImGui.GetColorU32(CardBorder), 8f);
        drawList.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y));
        ImGui.Dummy(new Vector2(1, 1));
        ImGui.Spacing();
    }

    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (color.HasValue) ImGui.TextColored(color.Value, icon.ToIconString());
        else ImGui.TextUnformatted(icon.ToIconString());
        ImGui.PopFont();
    }

    public static Vector2 ButtonSize(FontAwesomeIcon? icon, string label)
    {
        var pad = ImGui.GetStyle().FramePadding;
        var width = ImGui.CalcTextSize(label).X + pad.X * 2;
        if (icon.HasValue)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            width += ImGui.CalcTextSize(icon.Value.ToIconString()).X + 6;
            ImGui.PopFont();
        }
        return new Vector2(width, ImGui.GetFrameHeight());
    }

    /// A button with an optional icon in front of the label. kind: 0 normal, 1 accent, 2 danger.
    public static bool Button(FontAwesomeIcon? icon, string label, string id, int kind = 0, string? tooltip = null, float minWidth = 0)
    {
        var size = ButtonSize(icon, label);
        if (size.X < minWidth) size.X = minWidth;
        var pushed = 0;
        if (kind == 1)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
            pushed = 3;
        }
        else if (kind == 2)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Danger);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, DangerHover);
            pushed = 2;
        }

        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##{id}", size);
        if (pushed > 0) ImGui.PopStyleColor(pushed);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var pad = ImGui.GetStyle().FramePadding;
        var x = pos.X + pad.X + Math.Max(0, (size.X - ButtonSize(icon, label).X) / 2);
        if (icon.HasValue)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            var iconText = icon.Value.ToIconString();
            var iconWidth = ImGui.CalcTextSize(iconText).X;
            drawList.AddText(new Vector2(x, pos.Y + pad.Y), textColor, iconText);
            ImGui.PopFont();
            x += iconWidth + 6;
        }
        drawList.AddText(new Vector2(x, pos.Y + pad.Y), textColor, label);
        return clicked;
    }

    /// Square icon-only button (toolbar style).
    public static bool IconButton(FontAwesomeIcon icon, string id, string tooltip, bool active = false)
    {
        var height = ImGui.GetFrameHeight();
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, AccentActive);
        ImGui.PushFont(UiBuilder.IconFont);
        var clicked = ImGui.Button($"{icon.ToIconString()}##{id}", new Vector2(height, height));
        ImGui.PopFont();
        if (active) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// Small rounded label (status / tag).
    public static void Chip(string text, Vector4 color, bool sameLine = false)
    {
        if (sameLine) ImGui.SameLine();
        var pad = new Vector2(7, 2);
        var size = ImGui.CalcTextSize(text) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.18f)), 6f);
        drawList.AddText(pos + pad, ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    /// Chip with an opaque background (e.g. beige gear-set tag).
    public static void SolidChip(string text, Vector4 background, Vector4 foreground, bool sameLine = false)
    {
        if (sameLine) ImGui.SameLine();
        var pad = new Vector2(7, 2);
        var size = ImGui.CalcTextSize(text) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(background), 6f);
        drawList.AddText(pos + pad, ImGui.GetColorU32(foreground), text);
        ImGui.Dummy(size);
    }

    public static void Chips(params (bool Show, string Text, Vector4 Color)[] chips)
    {
        var first = true;
        foreach (var (show, text, color) in chips)
        {
            if (!show) continue;
            Chip(text, color, !first);
            first = false;
        }
    }

    public static void MutedText(string text, bool wrap = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        if (wrap) ImGui.TextWrapped(text);
        else ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// A "ⓘ" marker with a tooltip, placed on the same line.
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        Icon(FontAwesomeIcon.InfoCircle, Muted);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public static void Progress(int done, int total)
    {
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.ProgressBar(total <= 0 ? 0f : Math.Clamp(done / (float)total, 0f, 1f), new Vector2(-1, 8), string.Empty);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// Moves the cursor so that a widget of the given width ends at the right edge of the current line.
    public static void AlignRight(float width)
    {
        var target = ImGui.GetWindowContentRegionMax().X - width;
        if (target > ImGui.GetCursorPosX()) ImGui.SameLine(target);
        else ImGui.SameLine();
    }
}
