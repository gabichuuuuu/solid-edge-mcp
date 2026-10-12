// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Family of parts of a part or sheet metal document (the master): family variables (variables or dimensions that change
/// between members) and members with their values and suppressed features. Values in tool units (mm, degrees...).
/// Verified: a new member takes the master's current values; Apply gives the master the member's values and suppressions;
/// Populate writes the member file to the member's DocumentName and fails (E_FAIL) while it is empty.
/// </summary>
public static class PartFamilyService
{
    public static object List(SeContext context, string? documentHandle)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        return Describe(context, model, family);
    }

    /// <summary>Adds variables or dimensions (by name) to the family; every member gets the master's current value.</summary>
    public static object AddVariables(SeContext context, string? documentHandle, string[]? names)
    {
        if (names is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("add_variables requires 'variables': names from se_variables list.");
        }

        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        string[] added = names.Where(n => AddVariable(model, family, n)).ToArray();
        return new { added, family = Describe(context, model, family) };
    }

    public static object RemoveVariable(SeContext context, string? documentHandle, string name)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        object variable = FamilyVariables(family).FirstOrDefault(v => SameName(VariableService.NameOf(v), name))
            ?? throw ToolException.NotFound($"'{name}' is not a family variable.", "List them with se_part_family action 'list'.");
        family.RemoveFamilyVariable(variable);
        return new { removed = name, family = Describe(context, model, family) };
    }

    /// <summary>New member with the master's current values, then the given values and suppressed features.</summary>
    public static object AddMember(SeContext context, string? documentHandle, string name, string[]? variables, double[]? values,
        string[]? suppress, string? description)
    {
        CheckName(name);
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        if (FindMember(family, name) is not null)
        {
            throw new ToolException("already_exists", $"The family already has a member named '{name}'.",
                "Use edit_member to change it or choose another name.");
        }

        (string Name, double Value)[] pairs = Pairs(variables, values);
        object[] toSuppress = Features(context, model, suppress, "suppress");
        foreach ((string variable, _) in pairs)
        {
            AddVariable(model, family, variable);
        }

        FamilyMember member = family.Add(name);
        try
        {
            Change(member, pairs, toSuppress, [], description);
        }
        catch
        {
            TryDelete(member);
            throw;
        }

        return new { member = DescribeMember(context, model, member) };
    }

    public static object EditMember(SeContext context, string? documentHandle, string name, string[]? variables, double[]? values,
        string[]? suppress, string[]? unsuppress, string? description, string? newName)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        FamilyMember member = RequireMember(family, name);
        (string Name, double Value)[] pairs = Pairs(variables, values);
        object[] toSuppress = Features(context, model, suppress, "suppress");
        object[] toRestore = Features(context, model, unsuppress, "unsuppress");
        if (pairs.Length == 0 && toSuppress.Length == 0 && toRestore.Length == 0 && description is null && newName is null)
        {
            throw ToolException.InvalidArgument("edit_member needs something to change: 'variables' + 'values', 'suppress', 'unsuppress', 'description' or 'newName'.");
        }

        if (newName is not null)
        {
            CheckName(newName);
            if (!SameName(newName, member.Name) && FindMember(family, newName) is not null)
            {
                throw new ToolException("already_exists", $"The family already has a member named '{newName}'.");
            }
        }

        foreach ((string variable, _) in pairs)
        {
            AddVariable(model, family, variable);
        }

        Change(member, pairs, toSuppress, toRestore, description);
        if (newName is not null)
        {
            member.Name = newName;
        }

        return new { member = DescribeMember(context, model, member) };
    }

    /// <summary>The master takes the member's values and suppressions; both are checked afterwards.</summary>
    public static object Apply(SeContext context, string? documentHandle, string name)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        FamilyMember member = RequireMember(family, name);
        double before = model.Volume();
        member.Apply();
        context.ModelChanged(model.Document, $"the family member '{name}' was applied");

        var wrong = new List<string>();
        foreach ((object variable, double expected) in MemberValues(member))
        {
            double now = Convert.ToDouble(Late.Get(variable, "Value"), CultureInfo.InvariantCulture);
            if (Math.Abs(now - expected) > 1e-9 * Math.Max(1, Math.Abs(expected)))
            {
                int units = VariableService.UnitsOf(variable);
                wrong.Add($"{VariableService.NameOf(variable)} = {Round(VariableService.FromDatabase(now, units))} instead of "
                    + $"{Round(VariableService.FromDatabase(expected, units))} {VariableService.UnitLabel(units)}");
            }
        }

        wrong.AddRange(SuppressedFeatures(member).Where(f => Late.TryGet(f, "Suppress") is false)
            .Select(f => $"{Late.TryGetString(f, "Name")} is not suppressed"));
        if (wrong.Count > 0)
        {
            throw new ToolException("apply_failed", $"After applying '{name}' the master does not match it: {string.Join("; ", wrong)}.",
                "A value may be outside the variable's limits or give an invalid model; check it with se_variables and se_feature_tree.");
        }

        double after = model.Volume();
        return new
        {
            applied = member.Name,
            member = DescribeMember(context, model, member),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
        };
    }

    /// <summary>Writes (or updates) the member's file; the file is checked afterwards.</summary>
    public static object Populate(SeContext context, string? documentHandle, string name, string? path, bool overwrite)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        FamilyMember member = RequireMember(family, name);
        string current = member.DocumentName ?? "";
        string target;
        if (!string.IsNullOrWhiteSpace(path))
        {
            target = context.Paths.Check(path, "path");
        }
        else if (current.Length > 0)
        {
            target = context.Paths.Check(current, "path");
        }
        else
        {
            string master = model.Document.FullName ?? "";
            if (master.Length == 0)
            {
                throw ToolException.InvalidArgument("The master has not been saved, so there is no default folder for the member file.",
                    "Pass 'path' or save the master first (se_documents save_as).");
            }

            target = context.Paths.Check(Path.Combine(Path.GetDirectoryName(master)!, member.Name + Path.GetExtension(master)), "path");
        }

        string extension = model.Kind == "part" ? ".par" : ".psm";
        if (!string.Equals(Path.GetExtension(target), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"The member file of a {model.Kind.Replace('_', ' ')} must be a {extension} file.");
        }

        if (string.Equals(target, model.Document.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("The member file cannot be the master itself.");
        }

        if (File.Exists(target) && !overwrite && !string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("file_exists", $"'{target}' already exists and is not this member's file.",
                "Pass overwrite: true to replace it, or another 'path'.");
        }

        DateTime before = File.Exists(target) ? File.GetLastWriteTimeUtc(target) : DateTime.MinValue;
        member.DocumentName = target;
        try
        {
            member.Populate();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("populate_failed", $"Solid Edge could not write the member file '{target}': {ex.Message}",
                "Check that the folder exists and that the file is not open, and that the member applies without errors (action 'apply').", ex);
        }

        if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < before)
        {
            throw new ToolException("populate_failed", $"Solid Edge reported no error but did not write '{target}'.");
        }

        return new
        {
            member = DescribeMember(context, model, member),
            file = target,
            bytes = new FileInfo(target).Length,
        };
    }

    public static object CopyMember(SeContext context, string? documentHandle, string name, string newName)
    {
        CheckName(newName);
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        FamilyMember source = RequireMember(family, name);
        if (FindMember(family, newName) is not null)
        {
            throw new ToolException("already_exists", $"The family already has a member named '{newName}'.");
        }

        FamilyMember copy = family.Copy(source.Name, newName);
        return new { member = DescribeMember(context, model, copy) };
    }

    public static object DeleteMember(SeContext context, string? documentHandle, string name)
    {
        (ModelDocument model, FamilyMembers family) = Family(context, documentHandle);
        FamilyMember member = RequireMember(family, name);
        string deleted = member.Name;
        member.Delete();
        if (FindMember(family, deleted) is not null)
        {
            throw new ToolException("delete_failed", $"The member '{deleted}' is still in the family.");
        }

        return new { deleted, family = Describe(context, model, family) };
    }

    private static (ModelDocument Model, FamilyMembers Family) Family(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        FamilyMembers family = model.Document is PartDocument part ? part.FamilyMembers : ((SheetMetalDocument)model.Document).FamilyMembers;
        return (model, family);
    }

    private static object Describe(SeContext context, ModelDocument model, FamilyMembers family) => new
    {
        document = context.DocumentHandle(model.Document),
        variables = FamilyVariables(family).Select(v => DescribeVariable(v, Convert.ToDouble(Late.Get(v, "Value"), CultureInfo.InvariantCulture))).ToArray(),
        count = family.Count,
        members = Members(family).Select(m => DescribeMember(context, model, m)).ToArray(),
    };

    private static object DescribeMember(SeContext context, ModelDocument model, FamilyMember member) => new
    {
        name = member.Name,
        description = member.Description is { Length: > 0 } d ? d : null,
        status = Status(member),
        file = member.DocumentName is { Length: > 0 } f ? f : null,
        values = MemberValues(member).Select(p => DescribeVariable(p.Variable, p.Value)).ToArray(),
        suppressed = SuppressedFeatures(member).Select(f => new
        {
            name = Late.TryGetString(f, "Name"),
            handle = context.Register(f, "feature", model.Document),
        }).ToArray(),
    };

    private static object DescribeVariable(object variable, double databaseValue)
    {
        int units = VariableService.UnitsOf(variable);
        return new
        {
            name = VariableService.NameOf(variable),
            value = Round(VariableService.FromDatabase(databaseValue, units)),
            unit = VariableService.UnitLabel(units),
        };
    }

    private static string Status(FamilyMember member)
    {
        string text = member.Status.ToString().Replace("seStatus", "", StringComparison.Ordinal);
        return string.Concat(text.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    }

    private static void Change(FamilyMember member, (string Name, double Value)[] pairs, object[] suppress, object[] unsuppress, string? description)
    {
        (object Variable, double Value)[] current = MemberValues(member).ToArray();
        foreach ((string variableName, double value) in pairs)
        {
            int index = Array.FindIndex(current, p => SameName(VariableService.NameOf(p.Variable), variableName));
            if (index < 0)
            {
                throw ToolException.NotFound($"'{variableName}' is not a family variable of the member '{member.Name}'.");
            }

            double target = VariableService.ToDatabase(value, VariableService.UnitsOf(current[index].Variable));
            member.set_Value(index + 1, target);
            double now = member.get_Value(index + 1);
            if (Math.Abs(now - target) > 1e-9 * Math.Max(1, Math.Abs(target)))
            {
                throw new ToolException("value_rejected", $"Solid Edge kept '{variableName}' of '{member.Name}' at another value.");
            }
        }

        foreach (object feature in suppress)
        {
            member.AddSuppressedFeature(feature);
        }

        foreach (object feature in unsuppress)
        {
            member.RemoveSuppressedFeature(feature);
        }

        if (description is not null)
        {
            member.Description = description;
        }
    }

    private static bool AddVariable(ModelDocument model, FamilyMembers family, string name)
    {
        if (FamilyVariables(family).Any(v => SameName(VariableService.NameOf(v), name)))
        {
            return false;
        }

        object variable = VariableService.Find(model.Document, name);
        family.AddFamilyVariable(variable);
        if (!FamilyVariables(family).Any(v => SameName(VariableService.NameOf(v), VariableService.NameOf(variable))))
        {
            throw new ToolException("not_added", $"Solid Edge did not add '{name}' to the family.",
                "Use a user variable or a driving dimension (se_variables list).");
        }

        return true;
    }

    private static (string Name, double Value)[] Pairs(string[]? variables, double[]? values)
    {
        variables ??= [];
        values ??= [];
        if (variables.Length != values.Length)
        {
            throw ToolException.InvalidArgument($"'variables' ({variables.Length}) and 'values' ({values.Length}) must have the same length.",
                "Example: \"variables\":[\"Length\",\"Width\"],\"values\":[60,30].");
        }

        return variables.Select((n, i) => (n, Units.RequireFinite(values[i], $"values[{i}]"))).ToArray();
    }

    private static object[] Features(SeContext context, ModelDocument model, string[]? handles, string argument) =>
        (handles ?? []).Select(h =>
        {
            object feature = context.Handles.Resolve(h, argument);
            if (Late.TryGet(feature, "Suppress") is not bool)
            {
                throw ToolException.InvalidArgument($"'{h}' in '{argument}' is not a feature that can be suppressed.",
                    "Use feature handles from se_feature_tree.");
            }

            return feature;
        }).ToArray();

    private static IEnumerable<object> FamilyVariables(FamilyMembers family)
    {
        int count = family.FamilyVariableCount;
        for (int i = 1; i <= count; i++)
        {
            yield return family.get_FamilyVariable(i);
        }
    }

    private static IEnumerable<(object Variable, double Value)> MemberValues(FamilyMember member)
    {
        int count = member.VariableCount;
        for (int i = 1; i <= count; i++)
        {
            yield return (member.get_Variable(i), member.get_Value(i));
        }
    }

    private static IEnumerable<object> SuppressedFeatures(FamilyMember member)
    {
        int count = member.SuppressedFeatureCount;
        for (int i = 1; i <= count; i++)
        {
            yield return member.get_SuppressedFeature(i);
        }
    }

    private static IEnumerable<FamilyMember> Members(FamilyMembers family)
    {
        int count = family.Count;
        for (int i = 1; i <= count; i++)
        {
            yield return family.Item(i);
        }
    }

    private static FamilyMember? FindMember(FamilyMembers family, string name) =>
        Members(family).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal))
        ?? Members(family).FirstOrDefault(m => SameName(m.Name, name));

    private static FamilyMember RequireMember(FamilyMembers family, string name) =>
        FindMember(family, name) ?? throw ToolException.NotFound($"The family has no member named '{name}'.",
            "List the members with se_part_family action 'list'.");

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw ToolException.InvalidArgument($"'{name}' is not a valid member name (it also names the member file).");
        }
    }

    private static void TryDelete(FamilyMember member)
    {
        try
        {
            member.Delete();
        }
        catch (COMException)
        {
            // Already gone.
        }
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static double Round(double value) => Math.Round(value, 9);
}
