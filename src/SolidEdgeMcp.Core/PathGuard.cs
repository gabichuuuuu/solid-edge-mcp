// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Single gate for file paths: only paths inside the allowed folders or
/// the work folder are allowed. It resolves <c>..</c>, links, junctions, 8.3 names, network drives and
/// letter casing before comparing. Documented residual risk: between the check and the use, another
/// process could change a link inside an allowed folder.
/// </summary>
public sealed partial class PathGuard
{
    private readonly List<string> _roots = [];

    public PathGuard(IEnumerable<string> configuredRoots, string workFolder)
    {
        ArgumentNullException.ThrowIfNull(configuredRoots);
        Directory.CreateDirectory(workFolder);
        WorkFolder = ResolveExisting(Path.GetFullPath(workFolder));
        _roots.Add(WorkFolder);
        ExchangeFolder = WhereNewEntriesLand(WorkFolder);
        if (!ExchangeFolder.Equals(WorkFolder, StringComparison.OrdinalIgnoreCase))
        {
            _roots.Add(ExchangeFolder);
        }

        foreach (string configured in configuredRoots)
        {
            string expanded = Environment.ExpandEnvironmentVariables(configured);
            try
            {
                string syntactic = ValidateSyntax(expanded, "allowedRoots");
                if (!Directory.Exists(syntactic))
                {
                    Warnings.Add($"Allowed root does not exist and is ignored: '{configured}'.");
                    continue;
                }

                string resolved = ResolveExisting(syntactic);
                if (!_roots.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                {
                    _roots.Add(resolved);
                    ConfiguredRoots.Add(resolved);
                }
            }
            catch (ToolException ex)
            {
                Warnings.Add($"Allowed root is invalid and is ignored: '{configured}' ({ex.Message}).");
            }
        }

        if (ConfiguredRoots.Count == 0)
        {
            Warnings.Add("No usable allowedRoots: only the server work folder is allowed. Configure policy.json.");
        }
    }

    /// <summary>Resolved work folder; always allowed.</summary>
    public string WorkFolder { get; }

    /// <summary>
    /// Physical folder where what the server creates in the work folder lands: the place for the files that Solid Edge
    /// reads or writes (captures, copies it opens, reports it writes). It is the work folder, except when a packaged
    /// client (Claude Desktop) starts the server: Windows then puts what the server creates under %LocalAppData% in the
    /// package's own folder, while Solid Edge, outside the package, uses the real one, so a path under the work folder
    /// would name two different places. Always allowed.
    /// </summary>
    public string ExchangeFolder { get; }

    /// <summary>Configured folders that exist, already resolved.</summary>
    public List<string> ConfiguredRoots { get; } = [];

    /// <summary>All allowed folders (work + configured).</summary>
    public IReadOnlyList<string> AllowedRoots => _roots;

    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Returns the canonical path if it is allowed; otherwise throws <see cref="ToolException"/>.
    /// </summary>
    public string Check(string? path, string argumentName = "path")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ToolException.InvalidArgument($"'{argumentName}' must be a non-empty absolute path.");
        }

        string full = ValidateSyntax(path, argumentName);
        string canonical = ResolveLongestExistingPrefix(full);
        if (!IsUnderAnyRoot(canonical))
        {
            throw new ToolException(
                "path_not_allowed",
                $"'{argumentName}' is outside the allowed folders: '{path}'.",
                $"Use a path under one of: {string.Join("; ", _roots)}. Allowed folders are configured in policy.json (allowedRoots).");
        }

        return canonical;
    }

    public bool IsAllowed(string? path)
    {
        try
        {
            Check(path);
            return true;
        }
        catch (ToolException)
        {
            return false;
        }
    }

    /// <summary>Checks whether a text looks like an absolute Windows path (to filter generic arguments).</summary>
    public static bool LooksLikePath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (DriveLike().IsMatch(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal));

    private bool IsUnderAnyRoot(string canonical)
    {
        foreach (string root in _roots)
        {
            string trimmed = root.TrimEnd('\\');
            string prefix = trimmed + "\\";
            if (string.Equals(canonical.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Syntactic validation and normalization (without touching the disk).</summary>
    internal static string ValidateSyntax(string path, string argumentName)
    {
        string p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)
            || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            throw Reject(argumentName, path, "device and extended-length prefixes (\\\\?\\, \\\\.\\) are not accepted");
        }

        if (!Path.IsPathFullyQualified(p))
        {
            throw Reject(argumentName, path, "it must be an absolute path such as C:\\Folder\\file.par");
        }

        if (p.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0 || p.Any(c => c < 32))
        {
            throw Reject(argumentName, path, "it contains characters that are not valid in a path");
        }

        bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
        int colon = p.IndexOf(':', unc ? 0 : 2);
        if (colon >= 0)
        {
            throw Reject(argumentName, path, "alternate data streams (':' after the drive) are not accepted");
        }

        string[] segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments.Skip(unc ? 0 : 1))
        {
            if (segment is "." or "..")
            {
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw Reject(argumentName, path, $"the segment '{segment}' ends with a dot or a space");
            }

            if (segment.Length > 255)
            {
                throw Reject(argumentName, path, "a path segment is longer than 255 characters");
            }

            if (DeviceName().IsMatch(segment))
            {
                throw Reject(argumentName, path, $"'{segment}' is a reserved device name");
            }
        }

        return Path.GetFullPath(p);
    }

    private static ToolException Reject(string argumentName, string path, string reason) =>
        new("invalid_path", $"'{argumentName}' is not an accepted path ('{path}'): {reason}.");

    /// <summary>Resolves the longest existing prefix and appends the rest unresolved.</summary>
    private static string ResolveLongestExistingPrefix(string full)
    {
        string current = TrimSeparator(full);
        var pending = new Stack<string>();
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                // Not even the root exists (missing drive or network resource): compare without resolving.
                return full;
            }

            pending.Push(Path.GetFileName(current));
            current = TrimSeparator(parent);
        }

        string resolved = ResolveExisting(current);
        while (pending.Count > 0)
        {
            resolved = Path.Combine(resolved, pending.Pop());
        }

        return resolved;
    }

    /// <summary>Physical folder of a new entry created in the folder (a probe folder, removed again).</summary>
    private static string WhereNewEntriesLand(string folder)
    {
        string probe = Path.Combine(folder, ".probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            return Path.GetDirectoryName(ResolveExisting(probe)) ?? folder;
        }
        finally
        {
            try
            {
                Directory.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder left in the work folder does no harm.
            }
        }
    }

    /// <summary>Removes the trailing separator except at the root of a drive (<c>C:\</c>).</summary>
    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd('\\');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    /// <summary>Final path of an existing file or folder, as reported by the file system.</summary>
    internal static string ResolveExisting(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new ToolException(
                "path_unresolvable",
                $"Cannot resolve '{path}': {new Win32Exception(error).Message}",
                "Check that the path exists and is accessible.");
        }

        var buffer = new StringBuilder(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new ToolException("path_unresolvable", $"Cannot resolve '{path}': {new Win32Exception(error).Message}");
            }

            if (length < buffer.Capacity)
            {
                break;
            }

            buffer.Capacity = (int)length + 1;
        }

        string final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + final[8..];
        }

        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)\s*(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLike();

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}
