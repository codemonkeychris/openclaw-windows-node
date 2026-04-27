using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OpenClaw.Shared;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public enum PairingStatus
{
    Unknown,
    Pending,    // Connected but awaiting approval
    Paired,     // Approved with device token
    Rejected    // Pairing was rejected
}

public class PairingStatusEventArgs : EventArgs
{
    public PairingStatus Status { get; }
    public string DeviceId { get; }
    public string? Message { get; }
    
    public PairingStatusEventArgs(PairingStatus status, string deviceId, string? message = null)
    {
        Status = status;
        DeviceId = deviceId;
        Message = message;
    }
}

public enum ActivityKind
{
    Idle,
    Job,
    Exec,
    Read,
    Write,
    Edit,
    Search,
    Browser,
    Message,
    Tool
}

public class AgentActivity
{
    public string SessionKey { get; set; } = "";
    public bool IsMain { get; set; }
    public ActivityKind Kind { get; set; } = ActivityKind.Idle;
    public string State { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Label { get; set; } = "";

    public string Glyph => Kind switch
    {
        ActivityKind.Exec => "💻",
        ActivityKind.Read => "📄",
        ActivityKind.Write => "✍️",
        ActivityKind.Edit => "📝",
        ActivityKind.Search => "🔍",
        ActivityKind.Browser => "🌐",
        ActivityKind.Message => "💬",
        ActivityKind.Tool => "🛠️",
        ActivityKind.Job => "⚡",
        _ => ""
    };

    public string DisplayText => Kind == ActivityKind.Idle
        ? ""
        : $"{(IsMain ? "Main" : "Sub")} · {Glyph} {Label}";
}

public class OpenClawNotification
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsChat { get; set; } = false; // True if from chat response

    // Structured metadata (populated by gateway when available)
    public string? Channel { get; set; }   // e.g. telegram, email, chat
    public string? Agent { get; set; }     // agent name/identifier
    public string? Intent { get; set; }    // normalized intent (reminder, build, alert)
    public string[]? Tags { get; set; }    // free-form routing tags
}

/// <summary>
/// A user-defined notification categorization rule.
/// </summary>
public class UserNotificationRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public string Category { get; set; } = "info";
    public bool Enabled { get; set; } = true;
}

public class ChannelHealth
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public bool IsLinked { get; set; }
    public string? Error { get; set; }
    public string? AuthAge { get; set; }
    public string? Type { get; set; }

    // FrozenSet gives O(1) case-insensitive lookup with no per-call allocation;
    // these sets are never mutated after startup so FrozenSet is the correct choice.
    private static readonly FrozenSet<string> s_healthyStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ok", "connected", "running", "active", "ready" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> s_intermediateStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "stopped", "idle", "paused", "configured", "pending", "connecting", "reconnecting" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Maps each status string (case-insensitive) to its tray label; never mutated after startup.
    private static readonly FrozenDictionary<string, string> s_statusLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"]            = "[ON]",
            ["connected"]     = "[ON]",
            ["running"]       = "[ON]",
            ["active"]        = "[ON]",
            ["linked"]        = "[LINKED]",
            ["ready"]         = "[READY]",
            ["connecting"]    = "[...]",
            ["reconnecting"]  = "[...]",
            ["error"]         = "[ERR]",
            ["disconnected"]  = "[ERR]",
            ["stale"]         = "[STALE]",
            ["configured"]    = "[OFF]",
            ["stopped"]       = "[OFF]",
            ["not configured"] = "[N/A]",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the given status string represents a healthy/running channel.
    /// Use this instead of inline status checks to keep the healthy-status set consistent.
    /// </summary>
    public static bool IsHealthyStatus(string? status) =>
        status is not null && s_healthyStatuses.Contains(status);

    /// <summary>
    /// Returns true if the given status string represents an intermediate (not yet healthy, not error) state.
    /// </summary>
    public static bool IsIntermediateStatus(string? status) =>
        status is not null && s_intermediateStatuses.Contains(status);

    public string DisplayText
    {
        get
        {
            // FrozenDictionary lookup avoids allocating a lowercased copy of Status.
            var label = s_statusLabels.GetValueOrDefault(Status, "[OFF]");
            var detail = IsLinked && AuthAge != null ? $"linked · {AuthAge}" : Status;
            if (Error != null) detail += $" ({Error})";
            return $"{label} {Capitalize(Name)}: {detail}";
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

public static class ChannelHealthParser
{
    public static ChannelHealth[] Parse(JsonElement channels)
    {
        if (channels.ValueKind != JsonValueKind.Object)
            return [];

        var healthList = new List<ChannelHealth>();
        foreach (var prop in channels.EnumerateObject())
        {
            var ch = new ChannelHealth { Name = prop.Name };
            var val = prop.Value;

            var isRunning = TryGetBool(val, "running");
            var isConfigured = TryGetBool(val, "configured");
            var isLinked = TryGetBool(val, "linked");
            var probeOk = val.TryGetProperty("probe", out var probe) && TryGetBool(probe, "ok");
            var hasError = val.TryGetProperty("lastError", out var lastError) && lastError.ValueKind != JsonValueKind.Null;

            ch.IsLinked = isLinked;
            if (val.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                ch.Status = status.GetString() ?? "unknown";
            else if (hasError)
                ch.Status = "error";
            else if (isRunning)
                ch.Status = "running";
            else if (isConfigured && (probeOk || isLinked))
                ch.Status = "ready";
            else if (isConfigured && !hasError)
                ch.Status = "ready";
            else
                ch.Status = "not configured";

            ch.Error = GetString(val, "error") ?? GetString(val, "lastError");
            ch.AuthAge = GetString(val, "authAge");
            ch.Type = GetString(val, "type");

            healthList.Add(ch);
        }

        return healthList.ToArray();
    }

    private static bool TryGetBool(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }
}

public class SessionInfo
{
    public string Key { get; set; } = "";
    public bool IsMain { get; set; }
    public string Status { get; set; } = "unknown";
    public string? Model { get; set; }
    public string? Channel { get; set; }
    public string? DisplayName { get; set; }
    public string? Provider { get; set; }
    public string? Subject { get; set; }
    public string? Room { get; set; }
    public string? Space { get; set; }
    public string? SessionId { get; set; }
    public string? ThinkingLevel { get; set; }
    public string? VerboseLevel { get; set; }
    public bool SystemSent { get; set; }
    public bool AbortedLastRun { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long ContextTokens { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? CurrentActivity { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string DisplayText
    {
        get
        {
            // Build directly with string interpolation to avoid List<string> + Join allocations.
            // Max 3 segments: prefix [· Channel] [· Activity|Status]
            var prefix = IsMain ? "Main" : "Sub";
            var hasChannel = !string.IsNullOrEmpty(Channel);
            var tail = !string.IsNullOrEmpty(CurrentActivity)
                ? CurrentActivity
                : (!string.IsNullOrEmpty(Status) && Status != "unknown" && Status != "active" ? Status : null);

            if (hasChannel && tail != null) return $"{prefix} · {Channel} · {tail}";
            if (hasChannel)                return $"{prefix} · {Channel}";
            if (tail != null)              return $"{prefix} · {tail}";
            return prefix;
        }
    }

    public string RichDisplayText
    {
        get
        {
            var title = !string.IsNullOrWhiteSpace(DisplayName)
                ? DisplayName!
                : (IsMain ? "Main session" : "Session");

            // Fixed-size array avoids List<string> allocation; at most 9 detail slots.
            var details = new string?[9];
            int n = 0;
            if (!string.IsNullOrWhiteSpace(Channel))
                details[n++] = Channel!;
            if (!string.IsNullOrWhiteSpace(Model))
                details[n++] = Model!;
            var ctx = ContextSummaryShort;
            if (!string.IsNullOrWhiteSpace(ctx))
                details[n++] = $"{ctx} ctx";
            if (!string.IsNullOrWhiteSpace(ThinkingLevel))
                details[n++] = $"think {ThinkingLevel}";
            if (!string.IsNullOrWhiteSpace(VerboseLevel))
                details[n++] = $"verbose {VerboseLevel}";
            if (SystemSent)
                details[n++] = "system";
            if (AbortedLastRun)
                details[n++] = "aborted";
            if (!string.IsNullOrWhiteSpace(CurrentActivity))
                details[n++] = CurrentActivity!;
            else if (!string.IsNullOrWhiteSpace(Status) && Status != "unknown" && Status != "active")
                details[n++] = Status;

            return n == 0 ? title : $"{title} · {string.Join(" · ", details, 0, n)}";
        }
    }

    public string AgeText => ModelFormatting.FormatAge(UpdatedAt ?? LastSeen);

    public string ContextSummaryShort
    {
        get
        {
            if (TotalTokens <= 0 || ContextTokens <= 0) return "";
            return $"{ModelFormatting.FormatLargeNumber(TotalTokens)}/{ModelFormatting.FormatLargeNumber(ContextTokens)}";
        }
    }
    
    /// <summary>Gets a shortened, user-friendly version of the session key.</summary>
    public string ShortKey
    {
        get
        {
            if (string.IsNullOrEmpty(Key)) return "unknown";
            
            // Extract meaningful part from session keys like "agent:main:subagent:uuid"
            var parts = Key.Split(':');
            if (parts.Length >= 3)
            {
                // Return something like "subagent" or "cron" 
                return parts[^2]; // Second to last part
            }
            
            // For file paths, just return filename
            if (Key.Contains('/') || Key.Contains('\\'))
            {
                return Path.GetFileName(Key);
            }
            
            return Key.Length > 20 ? Key[..17] + "..." : Key;
        }
    }

}

public class GatewayUsageInfo
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double CostUsd { get; set; }
    public int RequestCount { get; set; }
    public string? Model { get; set; }
    public string? ProviderSummary { get; set; }

    public string DisplayText
    {
        get
        {
            // Avoid allocating a List<string> + string.Join: accumulate up to 4 nullable
            // string slots and build the result with a single switch expression.
            string? p0 = TotalTokens > 0
                ? $"Tokens: {ModelFormatting.FormatLargeNumber(TotalTokens)}"
                : null;
            string? p1 = CostUsd > 0
                ? "$" + CostUsd.ToString("F2", CultureInfo.InvariantCulture)
                : null;
            string? p2 = RequestCount > 0
                ? $"{RequestCount} requests"
                : null;
            string? p3 = !string.IsNullOrEmpty(Model) ? Model : null;

            // If all four are null, fall back to ProviderSummary or "No usage data".
            if (p0 is null && p1 is null && p2 is null && p3 is null)
                return string.IsNullOrEmpty(ProviderSummary) ? "No usage data" : ProviderSummary!;

            // Pack non-null slots into a fixed-size array and join — one allocation.
            var parts = new string?[4];
            int n = 0;
            if (p0 is not null) parts[n++] = p0;
            if (p1 is not null) parts[n++] = p1;
            if (p2 is not null) parts[n++] = p2;
            if (p3 is not null) parts[n++] = p3;
            return string.Join(" · ", parts, 0, n);
        }
    }

}

public class GatewayUsageWindowInfo
{
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }
    public DateTime? ResetAt { get; set; }
}

public class GatewayUsageProviderInfo
{
    public string Provider { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Plan { get; set; }
    public string? Error { get; set; }
    public List<GatewayUsageWindowInfo> Windows { get; set; } = new();
}

public class GatewayUsageStatusInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<GatewayUsageProviderInfo> Providers { get; set; } = new();
}

public class GatewayCostUsageTotalsInfo
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public int MissingCostEntries { get; set; }
}

public class GatewayCostUsageDayInfo
{
    public string Date { get; set; } = "";
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public int MissingCostEntries { get; set; }
}

public class GatewayCostUsageInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Days { get; set; }
    public GatewayCostUsageTotalsInfo Totals { get; set; } = new();
    public List<GatewayCostUsageDayInfo> Daily { get; set; } = new();
}

public class SessionPreviewItemInfo
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
}

public class SessionPreviewInfo
{
    public string Key { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public List<SessionPreviewItemInfo> Items { get; set; } = new();
}

public class SessionsPreviewPayloadInfo
{
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SessionPreviewInfo> Previews { get; set; } = new();
}

public class SessionCommandResult
{
    public string Method { get; set; } = "";
    public bool Ok { get; set; }
    public string? Key { get; set; }
    public bool? Deleted { get; set; }
    public bool? Compacted { get; set; }
    public int? Kept { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
}

public class GatewayNodeInfo
{
    public string NodeId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string? Platform { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool IsOnline { get; set; }
    public int CapabilityCount { get; set; }
    public int CommandCount { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ShortId => NodeId.Length <= 12 ? NodeId : NodeId[..12] + "…";

    public string DisplayText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DisplayName) ? ShortId : DisplayName;
            var status = IsOnline ? "online" : (string.IsNullOrWhiteSpace(Status) ? "offline" : Status);
            return $"{name} · {status}";
        }
    }

    public string DetailText
    {
        get
        {
            // Fixed-size accumulator (up to 5 slots) avoids a heap List on every render.
            var slots = new string?[5];
            int count = 0;
            if (!string.IsNullOrWhiteSpace(Mode))    slots[count++] = Mode!;
            if (!string.IsNullOrWhiteSpace(Platform)) slots[count++] = Platform!;
            if (CommandCount > 0)    slots[count++] = $"{CommandCount} cmd";
            if (CapabilityCount > 0) slots[count++] = $"{CapabilityCount} cap";
            if (LastSeen.HasValue)   slots[count++] = $"seen {FormatAge(LastSeen.Value)}";
            return count == 0 ? "no details" : string.Join(" · ", slots, 0, count);
        }
    }

    private static string FormatAge(DateTime timestampUtc) => ModelFormatting.FormatAge(timestampUtc);
}

public enum GatewayDiagnosticSeverity
{
    Info,
    Warning,
    Critical
}

public class GatewayDiagnosticWarning
{
    public GatewayDiagnosticSeverity Severity { get; set; } = GatewayDiagnosticSeverity.Info;
    public string Category { get; set; } = "general";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? RepairAction { get; set; }
    public string? CopyText { get; set; }
}

public class ChannelCommandCenterInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public bool IsLinked { get; set; }
    public string? Error { get; set; }
    public string? AuthAge { get; set; }
    public string? Type { get; set; }
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }

    public static ChannelCommandCenterInfo FromHealth(ChannelHealth health)
    {
        var isHealthy = ChannelHealth.IsHealthyStatus(health.Status);
        var hasName = !string.IsNullOrWhiteSpace(health.Name);
        return new ChannelCommandCenterInfo
        {
            Name = health.Name,
            Status = health.Status,
            IsLinked = health.IsLinked,
            Error = health.Error,
            AuthAge = health.AuthAge,
            Type = health.Type,
            CanStart = hasName && !isHealthy,
            CanStop = hasName && isHealthy
        };
    }
}

public class NodeCapabilityHealthInfo
{
    public string NodeId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Platform { get; set; }
    public bool IsOnline { get; set; }
    public List<string> Capabilities { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SafeDeclaredCommands { get; set; } = new();
    public List<string> DangerousDeclaredCommands { get; set; } = new();
    public List<string> WindowsSpecificDeclaredCommands { get; set; } = new();
    public List<string> BlockedDeclaredCommands { get; set; } = new();
    public List<string> MissingSafeAllowlistCommands { get; set; } = new();
    public List<string> MissingDangerousAllowlistCommands { get; set; } = new();
    public List<string> MissingMacParityCommands { get; set; } = new();
    public List<GatewayDiagnosticWarning> Warnings { get; set; } = new();

    public static NodeCapabilityHealthInfo FromNode(GatewayNodeInfo node)
    {
        var commandSet = new HashSet<string>(node.Commands, StringComparer.OrdinalIgnoreCase);
        var platform = node.Platform ?? "";
        var isWindows = platform.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || platform.Contains("win32", StringComparison.OrdinalIgnoreCase);

        var info = new NodeCapabilityHealthInfo
        {
            NodeId = node.NodeId,
            DisplayName = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName,
            Platform = node.Platform,
            IsOnline = node.IsOnline,
            Capabilities = node.Capabilities.ToList(),
            Commands = node.Commands.ToList(),
            Permissions = new Dictionary<string, bool>(node.Permissions, StringComparer.OrdinalIgnoreCase),
            SafeDeclaredCommands = CommandCenterCommandGroups.SafeCompanionCommands
                .Where(commandSet.Contains)
                .ToList(),
            DangerousDeclaredCommands = CommandCenterCommandGroups.DangerousCommands
                .Where(commandSet.Contains)
                .ToList(),
            WindowsSpecificDeclaredCommands = CommandCenterCommandGroups.WindowsSpecificCommands
                .Where(commandSet.Contains)
                .ToList()
        };

        foreach (var command in info.Commands)
        {
            if (!CommandCenterDiagnostics.TryGetCommandPermission(info.Permissions, command, out var allowed) || allowed)
                continue;

            info.BlockedDeclaredCommands.Add(command);
            if (CommandCenterCommandGroups.SafeCompanionCommandSet.Contains(command))
                info.MissingSafeAllowlistCommands.Add(command);
            else if (CommandCenterCommandGroups.DangerousCommandSet.Contains(command))
                info.MissingDangerousAllowlistCommands.Add(command);
        }

        if (isWindows)
        {
            info.MissingMacParityCommands = CommandCenterCommandGroups.MacNodeParityCommands
                .Where(command => !commandSet.Contains(command))
                .ToList();
        }

        info.Warnings = CommandCenterDiagnostics.BuildNodeWarnings(info);
        return info;
    }
}

public class GatewayCommandCenterState
{
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Disconnected;
    public DateTime LastRefresh { get; set; } = DateTime.UtcNow;
    public List<ChannelCommandCenterInfo> Channels { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public GatewayUsageInfo? Usage { get; set; }
    public GatewayUsageStatusInfo? UsageStatus { get; set; }
    public GatewayCostUsageInfo? UsageCost { get; set; }
    public List<NodeCapabilityHealthInfo> Nodes { get; set; } = new();
    public List<GatewayDiagnosticWarning> Warnings { get; set; } = new();
}

public static class CommandCenterCommandGroups
{
    public static readonly string[] SafeCompanionCommands =
    [
        "canvas.present",
        "canvas.hide",
        "canvas.navigate",
        "canvas.eval",
        "canvas.snapshot",
        "canvas.a2ui.push",
        "canvas.a2ui.pushJSONL",
        "canvas.a2ui.reset",
        "camera.list",
        "location.get",
        "screen.snapshot",
        "device.info",
        "device.status"
    ];

    public static readonly FrozenSet<string> SafeCompanionCommandSet =
        SafeCompanionCommands.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] DangerousCommands =
    [
        "camera.snap",
        "camera.clip",
        "screen.record"
    ];

    public static readonly FrozenSet<string> DangerousCommandSet =
        DangerousCommands.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] WindowsSpecificCommands =
    [
        "system.execApprovals.get",
        "system.execApprovals.set"
    ];

    public static readonly string[] MacNodeParityCommands =
    [
        .. SafeCompanionCommands,
        .. DangerousCommands,
        "system.notify",
        "system.run",
        "system.which",
        "browser.proxy"
    ];
}

public static class CommandCenterDiagnostics
{
    private static readonly IReadOnlyDictionary<GatewayDiagnosticSeverity, int> s_severityPriority =
        new Dictionary<GatewayDiagnosticSeverity, int>
        {
            [GatewayDiagnosticSeverity.Critical] = 0,
            [GatewayDiagnosticSeverity.Warning] = 1,
            [GatewayDiagnosticSeverity.Info] = 2
        };

    public static List<GatewayDiagnosticWarning> SortAndDedupeWarnings(IEnumerable<GatewayDiagnosticWarning> warnings) =>
        warnings
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .GroupBy(w => $"{w.Severity}|{w.Category}|{w.Title}|{w.Detail}", StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(w => s_severityPriority[w.Severity])
            .ThenBy(w => w.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string BuildAllowCommandsRepairCommand(IEnumerable<string> commands)
    {
        var json = "[" + string.Join(",", commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(command => $"\"{command}\"")) + "]";
        return $"openclaw config set gateway.nodes.allowCommands '{json}'";
    }

    public static bool TryGetCommandPermission(
        IReadOnlyDictionary<string, bool> permissions,
        string command,
        out bool allowed)
    {
        allowed = false;
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (permissions.TryGetValue(command, out allowed))
            return true;

        if (permissions.TryGetValue($"commands.{command}", out allowed))
            return true;

        if (permissions.TryGetValue($"command:{command}", out allowed))
            return true;

        return false;
    }

    public static List<GatewayDiagnosticWarning> BuildNodeWarnings(NodeCapabilityHealthInfo node)
    {
        var warnings = new List<GatewayDiagnosticWarning>();

        if (!node.IsOnline)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "node",
                Title = "Node offline",
                Detail = $"{node.DisplayName} is not currently online."
            });
        }

        if (node.Commands.Count == 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "allowlist",
                Title = "No node commands visible",
                Detail = "The gateway did not report any commands for this node. It may be unpaired, filtered by policy, or connected to an older gateway."
            });
        }

        if (node.MissingSafeAllowlistCommands.Count > 0)
        {
            var missing = string.Join(", ", node.MissingSafeAllowlistCommands);
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "allowlist",
                Title = "Safe node commands are filtered by gateway policy",
                Detail = $"{missing} {(node.MissingSafeAllowlistCommands.Count == 1 ? "is" : "are")} declared by the node but not allowed by gateway policy. After changing allowCommands, re-approve or re-pair the device if the gateway keeps an older command snapshot.",
                RepairAction = "Copy safe allowlist repair command",
                CopyText = BuildAllowCommandsRepairCommand(CommandCenterCommandGroups.SafeCompanionCommands)
            });
        }

        if (node.DangerousDeclaredCommands.Count > 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "allowlist",
                Title = "Privacy-sensitive commands require explicit opt-in",
                Detail = string.Join(", ", node.DangerousDeclaredCommands) + " should only be available when explicitly allowed by gateway.nodes.allowCommands."
            });
        }

        if (node.MissingDangerousAllowlistCommands.Count > 0)
        {
            var blocked = string.Join(", ", node.MissingDangerousAllowlistCommands);
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "allowlist",
                Title = "Privacy-sensitive commands are currently blocked",
                Detail = $"{blocked} {(node.MissingDangerousAllowlistCommands.Count == 1 ? "is" : "are")} declared but filtered by gateway policy. Leave blocked unless you explicitly want camera or screen recording access for this node."
            });
        }

        if (node.BlockedDeclaredCommands.Count > 0 &&
            node.MissingSafeAllowlistCommands.Count == 0 &&
            node.MissingDangerousAllowlistCommands.Count == 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "allowlist",
                Title = "Some node commands are filtered",
                Detail = string.Join(", ", node.BlockedDeclaredCommands) + " are declared but not allowed by gateway policy."
            });
        }

        if (node.MissingMacParityCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "parity",
                Title = "Browser proxy parity not implemented",
                Detail = "Windows does not yet implement the Mac node's browser.proxy command."
            });
        }

        return warnings;
    }
}

/// <summary>Shared display-formatting helpers used by model classes.</summary>
internal static class ModelFormatting
{
    /// <summary>
    /// Formats a UTC timestamp as a human-readable age string (e.g. "just now", "5m ago", "2h ago", "3d ago").
    /// </summary>
    internal static string FormatAge(DateTime timestampUtc)
    {
        var delta = DateTime.UtcNow - timestampUtc;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 48) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>
    /// Formats a large integer with K/M suffix for compact display (e.g. 1500 → "1.5K", 2_000_000 → "2.0M").
    /// </summary>
    internal static string FormatLargeNumber(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K";
        return n.ToString();
    }
}

