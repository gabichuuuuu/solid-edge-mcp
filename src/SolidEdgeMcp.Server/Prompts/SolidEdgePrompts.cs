// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SolidEdgeMcp.Server.Prompts;

/// <summary>
/// MCP prompts for common workflows (in Claude Code they appear as /mcp__solid-edge__&lt;name&gt; commands). Each one turns a
/// request into a task message that names the tools in the order a person would use the Solid Edge commands, and asks
/// for the measured result at the end.
/// </summary>
[McpServerPromptType]
public sealed class SolidEdgePrompts
{
    private const string Common = """
        Use only the solid-edge tools. Start with se_session connect. Lengths are in mm and angles in degrees. Every
        modeling call is verified: when one fails, read the error and its next step, fix the input and try again; do not
        work around it with the generic se_com_* tools unless no specific tool covers the step. Do not close, save or
        overwrite documents you did not open or create. Check the result with measurements (se_physical, se_geometry,
        se_measure) and a picture (se_view capture), and end with a short report of what was made, the measured values
        and where the files are.
        """;

    [McpServerPrompt(Name = "model_part", Title = "Model a part from sketches")]
    [Description("Models a part in Solid Edge from a description (shape and dimensions in mm), feature by feature, and checks it.")]
    public static string ModelPart(
        [Description("The part to model, with its dimensions in mm, e.g. 'a 100 x 60 x 10 plate with four 8 mm holes 10 mm from the corners and 5 mm rounds'.")] string description,
        [Description("Absolute path of the .par file to save (in an allowed folder); empty to leave it unsaved.")] string? path = null,
        [Description("Material from the material library, e.g. 'Steel'; empty for none.")] string? material = null) =>
        $"""
        Model this part in Solid Edge: {description}

        Workflow: se_documents new (kind part). For each feature: se_sketch create on a base plane ('xy', 'yz' or 'xz',
        which find the plane by its orientation in any template) or on a face handle from se_geometry, draw (rectangle, circle, line, arc, polyline), close, then se_extrude
        or se_revolve (operation add or cut). Holes: se_hole. Rounds and chamfers: se_round / se_chamfer on edge handles from
        se_geometry 'edges'. Patterns: se_pattern. Shell, draft, ribs and other features: se_shell, se_draft, se_part_feature.
        Compare the volume each call returns with the expected one before going on.
        {(string.IsNullOrWhiteSpace(material) ? "" : $"Apply the material '{material}' with se_material and report the mass from se_physical.")}
        {(string.IsNullOrWhiteSpace(path) ? "Leave the part open and unsaved." : $"Save it with se_documents save_as to '{path}'.")}

        {Common}
        """;

    [McpServerPrompt(Name = "part_from_template", Title = "Part from a template with variables")]
    [Description("Makes a variant of a saved part by setting its variables, then its drawing, STEP and PDF.")]
    public static string PartFromTemplate(
        [Description("Absolute path of the saved part used as template (it is not modified).")] string template,
        [Description("Variable values, e.g. 'Length=120, Width=60, Hole_D=8' (mm and degrees).")] string values,
        [Description("Absolute path of the folder for the new part, its drawing, the STEP and the PDF (allowed folder).")] string folder,
        [Description("Base name of the new files, e.g. 'bracket_120'.")] string name) =>
        $"""
        Make a variant of the part '{template}' with these variable values: {values}.

        Workflow: se_documents open the template, then se_documents save_as '{folder}\{name}.par' right away so the template
        is not changed. se_variables list to find the variables, se_variables set each value (check that the names exist),
        se_documents recompute, and verify with se_physical and se_geometry that the size changed as expected. Save. Then
        se_documents new (kind draft); se_drawing base_view of the part (front) plus projected_view top and right and an
        isometric view (projected_view in a diagonal direction, or base_view with orientation top_front_right); se_drawing retrieve_dimensions or dimension for the main sizes; se_properties for the title block
        (title = '{name}'); save it as '{folder}\{name}.dft'. Export the part with se_export to '{folder}\{name}.stp' and the
        drawing to '{folder}\{name}.pdf'.

        {Common}
        """;

    [McpServerPrompt(Name = "manufacturing_drawing", Title = "Manufacturing drawing of a part or assembly")]
    [Description("Makes a dimensioned drawing of a saved part or assembly (views, dimensions, parts list) and exports it to PDF.")]
    public static string ManufacturingDrawing(
        [Description("Absolute path of the saved part or assembly.")] string model,
        [Description("Absolute path of the PDF to write (allowed folder); the drawing is saved next to it as .dft.")] string pdf,
        [Description("Sheet size, e.g. 'A3' or 'A4' (default A3).")] string? sheet = null) =>
        $"""
        Make a manufacturing drawing of '{model}' on a {(string.IsNullOrWhiteSpace(sheet) ? "A3" : sheet)} sheet and export it to '{pdf}'.

        Workflow: se_documents new (kind draft); se_drawing setup_sheet for the size; se_drawing base_view of the model
        (front, a scale that fits the sheet), projected_view top and right, and an isometric view (base_view with orientation
        top_front_right); for an assembly add
        se_drawing parts_list and balloons (se_annotation); dimension the main sizes with se_drawing retrieve_dimensions or
        dimension; add notes with se_annotation if useful; fill the title block with se_properties. Check the sheet with
        se_view capture (nothing overlapping or outside the sheet; move or scale views if needed). Save the drawing next to
        the PDF as .dft and export it with se_export.

        {Common}
        """;

    [McpServerPrompt(Name = "welded_frame", Title = "Welded frame (weldment)")]
    [Description("Builds a weldment: base parts, structural frames from the Solid Edge library or own sections, fillet welds and assembly holes.")]
    public static string WeldedFrame(
        [Description("The frame to build, e.g. 'a 200 x 100 x 10 base plate with a mitred frame of 20 x 20 square tube on a rectangle inset 20 mm, welded to the plate'.")] string description,
        [Description("Absolute path of the folder for the parts and the weldment (allowed folder).")] string folder) =>
        $"""
        Build this welded frame in Solid Edge: {description}. Save every file in '{folder}'.

        Workflow: model and save the plates or other parts first (se_documents new part, sketches, se_extrude, save_as).
        se_documents new (kind weldment), save_as into the folder, se_assembly insert the parts. Frames: se_frame library to
        pick a section (or use a part of your own), then se_frame frame along the points of the path (assembly coordinates;
        the section is centred on the path, so lift the path by half the section to sit it on a face); always make frames
        with se_frame, which keeps the assembly features working. Welds: se_geometry 'faces' with source = the member or part
        occurrence to get the faces, then se_assembly_feature fillet_weld (base faces, target faces that meet them along an
        edge, leg). Holes through the parts: se_assembly_feature hole and pattern. Report the cut length of the frames
        (se_frame list), the weld bead volumes and the hole faces (se_assembly_feature list). Save the weldment.

        {Common}
        """;
}
