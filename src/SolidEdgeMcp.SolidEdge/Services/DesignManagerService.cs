// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Design Manager, the program installed with Solid Edge that manages files and their links without opening them in Solid
/// Edge, driven through its own automation server (DesignManager.Application, DesMgr.exe /automation): a hidden instance of
/// its own is started for each call and closed after it (a Design Manager the person has open is another process and is
/// not touched). Verified in Solid Edge 2026 on closed files, in the set of a top document (OpenFileInDesignManager, then
/// SetAction and PerformAction, as the rows of its window): rename, copy (Save As), replace and move write the files and
/// update the documents of the set that use them; revise copies a file as its next revision ("name-1.par", Revision 1)
/// and the set uses the revision; release sets the Released status of that file only. Obsolete marks every document of the
/// set and moves files, so it is not offered (the status action writes one file's status). A document outside the set that
/// used a renamed or moved file is left with a broken link (Design Manager finds those users through its global search
/// scope, an option of the program that this server does not change); the link object's SetPath and the document's
/// SaveAllLinks point it at the new file, so users found in the given folders are updated that way. Every change is checked
/// by reading the links back and the files on disk. The documents an action rewrites (those that use the files) must be in
/// the allowed folders and not released; a library the set only reads may be read-only.
/// </summary>
public static class DesignManagerService
{
    // DesignManagerAction (reference/api-external/DesignManager.txt).
    private const int CopyAction = 1;
    private const int ReviseAction = 3;
    private const int RenameAction = 5;
    private const int ReplaceAction = 7;
    private const int MoveAction = 9;
    private const int ReleaseAction = 11;

    /// <summary>Most documents a scan of folders reads.</summary>
    private const int MaxFiles = 5000;

    /// <summary>
    /// A Design Manager step (opening a document, or the action itself) that takes longer is ended by closing its process: a
    /// hidden dialog box waits forever. Shorter than the timeouts of se_design_manager, so a hung call frees the server.
    /// </summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan ActionLimit = TimeSpan.FromMinutes(8);

    /// <summary>The watchdog of the Design Manager call running on this thread, restarted at every step.</summary>
    [ThreadStatic]
    private static Timer? watchdog;

    private static readonly string[] DocumentExtensions = [".asm", ".dft", ".par", ".psm", ".pwd"];

    private static readonly string[] DocumentPatterns = [.. DocumentExtensions.Select(e => "*" + e)];

    /// <summary>Statuses whose documents Solid Edge keeps read-only: their links do not change.</summary>
    private static readonly string[] LockedStatuses = ["released", "baselined", "obsolete"];

    /// <summary>Every document a file refers to, at every level, with its status, revision and document number.</summary>
    public static object Links(SeContext context, string? path)
    {
        string top = Existing(context, path, "path");
        return WithDesignManager(manager =>
        {
            var rows = new List<LinkRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { top };
            void Walk(string file, int level)
            {
                foreach (object link in LinkedDocuments(manager, file))
                {
                    string child = FullName(link);
                    bool exists = File.Exists(child);
                    rows.Add(new LinkRow(child, file, level, exists, exists ? StatusOf(link) : null, Text(link, "Revision"), Text(link, "DocNum")));
                    if (seen.Add(child) && exists && level < 32 && rows.Count < MaxFiles)
                    {
                        Walk(child, level + 1);
                    }
                }
            }

            Walk(top, 1);
            object document = Open(manager, top);
            return new
            {
                document = top,
                status = StatusOf(document),
                revision = Text(document, "Revision"),
                documentNumber = Text(document, "DocNum"),
                count = rows.Count,
                missing = rows.Count(r => !r.Exists),
                links = rows.Select(r => new { file = r.File, parent = r.Parent, level = r.Level, exists = r.Exists, status = r.Status, revision = r.Revision, documentNumber = r.DocumentNumber }),
            };
        });
    }

    /// <summary>
    /// Renames, copies, replaces, moves, revises or releases files of the set of a top document; the documents of the set that
    /// use them are updated. For rename and move, the documents of <paramref name="folders"/> (default the folders of the
    /// files and of the top document) outside the set that use them are pointed at the new files too.
    /// </summary>
    public static object Act(SeContext context, string action, string? path, string[]? files, string[]? to, string[]? folders, bool subfolders)
    {
        int code = action switch
        {
            "rename" => RenameAction,
            "copy" => CopyAction,
            "replace" => ReplaceAction,
            "move" => MoveAction,
            "revise" => ReviseAction,
            "release" => ReleaseAction,
            _ => throw ToolException.InvalidArgument($"Unknown action '{action}'.", "Use rename, copy, replace, move, revise or release."),
        };
        string top = Existing(context, path, "path");
        if (files is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} needs 'files' (files of the set of 'path').");
        }

        if (code != ReleaseAction && (to is null || to.Length != files.Length))
        {
            throw ToolException.InvalidArgument($"{action} needs 'to': one {(code == ReplaceAction ? "existing file" : code == MoveAction ? "folder or new path" : "new name or path")} for each file, in the same order.");
        }

        string[] sources = [.. files.Select(f => Existing(context, Resolve(Path.GetDirectoryName(top)!, f), "files"))];
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
        {
            throw ToolException.InvalidArgument("'files' names the same file twice.");
        }

        if (sources.FirstOrDefault(s => Same(s, top)) is { } self)
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(self)}' is the top document of the set: Design Manager acts on the files it refers to.",
                code == ReleaseAction
                    ? "Release it with action 'status' and status 'released'."
                    : "Give as 'path' a document that uses it (e.g. its drawing), or save a copy with se_documents save_as.");
        }

        string[] targets = code == ReleaseAction ? sources : [.. sources.Select((s, i) => Target(context, code, s, to![i]))];
        if (code != ReleaseAction && targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
        {
            throw ToolException.InvalidArgument("'to' gives the same file twice.");
        }

        return WithDesignManager(manager =>
        {
            (List<string> before, List<(string Parent, string Child)> links) = SetOf(manager, top);
            if (sources.FirstOrDefault(s => !before.Any(b => Same(b, s))) is { } stranger)
            {
                throw ToolException.InvalidArgument($"'{Path.GetFileName(stranger)}' is not in the set of '{Path.GetFileName(top)}'.",
                    "List the set with action 'links'; give as 'path' a document that uses the file.");
            }

            // The documents of the set that use the files are rewritten to use the new ones (release writes only the status
            // of the files): the policy covers them as well as the files given.
            string[] parents = code == ReleaseAction ? []
                : [.. links.Where(l => sources.Any(s => Same(s, l.Child))).Select(l => l.Parent).Distinct(StringComparer.OrdinalIgnoreCase)];
            if (parents.FirstOrDefault(p => !context.Paths.IsAllowed(p)) is { } beyond)
            {
                throw new ToolException("path_not_allowed", $"'{beyond}' uses one of 'files' and Design Manager would rewrite it, but it is outside the allowed folders.",
                    "Add its folder to allowedRoots in policy.json, or act on a set inside them.");
            }

            List<(string User, string Source)> outside = code is RenameAction or MoveAction
                ? UsersOutside(context, manager, sources, [top, .. before], folders, subfolders, top)
                : [];
            string[] rewritten = [.. parents, .. outside.Select(o => o.User).Distinct(StringComparer.OrdinalIgnoreCase)];
            RequireClosed(context, [top, .. before.Where(File.Exists), .. rewritten],
                [.. rewritten, .. (code is RenameAction or MoveAction or ReleaseAction ? sources : [])]);
            if (rewritten.Select(d => (Document: d, Status: StatusOf(Open(manager, d)))).FirstOrDefault(d => LockedStatuses.Contains(d.Status)) is { Document: not null } locked)
            {
                throw new ToolException("read_only", $"'{Path.GetFileName(locked.Document)}' uses one of 'files' and is {locked.Status}: Solid Edge keeps it read-only, so its links cannot change.",
                    "Revise it (action revise) to get a revision to work on, or leave its folder out of 'folders'.");
            }

            int[] revisions = code == ReviseAction ? [.. sources.Select(s => Revision(manager, s))] : [];
            Arm(ActionLimit);
            Late.Call(manager, "OpenFileInDesignManager", top);
            Late.Call(manager, "SetAction", sources.Length, sources.Cast<object>().ToArray(), Enumerable.Repeat((object)code, sources.Length).ToArray(),
                targets.Cast<object>().ToArray(), Type.Missing);
            Arm(ActionLimit);
            Late.Call(manager, "PerformAction");
            List<string> after = AllLinks(manager, top);

            var problems = new List<string>();
            var done = new List<string>();
            var changed = new List<object>();
            for (int i = 0; i < sources.Length; i++)
            {
                string source = sources[i];
                string target = code == ReviseAction ? Revised(after, before, targets[i]) ?? targets[i] : targets[i];
                int failures = problems.Count;
                switch (code)
                {
                    case RenameAction or MoveAction when File.Exists(source) || !File.Exists(target):
                        problems.Add($"{Path.GetFileName(source)} was not {(code == RenameAction ? "renamed" : "moved")}");
                        break;
                    case CopyAction or ReviseAction when !File.Exists(target):
                        problems.Add($"{Path.GetFileName(target)} was not written");
                        break;
                    case ReleaseAction when StatusOf(Open(manager, source)) != "released":
                        problems.Add($"{Path.GetFileName(source)} was not released");
                        break;
                }

                if (code != ReleaseAction && (!after.Any(a => Same(a, target)) || after.Any(a => Same(a, source))))
                {
                    problems.Add($"the set does not use {Path.GetFileName(target)} instead of {Path.GetFileName(source)}");
                }

                if (problems.Count == failures)
                {
                    done.Add(code == ReleaseAction ? $"{Path.GetFileName(source)} released" : $"{Path.GetFileName(source)} to {target}");
                }

                object? revision = code == ReviseAction && File.Exists(target) ? Text(Open(manager, target), "Revision") : null;
                changed.Add(code == ReleaseAction
                    ? new { file = source, status = "released" }
                    : new { from = source, to = target, revision, previousRevision = code == ReviseAction ? (object?)revisions[i] : null });
            }

            if (problems.Count > 0)
            {
                // Design Manager cannot undo: say what it did.
                throw new ToolException("design_manager_failed",
                    $"Design Manager did not finish the {action}: {string.Join("; ", problems)}.{(done.Count > 0 ? $" Done: {string.Join("; ", done)}." : "")}",
                    "Check the set with action 'links' and the files in their folders.");
            }

            var users = new List<object>();
            int unlinked = 0;
            foreach ((string user, string source) in outside)
            {
                string target = targets[Array.FindIndex(sources, s => Same(s, source))];
                try
                {
                    Relink(manager, user, source, target);
                    users.Add(new { document = user, from = source, to = target, error = (string?)null });
                }
                catch (Exception ex) when (ex is ToolException || ComErrors.Unwrap(ex) is COMException)
                {
                    // The files are already renamed or moved: the other users are still relinked and this one is reported.
                    users.Add(new { document = user, from = source, to = target, error = (string?)ex.Message.Trim() });
                    unlinked++;
                }
            }

            return new
            {
                action,
                document = top,
                changed,
                outsideUsers = code is RenameAction or MoveAction ? users : null,
                note = unlinked > 0 ? $"{unlinked} document(s) outside the set still use the old file: point them at the new one with action 'relink'." : null,
                links = after,
            };
        });
    }

    /// <summary>
    /// Points the links of closed documents at other files (Design Manager's Replace and Redefine Links): one document
    /// (<paramref name="path"/>) or every document of <paramref name="folders"/> that refers to the files. A folder in
    /// <paramref name="files"/> with a folder in <paramref name="to"/> moves every link under the first to the same name under
    /// the second, where that file exists.
    /// </summary>
    public static object Relink(SeContext context, string? path, string[]? folders, bool subfolders, string[]? files, string[]? to)
    {
        if (files is not { Length: > 0 } || to is null || to.Length != files.Length)
        {
            throw ToolException.InvalidArgument("relink needs 'files' (the linked files, or a folder) and 'to' (the new file, or folder, for each).");
        }

        if (string.IsNullOrWhiteSpace(path) && folders is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("relink needs 'path' (one document) or 'folders' (the documents to look through).");
        }

        List<string> documents = string.IsNullOrWhiteSpace(path) ? Documents(context, folders, null, subfolders) : [Existing(context, path, "path")];

        var pairs = new List<(string From, string To, bool Folder)>();
        for (int i = 0; i < files.Length; i++)
        {
            string newPath = context.Paths.Check(to[i], "to");
            if (Directory.Exists(newPath))
            {
                pairs.Add((files[i].TrimEnd('\\', '/'), newPath.TrimEnd('\\', '/'), true));
            }
            else if (File.Exists(newPath))
            {
                pairs.Add((files[i], newPath, false));
            }
            else
            {
                throw ToolException.NotFound($"'{newPath}' does not exist.", "'to' takes existing files (or a folder holding the new files).");
            }
        }

        return WithDesignManager(manager =>
        {
            var plan = new List<(string Document, string From, string To)>();
            var skipped = new List<object>();
            foreach (string document in documents)
            {
                foreach (object link in LinkedDocuments(manager, document))
                {
                    string linked = FullName(link);
                    foreach ((string from, string target, bool folder) in pairs)
                    {
                        string? replacement = folder
                            ? linked.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Path.Combine(target, linked[(from.Length + 1)..]) : null
                            : Matches(linked, from) ? target : null;
                        if (replacement is null || Same(replacement, linked))
                        {
                            continue;
                        }

                        if (File.Exists(replacement))
                        {
                            plan.Add((document, linked, replacement));
                        }
                        else
                        {
                            skipped.Add(new { document, link = linked, missing = replacement });
                        }
                    }
                }
            }

            string[] changing = [.. plan.Select(p => p.Document).Distinct(StringComparer.OrdinalIgnoreCase)];
            RequireClosed(context, changing, changing);
            if (changing.Select(d => (Document: d, Status: StatusOf(Open(manager, d)))).FirstOrDefault(d => LockedStatuses.Contains(d.Status)) is { Document: not null } locked)
            {
                throw new ToolException("read_only", $"'{Path.GetFileName(locked.Document)}' is {locked.Status}: Solid Edge keeps it read-only, so its links cannot change.",
                    "Revise it (action revise) to get a revision to work on, or leave it out.");
            }

            foreach (var group in plan.GroupBy(p => p.Document, StringComparer.OrdinalIgnoreCase))
            {
                foreach ((string document, string from, string target) in group)
                {
                    Relink(manager, document, from, target);
                }
            }

            return new
            {
                scanned = documents.Count,
                count = plan.Count,
                changed = plan.Select(p => new { document = p.Document, from = p.From, to = p.To }),
                skipped = skipped.Count > 0 ? skipped : null,
            };
        });
    }

    /// <summary>The documents of the folders with links to files that do not exist (Design Manager's Find Broken Links).</summary>
    public static object BrokenLinks(SeContext context, string[]? folders, bool subfolders)
    {
        List<string> documents = Documents(context, folders, null, subfolders);
        return WithDesignManager(manager =>
        {
            var broken = new List<object>();
            int links = 0;
            foreach (string document in documents)
            {
                List<string> linked = [.. LinkedDocuments(manager, document).Select(FullName)];
                links += linked.Count;
                List<string> missing = [.. linked.Where(l => !File.Exists(l))];
                if (missing.Count > 0)
                {
                    broken.Add(new { document, missing });
                }
            }

            return new { scanned = documents.Count, links, count = broken.Count, documents = broken };
        });
    }

    /// <summary>
    /// Documents that are copies of one another (Design Manager's Find Duplicates): the same document ID, which a copy made
    /// outside Solid Edge keeps and which makes Solid Edge take one for the other, and the same file name in different folders.
    /// </summary>
    public static object Duplicates(SeContext context, string[]? folders, bool subfolders)
    {
        List<string> documents = Documents(context, folders, null, subfolders);
        var ids = new List<(string File, string? Id)>();
        foreach (string document in documents)
        {
            string? id;
            try
            {
                id = FilePropertyService.ReadText(document, "ExtendedSummaryInformation", "DocumentID");
            }
            catch (ToolException)
            {
                id = null;
            }

            ids.Add((document, string.IsNullOrWhiteSpace(id) ? null : id));
        }

        var sameId = ids.Where(d => d.Id is not null).GroupBy(d => d.Id!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => new { documentId = g.Key, files = g.Select(d => d.File) }).ToList();
        var sameName = documents.GroupBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => new { name = g.Key, files = g.ToList() }).ToList();
        return new
        {
            scanned = documents.Count,
            sameDocumentId = sameId,
            sameName,
            note = sameId.Count > 0 ? "Files with the same document ID were copied outside Solid Edge; give each copy its own identity with se_design_manager copy, or Save As in Solid Edge." : null,
        };
    }

    private sealed record LinkRow(string File, string Parent, int Level, bool Exists, string? Status, string? Revision, string? DocumentNumber);

    private static T WithDesignManager<T>(Func<object, T> work)
    {
        Type type = Type.GetTypeFromProgID("DesignManager.Application")
            ?? throw new ToolException("not_available", "Design Manager is not installed or its automation server is not registered.",
                "Install Design Manager with Solid Edge.");
        int[] running = [.. Process.GetProcessesByName("DesMgr").Select(p => p.Id)];
        object manager = Activator.CreateInstance(type)!;
        Process? own = Process.GetProcessesByName("DesMgr").FirstOrDefault(p => !running.Contains(p.Id));
        int stopped = 0;
        using var timer = new Timer(_ =>
        {
            Interlocked.Exchange(ref stopped, 1);
            Kill(own);
        }, null, StepLimit, Timeout.InfiniteTimeSpan);
        watchdog = timer;
        try
        {
            Late.Set(manager, "DisplayAlerts", 0);
            return work(manager);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com)
        {
            throw Volatile.Read(ref stopped) == 1
                ? new ToolException("design_manager_failed", "Design Manager stopped answering (a dialog box of its own may be waiting) and was closed: what it was doing may be partly done.",
                    "Check the set with action 'links' and the files in their folders.")
                : new ToolException("design_manager_failed", $"Design Manager failed: {com.Message.Trim()} (0x{com.HResult:X8})",
                    "Check that the files exist, are Solid Edge documents and are not open in another program.");
        }
        finally
        {
            watchdog = null;
            try
            {
                Late.Call(manager, "Quit");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                Kill(own);
            }

            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended on its own.
        }
    }

    /// <summary>Restarts the watchdog: the next step has <paramref name="limit"/> to answer.</summary>
    private static void Arm(TimeSpan limit) => watchdog?.Change(limit, Timeout.InfiniteTimeSpan);

    private static object Open(object manager, string file)
    {
        Arm(StepLimit);
        return Late.Call(manager, "Open", file) ?? throw new ToolException("design_manager_failed", $"Design Manager could not open '{file}'.", "Check that it is a Solid Edge document.");
    }

    private static List<object> LinkedDocuments(object manager, string file) =>
        Late.Get(Open(manager, file), "LinkedDocuments") is { } links ? [.. Late.Items(links)] : [];

    private static string FullName(object document) => Late.Get(document, "FullName") as string ?? "";

    private static string? Text(object document, string property) => Late.TryGet(document, property) is string { Length: > 0 } text ? text : null;

    private static string? StatusOf(object document) =>
        Late.TryGet(document, "Status") is { } value && Convert.ToInt32(value, CultureInfo.InvariantCulture) is int code && code >= 0
            ? FilePropertyService.StatusNames.ElementAtOrDefault(code)
            : null;

    private static int Revision(object manager, string file) =>
        int.TryParse(Text(Open(manager, file), "Revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int revision) ? revision : 0;

    /// <summary>Every file the set of a top document refers to, at every level.</summary>
    private static List<string> AllLinks(object manager, string top) => SetOf(manager, top).Files;

    /// <summary>Every file the set of a top document refers to, at every level, and every link (the document and the file it uses).</summary>
    private static (List<string> Files, List<(string Parent, string Child)> Links) SetOf(object manager, string top)
    {
        var all = new List<string>();
        var links = new List<(string, string)>();
        var queue = new Queue<string>([top]);
        while (queue.Count > 0 && all.Count < MaxFiles)
        {
            string parent = queue.Dequeue();
            foreach (string child in LinkedDocuments(manager, parent).Select(FullName))
            {
                links.Add((parent, child));
                if (!all.Any(a => Same(a, child)))
                {
                    all.Add(child);
                    if (File.Exists(child))
                    {
                        queue.Enqueue(child);
                    }
                }
            }
        }

        return (all, links);
    }

    /// <summary>Documents outside the set, in the folders, that refer to one of the files.</summary>
    private static List<(string User, string Source)> UsersOutside(SeContext context, object manager, string[] sources, List<string> set,
        string[]? folders, bool subfolders, string top)
    {
        IEnumerable<string> defaults = [Path.GetDirectoryName(top)!, .. sources.Select(s => Path.GetDirectoryName(s)!)];
        var users = new List<(string, string)>();
        foreach (string document in Documents(context, folders, defaults, subfolders))
        {
            if (set.Any(s => Same(s, document)) || sources.Any(s => Same(s, document)))
            {
                continue;
            }

            foreach (string linked in LinkedDocuments(manager, document).Select(FullName))
            {
                if (sources.FirstOrDefault(s => Same(s, linked)) is { } source)
                {
                    users.Add((document, source));
                }
            }
        }

        return users;
    }

    /// <summary>Points the link of a closed document to <paramref name="from"/> at <paramref name="to"/>, and checks it.</summary>
    private static void Relink(object manager, string document, string from, string to)
    {
        object opened = Open(manager, document);
        object link = (Late.Get(opened, "LinkedDocuments") is { } links ? Late.Items(links) : []).FirstOrDefault(l => Same(FullName(l), from))
            ?? throw new ToolException("design_manager_failed", $"'{Path.GetFileName(document)}' has no link to '{from}'.", "List its links with action 'links'.");
        Late.Call(link, "SetPath", to);
        Late.Call(opened, "SaveAllLinks");
        List<string> now = [.. LinkedDocuments(manager, document).Select(FullName)];
        if (!now.Any(l => Same(l, to)) || now.Any(l => Same(l, from)))
        {
            throw new ToolException("design_manager_failed", $"'{Path.GetFileName(document)}' still refers to '{from}' instead of '{to}'.",
                "Check that the document is not read-only (a released document cannot be changed).");
        }
    }

    /// <summary>The Solid Edge documents of the folders (allowed folders only).</summary>
    private static List<string> Documents(SeContext context, string[]? folders, IEnumerable<string>? defaults, bool subfolders)
    {
        string[] roots = folders is { Length: > 0 } ? [.. folders.Select(f => context.Paths.Check(f, "folders"))]
            : defaults is not null ? [.. defaults.Distinct(StringComparer.OrdinalIgnoreCase)]
            : throw ToolException.InvalidArgument("'folders' is required: the folders to look through.");
        if (roots.FirstOrDefault(r => !Directory.Exists(r)) is { } missing)
        {
            throw ToolException.NotFound($"The folder does not exist: '{missing}'.");
        }

        var option = subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return [.. roots.SelectMany(r => DocumentPatterns.SelectMany(p => Directory.EnumerateFiles(r, p, option)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles)];
    }

    /// <summary>
    /// Design Manager works on closed files: none of <paramref name="files"/> may be open in Solid Edge, which keeps its own
    /// copy and its own links, and the ones <paramref name="written"/> must be writable (the others, such as a read-only
    /// library, are only read).
    /// </summary>
    private static void RequireClosed(SeContext context, IEnumerable<string> files, IEnumerable<string> written)
    {
        foreach (string file in files.Concat(written).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DocumentService.FindOpen(context, file) is { } open)
            {
                throw new ToolException("document_open", $"'{open.Name}' is open in Solid Edge.",
                    "Close the documents of the set (se_documents close): Design Manager works on closed files.");
            }
        }

        foreach (string file in written.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new ToolException("document_open", $"'{Path.GetFileName(file)}' is in use by another program.", "Close it there first.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new ToolException("read_only", $"'{file}' is read-only.", "Clear its read-only attribute, or leave it out.");
            }
        }
    }

    /// <summary>The new file for a source: a name or path (rename, copy, revise), an existing file (replace) or a folder (move).</summary>
    private static string Target(SeContext context, int code, string source, string to)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            throw ToolException.InvalidArgument("'to' has an empty entry.");
        }

        string folder = Path.GetDirectoryName(source)!;
        string target = code == MoveAction && (Directory.Exists(Resolve(folder, to)) || !IsDocument(to))
            ? Path.Combine(Resolve(folder, to), Path.GetFileName(source))
            : Resolve(folder, to, Path.GetExtension(source));
        target = context.Paths.Check(target, "to");
        if (code != ReplaceAction && !Same(Path.GetExtension(target), Path.GetExtension(source)))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(target)}' is another type of document: the new file of '{Path.GetFileName(source)}' keeps its type.",
                $"Give a name without extension or ending in {Path.GetExtension(source)}.");
        }

        if (code == ReplaceAction)
        {
            return File.Exists(target) ? target : throw ToolException.NotFound($"The replacement '{target}' does not exist.", "'to' takes existing files for replace.");
        }

        if (code == ReviseAction)
        {
            // The revision is written as "name-revision": the name of the file itself is the usual one.
            return target;
        }

        if (Same(target, source))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(source)}' already has that name.");
        }

        if (File.Exists(target))
        {
            throw new ToolException("file_exists", $"'{target}' already exists.", "Give a new name.");
        }

        if (code == MoveAction)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        }

        return target;
    }

    /// <summary>The revision written for <paramref name="requested"/>: Design Manager adds "-revision" to the name given.</summary>
    private static string? Revised(List<string> after, List<string> before, string requested)
    {
        var name = new Regex($"^{Regex.Escape(Path.GetFileNameWithoutExtension(requested))}-[^-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return after.FirstOrDefault(a => !before.Any(b => Same(a, b))
            && Same(Path.GetDirectoryName(a), Path.GetDirectoryName(requested))
            && name.IsMatch(Path.GetFileNameWithoutExtension(a)));
    }

    private static bool Matches(string linked, string from) =>
        Path.IsPathRooted(from) ? Same(linked, from) : Same(Path.GetFileName(linked), from);

    private static string Existing(SeContext context, string? path, string argument)
    {
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument($"'{argument}' is required."), argument);
        return File.Exists(file) ? file : throw ToolException.NotFound($"The file does not exist: '{file}'.");
    }

    /// <summary>
    /// A full path as given, or a name in <paramref name="folder"/>, with <paramref name="extension"/> added when it does not
    /// end in the extension of a Solid Edge document ("bracket_v1.1" is "bracket_v1.1.par").
    /// </summary>
    private static string Resolve(string folder, string name, string? extension = null)
    {
        string full = Path.IsPathRooted(name) ? name : Path.Combine(folder, name);
        return extension is not null && !IsDocument(full) ? full + extension : full;
    }

    private static bool IsDocument(string path) => DocumentExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
