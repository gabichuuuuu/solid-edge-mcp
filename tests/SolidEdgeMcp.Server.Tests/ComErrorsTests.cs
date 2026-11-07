// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.Server.Tests;

public sealed class ComErrorsTests
{
    [Fact]
    public void Translate_turns_a_COM_error_raised_as_an_IO_exception_by_a_call_into_an_actionable_error()
    {
        // STG_E_PATHNOTFOUND reaches .NET as DirectoryNotFoundException (verified: Documents.Add of a weldment without its template).
        var inner = new DirectoryNotFoundException("The path could not be found.") { HResult = unchecked((int)0x80030003) };

        Exception translated = ComErrors.Translate(new TargetInvocationException(inner), "se_com_invoke.Add");

        ToolException error = Assert.IsType<ToolException>(translated);
        Assert.Contains("0x80030003", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_leaves_an_IO_exception_of_the_server_itself_alone()
    {
        var own = new DirectoryNotFoundException("no folder");

        Assert.Same(own, ComErrors.Translate(own, "se_export.export"));
    }

    [Fact]
    public void Translate_keeps_COM_errors()
    {
        Exception translated = ComErrors.Translate(new COMException("Unspecified error", unchecked((int)0x80004005)), "se_com_invoke.AddTube");

        Assert.IsType<ToolException>(translated);
    }

    [Fact]
    public void Translate_points_to_a_restart_after_an_internal_fault_of_Solid_Edge()
    {
        Exception fault = ComErrors.Translate(new COMException("The server threw an exception.", unchecked((int)0x80010105)), "se_region.add");
        Exception generic = ComErrors.Translate(new COMException("Unspecified error", unchecked((int)0x80004005)), "se_model_view.set");

        ToolException faultError = Assert.IsType<ToolException>(fault);
        Assert.Equal("solid_edge_error", faultError.Code);
        Assert.Contains("se_session restart", faultError.NextStep, StringComparison.Ordinal);
        Assert.Contains("se_session restart", Assert.IsType<ToolException>(generic).NextStep, StringComparison.Ordinal);
    }
}
