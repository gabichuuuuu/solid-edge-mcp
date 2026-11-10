// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>Simulation studies of parts and sheet metal (Simulation tab, solved by Simcenter Nastran).</summary>
[McpServerToolType]
public sealed class SimulationTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_simulation", Title = "Simulation", Destructive = false, OpenWorld = false)]
    [Description("""
        Finite element studies of a part or sheet metal document, solved by Simcenter Nastran (needs a Solid Edge licence with Simulation). Lengths mm, forces N, pressures and stresses MPa.
        list (studies: name, type, state, constraints, loads, handle; the constraint and load kinds the licence includes: Simulation Express has fixed, force and pressure, and static and modal studies) | create (type static|modal|buckling, modes for modal and buckling (default 5), bodies default all) |
        constrain (elements = faces/edges/vertices from se_geometry, constraint fixed|pinned|no_rotation|sliding|cylindrical) |
        load (elements, load = force (value N; direction [x,y,z], default normal to the face) | pressure (value MPa, presses on the face)) |
        solve (meshes and solves; static and buckling need a constraint and a load; returns the largest displacement and von Mises stress, the applied load and the reaction (resultants about the origin), the natural frequencies Hz or the buckling load factors) |
        results (quantity displacement|stress, component magnitude|x|y|z or von_mises|xx|yy|zz|xy|yz|zx, mode for modal and buckling (default 1); min and max; path = .png to save and return the picture of the plot) |
        close_results (back to the model from the results Solid Edge shows) | delete (study).
        study = name, index or handle; default the only study. The material needs an elastic modulus and Poisson ratio (and density for modes): se_material apply, folder installation for the libraries of Solid Edge.
        Example: {"action":"constrain","elements":["face:12"],"constraint":"fixed"}; {"action":"load","elements":["face:15"],"load":"force","value":100,"direction":[0,0,-1]}.
        """)]
    public Task<CallToolResult> Simulation(
        SimulationAction action,
        [Description("constrain, load, solve, results, delete: the study (name, 1-based index or handle); default the only one.")] string? study = null,
        [Description("create: the kind of study (default static).")] StudyType? type = null,
        [Description("create: number of modes of a modal or buckling study (default 5).")] int? modes = null,
        [Description("create: body handles to study; default every body of the document.")] string[]? bodies = null,
        [Description("constrain, load: face, edge or vertex handles (se_geometry).")] string[]? elements = null,
        [Description("constrain: the kind of constraint (default fixed).")] SimulationConstraint? constraint = null,
        [Description("load: the kind of load.")] SimulationLoad? load = null,
        [Description("load: force N or pressure MPa; always positive.")] double? value = null,
        [Description("load: direction [x,y,z] of a force.")] double[]? direction = null,
        [Description("results: what to read (default stress).")] ResultQuantity? quantity = null,
        [Description("results: the component (default magnitude for displacement, von_mises for stress).")] ResultComponent? component = null,
        [Description("results: the mode of a modal or buckling study (1-based, default 1).")] int? mode = null,
        [Description("results: a .png path in an allowed folder to save the picture of the plot and return it.")] string? path = null,
        [Description("Part or sheet metal document handle; default: the document of the study or elements, or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_simulation";
        string act = Snake(action);
        var spec = new SimulationSpec(action, study, type, modes, bodies, elements,
            action == SimulationAction.Constrain ? constraint : null, action == SimulationAction.Constrain ? null : load,
            value, direction, quantity, component, mode, path);
        TimeSpan? timeout = action == SimulationAction.Solve ? TimeSpan.FromMinutes(30) : null;
        return action == SimulationAction.Results
            ? runner.RunAsync(tool, act, ctx =>
            {
                SimulationPlot plot = SimulationService.Results(ctx, document, spec);
                return plot.Image is null ? plot.Details : new ToolImage(plot.Image, plot.MimeType!, plot.Details);
            }, cancellationToken)
            : runner.RunAsync(tool, act, ctx => SimulationService.Run(ctx, document, spec), cancellationToken, timeout: timeout);
    }
}
