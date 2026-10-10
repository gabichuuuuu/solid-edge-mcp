using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using ModelContextProtocol.Protocol;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.Server.Tools;

namespace SolidEdgeMcp.Server.Tests;

public sealed class ToolRegistrationTests
{
    [Fact]
    public void All_tool_types_register_tools_with_se_prefix()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string root = Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"));
        Program.ConfigureServices(services, new ServerPolicy { StartSolidEdgeIfNotRunning = false }, new AppPaths(root));
        using ServiceProvider provider = services.BuildServiceProvider();

        McpServerTool[] tools = provider.GetServices<McpServerTool>().ToArray();
        string registered = string.Join(", ", services.Select(s => s.ServiceType.Name).Distinct());
        Assert.True(tools.Length > 0, "No McpServerTool services. Registered: " + registered);
        Assert.All(tools, tool => Assert.StartsWith("se_", tool.ProtocolTool.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Enum_arguments_round_trip_in_snake_case()
    {
        Assert.Equal(DocumentAction.SaveAs, JsonSerializer.Deserialize<DocumentAction>("\"save_as\"", Program.ToolJson));
        Assert.Equal(ExtrudeExtent.ThroughAll, JsonSerializer.Deserialize<ExtrudeExtent>("\"through_all\"", Program.ToolJson));
        Assert.Equal("\"sheet_metal\"", JsonSerializer.Serialize(DocumentKind.SheetMetal, Program.ToolJson));
    }

    [Fact]
    public void Tool_schemas_offer_snake_case_enum_values()
    {
        using ServiceProvider provider = BuildProvider();
        Dictionary<string, string> schemas = provider.GetServices<McpServerTool>()
            .ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema.GetRawText());

        Assert.Contains("\"save_as\"", schemas["se_documents"], StringComparison.Ordinal);
        Assert.Contains("\"through_all\"", schemas["se_extrude"], StringComparison.Ordinal);
        Assert.Contains("\"contour_flange\"", schemas["se_sheetmetal"], StringComparison.Ordinal);
        Assert.Contains("\"drawn_cutout\"", schemas["se_sheetmetal_form"], StringComparison.Ordinal);
        Assert.Contains("\"helix\"", schemas["se_loft_sweep"], StringComparison.Ordinal);
        Assert.DoesNotContain("\"SaveAs\"", schemas["se_documents"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("se_documents", """{"action":"saveas"}""", "'action' must be one of:")]
    [InlineData("se_documents", """{"action":"list","colour":"red"}""", "has no argument 'colour'")]
    [InlineData("se_extrude", """{"sketch":"sketch:1","distance":"20mm"}""", "'distance' must be number")]
    [InlineData("se_extrude", """{"operation":"add"}""", "requires 'sketch'")]
    [InlineData("se_sketch", """{"action":"line","sketch":"sketch:1","points":[0,"a",1,1]}""", "'points[1]' must be number")]
    [InlineData("se_sheetmetal", """{"action":"hem","edges":["edge:1"],"hemType":"sflange"}""", "'hemType' must be one of:")]
    [InlineData("se_sheetmetal_form", """{"action":"dimple","depth":4}""", "requires 'sketch'")]
    [InlineData("se_loft_sweep", """{"action":"loft"}""", "requires 'sketches'")]
    [InlineData("se_loft_sweep", """{"action":"helix","sketches":["sketch:1"],"hand":"clockwise"}""", "'hand' must be one of:")]
    public void Invalid_arguments_get_an_actionable_message(string toolName, string json, string expected)
    {
        using ServiceProvider provider = BuildProvider();
        Tool tool = provider.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == toolName).ProtocolTool;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        string? error = ArgumentValidator.Validate(tool, arguments);

        Assert.NotNull(error);
        Assert.StartsWith("[invalid_argument]", error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("se_documents", """{"action":"save_as","path":"C:\\x\\a.par","overwrite":true}""")]
    [InlineData("se_extrude", """{"sketch":"sketch:1","operation":"cut","extent":"through_all","direction":"symmetric"}""")]
    [InlineData("se_round", """{"edges":["edge:1","edge:2"],"radius":2.5}""")]
    [InlineData("se_com_set", """{"target":"app","member":"Visible","value":true}""")]
    [InlineData("se_documents", """{"action":"list","document":null}""")]
    [InlineData("se_sheetmetal", """{"action":"hem","edges":["edge:1"],"hemType":"s_flange","length":10,"side":"bend_outside"}""")]
    [InlineData("se_sheetmetal_form", """{"action":"normal_cutout","sketch":"sketch:1","method":"mid_plane","direction":"auto"}""")]
    [InlineData("se_material", """{"action":"apply","name":"Aluminum, 6061-T6","library":"Materials"}""")]
    [InlineData("se_loft_sweep", """{"action":"loft","operation":"cut","sketches":["sketch:1","sketch:2"],"origins":[0,0,5,0]}""")]
    [InlineData("se_loft_sweep", """{"action":"helix","sketches":["sketch:1"],"axis":"refaxis:2","pitch":8,"turns":5,"hand":"left"}""")]
    public void Valid_arguments_pass(string toolName, string json)
    {
        using ServiceProvider provider = BuildProvider();
        Tool tool = provider.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == toolName).ProtocolTool;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        Assert.Null(ArgumentValidator.Validate(tool, arguments));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string root = Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"));
        Program.ConfigureServices(services, new ServerPolicy { StartSolidEdgeIfNotRunning = false }, new AppPaths(root));
        return services.BuildServiceProvider();
    }
}
