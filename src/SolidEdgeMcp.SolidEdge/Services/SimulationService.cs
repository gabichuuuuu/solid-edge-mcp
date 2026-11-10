// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_simulation does: the action and its inputs, in the units of the tools.</summary>
public sealed record SimulationSpec(SimulationAction Action, string? Study = null, StudyType? Type = null, int? Modes = null, string[]? Bodies = null,
    string[]? Elements = null, SimulationConstraint? Constraint = null, SimulationLoad? Load = null, double? Value = null, double[]? Direction = null,
    ResultQuantity? Quantity = null, ResultComponent? Component = null, int? Mode = null, string? Path = null);

/// <summary>A result of se_simulation results: its values and, when asked, the picture of the plot.</summary>
public sealed record SimulationPlot(object Details, byte[]? Image, string? MimeType);

/// <summary>
/// Simulation studies of a part or sheet metal document (Simulation tab, solved by Simcenter Nastran). Verified in Solid Edge
/// 2026 Foundation with a 100 x 20 x 10 mm steel cantilever:
/// <list type="bullet">
/// <item>AddStudy makes the study, but Solve neither meshes nor solves until Study.SetGeometries gives it the bodies.</item>
/// <item>Solve(document, 0) meshes and solves (the Simulation Results environment opens); GetStudyStatus(eStudyIsSolved) then
/// reads true. Plots are only reachable after ViewResults (before it, GetActivePlotOwner gives E_POINTER).</item>
/// <item>Results: ResultsOwner, its active ModesOwner, a Mode (one per natural or buckling mode), one PlotsOwner per
/// quantity (displacement, applied force, constraint force, stress, strain) and one Plot per component, in m and Pa.
/// Displacement plots 1-4 are magnitude, X, Y, Z; stress plots 60010-60015 are XX, YY, ZZ, XY, YZ, ZX and 60031 von Mises.</item>
/// <item>100 N on the free end: 0.0995 mm and 29.2 MPa (beam theory 0.100 mm and 30 MPa); 0.1 MPa on the top face:
/// 200 N applied and reacted; first two modes 817.9 and 1594 Hz (Euler-Bernoulli 816 and 1632 Hz). Stress components: a
/// pull along y or z gives the yy or zz plot the load over the section, a shear along x or y on the top face the zx or yz.</item>
/// <item>The natural frequencies, buckling factors and total load and reaction are not in the API: they are read from the
/// .f06 that Nastran writes in the scratch folder of the study owner. GetNastranErrorMessage reports a fatal error even after
/// a solve that worked, so it is not used.</item>
/// <item>A positive pressure pulls the face outwards (its load resultant points along the outward normal) and SetFlip does
/// not change what is solved, so the value is given negative: a positive pressure here presses on the face.</item>
/// <item>Simulation Express (in Foundation) has fixed constraints, force and pressure loads, static and modal studies; the
/// other kinds show a licence message (a message box without an owner) when they are solved, or made for buckling.</item>
/// </list>
/// </summary>
public static class SimulationService
{
    public static readonly IReadOnlyList<string> ConstraintKinds = ["fixed", "pinned", "no_rotation", "sliding", "cylindrical"];
    public static readonly IReadOnlyList<string> LoadKinds = ["force", "pressure"];

    /// <summary>Results kept by a new study: displacement, applied force, constraint force, stress and strain (FEAResultOptions_Auto).</summary>
    private const uint ResultOptions = 1 | 2 | 4 | 4096 | 8192;

    /// <summary>Close Simulation Results, on every tab of the results environment.</summary>
    private const int CloseResultsCommand = 10231;

    /// <summary>
    /// The Simulation ribbon button of each constraint and load. Simulation Express (in Solid Edge Foundation) knows only
    /// fixed, force and pressure: the other buttons stay on the ribbon, but CommandEnabled calls them unknown, and a study that
    /// has one shows "You do not have a valid license to use this command" when it is solved and then fails with E_FAIL.
    /// Torque and gravity are only checked in studies made in Solid Edge: se_simulation does not add them, because their
    /// options could not be verified with that licence.
    /// </summary>
    private static readonly Dictionary<string, int> KindCommands = new(StringComparer.Ordinal)
    {
        ["fixed"] = 10528,
        ["pinned"] = 10529,
        ["no_rotation"] = 10530,
        ["sliding"] = 10531,
        ["cylindrical"] = 10532,
        ["force"] = 10499,
        ["pressure"] = 10506,
        ["torque"] = 10521,
        ["gravity"] = 10522,
    };

    private static readonly (ResultQuantity Name, FEAPlotsOwnerTypeEnum_Auto Owner, double Scale, string Unit, (ResultComponent Component, int Code)[] Components)[] Quantities =
    [
        (ResultQuantity.Displacement, FEAPlotsOwnerTypeEnum_Auto.ePlotsOwnerTypeDisplacement_Auto, 1000, "mm",
            [(ResultComponent.Magnitude, 1), (ResultComponent.X, 2), (ResultComponent.Y, 3), (ResultComponent.Z, 4)]),
        (ResultQuantity.Stress, FEAPlotsOwnerTypeEnum_Auto.ePlotsOwnerTypeStress_Auto, 1e-6, "MPa",
            [(ResultComponent.VonMises, 60031), (ResultComponent.Xx, 60010), (ResultComponent.Yy, 60011), (ResultComponent.Zz, 60012),
                (ResultComponent.Xy, 60013), (ResultComponent.Yz, 60014), (ResultComponent.Zx, 60015)]),
    ];

    public static object Run(SeContext context, string? documentHandle, SimulationSpec spec)
    {
        SolidEdgeDocument document = Document(context, documentHandle, spec);
        if (spec.Action is SimulationAction.Solve or SimulationAction.CloseResults)
        {
            document.Activate();
        }

        return spec.Action switch
        {
            SimulationAction.List => List(context, document),
            SimulationAction.Create => Create(context, document, spec),
            SimulationAction.Constrain => Constrain(context, document, spec),
            SimulationAction.Load => Load(context, document, spec),
            SimulationAction.Solve => Solve(context, document, spec),
            SimulationAction.CloseResults => CloseResults(context, document),
            SimulationAction.Delete => Delete(context, document, spec),
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
    }

    private static SolidEdgeDocument Document(SeContext context, string? documentHandle, SimulationSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(documentHandle))
        {
            return context.GetDocument(documentHandle);
        }

        string? related = spec.Study is { } study && study.Contains(':', StringComparison.Ordinal) ? study
            : spec.Elements?.FirstOrDefault() ?? spec.Bodies?.FirstOrDefault();
        return related is null ? context.GetDocument(null) : context.DocumentOf(related);
    }

    private static StudyOwner Owner(SolidEdgeDocument document) => document switch
    {
        PartDocument part => part.StudyOwner,
        SheetMetalDocument sheetMetal => sheetMetal.StudyOwner,
        _ => throw new ToolException("wrong_document_type",
            $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}: these simulation studies belong to part and sheet metal documents.",
            "Activate or pass a part or sheet metal document (studies of assemblies are set up in Solid Edge)."),
    };

    private static List<Study> Studies(StudyOwner owner)
    {
        var studies = new List<Study>();
        for (int i = 1, count = owner.Count; i <= count; i++)
        {
            studies.Add(owner.Item(i));
        }

        return studies;
    }

    private static string NameOf(Study study)
    {
        study.GetStudyName(out string name);
        return name;
    }

    /// <summary>The study given by handle, name or 1-based index; without one, the only study or the active one.</summary>
    private static Study Find(SeContext context, StudyOwner owner, string? key)
    {
        List<Study> studies = Studies(owner);
        if (studies.Count == 0)
        {
            throw ToolException.NotFound("The document has no simulation study.", "Create one with se_simulation create.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            if (studies.Count == 1)
            {
                return studies[0];
            }

            throw ToolException.InvalidArgument($"The document has {studies.Count} studies ({string.Join(", ", studies.Select(NameOf))}): pass 'study'.");
        }

        if (key.Contains(':', StringComparison.Ordinal))
        {
            return context.Handles.Resolve<Study>(key, "study");
        }

        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return index >= 1 && index <= studies.Count ? studies[index - 1]
                : throw ToolException.NotFound($"There is no study {index}: the document has {studies.Count}.", "Studies are numbered from 1 (se_simulation list).");
        }

        string[] names = studies.Select(NameOf).ToArray();
        string name = NameMatch.Resolve(names, key)
            ?? throw ToolException.NotFound($"There is no study '{key}'.{NameMatch.Hint(names, key)}", "Use se_simulation list.");
        return studies[Array.IndexOf(names, name)];
    }

    private static object List(SeContext context, SolidEdgeDocument document)
    {
        List<Study> studies = Studies(Owner(document));
        (string[] constraints, string[] loads) = LicensedKinds(context, document);
        return new
        {
            document = context.DocumentHandle(document),
            count = studies.Count,
            studies = studies.Select((s, i) => Describe(context, document, s, i + 1)).ToArray(),
            licensed = new { constraints, loads },
        };
    }

    private static object Describe(SeContext context, SolidEdgeDocument document, Study study, int? index = null)
    {
        study.GetStudyType(out FEAStudyTypeEnum_Auto type);
        study.GetMeshType(out FEAMeshTypeEnum_Auto mesh);
        int? modes = null;
        if (type != FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto)
        {
            study.GetNastranOptions(out _, out uint count, out _, out _, out _, out _);
            modes = (int)count;
        }

        return new
        {
            index,
            name = NameOf(study),
            type = TypeName(type),
            mesh = mesh switch
            {
                FEAMeshTypeEnum_Auto.eMeshTypeTetrahedral_Auto => "tetrahedral",
                FEAMeshTypeEnum_Auto.eMeshType2D_Auto => "surface",
                FEAMeshTypeEnum_Auto.eMeshTypeMixed_Auto => "mixed",
                FEAMeshTypeEnum_Auto.eMeshTypeBeam_Auto => "beam",
                _ => mesh.ToString(),
            },
            modes,
            state = State(study),
            errors = Errors(study),
            bodies = Geometries(study).Length,
            constraints = Constraints(study).Select(DescribeConstraint).ToArray(),
            loads = Loads(study).Select(DescribeLoad).ToArray(),
            handle = context.Register(study, "study", document),
        };
    }

    private static string TypeName(FEAStudyTypeEnum_Auto type) => type switch
    {
        FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto => "static",
        FEAStudyTypeEnum_Auto.eStudyTypeNormalModal_Auto => "modal",
        FEAStudyTypeEnum_Auto.eStudyTypeLinearBuckling_Auto => "buckling",
        FEAStudyTypeEnum_Auto.eStudyTypeSSHT_Auto => "steady_state_heat_transfer",
        FEAStudyTypeEnum_Auto.eStudyTypeTransientHeatTransfer_Auto => "transient_heat_transfer",
        FEAStudyTypeEnum_Auto.eStudyTypeHarmonicResponse_Auto => "harmonic_response",
        _ => type.ToString(),
    };

    private static bool Status(Study study, StudyStatusType_Auto status)
    {
        study.GetStudyStatus(status, out bool value);
        return value;
    }

    private static string State(Study study) =>
        Status(study, StudyStatusType_Auto.eStudyIsSolved_Auto) ? "solved"
        : Status(study, StudyStatusType_Auto.eStudyIsMeshed_Auto) ? "meshed"
        : Status(study, StudyStatusType_Auto.eStudyReadyForSolve_Auto) ? "ready"
        : Status(study, StudyStatusType_Auto.eStudyReadyForMesh_Auto) ? "ready_for_mesh"
        : "incomplete";

    private static string[]? Errors(Study study)
    {
        string[] errors = new (StudyStatusType_Auto Status, string Name)[]
        {
            (StudyStatusType_Auto.eStudyGeomInError_Auto, "geometry"),
            (StudyStatusType_Auto.eStudyMeshInError_Auto, "mesh"),
            (StudyStatusType_Auto.eStudyResultsInError_Auto, "results"),
            (StudyStatusType_Auto.eLoadInError_Auto, "loads"),
            (StudyStatusType_Auto.eConstraintInError_Auto, "constraints"),
            (StudyStatusType_Auto.eConnectorInError_Auto, "connectors"),
        }.Where(e => Status(study, e.Status)).Select(e => e.Name).ToArray();
        return errors.Length == 0 ? null : errors;
    }

    private static object[] Geometries(Study study)
    {
        try
        {
            // The out arrays of the simulation objects must not be null: the dispatch call wraps them as safe arrays and fails on null.
            Array geometries = Array.Empty<object>();
            study.GetGeometries(out geometries);
            return geometries?.Cast<object>().ToArray() ?? [];
        }
        catch (COMException)
        {
            return [];
        }
    }

    private static List<FEAConstraint> Constraints(Study study)
    {
        study.GetConstraintOwner(out object owner);
        var constraints = (ConstraintOwner)owner;
        var list = new List<FEAConstraint>();
        for (int i = 1, count = constraints.Count; i <= count; i++)
        {
            list.Add(constraints.Item(i));
        }

        return list;
    }

    private static List<SolidEdgePart.Load> Loads(Study study)
    {
        study.GetLoadOwner(out object owner);
        var loads = (LoadOwner)owner;
        var list = new List<SolidEdgePart.Load>();
        for (int i = 1, count = loads.Count; i <= count; i++)
        {
            list.Add(loads.Item(i));
        }

        return list;
    }

    private static string KindOf(FEAConstraint constraint)
    {
        constraint.GetConstraintType(out FEAConstraintTypeEnum_Auto type);
        return type switch
        {
            FEAConstraintTypeEnum_Auto.eCnstrTypeFixed_Auto => "fixed",
            FEAConstraintTypeEnum_Auto.eCnstrTypePinned_Auto => "pinned",
            FEAConstraintTypeEnum_Auto.eCnstrTypeNoRotation_Auto => "no_rotation",
            FEAConstraintTypeEnum_Auto.eCnstrTypeSlidingAlongSurface_Auto => "sliding",
            FEAConstraintTypeEnum_Auto.eCnstrTypeCylindrical_Auto => "cylindrical",
            FEAConstraintTypeEnum_Auto.eCnstrTypeUserDefined_Auto => "user_defined",
            _ => type.ToString(),
        };
    }

    private static object DescribeConstraint(FEAConstraint constraint)
    {
        Array elements = Array.Empty<object>();
        constraint.GetGeometries(out elements);
        return new
        {
            name = constraint.Name,
            kind = KindOf(constraint),
            elements = elements?.Length ?? 0,
            suppressed = constraint.Suppress ? true : (bool?)null,
        };
    }

    /// <summary>Whether the licence includes a constraint or load kind (kinds without a known button count as included).</summary>
    private static bool Licensed(SeContext context, SolidEdgeDocument document, string kind)
    {
        if (!KindCommands.TryGetValue(kind, out int command))
        {
            return true;
        }

        context.Application.CommandEnabled(command, document is SheetMetalDocument ? "SheetMetal" : "Part", out bool licensed, out bool unknown);
        return licensed && !unknown;
    }

    private static (string[] Constraints, string[] Loads) LicensedKinds(SeContext context, SolidEdgeDocument document) =>
        ([.. ConstraintKinds.Where(k => Licensed(context, document, k))], [.. LoadKinds.Where(k => Licensed(context, document, k))]);

    private static void RequireLicence(SeContext context, SolidEdgeDocument document, string kind, string what, string? study = null)
    {
        if (Licensed(context, document, kind))
        {
            return;
        }

        (string[] constraints, string[] loads) = LicensedKinds(context, document);
        throw new ToolException("not_licensed",
            $"The Solid Edge licence of this computer does not include the {kind} {what}{(study is null ? "" : $" that '{study}' has")}: Solid Edge refuses the study when it is solved (Simulation Express knows only some kinds).",
            $"Use the kinds this licence includes: constraints {string.Join(", ", constraints)}; loads {string.Join(", ", loads)}.{(study is null ? "" : " Delete the study and make it again with them.")}");
    }

    private static (string Kind, string Unit, double Scale) KindOf(SolidEdgePart.Load load)
    {
        load.GetLoadType(out FEALoadTypeEnum_Auto type);
        return type switch
        {
            FEALoadTypeEnum_Auto.eLoadTypeForce_Auto => ("force", "N", 1),
            FEALoadTypeEnum_Auto.eLoadTypePressure_Auto => ("pressure", "MPa", 1e-6),
            FEALoadTypeEnum_Auto.eLoadTypeTorque_Auto => ("torque", "N*m", 1),
            FEALoadTypeEnum_Auto.eLoadTypeGravity_Auto => ("gravity", "m/s2", 1),
            _ => (type.ToString(), "SI", 1),
        };
    }

    private static object DescribeLoad(SolidEdgePart.Load load)
    {
        load.GetLoadType(out FEALoadTypeEnum_Auto type);
        load.GetLoadValue(out double value);
        Array elements = Array.Empty<object>();
        load.GetGeometries(out elements);
        (string kind, string unit, double scale) = KindOf(load);
        double[]? direction = null;
        if (type is FEALoadTypeEnum_Auto.eLoadTypeForce_Auto or FEALoadTypeEnum_Auto.eLoadTypeGravity_Auto)
        {
            load.GetLoadOptions(out _, out _, out _, out LoadSymbDirOptsEnum_Auto mode, out double x, out double y, out double z,
                out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
            direction = mode == LoadSymbDirOptsEnum_Auto.eLoadDirNormalToFace_Auto ? null : [Geo.R(x), Geo.R(y), Geo.R(z)];
        }

        return new
        {
            name = load.Name,
            kind,
            // Positive when the pressure presses on the face (Solid Edge stores that as a negative value).
            value = Geo.R(value * scale * (type == FEALoadTypeEnum_Auto.eLoadTypePressure_Auto ? -1 : 1)),
            unit,
            direction,
            normal = type is FEALoadTypeEnum_Auto.eLoadTypeForce_Auto && direction is null ? true : (bool?)null,
            elements = elements?.Length ?? 0,
            suppressed = load.Suppress ? true : (bool?)null,
        };
    }

    private static object Create(SeContext context, SolidEdgeDocument document, SimulationSpec spec)
    {
        StudyOwner owner = Owner(document);
        FEAStudyTypeEnum_Auto type = spec.Type switch
        {
            null or StudyType.Static => FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto,
            StudyType.Modal => FEAStudyTypeEnum_Auto.eStudyTypeNormalModal_Auto,
            StudyType.Buckling => FEAStudyTypeEnum_Auto.eStudyTypeLinearBuckling_Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
        int modes = spec.Modes ?? 5;
        if (type != FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto && modes is < 1 or > 100)
        {
            throw ToolException.InvalidArgument("'modes' must be between 1 and 100.");
        }

        // Simulation Express makes static and modal studies only: AddStudy of a buckling study shows the licence message and
        // fails with E_FAIL. Express is told apart by its constraints, as it knows only the fixed one.
        if (type == FEAStudyTypeEnum_Auto.eStudyTypeLinearBuckling_Auto && !Licensed(context, document, "pinned"))
        {
            throw new ToolException("not_licensed",
                "Buckling studies need Solid Edge Simulation: the licence of this computer has Simulation Express (static and modal studies, fixed constraints, force and pressure loads).",
                "Make a static or modal study.");
        }

        Array bodies = BodiesFor(context, document, spec.Bodies);
        owner.AddStudy(type, FEAMeshTypeEnum_Auto.eMeshTypeTetrahedral_Auto, 0, 0,
            type == FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto ? 0u : (uint)modes, 0, 0, "", "", ResultOptions, out object created);
        var study = (Study)created;
        try
        {
            study.SetGeometries(ref bodies);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            study.Delete();
            throw new ToolException("study_failed", $"Solid Edge refused the bodies of the study: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Pass bodies of this document (se_body list).", ex);
        }

        return new { document = context.DocumentHandle(document), study = Describe(context, document, study) };
    }

    /// <summary>The bodies given, or every body of the document.</summary>
    private static Array BodiesFor(SeContext context, SolidEdgeDocument document, string[]? handles)
    {
        object[] bodies = handles is { Length: > 0 }
            ? handles.Select(h => (object)context.Handles.Resolve<SolidEdgeGeometry.Body>(h, "bodies")).ToArray()
            : (ModelDocument.TryGet(document)?.TryBodies() ?? []).Select(b => (object)b.Body).ToArray();
        return bodies.Length > 0 ? bodies
            : throw new ToolException("no_model", $"'{SeContext.SafeName(document)}' has no solid body to study.", "Model the part first.");
    }

    private static Array Elements(SeContext context, string[]? handles, string action)
    {
        if (handles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"Action '{action}' requires 'elements': faces (or edges and vertices) from se_geometry.");
        }

        return handles.Select(h =>
        {
            object element = context.Handles.Resolve(h, "elements");
            return element is SolidEdgeGeometry.Face or SolidEdgeGeometry.Edge or SolidEdgeGeometry.Vertex ? element
                : throw ToolException.InvalidArgument($"'{h}' is not a face, edge or vertex.");
        }).ToArray();
    }

    private static object Constrain(SeContext context, SolidEdgeDocument document, SimulationSpec spec)
    {
        Study study = Find(context, Owner(document), spec.Study);
        FEAConstraintTypeEnum_Auto type = spec.Constraint switch
        {
            null or SimulationConstraint.Fixed => FEAConstraintTypeEnum_Auto.eCnstrTypeFixed_Auto,
            SimulationConstraint.Pinned => FEAConstraintTypeEnum_Auto.eCnstrTypePinned_Auto,
            SimulationConstraint.NoRotation => FEAConstraintTypeEnum_Auto.eCnstrTypeNoRotation_Auto,
            SimulationConstraint.Sliding => FEAConstraintTypeEnum_Auto.eCnstrTypeSlidingAlongSurface_Auto,
            SimulationConstraint.Cylindrical => FEAConstraintTypeEnum_Auto.eCnstrTypeCylindrical_Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(spec)),
        };
        RequireLicence(context, document, spec.Constraint is { } given ? ArgumentNames.Snake(given) : "fixed", "constraint");
        Array elements = Elements(context, spec.Elements, "constrain");
        study.GetConstraintOwner(out object owner);
        // Symbol colour, spacing and size as the generic call that was verified; all six degrees of freedom (63).
        ((ConstraintOwner)owner).AddConstraint(ref elements, type, LoadSymbDirOptsEnum_Auto.eLoadDirDefault_Auto, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0,
            out object created, 63);
        var constraint = (FEAConstraint)created;
        return new { study = NameOf(study), constraint = DescribeConstraint(constraint), state = State(study) };
    }

    private static object Load(SeContext context, SolidEdgeDocument document, SimulationSpec spec)
    {
        Study study = Find(context, Owner(document), spec.Study);
        SimulationLoad load = spec.Load ?? throw ToolException.InvalidArgument("Action 'load' requires 'load'.", $"Use one of: {string.Join(", ", LoadKinds)}.");
        string kind = ArgumentNames.Snake(load);
        RequireLicence(context, document, kind, "load");
        bool force = load == SimulationLoad.Force;
        double value = Required(spec.Value, kind, force ? "N" : "MPa");
        if (!(value > 0) || double.IsInfinity(value))
        {
            throw ToolException.InvalidArgument("'value' must be positive: give the sense with 'direction' (a pressure presses on the face).");
        }

        double[]? direction = spec.Direction is null ? null : Unit(spec.Direction);
        if (!force && direction is not null)
        {
            throw ToolException.InvalidArgument("A pressure has no 'direction': it acts along the normal of the faces.");
        }

        Array elements = Elements(context, spec.Elements, "load");
        // A positive pressure pulls the face outwards, and SetFlip changes the symbol but not what is solved: a negative value
        // presses on the face (0.1 MPa on the 2000 mm2 top face: 200 N applied downwards).
        (FEALoadTypeEnum_Auto type, double amount, LoadSymbDirOptsEnum_Auto mode) = force
            ? (FEALoadTypeEnum_Auto.eLoadTypeForce_Auto, value,
                direction is null ? LoadSymbDirOptsEnum_Auto.eLoadDirNormalToFace_Auto : LoadSymbDirOptsEnum_Auto.eLoadDirAlongVec_Auto)
            : (FEALoadTypeEnum_Auto.eLoadTypePressure_Auto, -value * 1e6, LoadSymbDirOptsEnum_Auto.eLoadDirNormalToFace_Auto);
        double[] d = direction ?? [0, 0, 0];
        study.GetLoadOwner(out object owner);
        ((LoadOwner)owner).AddLoad(ref elements, type, amount, 0, mode, d[0], d[1], d[2], 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, out object created);
        return new { study = NameOf(study), load = DescribeLoad((SolidEdgePart.Load)created), state = State(study) };
    }

    private static double Required(double? value, string kind, string unit) =>
        value ?? throw ToolException.InvalidArgument($"A {kind} requires 'value' ({unit}).");

    private static double[] Unit(double[] vector) =>
        vector.Length == 3 && vector.All(double.IsFinite) && Geo.TryUnit(vector) is { } unit ? unit
            : throw ToolException.InvalidArgument("'direction' must be a non-zero vector [x, y, z].");

    /// <summary>Nastran needs an elastic modulus and a Poisson ratio, and a density for modes; a material without them fails late.</summary>
    private static void CheckMaterial(SeContext context, SolidEdgeDocument document, FEAStudyTypeEnum_Auto type)
    {
        string? material = MaterialService.MaterialName(context, document);
        double e = MaterialService.DocumentValue(context, document, MatTablePropIndex.seModulusElasticity) ?? 0;
        double nu = MaterialService.DocumentValue(context, document, MatTablePropIndex.sePoissonRatio) ?? 0;
        double density = MaterialService.DocumentValue(context, document, MatTablePropIndex.seDensity) ?? 0;
        bool modes = type == FEAStudyTypeEnum_Auto.eStudyTypeNormalModal_Auto;
        if (material is null || !(e > 0) || !(nu >= 0 && nu < 0.5) || modes && !(density > 0))
        {
            throw new ToolException("material_incomplete",
                string.Create(CultureInfo.InvariantCulture,
                    $"The material of '{SeContext.SafeName(document)}' ({material ?? "none"}) has an elastic modulus of {Geo.R(e * 1e-6)} MPa, a Poisson ratio of {Geo.R(nu)} and a density of {Geo.R(density)} kg/m3: the solver needs a modulus above 0, a ratio from 0 to 0.5{(modes ? " and a density" : "")}."),
                "Apply a material that has them: se_material info shows them, se_material apply assigns one (folder 'installation' for the libraries installed with Solid Edge, e.g. Steel).");
        }
    }

    private static object Solve(SeContext context, SolidEdgeDocument document, SimulationSpec spec)
    {
        StudyOwner owner = Owner(document);
        Study study = Find(context, owner, spec.Study);
        string name = NameOf(study);
        study.GetStudyType(out FEAStudyTypeEnum_Auto type);
        CheckMaterial(context, document, type);
        if (Geometries(study).Length == 0)
        {
            throw new ToolException("study_incomplete", $"'{name}' has no bodies.", "Create the study again with se_simulation create.");
        }

        bool loaded = type != FEAStudyTypeEnum_Auto.eStudyTypeNormalModal_Auto;
        List<FEAConstraint> constraints = Constraints(study);
        List<SolidEdgePart.Load> loads = Loads(study);
        if (loaded && (constraints.Count == 0 || loads.Count == 0))
        {
            throw new ToolException("study_incomplete", $"'{name}' needs at least one constraint and one load before it is solved.",
                "Add them with se_simulation constrain and load.");
        }

        // A kind outside the licence makes Solid Edge show a licence message that holds the solve until someone answers it.
        foreach (string kind in constraints.Select(KindOf).Distinct())
        {
            RequireLicence(context, document, kind, "constraint", name);
        }

        foreach (string kind in loads.Select(l => KindOf(l).Kind).Distinct())
        {
            RequireLicence(context, document, kind, "load", name);
        }

        owner.SetStudyActive(name);
        DateTime started = DateTime.Now;
        study.Solve(document, 0);
        string? text = NastranText(owner, started);
        IReadOnlyList<string> fatal = text is null ? [] : NastranOutput.FatalMessages(text);
        if (!Status(study, StudyStatusType_Auto.eStudyIsSolved_Auto))
        {
            string[]? errors = Errors(study);
            throw new ToolException("solve_failed",
                $"Solid Edge did not solve '{name}'{(errors is null ? "" : $" (errors in: {string.Join(", ", errors)})")}.{(fatal.Count == 0 ? "" : " Nastran: " + string.Join(" ", fatal.Take(3)))}",
                "Check the constraints and loads (se_simulation list) and the material; the Nastran output is in the scratch folder of the study.");
        }

        IReadOnlyList<NastranEigenvalue> eigenvalues = text is null ? [] : NastranOutput.Eigenvalues(text);
        double? maxDisplacement = loaded ? Peak(study, ResultQuantity.Displacement, ResultComponent.Magnitude) : null;
        double? maxVonMises = loaded ? Peak(study, ResultQuantity.Stress, ResultComponent.VonMises) : null;
        var notes = new List<string>();
        if (text is null)
        {
            notes.Add("Nastran wrote no .f06 output that could be read, so the frequencies, factors and resultants are missing.");
        }

        if (loaded && (maxDisplacement is null || maxVonMises is null))
        {
            notes.Add("A peak value could not be read: se_simulation results reads the plots.");
        }

        if (fatal.Count > 0)
        {
            notes.Add("Nastran printed fatal messages, so these results may be those of an earlier solve.");
        }

        return new
        {
            study = NameOf(study),
            type = TypeName(type),
            state = State(study),
            seconds = Math.Round((DateTime.Now - started).TotalSeconds, 1),
            maxDisplacementMm = maxDisplacement,
            maxVonMisesMPa = maxVonMises,
            appliedLoad = loaded ? Resultant(text, "OLOAD") : null,
            reaction = loaded ? Resultant(text, "SPCFORCE") : null,
            frequenciesHz = type == FEAStudyTypeEnum_Auto.eStudyTypeNormalModal_Auto
                ? eigenvalues.Select(m => Math.Round(m.Cycles, 4)).ToArray() : null,
            loadFactors = type == FEAStudyTypeEnum_Auto.eStudyTypeLinearBuckling_Auto
                ? eigenvalues.Select(m => Math.Round(m.Eigenvalue, 6)).ToArray() : null,
            fatal = fatal.Count == 0 ? null : fatal,
            note = notes.Count > 0 ? string.Join(" ", notes) : null,
            environment = context.Application.ActiveEnvironment,
            next = "Solid Edge shows the results: se_simulation results reads a plot (and its picture), close_results goes back to the model.",
        };
    }

    /// <summary>The .f06 that Nastran wrote for this solve in the scratch folder of the study owner, or null.</summary>
    private static string? NastranText(StudyOwner owner, DateTime started)
    {
        owner.GetNASTRANScratchDirectoryLocation(out string folder);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            FileInfo? output = new DirectoryInfo(folder).EnumerateFiles("*.f06")
                .Where(f => f.LastWriteTime >= started.AddSeconds(-2))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            return output is null ? null : File.ReadAllText(output.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static object? Resultant(string? text, string name) =>
        text is not null && NastranOutput.Resultant(text, name) is { } r
            ? new { forceN = r.Force.Select(v => Math.Round(v, 6) + 0.0).ToArray(), momentNm = r.Moment.Select(v => Math.Round(v, 6) + 0.0).ToArray() }
            : null;

    /// <summary>The largest value of a plot, or null when the results cannot be read.</summary>
    private static double? Peak(Study study, ResultQuantity quantity, ResultComponent component)
    {
        try
        {
            (_, Plot plot, double scale, _) = PlotOf(study, quantity, component, null);
            plot.GetMinMaxValues(out double max, out _);
            return Geo.R(max * scale);
        }
        catch (Exception ex) when (ex is COMException or ToolException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Shows a plot of the results (the person sees it) and returns it with the factor to the units of the tools.</summary>
    private static (Mode Mode, Plot Plot, double Scale, string Unit) PlotOf(Study study, ResultQuantity quantity, ResultComponent component, int? modeNumber)
    {
        string quantityName = ArgumentNames.Snake(quantity);
        string componentName = ArgumentNames.Snake(component);
        var entry = Quantities.FirstOrDefault(q => q.Name == quantity);
        if (entry.Components is null)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        int code = entry.Components.FirstOrDefault(c => c.Component == component).Code;
        if (code == 0)
        {
            throw ToolException.InvalidArgument($"The {quantityName} has no component '{componentName}'.",
                $"Use one of: {string.Join(", ", entry.Components.Select(c => ArgumentNames.Snake(c.Component)))}.");
        }

        study.ViewResults();
        study.GetResultsOwner(out object resultsOwner);
        ((ResultsOwner)resultsOwner).GetActiveModesOwner(out object modesOwner);
        var modes = (ModesOwner)modesOwner;
        Mode mode;
        if (modeNumber is { } n)
        {
            mode = Indexed(i => modes.Item(i)).Skip(n - 1).FirstOrDefault()
                ?? throw ToolException.NotFound($"There is no mode {n}: the study has {Indexed(i => modes.Item(i)).Count()}.", "Modes are numbered from 1.");
            modes.SetActiveMode(mode);
        }
        else
        {
            modes.GetActiveMode(out object active);
            mode = (Mode)active;
        }

        PlotsOwner plots = Indexed(i => mode.Item(i)).FirstOrDefault(p =>
            {
                p.GetPlotOwnerType(out FEAPlotsOwnerTypeEnum_Auto type);
                return type == entry.Owner;
            })
            ?? throw ToolException.NotFound($"The results have no {quantityName}.", "The study keeps displacement and stress results when se_simulation creates it.");
        Plot plot = Indexed(i => plots.Item(i)).FirstOrDefault(p =>
            {
                p.GetPlotType(out int type);
                return type == code;
            })
            ?? throw ToolException.NotFound($"The {quantityName} results have no {componentName} component.");
        mode.SetActivePlotOwner(plots);
        plots.SetActivePlot(plot);
        return (mode, plot, entry.Scale, entry.Unit);
    }

    /// <summary>Items 1, 2... of a collection that has no Count, up to the index Solid Edge refuses.</summary>
    private static IEnumerable<T> Indexed<T>(Func<int, T> item)
    {
        for (int i = 1; i <= 200; i++)
        {
            T value;
            try
            {
                value = item(i);
            }
            catch (COMException ex) when (ex.ErrorCode == ComErrors.DispEBadIndex)
            {
                yield break;
            }

            yield return value;
        }
    }

    public static SimulationPlot Results(SeContext context, string? documentHandle, SimulationSpec spec)
    {
        SolidEdgeDocument document = Document(context, documentHandle, spec);
        document.Activate();
        Study study = Find(context, Owner(document), spec.Study);
        if (!Status(study, StudyStatusType_Auto.eStudyIsSolved_Auto))
        {
            throw new ToolException("not_solved", $"'{NameOf(study)}' has no results.", "Solve it first with se_simulation solve.");
        }

        ResultQuantity quantity = spec.Quantity ?? ResultQuantity.Stress;
        ResultComponent component = spec.Component ?? (quantity == ResultQuantity.Displacement ? ResultComponent.Magnitude : ResultComponent.VonMises);
        study.GetStudyType(out FEAStudyTypeEnum_Auto type);
        if (type != FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto && spec.Mode is < 1)
        {
            throw ToolException.InvalidArgument("'mode' starts at 1.");
        }

        (_, Plot plot, double scale, string unit) = PlotOf(study, quantity, component,
            type == FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto ? null : spec.Mode ?? 1);
        plot.GetMinMaxValues(out double max, out double min);
        string? saved = null;
        byte[]? image = null;
        if (spec.Path is not null)
        {
            saved = context.Paths.Check(spec.Path, "path");
            if (!System.IO.Path.GetExtension(saved).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidArgument("The image path must end in .png.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved)!);
            plot.SavePlotAsImage(saved, document);
            image = File.Exists(saved) ? File.ReadAllBytes(saved)
                : throw new ToolException("capture_failed", "Solid Edge did not write the picture of the plot.");
        }

        var details = new
        {
            study = NameOf(study),
            quantity = ArgumentNames.Snake(quantity),
            component = ArgumentNames.Snake(component),
            mode = type == FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto ? null : (int?)(spec.Mode ?? 1),
            min = Geo.R(min * scale),
            max = Geo.R(max * scale),
            unit,
            note = type == FEAStudyTypeEnum_Auto.eStudyTypeLinearStatic_Auto ? null
                : "Mode shapes are normalised by Nastran: their displacements and stresses show the shape, not amounts.",
            savedTo = saved,
        };
        return new SimulationPlot(details, image, image is null ? null : "image/png");
    }

    private static object CloseResults(SeContext context, SolidEdgeDocument document)
    {
        string before = context.Application.ActiveEnvironment;
        if (!before.StartsWith("FEAResults", StringComparison.Ordinal))
        {
            return new { document = context.DocumentHandle(document), environment = before, note = "No simulation results were open." };
        }

        context.Application.StartCommand((SolidEdgeCommandConstants)CloseResultsCommand);
        return new { document = context.DocumentHandle(document), environment = context.Application.ActiveEnvironment, previous = before };
    }

    private static object Delete(SeContext context, SolidEdgeDocument document, SimulationSpec spec)
    {
        StudyOwner owner = Owner(document);
        Study study = Find(context, owner, spec.Study);
        string name = NameOf(study);
        study.Delete();
        return new { document = context.DocumentHandle(document), deleted = name, remaining = owner.Count };
    }
}
