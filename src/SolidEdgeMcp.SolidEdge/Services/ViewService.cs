using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public sealed record CapturedImage(byte[] Data, string MimeType, string? SavedTo, int Width, int Height, string Document);

/// <summary>View: fit, orient, display mode and image capture.</summary>
public static class ViewService
{
    /// <summary>Standard view commands; the identifiers match in part, sheet metal and assembly.</summary>
    private static readonly Dictionary<string, int> Orientations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iso"] = 32810,
        ["front"] = 32811,
        ["right"] = 32812,
        ["top"] = 32813,
        ["left"] = 33109,
        ["bottom"] = 33110,
        ["back"] = 45079,
        ["dimetric"] = 45078,
        ["trimetric"] = 45077,
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
                model.View.Fit();
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
        if (!Orientations.TryGetValue(orientation, out int command))
        {
            throw ToolException.InvalidArgument($"Unknown orientation '{orientation}'.", $"Use one of: {string.Join(", ", Orientations.Keys)}.");
        }

        if (FirstWindow(document) is not Window window)
        {
            throw new ToolException("not_supported", "Orientations apply to 3D documents (part, sheet metal, assembly).");
        }

        using var shown = new ShownWindow(context.Application, window);
        window.Activate();
        context.Application.StartCommand((SolidEdgeCommandConstants)command);
        window.View.Fit();
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

    public static CapturedImage Capture(SeContext context, string? handle, int width, int height, string? savePath, bool fit = true)
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
        using (new ShownWindow(context.Application, target))
        {
            Save(target, file, width, height, fit);
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
        return new CapturedImage(data, mime, keep, width, height, document.Name);
    }

    private static void Save(object target, string file, int width, int height, bool fit)
    {
        switch (target)
        {
            case Window window:
                if (fit)
                {
                    window.View.Fit();
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
