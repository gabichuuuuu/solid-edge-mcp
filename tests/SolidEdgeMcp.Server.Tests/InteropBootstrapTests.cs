// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Server.Infrastructure;

namespace SolidEdgeMcp.Server.Tests;

public sealed class InteropBootstrapTests
{
    [Fact]
    public void Sanitize_keeps_a_product_version_as_a_folder_name()
    {
        Assert.Equal("226.00.01.004", InteropBootstrap.Sanitize("226.00.01.004"));
        Assert.Equal("226.00_build_7-beta", InteropBootstrap.Sanitize("226.00 build/7-beta"));
    }

    [Fact]
    public void Complete_requires_every_interop_assembly()
    {
        string folder = Directory.CreateTempSubdirectory("interop-test-").FullName;
        try
        {
            Assert.False(InteropBootstrap.Complete(folder));
            foreach (string name in InteropBootstrap.Assemblies.Skip(1))
            {
                File.WriteAllBytes(Path.Combine(folder, name + ".dll"), []);
            }

            Assert.False(InteropBootstrap.Complete(folder));
            File.WriteAllBytes(Path.Combine(folder, InteropBootstrap.Assemblies[0] + ".dll"), []);
            Assert.True(InteropBootstrap.Complete(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void IsMissing_recognises_an_interop_assembly_that_could_not_be_loaded()
    {
        var missing = new FileNotFoundException("Could not load file or assembly.",
            "Interop.SolidEdgeFramework, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

        Assert.True(InteropBootstrap.IsMissing(missing));
        Assert.True(InteropBootstrap.IsMissing(new System.Reflection.TargetInvocationException(missing)));
        Assert.False(InteropBootstrap.IsMissing(new FileNotFoundException("no file", @"C:\work\plate.par")));
        Assert.False(InteropBootstrap.IsMissing(new InvalidOperationException("other")));
    }

    [Fact]
    public void The_assemblies_list_matches_the_interop_the_server_is_built_against()
    {
        var referenced = typeof(InteropBootstrap).Assembly.GetReferencedAssemblies()
            .Concat(typeof(SolidEdgeMcp.SolidEdge.Com.ComErrors).Assembly.GetReferencedAssemblies())
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Interop.SolidEdge", StringComparison.Ordinal))
            .ToHashSet();

        Assert.NotEmpty(referenced);
        Assert.Subset(InteropBootstrap.Assemblies.ToHashSet(), referenced);
    }
}
