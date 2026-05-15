using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Config;
using OpenMono.Rendering;
using OpenMono.Tools;

namespace OpenMono.Permissions;

public sealed record PermissionDecision(bool Allowed, string? Reason = null);

public sealed record CapabilityDecision(
    bool Allowed,
    string? Reason = null,
    IReadOnlyList<Capability>? EvaluatedCapabilities = null);

public sealed class PermissionEngine
{

    internal const string PermissionDeniedOnce =
        "User denied this request. Ask the user how to proceed.";
    internal const string PermissionDeniedSession =
        "User denied all requests of this type for this session. Do not retry.";

    private readonly AppConfig _config;
    private readonly IOutputSink _output;
    private readonly IInputReader _input;
    private readonly HashSet<string> _sessionAllowAll = [];
    private readonly HashSet<string> _sessionDenyAll = [];
    private int _consecutiveDenials;
    private int _totalDenials;

    private readonly HashSet<string> _sessionAllowCapTypes = [];
    private readonly HashSet<string> _sessionDenyCapTypes = [];

    private readonly List<(string CapType, string Pattern, bool Allow)> _sessionCapRules = [];

    public PermissionEngine(AppConfig config, IOutputSink output, IInputReader input)
    {
        _config = config;
        _output = output;
        _input  = input;
        _output.WriteInfo($"[Perm] PermissionEngine initialized. WorkingDirectory='{config.WorkingDirectory}'");
    }

    public async Task<CapabilityDecision> CheckCapabilitiesAsync(
        string toolName, IReadOnlyList<Capability> capabilities, CancellationToken ct)
    {

        if (capabilities.Count == 0)
            return new(true, null, capabilities);

        if (_sessionAllowAll.Contains(toolName))
            return new(true, null, capabilities);
        if (_sessionDenyAll.Contains(toolName))
            return new(false,
                $"{toolName} was denied for this session by the user. " +
                "This is an app-level block — NOT a file system permission issue. " +
                "Tell the user to start a new session and allow the tool when prompted. " +
                "Do NOT suggest chmod, chown, attrib, or any OS permission commands.",
                capabilities);

        foreach (var cap in capabilities)
        {
            var capType = cap.GetType().Name;

            if (_sessionDenyCapTypes.Contains(capType))
                return new(false, $"Capability type {capType} denied for this session", capabilities);

            var denyReason = CheckCapabilityDenyRules(cap);
            if (denyReason is not null)
                return new(false, denyReason, capabilities);
        }

        var uncoveredCaps = new List<Capability>();
        foreach (var cap in capabilities)
        {
            var capType = cap.GetType().Name;

            if (_sessionAllowCapTypes.Contains(capType))
            {
                _output.WriteInfo($"[Perm] {toolName} | {capType} | ALLOWED by sessionAllowCapTypes");
                continue;
            }

            if (CheckCapabilityAllowRules(cap))
            {
                _output.WriteInfo($"[Perm] {toolName} | {capType} | ALLOWED by session cap rules");
                continue;
            }

            if (IsAutoAllowedCapability(cap))
            {
                _output.WriteInfo($"[Perm] {toolName} | {capType} | ALLOWED by IsAutoAllowed");
                continue;
            }

            _output.WriteInfo($"[Perm] {toolName} | {capType} | NOT covered → will prompt. WorkingDir={_config.WorkingDirectory} | CapSummary={cap.Summary}");
            if (cap is FileWriteCap fwDbg)
                _output.WriteInfo($"[Perm]   FileWriteCap path='{fwDbg.Path}' | workDir='{_config.WorkingDirectory}' | StartsWith={fwDbg.Path.StartsWith(_config.WorkingDirectory)} | OrdinalIC={fwDbg.Path.StartsWith(_config.WorkingDirectory, StringComparison.OrdinalIgnoreCase)}");
            if (cap is FileReadCap frDbg)
                _output.WriteInfo($"[Perm]   FileReadCap path='{frDbg.Path}' | workDir='{_config.WorkingDirectory}' | StartsWith={frDbg.Path.StartsWith(_config.WorkingDirectory)} | OrdinalIC={frDbg.Path.StartsWith(_config.WorkingDirectory, StringComparison.OrdinalIgnoreCase)}");

            uncoveredCaps.Add(cap);
        }

        if (uncoveredCaps.Count == 0)
            return new(true, null, capabilities);

        return await PromptUserForCapabilitiesAsync(toolName, uncoveredCaps, capabilities, ct);
    }

    public async Task<PermissionDecision> CheckAsync(
        string toolName, JsonElement input, PermissionLevel level, CancellationToken ct)
    {
        _output.WriteInfo($"[Perm/Legacy] {toolName} | level={level} | input={input}");

        if (_config.Permissions.Tools.TryGetValue(toolName, out var rules))
        {
            var inputStr = input.ToString();

            if (rules.Deny.Any(pattern => MatchesPattern(inputStr, pattern)))
            {
                if (TrackDenial())
                    return await PromptUserAsync(toolName, input, ct);
                return new(false, $"Denied by permission rule for {toolName}");
            }
        }

        if (_sessionAllowAll.Contains(toolName))
        {
            TrackAllow();
            return new(true);
        }
        if (_sessionDenyAll.Contains(toolName))
        {
            if (TrackDenial())
                return await PromptUserAsync(toolName, input, ct);
            return new(false,
                $"{toolName} was denied for this session by the user. " +
                "This is an app-level block — NOT a file system permission issue. " +
                "Tell the user to start a new session and allow the tool when prompted. " +
                "Do NOT suggest chmod, chown, attrib, or any OS permission commands.");
        }

        if (level == PermissionLevel.AutoAllow)
        {
            TrackAllow();
            return new(true);
        }

        if (level == PermissionLevel.Deny)
        {
            if (TrackDenial())
                return await PromptUserAsync(toolName, input, ct);
            return new(false, "Tool is not permitted");
        }

        if (rules is not null)
        {
            var inputStr = input.ToString();

            if (rules.Allow.Any(pattern => MatchesPattern(inputStr, pattern)))
            {
                TrackAllow();
                return new(true);
            }
        }

        var prompted = await PromptUserAsync(toolName, input, ct);
        if (prompted.Allowed) TrackAllow(); else TrackDenial();
        return prompted;
    }

    private string? CheckCapabilityDenyRules(Capability cap)
    {

        foreach (var (capType, pattern, allow) in _sessionCapRules)
        {
            if (!allow && cap.GetType().Name == capType && MatchesCapabilityPattern(cap, pattern))
                return $"Denied by session rule: {cap.Summary}";
        }

        return cap switch
        {
            FileWriteCap fw when IsProtectedPath(fw.Path) =>
                $"Protected path: {fw.Path}",
            ProcessExecCap pe when IsBlockedBinary(pe.Binary) =>
                $"Blocked binary: {pe.Binary}",
            VcsMutationCap vc when vc.Operation is "push" or "force-push" =>
                null,
            _ => null
        };
    }

    private bool CheckCapabilityAllowRules(Capability cap)
    {

        foreach (var (capType, pattern, allow) in _sessionCapRules)
        {
            if (allow && cap.GetType().Name == capType && MatchesCapabilityPattern(cap, pattern))
                return true;
        }

        return false;
    }

    private bool IsAutoAllowedCapability(Capability cap) => cap switch
    {

        FileReadCap fr when ResolvePath(fr.Path).StartsWith(_config.WorkingDirectory, StringComparison.OrdinalIgnoreCase) => true,

        FileWriteCap fw when ResolvePath(fw.Path).StartsWith(_config.WorkingDirectory, StringComparison.OrdinalIgnoreCase) => true,

        MemoryCap mc when mc.Operation == "read" => true,

        ProcessExecCap pe when IsSafeReadOnlyCommand(pe) => true,

        ProcessExecCap pe when !string.IsNullOrEmpty(pe.WorkingDirectory) &&
            pe.WorkingDirectory.StartsWith(_config.WorkingDirectory, StringComparison.OrdinalIgnoreCase) => true,

        _ => false
    };

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, _config.WorkingDirectory);

    private static readonly HashSet<string> SafeReadOnlyBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "cat", "head", "tail", "wc", "pwd", "whoami", "date", "echo",
        "which", "type", "file", "stat", "du", "df", "cd",
    };

    private static readonly HashSet<string> SafeGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "branch", "show", "fetch", "pull",
        "remote", "tag", "describe", "rev-parse", "rev-list", "ls-files", "shortlog",
    };

    // Git global options that consume the next argument as a value
    private static readonly HashSet<string> GitFlagsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--super-prefix", "--exec-path",
    };

    private static readonly Dictionary<string, HashSet<string>> SafeSubcommandsByBinary =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = SafeGitSubcommands,
            ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view" },
            ["yarn"] = new(StringComparer.OrdinalIgnoreCase) { "list" },
            ["dotnet"] = new(StringComparer.OrdinalIgnoreCase) { "--version" },
            ["node"] = new(StringComparer.OrdinalIgnoreCase) { "--version" },
            ["python"] = new(StringComparer.OrdinalIgnoreCase) { "--version" },
        };

    private static bool IsSafeReadOnlyCommand(ProcessExecCap cap)
    {
        if (SafeReadOnlyBinaries.Contains(cap.Binary))
            return true;

        if (!SafeSubcommandsByBinary.TryGetValue(cap.Binary, out var safeSubcommands))
            return false;

        // Walk args, skipping global flags and their values, to find the subcommand
        var flagsWithValue = cap.Binary.Equals("git", StringComparison.OrdinalIgnoreCase)
            ? GitFlagsWithValue
            : null;

        for (var i = 0; i < cap.Args.Count; i++)
        {
            var arg = cap.Args[i];

            if (flagsWithValue is not null && flagsWithValue.Contains(arg))
            {
                i++; // skip the flag's value
                continue;
            }

            if (arg.StartsWith('-'))
                continue;

            return safeSubcommands.Contains(arg);
        }

        return false;
    }

    private bool IsProtectedPath(string path)
    {
        var protectedPaths = new[] { "/etc/", "/usr/", "/bin/", "/sbin/", "/System/", "/Library/" };
        return protectedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlockedBinary(string binary)
    {
        var blockedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {

            "sudo", "su", "doas", "pkexec",

            "chmod", "chown", "chattr", "setfacl",
            "icacls", "takeown", "attrib",
        };
        return blockedBinaries.Contains(binary);
    }

    private static bool MatchesCapabilityPattern(Capability cap, string pattern)
    {
        var target = cap switch
        {
            FileReadCap fr => fr.Path,
            FileWriteCap fw => fw.Path,
            ProcessExecCap pe => pe.Binary,
            NetworkEgressCap ne => ne.Host,
            VcsMutationCap vc => vc.Repo,
            MemoryCap mc => mc.Namespace,
            AgentSpawnCap asc => asc.AgentType,
            _ => cap.Summary
        };
        return MatchesPattern(target, pattern);
    }

    private async Task<CapabilityDecision> PromptUserForCapabilitiesAsync(
        string toolName, List<Capability> uncoveredCaps, IReadOnlyList<Capability> allCaps, CancellationToken ct)
    {
        var summary = $"{toolName} requires:\n" +
                      string.Join("\n", uncoveredCaps.Select(c => $"  - {c.Summary}"));

        var response = await _input.AskPermissionAsync(toolName, summary, ct);

        return response switch
        {
            PermissionResponse.Allow => new(true, null, allCaps),
            PermissionResponse.Deny => new(false, PermissionDeniedOnce, allCaps),
            PermissionResponse.AllowAll => AllowAllCapabilitiesForSession(toolName, uncoveredCaps, allCaps),
            PermissionResponse.DenyAll => DenyAllCapabilitiesForSession(toolName, allCaps),
            _ => new(false, "Unknown response", allCaps)
        };
    }

    private CapabilityDecision AllowAllCapabilitiesForSession(
        string toolName, List<Capability> caps, IReadOnlyList<Capability> allCaps)
    {
        _sessionAllowAll.Add(toolName);

        foreach (var cap in caps)
            _sessionAllowCapTypes.Add(cap.GetType().Name);
        return new(true, null, allCaps);
    }

    private CapabilityDecision DenyAllCapabilitiesForSession(string toolName, IReadOnlyList<Capability> allCaps)
    {
        _sessionDenyAll.Add(toolName);
        return new(false, PermissionDeniedSession, allCaps);
    }

    private async Task<PermissionDecision> PromptUserAsync(
        string toolName, JsonElement input, CancellationToken ct)
    {
        var summary = BuildToolSummary(toolName, input);
        var response = await _input.AskPermissionAsync(toolName, summary, ct);

        return response switch
        {
            PermissionResponse.Allow => new(true),
            PermissionResponse.Deny => new(false, PermissionDeniedOnce),
            PermissionResponse.AllowAll => AllowAllForSession(toolName),
            PermissionResponse.DenyAll => DenyAllForSession(toolName),
            _ => new(false, "Unknown response")
        };
    }

    private PermissionDecision AllowAllForSession(string toolName)
    {
        _sessionAllowAll.Add(toolName);
        return new(true);
    }

    private PermissionDecision DenyAllForSession(string toolName)
    {
        _sessionDenyAll.Add(toolName);
        return new(false,
            $"{toolName} was denied for this session by the user. " +
            "This is an app-level block — NOT a file system permission issue. " +
            "Tell the user to start a new session and allow the tool when prompted. " +
            "Do NOT suggest chmod, chown, attrib, or any OS permission commands.");
    }

    private bool TrackDenial()
    {
        _consecutiveDenials++;
        _totalDenials++;
        if (_consecutiveDenials >= 3 || _totalDenials >= 20)
        {
            _consecutiveDenials = 0;
            _output.WriteInfo(
                $"[Permissions] {_totalDenials} denials this session — check your permission settings. Escalating to prompt.");
            return true;
        }
        return false;
    }

    private void TrackAllow() => _consecutiveDenials = 0;

    private static string BuildToolSummary(string toolName, JsonElement input)
    {
        if (toolName == "Bash" && input.TryGetProperty("command", out var cmd))
            return $"$ {cmd.GetString()}";

        if ((toolName == "FileWrite" || toolName == "FileEdit") &&
            input.TryGetProperty("file_path", out var fp))
            return fp.GetString() ?? input.ToString();

        return input.ToString();
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern == "*") return true;

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase);
    }
}

public enum PermissionResponse
{
    Allow,
    Deny,
    AllowAll,
    DenyAll
}
