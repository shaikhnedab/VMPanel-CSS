using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using PanoramaManager;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VMPanel;

[MinimumApiVersion(80)]
public sealed class VMPanelPlugin : BasePlugin, IPluginConfig<VMPanelConfig>
{
    public override string ModuleName => "VMPanel";
    public override string ModuleVersion => "3.5.0";
    public override string ModuleAuthor => "Summer-16 / CounterStrikeSharp port";
    public override string ModuleDescription =>
        "VMPanel VIP manager for Counter-Strike 2 / CounterStrikeSharp";

    public VMPanelConfig Config { get; set; } = new();

    private VMPanelDatabase? _database;
    private readonly ConcurrentDictionary<ulong, VipRecord> _vips = new();
    private readonly ConcurrentDictionary<ulong, HashSet<string>> _appliedPermissions = new();
    private readonly ConcurrentDictionary<ulong, CssTimer> _alertTimers = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private CssTimer? _refreshTimer;
    private VMPanelMenu? _menu;
    private VMPanelHud? _hud;

    private volatile bool _disposed;
    private volatile bool _dbReady;

    private static readonly Regex SteamIdRegex =
        new(@"^(?:\d{17}|STEAM_[0-5]:[01]:\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------------
    // Public helpers
    // ---------------------------------------------------------------------

    public VipRecord? GetVipRecord(ulong steamId) =>
        _vips.TryGetValue(steamId, out var record) ? record : null;

    public bool IsVip(ulong steamId) => _vips.ContainsKey(steamId);

    public IReadOnlyList<VipRecord> GetAllVips() =>
        _vips.Values.OrderBy(v => v.Name).ToArray();

    public async Task RequestRefreshAsync()
    {
        await RefreshVipAndAdminsAsync();
    }

    public VMPanelHud? GetHud() => _hud;

    public VMPanelMenu? GetMenu() => _menu;

    public string FormatExpiry(long expireStamp)
    {
        var dto = DateTimeOffset.FromUnixTimeSeconds(expireStamp);
        var shown = Config.UseLocalTime ? dto.ToLocalTime() : dto.ToUniversalTime();
        var suffix = Config.UseLocalTime ? string.Empty : " UTC";
        return shown.ToString("yyyy-MM-dd HH:mm") + suffix;
    }

    /// <summary>
    /// Localized string in the player's language (falls back to server
    /// language). All user-facing text goes through here — nothing
    /// player-visible is hardcoded.
    /// </summary>
    public string T(CCSPlayerController? player, string key, params object[] args)
    {
        try { return Localizer.ForPlayer(player, key, args); }
        catch
        {
            try { return Localizer[key, args]; }
            catch { return key; }
        }
    }

    /// <summary>Localized string in the server language (console / no player).</summary>
    public string Ts(string key, params object[] args)
    {
        try { return Localizer[key, args]; }
        catch { return key; }
    }

    // ---------------------------------------------------------------------
    // Config
    // ---------------------------------------------------------------------

    public void OnConfigParsed(VMPanelConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DatabaseConnectionString))
            throw new Exception("DatabaseConnectionString cannot be empty.");

        if (string.IsNullOrWhiteSpace(config.ServerTable))
            throw new Exception("ServerTable cannot be empty.");

        if (!Regex.IsMatch(config.ServerTable, "^[A-Za-z0-9_]+$"))
            throw new Exception("ServerTable may only contain A-Z, a-z, 0-9 and _.");

        if (float.IsNaN(config.AlertDelaySeconds) || float.IsInfinity(config.AlertDelaySeconds) || config.AlertDelaySeconds < 0)
            config.AlertDelaySeconds = 20;

        if (config.AlertDays < 0)
            config.AlertDays = 2;

        if (config.AlertDisplayType is < 1 or > 2)
            config.AlertDisplayType = 1;

        if (config.RefreshIntervalMinutes < 1)
            config.RefreshIntervalMinutes = 5;

        // Duration options shown as buttons in the admin Add-VIP days view
        // (max 4 — the layout ships four option buttons). Falls back to the
        // defaults when empty or entirely invalid.
        var cleaned = (config.VipDurationOptions ?? new List<int>())
            .Where(d => d >= 1 && d <= 36500)
            .Distinct()
            .Take(4)
            .ToList();

        config.VipDurationOptions = cleaned.Count > 0
            ? cleaned
            : new List<int> { 30, 60, 90, 365 };

        if (float.IsNaN(config.ReturnHomeDelaySeconds) || float.IsInfinity(config.ReturnHomeDelaySeconds) || config.ReturnHomeDelaySeconds < 0)
            config.ReturnHomeDelaySeconds = 2.5f;

        if (!config.VipStatusPermission.StartsWith('@'))
            config.VipStatusPermission = "@css/reservation";

        if (!config.AdminMenuPermission.StartsWith('@') && !config.AdminMenuPermission.StartsWith('#'))
            config.AdminMenuPermission = "@css/root";

        if (float.IsNaN(config.ToastDurationSeconds) || float.IsInfinity(config.ToastDurationSeconds) || config.ToastDurationSeconds < 1)
            config.ToastDurationSeconds = 6.0f;

        Config = config;
    }

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    public override void Load(bool hotReload)
    {
        Logger.LogInformation(
            "VMPanel {Version} loading. CounterStrikeSharp API: {ApiVersion}",
            ModuleVersion,
            Api.GetVersionString());

        Panorama.Init(this);

        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        _hud = new VMPanelHud(this);
        _menu = new VMPanelMenu(this);
        _hud.Initialize();
        _menu.Initialize();

        _ = InitializeAsync();
    }

    private void OnMapStart(string mapName)
    {
        if (!_dbReady)
            return;

        _ = RefreshVipAndAdminsAsync();
    }

    private void OnMapEnd()
    {
        foreach (var timer in _alertTimers.Values)
        {
            try { timer.Kill(); } catch { /* ignore */ }
        }

        _alertTimers.Clear();
    }

    public override void Unload(bool hotReload)
    {
        _disposed = true;

        _refreshTimer?.Kill();

        foreach (var timer in _alertTimers.Values)
        {
            try { timer.Kill(); } catch { /* ignore */ }
        }

        _alertTimers.Clear();

        foreach (var (steamId, permissions) in _appliedPermissions)
        {
            var player = Utilities.GetPlayerFromSteamId64(steamId);
            RemoveAppliedPermissions(player, permissions, resetImmunity: true);
        }

        _appliedPermissions.Clear();
        _vips.Clear();

        _hud?.Dispose();
        _menu?.Dispose();

        _hud = null;
        _menu = null;

        try { _refreshLock.Dispose(); } catch { /* ignore */ }

        Panorama.Shutdown();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _database = new VMPanelDatabase(
                Config.DatabaseConnectionString,
                Config.ServerTable);

            await _database.InitializeAsync();
            _dbReady = true;

            if (_disposed)
                return;

            await RunOnMainThreadAsync(() =>
            {
                if (_disposed)
                    return;

                _refreshTimer = AddTimer(
                    Config.RefreshIntervalMinutes * 60.0f,
                    () => { _ = RefreshVipAndAdminsAsync(); },
                    TimerFlags.REPEAT);
            });

            await RefreshVipAndAdminsAsync();

            if (Config.AutoRegisterServer)
                await EnsureServerRegisteredAsync();

            Logger.LogInformation(
                "VMPanel initialized. Loaded {Count} VIP records.",
                _vips.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "VMPanel initialization failed.");
        }
    }

    // ---------------------------------------------------------------------
    // Main-thread helpers
    // ---------------------------------------------------------------------

    private static Task RunOnMainThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource();

        Server.NextFrame(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static Task<T> RunOnMainThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();

        Server.NextFrame(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        return tcs.Task;
    }

    // ---------------------------------------------------------------------
    // Refresh
    // ---------------------------------------------------------------------

    private async Task RefreshVipAndAdminsAsync()
    {
        if (_disposed || _database is null || !_dbReady)
            return;

        if (!await _refreshLock.WaitAsync(0))
            return;

        try
        {
            var records = await _database.GetVipRecordsAsync();

            var newVips = records
                .Where(x => !x.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                .ToDictionary(x => x.SteamId);

            await RunOnMainThreadAsync(() =>
            {
                if (_disposed)
                    return;

                ApplyVipSnapshot(newVips);
            });

            Logger.LogInformation(
                "VMPanel VIP refresh completed: {Count} active VIPs.",
                _vips.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "VMPanel VIP refresh failed.");
        }
        finally
        {
            try { _refreshLock.Release(); } catch { /* ignore */ }
        }
    }

    private void ApplyVipSnapshot(Dictionary<ulong, VipRecord> newVips)
    {
        foreach (var (steamId, permissions) in _appliedPermissions.ToArray())
        {
            if (!newVips.TryGetValue(steamId, out var newRecord))
            {
                var player = Utilities.GetPlayerFromSteamId64(steamId);
                RemoveAppliedPermissions(player, permissions, resetImmunity: true);

                _appliedPermissions.TryRemove(steamId, out _);
                continue;
            }

            var newPermissions = ParsePermissions(newRecord.Flag, newRecord.Type);
            var newImmunity = ParseImmunity(newRecord.Flag);
            var connectedPlayer = Utilities.GetPlayerFromSteamId64(steamId);

            foreach (var oldPermission in permissions)
            {
                if (newPermissions.Contains(oldPermission))
                    continue;

                if (connectedPlayer is null || !connectedPlayer.IsValid)
                    continue;

                try
                {
                    AdminManager.RemovePlayerPermissions(
                        connectedPlayer,
                        new[] { oldPermission });
                }
                catch
                {
                    // Ignore stale admin handles.
                }
            }

            // Immunity removed or lowered in DB: reset explicitly so a stale
            // value does not survive the VIP downgrade.
            if (connectedPlayer is not null && connectedPlayer.IsValid)
            {
                try
                {
                    AdminManager.SetPlayerImmunity(
                        connectedPlayer,
                        newImmunity ?? 0);
                }
                catch
                {
                    // Ignore stale admin handles.
                }
            }
        }

        _vips.Clear();

        foreach (var pair in newVips)
            _vips[pair.Key] = pair.Value;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsUsablePlayer(player))
                continue;

            var steamId = player.SteamID;

            if (_vips.TryGetValue(steamId, out var vip))
                ApplyVip(player, vip);
            else
                RemoveVipPermissions(player);
        }

        _hud?.RefreshAll();
        _menu?.RefreshData();
    }

    // ---------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(
        EventPlayerConnectFull @event,
        GameEventInfo info)
    {
        var player = @event.Userid;

        if (!IsUsablePlayer(player))
            return HookResult.Continue;

        if (_vips.TryGetValue(player.SteamID, out var vip))
        {
            ApplyVip(player, vip);

            if (Config.ToastEnabled)
                _hud?.ShowToast(player, T(player, "hud.toast_active"), T(player, "hud.toast_left", vip.Name, vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));

            if (Config.AlertEnabled)
            {
                var steamId = player.SteamID;

                if (_alertTimers.TryRemove(steamId, out var old))
                {
                    try { old.Kill(); } catch { /* ignore */ }
                }

                _alertTimers[steamId] = AddTimer(
                    Config.AlertDelaySeconds,
                    () =>
                    {
                        _alertTimers.TryRemove(steamId, out _);
                        ShowExpiryAlert(steamId);
                    },
                    TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        return HookResult.Continue;
    }

    private void ShowExpiryAlert(ulong steamId)
    {
        if (_disposed)
            return;

        var player = Utilities.GetPlayerFromSteamId64(steamId);

        if (!IsUsablePlayer(player))
            return;

        if (!_vips.TryGetValue(steamId, out var vip))
            return;

        var daysLeft = vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (daysLeft > Config.AlertDays)
            return;

        if (Config.AlertDisplayType == 1)
        {
            player.PrintToChat(T(player, "alert.sub_name", vip.Name));
            player.PrintToChat(T(player, "alert.sub_days", daysLeft));
            player.PrintToChat(T(player, "alert.sub_renew", Config.PanelUrl));
        }
        else
        {
            player.PrintToChat(
                T(player, "alert.expiring", daysLeft));
            player.PrintToChat(T(player, "alert.renew", Config.PanelUrl));
        }
    }

    private void OnClientDisconnect(int playerSlot)
    {
        // Slot may already be recycled: clean up by SteamID where possible
        // and always drop Panorama sessions for the slot so input capture
        // can never leak to the next occupant.
        var player = Utilities.GetPlayerFromSlot(playerSlot);

        if (player is not null && player.SteamID != 0)
        {
            var steamId = player.SteamID;

            if (_alertTimers.TryRemove(steamId, out var timer))
            {
                try { timer.Kill(); } catch { /* ignore */ }
            }

            RemoveVipPermissions(player);
            _hud?.Close(player);
        }

        _hud?.CloseSlot(playerSlot);
        _menu?.CloseSlot(playerSlot);
    }

    // =====================================================================
    // COMMANDS (all in the main plugin class!)
    // =====================================================================

    // ---- HUD menu commands ----------------------------------------------

    [ConsoleCommand("css_vip", "Opens the VMPanel HUD menu.")]
    public void CmdVip(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsUsablePlayer(player))
        {
            command.ReplyToCommand(Ts("general.only_players"));
            return;
        }

        _hud?.Open(player!);
    }

    [ConsoleCommand("css_vipmenu", "Alias for css_vip.")]
    public void CmdVipMenu(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsUsablePlayer(player))
        {
            command.ReplyToCommand(Ts("general.only_players"));
            return;
        }

        _hud?.Open(player!);
    }

    [ConsoleCommand("css_vipclose", "Closes the VMPanel HUD.")]
    public void CmdVipClose(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsUsablePlayer(player))
        {
            command.ReplyToCommand(Ts("general.only_players"));
            return;
        }

        _hud?.Close(player!);
    }

    [ConsoleCommand("css_viphelp", "Shows VMPanel commands.")]
    public void CmdVipHelp(CCSPlayerController? player, CommandInfo command)
    {
        command.ReplyToCommand(" ");
        command.ReplyToCommand(T(player, "help.title"));
        command.ReplyToCommand(T(player, "help.vip"));
        command.ReplyToCommand(T(player, "help.vipclose"));
        command.ReplyToCommand(T(player, "help.vipstatus"));
        command.ReplyToCommand(T(player, "help.viprefresh"));
        command.ReplyToCommand(T(player, "help.viplist"));
        command.ReplyToCommand(T(player, "help.online"));
        command.ReplyToCommand(T(player, "help.vipadmin"));
        command.ReplyToCommand(T(player, "help.manage"));
        command.ReplyToCommand(" ");
    }

    [ConsoleCommand("css_vipadmin", "Opens the VMPanel admin menu.")]
    [RequiresPermissions("@css/root")]
    public void CmdVipAdmin(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsUsablePlayer(player))
        {
            command.ReplyToCommand(Ts("general.only_players"));
            return;
        }

        if (!AdminManager.PlayerHasPermissions(player, Config.AdminMenuPermission))
        {
            command.ReplyToCommand(T(player, "general.no_permission"));
            return;
        }

        _menu?.OpenAdminMenu(player!);
    }

    [ConsoleCommand("css_hud_debug", "Dumps VMPanel HUD state for debugging.")]
    [RequiresPermissions("@css/root")]
    public void CmdHudDebug(CCSPlayerController? player, CommandInfo command)
    {
        var hud = _hud;
        if (hud is null)
        {
            command.ReplyToCommand(Ts("general.hud_null"));
            return;
        }

        command.ReplyToCommand(T(player, "general.hud_debug"));
        command.ReplyToCommand(T(player, "general.panorama_clicks", Panorama.CanReceiveClicks ? "yes" : "no"));
        hud.DumpDebugState();
    }

    // ---- Data commands --------------------------------------------------

    [ConsoleCommand("css_viprefresh", "Refreshes VMPanel VIP/admin data.")]
    [RequiresPermissions("@css/generic")]
    public void VipRefresh(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        commandInfo.ReplyToCommand(T(player, "vip.refresh_started"));

        var caller = player?.SteamID ?? 0;

        _ = Task.Run(async () =>
        {
            await RefreshVipAndAdminsAsync();

            if (_disposed)
                return;

            Server.NextFrame(() =>
            {
                if (_disposed)
                    return;

                if (caller != 0)
                {
                    var p = Utilities.GetPlayerFromSteamId64(caller);
                    if (p is not null && p.IsValid)
                        p.PrintToChat(T(p, "vip.refresh_done"));
                }
                else
                {
                    commandInfo.ReplyToCommand(Ts("vip.refresh_done"));
                }
            });
        });
    }

    [ConsoleCommand("css_viplist", "Lists connected VIP players.")]
    [RequiresPermissions("@css/generic")]
    public void VipList(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lines = new List<string>();

        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsUsablePlayer(p))
                continue;

            if (!_vips.TryGetValue(p.SteamID, out var vip))
                continue;

            lines.Add(T(player, "vip.list_row", p.PlayerName, p.SteamID, vip.Name, vip.DaysLeft(now)));
        }

        if (lines.Count == 0)
        {
            commandInfo.ReplyToCommand(T(player, "vip.no_connected"));
            return;
        }

        commandInfo.ReplyToCommand(T(player, "vip.list_header", lines.Count));

        foreach (var line in lines.Take(20))
            commandInfo.ReplyToCommand("  " + line);
    }

    [ConsoleCommand("css_online", "Lists online players with SteamIDs (for chat admin work).")]
    [RequiresPermissions("@css/generic")]
    public void OnlineList(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        var list = new List<CCSPlayerController>();

        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsUsablePlayer(p))
                continue;

            list.Add(p);
        }

        if (list.Count == 0)
        {
            commandInfo.ReplyToCommand(T(player, "vip.nobody_online"));
            return;
        }

        list.Sort((a, b) => string.Compare(a.PlayerName, b.PlayerName, StringComparison.OrdinalIgnoreCase));

        commandInfo.ReplyToCommand(T(player, "vip.online_header", list.Count));

        foreach (var p in list.Take(20))
            commandInfo.ReplyToCommand(T(player, "vip.online_row", p.PlayerName, p.SteamID));
    }

    [ConsoleCommand("css_vipstatus", "Shows your VMPanel VIP status.")]
    public void VipStatus(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        if (!IsUsablePlayer(player))
        {
            commandInfo.ReplyToCommand(Ts("general.only_players"));
            return;
        }

        if (!_vips.TryGetValue(player.SteamID, out var vip))
        {
            if (!string.IsNullOrWhiteSpace(Config.VipStatusPermission) &&
                !AdminManager.PlayerHasPermissions(player, Config.VipStatusPermission) &&
                !AdminManager.PlayerHasPermissions(player, "@css/root"))
            {
                commandInfo.ReplyToCommand(T(player, "general.no_permission"));
                return;
            }

            commandInfo.ReplyToCommand(
                T(player, "vip.not_vip_renew", Config.PanelUrl));
            return;
        }

        var daysLeft = vip.DaysLeft(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        commandInfo.ReplyToCommand(
            T(player, "vip.status", vip.Name, daysLeft, FormatExpiry(vip.ExpireStamp)));
    }

    [ConsoleCommand("css_addvip", "Adds or updates a VMPanel VIP. Usage: css_addvip <SteamID64/SteamID> <days> <name>")]
    [RequiresPermissions("@css/root")]
    public void AddVip(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        if (commandInfo.ArgCount < 4)
        {
            commandInfo.ReplyToCommand(
                T(player, "vip.add_usage"));
            return;
        }

        var steamIdText = commandInfo.GetArg(1);
        var daysText = commandInfo.GetArg(2);
        var name = string.Join(
            " ",
            Enumerable.Range(3, commandInfo.ArgCount - 3)
                .Select(commandInfo.GetArg));

        if (!TryParseSteamId(steamIdText, out var steamId))
        {
            commandInfo.ReplyToCommand(T(player, "general.invalid_steamid"));
            return;
        }

        if (!int.TryParse(daysText, out var days) || days <= 0 || days > 36500)
        {
            commandInfo.ReplyToCommand(
                T(player, "vip.days_range"));
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            commandInfo.ReplyToCommand(T(player, "vip.name_empty"));
            return;
        }

        var creator = player?.PlayerName ?? "CONSOLE";

        _ = AddVipAsync(commandInfo, steamId, days, name, creator, player);
    }

    internal async Task<(bool Ok, string Message)> GrantVipAsync(
        ulong steamId,
        int days,
        string name,
        string creator,
        CCSPlayerController? localePlayer = null)
    {
        if (_database is null || !_dbReady)
            return (false, Ts("general.db_not_ready"));

        long expireStamp;
        try
        {
            checked
            {
                expireStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)days * 86400L;
            }
        }
        catch (OverflowException)
        {
            return (false, T(localePlayer, "vip.add_failed", "expiry overflow"));
        }

        var flag = NormalizeFlag(Config.DefaultVipFlag);

        try
        {
            await _database.AddOrUpdateVipAsync(steamId, flag, name, expireStamp);

            await _database.AddAuditLogAsync(
                "New VIP added",
                $"Added Through CounterStrikeSharp VMPanel Plugin: {steamId} / {name} / {days}d / exp {expireStamp} / flag {flag}",
                creator);

            await RefreshVipAndAdminsAsync();
            return (true, T(localePlayer, "vip.added", steamId, name, days));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add VIP {SteamId}", steamId);
            return (false, T(localePlayer, "vip.add_failed", ex.Message));
        }
    }

    internal async Task<(bool Ok, string Message)> RevokeVipAsync(
        ulong steamId,
        string creator,
        CCSPlayerController? localePlayer = null)
    {
        if (_database is null || !_dbReady)
            return (false, Ts("general.db_not_ready"));

        try
        {
            var removed = await _database.RemoveVipAsync(steamId);

            await _database.AddAuditLogAsync(
                "VIP removed",
                $"Removed Through CounterStrikeSharp VMPanel Plugin: {steamId}",
                creator);

            await RefreshVipAndAdminsAsync();
            return removed
                ? (true, T(localePlayer, "vip.removed", steamId))
                : (false, T(localePlayer, "vip.remove_none", steamId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to remove VIP {SteamId}", steamId);
            return (false, T(localePlayer, "vip.remove_failed", ex.Message));
        }
    }

    private async Task AddVipAsync(
        CommandInfo commandInfo,
        ulong steamId,
        int days,
        string name,
        string creator,
        CCSPlayerController? localePlayer)
    {
        var (_, message) = await GrantVipAsync(steamId, days, name, creator, localePlayer);

        if (_disposed)
            return;

        Server.NextFrame(() =>
        {
            if (_disposed)
                return;

            commandInfo.ReplyToCommand(message);
        });
    }

    [ConsoleCommand("css_delvip", "Removes a VMPanel VIP. Usage: css_delvip <SteamID64/SteamID>")]
    [RequiresPermissions("@css/root")]
    public void DelVip(
        CCSPlayerController? player,
        CommandInfo commandInfo)
    {
        if (commandInfo.ArgCount < 2)
        {
            commandInfo.ReplyToCommand(T(player, "vip.del_usage"));
            return;
        }

        if (!TryParseSteamId(commandInfo.GetArg(1), out var steamId))
        {
            commandInfo.ReplyToCommand(T(player, "general.invalid_steamid"));
            return;
        }

        if (_database is null || !_dbReady)
        {
            commandInfo.ReplyToCommand(T(player, "general.db_not_ready"));
            return;
        }

        var creator = player?.PlayerName ?? "CONSOLE";

        _ = DelVipAsync(commandInfo, steamId, creator, player);
    }

    private async Task DelVipAsync(
        CommandInfo commandInfo,
        ulong steamId,
        string creator,
        CCSPlayerController? localePlayer)
    {
        var (_, message) = await RevokeVipAsync(steamId, creator, localePlayer);

        if (_disposed)
            return;

        Server.NextFrame(() =>
        {
            if (_disposed)
                return;

            commandInfo.ReplyToCommand(message);
        });
    }

    // ---------------------------------------------------------------------
    // Server registration
    // ---------------------------------------------------------------------

    private async Task EnsureServerRegisteredAsync()
    {
        if (_disposed || _database is null)
            return;

        try
        {
            // RegisterServerAsync is idempotent (ON DUPLICATE KEY UPDATE),
            // so no separate exists-check race remains.
            var (serverName, serverIp, port) = await RunOnMainThreadAsync(() =>
            {
                var name = Config.ServerName;

                if (string.IsNullOrWhiteSpace(name))
                {
                    var hostname = ConVar.Find("hostname");
                    name = hostname?.StringValue ?? Config.ServerTable;
                }

                var ip = Config.ServerIp;

                if (string.IsNullOrWhiteSpace(ip))
                    ip = ResolveServerIp();

                var p = Config.ServerPort;

                if (p <= 0)
                {
                    var hostPort = ConVar.Find("hostport");

                    if (hostPort is not null)
                        p = hostPort.GetPrimitiveValue<int>();
                }

                if (p <= 0)
                    p = 27015;

                return (name, ip, p);
            });

            if (_disposed)
                return;

            await _database.RegisterServerAsync(
                serverName,
                serverIp,
                port,
                NormalizeFlag(Config.DefaultVipFlag));

            Logger.LogInformation(
                "VMPanel server registered: {Name} {Ip}:{Port}",
                serverName,
                serverIp,
                port);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to register VMPanel server.");
        }
    }

    // ---------------------------------------------------------------------
    // Permission application
    // ---------------------------------------------------------------------

    private void ApplyVip(CCSPlayerController player, VipRecord vip)
    {
        var permissions = ParsePermissions(vip.Flag, vip.Type);

        if (permissions.Count == 0)
            return;

        RemoveVipPermissions(player);

        AdminManager.AddPlayerPermissions(
            player,
            permissions.ToArray());

        // Always write immunity (0 when the flag carries none) so a stale
        // value from a previous VIP tier cannot survive a downgrade.
        AdminManager.SetPlayerImmunity(player, ParseImmunity(vip.Flag) ?? 0);

        _appliedPermissions[player.SteamID] =
            new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
    }

    private void RemoveVipPermissions(CCSPlayerController player)
    {
        if (!_appliedPermissions.TryRemove(
                player.SteamID,
                out var permissions))
            return;

        RemoveAppliedPermissions(player, permissions, resetImmunity: true);
    }

    private static void RemoveAppliedPermissions(
        CCSPlayerController? player,
        IEnumerable<string> permissions,
        bool resetImmunity = false)
    {
        if (player is null || !player.IsValid)
            return;

        try
        {
            AdminManager.RemovePlayerPermissions(
                player,
                permissions.ToArray());

            if (resetImmunity)
                AdminManager.SetPlayerImmunity(player, 0);
        }
        catch
        {
            // Ignore stale player/admin handles.
        }
    }

    // ---------------------------------------------------------------------
    // Utils
    // ---------------------------------------------------------------------

    internal static bool IsUsablePlayer([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player is not null &&
               player.IsValid &&
               player.Connected == PlayerConnectedState.Connected &&
               player.SteamID != 0 &&
               !player.IsHLTV &&
               !player.IsBot;
    }

    internal static HashSet<string> ParsePermissions(string flag, int type = 0)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // type is reserved for future VIP tiers; currently all rows behave
        // the same. Kept in the signature so callers stay stable.
        _ = type;

        flag = NormalizeFlag(flag);

        var colon = flag.IndexOf(':');

        if (colon < 0)
            return result;

        var sourceModFlags = flag[(colon + 1)..];

        var flagMap = new Dictionary<char, string>
        {
            ['a'] = "@css/reservation",
            ['b'] = "@css/generic",
            ['c'] = "@css/kick",
            ['d'] = "@css/ban",
            ['e'] = "@css/unban",
            ['f'] = "@css/slay",
            ['g'] = "@css/changemap",
            ['h'] = "@css/cvar",
            ['i'] = "@css/config",
            ['j'] = "@css/chat",
            ['k'] = "@css/vote",
            ['l'] = "@css/password",
            ['m'] = "@css/rcon",
            ['n'] = "@css/cheats",
            ['o'] = "@css/vip",
            ['p'] = "@css/vip",
            ['q'] = "@css/vip",
            ['r'] = "@css/vip",
            ['s'] = "@css/vip",
            ['t'] = "@css/vip",
            ['z'] = "@css/root",
        };

        foreach (var c in sourceModFlags)
        {
            if (flagMap.TryGetValue(c, out var permission))
                result.Add(permission);
        }

        if (result.Count == 0)
            result.Add("@css/vip");

        return result;
    }

    private static uint? ParseImmunity(string flag)
    {
        flag = NormalizeFlag(flag);

        var colon = flag.IndexOf(':');

        if (colon <= 0)
            return null;

        return uint.TryParse(
            flag[..colon],
            out var immunity)
            ? immunity
            : null;
    }

    private static string NormalizeFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return "0:a";

        return flag.Trim().Trim('"').Trim();
    }

    internal static bool TryParseSteamId(string value, out ulong steamId)
    {
        steamId = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim().Trim('"').Trim();

        if (ulong.TryParse(value, out steamId) && steamId > 0)
            return true;

        steamId = 0;

        if (!SteamIdRegex.IsMatch(value))
            return false;

        var parts = value.Split(':');

        if (parts.Length != 3 ||
            !int.TryParse(parts[1], out var y) ||
            (y != 0 && y != 1) ||
            !ulong.TryParse(parts[2], out var z))
            return false;

        steamId = 76561197960265728UL + (z * 2UL) + (ulong)y;
        return true;
    }

    private static string ResolveServerIp()
    {
        var hostIp = ConVar.Find("hostip");

        if (hostIp is null)
            return "0.0.0.0";

        var raw = unchecked((uint)hostIp.GetPrimitiveValue<int>());

        if (raw == 0)
            return "0.0.0.0";

        return string.Join(
            ".",
            (raw >> 24) & 255,
            (raw >> 16) & 255,
            (raw >> 8) & 255,
            raw & 255);
    }
}
