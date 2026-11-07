// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgePart;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Decals: an image wrapped on a face of a part (by label). Verified in Solid Edge 2026: Decals.AddByLabel takes an Image2d
/// placed in a sketch with Images2d.AddDecalImage (an image file path gives E_NOINTERFACE); the decal reads back its face and
/// status. AddByPlanarProjection onto two faces left Solid Edge unstable (RPC_E_SERVERFAULT), so it is not offered.
/// </summary>
public static class DecalService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public static object List(SeContext context, string? documentHandle)
    {
        PartDocument part = Part(context, documentHandle);
        var decals = Late.Items(part.Decals).OfType<Decal>().Select(d => Describe(context, part, d)).ToList();
        return new { document = part.Name, count = decals.Count, decals };
    }

    /// <summary>Wraps an image on a face (by label).</summary>
    public static object Add(SeContext context, string? documentHandle, string? image, string? face, string? plane, double[]? offset, string? name)
    {
        string file = context.Paths.Check(image, "image");
        if (!File.Exists(file) || !ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'image' must be an existing picture ({string.Join(", ", ImageExtensions)}).",
                "Save one with se_view capture 'path', or use a file in an allowed folder.");
        }

        if (string.IsNullOrWhiteSpace(face))
        {
            throw ToolException.InvalidArgument("'face' is required: the face to wrap the image on.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, face);
        PartDocument part = model.Document as PartDocument
            ?? throw new ToolException("wrong_document_type", "Decals are made in part documents.");
        Face target = context.Handles.Resolve(face, "face") as Face
            ?? throw ToolException.InvalidArgument($"'{face}' is not a face.", "Pass a face handle from se_geometry faces.");
        RefPlane refPlane = model.ResolvePlane(context, plane ?? face, "plane");
        Profile profile = model.Sketches.Add().Profiles.Add(refPlane);
        int count = part.Decals.Count;
        Decal decal;
        try
        {
            Image2d picture = profile.Images2d.AddDecalImage(false, file);
            profile.End(ProfileValidationType.igProfileClosed);
            // Not defined: the image has no key points and takes no relation or dimension (verified: AddFix E_INVALIDARG,
            // AddLength E_NOINTERFACE), so Solid Edge always sees its sketch as under-defined; the decal places it.
            decal = part.Decals.AddByLabel(picture, target);
        }
        catch (ToolException)
        {
            TryDelete(profile.Parent);
            throw;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            TryDelete(profile.Parent);
            throw new ToolException("decal_failed", $"Solid Edge could not place the decal: {ex.Message.Trim()}",
                "Use a face of the part and a picture file Solid Edge reads (png, jpg, bmp, tif).", ex);
        }

        if (part.Decals.Count != count + 1 || decal.GetStatusEx(out _) != FeatureStatusConstants.igFeatureOK)
        {
            TryDelete(decal);
            throw new ToolException("decal_failed", "Solid Edge left the decal failed.", "Use a face of the part the picture can lie on.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            decal.Name = name;
        }

        if (offset is { Length: 2 })
        {
            decal.SetOffset(Units.MmToM(offset[0]), Units.MmToM(offset[1]));
        }

        return new { decal = Describe(context, part, decal) };
    }

    public static object Delete(SeContext context, string? documentHandle, string? decalName)
    {
        PartDocument part = Part(context, documentHandle);
        Decal decal = Find(context, part, decalName);
        string removed = decal.Name;
        decal.Delete();
        return new { deleted = removed, count = part.Decals.Count };
    }

    private static Decal Find(SeContext context, PartDocument part, string? decalName)
    {
        if (string.IsNullOrWhiteSpace(decalName))
        {
            throw ToolException.InvalidArgument("'decal' is required: a decal name or handle (action list).");
        }

        if (decalName.Contains(':', StringComparison.Ordinal) && context.Handles.Resolve(decalName, "decal") is Decal byHandle)
        {
            return byHandle;
        }

        List<Decal> decals = [.. Late.Items(part.Decals).OfType<Decal>()];
        return decals.FirstOrDefault(d => string.Equals(d.Name, decalName, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"There is no decal '{decalName}'.{NameMatch.Hint(decals.Select(d => d.Name), decalName)}");
    }

    private static object Describe(SeContext context, PartDocument part, Decal decal)
    {
        int faceCount = (Late.TryGet(decal, "Faces") as Array)?.Length ?? 0;
        return new
        {
            handle = context.Register(decal, "decal", (SolidEdgeFramework.SolidEdgeDocument)part),
            name = decal.Name,
            mapping = decal.DecalMappingType.ToString().Replace("seDecalMapping", "", StringComparison.Ordinal).ToLowerInvariant(),
            status = decal.GetStatusEx(out _) is var status && status == FeatureStatusConstants.igFeatureOK ? "ok" : status.ToString(),
            faces = faceCount,
            offset = new[] { Geo.Mm(decal.OffsetX), Geo.Mm(decal.OffsetY) },
            visible = decal.Visible,
        };
    }

    private static PartDocument Part(SeContext context, string? documentHandle) =>
        context.GetDocument(documentHandle) as PartDocument
            ?? throw new ToolException("wrong_document_type", "Decals are made in part documents.", "Activate or pass a part document.");

    private static void TryDelete(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            Late.Call(value, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Left for the person to see.
        }
    }
}
