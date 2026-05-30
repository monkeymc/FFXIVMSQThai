using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivMsqThai.Windows;

public sealed class TalkAnchorWidget : Window, IDisposable
{
    // ── Button geometry ───────────────────────────────────────────────────────
    private const float BtnW    = 44f;
    private const float BtnH    = 26f;
    private const float MarginH =  8f;
    private const float MarginV =  5f;

    // ── Palette ───────────────────────────────────────────────────────────────
    private static readonly Vector4 BtnBg        = new(0.18f, 0.14f, 0.08f, 0.92f);
    private static readonly Vector4 BtnBgHover   = new(0.32f, 0.25f, 0.10f, 0.98f);
    private static readonly Vector4 BtnBgActive  = new(0.38f, 0.30f, 0.12f, 1.00f);
    private static readonly Vector4 BtnBorder    = new(0.72f, 0.58f, 0.28f, 0.90f);
    private static readonly Vector4 BtnBorderHot = new(1.00f, 0.86f, 0.32f, 1.00f);
    private static readonly Vector4 TextEnabled  = new(0.95f, 0.80f, 0.35f, 1.00f);
    private static readonly Vector4 TextDisab    = new(0.60f, 0.55f, 0.50f, 1.00f);

    private static readonly Vector4 PopupBg      = new(0.11f, 0.10f, 0.08f, 0.97f);
    private static readonly Vector4 PopupBorder  = new(0.55f, 0.47f, 0.30f, 0.85f);
    private static readonly Vector4 HeaderColor  = new(0.78f, 0.68f, 0.44f, 1.00f);

    private static readonly Vector4 RadioDot     = new(0.85f, 0.70f, 0.28f, 1.00f);
    private static readonly Vector4 RadioDotHov  = new(0.95f, 0.82f, 0.38f, 1.00f);
    private static readonly Vector4 RadioFrmHov  = new(0.72f, 0.60f, 0.32f, 1.00f);

    private static readonly Vector4 FntBg        = new(0.20f, 0.17f, 0.10f, 0.85f);
    private static readonly Vector4 FntHover     = new(0.72f, 0.58f, 0.28f, 0.25f);
    private static readonly Vector4 FntActive    = new(0.72f, 0.58f, 0.28f, 0.45f);
    private static readonly Vector4 FntBorder    = new(0.55f, 0.47f, 0.30f, 0.70f);
    private static readonly Vector4 FntText      = new(0.90f, 0.80f, 0.55f, 1.00f);

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly PluginConfig _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IGameGui _gameGui;
    private readonly MsqOverlayWindow _overlay;

    // Fixed 16 px Thai font — menu labels only, independent of overlay font size
    private readonly IFontHandle _menuFont;

    // ── Diagnostics ───────────────────────────────────────────────────────────
    private static readonly string[] _diagAddons =
        { "Talk", "TalkSubtitle", "CutSceneSubtitle", "CutsceneDialogue" };
    private static readonly string[] _cutsceneAddons =
        { "TalkSubtitle", "CutSceneSubtitle", "CutsceneDialogue" };
    private bool   _lastDrawCondition = false;
    private string _activeAddon = "Talk";

    public TalkAnchorWidget(
        PluginConfig config,
        IDalamudPluginInterface pi,
        IGameGui gameGui,
        MsqOverlayWindow overlay)
        : base("##msq-th-anchor",
            ImGuiWindowFlags.NoTitleBar        |
            ImGuiWindowFlags.NoResize          |
            ImGuiWindowFlags.NoMove            |
            ImGuiWindowFlags.NoScrollbar       |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse        |
            ImGuiWindowFlags.NoSavedSettings   |
            ImGuiWindowFlags.NoFocusOnAppearing|
            ImGuiWindowFlags.NoNav             |
            ImGuiWindowFlags.NoBackground      |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config  = config;
        _pi      = pi;
        _gameGui = gameGui;
        _overlay = overlay;

        var fontPath = Path.Combine(
            pi.AssemblyLocation.Directory!.FullName,
            "THSarabunNew-Bold.ttf");

        _menuFont = pi.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk => tk.AddFontFromFile(fontPath,
                new SafeFontConfig { SizePx = 26f })));

        IsOpen = true;
    }

    public void Dispose() => _menuFont.Dispose();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override unsafe bool DrawConditions()
    {
        // 1. ตรวจสอบการมองเห็นของหน้าต่าง Addon ดั้งเดิมของเกม
        var talkPtr = _gameGui.GetAddonByName("Talk");
        bool isNativeVisible = false;
        string newAddon = "Talk";

        if (talkPtr.Address != nint.Zero && ((AtkUnitBase*)talkPtr.Address)->IsVisible)
        {
            isNativeVisible = true;
            newAddon = "Talk";
        }
        else
        {
            foreach (var name in _cutsceneAddons)
            {
                var p = _gameGui.GetAddonByName(name);
                if (p.Address != nint.Zero && ((AtkUnitBase*)p.Address)->IsVisible)
                {
                    isNativeVisible = true;
                    newAddon = name;
                    break;
                }
            }
        }

        // (โค้ดสำหรับเก็บ Log เดิม)
        if (isNativeVisible != _lastDrawCondition || newAddon != _activeAddon)
        {
            Plugin.Log.Debug($"[TalkAnchorWidget] visible → {isNativeVisible}  addon={newAddon}  (was {_lastDrawCondition}/{_activeAddon})");
            foreach (var name in _diagAddons)
            {
                var p = _gameGui.GetAddonByName(name);
                if (p.Address == nint.Zero)
                    Plugin.Log.Debug($"[TalkAnchorWidget]   {name,-22} not found");
                else
                {
                    var a = (AtkUnitBase*)p.Address;
                    Plugin.Log.Debug($"[TalkAnchorWidget]   {name,-22} found  visible={a->IsVisible}  pos=({a->X},{a->Y})  scale={a->Scale:F2}");
                }
            }
            _lastDrawCondition = isNativeVisible;
            _activeAddon = newAddon;
        }

        // 2. [ส่วนที่ปรับปรุงใหม่] ควบคุมการ Render ปุ่ม
        // หากหน้าต่างเกมไม่ได้เปิดอยู่เลย ให้ซ่อนปุ่มทันที
        if (!isNativeVisible)
            return false;

        if (_config.Enabled)
        {
            // ถ้าเปิดแปลภาษาไทยอยู่: ให้แสดงปุ่มเฉพาะตอนที่ "กล่องข้อความแปลไทย" มีข้อความพร้อมและกำลัง Render อยู่เท่านั้น
            return _overlay.DrawConditions();
        }
        else
        {
            // ถ้าปิดการแปลไทยไว้ (เป็นโหมด EN): ให้แสดงปุ่มตามหน้าต่างเกมปกติ 
            // เพื่อให้ผู้เล่นสามารถมองเห็นปุ่มและคลิกเปลี่ยนกลับเป็นภาษาไทย (TH) ได้
            return true;
        }
    }

    public override unsafe void PreDraw()
    {
        var isCutscene = _activeAddon is "TalkSubtitle" or "CutSceneSubtitle" or "CutsceneDialogue";

        if (isCutscene)
        {
            // Position the button at the right edge of the MsqOverlayWindow text panel.
            // _overlay draws before _anchor in WindowSystem (added first), so LastPos/LastSize
            // are already set for this frame.
            Vector2 overlayPos, overlaySize;
            if (_overlay.LastSize.X > 0f)
            {
                overlayPos  = _overlay.LastPos;
                overlaySize = _overlay.LastSize;
            }
            else
            {
                // Fallback estimate if overlay hasn't drawn yet (first frame, plugin disabled, etc.)
                var io       = ImGui.GetIO();
                overlaySize  = new Vector2(Math.Min(1144f, io.DisplaySize.X * 0.85f), 80f);
                overlayPos   = new Vector2(
                    (io.DisplaySize.X - overlaySize.X) / 2f,
                    io.DisplaySize.Y - 180f - overlaySize.Y / 2f);
            }

            Position          = new Vector2(overlayPos.X + overlaySize.X - BtnW - MarginH, overlayPos.Y - BtnH - MarginV);
            PositionCondition = ImGuiCond.Always;
        }
        else
        {
            // Normal Talk addon — anchor to the native dialogue box
            var ptr    = _gameGui.GetAddonByName("Talk");
            var addonX = 0f;
            var addonY = 0f;
            var addonW = BtnW + MarginH * 2f;

            if (ptr.Address != nint.Zero)
            {
                var a  = (AtkUnitBase*)ptr.Address;
                addonX = (float)a->X;
                addonY = (float)a->Y;
                addonW = a->GetScaledWidth(true);
            }

            Position          = new Vector2(addonX + addonW - BtnW - MarginH, addonY + MarginV);
            PositionCondition = ImGuiCond.Always;
        }

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(BtnW, BtnH),
            MaximumSize = new Vector2(BtnW, BtnH)
        };

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(6f, 3f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        DrawAnchorButton();
        DrawMainPopup();
        DrawAdvancedPopup();
    }

    // ── Anchor button ─────────────────────────────────────────────────────────

    private void DrawAnchorButton()
    {
        var label     = _config.Enabled ? "TH" : "EN";
        var textColor = _config.Enabled ? TextEnabled : TextDisab;

        var winPos      = ImGui.GetWindowPos();
        var hovered     = ImGui.IsMouseHoveringRect(winPos, winPos + new Vector2(BtnW, BtnH), false);
        var borderColor = hovered ? BtnBorderHot : BtnBorder;

        ImGui.PushStyleColor(ImGuiCol.Text,          textColor);
        ImGui.PushStyleColor(ImGuiCol.Button,        BtnBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BtnBgHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  BtnBgActive);
        ImGui.PushStyleColor(ImGuiCol.Border,        borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.5f);

        if (ImGui.Button(label, new Vector2(BtnW, BtnH)))
            ImGui.OpenPopup("##mainmenu");

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##advmenu");

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    // ── Left-click popup ──────────────────────────────────────────────────────

    private void DrawMainPopup()
    {
        PushPopupStyle();
        if (ImGui.BeginPopup("##mainmenu"))
        {
            // ── Language section — labels in Thai font ────────────────────────
            using (_menuFont.Push())
            {
                SectionHeader("เลือกภาษาเนื้อเรื่อง");

                PushRadioStyle();

                bool clickTh = ImGui.RadioButton("##th", _config.Enabled);
                ImGui.SameLine(0, 8f);
                ImGui.Text("ภาษาไทย");
                if (clickTh && !_config.Enabled)
                {
                    _config.Enabled = true;
                    _config.Save();
                }

                bool clickEn = ImGui.RadioButton("##en", !_config.Enabled);
                ImGui.SameLine(0, 8f);
                ImGui.Text("ภาษาอังกฤษ");
                if (clickEn && _config.Enabled)
                {
                    _config.Enabled = false;
                    _config.Save();
                }

                PopRadioStyle();
            }

            ImGui.Spacing();

            // ── Font size section — header in Thai font, controls in default ──
            using (_menuFont.Push())
                SectionHeader("ขนาดตัวอักษร");

            // - / value / + row — same font + same FramePadding on all three items
            // so AlignTextToFramePadding() calculates the correct vertical offset.
            FontScaleButton(" - ", -2f);
            ImGui.SameLine(0f, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14f, 6f));
            ImGui.AlignTextToFramePadding();
            ImGui.PopStyleVar();
            using (_menuFont.Push())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, FntText);
                ImGui.Text($"{_config.FontSize:0} px");
                ImGui.PopStyleColor();
            }
            ImGui.SameLine(0f, 8f);
            FontScaleButton(" + ", +2f);

            ImGui.EndPopup();
        }
        PopPopupStyle();
    }

    // ── Right-click advanced popup ────────────────────────────────────────────

    private void DrawAdvancedPopup()
    {
        PushPopupStyle();
        if (ImGui.BeginPopup("##advmenu"))
        {
            using (_menuFont.Push())
            {
                SectionHeader("สถานะการแปล");
                ImGui.Text("  เป็นปัจจุบัน");

#if DEBUG
                ImGui.Spacing();
                SectionHeader("นักพัฒนา");
                ImGui.TextDisabled("Content root:");
                ImGui.TextDisabled(_config.ContentRoot);
#endif
            }

            ImGui.EndPopup();
        }
        PopPopupStyle();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FontScaleButton(string label, float delta)
    {
        ImGui.PushStyleColor(ImGuiCol.Text,          FntText);
        ImGui.PushStyleColor(ImGuiCol.Button,        FntBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, FntHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  FntActive);
        ImGui.PushStyleColor(ImGuiCol.Border,        FntBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,   5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,    new Vector2(14f, 6f));

        bool clicked;
        using (_menuFont.Push())
            clicked = ImGui.Button(label);

        if (clicked)
        {
            _config.FontSize = Math.Clamp(_config.FontSize + delta, 10f, 60f);
            _config.Save();
            _pi.UiBuilder.FontAtlas.BuildFontsAsync();
        }

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(5);
    }

    private static void PushRadioStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.CheckMark,      RadioDot);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.15f, 0.12f, 0.07f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, RadioFrmHov);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  RadioDotHov);
    }

    private static void PopRadioStyle() => ImGui.PopStyleColor(4);

    private static void SectionHeader(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, HeaderColor);
        ImGui.Text(text);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    private static void PushPopupStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PopupBg);
        ImGui.PushStyleColor(ImGuiCol.Border,  PopupBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   new Vector2(12f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,     new Vector2(8f, 7f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,   5f);
    }

    private static void PopPopupStyle()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
    }
}
