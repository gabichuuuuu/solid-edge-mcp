using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public sealed record CapturedImage(byte[] Data, string MimeType, string? SavedTo, int Width, int Height, string Document,
    IReadOnlyDictionary<string, string>? Samples = null);

/// <summary>View: fit, orient, display mode and image capture.</summary>
public static partial class ViewService
{
    /// <summary>
    /// Standard view commands by the name of their interop constant (&lt;Environment&gt;View&lt;Name&gt;, e.g. PartViewISOView),
    /// looked up in the active environment; the ids verified in Solid Edge 2026 are only a fallback.
    /// </summary>
    private static readonly Dictionary<string, (string Name, int Id)> Orientations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iso"] = ("ISOView", 32810),
        ["front"] = ("FrontView", 32811),
        ["right"] = ("RightView", 32812),
        ["top"] = ("TopView", 32813),
        ["left"] = ("LeftView", 33109),
        ["bottom"] = ("BottomView", 33110),
        ["back"] = ("BackView", 45079),
        ["dimetric"] = ("DimetricView", 45078),
        ["trimetric"] = ("TrimetricView", 45077),
    };

    public static IReadOnlyCollection<string> OrientationNames => Orientations.Keys;

    public static object Fit(SeContext context, string? handle)
    {
        SolidEdgeDocument document = Activate(context, handle);
        object window = FirstWindow(document);
        using var shown = new ShownWindow(context.Application, window);
        switch (window)
        {
            case Window model:
                FitView(model.View);
                break;
            case SolidEdgeDraft.SheetWindow sheet:
                FitSheet(sheet);
                break;
        }

        return new { fitted = document.Name };
    }

    public static object Orient(SeContext context, string? handle, string orientation)
    {
        SolidEdgeDocument document = Activate(context, handle);
        if (!Orientations.TryGetValue(orientation, out (string Name, int Id) view))
        {
            throw ToolException.InvalidArgument($"Unknown orientation '{orientation}'.", $"Use one of: {string.Join(", ", Orientations.Keys)}.");
        }

        if (FirstWindow(document) is not Window window)
        {
            throw new ToolException("not_supported", "Orientations apply to 3D documents (part, sheet metal, assembly).");
        }

        using var shown = new ShownWindow(context.Application, window);
        window.Activate();
        int command = CommandService.IdOf(Late.TryGetString(context.Application, "ActiveEnvironment"), "View" + view.Name) ?? view.Id;
        Camera before = CameraOf(window.View);
        context.Application.StartCommand((SolidEdgeCommandConstants)command);

        // Verified in Solid Edge 2026: the orientation command fits a perspective camera by narrowing its field of view
        // (40° to 5.1°), so the previous angle is put back.
        FitView(window.View, before.Perspective ? before.Scale : null);
        return new { orientation, document = document.Name };
    }

    public static object SetRenderMode(SeContext context, string? handle, string mode)
    {
        SolidEdgeDocument document = Activate(context, handle);
        if (FirstWindow(document) is not Window window)
        {
            throw new ToolException("not_supported", "Render modes apply to 3D documents.");
        }

        if (!Enum.TryParse(mode, ignoreCase: true, out SeRenderModeType value) || !Enum.IsDefined(value))
        {
            throw ToolException.InvalidArgument($"Unknown render mode '{mode}'.",
                $"Use one of: {string.Join(", ", Enum.GetNames<SeRenderModeType>().Where(n => n != nameof(SeRenderModeType.seRenderModeUndefined)))}.");
        }

        SeRenderModeType previous = window.View.RenderModeType;
        window.View.RenderModeType = value;
        window.View.Update();
        SeRenderModeType applied = window.View.RenderModeType;
        if (applied != value)
        {
            // Verified in Solid Edge 2026: Boundary and SmoothBoundary are changed to Smooth without warning.
            window.View.RenderModeType = previous;
            window.View.Update();
            throw new ToolException("not_supported", $"Solid Edge does not offer '{value}' in this window (it switched to '{applied}'); the previous mode was kept.",
                "Use seRenderModeSmooth, seRenderModeSmoothVHL, seRenderModeVHL, seRenderModeOutline, seRenderModeWireframe or seRenderModePhong.");
        }

        return new { renderMode = applied.ToString() };
    }

    /// <summary>Named views of a 3D document (the six standard ones and those saved), with their cameras in mm.</summary>
    public static object NamedViews(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        NamedViews views = NamedViewsOf(document);
        var rows = Late.Items(views).Cast<NamedView>().Select(DescribeView).ToList();
        return new { document = document.Name, count = rows.Count, views = rows };
    }

    /// <summary>
    /// Saves the current view of the window under a name. Verified: <c>NamedViews.Create</c> stores a default camera, not the
    /// current one, so the camera of the window is copied to it and read back.
    /// </summary>
    public static object SaveView(SeContext context, string? handle, string? name, string? description)
    {
        string viewName = RequireViewName(name);
        SolidEdgeDocument document = Activate(context, handle);
        Window window = ModelWindow(document);
        NamedViews views = NamedViewsOf(document);
        if (FindView(views, viewName) is not null)
        {
            throw ToolException.InvalidArgument($"There is already a named view '{viewName}'.", "Choose another name or delete it first (action delete_view).");
        }

        Camera camera = CameraOf(window.View);
        NamedView view = views.Create(viewName);
        try
        {
            view.SetCamera(camera.Eye[0], camera.Eye[1], camera.Eye[2], camera.Target[0], camera.Target[1], camera.Target[2],
                camera.Up[0], camera.Up[1], camera.Up[2], camera.Perspective, camera.Scale);
            if (!string.IsNullOrEmpty(description))
            {
                view.Description = description;
            }

            if (!SameDirection(CameraOf(view), camera))
            {
                throw new ToolException("view_failed", "Solid Edge did not keep the camera of the named view.", "Try again with the window shown.");
            }
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsDisconnected(ex)))
        {
            TryRemove(views, viewName);
            throw ex is ToolException ? ex : new ToolException("view_failed", $"Solid Edge rejected the named view: {ComErrors.Unwrap(ex).Message.Trim()}", "Use a plain name.", ex);
        }

        return new { document = document.Name, saved = DescribeView(view) };
    }

    /// <summary>Shows a named view in the window and checks that the window now looks along it (direction and up vector).</summary>
    public static object ApplyView(SeContext context, string? handle, string? name)
    {
        string viewName = RequireViewName(name);
        SolidEdgeDocument document = Activate(context, handle);
        Window window = ModelWindow(document);
        NamedView view = FindView(NamedViewsOf(document), viewName) ?? throw UnknownView(document, viewName);
        using var shown = new ShownWindow(context.Application, window);
        window.View.ApplyNamedView(Late.TryGetString(view, "Name") ?? viewName);
        if (!SameDirection(CameraOf(window.View), CameraOf(view)))
        {
            throw new ToolException("view_failed", $"The window does not look along the named view '{viewName}' after applying it.", "Try again with the window shown.");
        }

        return new { document = document.Name, applied = DescribeView(view) };
    }

    public static object DeleteView(SeContext context, string? handle, string? name)
    {
        string viewName = RequireViewName(name);
        SolidEdgeDocument document = context.GetDocument(handle);
        NamedViews views = NamedViewsOf(document);
        NamedView view = FindView(views, viewName) ?? throw UnknownView(document, viewName);
        string actual = Late.TryGetString(view, "Name") ?? viewName;
        if (StandardViews.Contains(actual))
        {
            throw new ToolException("not_supported", $"'{actual}' is one of the standard views of Solid Edge and is not deleted.", "Delete only views saved with save_view.");
        }

        views.Remove(actual);
        if (FindView(views, actual) is not null)
        {
            throw new ToolException("view_failed", $"Solid Edge did not delete the named view '{actual}'.");
        }

        return new { document = document.Name, deleted = actual };
    }

    /// <summary>Named views that every 3D document has.</summary>
    private static readonly HashSet<string> StandardViews = new(StringComparer.OrdinalIgnoreCase) { "top", "front", "right", "iso", "dimetric", "trimetric" };

    private sealed record Camera(double[] Eye, double[] Target, double[] Up, bool Perspective, double Scale);

    private static Camera CameraOf(SolidEdgeFramework.View view)
    {
        view.GetCamera(out double ex, out double ey, out double ez, out double tx, out double ty, out double tz,
            out double ux, out double uy, out double uz, out bool perspective, out double scale);
        return new Camera([ex, ey, ez], [tx, ty, tz], [ux, uy, uz], perspective, scale);
    }

    private static Camera CameraOf(NamedView view)
    {
        view.GetCamera(out double ex, out double ey, out double ez, out double tx, out double ty, out double tz,
            out double ux, out double uy, out double uz, out bool perspective, out double scale);
        return new Camera([ex, ey, ez], [tx, ty, tz], [ux, uy, uz], perspective, scale);
    }

    /// <summary>Same viewing direction (eye to target) and up vector, within 0.001 rad; the distance and zoom may change.</summary>
    private static bool SameDirection(Camera a, Camera b)
    {
        static double[] Unit(double[] v)
        {
            double length = Math.Sqrt(v.Sum(c => c * c));
            return length < 1e-12 ? v : v.Select(c => c / length).ToArray();
        }

        static double[] Direction(Camera c) => Unit(c.Target.Zip(c.Eye, (t, e) => t - e).ToArray());
        static bool Close(double[] x, double[] y) => x.Zip(y, (p, q) => Math.Abs(p - q)).Max() < 1e-3;
        return Close(Direction(a), Direction(b)) && Close(Unit(a.Up), Unit(b.Up));
    }

    private static object DescribeView(NamedView view)
    {
        Camera camera = CameraOf(view);
        string name = Late.TryGetString(view, "Name") ?? "";
        double[] direction = camera.Target.Zip(camera.Eye, (t, e) => t - e).ToArray();
        double length = Math.Sqrt(direction.Sum(c => c * c));
        return new
        {
            name,
            description = Late.TryGetString(view, "Description") is { Length: > 0 } text ? text : null,
            standard = StandardViews.Contains(name),
            direction = length < 1e-12 ? direction : direction.Select(c => Geo.R(c / length)).ToArray(),
            up = camera.Up.Select(Geo.R).ToArray(),
            target = camera.Target.Select(Geo.Mm).ToArray(),
            perspective = camera.Perspective,
        };
    }

    private static NamedViews NamedViewsOf(SolidEdgeDocument document) => Late.TryGet(document, "NamedViews") as NamedViews
        ?? throw new ToolException("not_supported", $"'{document.Name}' has no named views.", "Named views belong to parts, sheet metal and assemblies.");

    /// <summary>Named view by name, ignoring case (Solid Edge applies "Top" as "top").</summary>
    private static NamedView? FindView(NamedViews views, string name) =>
        Late.Items(views).Cast<NamedView>().FirstOrDefault(v => string.Equals(Late.TryGetString(v, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static ToolException UnknownView(SolidEdgeDocument document, string name) =>
        new("not_found", $"'{document.Name}' has no named view '{name}'.", "List them with se_view action named_views.");

    private static string RequireViewName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("'name' (the named view) is required.") : name.Trim();

    private static Window ModelWindow(SolidEdgeDocument document) => FirstWindow(document) as Window
        ?? throw new ToolException("not_supported", "Named views apply to 3D documents (part, sheet metal, assembly).");

    /// <summary>View of the first window of a 3D document; null when it has no window or it is a drawing sheet.</summary>
    internal static SolidEdgeFramework.View? ModelView(SolidEdgeDocument document) =>
        document.Windows.Count > 0 && document.Windows.Item(1) is Window window ? window.View : null;

    private static void TryRemove(NamedViews views, string name)
    {
        try
        {
            views.Remove(name);
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Left as it is; the error reports the failure.
        }
    }

    /// <summary>
    /// Image of the window. With samples, also the colours of its corners, edges and centre, read from a second small BMP
    /// of the same view (to check backgrounds and colours without looking at the image).
    /// </summary>
    public static CapturedImage Capture(SeContext context, string? handle, int width, int height, string? savePath, bool fit = true,
        bool samples = false)
    {
        int maxWidth = context.Policy.Limits.MaxImageWidth;
        int maxHeight = context.Policy.Limits.MaxImageHeight;
        if (width < 16 || height < 16 || width > maxWidth || height > maxHeight)
        {
            throw ToolException.InvalidArgument($"Image size must be between 16×16 and {maxWidth}×{maxHeight} pixels.");
        }

        SolidEdgeDocument document = Activate(context, handle);
        string? keep = null;
        string file;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            string folder = Path.Combine(context.AppPaths.Work, "captures");
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        }
        else
        {
            file = context.Paths.Check(savePath, "path");
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            {
                throw ToolException.InvalidArgument("The image path must end in .png, .jpg or .bmp.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            keep = file;
        }

        object target = FirstWindow(document);
        Dictionary<string, string>? colours = null;
        using (new ShownWindow(context.Application, target))
        {
            Save(target, file, width, height, fit);
            if (samples)
            {
                colours = Sample(context, target, width, height);
            }
        }

        if (!File.Exists(file))
        {
            throw new ToolException("capture_failed", "Solid Edge did not write the image.");
        }

        byte[] data = File.ReadAllBytes(file);
        if (keep is null)
        {
            TryDelete(file);
        }

        string mime = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        return new CapturedImage(data, mime, keep, width, height, document.Name, colours);
    }

    /// <summary>The same view, without fitting again, as a BMP 160 pixels wide with the proportions of the capture.</summary>
    private static Dictionary<string, string> Sample(SeContext context, object target, int width, int height)
    {
        const int sampleWidth = 160;
        int sampleHeight = Math.Max(16, (int)Math.Round(sampleWidth * (double)height / width));
        string folder = Path.Combine(context.AppPaths.Work, "captures");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"samples-{Guid.NewGuid():N}.bmp");
        try
        {
            Save(target, file, sampleWidth, sampleHeight, fit: false);
            return File.Exists(file) ? Pixels(File.ReadAllBytes(file)) : throw new ToolException("capture_failed", "Solid Edge did not write the sample image.");
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void Save(object target, string file, int width, int height, bool fit)
    {
        switch (target)
        {
            case Window window:
                if (fit)
                {
                    FitView(window.View);
                }

                window.View.SaveAsImage(file, width, height, Type.Missing, Type.Missing, Type.Missing, SeImageQualityType.seImageQualityHigh);
                break;
            case SolidEdgeDraft.SheetWindow sheet:
                if (fit)
                {
                    FitSheet(sheet);
                }

                sheet.SaveAsImage(file, width, height, Type.Missing, Type.Missing, SeImageQualityType.seImageQualityHigh);
                break;
            default:
                throw new ToolException("not_supported", "The document has no window that can be captured.");
        }
    }

    /// <summary>
    /// Whole sheet, with the title block and the background frame. <c>SheetWindow.Fit</c> frames only the views and leaves out
    /// the title block (verified in Solid Edge 2026).
    /// </summary>
    private static void FitSheet(SolidEdgeDraft.SheetWindow sheet) => sheet.FitEx(SolidEdgeDraft.SheetFitConstants.igFitSheet);

    /// <summary>
    /// With Solid Edge minimized the view has no size: Fit leaves a degenerate camera and the captures
    /// come out empty. While it lasts, the main window is shown without activating it (it does not take focus from the
    /// person) and it is minimized again when finished.
    /// </summary>
    private sealed class ShownWindow : IDisposable
    {
        private const int MinimumHeight = 100;
        private static readonly TimeSpan LayoutTimeout = TimeSpan.FromSeconds(3);

        private readonly IntPtr _frame;

        public ShownWindow(Application application, object window)
        {
            _frame = new IntPtr(application.hWnd);
            if (_frame == IntPtr.Zero || !NativeMethods.IsIconic(_frame))
            {
                _frame = IntPtr.Zero;
                return;
            }

            NativeMethods.ShowWindow(_frame, NativeMethods.SwShowNoActivate);

            // Waits for Solid Edge to reposition the document window at its real size.
            DateTime limit = DateTime.UtcNow + LayoutTimeout;
            while (DateTime.UtcNow < limit && Late.TryGet(window, "Height") is int height && height < MinimumHeight)
            {
                Thread.Sleep(50);
            }
        }

        public void Dispose()
        {
            if (_frame != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_frame, NativeMethods.SwShowMinNoActive);
            }
        }
    }

    private static SolidEdgeDocument Activate(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        if (!string.IsNullOrWhiteSpace(handle))
        {
            document.Activate();
        }

        return document;
    }

    private static object FirstWindow(SolidEdgeDocument document)
    {
        Windows windows = document.Windows;
        if (windows.Count == 0)
        {
            throw new ToolException("no_window", $"'{document.Name}' has no open window.",
                "Activate the document (se_documents activate) so that Solid Edge shows it.");
        }

        return windows.Item(1);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool IsComObject(object? value) => value is not null && Marshal.IsComObject(value);
}
