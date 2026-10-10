using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public sealed record DocumentInfo(
    string Handle,
    string Name,
    string? Path,
    string Kind,
    bool Dirty,
    bool ReadOnly,
    bool Active);

/// <summary>Documentos: listar, crear, abrir, guardar y cerrar, siempre con <see cref="PathGuard"/>.</summary>
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
        object created = string.IsNullOrWhiteSpace(template)
            ? context.Application.Documents.Add(progId)
            : context.Application.Documents.Add(progId, CheckTemplate(context, template, type));
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

        SolidEdgeDocument? already = FindOpen(context, checkedPath);
        if (already is not null)
        {
            already.Activate();
            return Describe(context, already, already);
        }

        var document = (SolidEdgeDocument)context.Application.Documents.Open(checkedPath);
        return Describe(context, document, document);
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

        if (File.Exists(target) && !overwrite)
        {
            throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (copy)
        {
            document.SaveCopyAs(target);
        }
        else
        {
            document.SaveAs(target);
        }

        if (!File.Exists(target))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{target}'.");
        }

        return Describe(context, document, TryActive(context));
    }

    public static object Close(SeContext context, string? handle, bool? saveChanges)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string name = document.Name;
        bool dirty = document.Dirty;
        if (dirty && saveChanges is null)
        {
            throw new ToolException("unsaved_changes", $"'{name}' has unsaved changes.",
                "Pass saveChanges=true to save them or saveChanges=false to discard them.");
        }

        bool save = dirty && saveChanges == true;
        if (save)
        {
            string path = PathOf(document) ?? throw new ToolException("never_saved",
                $"'{name}' has never been saved.", "Use action 'save_as' first, then close.");
            context.Paths.Check(path, "document path");
        }

        string documentHandle = context.DocumentHandle(document);
        document.Close(save);
        context.Handles.InvalidateScope(documentHandle, "its document was closed");
        context.Handles.Remove(documentHandle, "the document was closed");

        // Solid Edge puede quedarse sin documento activo aunque queden otros abiertos; se activa el último, como en la interfaz.
        SolidEdgeDocument? active = TryActive(context);
        Documents documents = context.Application.Documents;
        if (active is null && documents.Count > 0 && documents.Item(documents.Count) is SolidEdgeDocument last)
        {
            last.Activate();
            active = last;
        }

        return new { closed = name, saved = save, active = active is null ? null : context.DocumentHandle(active) };
    }

    /// <summary>Deshacer, rehacer o recalcular (el documento debe admitirlo).</summary>
    public static object Edit(SeContext context, string? handle, string action, int steps)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string member = action switch
        {
            "undo" => "Undo",
            "redo" => "Redo",
            "recompute" => "Recompute",
            _ => throw ToolException.InvalidArgument($"Unknown edit action '{action}'."),
        };
        try
        {
            object?[] args = member == "Recompute" ? [] : [Math.Clamp(steps, 1, 100)];
            document.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, document, args, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Com.ComErrors.Unwrap(ex) is COMException { HResult: Com.ComErrors.DispEMemberNotFound or Com.ComErrors.DispEUnknownName })
        {
            throw new ToolException("not_supported", $"'{action}' is not available for a {DocumentKinds.Describe(document)}.");
        }

        // Tras deshacer o recalcular, los objetos del modelo pueden haber cambiado.
        context.Handles.InvalidateScope(context.DocumentHandle(document), $"the document changed ({action})");
        return Describe(context, document, TryActive(context));
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
        }

        return new DocumentInfo(
            context.DocumentHandle(document),
            document.Name,
            PathOf(document),
            DocumentKinds.ToKind(document.Type),
            document.Dirty,
            readOnly,
            active is not null && ReferenceEquals(active, document));
    }

    /// <summary>Ruta completa, o <c>null</c> si el documento no se ha guardado nunca.</summary>
    public static string? PathOf(SolidEdgeDocument document)
    {
        string? full = document.FullName;
        return string.IsNullOrWhiteSpace(full) || !System.IO.Path.IsPathFullyQualified(full) ? null : full;
    }

    private static SolidEdgeDocument? FindOpen(SeContext context, string path)
    {
        Documents documents = context.Application.Documents;
        for (int i = 1; i <= documents.Count; i++)
        {
            if (documents.Item(i) is SolidEdgeDocument document
                && string.Equals(PathOf(document), path, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
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
        catch (COMException)
        {
            return;
        }

        if (readOnly)
        {
            throw new ToolException("read_only", $"'{document.Name}' is open read-only.", "Use 'save_as' to save it under another name.");
        }
    }

    /// <summary>
    /// Las plantillas solo se leen: se aceptan en las carpetas permitidas y en la carpeta de
    /// plantillas de Solid Edge.
    /// </summary>
    private static string CheckTemplate(SeContext context, string template, DocumentTypeConstants type)
    {
        string full;
        try
        {
            return context.Paths.Check(template, "template");
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
        }

        return folders;
    }
}
