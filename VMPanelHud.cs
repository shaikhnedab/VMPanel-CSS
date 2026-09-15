using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VMPanel;

/// <summary>
/// Main VIP panel + toast notifications, driven entirely through
/// PanoramaManager <see cref="PanelHandle"/> (no raw CCSCustomHudLayout).
/// All text is per-viewer via SetVariableFor so concurrent viewers never
/// overwrite each other (PanelHandle.Title/Items are shared state).
/// </summary>
public sealed class VMPanelHud : IDisposable
{
    private readonly VMPanelPlugin _plugin;
    private PanelHandle? _main;
    private PanelHandle? _toast;
    private readonly Dictionary<ulong, CssTimer> _toastTimers = new();
    private bool _disposed;

    // Compiled resource names (engine resolves the _c assets shipped in the
    // mounted workshop addon). Source files live in workshop/panorama/.
    private const string LayoutPath = "panorama/layout/custom_game/vmpanel_menu.vxml_c";
    private const string ToastPath = "panorama/layout/custom_game/vmpanel_toast.vxml_c";

    public VMPanelHud(VMPanelPlugin plugin)
    {
        _plugin = plugin;
    }

    public void Initialize()
    {
        _main = Panorama.Spawn(LayoutPath, new LayoutContract
        {
            RootPanelId = "VMPanelRoot",
            RowCount = 6,
            RevealClass = "vmpanel-visible",
            CloseButtonId = "action_close",
            CaptureInput = true,
            // Spectating opens are refused inside the library when the viewer
            // watches another slot (engine would draw the target's state).
            // Null = silent refusal; we detect it via IsOpenFor and fall
            // back to chat instead.
            SpectatingMessage = null,
            HideFromSpectators = false,
        });
        _main.OnEvent += OnMainEvent;

        _toast = Panorama.Spawn(ToastPath, new LayoutContract
        {
            RootPanelId = "VMToastRoot",
            RowCount = 1,
            CloseButtonId = "VMToastRoot",
            CaptureInput = false,
            SpectatingMessage = null,
            HideFromSpectators = false,
        });

        _plugin.Logger.LogInformation(
            "[VMPanel:Hud] Panorama handles spawned. main={Main} toast={Toast}",
            LayoutPath, ToastPath);
    }

    public void Dispose()
    {
        _disposed = true;

        foreach (var timer in _toastTimers.Values)
        {
            try { timer.Kill(); } catch { /* ignore */ }
        }

        _toastTimers.Clear();

        try { _main?.Dispose(); } catch { /* ignore */ }
        try { _toast?.Dispose(); } catch { /* ignore */ }

        _main = null;
        _toast = null;
    }

    // -----------------------------------------------------------------
    // Public API (css_vip always ensures open; X / css_vipclose closes)
    // -----------------------------------------------------------------

    public void Open(CCSPlayerController player)
    {
        if (_disposed || _main is null)
            return;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        // Order matters: Open first (creates the per-player session), then
        // write per-viewer variables, then render. Pre-Open writes are wiped
        // by the initial render, which is the blank-until-click bug.
        _main.Open(player);

        if (!_main.IsOpenFor(player))
        {
            // Spectating another player's slot (or a dead entity): the client
            // would draw the target's state, so the library refused — serve
            // the same content over chat instead.
            ChatFallback(player);
            return;
        }

        Populate(player);
        _main.Refresh(player);

        _plugin.Logger.LogInformation(
            "[VMPanel:Hud] Opened Panorama menu for {Name} ({SteamId})",
            player.PlayerName, player.SteamID);
    }

    /// <summary>
    /// Full menu content over chat for viewers whose HUD was refused
    /// (spectating) or failed. Mirrors what the panel would show.
    /// </summary>
    public void ChatFallback(CCSPlayerController player)
    {
        var vip = _plugin.GetVipRecord(player.SteamID);

        player.PrintToChat(" ");
        player.PrintToChat(_plugin.T(player, "hud.fallback_head"));

        if (vip is not null)
        {
            var daysLeft = vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            player.PrintToChat(_plugin.T(player, "hud.fallback_vip", vip.Name, daysLeft, _plugin.FormatExpiry(vip.ExpireStamp)));
            player.PrintToChat(_plugin.T(player, "hud.fallback_renew", _plugin.Config.PanelUrl));
        }
        else
        {
            player.PrintToChat(_plugin.T(player, "hud.fallback_notvip"));
            player.PrintToChat(_plugin.T(player, "hud.fallback_purchase", _plugin.Config.PanelUrl));
        }

        player.PrintToChat(_plugin.T(player, "hud.fallback_cmds"));
        player.PrintToChat(" ");
    }

    public void Close(CCSPlayerController player)
    {
        if (_disposed)
            return;

        try { _main?.Close(player); } catch { /* ignore */ }
        try { _toast?.Close(player); } catch { /* ignore */ }
    }

    public void CloseSlot(int slot)
    {
        if (_disposed)
            return;

        try { _main?.Close(slot); } catch { /* ignore */ }
        try { _toast?.Close(slot); } catch { /* ignore */ }
    }

    public void CloseAll()
    {
        if (_disposed)
            return;

        foreach (var player in CounterStrikeSharp.API.Utilities.GetPlayers())
        {
            try { _main?.Close(player); } catch { /* ignore */ }
        }
    }

    public bool IsOpenFor(CCSPlayerController player) =>
        _main?.IsOpenFor(player) ?? false;

    public void Refresh(CCSPlayerController player)
    {
        if (_disposed || _main is null)
            return;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        if (!_main.IsOpenFor(player))
            return;

        Populate(player);
        _main.Refresh(player);
    }

    public void RefreshAll()
    {
        if (_disposed || _main is null)
            return;

        foreach (var player in CounterStrikeSharp.API.Utilities.GetPlayers())
        {
            if (!VMPanelPlugin.IsUsablePlayer(player))
                continue;

            if (!_main.IsOpenFor(player))
                continue;

            Populate(player);
            _main.Refresh(player);
        }
    }

    public void ShowToast(CCSPlayerController player, string title, string subtitle)
    {
        if (_disposed || _toast is null)
            return;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        if (!_plugin.Config.ToastEnabled)
            return;

        // Same ordering as Open: session first, then variables, then render.
        _toast.Open(player);
        _toast.SetVariableFor(player, "toast_title", title);
        _toast.SetVariableFor(player, "toast_subtitle", subtitle);
        _toast.Refresh(player);

        var steamId = player.SteamID;

        if (_toastTimers.Remove(steamId, out var old))
        {
            try { old.Kill(); } catch { /* ignore */ }
        }

        _toastTimers[steamId] = _plugin.AddTimer(
            _plugin.Config.ToastDurationSeconds,
            () =>
            {
                _toastTimers.Remove(steamId);
                try { _toast?.Close(player); } catch { /* ignore */ }
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    // -----------------------------------------------------------------
    // Content (per-viewer variables — never the shared Title/Subtitle)
    // -----------------------------------------------------------------

    private void Populate(CCSPlayerController player)
    {
        if (_main is null)
            return;

        var vip = _plugin.GetVipRecord(player.SteamID);
        var name = player.PlayerName;

        _main.SetVariableFor(player, "header_title", _plugin.T(player, "hud.menu_title"));
        _main.SetVariableFor(player, "header_subtitle", _plugin.T(player, "hud.welcome", name));

        if (vip is not null)
        {
            var daysLeft = vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _main.SetVariableFor(player, "status_title", _plugin.T(player, "hud.status_vip", vip.Name));
            _main.SetVariableFor(player, "status_subtitle",
                _plugin.T(player, "hud.status_left", daysLeft, _plugin.FormatExpiry(vip.ExpireStamp)));

            _main.SetVariableFor(player, "action_renew_title", _plugin.T(player, "hud.renew_title"));
            _main.SetVariableFor(player, "action_renew_subtitle",
                _plugin.T(player, "hud.renew_sub", _plugin.Config.PanelUrl));
        }
        else
        {
            _main.SetVariableFor(player, "status_title", _plugin.T(player, "hud.notvip_title"));
            _main.SetVariableFor(player, "status_subtitle",
                _plugin.T(player, "hud.notvip_sub", _plugin.Config.PanelUrl));

            _main.SetVariableFor(player, "action_renew_title", _plugin.T(player, "hud.buy_title"));
            _main.SetVariableFor(player, "action_renew_subtitle",
                _plugin.T(player, "hud.buy_sub"));
        }

        var isAdmin = AdminManager.PlayerHasPermissions(player, _plugin.Config.AdminMenuPermission)
            || AdminManager.PlayerHasPermissions(player, "@css/root");

        if (isAdmin)
        {
            _main.SetVariableFor(player, "action_admin_title", _plugin.T(player, "hud.admin_title"));
            _main.SetVariableFor(player, "action_admin_subtitle",
                _plugin.T(player, "hud.admin_sub"));
            _main.SetClassFor(player, "VMPanelRoot", "vmpanel-admin", true);
        }
        else
        {
            _main.SetClassFor(player, "VMPanelRoot", "vmpanel-admin", false);
        }

        _main.SetVariableFor(player, "action_close_title", _plugin.T(player, "hud.close_title"));
        _main.SetVariableFor(player, "action_close_subtitle",
            _plugin.T(player, "hud.close_sub"));
    }

    // -----------------------------------------------------------------
    // Click handling
    // -----------------------------------------------------------------

    private void OnMainEvent(PanelEvent e)
    {
        var player = e.Player;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        // Restored after round restart / map change: redraw per-viewer vars.
        if (e.Action == PanelAction.Restored)
        {
            Populate(player);
            return;
        }

        if (e.Action == PanelAction.Close)
            return;

        if (e.Action != PanelAction.Button && e.Action != PanelAction.Click)
            return;

        switch (e.ElementId)
        {
            case "action_close":
                try { _main?.Close(player); } catch { /* ignore */ }
                break;

            case "action_renew":
                player.PrintToChat(
                    _plugin.T(player, "hud.renew_chat", _plugin.Config.PanelUrl));
                break;

            case "action_admin":
                if (AdminManager.PlayerHasPermissions(player, _plugin.Config.AdminMenuPermission) ||
                    AdminManager.PlayerHasPermissions(player, "@css/root"))
                    _plugin.GetMenu()?.OpenAdminMenu(player);
                else
                    player.PrintToChat(
                        _plugin.T(player, "hud.no_perm_chat"));
                break;

            case "action_status":
                var vip = _plugin.GetVipRecord(player.SteamID);
                if (vip is null)
                {
                    player.PrintToChat(
                        _plugin.T(player, "hud.not_vip_chat", _plugin.Config.PanelUrl));
                    return;
                }

                player.PrintToChat(
                    _plugin.T(player, "hud.status_chat", vip.Name, vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), _plugin.FormatExpiry(vip.ExpireStamp)));
                break;

            default:
                _plugin.Logger.LogWarning(
                    "[VMPanel:Hud] Unhandled button id: {ButtonId}", e.ElementId);
                break;
        }
    }

    // -----------------------------------------------------------------
    // Debug
    // -----------------------------------------------------------------

    public void DumpDebugState()
    {
        _plugin.Logger.LogInformation("[VMPanel:Hud] === DEBUG DUMP ===");
        _plugin.Logger.LogInformation("[VMPanel:Hud] LayoutPath = {Path}", LayoutPath);
        _plugin.Logger.LogInformation("[VMPanel:Hud] ToastPath = {Path}", ToastPath);
        _plugin.Logger.LogInformation(
            "[VMPanel:Hud] CanReceiveClicks = {V}, CanWritePerPlayerText = {W}",
            Panorama.CanReceiveClicks, Panorama.CanWritePerPlayerText);
        _plugin.Logger.LogInformation(
            "[VMPanel:Hud] Main OpenCount = {C}", _main?.OpenCount ?? -1);
        _plugin.Logger.LogInformation(
            "[VMPanel:Hud] Toast OpenCount = {C}", _toast?.OpenCount ?? -1);

        _plugin.Logger.LogInformation("[VMPanel:Hud] === END DUMP ===");
    }
}
