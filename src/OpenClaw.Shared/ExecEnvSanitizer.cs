using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace OpenClaw.Shared;

/// <summary>
/// Filters environment variables supplied by remote agents to prevent
/// execution-hook injection, loader hijacking, and shell-influence attacks.
///
/// An agent can request env overrides that alter how the OS resolves
/// executables (PATH, PATHEXT), pre-loads native libraries (LD_PRELOAD,
/// DYLD_INSERT_LIBRARIES), injects language-runtime options (NODE_OPTIONS,
/// PYTHONSTARTUP), or substitutes the shell itself (ComSpec, BASH_ENV).
/// This sanitizer removes those overrides before the command runs so that
/// an otherwise-approved command cannot be silently hijacked.
/// </summary>
public static class ExecEnvSanitizer
{
    // Exact-match denylist — case-insensitive (Windows env vars are case-insensitive).
    private static readonly FrozenSet<string> s_blocked =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Executable / shell resolution
            "PATH", "PATHEXT", "ComSpec",

            // PowerShell module / execution policy injection
            "PSModulePath",

            // Node.js runtime injection
            "NODE_OPTIONS", "NODE_PATH",

            // Python runtime injection
            "PYTHONPATH", "PYTHONSTARTUP", "PYTHONUSERBASE",

            // Ruby runtime injection
            "RUBYOPT", "RUBYLIB",

            // Shell startup scripts (bash, sh, zsh)
            "BASH_ENV", "ENV", "ZDOTDIR",

            // Git execution hook injection
            "GIT_SSH", "GIT_SSH_COMMAND", "GIT_EXEC_PATH",
            "GIT_PROXY_COMMAND", "GIT_ASKPASS",

            // Unix dynamic linker — still block on Windows for defence-in-depth
            "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT",
            "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",

            // Perl runtime injection
            "PERL5OPT", "PERL5LIB", "PERLIO",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Prefix denylist — any variable whose name starts with one of these is blocked.
    private static readonly string[] s_blockedPrefixes =
    [
        "LD_",     // Linux dynamic linker family
        "DYLD_",   // macOS dynamic linker family
    ];

    /// <summary>Result of sanitizing an environment variable dictionary.</summary>
    public sealed class SanitizeResult
    {
        /// <summary>Variables that passed the filter and may be forwarded to the process.</summary>
        public Dictionary<string, string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Variable names that were removed by the sanitizer.</summary>
        public List<string> Blocked { get; } = [];
    }

    /// <summary>
    /// Filters dangerous environment variable overrides from <paramref name="env"/>.
    /// Returns the allowed subset and the list of blocked names for audit logging.
    /// </summary>
    public static SanitizeResult Sanitize(Dictionary<string, string> env)
    {
        var result = new SanitizeResult();
        foreach (var (name, value) in env)
        {
            if (IsBlocked(name))
                result.Blocked.Add(name);
            else
                result.Allowed[name] = value;
        }
        return result;
    }

    /// <summary>Returns true if the environment variable name is on the denylist.</summary>
    public static bool IsBlocked(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;

        // Reject names with characters that are invalid in env-var names.
        // '=' is used as the key/value separator in the process environment block;
        // control characters and null bytes are never legitimate.
        foreach (var c in name)
        {
            if (c == '=' || c < 0x20)
                return true;
        }

        if (s_blocked.Contains(name))
            return true;

        foreach (var prefix in s_blockedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
