using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Conjuntos: ocurrencias (insertar, mover, girar, fijar, sustituir, ocultar y borrar), interferencias y lista de
/// materiales. Solid Edge fija (ground) cada ocurrencia que se inserta por el API; para colocarla con relaciones hay
/// que quitar esa fijación, cosa que hace se_relations con la pieza que se mueve.
/// </summary>
public static class AssemblyService
{
    internal static AssemblyDocument Assembly(SeContext context, string? documentHandle) =>
        context.GetDocument<AssemblyDocument>(documentHandle, "assembly");

    internal static Occurrence Occurrence(SeContext context, string? handle, string argumentName = "occurrence") =>
        string.IsNullOrWhiteSpace(handle)
            ? throw ToolException.InvalidArgument($"'{argumentName}' is required: an occurrence handle from se_assembly 'list' or 'insert'.")
            : context.Handles.Resolve<Occurrence>(handle, argumentName);

    /// <summary>Documento de pieza o chapa de una ocurrencia (no de un subconjunto).</summary>
    internal static ModelDocument PartOf(Occurrence occurrence)
    {
        if (occurrence.Subassembly)
        {
            throw new ToolException("wrong_occurrence", $"'{occurrence.Name}' is a subassembly; its geometry belongs to its parts.",
                "Open the subassembly (se_documents open) to work on its parts, or use a part occurrence of this assembly.");
        }

        return occurrence.OccurrenceDocument is SolidEdgeDocument document
            ? ModelDocument.From(document)
            : throw new ToolException("file_missing", $"The file of '{occurrence.Name}' is not loaded ({occurrence.OccurrenceFileName}).",
                "Check that the file exists, then reopen the assembly.");
    }

    /// <summary>
    /// Ocurrencias de primer nivel (y, con <paramref name="recursive"/>, las de los subconjuntos). Las suprimidas ya
    /// no están en Occurrences: salen al final con su handle de componente suprimido (para 'unsuppress').
    /// </summary>
    public static object List(SeContext context, string? documentHandle, bool recursive, int offset, int limit)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        HashSet<int> grounded = GroundedIds(assembly);
        var rows = new List<object>();
        foreach (Occurrence occurrence in Items<Occurrence>(assembly.Occurrences))
        {
            try
            {
                rows.Add(Describe(context, assembly, occurrence, grounded));
            }
            catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
            {
                rows.Add(new { handle = context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly), name = occurrence.Name, available = false, error = ex.Message });
                continue;
            }

            if (recursive && occurrence.Subassembly)
            {
                AddSubOccurrences(occurrence.SubOccurrences, occurrence.Name, rows);
            }
        }

        foreach (SuppressComponent component in SuppressedComponents(assembly))
        {
            rows.Add(new
            {
                handle = context.Register(component, "suppressed", (SolidEdgeDocument)assembly),
                name = component.Name,
                file = component.OccurrenceFileName,
                kind = KindOf(component.OccurrenceFileName),
                suppressed = true,
            });
        }

        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        List<object> page = rows.Skip(start).Take(take).ToList();
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            count = rows.Count,
            offset = start,
            returned = page.Count,
            more = start + page.Count < rows.Count,
            occurrences = page,
        };
    }

    /// <summary>Inserta un archivo (pieza, chapa o subconjunto) con posición (mm) y giros (grados) respecto del conjunto.</summary>
    public static object Insert(SeContext context, string? documentHandle, string? path, double[]? origin, double[]? angles, bool ground)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string file = context.Paths.Check(path, "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{file}' does not exist.", "Save the part first (se_documents save_as) or check the path.");
        }

        (double x, double y, double z) = Vector3(origin, "origin");
        (double ax, double ay, double az) = Vector3(angles, "angles");
        Occurrence occurrence = assembly.Occurrences.AddWithTransform(file, Units.MmToM(x), Units.MmToM(y), Units.MmToM(z),
            Units.DegToRad(ax), Units.DegToRad(ay), Units.DegToRad(az));
        if (!ground)
        {
            Unground(assembly, occurrence);
        }

        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    /// <summary>Tolerancia de posición al comprobar un movimiento (m): 0,0001 mm.</summary>
    private const double PlacementTolerance = 1e-7;

    /// <summary>
    /// Traslada (delta) o coloca (origen y giros absolutos) una ocurrencia y comprueba que ha llegado: si sus relaciones
    /// la retienen, se deja donde estaba y se avisa.
    /// </summary>
    public static object Move(SeContext context, string? handle, double[]? delta, double[]? origin, double[]? angles)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        occurrence.GetTransform(out double x0, out double y0, out double z0, out double ax0, out double ay0, out double az0);
        double[] before = Placement.Of(occurrence).Matrix;
        double x = x0, y = y0, z = z0, ax = ax0, ay = ay0, az = az0;
        if (delta is not null)
        {
            if (origin is not null || angles is not null)
            {
                throw ToolException.InvalidArgument("Pass either 'delta' (relative move) or 'origin'/'angles' (absolute placement), not both.");
            }

            // Occurrence.Move da E_FAIL en Solid Edge 2026 (comprobado); se recoloca el origen.
            (double dx, double dy, double dz) = Vector3(delta, "delta");
            (x, y, z) = (x0 + Units.MmToM(dx), y0 + Units.MmToM(dy), z0 + Units.MmToM(dz));
            occurrence.PutOrigin(x, y, z);
        }
        else if (origin is not null || angles is not null)
        {
            if (origin is not null)
            {
                (double ox, double oy, double oz) = Vector3(origin, "origin");
                (x, y, z) = (Units.MmToM(ox), Units.MmToM(oy), Units.MmToM(oz));
            }

            if (angles is not null)
            {
                (double rx, double ry, double rz) = Vector3(angles, "angles");
                (ax, ay, az) = (Units.DegToRad(rx), Units.DegToRad(ry), Units.DegToRad(rz));
            }

            occurrence.PutTransform(x, y, z, ax, ay, az);
        }
        else
        {
            throw ToolException.InvalidArgument("'move' needs 'delta' [dx,dy,dz] or 'origin' [x,y,z] / 'angles' [ax,ay,az].");
        }

        occurrence.GetTransform(out double x1, out double y1, out double z1, out _, out _, out _);
        bool placed = Math.Abs(x1 - x) <= PlacementTolerance && Math.Abs(y1 - y) <= PlacementTolerance && Math.Abs(z1 - z) <= PlacementTolerance;
        bool turnRequested = AngleDiffers(ax, ax0) || AngleDiffers(ay, ay0) || AngleDiffers(az, az0);
        bool turned = !turnRequested || RotationDiffers(before, Placement.Of(occurrence).Matrix);
        if (!placed || !turned)
        {
            occurrence.PutTransform(x0, y0, z0, ax0, ay0, az0);
            throw Held(occurrence);
        }

        return Placed(context, assembly, occurrence);
    }

    /// <summary>Gira una ocurrencia un ángulo (grados) alrededor del eje que pasa por dos puntos (mm, conjunto).</summary>
    public static object Rotate(SeContext context, string? handle, double[]? axis, double? angle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        List<(double X, double Y, double Z)> points = Geo.Points3(axis, "axis", 2);
        if (points.Count != 2)
        {
            throw ToolException.InvalidArgument("'axis' must be two points [x1,y1,z1, x2,y2,z2] in mm (assembly coordinates).");
        }

        double radians = Units.DegToRad(angle ?? throw ToolException.InvalidArgument("'rotate' needs 'angle' (degrees)."));
        double[] direction = [points[1].X - points[0].X, points[1].Y - points[0].Y, points[1].Z - points[0].Z];
        if (Geo.Length(direction) < 1e-9)
        {
            throw ToolException.InvalidArgument("The two 'axis' points must be different.");
        }

        // Occurrence.Rotate da E_FAIL en Solid Edge 2026 (comprobado); se calcula y se aplica la matriz girada.
        Placement start = Placement.Of(occurrence);
        double[] expected = start.Rotated([points[0].X, points[0].Y, points[0].Z], direction, radians);
        Array matrix = expected;
        occurrence.PutMatrix(ref matrix, true);
        double[] actual = Placement.Of(occurrence).Matrix;
        if (actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max() > PlacementTolerance)
        {
            Array original = start.Matrix;
            occurrence.PutMatrix(ref original, true);
            throw Held(occurrence);
        }

        return Placed(context, occurrence.TopLevelDocument, occurrence);
    }

    /// <summary>Fija o libera una ocurrencia (relación de fijación).</summary>
    public static object Ground(SeContext context, string? handle, bool ground)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        bool changed = ground
            ? !GroundedIds(assembly).Contains(occurrence.OccurrenceID) && assembly.Relations3d.AddGround(occurrence) is not null
            : Unground(assembly, occurrence);
        object description = Describe(context, assembly, occurrence, GroundedIds(assembly));
        return new { occurrence = description, changed };
    }

    /// <summary>Sustituye el archivo de una ocurrencia (o de todas las del mismo archivo).</summary>
    public static object Replace(SeContext context, string? handle, string? path, bool all)
    {
        Occurrence occurrence = Occurrence(context, handle);
        string file = context.Paths.Check(path, "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{file}' does not exist.");
        }

        AssemblyDocument assembly = occurrence.TopLevelDocument;
        occurrence.Replace(file, all);
        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    public static object SetVisible(SeContext context, string? handle, bool visible)
    {
        Occurrence occurrence = Occurrence(context, handle);
        occurrence.Visible = visible;
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    /// <summary>Suprime una ocurrencia: deja de cargarse y de contar, pero sigue en el conjunto para recuperarla.</summary>
    public static object Suppress(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        string name = occurrence.Name;
        assembly.SetSuppressComponent(occurrence, out SuppressComponent component);
        context.Handles.Remove(handle!, "the occurrence was suppressed; use the 'suppressed' handle to unsuppress it");
        context.ModelChanged((SolidEdgeDocument)assembly, "an occurrence was suppressed");
        return new { suppressed = name, handle = context.Register(component, "suppressed", (SolidEdgeDocument)assembly) };
    }

    /// <summary>Recupera una ocurrencia suprimida (handle de 'suppress' o de 'list').</summary>
    public static object Unsuppress(SeContext context, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw ToolException.InvalidArgument("'occurrence' is required: the suppressed handle from se_assembly 'suppress' or 'list'.");
        }

        SuppressComponent component = context.Handles.Resolve<SuppressComponent>(handle, "occurrence");
        AssemblyDocument assembly = context.GetDocument<AssemblyDocument>(context.Handles.GetScope(handle), "assembly");
        int id = component.OccurrenceID;
        component.UnSuppress();
        context.Handles.Remove(handle, "the occurrence was unsuppressed");
        context.ModelChanged((SolidEdgeDocument)assembly, "an occurrence was unsuppressed");
        Occurrence occurrence = Items<Occurrence>(assembly.Occurrences).FirstOrDefault(o => o.OccurrenceID == id)
            ?? throw new ToolException("unsuppress_failed", "Solid Edge did not bring the occurrence back.", "Check the assembly with se_assembly 'list'.");
        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    /// <summary>Quita la ocurrencia del conjunto (el archivo no se borra).</summary>
    public static object Delete(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        string name = occurrence.Name;
        occurrence.Delete();
        context.Handles.Remove(handle!, "the occurrence was deleted");
        return new { deleted = name };
    }

    /// <summary>
    /// Interferencias: las ocurrencias indicadas entre sí, o contra <paramref name="against"/>; sin ninguna, todas
    /// las de primer nivel entre sí.
    /// </summary>
    public static object Interference(SeContext context, string? documentHandle, string[]? occurrences, string[]? against)
    {
        AssemblyDocument assembly = occurrences is { Length: > 0 } ? Occurrence(context, occurrences[0], "occurrences").TopLevelDocument : Assembly(context, documentHandle);
        object[] set1 = occurrences is { Length: > 0 }
            ? occurrences.Select(h => (object)Occurrence(context, h, "occurrences")).ToArray()
            : Items<Occurrence>(assembly.Occurrences).Cast<object>().ToArray();
        object[]? set2 = against is { Length: > 0 } ? against.Select(h => (object)Occurrence(context, h, "against")).ToArray() : null;
        if (set2 is null && set1.Length < 2)
        {
            throw ToolException.InvalidArgument("Interference needs at least two occurrences, or 'against'.");
        }

        // 1: conjunto 1 contra conjunto 2; 4: conjunto 1 consigo mismo.
        int method = set2 is null ? 4 : 1;
        Array first = set1;

        // Las listas de salida del API vuelven vacías en Solid Edge 2026 (comprobado): los pares y el volumen común se
        // leen del informe (nombres + volumen = 1 + 8), que se escribe en una carpeta temporal propia y se borra.
        string report = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SolidEdgeMcp", $"interference-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
        InterferenceStatusConstants status;
        IReadOnlyList<InterferenceReport.Pair> found;
        try
        {
            object none1 = null!;
            object none2 = null!;
            object none3 = null!;
            assembly.CheckInterference(set1.Length, ref first, out status, method,
                set2 is null ? Missing.Value : set2.Length, set2 is null ? Missing.Value : set2, false, report, 9,
                out object _, ref none1, ref none2, ref none3, out object _);
            found = File.Exists(report) ? InterferenceReport.Parse(File.ReadAllText(report)) : [];
        }
        finally
        {
            TryDeleteFile(report);
        }

        Dictionary<string, Occurrence> byName = Items<Occurrence>(assembly.Occurrences)
            .GroupBy(o => o.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        object Named(string name) => new
        {
            name,
            handle = byName.TryGetValue(name, out Occurrence? occurrence) ? context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly) : null,
        };
        var pairs = found.Select(p => new { occurrences = new[] { Named(p.Occurrence1), Named(p.Occurrence2) }, volume = p.VolumeMm3 }).ToList();

        return new
        {
            status = status switch
            {
                InterferenceStatusConstants.seInterferenceStatusNoInterference => "none",
                InterferenceStatusConstants.seInterferenceStatusConfirmedInterference => "confirmed",
                InterferenceStatusConstants.seInterferenceStatusProbableInterference => "probable",
                InterferenceStatusConstants.seInterferenceStatusConfirmedAndProbableInterference => "confirmed_and_probable",
                _ => "incomplete",
            },
            checkedOccurrences = set1.Length + (set2?.Length ?? 0),
            interferences = pairs.Count,
            pairs,
            note = pairs.Count == 0 && status is not InterferenceStatusConstants.seInterferenceStatusNoInterference
                ? "Solid Edge reported interference but its report listed no pairs; check the occurrences in pairs or with se_view capture."
                : null,
        };
    }

    /// <summary>
    /// Lista de materiales. top: ocurrencias de primer nivel agrupadas por archivo (los subconjuntos cuentan como
    /// una línea); flat: solo piezas, con las cantidades multiplicadas por las de sus subconjuntos.
    /// </summary>
    public static object Bom(SeContext context, string? documentHandle, string structure)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        bool flat = structure switch
        {
            "top" => false,
            "flat" => true,
            _ => throw ToolException.InvalidArgument($"Unknown structure '{structure}'.", "Use top or flat."),
        };
        var lines = new Dictionary<string, BomLine>(StringComparer.OrdinalIgnoreCase);
        foreach (Occurrence occurrence in Items<Occurrence>(assembly.Occurrences))
        {
            if (!occurrence.IncludeInBom)
            {
                continue;
            }

            int quantity = Math.Max(occurrence.Quantity, 1);
            if (flat && occurrence.Subassembly)
            {
                AddFlat(occurrence.SubOccurrences, quantity, lines);
            }
            else
            {
                Count(lines, occurrence.OccurrenceFileName, occurrence.Subassembly, quantity);
            }
        }

        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            structure,
            lines = lines.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(l => new { item = l.Name, file = l.File, kind = l.Kind, quantity = l.Quantity }).ToList(),
            totalQuantity = lines.Values.Sum(l => l.Quantity),
        };
    }

    /// <summary>Quita las relaciones de fijación de una ocurrencia. Devuelve si había alguna.</summary>
    internal static bool Unground(AssemblyDocument assembly, Occurrence occurrence)
    {
        int id = occurrence.OccurrenceID;
        bool removed = false;
        foreach (object relation in Items<object>(assembly.Relations3d).ToList())
        {
            if (relation is GroundRelation3d ground && GroundedId(ground) == id)
            {
                ground.Delete();
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>
    /// Componentes suprimidos (ya no están en Occurrences). Con enlace temprano GetSuppressedComponents da E_POINTER
    /// (comprobado): Solid Edge necesita una matriz ya creada, cosa que 'out Array' no permite. Por IDispatch se le pasa
    /// una vacía, que debe ser de SuppressComponent: una de object da DISP_E_TYPEMISMATCH.
    /// </summary>
    internal static List<SuppressComponent> SuppressedComponents(AssemblyDocument assembly)
    {
        object?[] args = [(int)AssemblyComponentTypeConstants.seAssemblyComponentTypeAll, 0, Array.Empty<SuppressComponent>()];
        var modifier = new ParameterModifier(3);
        modifier[1] = true;
        modifier[2] = true;
        try
        {
            assembly.GetType().InvokeMember("GetSuppressedComponents", BindingFlags.InvokeMethod, null, assembly, args, [modifier],
                CultureInfo.InvariantCulture, null);
            return args[2] is Array components ? components.OfType<SuppressComponent>().ToList() : [];
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or NullReferenceException && !ComErrors.IsDisconnected(ex))
        {
            return [];
        }
    }

    /// <summary>Ocurrencias fijadas. La fijación de una ocurrencia suprimida no deja leer su ocurrencia (0x80020005).</summary>
    internal static HashSet<int> GroundedIds(AssemblyDocument assembly)
    {
        var ids = new HashSet<int>();
        foreach (GroundRelation3d ground in Items<object>(assembly.Relations3d).OfType<GroundRelation3d>())
        {
            if (GroundedId(ground) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static int? GroundedId(GroundRelation3d ground)
    {
        try
        {
            return ground.Occurrence.OccurrenceID;
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    /// <summary>Descripción con posición; marca si alguna relación (no de fijación) puede recolocarla.</summary>
    internal static object Placed(SeContext context, AssemblyDocument assembly, Occurrence occurrence)
    {
        // Mover una ocurrencia no cambia sus caras: los handles de ocurrencia siguen valiendo.
        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    internal static Dictionary<string, object?> Describe(SeContext context, AssemblyDocument assembly, Occurrence occurrence, HashSet<int> grounded)
    {
        occurrence.GetTransform(out double x, out double y, out double z, out double ax, out double ay, out double az);
        return new Dictionary<string, object?>
        {
            ["handle"] = context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly),
            ["name"] = occurrence.Name,
            ["file"] = occurrence.OccurrenceFileName,
            ["kind"] = occurrence.Subassembly ? "assembly" : KindOf(occurrence.OccurrenceFileName),
            ["origin"] = new[] { Geo.Mm(x), Geo.Mm(y), Geo.Mm(z) },
            ["angles"] = new[] { Geo.R(Units.RadToDeg(ax)), Geo.R(Units.RadToDeg(ay)), Geo.R(Units.RadToDeg(az)) },
            ["grounded"] = grounded.Contains(occurrence.OccurrenceID),
            ["status"] = StatusName(occurrence),
            ["visible"] = occurrence.Visible,
        };
    }

    internal static IEnumerable<T> Items<T>(object collection) => Late.Items(collection).OfType<T>();

    private static void AddSubOccurrences(SubOccurrences children, string parent, List<object> rows)
    {
        foreach (SubOccurrence child in Items<SubOccurrence>(children))
        {
            string path = $"{parent}/{child.Name}";
            rows.Add(new { name = path, file = child.SubOccurrenceFileName, kind = child.Subassembly ? "assembly" : KindOf(child.SubOccurrenceFileName), visible = child.Visible, nested = true });
            if (child.Subassembly)
            {
                AddSubOccurrences(child.SubOccurrences, path, rows);
            }
        }
    }

    private static void AddFlat(SubOccurrences children, int multiplier, Dictionary<string, BomLine> lines)
    {
        foreach (SubOccurrence child in Items<SubOccurrence>(children))
        {
            if (child.Subassembly)
            {
                AddFlat(child.SubOccurrences, multiplier, lines);
            }
            else
            {
                Count(lines, child.SubOccurrenceFileName, false, multiplier);
            }
        }
    }

    private static void Count(Dictionary<string, BomLine> lines, string file, bool subassembly, int quantity)
    {
        if (!lines.TryGetValue(file, out BomLine? line))
        {
            line = new BomLine(System.IO.Path.GetFileNameWithoutExtension(file), file, subassembly ? "assembly" : KindOf(file));
            lines.Add(file, line);
        }

        line.Quantity += quantity;
    }

    private static string KindOf(string file) => System.IO.Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".par" => "part",
        ".psm" => "sheet_metal",
        ".asm" => "assembly",
        _ => "other",
    };

    private static string StatusName(Occurrence occurrence)
    {
        try
        {
            return occurrence.Status switch
            {
                OccurrenceStatusConstants.seOccurrenceStatusWellDefined => "fully_constrained",
                OccurrenceStatusConstants.seOccurrenceStatusFixed => "fixed",
                OccurrenceStatusConstants.seOccurrenceStatusUnderDefined => "under_constrained",
                OccurrenceStatusConstants.seOccurrenceStatusOverDefined => "over_constrained",
                OccurrenceStatusConstants.seOccurrenceStatusNotConsistent => "inconsistent",
                _ => "unknown",
            };
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return "unknown";
        }
    }

    private static (double X, double Y, double Z) Vector3(double[]? values, string name)
    {
        if (values is null)
        {
            return (0, 0, 0);
        }

        if (values.Length != 3 || !values.All(double.IsFinite))
        {
            throw ToolException.InvalidArgument($"'{name}' must be [x, y, z].");
        }

        return (values[0], values[1], values[2]);
    }

    private static ToolException Held(Occurrence occurrence) => new("occurrence_held",
        $"'{occurrence.Name}' did not move: its relations hold it, so it was left where it was.",
        "See its relations with se_relations action 'list' and 'occurrence'; delete the ones that hold it, then move it again.");

    /// <summary>Ángulos distintos salvo vueltas completas (rad).</summary>
    private static bool AngleDiffers(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) > 1e-9;

    /// <summary>Si ha cambiado la parte de giro de la matriz (filas de los ejes).</summary>
    private static bool RotationDiffers(double[] before, double[] after) =>
        new[] { 0, 1, 2, 4, 5, 6, 8, 9, 10 }.Any(i => Math.Abs(before[i] - after[i]) > 1e-9);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Archivo temporal: si Solid Edge aún lo tiene abierto, queda en la carpeta temporal del usuario.
        }
    }

    private sealed class BomLine(string name, string file, string kind)
    {
        public string Name { get; } = name;

        public string File { get; } = file;

        public string Kind { get; } = kind;

        public int Quantity { get; set; }
    }
}
