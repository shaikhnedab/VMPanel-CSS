using System.Text.RegularExpressions;
using MySqlConnector;

namespace VMPanel;

public sealed class VMPanelDatabase
{
    private readonly string _connectionString;
    private readonly string _serverTable;

    private static readonly Regex SafeIdentifier =
        new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public VMPanelDatabase(string connectionString, string serverTable)
    {
        _connectionString = connectionString;
        _serverTable = ValidateIdentifier(serverTable);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var vipSql = $"""
            CREATE TABLE IF NOT EXISTS `{_serverTable}` (
                `authId`      varchar(50)  COLLATE utf8mb4_unicode_ci NOT NULL,
                `flag`        varchar(45)  COLLATE utf8mb4_unicode_ci DEFAULT '"0:a"',
                `name`        varchar(100) COLLATE utf8mb4_unicode_ci NOT NULL,
                `expireStamp` int(20)      UNSIGNED NOT NULL,
                `created_at`  datetime     NOT NULL,
                `type`        int(20)      NOT NULL DEFAULT 0,
                PRIMARY KEY (`authId`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        const string serverSql = """
            CREATE TABLE IF NOT EXISTS `tbl_servers` (
                `id`          int(11)      NOT NULL AUTO_INCREMENT,
                `tbl_name`    varchar(100) NOT NULL,
                `server_name` varchar(255) DEFAULT NULL,
                `server_ip`   varchar(50)  DEFAULT NULL,
                `server_port` varchar(10)  DEFAULT NULL,
                `vip_flag`    varchar(45)  DEFAULT '"0:a"',
                PRIMARY KEY (`id`),
                UNIQUE KEY `tbl_name` (`tbl_name`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        const string auditSql = """
            CREATE TABLE IF NOT EXISTS `tbl_audit_logs` (
                `id`              int(11)      NOT NULL AUTO_INCREMENT,
                `activity`        varchar(255) NOT NULL,
                `additional_info` text,
                `created_by`      varchar(100) DEFAULT NULL,
                `created_at`      datetime     NOT NULL,
                PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        await using (var cmd = new MySqlCommand(vipSql, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        await using (var cmd = new MySqlCommand(serverSql, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        await using (var cmd = new MySqlCommand(auditSql, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<VipRecord>> GetVipRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var sql = $"""
            SELECT authId, flag, name, expireStamp, created_at, type
            FROM `{_serverTable}`;
            """;

        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var records = new List<VipRecord>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var rawAuthId = reader.GetString(0);
            var steamId = ParseSteamId(rawAuthId);

            if (steamId == 0)
                continue;

            records.Add(new VipRecord(
                steamId,
                CleanDbString(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetDateTime(4),
                reader.GetInt32(5)));
        }

        return records;
    }

    public async Task AddOrUpdateVipAsync(
        ulong steamId,
        string flag,
        string name,
        long expireStamp,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var sql = $"""
            INSERT INTO `{_serverTable}`
                (authId, flag, name, expireStamp, created_at, type)
            VALUES
                (@authId, @flag, @name, @expireStamp, NOW(), 0)
            ON DUPLICATE KEY UPDATE
                flag        = VALUES(flag),
                name        = VALUES(name),
                expireStamp = VALUES(expireStamp);
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@authId", steamId.ToString());
        command.Parameters.AddWithValue("@flag", flag);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@expireStamp", expireStamp);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAuditLogAsync(
        string createdBy,
        CancellationToken cancellationToken = default)
        => await AddAuditLogAsync("New VIP added", "Added Through CounterStrikeSharp VMPanel Plugin", createdBy, cancellationToken);

    public async Task AddAuditLogAsync(
        string activity,
        string additionalInfo,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO tbl_audit_logs
                (activity, additional_info, created_by, created_at)
            VALUES
                (@activity,
                 @additionalInfo,
                 @createdBy,
                 NOW());
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@activity", activity);
        command.Parameters.AddWithValue("@additionalInfo", additionalInfo);
        command.Parameters.AddWithValue("@createdBy", createdBy);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException)
        {
            // Keep VIP creation successful if an older panel installation
            // does not have tbl_audit_logs.
        }
    }

    public async Task<bool> RemoveVipAsync(
        ulong steamId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var sql = $"""
            DELETE FROM `{_serverTable}`
            WHERE authId = @authId;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@authId", steamId.ToString());

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ServerExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        const string sql = """
            SELECT COUNT(*)
            FROM tbl_servers
            WHERE tbl_name = @tableName;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", _serverTable);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    public async Task RegisterServerAsync(
        string serverName,
        string serverIp,
        int serverPort,
        string vipFlag,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // Idempotent: two servers booting with the same tbl_name must not
        // throw a duplicate-key error (additive, works on existing schema
        // which already has UNIQUE(tbl_name)).
        const string sql = """
            INSERT INTO tbl_servers
                (tbl_name, server_name, server_ip, server_port, vip_flag)
            VALUES
                (@tblName, @serverName, @serverIp, @serverPort, @vipFlag)
            ON DUPLICATE KEY UPDATE
                server_name = VALUES(server_name),
                server_ip   = VALUES(server_ip),
                server_port = VALUES(server_port),
                vip_flag    = VALUES(vip_flag);
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tblName", _serverTable);
        command.Parameters.AddWithValue("@serverName", serverName);
        command.Parameters.AddWithValue("@serverIp", serverIp);
        command.Parameters.AddWithValue("@serverPort", serverPort.ToString());
        command.Parameters.AddWithValue("@vipFlag", vipFlag);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifier.IsMatch(value))
            throw new ArgumentException(
                $"Invalid SQL table identifier: '{value}'. Use only A-Z, a-z, 0-9 and _.");

        return value;
    }

    private static ulong ParseSteamId(string value)
    {
        value = CleanDbString(value);

        if (ulong.TryParse(value, out var steam64))
            return steam64;

        var parts = value.Split(':');
        if (parts.Length == 3 &&
            int.TryParse(parts[1], out var y) &&
            ulong.TryParse(parts[2], out var z))
        {
            return 76561197960265728UL + (z * 2UL) + (ulong)y;
        }

        return 0;
    }

    private static string CleanDbString(string value) =>
        value.Trim().Trim('"').Trim();
}