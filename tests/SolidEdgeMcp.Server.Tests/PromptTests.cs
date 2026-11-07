// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Server.Tests;

public sealed class PromptTests
{
    [Fact]
    public void Workflow_prompts_are_registered_with_their_arguments()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string root = Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"));
        Program.ConfigureServices(services, new ServerPolicy { StartSolidEdgeIfNotRunning = false }, new AppPaths(root));
        using ServiceProvider provider = services.BuildServiceProvider();

        var prompts = provider.GetServices<McpServerPrompt>().ToDictionary(p => p.ProtocolPrompt.Name);

        Assert.Equal(["manufacturing_drawing", "model_part", "part_from_template", "welded_frame"], prompts.Keys.Order());
        Assert.All(prompts.Values, p => Assert.False(string.IsNullOrWhiteSpace(p.ProtocolPrompt.Description)));
        Assert.Contains(prompts["model_part"].ProtocolPrompt.Arguments!, a => a.Name == "description" && a.Required == true);
        Assert.Contains(prompts["model_part"].ProtocolPrompt.Arguments!, a => a.Name == "path" && a.Required != true);
        Assert.Equal(4, prompts["part_from_template"].ProtocolPrompt.Arguments!.Count(a => a.Required == true));
    }
}
