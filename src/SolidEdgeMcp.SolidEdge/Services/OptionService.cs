// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using IOPath = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Solid Edge options (the "Solid Edge Options" dialog and the global settings of each document type) through
/// Get/SetGlobalParameter, and document units (UnitsOfMeasure). Application options belong to the user and
/// persist between sessions: each change returns the previous value so it can be restored.
/// </summary>
public static partial class OptionService
{
    /// <summary>Enumeration of parameters of the scope, the object that serves them, and prefixes removed from the name.</summary>
    private sealed record Catalog(Type Enum, object Target, string Owner, string[] Prefixes);

    public static object List(SeContext context, OptionScope scope, string? documentHandle, string? filter, string? category, int offset, int limit)
    {
        if (scope == OptionScope.Drawing)
        {
            return DrawingList(context, documentHandle, filter, offset, limit);
        }

        Catalog catalog = CatalogOf(context, scope, documentHandle);
        if (!string.IsNullOrWhiteSpace(category) && catalog.Owner == "application" && !OptionCategories.Exists(category))
        {
            throw ToolException.InvalidArgument($"Unknown category '{category}'.",
                $"Use one of: {string.Join(", ", OptionCategories.All.Select(c => c.Key))} (se_options action 'categories').");
        }

        var matches = Entries(catalog)
            .Where(e => string.IsNullOrWhiteSpace(filter) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(category) || catalog.Owner != "application"
                || string.Equals(OptionCategories.Of(e.Value), category.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = matches.Skip(start).Take(take).Select(e => Read(catalog, e.Name, e.Value)).ToList();
        return new
        {
            scope = catalog.Owner,
            category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant(),
            count = matches.Count,
            offset = start,
            returned = items.Count,
            more = start + items.Count < matches.Count,
            options = items,
        };
    }

    /// <summary>Categories of the application options, as the Solid Edge Options dialog groups them, with their number of options.</summary>
    public static object Categories(SeContext context)
    {
        Catalog catalog = CatalogOf(context, OptionScope.Application, null);
        var counts = Entries(catalog).GroupBy(e => OptionCategories.Of(e.Value)).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            scope = catalog.Owner,
            categories = OptionCategories.All.Select(c => new { key = c.Key, title = c.Title, options = counts.GetValueOrDefault(c.Key) }).ToList(),
            note = "List a category with se_options action 'list' and 'category'. Document options: scope 'document'.",
        };
    }

    /// <summary>Value of an application option (null when Solid Edge cannot read it), for the other services.</summary>
    internal static object? ReadApplication(SeContext context, ApplicationGlobalConstants option)
    {
        Catalog catalog = CatalogOf(context, OptionScope.Application, null);
        return ReadRaw(catalog, (int)option, out _);
    }

    /// <summary>
    /// Saves every readable option of the scope to a JSON file in an allowed folder (by default the work folder), so that
    /// se_options 'restore' can put them back after changes.
    /// </summary>
    public static object Snapshot(SeContext context, OptionScope scope, string? documentHandle, string? path)
    {
        if (scope == OptionScope.Drawing)
        {
            throw new ToolException("not_supported", "Drawing options are saved with the drawing and are not kept in snapshots.",
                "Each set returns the previous value to put it back; or close the drawing without saving.");
        }

        Catalog catalog = CatalogOf(context, scope, documentHandle);
        string target = path is null
            ? IOPath.Combine(context.AppPaths.Work, "options",
                $"{catalog.Owner}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json")
            : context.Paths.Check(path);
        if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("The snapshot is a .json file.", "Give 'path' the .json extension, or leave it out to use the work folder.");
        }

        var entries = new List<SnapshotEntry>();
        int unreadable = 0;
        foreach ((string Name, int Value) entry in Entries(catalog))
        {
            if (Volatile(entry.Name))
            {
                continue;
            }

            object? raw = ReadRaw(catalog, entry.Value, out string? error);
            if (error is not null || raw is null or DBNull or Array || Format(raw) is not { } value || value is string && raw is not string)
            {
                unreadable++;
                continue;
            }

            entries.Add(new SnapshotEntry(entry.Name, raw.GetType().Name, JsonSerializer.SerializeToElement(value)));
        }

        Directory.CreateDirectory(IOPath.GetDirectoryName(target)!);
        bool documentOpen = context.Application.Documents.Count > 0;
        var file = new SnapshotFile(SnapshotFormat, DateTimeOffset.Now, context.Application.Version, catalog.Owner, entries, documentOpen);
        File.WriteAllText(target, JsonSerializer.Serialize(file, SnapshotJson));
        return new
        {
            path = target,
            scope = catalog.Owner,
            saved = entries.Count,
            unreadable,
            documentOpen,
            note = "Put them back with se_options action 'restore' and this path (dryRun = true lists the differences first)"
                + (documentOpen ? ", with a document window open." : ", with no document open (with one, Solid Edge reads some drawing options differently)."),
        };
    }

    /// <summary>
    /// Sets back every option that differs from a snapshot made by 'snapshot' (also changes the person made since then;
    /// <paramref name="dryRun"/> only lists them).
    /// </summary>
    public static object Restore(SeContext context, string? documentHandle, string path, bool dryRun)
    {
        string file = context.Paths.Check(path);
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"There is no snapshot file '{file}'.", "Make one with se_options action 'snapshot'.");
        }

        SnapshotFile? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(file), SnapshotJson);
        }
        catch (JsonException ex)
        {
            throw ToolException.InvalidArgument($"'{file}' is not an options snapshot: {ex.Message}");
        }

        if (snapshot is null || snapshot.Format != SnapshotFormat || snapshot.Options is null)
        {
            throw ToolException.InvalidArgument($"'{file}' is not an options snapshot made by se_options 'snapshot'.");
        }

        Catalog catalog = CatalogOf(context, snapshot.Scope == "application" ? OptionScope.Application : OptionScope.Document, documentHandle);
        if (catalog.Owner != snapshot.Scope)
        {
            throw new ToolException("wrong_document_type", $"The snapshot holds {snapshot.Scope} options, but the document is a {catalog.Owner}.",
                "Pass a document of the same type (document) or use an application snapshot.");
        }

        // Verified: with no document window, Solid Edge cannot read some drawing options and reports others as -1
        // (LimitsViewsDraft, DetailEnvelope), so a snapshot is only compared in the same state it was made in.
        bool documentOpen = context.Application.Documents.Count > 0;
        if (snapshot.Scope == "application" && snapshot.DocumentOpen is { } wasOpen && wasOpen != documentOpen)
        {
            throw new ToolException("state_mismatch",
                wasOpen
                    ? "The snapshot was made with a document window open and none is open now: Solid Edge reads some options differently without one."
                    : "The snapshot was made with no document open and one is open now: Solid Edge reads some drawing options differently with one.",
                wasOpen
                    ? "Open or create a document (se_documents) and restore again."
                    : "Close the documents and restore again, or make the snapshot again with a document open.");
        }

        var changed = new List<object>();
        var failed = new List<object>();
        int unchanged = 0;
        foreach (SnapshotEntry entry in snapshot.Options)
        {
            (string Name, int Value)? option = TryFind(catalog, entry.Name);
            if (option is null)
            {
                failed.Add(new { name = entry.Name, error = "This Solid Edge version has no such option." });
                continue;
            }

            object? current = ReadRaw(catalog, option.Value.Value, out string? readError);
            if (readError is not null)
            {
                failed.Add(new { name = entry.Name, error = readError });
                continue;
            }

            object wanted;
            try
            {
                wanted = FromSnapshot(entry);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                failed.Add(new { name = entry.Name, error = $"The saved value {entry.Value.GetRawText()} is not a {entry.Type}." });
                continue;
            }

            if (SameValue(current, wanted))
            {
                unchanged++;
                continue;
            }

            if (!dryRun)
            {
                try
                {
                    Late.Call(catalog.Target, "SetGlobalParameter", option.Value.Value, wanted);
                }
                catch (Exception ex) when (Late.IsMemberFailure(ex))
                {
                    failed.Add(new { name = entry.Name, from = Format(current), to = Format(wanted), error = ComErrors.Unwrap(ex).Message });
                    continue;
                }

                object? after = ReadRaw(catalog, option.Value.Value, out _);
                if (!SameValue(after, wanted))
                {
                    failed.Add(new { name = entry.Name, from = Format(current), to = Format(wanted), error = $"Solid Edge kept {Describe(after)}." });
                    continue;
                }
            }

            changed.Add(new { name = entry.Name, from = Format(current), to = Format(wanted) });
        }

        bool noWindow = catalog.Owner == "application" && !documentOpen;
        return new
        {
            path = file,
            scope = snapshot.Scope,
            created = snapshot.Created,
            dryRun,
            changed,
            unchanged,
            failed,
            note = failed.Count > 0 && noWindow
                ? "Some options, such as the display colours, can only be changed while a document window is open: open one and restore again."
                : null,
        };
    }

    public static object Get(SeContext context, OptionScope scope, string? documentHandle, string name)
    {
        if (scope == OptionScope.Drawing)
        {
            return DrawingGet(context, documentHandle, name);
        }

        Catalog catalog = CatalogOf(context, scope, documentHandle);
        (string Name, int Value) entry = Find(catalog, name);
        return Read(catalog, entry.Name, entry.Value);
    }

    /// <summary>Changes an option using the type of its current value and checks that Solid Edge accepted it.</summary>
    public static object Set(SeContext context, OptionScope scope, string? documentHandle, string name, JsonElement value)
    {
        if (scope == OptionScope.Drawing)
        {
            return DrawingSet(context, documentHandle, name, value);
        }

        Catalog catalog = CatalogOf(context, scope, documentHandle);
        (string Name, int Value) entry = Find(catalog, name);
        object? previous = ReadRaw(catalog, entry.Value, out string? readError);
        if (readError is not null)
        {
            throw new ToolException("option_failed", $"Solid Edge cannot read the option '{entry.Name}': {readError}",
                "Only options that can be read can be changed safely; use se_com_invoke on SetGlobalParameter if you must.");
        }

        object converted = EnumOption(entry.Name) is { } codes && value.ValueKind == JsonValueKind.String
            ? ConvertValue(JsonSerializer.SerializeToElement(CodeOf(codes, entry.Name, value.GetString() ?? "")), previous, false)
            : ConvertValue(value, previous, IsColor(entry.Name));
        if (converted is string text && PathGuard.LooksLikePath(text) && !SameValue(text, previous))
        {
            CheckPathValue(context, catalog, entry.Name, text);
        }

        Originals.TryAdd($"{catalog.Owner}/{entry.Name}", previous);
        try
        {
            Late.Call(catalog.Target, "SetGlobalParameter", entry.Value, converted);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Verified: with no document open, setting a display color (ColorBackground) gives E_FAIL; with a part window open it works.
            bool noWindow = catalog.Owner == "application" && context.Application.Documents.Count == 0;
            throw new ToolException("option_failed", $"Solid Edge rejected the value for '{entry.Name}': {ComErrors.Unwrap(ex).Message}",
                noWindow
                    ? "No document is open: some application options, such as the display colors, can only be changed while a document window is open. Open or create one (se_documents) and try again."
                    : "Check the type and range of the current value (se_options get).", ex);
        }

        object? current = ReadRaw(catalog, entry.Value, out _);
        bool applied = SameValue(current, converted);
        if (!applied && SameValue(current, previous) && !SameValue(previous, converted))
        {
            throw new ToolException("option_not_applied",
                $"Solid Edge kept '{entry.Name}' at {Describe(previous)} instead of {Describe(converted)}.",
                "The value may be out of range or the option may be read-only.");
        }

        return new
        {
            scope = catalog.Owner,
            name = entry.Name,
            previous = Format(previous),
            value = Format(current),
            meaning = Meaning(entry.Name, current),
            color = IsColor(entry.Name) ? Hex(current) : null,
            note = applied ? null : "Solid Edge adjusted the value (for example to its allowed range).",
            // Verified: Solid Edge refuses an empty value (E_FAIL) for an unset folder option such as Macros.
            restore = previous is null
                ? "It had no value before, and Solid Edge refuses an empty value for some options (such as the Macros folder): this change may not be undone through se_options."
                : $"To undo, set it back to {Describe(previous)}.",
        };
    }

    private const string SnapshotFormat = "solid-edge-mcp/options/1";

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record SnapshotEntry(string Name, string Type, JsonElement Value);

    private sealed record SnapshotFile(string Format, DateTimeOffset Created, string? Version, string Scope, List<SnapshotEntry>? Options, bool? DocumentOpen = null);

    /// <summary>Value each option had before this server first changed it, so a path can always be set back.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object?> Originals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Options that Solid Edge changes by itself (last folder of a dialog, system information): not saved in snapshots.</summary>
    private static bool Volatile(string name) =>
        name.StartsWith("Last", StringComparison.OrdinalIgnoreCase) || name.Contains("SystemInfo", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OEM", StringComparison.OrdinalIgnoreCase) || name.Contains("Migration", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A folder or file option may point into the allowed folders, the Solid Edge installation (templates, material
    /// tables, standards) or back to the value it had before this server changed it; anything else needs the policy.
    /// </summary>
    private static void CheckPathValue(SeContext context, Catalog catalog, string name, string text)
    {
        if (context.Paths.IsAllowed(text)
            || (Originals.TryGetValue($"{catalog.Owner}/{name}", out object? original) && SameValue(original, text)))
        {
            return;
        }

        string? install = SolidEdgeSettings.InstallFolder(context.Application);
        string full;
        try
        {
            full = IOPath.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw ToolException.InvalidArgument($"'{text}' is not a valid path: {ex.Message}");
        }

        if (install is not null && full.StartsWith(install.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ToolException("path_not_allowed", $"The option '{name}' would point Solid Edge to '{text}', outside the allowed folders.",
            $"Use a folder under one of: {string.Join("; ", context.Paths.AllowedRoots)}, or the Solid Edge installation; other folders need allowedRoots in policy.json.");
    }

    private static (string Name, int Value)? TryFind(Catalog catalog, string name)
    {
        string wanted = Strip(name.Trim(), catalog.Prefixes);
        foreach ((string Name, int Value) entry in Entries(catalog))
        {
            if (string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static object FromSnapshot(SnapshotEntry entry)
    {
        JsonElement v = entry.Value;
        return entry.Type switch
        {
            nameof(Boolean) => v.GetBoolean(),
            nameof(Int32) => v.GetInt32(),
            nameof(Int16) => v.GetInt16(),
            nameof(UInt16) => v.GetUInt16(),
            nameof(UInt32) => v.GetUInt32(),
            nameof(Int64) => v.GetInt64(),
            nameof(Byte) => v.GetByte(),
            nameof(Single) => v.GetSingle(),
            nameof(Double) => v.GetDouble(),
            nameof(String) => v.GetString() ?? "",
            _ => throw new FormatException(),
        };
    }

    /// <summary>Options whose number stands for a named choice (enumeration of the interop).</summary>
    private static Type? EnumOption(string name) =>
        string.Equals(name, "ColorTheme", StringComparison.OrdinalIgnoreCase) ? typeof(SEColorTheme) : null;

    /// <summary>Name of the choice (dark, light...) of an option that stores a code, or null.</summary>
    internal static string? Meaning(string name, object? value)
    {
        if (EnumOption(name) is not { } codes || value is null or DBNull)
        {
            return null;
        }

        try
        {
            string? member = Enum.GetName(codes, Enum.ToObject(codes, Convert.ToInt32(value, CultureInfo.InvariantCulture)));
            return member is null ? null : Snake(member[CodePrefix(codes).Length..]);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static int CodeOf(Type codes, string option, string choice)
    {
        string prefix = CodePrefix(codes);
        string wanted = choice.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        foreach (string member in Enum.GetNames(codes))
        {
            if (string.Equals(member[prefix.Length..], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt32(Enum.Parse(codes, member), CultureInfo.InvariantCulture);
            }
        }

        if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
        {
            return raw;
        }

        throw ToolException.InvalidArgument($"'{choice}' is not a value of {option}.",
            $"Use one of: {string.Join(", ", Enum.GetNames(codes).Select(m => Snake(m[prefix.Length..])))}.");
    }

    private static string CodePrefix(Type codes) => UnitPrefix(codes);

    /// <summary>Document units: type, readout unit and precision.</summary>
    public static object Units(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        var items = new List<object>();
        foreach (UnitOfMeasure unit in UnitsOf(document))
        {
            items.Add(DescribeUnit(unit));
        }

        return new
        {
            document = context.DocumentHandle(document),
            units = items,
            note = "Display units only: the se_* tools always take and return mm, degrees and kg.",
        };
    }

    /// <summary>Changes the readout unit and precision of a document unit type.</summary>
    public static object SetUnits(SeContext context, string? documentHandle, string type, string? unit, int? precision)
    {
        if (unit is null && precision is null)
        {
            throw ToolException.InvalidArgument("Action 'set_units' requires 'unit' and/or 'precision'.");
        }

        SolidEdgeDocument document = context.GetDocument(documentHandle);
        UnitOfMeasure target = UnitsOf(document).FirstOrDefault(u => string.Equals(TypeName(u.Type), Key(type), StringComparison.Ordinal))
            ?? throw ToolException.NotFound($"The document has no unit type '{type}'.", "List them with se_options action 'units'.");
        object before = DescribeUnit(target);
        if (unit is not null)
        {
            int wanted = UnitValue(target.Type, unit);
            target.Units = wanted;
            if (target.Units != wanted)
            {
                throw new ToolException("option_not_applied", $"Solid Edge kept the {type} unit at {UnitName(target.Type, target.Units)}.");
            }
        }

        if (precision is { } p)
        {
            target.Precision = p;
            if (target.Precision != p)
            {
                throw new ToolException("option_not_applied", $"Solid Edge kept the {type} precision at {target.Precision} instead of {p}.",
                    "Precision is an index (distance: 0 ones, 1 tenths, 2 hundredths, 3 thousandths, 4 ten-thousandths...).");
            }
        }

        return new { document = context.DocumentHandle(document), previous = before, current = DescribeUnit(target), note = "Save the document to keep the change." };
    }

    /// <summary>
    /// A value in tool units as the document displays it (UnitsOfMeasure.FormatUnit: 60 mm reads "2.36 in" with the distance
    /// unit in inches and precision 2), or, for text, the text read as Solid Edge reads a typed value (ParseUnit: "2.5 in" is
    /// 63.5 mm; a number alone is in the document's unit).
    /// </summary>
    public static object FormatUnits(SeContext context, string? documentHandle, string type, JsonElement value)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        UnitOfMeasure target = UnitsOf(document).FirstOrDefault(u => string.Equals(TypeName(u.Type), Key(type), StringComparison.Ordinal))
            ?? throw ToolException.NotFound($"The document has no unit type '{type}'.", "List them with se_options action 'units'.");
        UnitsOfMeasure units = document.UnitsOfMeasure;
        (double factor, string toolUnit) = ToolUnit(target.Type);
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()!;
                double parsed = Convert.ToDouble(units.ParseUnit((int)target.Type, text), CultureInfo.InvariantCulture);
                return new { document = context.DocumentHandle(document), unitType = TypeName(target.Type), text, value = Math.Round(parsed / factor, 9), unit = toolUnit };
            }

            if (value.ValueKind != JsonValueKind.Number)
            {
                throw ToolException.InvalidArgument("'value' must be a number in tool units (to format) or a text with its unit (to read).");
            }

            double number = SolidEdgeMcp.Core.Units.RequireFinite(value.GetDouble(), "value");
            string shown = Convert.ToString(units.FormatUnit((int)target.Type, number * factor, Type.Missing), CultureInfo.InvariantCulture) ?? "";
            return new
            {
                document = context.DocumentHandle(document),
                unitType = TypeName(target.Type),
                value = number,
                unit = toolUnit,
                text = shown,
                displayUnit = UnitName(target.Type, target.Units),
                precision = target.Precision,
            };
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw ToolException.InvalidArgument($"Solid Edge could not read '{value}' as a {TypeName(target.Type)}: {ex.Message.Trim()}",
                "Give a number with a unit Solid Edge knows, e.g. '2.5 in', '63.5 mm' or '30 deg'.");
        }
    }

    /// <summary>Factor from tool units to Solid Edge's database units (SI) and the name of the tool unit.</summary>
    private static (double Factor, string Unit) ToolUnit(UnitTypeConstants type) => type switch
    {
        UnitTypeConstants.igUnitDistance => (1e-3, "mm"),
        UnitTypeConstants.igUnitAngle => (Math.PI / 180, "deg"),
        UnitTypeConstants.igUnitArea => (1e-6, "mm2"),
        UnitTypeConstants.igUnitVolume => (1e-9, "mm3"),
        UnitTypeConstants.igUnitMass => (1, "kg"),
        _ => (1, "SI"),
    };

    private static Catalog CatalogOf(SeContext context, OptionScope scope, string? documentHandle)
    {
        if (scope == OptionScope.Application)
        {
            return new Catalog(typeof(ApplicationGlobalConstants), context.Application, "application", ["seApplicationGlobal"]);
        }

        if (scope != OptionScope.Document)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        SolidEdgeDocument document = context.GetDocument(documentHandle);
        return document switch
        {
            SolidEdgePart.WeldmentDocument => new Catalog(typeof(SolidEdgePart.WeldmentGlobalConstants), document, "weldment", ["seWeldmentGlobal", "seWeldment"]),
            SolidEdgePart.SheetMetalDocument => new Catalog(typeof(SolidEdgePart.SheetMetalGlobalConstants), document, "sheet_metal", ["seSheetMetalGlobal", "seSheetMetal"]),
            SolidEdgePart.PartDocument => new Catalog(typeof(SolidEdgePart.PartGlobalConstants), document, "part", ["sePartGlobal", "sePart"]),
            SolidEdgeAssembly.AssemblyDocument => new Catalog(typeof(SolidEdgeAssembly.AssemblyGlobalConstants), document, "assembly", ["seAssemblyGlobal", "seAssembly"]),
            SolidEdgeDraft.DraftDocument => new Catalog(typeof(SolidEdgeDraft.DraftGlobalConstants), document, "draft", ["seDraftGlobal", "seDraft"]),
            _ => throw new ToolException("wrong_document_type", "This document type has no document options.", "Use scope 'application'."),
        };
    }

    private static IEnumerable<(string Name, int Value)> Entries(Catalog catalog) =>
        Enum.GetNames(catalog.Enum)
            .Select(n => (Name: Strip(n, catalog.Prefixes), Value: Convert.ToInt32(Enum.Parse(catalog.Enum, n), CultureInfo.InvariantCulture)))
            .OrderBy(e => e.Value);

    private static (string Name, int Value) Find(Catalog catalog, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required.");
        }

        string wanted = Strip(name.Trim(), catalog.Prefixes);
        foreach ((string Name, int Value) entry in Entries(catalog))
        {
            if (string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        throw ToolException.NotFound($"There is no {catalog.Owner} option named '{name}'.", "Search with se_options action 'list' and 'filter'.");
    }

    private static string Strip(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    private static object Read(Catalog catalog, string name, int parameter)
    {
        object? raw = ReadRaw(catalog, parameter, out string? error);
        return new
        {
            name,
            category = catalog.Owner == "application" ? OptionCategories.Of(parameter) : null,
            value = Format(raw),
            meaning = error is null ? Meaning(name, raw) : null,
            color = error is null && IsColor(name) ? Hex(raw) : null,
            error,
        };
    }

    private static object? ReadRaw(Catalog catalog, int parameter, out string? error)
    {
        error = null;
        try
        {
            return Late.CallByRef(catalog.Target, "GetGlobalParameter", [parameter, null], 1)[1];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            error = ComErrors.Unwrap(ex) is COMException com ? $"0x{com.HResult:X8} {com.Message}" : ComErrors.Unwrap(ex).Message;
            return null;
        }
    }

    private static object? Format(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or int or double or float or short or long or uint or ushort or byte or sbyte or ulong => value,
        Array array => array.Cast<object?>().Select(Format).ToArray(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Describe(object? value) => value switch
    {
        null => "(empty)",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static bool IsColor(string name) => name.Contains("Color", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Opacity", StringComparison.OrdinalIgnoreCase) && !name.Contains("Scheme", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Theme", StringComparison.OrdinalIgnoreCase) && !name.Contains("Mode", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("ColorManager", StringComparison.OrdinalIgnoreCase);

    /// <summary>OLE color (0x00BBGGRR) as #RRGGBB.</summary>
    private static string? Hex(object? value)
    {
        long? bgr = value switch
        {
            int i when i >= 0 => i,
            uint u => u,
            long l when l >= 0 => l,
            double d when d >= 0 && d == Math.Floor(d) && d <= 0xFFFFFF => (long)d,
            _ => null,
        };
        return bgr is { } c && c <= 0xFFFFFF ? Rgb.HexOfColorRef((int)c) : null;
    }

    private static object ConvertValue(JsonElement value, object? current, bool color)
    {
        if (color && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.StartsWith('#'))
        {
            int bgr = Rgb.ColorRef(text);
            return current switch
            {
                uint => (uint)bgr,
                double => (double)bgr,
                _ => bgr,
            };
        }

        try
        {
            return current switch
            {
                bool => value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => value.GetDouble() != 0,
                    JsonValueKind.String => bool.Parse(value.GetString() ?? ""),
                    _ => throw new FormatException(),
                },
                int => Convert.ToInt32(Number(value), CultureInfo.InvariantCulture),
                short => Convert.ToInt16(Number(value), CultureInfo.InvariantCulture),
                ushort => Convert.ToUInt16(Number(value), CultureInfo.InvariantCulture),
                byte => Convert.ToByte(Number(value), CultureInfo.InvariantCulture),
                uint => Convert.ToUInt32(Number(value), CultureInfo.InvariantCulture),
                long => Convert.ToInt64(Number(value), CultureInfo.InvariantCulture),
                double => Number(value),
                float => (float)Number(value),
                string => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => value.GetRawText(),
                },
                _ => value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when value.TryGetInt32(out int i) => i,
                    JsonValueKind.Number => value.GetDouble(),
                    _ => throw new FormatException(),
                },
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            throw ToolException.InvalidArgument($"The value {value.GetRawText()} does not fit the option type ({current?.GetType().Name ?? "unknown"}).",
                "Pass a value of the same type as the current one (se_options get); colours accept #RRGGBB.");
        }
    }

    private static double Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => double.Parse(value.GetString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => throw new FormatException(),
    };

    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is bool || b is bool)
        {
            return Convert.ToBoolean(a, CultureInfo.InvariantCulture) == Convert.ToBoolean(b, CultureInfo.InvariantCulture);
        }

        if (a is string || b is string)
        {
            return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        double x = Convert.ToDouble(a, CultureInfo.InvariantCulture);
        double y = Convert.ToDouble(b, CultureInfo.InvariantCulture);
        return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
    }

    private static IEnumerable<UnitOfMeasure> UnitsOf(SolidEdgeDocument document)
    {
        UnitsOfMeasure units = document.UnitsOfMeasure;
        for (int i = 1; i <= units.Count; i++)
        {
            yield return units.Item(i);
        }
    }

    private static object DescribeUnit(UnitOfMeasure unit) => new
    {
        type = TypeName(unit.Type),
        unit = UnitName(unit.Type, unit.Units),
        unitValue = unit.Units,
        precision = unit.Precision,
    };

    /// <summary>igUnitLinearVelocity → linear_velocity.</summary>
    private static string TypeName(UnitTypeConstants type) =>
        Snake(type.ToString().Replace("igUnit", "", StringComparison.Ordinal));

    private static string Key(string text) => text.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>Enumeration of the readout units of the type (SolidEdgeConstants.UnitOfMeasure*ReadoutConstants), if it exists.</summary>
    private static Type? ReadoutEnum(UnitTypeConstants type)
    {
        string name = type.ToString().Replace("igUnit", "", StringComparison.Ordinal);
        string[] candidates = name switch
        {
            "Distance" => ["Length"],
            "CoefficientOfThermalExpansion" => ["CoefOfThermalExpansion"],
            "FilmCoefficient" => ["HeatTransferCoefficient"],
            "MassPerLength" => ["LinearDensity"],
            "HeatFluxPerArea" => ["HeatFlux"],
            "HeatCapacity" => ["SpecificHeat"],
            _ => [name],
        };
        return candidates
            .Select(c => InteropCatalog.Enums.FirstOrDefault(t => t.FullName == $"SolidEdgeConstants.UnitOfMeasure{c}ReadoutConstants"))
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>Short name of the unit (seLengthMillimeter → millimeter), or <c>null</c> if there is no enumeration.</summary>
    private static string? UnitName(UnitTypeConstants type, int value)
    {
        Type? readout = ReadoutEnum(type);
        if (readout is null)
        {
            return null;
        }

        string? name = Enum.GetName(readout, Enum.ToObject(readout, value));
        return name is null || name.Length <= UnitPrefix(readout).Length ? null : Snake(name[UnitPrefix(readout).Length..]);
    }

    private static int UnitValue(UnitTypeConstants type, string unit)
    {
        if (int.TryParse(unit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
        {
            return raw;
        }

        Type readout = ReadoutEnum(type)
            ?? throw ToolException.InvalidArgument($"Unit names are not known for '{TypeName(type)}'.", "Pass the numeric unit value instead.");
        string prefix = UnitPrefix(readout);
        string wanted = unit.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        foreach (string name in Enum.GetNames(readout))
        {
            if (string.Equals(name[prefix.Length..], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt32(Enum.Parse(readout, name), CultureInfo.InvariantCulture);
            }
        }

        throw ToolException.InvalidArgument($"Unknown {TypeName(type)} unit '{unit}'.",
            $"Use one of: {string.Join(", ", Enum.GetNames(readout).Select(n => Snake(n[prefix.Length..])))}.");
    }

    /// <summary>Common prefix of the enumeration names, cut at a word boundary (seLength, seMass...).</summary>
    private static string UnitPrefix(Type readout)
    {
        string[] names = Enum.GetNames(readout);
        int length = names.Min(n => n.Length);
        int common = 0;
        while (common < length && names.All(n => n[common] == names[0][common]))
        {
            common++;
        }

        while (common > 0 && !names.All(n => n.Length > common && char.IsUpper(n[common])))
        {
            common--;
        }

        return names[0][..common];
    }
}
