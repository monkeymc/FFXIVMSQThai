using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FfxivMsqThai.Services;

public sealed class TalkHook : IDisposable
{
    private const string AddonName = "Talk";

    private static readonly string[] CutsceneAddons = {
        "TalkSubtitle",
        "CutSceneSubtitle",
        "CutsceneDialogue"
    };

    public string ActiveAddonName { get; private set; } = "Talk";

    private static readonly Regex DashOnlyLine =
        new(@"^[\-–—\s]+$", RegexOptions.Compiled);
    private static readonly Regex InlineDashRun =
        new(@"[\-–—]{3,}", RegexOptions.Compiled);
    private static readonly Regex SeControl =
        new(@"[\x02][\s\S]{1,4}[\x03]|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]",
            RegexOptions.Compiled);

    private readonly IAddonLifecycle    _addonLifecycle;
    private readonly DialogueDictionary _dictionary;
    private readonly QuestDetector      _questDetector;
    private readonly IClientState       _clientState;
    private readonly IObjectTable       _objectTable;
    private readonly IPluginLog         _log = Plugin.Log;

    private string     _lastTextEn    = string.Empty;
    private string     _lastAddonName = string.Empty;
    private QuestInfo? _lastQuestInfo;
    private QuestInfo? _previousQuestInfo;

    public string[] CurrentTokens { get; private set; } = Array.Empty<string>();

    public TalkHook(
        IAddonLifecycle    addonLifecycle,
        DialogueDictionary dictionary,
        QuestDetector      questDetector,
        IClientState       clientState,
        IObjectTable       objectTable)
    {
        _addonLifecycle = addonLifecycle;
        _dictionary     = dictionary;
        _questDetector  = questDetector;
        _clientState    = clientState;
        _objectTable    = objectTable;

        _addonLifecycle.RegisterListener(AddonEvent.PreRefresh,  AddonName, OnPreRefresh);
        _addonLifecycle.RegisterListener(AddonEvent.PreHide,     AddonName, OnHide);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnHide);

        foreach (var addon in CutsceneAddons)
        {
            _addonLifecycle.RegisterListener(AddonEvent.PreSetup,    addon, OnPreRefresh);
            _addonLifecycle.RegisterListener(AddonEvent.PreRefresh,  addon, OnPreRefresh);
            _addonLifecycle.RegisterListener(AddonEvent.PreHide,     addon, OnHide);
            _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, addon, OnHide);
        }
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh,  AddonName, OnPreRefresh);
        _addonLifecycle.UnregisterListener(AddonEvent.PreHide,     AddonName, OnHide);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnHide);

        foreach (var addon in CutsceneAddons)
        {
            _addonLifecycle.UnregisterListener(AddonEvent.PreSetup,    addon, OnPreRefresh);
            _addonLifecycle.UnregisterListener(AddonEvent.PreRefresh,  addon, OnPreRefresh);
            _addonLifecycle.UnregisterListener(AddonEvent.PreHide,     addon, OnHide);
            _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addon, OnHide);
        }
    }

    private unsafe void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        ActiveAddonName = args.AddonName;

        // ── Read dialogue text ────────────────────────────────────────────
        string textEn;

        if (args.AddonName == AddonName)
        {
            if (args is not AddonRefreshArgs refreshArgs) return;
            var atkValues = (AtkValue*)refreshArgs.AtkValues;
            if (atkValues == null || refreshArgs.AtkValueCount < 1) return;

            var textPtr = (nint)atkValues[0].String.Value;
            if (textPtr == 0) { CurrentTokens = Array.Empty<string>(); return; }

            textEn = MemoryHelper.ReadSeStringAsString(out _, textPtr);
        }
        else if (args.AddonName is "TalkSubtitle" or "CutSceneSubtitle" or "CutsceneDialogue")
        {
            AtkValue* atkValues = args switch
            {
                AddonSetupArgs   s => (AtkValue*)s.AtkValues,
                AddonRefreshArgs r => (AtkValue*)r.AtkValues,
                _                  => null
            };

            if (atkValues != null
                && atkValues[0].Type == AtkValueType.String
                && atkValues[0].String.Value != null)
                textEn = MemoryHelper.ReadSeStringAsString(out _, (nint)atkValues[0].String.Value);
            else
                textEn = GetTextFromSubtitleAddon((AtkUnitBase*)args.Addon.Address);
        }
        else return;

        if (textEn == _lastTextEn && args.AddonName == _lastAddonName) return;
        _lastTextEn    = textEn;
        _lastAddonName = args.AddonName;

        if (string.IsNullOrWhiteSpace(textEn)) { CurrentTokens = Array.Empty<string>(); return; }

        // ── Quest detection ───────────────────────────────────────────────
        var questInfo = _questDetector.GetCurrentQuestInfo();
        LogQuestChangeIfNeeded(questInfo);

        // ── Normalize key ─────────────────────────────────────────────────
        var displayEn = DialogueDictionary.NormalizeEnglishKey(textEn);
        var fullName  = _objectTable.LocalPlayer?.Name.ToString() ?? string.Empty;
        var firstName = !string.IsNullOrEmpty(fullName) ? fullName.Split(' ')[0] : string.Empty;

        if (!string.IsNullOrEmpty(fullName) && displayEn.Contains(fullName))
            displayEn = displayEn.Replace(fullName, "Forename Surname");
        else if (!string.IsNullOrEmpty(firstName) && displayEn.Contains(firstName))
            displayEn = displayEn.Replace(firstName, "Forename");

        if (displayEn.Length == 0) { CurrentTokens = Array.Empty<string>(); return; }
        
        var questSlug = questInfo?.Slug ?? string.Empty;

        _log.Information($"[MSQ-Thai] Lookup '{Clip(displayEn)}'  quest='{questInfo.Slug}'");

        // ── Unified Search (Token-based & Proportional) ───────────────────
        var translation = _dictionary.GetTranslation(questSlug, displayEn);

        if (translation == null && _previousQuestInfo != null && !string.IsNullOrEmpty(_previousQuestInfo.Slug))
        {
            _log.Information($"[MSQ-Thai] MISS on current, fallback to '{_previousQuestInfo.Slug}'");
            translation = _dictionary.GetTranslation(_previousQuestInfo.Slug, displayEn);
        }

        if (translation != null)
        {
            _log.Information($"[MSQ-Thai] HIT");
            ApplyTranslation(translation);
        }
        else
        {
            _log.Information($"[MSQ-Thai] MISS");
            CurrentTokens = Array.Empty<string>();
        }
    }

    // ── Quest-change log ──────────────────────────────────────────────────

    private void LogQuestChangeIfNeeded(QuestInfo? current)
    {
        if (current?.Id == _lastQuestInfo?.Id) return;
        _previousQuestInfo = _lastQuestInfo;
        _lastQuestInfo = current;

        if (current == null)
        {
            _log.Information("[MSQ-Thai-Quest] Quest: (none detected)");
            return;
        }

        var filePath = _dictionary.GetQuestFilePath(current.Slug);
        var keyCount = _dictionary.GetQuestKeyCount(current.Slug);

        _log.Information(
            $"[MSQ-Thai-Quest] Quest: id={current.Id}  name='{current.Name}'  slug='{current.Slug}'");

        if (filePath != null)
            _log.Information($"[MSQ-Thai-Quest]   File: {filePath}  ({keyCount} dialogue pairs)");
        else
            _log.Warning($"[MSQ-Thai-Quest]   File: NOT FOUND for slug='{current.Slug}'");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void OnHide(AddonEvent type, AddonArgs args)
    {
        CurrentTokens  = Array.Empty<string>();
        _lastTextEn    = string.Empty;
        _lastAddonName = string.Empty;
    }

    private void ApplyTranslation(string rawThai)
    {
        var clean = SanitizeThai(rawThai);
        CurrentTokens = string.IsNullOrWhiteSpace(clean)
            ? Array.Empty<string>()
            : ThaiWordSegmenter.Segment(clean);
    }

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    internal static string SanitizeThai(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (DashOnlyLine.IsMatch(text)) return string.Empty;
        text = SeControl.Replace(text, string.Empty);
        text = InlineDashRun.Replace(text, string.Empty);
        return text.Trim();
    }

    private unsafe string GetTextFromSubtitleAddon(AtkUnitBase* addon)
    {
        if (addon == null) return string.Empty;
        var textNode = FindTextNode(addon->RootNode);
        if (textNode == null) return string.Empty;

        var strPtr = ((AtkTextNode*)textNode)->NodeText.StringPtr.Value;
        if (strPtr == null) return string.Empty;

        return MemoryHelper.ReadSeStringAsString(out _, (nint)strPtr);
    }

    private unsafe AtkResNode* FindTextNode(AtkResNode* node)
    {
        if (node == null) return null;
        if ((int)node->Type == 3) return node;
        var child = FindTextNode(node->ChildNode);
        if (child != null) return child;
        return FindTextNode(node->NextSiblingNode);
    }
}
