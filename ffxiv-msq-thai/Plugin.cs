using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivMsqThai.Services;
using FfxivMsqThai.Windows;

namespace FfxivMsqThai;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string ToggleCommand = "/th";
    private const string FontCommand   = "/thf";

    private readonly WindowSystem _windowSystem = new("ffxiv-msq-thai");
    private readonly PluginConfig _config;
    private readonly DialogueDictionary _dictionary;
    private readonly TalkHook _talkHook;
    private readonly MsqOverlayWindow _overlay;
    private readonly TalkAnchorWidget _anchor;

    public Plugin()
    {
        _config     = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        var assemblyDir = PluginInterface.AssemblyLocation.Directory!.FullName;
        ThaiWordSegmenter.LoadDictionary(assemblyDir, Log);
        var contentRoot = string.IsNullOrEmpty(_config.ContentRoot) ? assemblyDir : _config.ContentRoot;
        _dictionary   = new DialogueDictionary(contentRoot, Log);
        _talkHook     = new TalkHook(AddonLifecycle, _dictionary, ClientState, ObjectTable);
        _overlay    = new MsqOverlayWindow(_talkHook, GameGui, PluginInterface, _config);
        _anchor     = new TalkAnchorWidget(_config, PluginInterface, GameGui, _overlay);

        _windowSystem.AddWindow(_overlay);
        _windowSystem.AddWindow(_anchor);

        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        PluginInterface.UiBuilder.Draw                  += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _anchor.Toggle;

        CommandManager.AddHandler(ToggleCommand, new CommandInfo(OnToggleCommand)
        {
            HelpMessage = "/th — toggle Thai overlay"
        });
        CommandManager.AddHandler(FontCommand, new CommandInfo(OnFontCommand)
        {
            HelpMessage = "/thf <size> — set overlay font size (10–60)"
        });

        Log.Information($"[ffxiv-msq-thai] Ready — {_dictionary.Count} sentences indexed.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        CommandManager.RemoveHandler(FontCommand);
        PluginInterface.UiBuilder.OpenConfigUi -= _anchor.Toggle;
        PluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
        _overlay.Dispose();
        _anchor.Dispose();
        _talkHook.Dispose();
    }

    private void OnToggleCommand(string command, string args)
    {
        _config.Enabled = !_config.Enabled;
        _config.Save();
        ChatGui.Print(new XivChatEntry
        {
            Message = new SeStringBuilder().AddText($"[msq-thai] Turn → {(_config.Enabled ? "ON" : "OFF")}").Build(),
            Type    = XivChatType.Debug,
        });
        Log.Information($"[ffxiv-msq-thai] Overlay {(_config.Enabled ? "ON" : "OFF")}.");
    }

    private void OnFontCommand(string command, string args)
    {
        if (float.TryParse(args.Trim(), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var size))
        {
            _config.FontSize = Math.Clamp(size, 10f, 60f);
            _config.Save();
            PluginInterface.UiBuilder.FontAtlas.BuildFontsAsync();
            ChatGui.Print(new XivChatEntry
            {
                Message = new SeStringBuilder().AddText($"[msq-thai] Font Size → {_config.FontSize} px").Build(),
                Type    = XivChatType.Debug,
            });
            Log.Information($"[ffxiv-msq-thai] Font size set to {_config.FontSize} px.");
        }
        else
        {
            Log.Information("[ffxiv-msq-thai] Usage: /thf <size>  (valid range 10–60)");
        }
    }
}
