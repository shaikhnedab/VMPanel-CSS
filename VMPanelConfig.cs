using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace VMPanel;

public sealed class VMPanelConfig : BasePluginConfig
{
    [JsonPropertyName("DatabaseConnectionString")]
    public string DatabaseConnectionString { get; set; } =
        "Server=127.0.0.1;Port=3306;Database=vmpanel;User ID=vmpanel;Password=CHANGE_ME;";

    [JsonPropertyName("ServerTable")]
    public string ServerTable { get; set; } = "sv_table";

    [JsonPropertyName("PanelUrl")]
    public string PanelUrl { get; set; } = "https://vmpanel.example";

    [JsonPropertyName("DefaultVipFlag")]
    public string DefaultVipFlag { get; set; } = "0:a";

    [JsonPropertyName("VipDurationOptions")]
    public List<int> VipDurationOptions { get; set; } = new() { 30, 60, 90, 365 };

    [JsonPropertyName("ReturnHomeDelaySeconds")]
    public float ReturnHomeDelaySeconds { get; set; } = 2.5f;

    [JsonPropertyName("AlertEnabled")]
    public bool AlertEnabled { get; set; } = true;

    [JsonPropertyName("AlertDelaySeconds")]
    public float AlertDelaySeconds { get; set; } = 20.0f;

    [JsonPropertyName("AlertDays")]
    public int AlertDays { get; set; } = 2;

    [JsonPropertyName("AlertDisplayType")]
    public int AlertDisplayType { get; set; } = 1;

    [JsonPropertyName("VipStatusPermission")]
    public string VipStatusPermission { get; set; } = "@css/reservation";

    [JsonPropertyName("AdminMenuPermission")]
    public string AdminMenuPermission { get; set; } = "@css/root";

    [JsonPropertyName("RefreshIntervalMinutes")]
    public int RefreshIntervalMinutes { get; set; } = 5;

    [JsonPropertyName("AutoRegisterServer")]
    public bool AutoRegisterServer { get; set; } = true;

    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("ServerIp")]
    public string ServerIp { get; set; } = "";

    [JsonPropertyName("ServerPort")]
    public int ServerPort { get; set; } = 0;

    [JsonPropertyName("UseLocalTime")]
    public bool UseLocalTime { get; set; } = false;

    [JsonPropertyName("ToastEnabled")]
    public bool ToastEnabled { get; set; } = true;

    [JsonPropertyName("ToastDurationSeconds")]
    public float ToastDurationSeconds { get; set; } = 6.0f;
}