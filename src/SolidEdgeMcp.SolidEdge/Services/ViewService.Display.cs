using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>View style, background and camera of a part, sheet metal or assembly window.</summary>
public static partial class ViewService
{
    /// <summary>Background images that come with Solid Edge (its View Styles dialog), under the installation folder.</summary>
    private const string InstalledBackgrounds = @"Images\environments\backgrounds";

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif"];

    /// <summary>
    /// Kinds that can be set. Verified in Solid Edge 2026: the environment kinds (static and immersive) fall back to solid
    /// through the API, even with a sky image (SetSkyboxSkyFile), so they are only reported.
    /// </summary>
    private static readonly Dictionary<string, SeBackgroundType> Backgrounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solid"] = SeBackgroundType.seBackgroundTypeSolid,
        ["gradient"] = SeBackgroundType.seBackgroundTypeGradient,
        ["image"] = SeBackgroundType.seBackgroundTypeImage,
    };

    private static readonly Dictionary<SeBackgroundType, string> OtherBackgrounds = new()
    {
        [SeBackgroundType.seBackgroundTypeImageReference] = "image_reference",
        [SeBackgroundType.seBackgroundTypeStaticEnvironment] = "static_environment",
        [SeBackgroundType.seBackgroundTypeImmersiveEnvironment] = "immersive_environment",
    };

    private static readonly Dictionary<string, SeGradientType> Gradients = new(StringComparer.OrdinalIgnoreCase)
    {
        ["horizontal"] = SeGradientType.seGradientTypeHorizontal,
        ["vertical"] = SeGradientType.seGradientTypeVertical,
        ["diagonal_up"] = SeGradientType.seGradientTypeDiagonalUp,
        ["diagonal_down"] = SeGradientType.seGradientTypeDiagonalDown,
        ["square_spot"] = SeGradientType.seGradientTypeSquareSpot,
        ["circular_spot"] = SeGradientType.seGradientTypeCircularSpot,
    };

    public static IReadOnlyCollection<string> BackgroundNames => Backgrounds.Keys;

    public static IReadOnlyCollection<string> GradientNames => Gradients.Keys;

    /// <summary>View style of the window; without a name, the current one and those the document has.</summary>
    public static object Style(SeContext context, string? handle, string? name)
    {
        SolidEdgeDocument document = Activate(context, handle);
        View view = Window3D(document).View;
        string previous = view.Style;
        string[] styles = ViewStyleNames(document);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new { document = document.Name, style = previous, styles };
        }

        string wanted = styles.FirstOrDefault(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"'{document.Name}' has no view style '{name}'.{NameMatch.Hint(styles, name)}",
                "Call se_view style without a name to list them; se_styles add (type view) creates one.");
        view.Style = wanted;
        view.Update();
        string applied = view.Style;
        return string.Equals(applied, wanted, StringComparison.OrdinalIgnoreCase)
            ? new { document = document.Name, style = applied, previous }
            : throw new ToolException("value_rejected", $"Solid Edge kept the view style '{applied}'.");
    }

    /// <summary>
    /// Background of the window, kept in its view style (saved with the document). Verified in Solid Edge 2026: the API does
    /// not change the colour of a solid background (SetGradientColor and the ColorBackground option leave it as it is), so a
    /// solid colour is made as a gradient with that colour at both ends; a horizontal gradient has its first colour at the
    /// top and a vertical one at the left.
    /// </summary>
    public static object Background(SeContext context, string? handle, string? kind, string[]? colors, string? gradient, string? image)
    {
        SolidEdgeDocument document = Activate(context, handle);
        View view = Window3D(document).View;
        ViewStyle style = StyleOf(document, view);
        Backdrop previous = BackdropOf(style);
        if (string.IsNullOrWhiteSpace(kind))
        {
            if (colors is { Length: > 0 } || gradient is not null || image is not null)
            {
                throw ToolException.InvalidArgument("Changing the background requires 'kind' (solid, gradient, image...).");
            }

            return new { document = document.Name, style = style.StyleName, background = Describe(previous), images = InstalledImages(context) };
        }

        Backdrop wanted = Wanted(context, kind, colors ?? [], gradient, image, previous);
        try
        {
            Apply(style, wanted);
            view.Update();
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            Restore(style, view, previous);
            throw new ToolException("value_rejected", $"Solid Edge rejected the background: {ComErrors.Unwrap(ex).Message.Trim()}",
                "The previous background was put back.", ex);
        }

        Backdrop current = BackdropOf(style);
        if (!Matches(current, wanted))
        {
            Restore(style, view, previous);
            throw new ToolException("value_rejected",
                $"Solid Edge kept the background as {JsonSerializer.Serialize(Describe(current))} instead of {JsonSerializer.Serialize(Describe(wanted))}.",
                "The previous background was put back.");
        }

        bool solidColor = string.Equals(kind.Trim(), "solid", StringComparison.OrdinalIgnoreCase) && colors is { Length: > 0 };
        return new
        {
            document = document.Name,
            style = style.StyleName,
            background = Describe(current),
            previous = Describe(previous),
            note = solidColor
                ? "Solid Edge does not set the colour of a solid background through its API: this is a gradient with the same colour at both ends, which looks the same."
                : null,
        };
    }

    public static object GetCamera(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        return new { document = document.Name, camera = DescribeCamera(CameraOf(Window3D(document).View)) };
    }

    /// <summary>
    /// Places the camera: eye, target (mm) and up vector, perspective or not, and the field of view of a perspective camera
    /// (degrees); what is not given stays as it is. Checked by reading the camera back.
    /// </summary>
    public static object SetCamera(SeContext context, string? handle, double[]? eye, double[]? target, double[]? up, bool? perspective,
        double? angle, bool fit)
    {
        SolidEdgeDocument document = Activate(context, handle);
        Window window = Window3D(document);
        View view = window.View;
        using var shown = new ShownWindow(context.Application, window);
        Camera before = CameraOf(view);
        double[] e = eye is null ? before.Eye : Point(eye, "eye");
        double[] t = target is null ? before.Target : Point(target, "target");
        double[] u = up is null ? before.Up : Vector(up, "up");
        bool persp = perspective ?? before.Perspective;
        double scaleOrAngle;
        if (persp)
        {
            double degrees = angle ?? (before.Perspective ? Units.RadToDeg(before.Scale) : 30);
            scaleOrAngle = degrees is > 0 and < 180
                ? Units.DegToRad(degrees)
                : throw ToolException.InvalidArgument("'angle' (field of view of a perspective camera) must be between 0 and 180 degrees.");
        }
        else
        {
            scaleOrAngle = angle is null
                ? before.Perspective ? 1.0 : before.Scale
                : throw ToolException.InvalidArgument("'angle' is the field of view of a perspective camera: pass perspective = true.");
        }

        double[] direction = Minus(t, e);
        if (Length(direction) < 1e-9)
        {
            throw ToolException.InvalidArgument("'eye' and 'target' must be different points.");
        }

        if (Length(Cross(direction, u)) < 1e-6 * Length(direction) * Length(u))
        {
            throw ToolException.InvalidArgument("'up' must not be parallel to the viewing direction (target - eye).");
        }

        view.SetCamera(e[0], e[1], e[2], t[0], t[1], t[2], u[0], u[1], u[2], persp, scaleOrAngle);

        // From perspective to parallel the zoom has no meaning, so the model is fitted.
        if (fit || (before.Perspective && !persp))
        {
            FitView(view);
        }

        view.Update();
        Camera after = CameraOf(view);
        if (!LooksAlong(after, direction, u) || after.Perspective != persp)
        {
            view.SetCamera(before.Eye[0], before.Eye[1], before.Eye[2], before.Target[0], before.Target[1], before.Target[2],
                before.Up[0], before.Up[1], before.Up[2], before.Perspective, before.Scale);
            view.Update();
            throw new ToolException("view_failed", "Solid Edge did not keep the camera; the previous one was put back.", "Try again with the window shown.");
        }

        return new { document = document.Name, camera = DescribeCamera(after), previous = DescribeCamera(before) };
    }

    /// <summary>
    /// Zooms in (factor above 1) or out (below 1) about the centre of the window. Verified in Solid Edge 2026: the scale of a
    /// parallel camera is in pixels per metre and ZoomCamera multiplies it by the factor.
    /// </summary>
    public static object Zoom(SeContext context, string? handle, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw ToolException.InvalidArgument("'factor' must be a positive number (2 = twice as large, 0.5 = half).");
        }

        SolidEdgeDocument document = Activate(context, handle);
        Window window = Window3D(document);
        View view = window.View;
        using var shown = new ShownWindow(context.Application, window);
        Camera before = CameraOf(view);
        view.ZoomCamera(factor);
        view.Update();
        Camera after = CameraOf(view);
        bool zoomed = before.Perspective
            ? Math.Abs(after.Scale - before.Scale) > 1e-12 || Length(Minus(after.Eye, before.Eye)) > 1e-12
            : Math.Abs((after.Scale / before.Scale) - factor) < 0.01 * factor;
        return zoomed || Math.Abs(factor - 1) < 1e-9
            ? new { document = document.Name, factor, camera = DescribeCamera(after), previous = DescribeCamera(before) }
            : throw new ToolException("view_failed", "Solid Edge did not zoom the view.", "Try again with the window shown.");
    }

    /// <summary>
    /// Turns the camera about an axis through its target, right-handed (default axis its up vector: the model seems to turn
    /// the other way on the screen). Verified in Solid Edge 2026: RotateCamera takes the angle in degrees.
    /// </summary>
    public static object Rotate(SeContext context, string? handle, double angle, double[]? axis)
    {
        Units.RequireFinite(angle, "angle");
        SolidEdgeDocument document = Activate(context, handle);
        Window window = Window3D(document);
        View view = window.View;
        using var shown = new ShownWindow(context.Application, window);
        Camera before = CameraOf(view);
        double[] a = Unit(axis is null ? before.Up : Vector(axis, "axis"));
        view.RotateCamera(angle, before.Target[0], before.Target[1], before.Target[2], a[0], a[1], a[2]);
        view.Update();
        Camera after = CameraOf(view);
        double radians = Units.DegToRad(angle);
        if (!LooksAlong(after, Turn(Minus(before.Target, before.Eye), a, radians), Turn(before.Up, a, radians)))
        {
            throw new ToolException("view_failed", "The camera did not turn as asked.", "Try again with the window shown.");
        }

        return new { document = document.Name, angle, axis = a.Select(Geo.R).ToArray(), camera = DescribeCamera(after), previous = DescribeCamera(before) };
    }

    /// <summary>Vector turned by an angle about a unit axis, right-handed (Rodrigues).</summary>
    private static double[] Turn(double[] v, double[] axis, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double[] cross = Cross(axis, v);
        double dot = Dot(axis, v) * (1 - cos);
        return [(v[0] * cos) + (cross[0] * sin) + (axis[0] * dot), (v[1] * cos) + (cross[1] * sin) + (axis[1] * dot), (v[2] * cos) + (cross[2] * sin) + (axis[2] * dot)];
    }

    /// <summary>
    /// Fits the model in the view. Verified in Solid Edge 2026: View.Fit narrows the field of view of a perspective camera
    /// (40° became 5°), so a perspective view is framed by moving the eye along its direction, keeping the angle.
    /// </summary>
    private static void FitView(View view, double? angle = null)
    {
        Camera camera = CameraOf(view);
        double fieldOfView = angle ?? camera.Scale;
        view.GetModelRange(out double lx, out double ly, out double lz, out double hx, out double hy, out double hz);
        double[] center = [(lx + hx) / 2, (ly + hy) / 2, (lz + hz) / 2];
        double radius = Length([hx - lx, hy - ly, hz - lz]) / 2;
        if (!camera.Perspective || radius < 1e-9 || fieldOfView is <= 0 or >= Math.PI)
        {
            view.Fit();
            return;
        }

        double[] eye = Minus(center, Scale(Unit(Minus(camera.Target, camera.Eye)), radius / Math.Sin(fieldOfView / 2)));
        view.SetCamera(eye[0], eye[1], eye[2], center[0], center[1], center[2], camera.Up[0], camera.Up[1], camera.Up[2], true, fieldOfView);
    }

    private sealed record Backdrop(SeBackgroundType Type, SeGradientType Gradient, int Color1, int Color2, string? Image);

    private static Backdrop BackdropOf(ViewStyle style)
    {
        style.GetGradientBackground(out SeGradientType gradient, out int first, out int second, out _, out _);
        return new Backdrop(style.BackgroundType, gradient, first, second, Late.TryGetString(style, "BackgroundImageFile"));
    }

    private static Backdrop Wanted(SeContext context, string kind, string[] colors, string? gradient, string? image, Backdrop previous)
    {
        string key = kind.Trim();
        if (!Backgrounds.TryGetValue(key, out SeBackgroundType type))
        {
            throw ToolException.InvalidArgument($"Unknown background '{kind}'.", $"Use one of: {string.Join(", ", Backgrounds.Keys)}.");
        }

        bool colored = type is SeBackgroundType.seBackgroundTypeSolid or SeBackgroundType.seBackgroundTypeGradient;
        if (colors.Length > (type == SeBackgroundType.seBackgroundTypeGradient ? 2 : colored ? 1 : 0))
        {
            throw ToolException.InvalidArgument(colored ? "Too many 'colors': a solid background takes one, a gradient one or two." : $"A '{key}' background takes no 'colors'.");
        }

        if (gradient is not null && type != SeBackgroundType.seBackgroundTypeGradient)
        {
            throw ToolException.InvalidArgument("'gradient' applies to kind 'gradient'.");
        }

        if (image is not null && type != SeBackgroundType.seBackgroundTypeImage)
        {
            throw ToolException.InvalidArgument("'path' applies to kind 'image'.");
        }

        if (type == SeBackgroundType.seBackgroundTypeGradient || (type == SeBackgroundType.seBackgroundTypeSolid && colors.Length > 0))
        {
            if (colors.Length == 0)
            {
                throw ToolException.InvalidArgument("A gradient background needs 'colors': one or two '#RRGGBB'.");
            }

            int first = ComProperties.ColorRef(colors[0], "colors[0]");
            int second = colors.Length > 1 ? ComProperties.ColorRef(colors[1], "colors[1]") : first;
            SeGradientType shape = gradient is null
                ? SeGradientType.seGradientTypeHorizontal
                : Gradients.TryGetValue(gradient.Trim(), out SeGradientType g)
                    ? g
                    : throw ToolException.InvalidArgument($"Unknown gradient '{gradient}'.", $"Use one of: {string.Join(", ", Gradients.Keys)}.");
            return previous with { Type = SeBackgroundType.seBackgroundTypeGradient, Gradient = shape, Color1 = first, Color2 = second };
        }

        return type == SeBackgroundType.seBackgroundTypeImage
            ? previous with { Type = type, Image = ImageFile(context, image ?? throw ToolException.InvalidArgument("An image background needs 'path'.")) }
            : previous with { Type = type };
    }

    /// <summary>
    /// Verified in Solid Edge 2026: the style keeps a gradient as a pseudo image name ($$GRAD_S_ff0000_0000ff$$) in
    /// BackgroundImageFile, so writing an image name undoes a gradient, and a solid background clears it.
    /// </summary>
    private static void Apply(ViewStyle style, Backdrop backdrop)
    {
        style.BackgroundType = backdrop.Type;
        if (backdrop.Type == SeBackgroundType.seBackgroundTypeGradient)
        {
            style.SetGradientBackground(backdrop.Gradient, backdrop.Color1, backdrop.Color2);
        }
        else if (backdrop.Type == SeBackgroundType.seBackgroundTypeImage && backdrop.Image is { Length: > 0 } file)
        {
            style.BackgroundImageFile = file;
        }
    }

    private static void Restore(ViewStyle style, View view, Backdrop previous)
    {
        try
        {
            Apply(style, previous);
            view.Update();
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            // The error that brought here is the one reported.
        }
    }

    private static bool Matches(Backdrop current, Backdrop wanted) =>
        current.Type == wanted.Type
        && (wanted.Type != SeBackgroundType.seBackgroundTypeGradient
            || (current.Gradient == wanted.Gradient && current.Color1 == wanted.Color1 && current.Color2 == wanted.Color2))
        && (wanted.Type != SeBackgroundType.seBackgroundTypeImage
            || string.Equals(current.Image, wanted.Image, StringComparison.OrdinalIgnoreCase));

    private static object Describe(Backdrop backdrop)
    {
        bool gradient = backdrop.Type == SeBackgroundType.seBackgroundTypeGradient;
        bool image = backdrop.Type is SeBackgroundType.seBackgroundTypeImage or SeBackgroundType.seBackgroundTypeImageReference;
        return new
        {
            kind = Backgrounds.FirstOrDefault(p => p.Value == backdrop.Type).Key
                ?? OtherBackgrounds.GetValueOrDefault(backdrop.Type) ?? ((int)backdrop.Type).ToString(System.Globalization.CultureInfo.InvariantCulture),
            colors = gradient ? new[] { ComProperties.Hex(backdrop.Color1), ComProperties.Hex(backdrop.Color2) } : null,
            gradient = gradient ? Gradients.FirstOrDefault(p => p.Value == backdrop.Gradient).Key ?? backdrop.Gradient.ToString() : null,
            path = image ? backdrop.Image : null,
        };
    }

    /// <summary>
    /// An image in the allowed folders or among those of the Solid Edge installation (only read). A relative path is taken
    /// from the installation and kept as given, as Solid Edge's own view styles do (images\environments\backgrounds\...).
    /// </summary>
    private static string ImageFile(SeContext context, string path)
    {
        string text = path.Trim();
        string? install = SolidEdgeSettings.InstallFolder(context.Application)?.TrimEnd('\\');
        string full;
        string stored;
        if (Path.IsPathFullyQualified(text))
        {
            full = install is not null && Path.GetFullPath(text).StartsWith(install + "\\", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(text)
                : context.Paths.Check(text, "path");
            stored = full;
        }
        else
        {
            full = install is null ? "" : Path.GetFullPath(Path.Combine(install, text));
            if (install is null || !full.StartsWith(install + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidArgument($"'{text}' is not an absolute path nor one inside the Solid Edge installation.");
            }

            stored = text;
        }

        if (!ImageExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(full)}' is not an image: use {string.Join(", ", ImageExtensions)}.");
        }

        return File.Exists(full)
            ? stored
            : throw new ToolException("file_missing", $"There is no image '{full}'.", "se_view background without 'kind' lists the images that come with Solid Edge.");
    }

    private static string[] InstalledImages(SeContext context)
    {
        string? install = SolidEdgeSettings.InstallFolder(context.Application);
        string folder = install is null ? "" : Path.Combine(install, InstalledBackgrounds);
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder).Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).Take(context.MaxListItems)]
            : [];
    }

    private static string[] ViewStyleNames(SolidEdgeDocument document) => Late.TryGet(document, "ViewStyles") is { } styles
        ? [.. Late.Items(styles).Select(s => Late.TryGetString(s, "StyleName")).OfType<string>()]
        : [];

    private static ViewStyle StyleOf(SolidEdgeDocument document, View view)
    {
        if (Late.TryGet(view, "ViewStyle") is ViewStyle style)
        {
            return style;
        }

        string name = view.Style;
        return Late.TryGet(document, "ViewStyles") is { } styles
            && Late.Items(styles).OfType<ViewStyle>().FirstOrDefault(s => string.Equals(s.StyleName, name, StringComparison.OrdinalIgnoreCase)) is { } found
                ? found
                : throw new ToolException("not_supported", $"The view style '{name}' of the window could not be read.");
    }

    private static Window Window3D(SolidEdgeDocument document) => FirstWindow(document) as Window
        ?? throw new ToolException("not_supported", "View styles, backgrounds and cameras belong to 3D windows (part, sheet metal, assembly).",
            "The colours of drawing sheets are application options: se_options list with category colors.");

    private static object DescribeCamera(Camera camera)
    {
        double[] direction = Unit(Minus(camera.Target, camera.Eye));
        return new
        {
            eye = camera.Eye.Select(Geo.Mm).ToArray(),
            target = camera.Target.Select(Geo.Mm).ToArray(),
            up = Unit(camera.Up).Select(Geo.R).ToArray(),
            direction = direction.Select(Geo.R).ToArray(),
            perspective = camera.Perspective,
            angle = camera.Perspective ? Math.Round(Units.RadToDeg(camera.Scale), 6) : (double?)null,
            scale = camera.Perspective ? (double?)null : Geo.R(camera.Scale),
        };
    }

    /// <summary>Whether the camera looks along the direction with that up vector (its part across the direction), within 0.001.</summary>
    private static bool LooksAlong(Camera camera, double[] direction, double[] up)
    {
        static double[] Across(double[] v, double[] d) => Unit(Minus(v, Scale(d, Dot(v, d))));
        static bool Close(double[] x, double[] y) => x.Zip(y, (p, q) => Math.Abs(p - q)).Max() < 1e-3;
        double[] wanted = Unit(direction);
        double[] actual = Unit(Minus(camera.Target, camera.Eye));
        return Close(actual, wanted) && Close(Across(camera.Up, actual), Across(up, wanted));
    }

    /// <summary>Point in mm → metres.</summary>
    private static double[] Point(double[] mm, string argument) =>
        mm.Length == 3 && mm.All(double.IsFinite)
            ? [.. mm.Select(Units.MmToM)]
            : throw ToolException.InvalidArgument($"'{argument}' must be three numbers [x, y, z] in mm.");

    private static double[] Vector(double[] value, string argument) =>
        value.Length == 3 && value.All(double.IsFinite) && Length(value) > 1e-12
            ? value
            : throw ToolException.InvalidArgument($"'{argument}' must be three numbers [x, y, z], not all zero.");

    private static double[] Minus(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Scale(double[] v, double s) => [v[0] * s, v[1] * s, v[2] * s];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double Length(double[] v) => Math.Sqrt(Dot(v, v));

    private static double[] Unit(double[] v)
    {
        double length = Length(v);
        return length < 1e-12 ? v : Scale(v, 1 / length);
    }

    /// <summary>Colour (#RRGGBB) of some pixels of a 24 or 32 bit BMP: corners, middle of the top and bottom edges, centre.</summary>
    internal static Dictionary<string, string> Pixels(byte[] bmp)
    {
        if (bmp.Length < 54 || bmp[0] != 'B' || bmp[1] != 'M')
        {
            throw new ToolException("capture_failed", "The sample image is not a BMP.");
        }

        int offset = BitConverter.ToInt32(bmp, 10);
        int width = BitConverter.ToInt32(bmp, 18);
        int height = BitConverter.ToInt32(bmp, 22);
        int bits = BitConverter.ToInt16(bmp, 28);
        if (bits is not (24 or 32) || width < 8 || height == 0)
        {
            throw new ToolException("capture_failed", $"Unexpected sample image ({width}×{height}, {bits} bits).");
        }

        bool bottomUp = height > 0;
        height = Math.Abs(height);
        int stride = ((width * bits) + 31) / 32 * 4;
        string At(int x, int y)
        {
            int i = offset + ((bottomUp ? height - 1 - y : y) * stride) + (x * (bits / 8));
            return i + 2 < bmp.Length ? $"#{bmp[i + 2]:X2}{bmp[i + 1]:X2}{bmp[i]:X2}" : throw new ToolException("capture_failed", "The sample image is truncated.");
        }

        const int m = 2;
        return new Dictionary<string, string>
        {
            ["topLeft"] = At(m, m),
            ["top"] = At(width / 2, m),
            ["topRight"] = At(width - 1 - m, m),
            ["center"] = At(width / 2, height / 2),
            ["bottomLeft"] = At(m, height - 1 - m),
            ["bottom"] = At(width / 2, height - 1 - m),
            ["bottomRight"] = At(width - 1 - m, height - 1 - m),
        };
    }
}
