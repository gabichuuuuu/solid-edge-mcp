// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Alternate positions and families of assemblies: one assembly file with named members, each with its own positions
/// (alternate positions) or its own set of components (family). Verified in Solid Edge 2026: AssemblyFamilyMembers fails
/// with E_FAIL on a plain assembly; CreateFamilyFile turns a saved assembly into one with two members (the document then
/// reads "file.asm!Master"); a member keeps the positions or the components it had while it was active; NewMember adds a
/// member and activates it; RenameMember fails with STG_E_FILENOTFOUND until the new member has been saved; SaveMember
/// writes a plain assembly of one member.
/// </summary>
public static class AssemblyFamilyService
{
    private const int StorageFileNotFound = unchecked((int)0x80030002);

    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        return Describe(context, assembly);
    }

    /// <summary>Turns a saved plain assembly into one with alternate positions or a family, with two members.</summary>
    public static object Create(SeContext context, string? documentHandle, string kind, string[]? names)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (names is not { Length: 2 } || names.Any(string.IsNullOrWhiteSpace) || string.Equals(names[0], names[1], StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("'members' must give two different names, e.g. [\"Closed\",\"Open\"].");
        }

        if (Kind(assembly) is not "none")
        {
            throw new ToolException("family_exists", "The assembly already has alternate positions or is a family.", "Add a member with action 'add'.");
        }

        if (DocumentService.PathOf((SolidEdgeDocument)assembly) is null || ((SolidEdgeDocument)assembly).Dirty)
        {
            throw new ToolException("not_saved", "Solid Edge makes the members from the saved assembly file.", "Save it first (se_documents save_as or save).");
        }

        assembly.CreateFamilyFile(names[0].Trim(), names[1].Trim(), kind == "alternate_positions");
        string made = Kind(assembly);
        if (made != kind || MembersOf(assembly).Count != 2)
        {
            throw new ToolException("family_failed", $"Solid Edge made '{made}' instead of '{kind}' with two members.");
        }

        return Describe(context, assembly);
    }

    /// <summary>Adds a member, made from the active one, and activates it.</summary>
    public static object Add(SeContext context, string? documentHandle, string? name)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        string member = RequireName(name, "name");
        if (MembersOf(assembly).Any(m => string.Equals(m.MemberName, member, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("member_exists", $"There is already a member '{member}'.");
        }

        members.NewMember(member);
        if (!string.Equals(members.ActiveMember, member, StringComparison.Ordinal))
        {
            throw new ToolException("family_failed", $"Solid Edge did not add the member '{member}'.");
        }

        return Describe(context, assembly);
    }

    public static object Activate(SeContext context, string? documentHandle, string? member)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        string name = Existing(assembly, member);
        members.ActivateMember(name);
        if (!string.Equals(members.ActiveMember, name, StringComparison.Ordinal))
        {
            throw new ToolException("family_failed", $"Solid Edge kept '{members.ActiveMember}' active.");
        }

        return Describe(context, assembly);
    }

    public static object Rename(SeContext context, string? documentHandle, string? member, string? newName)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        string name = Existing(assembly, member);
        string target = RequireName(newName, "newName");
        if (MembersOf(assembly).Any(m => string.Equals(m.MemberName, target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("member_exists", $"There is already a member '{target}'.");
        }

        try
        {
            members.RenameMember(name, target);
        }
        catch (COMException ex) when (ex.HResult == StorageFileNotFound)
        {
            throw new ToolException("not_saved", $"Solid Edge renames a member once it has been saved, and '{name}' has not been yet.",
                "Save the assembly (se_documents save), then rename.");
        }

        if (!MembersOf(assembly).Any(m => m.MemberName == target))
        {
            throw new ToolException("family_failed", $"Solid Edge did not rename '{name}'.");
        }

        return Describe(context, assembly);
    }

    public static object Delete(SeContext context, string? documentHandle, string? member)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        string name = Existing(assembly, member);
        if (string.Equals(members.ActiveMember, name, StringComparison.Ordinal))
        {
            throw ToolException.InvalidArgument($"'{name}' is the active member.", "Activate another member first.");
        }

        if (MembersOf(assembly).Count <= 2)
        {
            throw ToolException.InvalidArgument("The assembly would be left with one member.", "Keep at least two members.");
        }

        int before = MembersOf(assembly).Count;
        members.DeleteMember(name);
        if (MembersOf(assembly).Count != before - 1)
        {
            throw new ToolException("family_failed", $"Solid Edge did not delete '{name}'.");
        }

        return Describe(context, assembly);
    }

    /// <summary>Writes one member as a plain assembly file (allowed folders).</summary>
    public static object SaveMember(SeContext context, string? documentHandle, string? member, string? path)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        string name = Existing(assembly, member);
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("Action 'save_member' requires 'path': the .asm file to write."), "path");
        if (!string.Equals(Path.GetExtension(file), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("'path' is an assembly file (.asm).");
        }

        if (string.Equals(file, DocumentService.PathOf((SolidEdgeDocument)assembly), StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("'path' is the assembly itself.", "Give another file name.");
        }

        DateTime before = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        members.SaveMember(name, file);
        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) == before)
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{file}'.");
        }

        return new { member = name, path = file };
    }

    /// <summary>Family members only: leaves components out of a member (they stay in the others).</summary>
    public static object Exclude(SeContext context, string? documentHandle, string? member, string[]? occurrences)
    {
        AssemblyFamilyMembers members = Family(context, documentHandle, out AssemblyDocument assembly);
        if (Kind(assembly) != "family")
        {
            throw ToolException.InvalidArgument("Components are left out of members of a family; alternate positions keep every component.",
                "Move the components in each alternate position instead.");
        }

        string name = Existing(assembly, member);
        if (occurrences is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' is required: the components to leave out of the member.");
        }

        Occurrence[] parts = [.. occurrences.Select(h => Occurrence(context, h, "occurrences"))];
        members.ActivateMember(name);
        AssemblyFamilyMember target = MembersOf(assembly).First(m => m.MemberName == name);
        int before = assembly.Occurrences.Count;
        foreach (Occurrence occurrence in parts)
        {
            target.ExcludeObject(occurrence);
        }

        if (assembly.Occurrences.Count != before - parts.Length)
        {
            throw new ToolException("family_failed", $"The member '{name}' has {assembly.Occurrences.Count} components instead of {before - parts.Length}.");
        }

        foreach (string handle in occurrences)
        {
            context.Handles.Remove(handle, $"it was left out of the member '{name}'");
        }

        return Describe(context, assembly);
    }

    internal static string Kind(AssemblyDocument assembly) =>
        assembly.IsFileAlternatePositionByDocument ? "alternate_positions" : assembly.IsFileFamilyByDocument ? "family" : "none";

    private static object Describe(SeContext context, AssemblyDocument assembly)
    {
        string kind = Kind(assembly);
        if (kind == "none")
        {
            return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), kind, members = Array.Empty<object>() };
        }

        string active = assembly.AssemblyFamilyMembers.ActiveMember;
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            kind,
            active,
            components = assembly.Occurrences.Count,
            members = MembersOf(assembly).Select(m => new
            {
                name = m.MemberName,
                status = m.MemberStatus switch
                {
                    AssemblyFamilyMemberStatusConstants.seAssemblyFamilyMemberStatusUpToDate => "up_to_date",
                    AssemblyFamilyMemberStatusConstants.seAssemblyFamilyMemberStatusNotPopulated => "not_populated",
                    _ => "out_of_date",
                },
                active = m.MemberName == active,
            }).ToList(),
        };
    }

    private static List<AssemblyFamilyMember> MembersOf(AssemblyDocument assembly) =>
        [.. Late.Items(assembly.AssemblyFamilyMembers).OfType<AssemblyFamilyMember>()];

    private static AssemblyFamilyMembers Family(SeContext context, string? documentHandle, out AssemblyDocument assembly)
    {
        assembly = Assembly(context, documentHandle);
        return Kind(assembly) == "none"
            ? throw new ToolException("not_a_family", "The assembly has no alternate positions and is not a family.", "Make one with action 'create'.")
            : assembly.AssemblyFamilyMembers;
    }

    private static string Existing(AssemblyDocument assembly, string? member)
    {
        string name = RequireName(member, "member");
        List<AssemblyFamilyMember> members = MembersOf(assembly);
        return members.FirstOrDefault(m => string.Equals(m.MemberName, name, StringComparison.OrdinalIgnoreCase))?.MemberName
            ?? throw ToolException.NotFound($"There is no member '{name}'.", $"Members: {string.Join(", ", members.Select(m => m.MemberName))}.");
    }

    private static string RequireName(string? name, string argument) =>
        string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument($"'{argument}' is required: a member name.") : name.Trim();
}
