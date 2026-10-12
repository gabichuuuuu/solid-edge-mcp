using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;
using IOPath = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Structural frames and pipes of an assembly, along line segments made from points (mm) or along lines of a sketch drawn in the
/// assembly. Verified: <c>StructuralFrames.Add(section, n, paths)</c> works in an unsaved assembly and adds one occurrence of the
/// section part per member, without writing files; a frame along two segments of 100 mm at a corner gives two members, mitred
/// (110 mm long each) and a cut length of 220 mm; <c>StructuralFrames.Delete</c> removes the members. <c>Pipes.Add</c> along
/// one segment gives one pipe segment occurrence, along two at a corner two segments and an elbow (cut lengths 398 + 398 mm for
/// two 500 mm legs, from GetPipeLengths; without an elbow the corner gets no fitting); <c>Pipe.Delete</c> removes them. The
/// section of a member (its part file) has its own length: the member's extent is read from its occurrence range. Saving the
/// assembly writes only the .asm (and .cfg): members and segments stay occurrences of the section and pipe parts.
/// Library parts of the installation (Frames, Piping Route) are copied into the work folder and used from there, so the
/// installation is only read.
/// </summary>
public static class FrameService
{
    private const string FramesLibrary = "Frames";
    private const string PipingLibrary = "Piping Route";

    /// <summary>
    /// Section parts (Frames) or pipe and fitting parts (Piping Route) installed with Solid Edge, as library names; or the
    /// parts of a library folder of your own (<paramref name="folder"/>, allowed folders), as absolute paths.
    /// </summary>
    public static object Library(SeContext context, string kind, string? filter, int offset, int limit, string? folder = null)
    {
        bool own = !string.IsNullOrWhiteSpace(folder);
        string root = own ? context.Paths.Check(folder, "folder") : LibraryRoot(kind == "pipes" ? PipingLibrary : FramesLibrary);
        if (own && !Directory.Exists(root))
        {
            throw ToolException.NotFound($"The folder does not exist: '{root}'.");
        }

        string[] names = Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*.par", SearchOption.AllDirectories)
                .Select(f => own ? f : LibraryName(root, f))
                .Where(n => string.IsNullOrWhiteSpace(filter) || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)]
            : [];
        int skip = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        string[] page = [.. names.Skip(skip).Take(take)];
        return new
        {
            kind,
            count = names.Length,
            offset = skip,
            returned = page.Length,
            more = skip + page.Length < names.Length,
            folder = root,
            parts = page,
            note = own
                ? "Pass a path as given to 'section', 'pipe', 'elbow'..."
                : "Pass a name as given (or the absolute path of a part of your own) to 'section', 'pipe', 'elbow'...; it is copied into the work folder and used from there.",
        };
    }

    /// <summary>Frames and pipes of the assembly with their members and segments.</summary>
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] frames = [.. Items<StructuralFrame>(assembly.StructuralFrames).Select(f => DescribeFrame(context, assembly, f))];
        object[] pipes = [.. Items<Pipe>(assembly.Pipes).Select(p => DescribePipe(context, assembly, p, null, null))];
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), frames, pipes };
    }

    /// <summary>
    /// Structural frame of <paramref name="section"/> along a polyline (<paramref name="points"/>, mm) or along sketch lines,
    /// with an optional end condition at the corners. Checked by its cut length and its new member occurrences.
    /// </summary>
    public static object Frame(SeContext context, string? documentHandle, string? section, double[]? points, string[]? lines, string? endCondition, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string file = LibraryPart(context, section, FramesLibrary, "section");
        StructuralFrameEndConditionConstants? condition = endCondition switch
        {
            null => null,
            "miter" => StructuralFrameEndConditionConstants.seMiter,
            "butt1" => StructuralFrameEndConditionConstants.seButt1,
            "butt2" => StructuralFrameEndConditionConstants.seButt2,
            "none" => StructuralFrameEndConditionConstants.seNone,
            _ => throw ToolException.InvalidArgument($"Unknown endCondition '{endCondition}'.", "Use miter, butt1, butt2 or none."),
        };
        CheckName(assembly, name);
        (object[] paths, object[] made) = Paths(context, assembly, points, lines);
        StructuralFrames frames = assembly.StructuralFrames;
        int before = frames.Count;
        HashSet<string> existing = OccurrenceNames(assembly);
        AssemblyFeatureService.PrepareForFrame(assembly);
        string? environment = Late.TryGetString(context.Application, "ActiveEnvironment");
        StructuralFrame frame;
        try
        {
            Array pathArray = paths;
            frame = condition is { } c
                ? frames.Add(file, paths.Length, ref pathArray, c)
                : frames.Add(file, paths.Length, ref pathArray);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            LeaveFrameEnvironment(context, environment);
            RemoveFramesSince(frames, before);
            RemoveSegments(assembly, made);
            throw new ToolException("feature_failed", $"Solid Edge did not make the frame: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check that the section is a part with a sketch or face to sweep (a library section, se_frame library) and that the path is connected.");
        }

        LeaveFrameEnvironment(context, environment);
        frame.CutLength(out double cutLength, out _);
        Occurrence[] members = Members(assembly, frame);
        if (cutLength <= 0 || members.Length == 0 || members.All(m => existing.Contains(m.Name)))
        {
            TryDeleteFrame(frames, frame);
            RemoveSegments(assembly, made);
            throw new ToolException("feature_failed", $"Solid Edge made no frame member (cut length {Geo.Mm(cutLength)} mm) and the frame was removed.",
                "Check the section part and the path.");
        }

        if (name is not null)
        {
            frame.Name = name;
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "frame");
        return DescribeFrame(context, assembly, frame);
    }

    /// <summary>
    /// Pipe of outside diameter <paramref name="diameter"/> (mm) along a polyline or sketch lines, with the pipe part and the
    /// fittings it needs at corners (elbow), branches (tee, cross) or joints (coupling). Checked by its segment occurrences.
    /// </summary>
    public static object Pipe(SeContext context, string? documentHandle, double? diameter, double[]? points, string[]? lines, PipeParts parts, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        double outside = Units.MmToM(Units.RequirePositive(diameter ?? throw ToolException.InvalidArgument(
            "'diameter' is required: the outside diameter of the pipe in mm (e.g. 114.3 for DIN 100)."), "diameter"));
        string pipeFile = LibraryPart(context, parts.Pipe, PipingLibrary, "pipe");
        string Optional(string? value, string argument) => string.IsNullOrWhiteSpace(value) ? "" : LibraryPart(context, value, PipingLibrary, argument);
        string elbow = Optional(parts.Elbow, "elbow");
        string tee = Optional(parts.Tee, "tee");
        string cross = Optional(parts.Cross, "cross");
        string coupling = Optional(parts.Coupling, "coupling");
        CheckName(assembly, name);
        (object[] paths, object[] made) = Paths(context, assembly, points, lines);
        Pipes pipes = assembly.Pipes;
        int before = pipes.Count;
        HashSet<string> existing = OccurrenceNames(assembly);
        Pipe pipe;
        try
        {
            Array pathArray = paths;
            pipe = pipes.Add(outside, paths.Length, ref pathArray, false, 0, pipeFile, elbow, tee, cross, coupling);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            RemovePipesSince(pipes, before);
            RemoveSegments(assembly, made);
            throw new ToolException("feature_failed", $"Solid Edge did not make the pipe: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the diameter against the pipe part, and give an 'elbow' when the path turns (se_frame library kind 'pipes').");
        }

        Occurrence[] segments = Segments(pipe);
        if (segments.Length == 0)
        {
            TryDelete(pipe);
            RemoveSegments(assembly, made);
            throw new ToolException("feature_failed", "Solid Edge made no pipe segment and the pipe was removed.",
                "Check the diameter against the pipe part, and give an 'elbow' when the path turns.");
        }

        if (name is not null)
        {
            pipe.Name = name;
        }

        HashSet<string> segmentNames = [.. segments.Select(s => s.Name)];
        Occurrence[] fittings = [.. Items<Occurrence>(assembly.Occurrences).Where(o => !existing.Contains(o.Name) && !segmentNames.Contains(o.Name))];
        context.ModelChanged((SolidEdgeDocument)assembly, "pipe");
        string[]? warnings = string.IsNullOrWhiteSpace(parts.Elbow) && Turns(points)
            ? ["The path turns but no 'elbow' was given: Solid Edge leaves the corners without fittings."]
            : null;
        return DescribePipe(context, assembly, pipe, fittings, warnings);
    }

    /// <summary>Deletes a frame (with its members) or a pipe (with its segments and fittings) by name.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required: the frame or pipe name (action 'list').");
        }

        if (Items<StructuralFrame>(assembly.StructuralFrames).FirstOrDefault(f => Same(f.Name, name)) is { } frame)
        {
            assembly.StructuralFrames.Delete(frame);
        }
        else if (Items<Pipe>(assembly.Pipes).FirstOrDefault(p => Same(p.Name, name)) is { } pipe)
        {
            pipe.Delete();
        }
        else
        {
            throw ToolException.InvalidArgument($"The assembly has no frame or pipe '{name}'.", "List them with action 'list'.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "delete");
        return Exists(assembly, name)
            ? throw new ToolException("feature_failed", $"Solid Edge did not delete '{name}'.", "Check it with action 'list'.")
            : new { deleted = name, frames = assembly.StructuralFrames.Count, pipes = assembly.Pipes.Count, occurrences = assembly.Occurrences.Count };
    }

    /// <summary>
    /// Repairs an assembly whose frames were made before any assembly feature (outside se_frame), where Solid Edge refuses
    /// assembly features for good: deletes its frames (verified: the AssemblyFeatures property works again), prepares the
    /// assembly as se_frame does and makes every frame again with the same section, path, end condition and name. The
    /// members are new occurrences.
    /// </summary>
    public static object Repair(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        // Internal name of the assembly environment (an API identifier, the same in every language and theme): frames made
        // elsewhere may have left Solid Edge in the Frame environment.
        const string environment = "Assembly";
        if (AssemblyFeatureService.FeaturesAvailable(assembly))
        {
            return new { repaired = false, frames = assembly.StructuralFrames.Count, note = "Assembly features already work in this assembly: nothing to repair." };
        }

        StructuralFrames frames = assembly.StructuralFrames;
        var definitions = new List<(string Name, string Section, object[] Paths, StructuralFrameEndConditionConstants? Condition)>();
        foreach (StructuralFrame frame in Items<StructuralFrame>(frames))
        {
            string? section = Members(assembly, frame).Select(m => m.OccurrenceFileName).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
            object[] paths = FramePath(frame);
            if (section is null || paths.Length == 0)
            {
                throw new ToolException("solid_edge_limitation", $"The section or the path of frame '{frame.Name}' cannot be read, so it cannot be made again.",
                    "Delete the frame (se_frame delete) and make it again with se_frame frame.");
            }

            StructuralFrameEndConditionConstants? condition = null;
            try
            {
                frame.GetGlobalEndCondition(out StructuralFrameEndConditionConstants value, out _);
                condition = value;
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                // Made again with Solid Edge's default.
            }

            definitions.Add((frame.Name, section, paths, condition));
        }

        foreach (StructuralFrame frame in Items<StructuralFrame>(frames).ToList())
        {
            frames.Delete(frame);
        }

        LeaveFrameEnvironment(context, environment);
        if (!AssemblyFeatureService.FeaturesAvailable(assembly))
        {
            throw new ToolException("solid_edge_limitation", "Solid Edge still refuses the assembly features after deleting the frames; they were not made again.",
                "Close the assembly without saving to get the frames back.");
        }

        AssemblyFeatureService.PrepareForFrame(assembly);
        var made = new List<object>();
        foreach ((string name, string section, object[] paths, StructuralFrameEndConditionConstants? condition) in definitions)
        {
            Array pathArray = paths;
            StructuralFrame frame = condition is { } c
                ? frames.Add(section, paths.Length, ref pathArray, c)
                : frames.Add(section, paths.Length, ref pathArray);
            LeaveFrameEnvironment(context, environment);
            if (!string.Equals(frame.Name, name, StringComparison.Ordinal))
            {
                try
                {
                    frame.Name = name;
                }
                catch (COMException ex) when (!ComErrors.IsGone(ex))
                {
                    // Solid Edge keeps its own name for the frame made again.
                }
            }

            made.Add(DescribeFrame(context, assembly, frame));
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "repair");
        return new { repaired = true, frames = made };
    }

    /// <summary>Path objects of a frame (line segments, sketch lines). Verified: GetFramePathObjects gives them, GetFramePath DISP_E_TYPEMISMATCH.</summary>
    private static object[] FramePath(StructuralFrame frame)
    {
        try
        {
            Array segments = Array.Empty<object>();
            frame.GetFramePathObjects(out _, ref segments);
            return [.. segments.Cast<object>()];
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException or InvalidCastException && !ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    private static object DescribeFrame(SeContext context, AssemblyDocument assembly, StructuralFrame frame)
    {
        double? cutLength = null;
        try
        {
            frame.CutLength(out double length, out _);
            cutLength = Geo.Mm(length);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Reported without it.
        }

        string? condition = null;
        try
        {
            frame.GetGlobalEndCondition(out StructuralFrameEndConditionConstants value, out _);
            condition = value.ToString().Replace("se", "", StringComparison.Ordinal).ToLowerInvariant();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Reported without it.
        }

        return new
        {
            name = frame.Name,
            type = "frame",
            cutLength,
            sectionCutLengths = SectionCutLengths(frame),
            endCondition = condition,
            members = Members(assembly, frame).Select(m => DescribeOccurrence(context, assembly, m)).ToArray(),
            note = "Each member is an occurrence of the section part; its extent is the boundingBox (the part file keeps its own length).",
        };
    }

    private static object DescribePipe(SeContext context, AssemblyDocument assembly, Pipe pipe, Occurrence[]? fittings, string[]? warnings)
    {
        Dictionary<string, double>? lengths = SegmentLengths(pipe);
        return new
        {
            name = pipe.Name,
            type = "pipe",
            segments = Segments(pipe).Select(s => new
            {
                segment = DescribeOccurrence(context, assembly, s),
                length = lengths is not null && lengths.TryGetValue(s.Name, out double length) ? Geo.Mm(length) : (double?)null,
            }).ToArray(),
            fittings = (fittings ?? Fittings(pipe))?.Select(f => DescribeOccurrence(context, assembly, f)).ToArray(),
            note = "length is the cut length of each segment; the segment occurrence shows the pipe part with its own length.",
            warnings,
        };
    }

    /// <summary>Whether a polyline (mm) changes direction at any of its points.</summary>
    private static bool Turns(double[]? points)
    {
        if (points is not { Length: >= 9 })
        {
            return false;
        }

        for (int i = 3; i + 3 < points.Length; i += 3)
        {
            double[] a = [points[i] - points[i - 3], points[i + 1] - points[i - 2], points[i + 2] - points[i - 1]];
            double[] b = [points[i + 3] - points[i], points[i + 4] - points[i + 1], points[i + 5] - points[i + 2]];
            if (Geo.Length(Geo.Cross(a, b)) > 1e-9 * Math.Max(Geo.Length(a) * Geo.Length(b), 1e-12))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Length of each pipe segment by occurrence name (m), or null if Solid Edge does not give them.</summary>
    private static Dictionary<string, double>? SegmentLengths(Pipe pipe)
    {
        try
        {
            Array occurrences = Array.Empty<object>();
            Array lines = Array.Empty<object>();
            Array lengths = Array.Empty<double>();
            pipe.GetPipeLengths(out _, ref occurrences, ref lines, ref lengths);
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            double[] values = [.. Geo.Doubles(lengths)];
            Occurrence[] items = [.. occurrences.OfType<Occurrence>()];
            for (int i = 0; i < Math.Min(items.Length, values.Length); i++)
            {
                result[items[i].Name] = values[i];
            }

            return result;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException or InvalidCastException && !ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    private static object DescribeOccurrence(SeContext context, AssemblyDocument assembly, Occurrence occurrence)
    {
        double[]? min = null;
        double[]? max = null;
        try
        {
            Array lo = new double[3];
            Array hi = new double[3];
            occurrence.GetRangeBox(ref lo, ref hi);
            min = Geo.Mm(lo);
            max = Geo.Mm(hi);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Reported without it.
        }

        return new
        {
            occurrence = context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly),
            name = occurrence.Name,
            boundingBox = min is null ? null : new { min, max, size = max!.Zip(min, (a, b) => Geo.R(a - b)).ToArray() },
        };
    }

    /// <summary>
    /// Cut length of each member section (mm), or null if Solid Edge does not give them. Verified through the typed interface
    /// (IDispatch gives DISP_E_TYPEMISMATCH): two mitred members of 110 give [110, 110]; ReturnOccurrenceForGivenSectionID
    /// returns no occurrence, so the lengths are not matched to the members.
    /// </summary>
    private static double[]? SectionCutLengths(StructuralFrame frame)
    {
        try
        {
            Array sections = Array.Empty<int>();
            frame.GetSections(out _, ref sections);
            return [.. sections.Cast<object>().Select(id =>
            {
                frame.SegmentCutLength(Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture), out double length, out _);
                return Geo.Mm(length);
            })];
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException or InvalidCastException && !ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Members of a frame: the occurrences Solid Edge maps back to it.</summary>
    private static Occurrence[] Members(AssemblyDocument assembly, StructuralFrame frame)
    {
        string name = frame.Name;
        return [.. Items<Occurrence>(assembly.Occurrences).Where(o =>
        {
            try
            {
                return o.IsStructuralFrameItem && assembly.StructuralFrames.GetStructuralFrameByOccurrence(o) is { } owner && owner.Name == name;
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                return false;
            }
        })];
    }

    private static Occurrence[] Segments(Pipe pipe)
    {
        try
        {
            Array occurrences = Array.Empty<object>();
            pipe.GetPipeSegments(out _, ref occurrences);
            return [.. occurrences.OfType<Occurrence>()];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    /// <summary>Fittings of a pipe, or null if Solid Edge does not give them (GetPipeFittings failed through IDispatch, r183).</summary>
    private static Occurrence[]? Fittings(Pipe pipe)
    {
        try
        {
            Array occurrences = Array.Empty<object>();
            Array types = Array.Empty<int>();
            pipe.GetPipeFittings(out _, ref occurrences, ref types);
            return [.. occurrences.OfType<Occurrence>()];
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException or InvalidCastException && !ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Goes back to the environment the assembly was in (<paramref name="before"/>) when a frame call left Solid Edge in
    /// another one (verified: StructuralFrames.Add opens the Frame environment, as the Frame command does), with the command
    /// that closes a sub-environment, as a person would; its id comes from the interop's AssemblyCommandConstants.
    /// </summary>
    private static void LeaveFrameEnvironment(SeContext context, string? before)
    {
        Application app = context.Application;
        string? current = Late.TryGetString(app, "ActiveEnvironment");
        if (before is null || current is null || string.Equals(current, before, StringComparison.Ordinal))
        {
            return;
        }

        app.StartCommand((SolidEdgeCommandConstants)EnvironmentExitCommand());
        for (int i = 0; i < 50 && !string.Equals(Late.TryGetString(app, "ActiveEnvironment"), before, StringComparison.Ordinal); i++)
        {
            Thread.Sleep(100);
        }
    }

    private static int EnvironmentExitCommand()
    {
        Type? commands = InteropCatalog.Enums.FirstOrDefault(e => e.Name == "AssemblyCommandConstants");
        return commands is not null && Enum.GetNames(commands).Contains("AssemblyEnvironmentsExit")
            ? Convert.ToInt32(Enum.Parse(commands, "AssemblyEnvironmentsExit"), System.Globalization.CultureInfo.InvariantCulture)
            : 10231;
    }

    private static string LibraryName(string root, string file) =>
        IOPath.ChangeExtension(IOPath.GetRelativePath(root, file), null).Replace(IOPath.DirectorySeparatorChar, '/');

    /// <summary>
    /// Path objects: one line segment per pair of consecutive points (mm, assembly coordinates), or lines of a sketch drawn
    /// in the assembly (se_sketch line) or of a 3D sketch.
    /// </summary>
    private static (object[] Paths, object[] Made) Paths(SeContext context, AssemblyDocument assembly, double[]? points, string[]? lines)
    {
        if (lines is { Length: > 0 })
        {
            if (points is { Length: > 0 })
            {
                throw ToolException.InvalidArgument("Give either 'points' or 'lines', not both.");
            }

            object[] given = [.. lines.Select(h => context.Handles.Resolve(h, "lines") is var line && line is SolidEdgeFrameworkSupport.Line2d or SolidEdgePart.Line3D or LineSegment
                ? line
                : throw new ToolException("handle_wrong_type", $"'{h}' is not a line.", "Pass lines drawn with se_sketch line in the assembly."))];
            return (given, []);
        }

        List<(double X, double Y, double Z)> path = Geo.Points3(points, "points", 2); // metres
        for (int i = 1; i < path.Count; i++)
        {
            if (Geo.Distance([path[i].X, path[i].Y, path[i].Z], [path[i - 1].X, path[i - 1].Y, path[i - 1].Z]) < 1e-6)
            {
                throw ToolException.InvalidArgument($"Points {i} and {i + 1} are the same: every segment needs two different points.");
            }
        }

        LineSegments segments = assembly.LineSegments;
        var created = new List<object>();
        for (int i = 1; i < path.Count; i++)
        {
            Array start = new[] { path[i - 1].X, path[i - 1].Y, path[i - 1].Z };
            Array end = new[] { path[i].X, path[i].Y, path[i].Z };
            created.Add(segments.Add(ref start, ref end));
        }

        return ([.. created], [.. created]);
    }

    /// <summary>
    /// A part given by absolute path (allowed folders) or by its name in an installed library (as action 'library' lists it),
    /// which is copied into the work folder and used from there (the installation is never written).
    /// </summary>
    private static string LibraryPart(SeContext context, string? value, string library, string argument)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ToolException.InvalidArgument($"'{argument}' is required: a library name (se_frame library) or the absolute path of a part.");
        }

        if (IOPath.IsPathFullyQualified(value))
        {
            string checkedPath = context.Paths.Check(value, argument);
            return File.Exists(checkedPath) ? checkedPath : throw ToolException.NotFound($"The file does not exist: '{checkedPath}'.");
        }

        string root = LibraryRoot(library);
        string relative = value.Replace('/', '\\').TrimStart('\\');
        if (!relative.EndsWith(".par", StringComparison.OrdinalIgnoreCase))
        {
            relative += ".par";
        }

        string source = IOPath.GetFullPath(IOPath.Combine(root, relative));
        if (!source.StartsWith(root + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
        {
            string[] known = Directory.Exists(root)
                ? [.. Directory.EnumerateFiles(root, "*.par", SearchOption.AllDirectories).Select(f => LibraryName(root, f))]
                : [];
            throw ToolException.NotFound($"'{value}' is not a part of the {library} library.{NameMatch.Hint(known, value)}",
                $"List them with se_frame library (kind {(library == FramesLibrary ? "frames" : "pipes")}), or pass the absolute path of a part.");
        }

        string target = IOPath.Combine(context.AppPaths.Work, "library", library, relative);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(target)!);
            File.Copy(source, target);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }

        return context.Paths.Check(target, argument);
    }

    /// <summary>Library folder of the installation: next to the Program folder of Edge.exe (COM registry).</summary>
    private static string LibraryRoot(string library)
    {
        string? program = IOPath.GetDirectoryName(SolidEdgeLauncher.FindExecutable() ?? "");
        string? install = string.IsNullOrEmpty(program) ? null : Directory.GetParent(program)?.FullName;
        return install is null
            ? throw ToolException.NotFound("The Solid Edge installation folder could not be found.", "Pass the absolute path of a part of your own.")
            : IOPath.Combine(install, library);
    }

    private static void CheckName(AssemblyDocument assembly, string? name)
    {
        if (name is not null && Exists(assembly, name))
        {
            throw ToolException.InvalidArgument($"The assembly already has a frame or pipe named '{name}'.", "Pass another 'name'.");
        }
    }

    private static bool Exists(AssemblyDocument assembly, string name) =>
        Items<StructuralFrame>(assembly.StructuralFrames).Any(f => Same(f.Name, name)) || Items<Pipe>(assembly.Pipes).Any(p => Same(p.Name, name));

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> OccurrenceNames(AssemblyDocument assembly) =>
        [.. Items<Occurrence>(assembly.Occurrences).Select(o => o.Name)];

    /// <summary>
    /// Removes the line segments made for a path that failed. LineSegment has no Delete; deleting them through the select
    /// set works (verified: LineSegments.Count went back from 2 to 1).
    /// </summary>
    private static void RemoveSegments(AssemblyDocument assembly, object[] segments)
    {
        if (segments.Length == 0)
        {
            return;
        }

        try
        {
            SelectSet select = ((SolidEdgeDocument)assembly).SelectSet;
            select.RemoveAll();
            foreach (object segment in segments)
            {
                select.Add(segment);
            }

            select.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }

    private static void RemoveFramesSince(StructuralFrames frames, int before)
    {
        for (int i = frames.Count; i > before; i--)
        {
            TryDeleteFrame(frames, frames.Item(i));
        }
    }

    private static void RemovePipesSince(Pipes pipes, int before)
    {
        for (int i = pipes.Count; i > before; i--)
        {
            TryDelete(pipes.Item(i));
        }
    }

    private static void TryDeleteFrame(StructuralFrames frames, StructuralFrame frame)
    {
        try
        {
            frames.Delete(frame);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }

    private static void TryDelete(Pipe pipe)
    {
        try
        {
            pipe.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }
}

/// <summary>Parts of a pipe: the pipe and the fittings, each a library name or an absolute path.</summary>
public sealed record PipeParts(string? Pipe, string? Elbow, string? Tee, string? Cross, string? Coupling);
