// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Feature recognition on synchronous bodies, as the Recognize Holes, Recognize Patterns and Recognize Chamfers commands:
/// geometry with no history (an imported part moved to synchronous, or synchronous cuts) becomes hole, pattern and chamfer
/// features. Verified in Solid Edge 2026 on a STEP part moved to synchronous (se_feature_edit move_to_synchronous):
/// <list type="bullet">
/// <item>Holes.RecognizeAndCreateHoleGroups(1, [design body], out n, ref holes) made one hole feature per cylindrical cut (three
/// through holes: 3); the cylindrical faces as entities give E_FAIL, and an ordered body 0x80004021 (not supported).</item>
/// <item>Patterns.RecognizeAndCreatePatterns(n, features, ref made, ref patterns) made one pattern of three holes in a row; a
/// single feature gives E_FAIL.</item>
/// <item>Chamfers.RecognizeAndCreateChamfers(out chains, ref chamfers, body, 0, ref empty) made one chamfer per chain (two
/// chamfered edges: 2); without the optional arguments the call fails before Solid Edge, and a body without chamfers gives
/// E_FAIL.</item>
/// </list>
/// None of them changes the volume.
/// </summary>
public static class RecognizeService
{
    private const int NotSupported = unchecked((int)0x80004021);

    public static object Run(SeContext context, string? documentHandle, RecognizeAction action, string[]? features)
    {
        string what = ArgumentNames.Snake(action);
        ModelDocument model = features is { Length: > 0 } ? ModelDocument.For(context, documentHandle, features[0]) : ModelDocument.Get(context, documentHandle);
        Model target = model.RequireModel();
        if (Late.Items(target.Features).FirstOrDefault(IsOrdered) is { } ordered)
        {
            throw new ToolException("not_supported", $"Solid Edge recognises {what} only on synchronous geometry, and '{Late.TryGetString(ordered, "Name")}' of '{model.Document.Name}' is an ordered feature.",
                "Move the ordered features to synchronous first: se_feature_edit move_to_synchronous on the first of them (se_feature_tree).");
        }

        bool switched = model.EnsureSynchronous();
        double before = model.Volume();
        object[] made;
        try
        {
            made = action switch
            {
                RecognizeAction.Holes => Holes(target),
                RecognizeAction.Patterns => Patterns(context, target, features),
                RecognizeAction.Chamfers => Chamfers(target),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException { HResult: NotSupported })
        {
            model.RestoreOrdered(switched);
            throw new ToolException("not_supported", $"Solid Edge cannot recognise {what} on ordered features of '{model.Document.Name}'.",
                "Move the body feature to synchronous first: se_feature_edit move_to_synchronous (se_feature_tree gives its handle).", ex);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            model.RestoreOrdered(switched);
            throw new ToolException("nothing_recognized", $"Solid Edge recognised no {what} in '{model.Document.Name}' ({com.Message.Trim()}).",
                action switch
                {
                    RecognizeAction.Holes => "Recognition looks for cylindrical cuts in the synchronous design body.",
                    RecognizeAction.Patterns => "Give two or more alike features in a regular layout (default: all the holes; se_feature_tree).",
                    _ => "Recognition looks for flat bevels between faces of the synchronous design body.",
                }, ex);
        }

        context.ModelChanged(model.Document, $"the model changed (recognise {what})");
        double after = model.Volume();
        if (made.Length == 0)
        {
            model.RestoreOrdered(switched);
            throw new ToolException("nothing_recognized", $"Solid Edge recognised no {what} in '{model.Document.Name}'.");
        }

        return new
        {
            recognized = made.Length,
            features = made.Select(f => Describe(context, model, f)).ToArray(),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            note = switched ? "The document was switched to synchronous modelling, where recognition works." : null,
        };
    }

    private static bool IsOrdered(object feature) =>
        Late.TryGet(feature, "ModelingModeType") is { } mode && Convert.ToInt32(mode, System.Globalization.CultureInfo.InvariantCulture) == (int)ModelingModeConstants.seModelingModeOrdered;

    private static object[] Holes(Model target)
    {
        Array entities = new object[] { target.Body };
        Array holes = Array.Empty<object>();
        target.Holes.RecognizeAndCreateHoleGroups(1, ref entities, out int count, ref holes);
        return Made(holes, count);
    }

    private static object[] Patterns(SeContext context, Model target, string[]? features)
    {
        object[] input = features is { Length: > 0 }
            ? [.. features.Select(h => context.Handles.Resolve(h, "features"))]
            : [.. Late.Items(target.Holes)];
        if (input.Length < 2)
        {
            throw ToolException.InvalidArgument($"patterns needs two or more features to compare; {(features is { Length: > 0 } ? "one was given" : $"the part has {input.Length} hole(s)")}.",
                "Give 'features' (se_feature_tree), or recognise the holes first (action holes).");
        }

        Array array = input;
        int count = 0;
        Array patterns = Array.Empty<object>();
        target.Patterns.RecognizeAndCreatePatterns(input.Length, array, ref count, ref patterns);
        return Made(patterns, count);
    }

    private static object[] Chamfers(Model target)
    {
        Array chamfers = Array.Empty<object>();
        Array required = Array.Empty<object>();
        target.Chamfers.RecognizeAndCreateChamfers(out int count, ref chamfers, target.Body, 0, ref required);
        return Made(chamfers, count);
    }

    private static object[] Made(Array? values, int count) =>
        values is null ? [] : [.. values.Cast<object?>().OfType<object>().Take(Math.Max(count, 0))];

    private static object Describe(SeContext context, ModelDocument model, object feature) => feature switch
    {
        Hole hole => new
        {
            feature = FeatureService.Describe(context, model, hole),
            diameter = Late.TryGet(hole, "HoleData") is { } data && Late.TryGet(data, "HoleDiameter") is double d ? Geo.Mm(d) : (double?)null,
        },
        Pattern pattern => new { feature = FeatureService.Describe(context, model, pattern), occurrences = pattern.NumberOfOccurrences },
        _ => (object)new { feature = FeatureService.Describe(context, model, feature) },
    };
}
