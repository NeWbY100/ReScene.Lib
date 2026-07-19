using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReScene.SRR;

/// <summary>
/// Single writer-boundary name contract (spec §1a): OS-final-path containment (resolves every
/// ancestor junction/symlink — Path.GetFullPath alone does not), forward-slash logical names,
/// and SFV-entry hardening. Windows-first (GetFinalPathNameByHandle); on non-Windows,
/// a component-order walk resolves each existing ancestor's link target before the next
/// component is applied, so a symlink is always resolved before a following "..". Containment
/// is centralized in <see cref="EnsureContainedRelative"/> and reused by every entry point.
/// </summary>
public static class SrrNameCanonicalizer
{
    // Windows paths are case-insensitive at the filesystem; POSIX paths are case-sensitive
    // (codex Important #3 — "/tmp/Root" must not match "/tmp/root" on POSIX).
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string GetFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Component-order walk: resolve EVERY ancestor (codex r1 f1 / r4 f1 / Critical #1).
            return ResolveAncestorChain(path);
        }

        using SafeFileHandle handle = OpenForMetadata(path);
        var buffer = new char[260];
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new SrrNameException($"Cannot resolve final path (Win32 error {error}): {path}");
            }

            if (length < buffer.Length)
            {
                return NormalizeExtendedPrefix(new string(buffer, 0, (int)length));
            }

            // The API returns the required buffer size (including the null terminator) when
            // the supplied buffer is too small — grow and retry rather than rejecting a valid,
            // merely-long result (codex Important #4).
            buffer = new char[length];
        }
    }

    // Strips \\?\ only where it is safe and lossless: drive-letter paths, and the distinct
    // \\?\UNC\ form for UNC shares. Other device/volume forms (\\?\Volume{GUID}\,
    // \\?\GLOBALROOT\, ...) have no non-extended equivalent and are returned unchanged rather
    // than mangled into an unusable or wrong path (codex Important #5).
    private static string NormalizeExtendedPrefix(string finalPath)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        if (finalPath.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[extendedUncPrefix.Length..];
        }

        const string extendedPrefix = @"\\?\";
        if (finalPath.StartsWith(extendedPrefix, StringComparison.Ordinal)
            && finalPath.Length >= extendedPrefix.Length + 2
            && char.IsAsciiLetter(finalPath[extendedPrefix.Length])
            && finalPath[extendedPrefix.Length + 1] == ':')
        {
            return finalPath[extendedPrefix.Length..];
        }

        return finalPath;
    }

    // Component-order POSIX fallback: walks the ORIGINAL path components in order, resolving
    // each existing component's link target before the next component is applied. This is
    // deliberately NOT `Path.GetFullPath(path)` first — GetFullPath collapses ".." lexically
    // BEFORE any symlink is resolved, so "/root/L/../secret" with L -> /outside/dir would be
    // checked as "/root/secret" instead of the OS-correct "/outside/secret" (codex Critical #1).
    private static string ResolveAncestorChain(string path)
    {
        string absolute = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
        string root = Path.GetPathRoot(absolute)!;
        string current = root;
        foreach (string component in absolute[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = ApplyComponent(current, component, root);
        }

        return current;
    }

    // Applies one raw path component to an already-fully-resolved `current` prefix: "." is a
    // no-op, ".." pops one level (safe — `current` never has an unresolved link at its tail),
    // and any other component is resolved through its link target (if it has one) before being
    // adopted as the new `current`.
    private static string ApplyComponent(string current, string component, string root)
    {
        if (component == ".")
        {
            return current;
        }

        if (component == "..")
        {
            return Path.GetDirectoryName(TrimAllTrailingSeparators(current)) ?? root;
        }

        string candidate = Path.Combine(current, component);
        FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
        FileSystemInfo resolved = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        return resolved.FullName;
    }

    public static string CanonicalizeRelative(string rootFinalPath, string sourcePath)
    {
        string source = GetFinalPath(sourcePath);
        return EnsureContainedRelative(rootFinalPath, source, sourcePath, "Source is outside the release root");
    }

    // Centralized final-path containment (codex "CENTRALIZE first"): reused by both
    // CanonicalizeRelative and ResolveSfvEntry so the boundary math — filesystem-root and
    // repeated-trailing-separator normalization, host-appropriate case sensitivity — is
    // computed exactly once (codex Important #3). Trims ALL trailing separators (not just one,
    // unlike Path.TrimEndingDirectorySeparator, which preserves a bare root's own separator and
    // so cannot be used here) before re-adding exactly one boundary separator.
    private static string EnsureContainedRelative(
        string finalRoot, string finalCandidate, string displayPath, string escapeContext)
    {
        string root = TrimAllTrailingSeparators(finalRoot);
        string candidate = TrimAllTrailingSeparators(finalCandidate);
        string boundary = root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(boundary, _pathComparison))
        {
            throw new SrrNameException($"{escapeContext}: {displayPath}");
        }

        return candidate[boundary.Length..].Replace('\\', '/');
    }

    private static string TrimAllTrailingSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string CanonicalizeLogicalName(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new SrrNameException($"Invalid stored logical name: {logicalName}");
        }

        ValidatePortableRootingGrammar(logicalName, "Stored logical name");

        string name = logicalName.Replace('\\', '/');
        if (name == "." || name.Split('/').Any(seg => seg is "." or ".." or ""))
        {
            throw new SrrNameException($"Invalid stored logical name: {logicalName}");
        }

        return name;
    }

    // Host-independent rooted-name grammar (codex Important #6): the host OS's own
    // Path.IsPathRooted only recognizes its own native rooted forms — e.g. on POSIX,
    // Path.IsPathRooted("C:/x") is false — so "C:/abs/x.nfo" would slip through on Linux.
    // Rejects leading '/' or '\' (POSIX absolute, UNC, and device prefixes all start this way)
    // and drive designators ("C:", "C:relative", ...) regardless of the host.
    private static void ValidatePortableRootingGrammar(string rawName, string exceptionContext)
    {
        if (rawName.Length > 0 && rawName[0] is '/' or '\\')
        {
            throw new SrrNameException($"{exceptionContext} is rooted: {rawName}");
        }

        if (rawName.Length >= 2 && char.IsAsciiLetter(rawName[0]) && rawName[1] == ':')
        {
            throw new SrrNameException($"{exceptionContext} is rooted: {rawName}");
        }
    }

    public static string ResolveSfvEntry(string sfvDirectory, string entryName)
    {
        ValidatePortableRootingGrammar(entryName, "SFV entry");

        string normalized = entryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string finalDir = GetFinalPath(sfvDirectory);
        string candidateFinal = ResolveExistingPrefixThenAppend(finalDir, normalized);

        // Same centralized containment as CanonicalizeRelative, on FINAL (link-resolved) paths
        // — a junction/symlink named e.g. "J" inside sfvDirectory that targets outside it is
        // now caught, where the old lexical-only check accepted it (codex Critical #2).
        _ = EnsureContainedRelative(finalDir, candidateFinal, entryName, "SFV entry escapes its directory");
        return candidateFinal;
    }

    // Walks `relativeSpec` component-by-component from the already-final `finalBase`, resolving
    // each EXISTING ancestor through GetFinalPath (so a link at any level is caught — codex
    // Critical #2), then literal-appending any trailing components that don't exist on disk (an
    // SFV entry may legitimately reference a file not yet materialized).
    private static string ResolveExistingPrefixThenAppend(string finalBase, string relativeSpec)
    {
        string current = finalBase;
        bool stillExisting = true;
        foreach (string component in relativeSpec.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                current = Path.GetDirectoryName(TrimAllTrailingSeparators(current)) ?? current;
                continue;
            }

            string candidate = Path.Combine(current, component);
            if (stillExisting && (Directory.Exists(candidate) || File.Exists(candidate)))
            {
                current = GetFinalPath(candidate);
            }
            else
            {
                stillExisting = false;
                current = candidate;
            }
        }

        return current;
    }

    private static SafeFileHandle OpenForMetadata(string path)
    {
        // FILE_FLAG_BACKUP_SEMANTICS (0x02000000) lets CreateFileW open directories too.
        SafeFileHandle handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastPInvokeError();

        // Our raw P/Invoke doesn't get .NET's automatic long-path (\\?\) prefixing, so a path
        // beyond MAX_PATH can fail here even though it's otherwise valid. Retry once with an
        // explicit extended-length form (codex Important #4). Path.GetFullPath is safe here —
        // this is a Windows-only fallback used just to widen a failed native open, not a
        // containment decision, so it cannot reintroduce the Critical #1 symlink-vs-".." bug.
        string extended = ToExtendedLengthPath(path);
        if (string.Equals(extended, path, StringComparison.Ordinal))
        {
            throw new SrrNameException($"Cannot open for path resolution (Win32 error {error}): {path}");
        }

        handle = CreateFileW(extended, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int retryError = Marshal.GetLastPInvokeError();
            throw new SrrNameException($"Cannot open for path resolution (Win32 error {retryError}): {path}");
        }

        return handle;
    }

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr securityAttrs,
        FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}
