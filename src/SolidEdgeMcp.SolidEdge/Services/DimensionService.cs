// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_dimension does: the action, the dimension and its tolerance or texts.</summary>
public sealed record DimensionSpec(string Action, string? Dimension, string? Tolerance = null, string? Upper = null, string? Lower = null,
    string? Hole = null, string? Shaft = null, string? Prefix = null, string? Suffix = null, string? Override = null);

/// <summary>
/// Tolerance and texts of a dimension (drawing, sketch or PMI). Verified in Solid Edge 2026 on drawing dimensions: DisplayType
/// igDimDisplayTypeTolerance with PrimaryUpperTolerance "0.1" and PrimaryLowerTolerance "-0.2" reads back as set;
/// igDimDisplayTypeClassfit with HoleClassString "H7" too, and the ISO tolerance table of the view then lists it (60, H7).
/// </summary>
public static partial class DimensionService
{
    public static readonly IReadOnlyList<string> Actions = ["get", "tolerance", "text"];

    public static readonly IReadOnlyList<string> Kinds = ["nominal", "plus_minus", "limits", "fit", "fit_plus_minus", "fit_limits", "basic", "reference"];

    public static object Run(SeContext context, DimensionSpec spec)
    {
        string handle = string.IsNullOrWhiteSpace(spec.Dimension)
            ? throw ToolException.InvalidArgument("'dimension' is required: a dimension handle (se_drawing dimensions, se_drawing dimension, se_sketch_constraint list).")
            : spec.Dimension;
        Dimension dimension = context.Handles.Resolve<Dimension>(handle, "dimension");
        return spec.Action switch
        {
            "get" => Describe(dimension, handle),
            "tolerance" => Tolerance(dimension, handle, spec),
            "text" => Text(dimension, handle, spec),
            _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", Actions)}."),
        };
    }

    private static object Tolerance(Dimension dimension, string handle, DimensionSpec spec)
    {
        DimDispTypeConstants type = spec.Tolerance switch
        {
            "nominal" => DimDispTypeConstants.igDimDisplayTypeNominal,
            "plus_minus" => DimDispTypeConstants.igDimDisplayTypeTolerance,
            "limits" => DimDispTypeConstants.igDimDisplayTypeLimits,
            "fit" => DimDispTypeConstants.igDimDisplayTypeClassfit,
            "fit_plus_minus" => DimDispTypeConstants.igDimDisplayTypeClassPlusMinus,
            "fit_limits" => DimDispTypeConstants.igDimDisplayTypeFitWithLimits,
            "basic" => DimDispTypeConstants.igDimDisplayTypeBasic,
            "reference" => DimDispTypeConstants.igDimDisplayTypeReference,
            null => throw ToolException.InvalidArgument("tolerance requires 'tolerance': the kind of tolerance.", $"Use one of: {string.Join(", ", Kinds)}."),
            _ => throw ToolException.InvalidArgument($"Unknown tolerance '{spec.Tolerance}'.", $"Use one of: {string.Join(", ", Kinds)}."),
        };

        bool deviations = type is DimDispTypeConstants.igDimDisplayTypeTolerance or DimDispTypeConstants.igDimDisplayTypeLimits;
        bool fit = type is DimDispTypeConstants.igDimDisplayTypeClassfit or DimDispTypeConstants.igDimDisplayTypeClassPlusMinus
            or DimDispTypeConstants.igDimDisplayTypeFitWithLimits;
        string? upper = Deviation(spec.Upper, "upper");
        string? lower = Deviation(spec.Lower, "lower");
        if (deviations && (upper is null || lower is null))
        {
            throw ToolException.InvalidArgument($"'{spec.Tolerance}' requires 'upper' and 'lower': the deviations in the dimension's units, e.g. \"0.1\" and \"-0.2\".");
        }

        string? hole = FitClass(spec.Hole, "hole", upperCase: true);
        string? shaft = FitClass(spec.Shaft, "shaft", upperCase: false);
        if (fit && hole is null && shaft is null)
        {
            throw ToolException.InvalidArgument($"'{spec.Tolerance}' requires a fit: 'hole' (e.g. H7) and/or 'shaft' (e.g. g6).");
        }

        if (!deviations && (upper is not null || lower is not null) || !fit && (hole is not null || shaft is not null))
        {
            throw ToolException.InvalidArgument($"'{spec.Tolerance}' takes {(deviations ? "'upper' and 'lower'" : fit ? "'hole' and/or 'shaft'" : "no deviations or fit")}.");
        }

        var before = (dimension.DisplayType, dimension.PrimaryUpperTolerance, dimension.PrimaryLowerTolerance, dimension.HoleClassString, dimension.ShaftClassString);
        dimension.DisplayType = type;
        if (deviations)
        {
            dimension.PrimaryUpperTolerance = upper!;
            dimension.PrimaryLowerTolerance = lower!;
        }

        if (hole is not null)
        {
            dimension.HoleClassString = hole;
        }

        if (shaft is not null)
        {
            dimension.ShaftClassString = shaft;
        }

        bool kept = dimension.DisplayType == type
            && (!deviations || dimension.PrimaryUpperTolerance == upper && dimension.PrimaryLowerTolerance == lower)
            && (hole is null || dimension.HoleClassString == hole) && (shaft is null || dimension.ShaftClassString == shaft);
        if (!kept)
        {
            dimension.DisplayType = before.DisplayType;
            dimension.PrimaryUpperTolerance = before.PrimaryUpperTolerance;
            dimension.PrimaryLowerTolerance = before.PrimaryLowerTolerance;
            dimension.HoleClassString = before.HoleClassString;
            dimension.ShaftClassString = before.ShaftClassString;
            throw new ToolException("value_rejected", "Solid Edge did not keep the tolerance; the dimension was put back as it was.",
                "Check the deviations (numbers in the dimension's units) and the fit classes (e.g. H7, g6).");
        }

        return Describe(dimension, handle);
    }

    private static object Text(Dimension dimension, string handle, DimensionSpec spec)
    {
        if (spec.Prefix is null && spec.Suffix is null && spec.Override is null)
        {
            throw ToolException.InvalidArgument("text needs 'prefix', 'suffix' or 'override' (an empty text clears it).");
        }

        if (spec.Prefix is not null)
        {
            dimension.PrefixString = spec.Prefix;
            dimension.EnablePrefix = spec.Prefix.Length > 0;
        }

        if (spec.Suffix is not null)
        {
            dimension.SuffixString = spec.Suffix;
        }

        if (spec.Override is not null)
        {
            dimension.OverrideString = spec.Override;
        }

        return Describe(dimension, handle);
    }

    private static string? Deviation(string? text, string name)
    {
        if (text is null)
        {
            return null;
        }

        string trimmed = text.Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? trimmed
            : throw ToolException.InvalidArgument($"'{name}' must be a number in the dimension's units, e.g. \"0.1\" or \"-0.05\"; got '{text}'.");
    }

    private static string? FitClass(string? text, string name, bool upperCase)
    {
        if (text is null)
        {
            return null;
        }

        string trimmed = text.Trim();
        return FitPattern().IsMatch(trimmed) && char.IsUpper(trimmed[0]) == upperCase
            ? trimmed
            : throw ToolException.InvalidArgument($"'{name}' must be an ISO fit class with {(upperCase ? "capital letters for a hole (e.g. H7, JS9)" : "small letters for a shaft (e.g. g6, js9)")}; got '{text}'.");
    }

    [GeneratedRegex("^(?:[A-Za-z]{1,2})(?:[0-9]{1,2})$")]
    private static partial Regex FitPattern();

    private static object Describe(Dimension dimension, string handle)
    {
        int units = VariableService.UnitsOf(dimension);
        DimDispTypeConstants type = dimension.DisplayType;
        return new
        {
            handle,
            value = Math.Round(VariableService.FromDatabase(dimension.Value, units), 6),
            unit = VariableService.UnitLabel(units),
            tolerance = type switch
            {
                DimDispTypeConstants.igDimDisplayTypeNominal => "nominal",
                DimDispTypeConstants.igDimDisplayTypeTolerance => "plus_minus",
                DimDispTypeConstants.igDimDisplayTypeLimits => "limits",
                DimDispTypeConstants.igDimDisplayTypeClassfit => "fit",
                DimDispTypeConstants.igDimDisplayTypeClassPlusMinus => "fit_plus_minus",
                DimDispTypeConstants.igDimDisplayTypeFitWithLimits => "fit_limits",
                DimDispTypeConstants.igDimDisplayTypeBasic => "basic",
                DimDispTypeConstants.igDimDisplayTypeReference => "reference",
                _ => type.ToString(),
            },
            upper = Empty(Late.TryGetString(dimension, "PrimaryUpperTolerance")),
            lower = Empty(Late.TryGetString(dimension, "PrimaryLowerTolerance")),
            hole = Empty(Late.TryGetString(dimension, "HoleClassString")),
            shaft = Empty(Late.TryGetString(dimension, "ShaftClassString")),
            prefix = Empty(Late.TryGetString(dimension, "PrefixString")),
            suffix = Empty(Late.TryGetString(dimension, "SuffixString")),
            @override = Empty(Late.TryGetString(dimension, "OverrideString")),
        };
    }

    private static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
