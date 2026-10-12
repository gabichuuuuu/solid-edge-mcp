namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Categories of the application options (ApplicationGlobalConstants), as the pages and sections of the Solid Edge
/// Options dialog group them. Every constant of Solid Edge 2026 belongs to exactly one category; a constant added by
/// a later version falls under "other".
/// </summary>
internal static class OptionCategories
{
    internal sealed record Category(string Key, string Title, int[] Ids);

    public static readonly IReadOnlyList<Category> All =
    [
        new("general", "General: undo steps, mouse wheel, 3D mouse, update all, read-only opening",
            [46, .. R(89, 90), .. R(95, 96), 176, 178, 180, 182, 185, .. R(192, 193), 195, 214, 252, 259, 320, .. R(439, 440), 458, 468, 482, 489, .. R(499, 500)]),
        new("save", "Save: automatic save, backups, prompt for properties, stored geometry",
            [.. R(12, 13), 177, 206, .. R(245, 251), .. R(493, 494), 506]),
        new("file_locations", "File locations: templates, material table, hole and thread files, property seed, logs",
            [21, 53, .. R(61, 63), .. R(260, 267), .. R(270, 273), 443, 463, 498]),
        new("startup", "Start-up: start screen, start template, last document, synchronous or ordered",
            [85, .. R(306, 309)]),
        new("interface", "Interface: colour theme, colour scheme, language, ribbon, status bar buttons, tips, help, scroll bars, zoom tool",
            [.. R(71, 84), 86, 105, .. R(143, 146), .. R(310, 319), .. R(372, 378), 436, .. R(464, 465), 469, 481, 485, 491, .. R(504, 505)]),
        new("display", "Display: quality, sharpness, anti-aliasing, shadows, sizes of planes and triad, view transitions",
            [.. R(0, 1), .. R(14, 19), .. R(34, 35), 110, .. R(141, 142), 179, 181, 194, .. R(196, 197), .. R(200, 205), 207, .. R(209, 210), 213, 215, .. R(460, 462), 466, 475]),
        new("colors", "Colours: background, highlight, selection, construction, failed, sketch states, live sections",
            [.. R(2, 11), .. R(28, 29), .. R(31, 32), .. R(54, 58), .. R(91, 94), .. R(216, 240)]),
        new("helpers", "Helpers: grids and snapping per environment, readouts, key-ins, locate, selection fence",
            [.. R(147, 167), 198, .. R(241, 244), .. R(323, 325), .. R(502, 503)]),
        new("sketch", "Sketching: dynamic edit, undo, paste, regions, sketch view orientation, shaded sketches",
            [183, .. R(186, 191), 256, 467, 497]),
        new("assembly", "Assembly: activation and simplification by size, placement, relationships, PathFinder, item numbers, frames",
            [.. R(64, 70), 140, 184, 208, 211, .. R(253, 255), .. R(257, 258), .. R(321, 322), .. R(326, 357), 490]),
        new("sheet_metal", "Sheet metal: bend line display", [212]),
        new("dimension_style_mapping", "Drawing: dimension style mapping by annotation type", [52, .. R(111, 134)]),
        new("pdf", "Drawing: save as PDF (colours, grid, quality, sheets, watermark)", [.. R(168, 174), 470, 492]),
        new("standards", "Standards: tolerance tables, limits and fits, weld and edge symbols, modelling standard",
            [.. R(97, 100), .. R(282, 284), 457]),
        new("text_defaults", "Drawing: default text (font, size, spacing, alignment, style)", [.. R(444, 456)]),
        new("drawing", "Drawing: views, edges, sheets and tabs, colours, updates, PDF and viewer data",
            [60, .. R(101, 103), .. R(106, 107), .. R(136, 139), .. R(358, 371), .. R(379, 419), .. R(421, 426), 501]),
        new("units", "Units: show units in value fields", [104, 175]),
        new("simulation", "Simulation: study type and display colours", [.. R(285, 305), 459]),
        new("performance", "Performance: threaded display, dynamic display by size, limited update and save, culling",
            [30, 45, .. R(427, 435), .. R(437, 438), .. R(441, 442), .. R(486, 487)]),
        new("data_management", "Data management: status folders, revisions, Teamcenter, Insight, document name formula",
            [20, .. R(22, 27), 33, .. R(36, 44), .. R(47, 51), 59, .. R(87, 88), .. R(108, 109), 135, .. R(274, 280), .. R(483, 484)]),
        new("other", "Other: OEM identification and migration state", [.. R(476, 480), .. R(495, 496)]),
    ];

    private static readonly Dictionary<int, string> ById = All
        .SelectMany(c => c.Ids.Select(id => (Id: id, c.Key)))
        .ToDictionary(e => e.Id, e => e.Key);

    /// <summary>Category of an application option; "other" for one this table does not know.</summary>
    public static string Of(int id) => ById.GetValueOrDefault(id, "other");

    public static bool Exists(string key) => All.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    private static int[] R(int first, int last) => Enumerable.Range(first, last - first + 1).ToArray();
}
