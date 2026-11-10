// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using DocumentStatus = SolidEdgeFramework.DocumentStatus;

namespace SolidEdgeMcp.SolidEdge.Services;

public sealed record DocumentInfo(
    string Handle,
    string Name,
    string? Path,
    string Kind,
    bool Dirty,
    bool ReadOnly,
    bool Active,
    string? Member = null);

/// <summary>Documents: list, create, open, save and close, always through <see cref="PathGuard"/>.</summary>
public static class DocumentService
{
    private static readonly Dictionary<string, string[]> NativeExtensions = new()
    {
        ["part"] = [".par"],
        ["sheet_metal"] = [".psm"],
        ["assembly"] = [".asm"],
        ["draft"] = [".dft"],
        ["weldment"] = [".pwd"],
        ["weldment_assembly"] = [".asm"],
    };

    /// <summary>
    /// Templates of this installation: the default one of each kind (Solid Edge's options) and the template files in its
    /// template folders, so a template is chosen from what this Solid Edge has instead of a remembered name.
    /// </summary>
    public static object Templates(SeContext context, string? kind)
    {
        string[] kinds = kind is null ? [.. DocumentKinds.CreatableKinds] : [kind];
        var defaults = new Dictionary<string, string?>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in kinds)
        {
            DocumentTypeConstants type = DocumentKinds.ForNew(k).Type;
            string? path = type == DocumentTypeConstants.igWeldmentDocument ? WeldmentAssemblyTemplate(context) ?? DefaultTemplate(context, type) : DefaultTemplate(context, type);
            defaults[k] = path is not null && File.Exists(path) ? path : null;
            foreach (string folder in TemplateFolders(context, type))
            {
                folders.Add(folder);
            }
        }

        string[] extensions = [.. kinds.SelectMany(k => NativeExtensions.GetValueOrDefault(k) ?? []).Append(".asm").Distinct()];
        string[] files = [.. folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(context.MaxListItems)];
        return new
        {
            defaults,
            folders = folders.ToArray(),
            count = files.Length,
            templates = files,
            note = "Pass a template path to se_documents new ('template'); without one, the default of the kind is used.",
        };
    }

    /// <summary>The template given (checked) or the installation's default template of the type.</summary>
    internal static string TemplateFor(SeContext context, DocumentTypeConstants type, string? template) =>
        !string.IsNullOrWhiteSpace(template) ? CheckTemplate(context, template, type)
        : DefaultTemplate(context, type) is { } byDefault && File.Exists(byDefault) ? byDefault
        : throw new ToolException("template_missing", $"Solid Edge has no default {DocumentKinds.ToKind(type).Replace('_', ' ')} template.",
            "Pass 'template' (se_documents templates lists them).");

    private static string? DefaultTemplate(SeContext context, DocumentTypeConstants type)
    {
        try
        {
            return context.Application.GetDefaultTemplatePath(type);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static object List(SeContext context)
    {
        Documents documents = context.Application.Documents;
        SolidEdgeDocument? active = TryActive(context);
        var list = new List<DocumentInfo>();
        for (int i = 1; i <= documents.Count && list.Count < context.MaxListItems; i++)
        {
            if (documents.Item(i) is SolidEdgeDocument document)
            {
                list.Add(Describe(context, document, active));
            }
        }

        return new { count = documents.Count, documents = list };
    }

    public static DocumentInfo Info(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        return Describe(context, document, TryActive(context));
    }

    public static DocumentInfo Activate(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        document.Activate();
        return Describe(context, document, document);
    }

    public static DocumentInfo New(SeContext context, string kind, string? template)
    {
        (string progId, DocumentTypeConstants type) = DocumentKinds.ForNew(kind);
        object created;
        if (!string.IsNullOrWhiteSpace(template))
        {
            created = context.Application.Documents.Add(progId, CheckTemplate(context, template, type));
        }
        else if (type == DocumentTypeConstants.igWeldmentDocument && WeldmentAssemblyTemplate(context) is { } weldment)
        {
            // Verified in Solid Edge 2026: it has no .pwd template (the default template is only the folder and
            // Add fails with STG_E_PATHNOTFOUND); the weldment is an assembly created with "<units> weldment.asm".
            created = context.Application.Documents.Add("SolidEdge.AssemblyDocument", weldment);
        }
        else
        {
            created = context.Application.Documents.Add(progId);
        }
        var document = (SolidEdgeDocument)created;
        return Describe(context, document, document);
    }

    public static DocumentInfo Open(SeContext context, string path)
    {
        string checkedPath = context.Paths.Check(path);
        if (!File.Exists(checkedPath))
        {
            throw ToolException.NotFound($"The file does not exist: '{checkedPath}'.");
        }

        // Documents.Open of a file of another CAD system fails with E_FAIL (verified in Solid Edge 2026 with a STEP file):
        // Solid Edge translates it into a new document.
        if (ForeignFormats.ContainsKey(Path.GetExtension(checkedPath)))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(checkedPath)}' is a file of another CAD system: Solid Edge translates it into a new document instead of opening it.",
                "Use action 'import' with this path (optionally 'kind' and 'template').");
        }

        SolidEdgeDocument? already = FindOpen(context, checkedPath);
        if (already is not null)
        {
            already.Activate();
            return Describe(context, already, already);
        }

        var document = (SolidEdgeDocument)context.Application.Documents.Open(checkedPath);
        return Describe(context, document, document);
    }

    /// <summary>Document kind each format of other CAD systems is translated into when no kind is given.</summary>
    private static readonly Dictionary<string, string> ForeignFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stp"] = "part", [".step"] = "part", [".igs"] = "part", [".iges"] = "part", [".x_t"] = "part", [".x_b"] = "part",
        [".jt"] = "part", [".sat"] = "part", [".stl"] = "part", [".obj"] = "part", [".3mf"] = "part",
        [".catpart"] = "part", [".catproduct"] = "assembly", [".model"] = "part",
        [".dxf"] = "draft", [".dwg"] = "draft",
    };

    /// <summary>
    /// Opens a file of another CAD system as a new, unsaved Solid Edge document made from a template of the given kind
    /// (Documents.OpenWithTemplate, as File > Open with a template chosen in the import options).
    /// </summary>
    public static object Import(SeContext context, string path, string? kind, string? template)
    {
        string source = context.Paths.Check(path);
        if (!File.Exists(source))
        {
            throw ToolException.NotFound($"The file does not exist: '{source}'.");
        }

        string extension = System.IO.Path.GetExtension(source);
        if (NativeExtensions.Values.Any(e => e.Contains(extension, StringComparer.OrdinalIgnoreCase)))
        {
            throw ToolException.InvalidArgument($"'{System.IO.Path.GetFileName(source)}' is already a Solid Edge document.", "Use action 'open'.");
        }

        string target = kind ?? (ForeignFormats.TryGetValue(extension, out string? byFormat) ? byFormat
            : throw ToolException.InvalidArgument($"'{extension}' is not a CAD format this server translates.",
                $"Use one of {string.Join(", ", ForeignFormats.Keys)}, or pass 'kind' to try another one."));
        (_, DocumentTypeConstants type) = DocumentKinds.ForNew(target);
        string templatePath = !string.IsNullOrWhiteSpace(template)
            ? CheckTemplate(context, template, type)
            : DefaultTemplate(context, type) is { } byDefault && File.Exists(byDefault) ? byDefault
            : throw new ToolException("template_missing", $"Solid Edge has no default {target.Replace('_', ' ')} template.",
                "Pass 'template' (se_documents templates lists them).");
        // STL carries no unit: it is read in mm, so the translator (which reads the unit of its options, inches as installed
        // and without an options file) is given a copy scaled to that unit.
        string opened = source;
        string? scratch = null;
        double? stlUnit = extension.Equals(".stl", StringComparison.OrdinalIgnoreCase)
            ? ExportService.StlImportUnit(context)
            : null;
        if (stlUnit is { } unit && unit != 1)
        {
            scratch = System.IO.Path.Combine(context.Paths.ExchangeFolder, "import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            opened = System.IO.Path.Combine(scratch, System.IO.Path.GetFileName(source));
            try
            {
                StlFile.Scale(source, opened, 1 / unit);
            }
            catch (InvalidDataException ex)
            {
                Directory.Delete(scratch, true);
                throw ToolException.InvalidArgument($"'{System.IO.Path.GetFileName(source)}': {ex.Message}");
            }
        }

        SolidEdgeDocument document;
        try
        {
            // Verified in Solid Edge 2026 with STEP, IGES, Parasolid, STL and DXF files: no dialog, a new unsaved document.
            document = (SolidEdgeDocument)context.Application.Documents.OpenWithTemplate(opened, templatePath);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex) && extension.ToLowerInvariant() is ".dxf" or ".dwg")
        {
            // Verified with Solid Edge 2026 Foundation: "Property name is invalid" with every template and kind, also for a DXF
            // that Solid Edge wrote, and Documents.Open gives E_FAIL; Application.CreateSEDraftDocFromDXFAuto writes the
            // template without the file's elements. That installation has no Preferences\Translators\seacad.ini, the DXF/DWG
            // translator settings that Community Edition had when it opened the same files, and ResetConfigFile writes none.
            string? install = SolidEdgeSettings.InstallFolder(context.Application);
            bool noSettings = install is not null && !File.Exists(System.IO.Path.Combine(install, "Preferences", "Translators", "seacad.ini"));
            throw new ToolException("import_failed", $"Solid Edge could not open '{System.IO.Path.GetFileName(source)}': {ex.Message.Trim()}",
                noSettings
                    ? "This installation has no DXF/DWG translator settings (Preferences\\Translators\\seacad.ini), and there Solid Edge reads DXF and DWG through no API route (as Solid Edge 2026 Foundation). Import STEP, IGES or Parasolid, or open the file in Solid Edge by hand."
                    : "Open the file in Solid Edge by hand to see its message, or import STEP, IGES or Parasolid.", ex);
        }
        finally
        {
            if (scratch is not null)
            {
                try
                {
                    Directory.Delete(scratch, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left in the work folder.
                }
            }
        }

        double? volume = document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument ? ModelDocument.From(document).Volume() : null;
        Dictionary<string, int>? elements = document is SolidEdgeDraft.DraftDocument draft ? DrawingService.SheetElements(draft.ActiveSheet) : null;
        int? occurrences = document is SolidEdgeAssembly.AssemblyDocument assembly ? assembly.Occurrences.Count : null;
        if (volume is <= 0 || elements?.GetValueOrDefault("total") == 0 || occurrences == 0)
        {
            TryClose(document);
            throw new ToolException("import_empty", $"Solid Edge translated no geometry from '{System.IO.Path.GetFileName(source)}', so the empty document was closed.",
                "Check the file in the program that wrote it, or pass another 'kind' or 'template'.");
        }

        return new
        {
            document = Describe(context, document, document),
            source,
            template = templatePath,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
            occurrences,
            elements,
            note = "Not saved yet: save_as writes it as a Solid Edge file."
                + (stlUnit is null ? "" : stlUnit == 1 ? " STL files carry no unit: it was read in mm."
                    : " STL files carry no unit: it was read in mm (the server converted it for Solid Edge's STL translator, which reads another unit)."),
        };
    }

    public static DocumentInfo Save(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string? path = PathOf(document);
        if (path is null)
        {
            throw new ToolException("never_saved", $"'{document.Name}' has never been saved.",
                "Use action 'save_as' with a path in an allowed folder.");
        }

        context.Paths.Check(path, "document path");
        RequireWritable(document);
        document.Save();
        return Describe(context, document, TryActive(context));
    }

    public static DocumentInfo SaveAs(SeContext context, string? handle, string path, bool overwrite, bool copy)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string target = context.Paths.Check(path);
        string kind = DocumentKinds.ToKind(document.Type);
        string extension = System.IO.Path.GetExtension(target).ToLowerInvariant();
        if (NativeExtensions.TryGetValue(kind, out string[]? allowed) && !allowed.Contains(extension))
        {
            throw ToolException.InvalidArgument(
                $"A {kind.Replace('_', ' ')} document must be saved with extension {string.Join(" or ", allowed)}; got '{extension}'.",
                "To write another format (STEP, PDF, DXF, STL...) use se_export.");
        }

        bool existed = File.Exists(target);
        if (existed && !overwrite)
        {
            throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        try
        {
            if (copy)
            {
                document.SaveCopyAs(target);
            }
            else
            {
                document.SaveAs(target);
            }
        }
        catch (COMException ex) when (existed && !ComErrors.IsGone(ex))
        {
            // Seen once: after some assemblies were closed, Solid Edge kept refusing to replace the files of their parts
            // (DISP_E_EXCEPTION) for as long as it ran (over 12 minutes), with no document open and the file not locked.
            throw new ToolException("file_in_use", $"Solid Edge did not replace '{target}' ({ex.Message.Trim()}): it may still hold that file for a document closed earlier.",
                "Save under another name, or close Solid Edge and open it again before replacing the file.");
        }

        if (!copy && document.Dirty)
        {
            // Seen with a draft and an assembly of a model that was open (not reproduced on demand): they came back
            // modified right after SaveAs and a second save left them clean, as after Save As in the interface.
            document.Save();
        }

        if (!File.Exists(target))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{target}'.");
        }

        return Describe(context, document, TryActive(context));
    }

    /// <summary>
    /// Save As Mirrored Part: a new part file that is the mirror image of this part about one of its reference planes,
    /// opened in Solid Edge. Verified in Solid Edge 2026 (Models.SaveAsMirrorPart): a 100 x 60 x 20 block (x 0..100) mirrored
    /// about plane 2 (YZ) spans x -100..0, about plane 3 (XZ) y -60..0, with the same volume; linked, the new part holds a
    /// link to this one (4 features); not linked, the features are copied (5).
    /// </summary>
    public static object SaveMirror(SeContext context, string? handle, string path, string plane, bool link, bool overwrite)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        if (document is not SolidEdgePart.PartDocument)
        {
            throw new ToolException("not_supported", "save_mirror works on part documents (Save As Mirrored Part).");
        }

        string target = context.Paths.Check(path);
        if (!string.Equals(System.IO.Path.GetExtension(target), ".par", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("The mirrored part must be saved as a .par file.");
        }

        if (FindOpen(context, target) is not null)
        {
            throw new ToolException("file_in_use", $"'{target}' is open in Solid Edge.", "Close it first, or choose another path.");
        }

        if (File.Exists(target))
        {
            if (!overwrite)
            {
                throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
            }

            File.Delete(target);
        }

        ModelDocument model = ModelDocument.From(document);
        SolidEdgePart.RefPlane mirrorPlane = model.ResolvePlane(context, plane);
        double volume = model.Volume();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        try
        {
            model.Models.SaveAsMirrorPart(target, mirrorPlane, link, out _, out _);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("save_failed", $"Solid Edge did not save the mirrored part: {ex.Message.Trim()}",
                "The part needs a solid body; use one of its reference planes (se_ref_plane list).", ex);
        }

        if (!File.Exists(target))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{target}'.");
        }

        SolidEdgeDocument mirrored = FindOpen(context, target) ?? (SolidEdgeDocument)context.Application.Documents.Open(target);
        double mirroredVolume = ModelDocument.From(mirrored).Volume();
        return new
        {
            document = Describe(context, mirrored, TryActive(context)),
            source = context.DocumentHandle(document),
            linked = link,
            volume = Geo.Mm3(mirroredVolume),
            sourceVolume = Geo.Mm3(volume),
        };
    }

    public static object Close(SeContext context, string? handle, bool? saveChanges)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string name = IsFamilyMember(document.FullName) && PathOf(document) is { } file ? System.IO.Path.GetFileName(file) : document.Name;
        bool dirty = document.Dirty;
        if (dirty && saveChanges is null)
        {
            throw new ToolException("unsaved_changes", $"'{name}' has unsaved changes.",
                "Pass saveChanges=true to save them or saveChanges=false to discard them.");
        }

        bool save = dirty && saveChanges == true;
        string? note = saveChanges == true && !dirty
            ? PathOf(document) is null ? "It had no changes and had never been saved: nothing was written." : "It had no unsaved changes: its file was already up to date."
            : null;
        if (save)
        {
            string path = PathOf(document) ?? throw new ToolException("never_saved",
                $"'{name}' has never been saved.", "Use action 'save_as' first, then close.");
            context.Paths.Check(path, "document path");
        }

        string documentHandle = context.DocumentHandle(document);
        List<int> windows = WindowHandles(document);
        document.Close(save);
        CloseLeftWindows(context.Application, windows);
        context.Handles.InvalidateScope(documentHandle, "its document was closed");
        context.Handles.Remove(documentHandle, "the document was closed");

        // Parts loaded by an assembly or a drawing are unloaded with it: their handles are released too.
        Documents documents = context.Application.Documents;
        HashSet<object> open = AfterClose(() =>
        {
            var listed = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (int i = 1, count = documents.Count; i <= count; i++)
            {
                listed.Add(documents.Item(i));
            }

            return listed;
        });
        foreach ((string id, object value) in context.Handles.OfKind("doc"))
        {
            if (!open.Contains(value))
            {
                context.Handles.InvalidateScope(id, "its document was closed");
                context.Handles.Remove(id, "the document was closed");
            }
        }

        // The wrappers of the closed document's objects are released now. Left to the garbage collector, a wrapper of a
        // dead object could be handed out again for a new object that Solid Edge creates at the same address, with its old
        // interfaces (verified: an occurrence of a reopened assembly failed with RPC_E_DISCONNECTED).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Solid Edge can end up with no active document even when others are open; the last one is activated, as in the UI.
        SolidEdgeDocument? active = TryActive(context) ?? AfterClose(() =>
        {
            if (documents.Count > 0 && documents.Item(documents.Count) is SolidEdgeDocument last)
            {
                last.Activate();
                return last;
            }

            return null;
        });
        return new { closed = name, saved = save, active = active is null ? null : context.DocumentHandle(active), note };
    }

    /// <summary>
    /// Reads the open documents right after a close. Solid Edge can still be unloading the closed document: it may leave
    /// the collection between Count and Item (DISP_E_BADINDEX) or still be listed and refuse calls (RPC_E_DISCONNECTED), so
    /// the read is tried again a few times.
    /// </summary>
    private static T AfterClose<T>(Func<T> read)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (COMException ex) when (attempt < CloseAttempts && (ex.HResult == ComErrors.DispEBadIndex || ComErrors.IsDisconnected(ex)))
            {
                Thread.Sleep(CloseWaitMs);
            }
        }
    }

    private const int CloseAttempts = 10;

    private const int CloseWaitMs = 200;

    private static readonly string[] Statuses = ["available", "in_work", "in_review", "released", "baselined", "obsolete", "unknown"];

    /// <summary>
    /// Life-cycle status of a document (File > Properties > Status). Without <paramref name="value"/> it is read; with it, set
    /// and read back. Verified: a new part is available and goes to in review and released; released to available gives E_FAIL.
    /// </summary>
    public static object Status(SeContext context, string? handle, DocumentLifeStatus? value)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string before = StatusName(document);
        if (value is not { } wanted)
        {
            return new { document = context.DocumentHandle(document), status = before };
        }

        int code = wanted switch
        {
            DocumentLifeStatus.Available => 0,
            DocumentLifeStatus.InWork => 1,
            DocumentLifeStatus.InReview => 2,
            DocumentLifeStatus.Released => 3,
            DocumentLifeStatus.Baselined => 4,
            DocumentLifeStatus.Obsolete => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        string name = ArgumentNames.Snake(wanted);

        try
        {
            document.Status = (DocumentStatus)code;
        }
        catch (COMException ex)
        {
            throw new ToolException("status_refused", $"Solid Edge did not change the status of '{document.Name}' to {name}: {ex.Message.Trim()}",
                "Solid Edge's workflow moves forward: a released document does not go back to available (E_FAIL).", ex);
        }

        string after = StatusName(document);
        return after == name
            ? new { document = context.DocumentHandle(document), status = after, previous = before, readOnly = document.ReadOnly }
            : throw new ToolException("status_refused", $"Solid Edge kept '{document.Name}' {after} instead of {name}.",
                "Some changes need the document saved or another status first (Solid Edge's workflow).");
    }

    /// <summary>
    /// Reads or sets the modelling mode of a part or sheet metal part, as the ribbon's Synchronous and Ordered buttons do. Rounds,
    /// chamfers and the face edits of se_part_feature keep a synchronous part synchronous; the other features of this server
    /// are ordered and switch it. In synchronous mode Solid Edge does not compute the ordered features, so the body read there
    /// holds only the synchronous ones.
    /// </summary>
    public static object ModelingMode(SeContext context, string? handle, DocumentModelingMode? mode)
    {
        ModelDocument model = ModelDocument.Get(context, handle);
        string before = model.ModelingMode;
        if (mode is null)
        {
            return new { document = context.DocumentHandle(model.Document), modelingMode = before };
        }

        bool changed = mode switch
        {
            DocumentModelingMode.Synchronous => model.EnsureSynchronous(),
            DocumentModelingMode.Ordered => model.EnsureOrdered(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (changed)
        {
            context.ModelChanged(model.Document, "the modelling mode changed");
        }

        double? volume = model.TryModel() is null ? null : model.TryVolume();
        return new
        {
            document = context.DocumentHandle(model.Document),
            modelingMode = model.ModelingMode,
            previous = before,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
        };
    }

    private static string StatusName(SolidEdgeDocument document)
    {
        int code = (int)document.Status;
        return code >= 0 && code < Statuses.Length ? Statuses[code] : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Undo, redo or recalculate (the document must support it). An assembly has no Recompute: it is updated with
    /// UpdateDocument, as the ribbon's Update button.
    /// </summary>
    public static object Edit(SeContext context, string? handle, string action, int steps)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string member = action switch
        {
            "undo" => "Undo",
            "redo" => "Redo",
            "recompute" => document is SolidEdgeAssembly.AssemblyDocument ? "UpdateDocument" : "Recompute",
            _ => throw ToolException.InvalidArgument($"Unknown edit action '{action}'."),
        };
        // Solid Edge does not record as undoable the operations created through the API from sketches, and creating a
        // sketch clears the history (verified): the number of steps is checked so that nothing that did not happen is assumed.
        string counter = action == "undo" ? "UndoTransactionCount" : "RedoTransactionCount";
        int? available = action == "recompute" ? null : TransactionCount(document, counter);
        if (available == 0)
        {
            throw new ToolException($"nothing_to_{action}", $"Solid Edge has no step to {action} in '{document.Name}'.",
                action == "undo"
                    ? "Solid Edge does not record features made through its API from sketches, and creating a sketch clears the history: remove or suppress the feature with se_feature_edit (delete/suppress) instead."
                    : "Only steps undone with 'undo' can be redone.");
        }

        int requested = Math.Clamp(steps, 1, 100);
        int count = available is { } n ? Math.Min(requested, n) : requested;
        try
        {
            object?[] args = action == "recompute" ? [] : [count];
            Com.Late.Call(document, member, args);
        }
        catch (Exception ex) when (Com.ComErrors.Unwrap(ex) is COMException { HResult: Com.ComErrors.DispEMemberNotFound or Com.ComErrors.DispEUnknownName })
        {
            throw new ToolException("not_supported", $"'{action}' is not available for this document ({DocumentKinds.Describe(document)}).",
                document is SolidEdgeDraft.DraftDocument ? "Update a drawing with se_drawing action 'update' (force = full update)." : null);
        }

        // After undo or recalculate, the model objects may have changed.
        context.Handles.InvalidateScope(context.DocumentHandle(document), $"the document changed ({action})");
        DocumentInfo info = Describe(context, document, TryActive(context));
        if (available is null)
        {
            return info;
        }

        int left = TransactionCount(document, counter) ?? 0;
        if (left >= available)
        {
            throw new ToolException($"{action}_failed", $"Solid Edge did not {action} anything in '{document.Name}'.",
                "Check the document in Solid Edge; use se_feature_edit to remove or restore features.");
        }

        return new
        {
            document = info,
            steps = available - left,
            requested = count < requested ? requested : (int?)null,
            remaining = left,
            note = count < requested ? $"Only {count} step(s) were available." : null,
        };
    }

    /// <summary>Undo or redo steps that Solid Edge keeps, or null if the document does not report them.</summary>
    private static int? TransactionCount(SolidEdgeDocument document, string counter)
    {
        try
        {
            return Convert.ToInt32(Com.Late.Call(document, counter), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Com.ComErrors.Unwrap(ex) is COMException or MissingMethodException)
        {
            return null;
        }
    }

    public static DocumentInfo Describe(SeContext context, SolidEdgeDocument document, SolidEdgeDocument? active)
    {
        bool readOnly = false;
        try
        {
            readOnly = document.ReadOnly;
        }
        catch (COMException)
        {
            // Not every document gives it: listed as writable.
        }

        string? path = PathOf(document);
        string? member = null;
        if (IsFamilyMember(document.FullName))
        {
            // The document of an assembly with alternate positions or a family is named "file.asm!Master": its active
            // member is the one shown and edited.
            member = Late.Get(document, "AssemblyFamilyMembers") is { } members ? Late.Get(members, "ActiveMember") as string : null;
        }

        return new DocumentInfo(
            context.DocumentHandle(document),
            path is not null && member is not null ? System.IO.Path.GetFileName(path) : document.Name,
            path,
            DocumentKinds.ToKind(document.Type),
            document.Dirty,
            readOnly,
            active is not null && ReferenceEquals(active, document),
            member);
    }

    /// <summary>Full path, or <c>null</c> if the document has never been saved.</summary>
    public static string? PathOf(SolidEdgeDocument document)
    {
        string? full = FileOf(document.FullName);
        return string.IsNullOrWhiteSpace(full) || !System.IO.Path.IsPathFullyQualified(full) ? null : full;
    }

    /// <summary>
    /// The file of a document or model link name. Verified: once an assembly has alternate positions or is a family, its
    /// document reads "C:\...\file.asm!Master" followed by a control character, and a drawing links "file.asm!Member".
    /// </summary>
    public static string? FileOf(string? fullName) =>
        fullName is not null && FamilyMember.Match(fullName) is { Success: true } m ? m.Groups[1].Value : fullName;

    /// <summary>The member a "file.asm!Member" name refers to, or null.</summary>
    public static string? MemberOf(string? fullName) =>
        fullName is not null && FamilyMember.Match(fullName) is { Success: true } m ? m.Groups[2].Value : null;

    private static bool IsFamilyMember(string? fullName) => fullName is not null && FamilyMember.IsMatch(fullName);

    private static readonly System.Text.RegularExpressions.Regex FamilyMember =
        new(@"^(.+\.(?:asm|par|psm))!([^\x00-\x1f]+)[\x00-\x1f]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static SolidEdgeDocument? FindOpen(SeContext context, string path)
    {
        Documents documents = context.Application.Documents;
        for (int i = 1, count = documents.Count; i <= count; i++)
        {
            if (documents.Item(i) is SolidEdgeDocument document
                && string.Equals(PathOf(document), path, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>Closes a document this call created, without saving; a failure leaves it open for the person.</summary>
    private static void TryClose(SolidEdgeDocument document)
    {
        try
        {
            document.Close(false);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            // The error that stopped the import is the one reported.
        }
    }

    /// <summary>The hWnd of each window of a document.</summary>
    private static List<int> WindowHandles(SolidEdgeDocument document)
    {
        try
        {
            return [.. Late.Items(document.Windows).Select(w => Convert.ToInt32(Late.Get(w, "hWnd"), System.Globalization.CultureInfo.InvariantCulture))];
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Verified in Solid Edge 2026: after Close, a part with features or a draft with views can leave its window in
    /// Application.Windows, hidden and bound to the deleted document, and those windows pile up; Window.Close removes them.
    /// Only the windows of the closed document are touched.
    /// </summary>
    private static void CloseLeftWindows(Application application, List<int> handles)
    {
        if (handles.Count == 0)
        {
            return;
        }

        try
        {
            List<object> left = [.. Late.Items(application.Windows)
                .Where(w => handles.Contains(Convert.ToInt32(Late.Get(w, "hWnd"), System.Globalization.CultureInfo.InvariantCulture)))];
            foreach (object window in left)
            {
                Late.Call(window, "Close", false);
            }
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex) || (ComErrors.IsDisconnected(ex) && Responds(application)))
        {
            // The document is closed; a window that cannot be closed only stays hidden, as before. One that Solid Edge releases
            // with its document while it is being closed is disconnected (RPC_E_DISCONNECTED) while Solid Edge itself still runs.
        }
    }

    private static bool Responds(Application application)
    {
        try
        {
            _ = application.Documents.Count;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static SolidEdgeDocument? TryActive(SeContext context)
    {
        try
        {
            return context.Application.Documents.Count == 0 ? null : context.Application.ActiveDocument as SolidEdgeDocument;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void RequireWritable(SolidEdgeDocument document)
    {
        bool readOnly;
        try
        {
            readOnly = document.ReadOnly;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Not every document gives it: the save reports its own error.
            return;
        }

        if (readOnly)
        {
            throw new ToolException("read_only", $"'{document.Name}' is open read-only.", "Use 'save_as' to save it under another name.");
        }
    }

    /// <summary>
    /// Templates are only read: they are accepted in the allowed folders and in the Solid Edge
    /// template folder.
    /// </summary>
    internal static string CheckTemplate(SeContext context, string template, DocumentTypeConstants type, string argument = "template")
    {
        string full;
        try
        {
            return context.Paths.Check(template, argument);
        }
        catch (ToolException ex) when (ex.Code == "path_not_allowed")
        {
            full = System.IO.Path.GetFullPath(template);
            foreach (string folder in TemplateFolders(context, type))
            {
                if (full.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(full))
                    {
                        throw ToolException.NotFound($"The template does not exist: '{full}'.");
                    }

                    return full;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Weldment template as an assembly when there is no installed .pwd template: the one set in Solid Edge's options
    /// (Global\Templates\WeldAsmTemplate of the running version, verified in Solid Edge 2026), otherwise one named like a
    /// weldment next to the default assembly template; null if the .pwd exists or none is found.
    /// </summary>
    private static string? WeldmentAssemblyTemplate(SeContext context)
    {
        try
        {
            if (File.Exists(context.Application.GetDefaultTemplatePath(DocumentTypeConstants.igWeldmentDocument)))
            {
                return null;
            }

            if (SolidEdgeSettings.Read(context.Application, @"Global\Templates", "WeldAsmTemplate") is { } configured && File.Exists(configured))
            {
                return configured;
            }

            string? folder = System.IO.Path.GetDirectoryName(
                context.Application.GetDefaultTemplatePath(DocumentTypeConstants.igAssemblyDocument));
            return folder is not null && Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*weld*.asm").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IEnumerable<string> TemplateFolders(SeContext context, DocumentTypeConstants type)
    {
        var folders = new List<string>();
        try
        {
            string? path = context.Application.Documents.TemplatePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                folders.Add(System.IO.Path.GetFullPath(path));
            }
        }
        catch (COMException)
        {
            // No template folder set: only the allowed folders count.
        }

        try
        {
            string? defaultTemplate = context.Application.GetDefaultTemplatePath(type);
            if (!string.IsNullOrWhiteSpace(defaultTemplate))
            {
                folders.Add(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(defaultTemplate))!);
            }
        }
        catch (COMException)
        {
            // No default template for this type.
        }

        return folders;
    }
}
