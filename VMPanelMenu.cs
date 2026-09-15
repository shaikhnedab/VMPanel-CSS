using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using PanoramaManager;

namespace VMPanel;

/// <summary>
/// Panorama admin menu (PanoramaManager PanelHandle, button-driven).
/// Every admin function lives in the HUD: refresh, add (step-by-step chat
/// prompts echoed in-HUD + HUD confirm), browse/remove (paged HUD list with
/// inline confirm). Chat commands remain as an optional fallback.
/// All commands remain in <see cref="VMPanelPlugin"/> because
/// CounterStrikeSharp only scans the main plugin class for attributes.
/// </summary>
public sealed class VMPanelMenu : IDisposable
{
    private readonly VMPanelPlugin _plugin;
    private PanelHandle? _admin;
    private bool _disposed;

    private const string AdminLayoutPath = "panorama/layout/custom_game/vmpanel_admin.vxml_c";
    private const int PageSize = 8;

    private enum AdminView { Main, Browse, Add, Days, Remove }

    private sealed class AdminSession
    {
        public AdminView View = AdminView.Main;
        public bool PickForAdd;
        public ulong SelectedSteamId;
        public string SelectedName = string.Empty;
        public int AddStep; // 0 idle, 1 picked (need days), 2 ready to confirm
        public ulong AddSteamId;
        public int AddDays;
        public string AddName = string.Empty;
        public ulong DelSteamId;
        public string DelName = string.Empty;
        public int HomeToken;
    }

    private readonly Dictionary<ulong, AdminSession> _sessions = new();
    private readonly HashSet<ulong> _openAdmins = new();

    public VMPanelMenu(VMPanelPlugin plugin)
    {
        _plugin = plugin;
    }

    public void Initialize()
    {
        _admin = Panorama.Spawn(AdminLayoutPath, new LayoutContract
        {
            RootPanelId = "VMAdminRoot",
            RowCount = 8,
            RevealClass = "vmpanel-visible",
            CloseButtonId = "admin_close",
            CaptureInput = true,
            // See VMPanelHud: spectating refusal is silent; OpenAdminMenu
            // falls back to chat when IsOpenFor is false after Open.
            SpectatingMessage = null,
            HideFromSpectators = false,
        });
        _admin.OnEvent += OnAdminEvent;

        _plugin.Logger.LogInformation(
            "[VMPanel:Menu] Admin Panorama handle spawned. layout={Path}",
            AdminLayoutPath);
    }

    public void Dispose()
    {
        _disposed = true;

        try { _admin?.Dispose(); } catch { /* ignore */ }
        _admin = null;
        _sessions.Clear();
        _openAdmins.Clear();
    }

    public void CloseSlot(int slot)
    {
        if (_disposed)
            return;

        try { _admin?.Close(slot); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------------
    // Public helpers (called from VMPanelPlugin commands)
    // ---------------------------------------------------------------------

    public void OpenAdminMenu(CCSPlayerController player)
    {
        if (_disposed || _admin is null)
            return;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        if (!IsAdmin(player))
        {
            player.PrintToChat(
                _plugin.T(player, "general.no_permission"));
            return;
        }

        var session = GetSession(player.SteamID);
        session.View = AdminView.Main;
        session.PickForAdd = false;
        RebuildItems();
        // Order matters (see VMPanelHud.Open): session first, then content.
        _admin.Open(player);

        if (!_admin.IsOpenFor(player))
        {
            // Spectating another player's slot: serve the admin menu over
            // chat instead (all functions remain available as commands).
            ChatAdminFallback(player);
            return;
        }

        DrawMain(player, session);
        _admin.Refresh(player);
        _openAdmins.Add(player.SteamID);
    }

    /// <summary>
    /// Full admin menu over chat for viewers whose HUD was refused
    /// (spectating) or failed. Every function maps to a chat command.
    /// </summary>
    public void ChatAdminFallback(CCSPlayerController player)
    {
        var total = _plugin.GetAllVips().Count;

        player.PrintToChat(" ");
        player.PrintToChat(_plugin.T(player, "admin.fallback_head"));
        player.PrintToChat(_plugin.T(player, "admin.fallback_count", total));
        player.PrintToChat(_plugin.T(player, "admin.fallback_refresh"));
        player.PrintToChat(_plugin.T(player, "admin.fallback_online"));
        player.PrintToChat(_plugin.T(player, "admin.fallback_add"));
        player.PrintToChat(_plugin.T(player, "admin.fallback_del"));
        player.PrintToChat(_plugin.T(player, "admin.fallback_list"));
        player.PrintToChat(" ");
    }

    /// <summary>
    /// Rebuilds the shared browse list (called on open, after DB refresh,
    /// and after add/remove). Content is identical for every admin viewer.
    /// </summary>
    public void RebuildItems()
    {
        if (_disposed || _admin is null)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _admin.SetItems(_plugin.GetAllVips().Select(vip =>
        {
            var steamId = vip.SteamId;
            var name = vip.Name;
            var online = Utilities.GetPlayerFromSteamId64(steamId);
            var onlineMark = online is not null && VMPanelPlugin.IsUsablePlayer(online) ? " • online" : string.Empty;

            return new MenuItem(
                Id: $"vip:{steamId}",
                Title: $"{name} — {vip.DaysLeft(now)}d left{onlineMark}",
                Subtitle: $"{steamId} | exp {_plugin.FormatExpiry(vip.ExpireStamp)}",
                OnSelect: e => OnVipRowSelected(e.Player, steamId, name));
        }));
    }

    // ---------------------------------------------------------------------
    // Views
    // ---------------------------------------------------------------------

    private void SetView(CCSPlayerController player, AdminView view)
    {
        if (_admin is null)
            return;

        _admin.SetClassFor(player, "VMAdminRoot", "vmpanel-show-main", view == AdminView.Main);
        _admin.SetClassFor(player, "VMAdminRoot", "vmpanel-show-browse", view == AdminView.Browse);
        _admin.SetClassFor(player, "VMAdminRoot", "vmpanel-show-add", view == AdminView.Add);
        _admin.SetClassFor(player, "VMAdminRoot", "vmpanel-show-days", view == AdminView.Days);
        _admin.SetClassFor(player, "VMAdminRoot", "vmpanel-show-remove", view == AdminView.Remove);
    }

    private void DrawMain(CCSPlayerController player, AdminSession session)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Main;
        SetView(player, AdminView.Main);

        var total = _plugin.GetAllVips().Count;
        var online = CountOnlineVips();

        // Shared header: identical for every admin viewer.
        _admin.Title = _plugin.T(player, "admin.title");
        _admin.Subtitle = _plugin.T(player, "admin.subtitle", total, online);

        _admin.SetVariableFor(player, "admin_refresh_title", _plugin.T(player, "admin.refresh_title"));
        _admin.SetVariableFor(player, "admin_refresh_subtitle",
            _plugin.T(player, "admin.refresh_sub"));
        _admin.SetVariableFor(player, "admin_add_title", _plugin.T(player, "admin.add_title"));
        _admin.SetVariableFor(player, "admin_add_subtitle",
            _plugin.T(player, "admin.add_sub"));
        _admin.SetVariableFor(player, "admin_browse_title", _plugin.T(player, "admin.browse_title"));
        _admin.SetVariableFor(player, "admin_browse_subtitle",
            _plugin.T(player, "admin.browse_sub", total));
        _admin.SetVariableFor(player, "admin_del_title", _plugin.T(player, "admin.del_title"));
        _admin.SetVariableFor(player, "admin_del_subtitle",
            _plugin.T(player, "admin.del_sub"));

        WriteUiChrome(player);
        _admin.Refresh(player);
    }

    /// <summary>
    /// Static chrome labels (single language per viewer via per-viewer vars).
    /// </summary>
    private void WriteUiChrome(CCSPlayerController player)
    {
        if (_admin is null)
            return;

        _admin.SetVariableFor(player, "ui_back", _plugin.T(player, "ui.back"));
        _admin.SetVariableFor(player, "ui_cancel", _plugin.T(player, "ui.cancel"));
        _admin.SetVariableFor(player, "ui_prev", _plugin.T(player, "ui.prev"));
        _admin.SetVariableFor(player, "ui_next", _plugin.T(player, "ui.next"));
        _admin.SetVariableFor(player, "ui_close", _plugin.T(player, "ui.close"));
        _admin.SetVariableFor(player, "ui_close_sub", _plugin.T(player, "ui.close_sub"));
        _admin.SetVariableFor(player, "admin_add_confirm_title", _plugin.T(player, "admin.add_confirm_title"));
        _admin.SetVariableFor(player, "admin_del_confirm_title", _plugin.T(player, "admin.del_confirm_title"));
    }

    private void DrawBrowse(CCSPlayerController player, AdminSession session)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Browse;
        session.PickForAdd = false;
        SetView(player, AdminView.Browse);

        RebuildItems();

        var total = _plugin.GetAllVips().Count;

        _admin.Title = _plugin.T(player, "admin.vips_title");
        _admin.Subtitle = _plugin.T(player, "admin.vips_sub", total);

        _admin.SetVariableFor(player, "adminbrowse_title",
            total == 0 ? _plugin.T(player, "admin.browse_empty") : _plugin.T(player, "admin.browse_hint"));

        _admin.SetVariableFor(player, "adminbrowse_footer",
            session.SelectedSteamId != 0
                ? _plugin.T(player, "admin.browse_footer_selected", session.SelectedName, session.SelectedSteamId)
                : _plugin.T(player, "admin.browse_footer_idle"));

        _admin.Refresh(player);
    }

    /// <summary>
    /// Add flow entry: the browse view lists ONLINE players to tap.
    /// Name comes from the server (picked player's name) — no typing.
    /// </summary>
    private void StartAddPick(CCSPlayerController player, AdminSession session, bool rearmPrompt = true)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Browse;
        session.PickForAdd = true;
        session.AddStep = 0;
        session.AddSteamId = 0;
        session.AddDays = 0;
        session.AddName = string.Empty;
        SetView(player, AdminView.Browse);

        var online = GetOnlinePlayers();

        _admin.Title = _plugin.T(player, "admin.pick_title");
        _admin.Subtitle = _plugin.T(player, "admin.pick_sub", online.Count);

        _admin.SetItems(online.Select(p => new MenuItem(
            Id: $"pick:{p.SteamID}",
            Title: p.PlayerName,
            Subtitle: $"{p.SteamID}",
            OnSelect: e => OnPickPlayer(e.Player, p.SteamID, p.PlayerName))));

        _admin.SetVariableFor(player, "adminbrowse_title", _plugin.T(player, "admin.pick_hint"));
        _admin.SetVariableFor(player, "adminbrowse_footer",
            online.Count == 0
                ? _plugin.T(player, "admin.pick_empty")
                : _plugin.T(player, "admin.pick_footer"));

        _admin.Refresh(player);
    }

    private void OnPickPlayer(CCSPlayerController player, ulong steamId, string name)
    {
        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        var session = GetSession(player.SteamID);

        if (!session.PickForAdd || session.View != AdminView.Browse)
            return;

        session.PickForAdd = false;
        session.AddSteamId = steamId;
        session.AddName = name;
        session.AddDays = 0;
        session.AddStep = 1;
        DrawDays(player, session);
    }

    private void DrawDays(CCSPlayerController player, AdminSession session)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Days;
        SetView(player, AdminView.Days);

        var who = string.IsNullOrWhiteSpace(session.AddName)
            ? session.AddSteamId.ToString()
            : $"{session.AddName} ({session.AddSteamId})";

        _admin.Title = _plugin.T(player, "admin.days_title");
        _admin.Subtitle = who;

        _admin.SetVariableFor(player, "admin_days_summary",
            _plugin.T(player, "admin.days_summary", who));

        var options = _plugin.Config.VipDurationOptions;

        for (var i = 0; i < 4; i++)
        {
            if (i < options.Count)
            {
                _admin.SetVariableFor(player, $"admin_days_label{i}", _plugin.T(player, "admin.day_label", options[i]));
                _admin.SetClassFor(player, $"admin_days_opt{i}", "vmpanel-row-hidden", false);
            }
            else
            {
                _admin.SetVariableFor(player, $"admin_days_label{i}", string.Empty);
                _admin.SetClassFor(player, $"admin_days_opt{i}", "vmpanel-row-hidden", true);
            }
        }

        _admin.Refresh(player);
    }

    private void OnPickDayOption(CCSPlayerController player, int optionIndex)
    {
        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        var session = GetSession(player.SteamID);

        if (session.View != AdminView.Days || session.AddSteamId == 0)
            return;

        var options = _plugin.Config.VipDurationOptions;

        if (optionIndex < 0 || optionIndex >= options.Count)
            return;

        OnPickDays(player, options[optionIndex]);
    }

    private void OnPickDays(CCSPlayerController player, int days)
    {
        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        var session = GetSession(player.SteamID);

        if (session.View != AdminView.Days || session.AddSteamId == 0)
            return;

        session.AddDays = days;
        session.AddStep = 2;

        if (string.IsNullOrWhiteSpace(session.AddName))
            session.AddName = session.AddSteamId.ToString();

        DrawAdd(player, session);
    }

    private static IReadOnlyList<CCSPlayerController> GetOnlinePlayers()
    {
        var list = new List<CCSPlayerController>();

        foreach (var p in Utilities.GetPlayers())
        {
            if (!VMPanelPlugin.IsUsablePlayer(p))
                continue;

            list.Add(p);
        }

        list.Sort((a, b) => string.Compare(a.PlayerName, b.PlayerName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private void OnVipRowSelected(CCSPlayerController player, ulong steamId, string name)
    {
        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        var session = GetSession(player.SteamID);

        // Second tap on the selected row = go remove it.
        if (session.SelectedSteamId == steamId)
        {
            session.DelSteamId = steamId;
            session.DelName = name;
            DrawRemove(player, session);
            return;
        }

        session.SelectedSteamId = steamId;
        session.SelectedName = name;

        _admin?.SetVariableFor(player, "adminbrowse_footer",
            $"Selected: {name} ({steamId}) — tap again to remove");
    }

    private void DrawAdd(CCSPlayerController player, AdminSession session, string? stepOverride = null)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Add;
        SetView(player, AdminView.Add);

        _admin.Title = _plugin.T(player, "admin.add_review_title");
        _admin.Subtitle = _plugin.T(player, "admin.add_review_sub");

        var who = string.IsNullOrWhiteSpace(session.AddName)
            ? session.AddSteamId.ToString()
            : $"{session.AddName} ({session.AddSteamId})";

        var step = stepOverride ?? (session.AddStep == 2
            ? _plugin.T(player, "admin.add_ready", session.AddSteamId, session.AddName, session.AddDays)
            : _plugin.T(player, "admin.add_need"));

        _admin.SetVariableFor(player, "admin_add_step", step);
        _admin.SetVariableFor(player, "admin_add_echo", session.AddName);
        _admin.Refresh(player);
    }

    private void DrawRemove(CCSPlayerController player, AdminSession session, string? echoOverride = null)
    {
        if (_admin is null)
            return;

        session.View = AdminView.Remove;
        SetView(player, AdminView.Remove);

        _admin.Title = _plugin.T(player, "admin.del_title2");
        _admin.Subtitle = _plugin.T(player, "admin.del_sub2");

        _admin.SetVariableFor(player, "admin_del_echo",
            echoOverride ?? (session.DelSteamId != 0
                ? _plugin.T(player, "admin.del_ask", session.DelName, session.DelSteamId)
                : _plugin.T(player, "admin.del_nothing")));
        _admin.Refresh(player);
    }

    // ---------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------

    private void OnAdminEvent(PanelEvent e)
    {
        var player = e.Player;

        if (!VMPanelPlugin.IsUsablePlayer(player))
            return;

        // Central auth gate: veto everything that is not ours to serve.
        if (!IsAdmin(player))
        {
            e.Cancel = true;
            player.PrintToChat(
                _plugin.T(player, "general.no_permission"));
            return;
        }

        var session = GetSession(player.SteamID);

        if (e.Action == PanelAction.Restored)
        {
            _openAdmins.Add(player.SteamID);
            RedrawCurrent(player, session);
            return;
        }

        if (e.Action == PanelAction.Close)
        {
            _openAdmins.Remove(player.SteamID);
            return;
        }

        if (e.Action != PanelAction.Button && e.Action != PanelAction.Click)
            return;

        // Library row-pool clicks (vip:<steam>) are resolved to MenuItem
        // OnSelect handlers — nothing to decode here.
        if (e.Action == PanelAction.Click)
            return;

        switch (e.ElementId)
        {
            case "admin_close":
                _openAdmins.Remove(player.SteamID);
                try { _admin?.Close(player); } catch { /* ignore */ }
                break;

            case "admin_refresh":
                player.PrintToChat(_plugin.T(player, "vip.refresh_started"));
                RefreshAndRedraw(player.SteamID);
                break;

            case "admin_browse":
                session.PickForAdd = false;
                session.SelectedSteamId = 0;
                session.SelectedName = string.Empty;
                DrawBrowse(player, session);
                break;

            case "admin_add":
                if (session.AddStep != 0)
                {
                    DrawAdd(player, session, _plugin.T(player, "admin.add_busy"));
                    return;
                }
                StartAddPick(player, session);
                break;

            case "admin_add_manual":
                ResetAddState(session);
                DrawMain(player, session);
                PromptManualSteam(player);
                break;

            case "admin_days_opt0": OnPickDayOption(player, 0); break;
            case "admin_days_opt1": OnPickDayOption(player, 1); break;
            case "admin_days_opt2": OnPickDayOption(player, 2); break;
            case "admin_days_opt3": OnPickDayOption(player, 3); break;

            case "admin_days_back":
                if (session.PickForAdd || session.AddSteamId != 0)
                    StartAddPick(player, session);
                else
                    DrawMain(player, session);
                break;

            case "admin_add_confirm":
                ConfirmAdd(player);
                break;

            case "admin_add_cancel":
            case "admin_add_back":
                CancelAddFlow(player, session, toMain: e.ElementId == "admin_add_back");
                break;

            case "admin_del":
                DrawRemove(player, session);
                PromptDelSteam(player);
                break;

            case "admin_del_confirm":
                ConfirmRemove(player);
                break;

            case "admin_del_cancel":
            case "admin_del_back":
                session.DelSteamId = 0;
                session.DelName = string.Empty;
                DrawMain(player, session);
                break;

            case "adminvip_back":
                try { _admin?.CancelPrompt(player); } catch { /* ignore */ }
                session.PickForAdd = false;
                session.SelectedSteamId = 0;
                session.SelectedName = string.Empty;
                DrawMain(player, session);
                break;

            default:
                _plugin.Logger.LogWarning(
                    "[VMPanel:Menu] Unhandled button id: {ButtonId}", e.ElementId);
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Add flow: pick player (HUD) -> pick days (HUD buttons) -> name from
    // server -> confirm (HUD). Manual SteamID entry survives only as a
    // fallback for offline players.
    // ---------------------------------------------------------------------

    private void PromptManualSteam(CCSPlayerController player)
    {
        if (_admin is null)
            return;

        player.PrintToChat(_plugin.T(player, "admin.prompt_manual_steam"));

        _admin.PromptText(player, new TextPrompt
        {
            Variable = "admin_add_echo",
            Hint = _plugin.T(player, "admin.prompt_hint_steam"),
            CancelWord = _plugin.T(player, "admin.prompt_cancel_word"),
            MaxLength = 32,
            TimeoutSeconds = 60,
            OnResult = result =>
            {
                if (!VMPanelPlugin.IsUsablePlayer(player))
                    return;

                var s = GetSession(player.SteamID);

                if (result.Outcome != TextPromptOutcome.Submitted)
                {
                    player.PrintToChat(_plugin.T(player, "admin.prompt_manual_cancelled"));
                    DrawMain(player, s);
                    return;
                }

                if (!VMPanelPlugin.TryParseSteamId(result.Text.Trim(), out var steamId))
                {
                    player.PrintToChat(_plugin.T(player, "admin.prompt_invalid_retry"));
                    PromptManualSteam(player);
                    return;
                }

                // Name comes from the server when the player is online.
                var online = Utilities.GetPlayerFromSteamId64(steamId);
                s.AddSteamId = steamId;
                s.AddName = online is not null && VMPanelPlugin.IsUsablePlayer(online)
                    ? online.PlayerName
                    : steamId.ToString();
                s.AddDays = 0;
                s.AddStep = 1;
                s.PickForAdd = false;
                DrawDays(player, s);
            },
        });
    }

    private void ResetAddState(AdminSession session)
    {
        session.PickForAdd = false;
        session.AddStep = 0;
        session.AddSteamId = 0;
        session.AddDays = 0;
        session.AddName = string.Empty;
    }

    private void ConfirmAdd(CCSPlayerController player)
    {
        var session = GetSession(player.SteamID);

        if (session.AddStep != 2 || session.AddSteamId == 0 || session.AddDays <= 0)
        {
            DrawAdd(player, session, _plugin.T(player, "admin.add_incomplete"));
            return;
        }

        var steamId = session.AddSteamId;
        var days = session.AddDays;
        var name = string.IsNullOrWhiteSpace(session.AddName)
            ? steamId.ToString()
            : session.AddName;
        var creator = player.PlayerName;
        ResetAddState(session);

        DrawAdd(player, session, _plugin.T(player, "vip.result_working"));

        _ = Task.Run(async () =>
        {
            var (_, message) = await _plugin.GrantVipAsync(steamId, days, name, creator);

            Server.NextFrame(() =>
            {
                var admin = Utilities.GetPlayerFromSteamId64(player.SteamID);

                if (!VMPanelPlugin.IsUsablePlayer(admin) || _admin is null || !IsOpen(admin))
                    return;

                var s2 = GetSession(admin.SteamID);
                DrawAdd(admin, s2, message);
                admin.PrintToChat(message);
                ReturnHomeSoon(admin.SteamID, AdminView.Add, _plugin.Config.ReturnHomeDelaySeconds);
            });
        });
    }

    /// <summary>
    /// Shows the result briefly, then brings the admin menu back home —
    /// unless they already navigated elsewhere meanwhile.
    /// </summary>
    private void ReturnHomeSoon(ulong adminSteamId, AdminView fromView, float delaySeconds)
    {
        var session = GetSession(adminSteamId);
        var token = ++session.HomeToken;

        _plugin.AddTimer(
            delaySeconds,
            () =>
            {
                if (_disposed)
                    return;

                var admin = Utilities.GetPlayerFromSteamId64(adminSteamId);

                if (!VMPanelPlugin.IsUsablePlayer(admin) || _admin is null || !IsOpen(admin))
                    return;

                var s = GetSession(adminSteamId);

                if (s.HomeToken != token || s.View != fromView)
                    return;

                DrawMain(admin, s);
            },
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CancelAddFlow(CCSPlayerController player, AdminSession session, bool toMain)
    {
        try { _admin?.CancelPrompt(player); } catch { /* ignore */ }

        ResetAddState(session);
        _admin?.SetVariableFor(player, "admin_add_echo", string.Empty);

        if (toMain)
            DrawMain(player, session);
        else
            DrawAdd(player, session, _plugin.T(player, "admin.add_cancelled"));
    }

    // ---------------------------------------------------------------------
    // Remove flow
    // ---------------------------------------------------------------------

    private void PromptDelSteam(CCSPlayerController player)
    {
        if (_admin is null)
            return;

        player.PrintToChat(_plugin.T(player, "admin.prompt_del"));

        _admin.PromptText(player, new TextPrompt
        {
            Variable = "admin_del_echo",
            Hint = _plugin.T(player, "admin.prompt_hint_steam"),
            CancelWord = _plugin.T(player, "admin.prompt_cancel_word"),
            MaxLength = 32,
            TimeoutSeconds = 60,
            OnResult = result =>
            {
                if (!VMPanelPlugin.IsUsablePlayer(player))
                    return;

                var s = GetSession(player.SteamID);

                if (result.Outcome != TextPromptOutcome.Submitted)
                {
                    DrawRemove(player, s, _plugin.T(player, "admin.prompt_del_cancelled"));
                    return;
                }

                if (!VMPanelPlugin.TryParseSteamId(result.Text.Trim(), out var steamId))
                {
                    DrawRemove(player, s, _plugin.T(player, "admin.prompt_del_invalid"));
                    PromptDelSteam(player);
                    return;
                }

                var existing = _plugin.GetVipRecord(steamId);
                s.DelSteamId = steamId;
                s.DelName = existing?.Name ?? "(not in database)";
                DrawRemove(player, s);
            },
        });
    }

    private void ConfirmRemove(CCSPlayerController player)
    {
        var session = GetSession(player.SteamID);

        if (session.DelSteamId == 0)
        {
            DrawRemove(player, session, _plugin.T(player, "admin.del_nothing"));
            return;
        }

        var steamId = session.DelSteamId;
        var creator = player.PlayerName;
        session.DelSteamId = 0;
        session.DelName = string.Empty;
        session.SelectedSteamId = 0;
        session.SelectedName = string.Empty;

        DrawRemove(player, session, _plugin.T(player, "vip.result_working"));

        _ = Task.Run(async () =>
        {
            var (_, message) = await _plugin.RevokeVipAsync(steamId, creator);

            Server.NextFrame(() =>
            {
                var admin = Utilities.GetPlayerFromSteamId64(player.SteamID);

                if (!VMPanelPlugin.IsUsablePlayer(admin) || _admin is null || !IsOpen(admin))
                    return;

                var s2 = GetSession(admin.SteamID);
                DrawRemove(admin, s2, message);
                admin.PrintToChat(message);
                ReturnHomeSoon(admin.SteamID, AdminView.Remove, _plugin.Config.ReturnHomeDelaySeconds);
            });
        });
    }

    // ---------------------------------------------------------------------
    // Utils
    // ---------------------------------------------------------------------

    private void RefreshAndRedraw(ulong adminSteamId)
    {
        _ = Task.Run(async () =>
        {
            await _plugin.RequestRefreshAsync();

            Server.NextFrame(() =>
            {
                var admin = Utilities.GetPlayerFromSteamId64(adminSteamId);

                if (!VMPanelPlugin.IsUsablePlayer(admin) || _admin is null || !IsOpen(admin))
                    return;

                RebuildItems();
                RedrawCurrent(admin, GetSession(adminSteamId));
                admin.PrintToChat(_plugin.T(admin, "vip.refresh_done"));
            });
        });
    }

    /// <summary>
    /// Called after every DB snapshot: shared browse data is fresh, and each
    /// open admin viewer is redrawn in whatever view they are in.
    /// </summary>
    public void RefreshData()
    {
        if (_disposed || _admin is null)
            return;

        RebuildItems();

        foreach (var steamId in _openAdmins.ToArray())
        {
            var viewer = Utilities.GetPlayerFromSteamId64(steamId);

            if (!VMPanelPlugin.IsUsablePlayer(viewer) || !IsOpen(viewer))
            {
                _openAdmins.Remove(steamId);
                continue;
            }

            var session = GetSession(steamId);

            if (session.View == AdminView.Browse && session.PickForAdd)
                StartAddPick(viewer, session, rearmPrompt: false);
            else
                RedrawCurrent(viewer, session);
        }

        _admin.Refresh();
    }

    private void RedrawCurrent(CCSPlayerController player, AdminSession session)
    {
        switch (session.View)
        {
            case AdminView.Browse:
                if (session.PickForAdd)
                    StartAddPick(player, session);
                else
                    DrawBrowse(player, session);
                break;
            case AdminView.Days: DrawDays(player, session); break;
            case AdminView.Add: DrawAdd(player, session); break;
            case AdminView.Remove: DrawRemove(player, session); break;
            default: DrawMain(player, session); break;
        }
    }

    private bool IsOpen(CCSPlayerController player) =>
        _admin?.IsOpenFor(player) ?? false;

    private int CountOnlineVips()
    {
        var count = 0;

        foreach (var p in Utilities.GetPlayers())
        {
            if (!VMPanelPlugin.IsUsablePlayer(p))
                continue;

            if (_plugin.IsVip(p.SteamID))
                count++;
        }

        return count;
    }

    private AdminSession GetSession(ulong steamId)
    {
        if (!_sessions.TryGetValue(steamId, out var session))
        {
            session = new AdminSession();
            _sessions[steamId] = session;
        }

        return session;
    }

    private bool IsAdmin(CCSPlayerController player) =>
        AdminManager.PlayerHasPermissions(player, "@css/root") ||
        AdminManager.PlayerHasPermissions(player, _plugin.Config.AdminMenuPermission);
}
