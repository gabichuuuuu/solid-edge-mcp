// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Properties of closed Solid Edge files, read and written without opening them in Solid Edge, through the property
/// library installed with it (SolidEdge.FileProperties, the one behind the Properties tab of Windows Explorer and Design
/// Manager). Verified in Solid Edge 2026: sets and properties are indexed from 0 (the open document's sets from 1); a value
/// written and saved reads back in Design Manager and in Solid Edge, and a Status of 3 (released) makes Solid Edge open the
/// file read-only.
/// </summary>
public static class FilePropertyService
{
    private const string ProgId = "SolidEdge.FileProperties";
    private const string StatusSet = "ExtendedSummaryInformation";

    /// <summary>The document status values of Solid Edge (DocumentStatus), in order.</summary>
    public static readonly string[] StatusNames = ["available", "in_work", "in_review", "released", "baselined", "obsolete"];

    public static object List(SeContext context, string? path, string? set)
    {
        string file = ClosedFile(context, path);
        return WithFile(file, readOnly: true, sets =>
        {
            var result = new List<object>();
            int budget = context.MaxListItems;
            foreach (object properties in Items(sets))
            {
                string name = Late.Get(properties, "Name") as string ?? "";
                if (set is not null && !string.Equals(name, set, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                int position = 0;
                foreach (object property in Items(properties))
                {
                    position++;
                    if (budget-- <= 0)
                    {
                        break;
                    }

                    // Some sets have unnamed properties: they are identified by their position, as for an open document.
                    string key = Late.Get(property, "Name") as string is { Length: > 0 } named ? named : $"#{position}";
                    values[key] = ValueOf(property);
                }

                result.Add(new { set = name, count = Late.Count(properties), properties = values });
            }

            if (set is not null && result.Count == 0)
            {
                throw ToolException.NotFound($"There is no property set named '{set}'.", "Omit 'set' to list all sets.");
            }

            return new { file, sets = result, truncated = budget < 0 ? true : (bool?)null };
        });
    }

    /// <summary>Changes a property of a closed file and saves it; a missing custom property is created. The value is read back.</summary>
    public static object Set(SeContext context, string? path, string? set, string name, JsonElement value)
    {
        string file = ClosedFile(context, path);
        object converted = PropertyService.ConvertValue(value);
        (string setName, string propertyName, bool created) = WithFile(file, readOnly: false, sets =>
        {
            (object properties, object? property) = string.IsNullOrWhiteSpace(set)
                ? Standard(sets, name) ?? (FindSet(sets, PropertyService.CustomSet), null)
                : (FindSet(sets, set), null);
            property ??= Find(properties, name);
            string setName = Late.Get(properties, "Name") as string ?? "";
            bool created = false;
            if (property is not null)
            {
                converted = PropertyService.ForProperty(converted, Late.TryGet(property, "Value") is DateTime, name);
                Late.Set(property, "Value", converted is DateTime date ? ToFile(date) : converted);
            }
            else if (string.Equals(setName, PropertyService.CustomSet, StringComparison.OrdinalIgnoreCase))
            {
                property = Late.Call(properties, "Add", name, converted)!;
                created = true;
            }
            else
            {
                throw ToolException.NotFound($"'{name}' does not exist in the '{setName}' set.",
                    $"Only the '{PropertyService.CustomSet}' set accepts new properties; list the existing ones with action 'list'.");
            }

            string propertyName = Late.Get(property, "Name") as string ?? name;
            Late.Call(sets, "Save");
            return (setName, propertyName, created);
        });

        object? read = WithFile(file, readOnly: true, sets => Find(FindSet(sets, setName), propertyName) is { } p ? ValueOf(p) : null);
        bool same = converted switch
        {
            double number => read is not null && Math.Abs(Convert.ToDouble(read, CultureInfo.InvariantCulture) - number) < 1e-9,
            DateTime date => read is string text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime back) && back == date,
            _ => string.Equals(Convert.ToString(read, CultureInfo.InvariantCulture), Convert.ToString(converted, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
        };
        return same
            ? new { file, set = setName, name = propertyName, value = read, created }
            : throw new ToolException("write_failed", $"'{propertyName}' of '{file}' reads '{read}' after writing '{converted}'.", "Check that the file is not read-only.");
    }

    public static object Delete(SeContext context, string? path, string name)
    {
        string file = ClosedFile(context, path);
        WithFile(file, readOnly: false, sets =>
        {
            object properties = FindSet(sets, PropertyService.CustomSet);
            object property = Find(properties, name) ?? throw ToolException.NotFound($"There is no custom property '{name}'.");
            Late.Call(property, "Delete");
            Late.Call(sets, "Save");
            return 0;
        });

        bool gone = WithFile(file, readOnly: true, sets => Find(FindSet(sets, PropertyService.CustomSet), name) is null);
        return gone
            ? new { file, deleted = name, set = PropertyService.CustomSet }
            : throw new ToolException("write_failed", $"The custom property '{name}' is still in '{file}' after deleting it.", "Check that the file is not read-only.");
    }

    /// <summary>The document status of closed files (Status of the ExtendedSummaryInformation set), each read back.</summary>
    public static object SetStatus(SeContext context, string[]? files, string status)
    {
        int code = Array.IndexOf(StatusNames, status);
        if (code < 0)
        {
            throw ToolException.InvalidArgument($"Unknown status '{status}'.", $"Use one of: {string.Join(", ", StatusNames)}.");
        }

        if (files is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("status needs 'files' (the files to change).");
        }

        string[] paths = [.. files.Select(f => ClosedFile(context, f, "files"))];
        // Every file is checked before any is written, so a refusal leaves them all as they were.
        List<(string File, int Was)> current = [.. paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => (f, ReadStatus(f)))];
        foreach ((string file, int was) in current)
        {
            // Solid Edge's workflow moves forward (se_documents status: released to available gives E_FAIL); the property
            // could be written backwards, so the same rule is kept here.
            if ((was is 3 or 4 && code < 3) || (was == 5 && code != 5))
            {
                throw new ToolException("status_refused", $"'{Path.GetFileName(file)}' is {StatusNames.ElementAtOrDefault(was)}: Solid Edge's workflow does not take it back to {status}.",
                    "Revise it (se_design_manager revise) to get a new revision to work on.");
            }
        }

        var changed = new List<object>();
        foreach ((string file, int was) in current)
        {
            string before = StatusNames.ElementAtOrDefault(was) ?? "unknown";
            WithFile(file, readOnly: false, sets =>
            {
                object properties = FindSet(sets, StatusSet);
                object property = Find(properties, "Status") ?? throw new ToolException("not_supported", $"'{file}' has no Status property.");
                Late.Set(property, "Value", code);
                if (Find(properties, "StatusChangeDate") is { } date)
                {
                    Late.Set(date, "Value", ToFile(DateTime.Now));
                }

                Late.Call(sets, "Save");
                return 0;
            });

            int after = ReadStatus(file);
            if (after != code)
            {
                throw new ToolException("write_failed", $"The status of '{file}' reads {StatusNames.ElementAtOrDefault(after) ?? after.ToString(CultureInfo.InvariantCulture)} after writing {status}.",
                    "Check that the file is not read-only.");
            }

            changed.Add(new { file, from = before, to = status });
        }

        return new
        {
            status,
            count = changed.Count,
            files = changed,
            note = code is 3 or 4 or 5 ? "Solid Edge opens released, baselined and obsolete documents read-only." : null,
        };
    }

    /// <summary>The document status code of a closed file (0 available ... 5 obsolete), -1 if it has none.</summary>
    internal static int ReadStatus(string file) => WithFile(file, readOnly: true, sets =>
        Find(FindSet(sets, StatusSet), "Status") is { } property && Late.Get(property, "Value") is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : -1);

    /// <summary>A text property of a closed file, or null.</summary>
    internal static string? ReadText(string file, string set, string name) => WithFile(file, readOnly: true, sets =>
        Find(FindSet(sets, set), name) is { } property ? Convert.ToString(Late.Get(property, "Value"), CultureInfo.InvariantCulture) : null);

    /// <summary>A closed Solid Edge file of the allowed folders: open documents keep their own properties, which saving them writes back.</summary>
    internal static string ClosedFile(SeContext context, string? path, string argument = "path")
    {
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument($"'{argument}' is required."), argument);
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file does not exist: '{file}'.");
        }

        if (DocumentService.FindOpen(context, file) is { } open)
        {
            throw new ToolException("document_open", $"'{open.Name}' is open in Solid Edge.",
                "Pass its handle as 'document' instead of 'path' (and save it), or close it first.");
        }

        return file;
    }

    private static T WithFile<T>(string file, bool readOnly, Func<object, T> work)
    {
        Type type = Type.GetTypeFromProgID(ProgId)
            ?? throw new ToolException("not_available", "The Solid Edge file property library (SolidEdge.FileProperties) is not registered.", "Repair the Solid Edge installation.");
        object sets = Activator.CreateInstance(type)!;
        T result;
        try
        {
            Late.Call(sets, "Open", file, readOnly);
            try
            {
                result = work(sets);
            }
            finally
            {
                Late.Call(sets, "Close");
            }
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com)
        {
            throw new ToolException("file_properties_failed", $"Could not {(readOnly ? "read" : "write")} the properties of '{Path.GetFileName(file)}': {com.Message.Trim()}",
                readOnly ? "Check that it is a Solid Edge document." : "Check that it is not read-only or open in another program.");
        }
        finally
        {
            Marshal.FinalReleaseComObject(sets);
        }

        return result;
    }

    /// <summary>The items of a set or of the sets, indexed from 0 in this library.</summary>
    private static IEnumerable<object> Items(object collection)
    {
        int count = Late.Count(collection);
        for (int i = 0; i < count; i++)
        {
            if (Late.Get(collection, "Item", i) is { } item)
            {
                yield return item;
            }
        }
    }

    private static object FindSet(object sets, string name) =>
        Items(sets).FirstOrDefault(s => string.Equals(Late.Get(s, "Name") as string, name, StringComparison.OrdinalIgnoreCase))
        ?? throw ToolException.NotFound($"There is no property set named '{name}'.", "List the sets with action 'list'.");

    private static object? Find(object properties, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required.");
        }

        return Items(properties).FirstOrDefault(p => string.Equals(Late.Get(p, "Name") as string, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The property of that name in a set other than the custom one (Title, Comments, Project Name...), as for an open document.</summary>
    private static (object Set, object? Property)? Standard(object sets, string name)
    {
        foreach (object properties in Items(sets))
        {
            if (!string.Equals(Late.Get(properties, "Name") as string, PropertyService.CustomSet, StringComparison.OrdinalIgnoreCase)
                && Find(properties, name) is { } property)
            {
                return (properties, property);
            }
        }

        return null;
    }

    /// <summary>
    /// The value to write for a local date: the library stores the date it is given as UTC, and it and Solid Edge read it
    /// back converted to local time with the current offset (verified: 10:30 written read 12:30 at UTC+2, also for a date
    /// in winter), so the date is written less that offset.
    /// </summary>
    private static DateTime ToFile(DateTime local) => DateTime.SpecifyKind(local - TimeZoneInfo.Local.GetUtcOffset(DateTime.Now), DateTimeKind.Unspecified);

    private static object? ValueOf(object property)
    {
        object? value;
        try
        {
            value = Late.Get(property, "Value");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }

        return PropertyService.Normalize(value);
    }
}
