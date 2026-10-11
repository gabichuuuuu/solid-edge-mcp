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
        Assert.Contains("\"perpendicular\"", schemas["se_sketch_constraint"], StringComparison.Ordinal);
        Assert.DoesNotContain("\"SaveAs\"", schemas["se_documents"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("se_documents", """{"action":"saveas"}""", "'action' must be one of:")]
    [InlineData("se_documents", """{"action":"list","colour":"red"}""", "has no argument 'colour'")]
    [InlineData("se_extrude", """{"sketch":"sketch:1","distance":"20mm"}""", "'distance' must be number")]
    [InlineData("se_extrude", """{"operation":"add"}""", "requires 'sketch'")]
    [InlineData("se_sketch", """{"action":"line","sketch":"sketch:1","points":[0,"a",1,1]}""", "'points[1]' must be number")]
    [InlineData("se_sheetmetal", """{"action":"hem","edges":["edge:1"],"hemType":"sflange"}""", "'hemType' must be one of:")]
    [InlineData("se_sheetmetal", """{"action":"tab","sketch":"sketch:1","direction":"symmetric"}""", "'direction' must be one of:")]
    [InlineData("se_sheetmetal_form", """{"action":"dimple","depth":4}""", "requires 'sketch'")]
    [InlineData("se_loft_sweep", """{"action":"loft"}""", "requires 'sketches'")]
    [InlineData("se_loft_sweep", """{"action":"helix","sketches":["sketch:1"],"hand":"clockwise"}""", "'hand' must be one of:")]
    [InlineData("se_sketch_constraint", """{"action":"relation","sketch":"sketch:1","relation":"perpendicular_to"}""", "'relation' must be one of:")]
    [InlineData("se_sketch_constraint", """{"action":"dimension","sketch":"sketch:1","type":"length"}""", "has no argument 'type'")]
    [InlineData("se_options", """{"action":"get","scope":"global"}""", "'scope' must be one of:")]
    [InlineData("se_ui", """{"action":"arrange","style":"grid"}""", "'style' must be one of:")]
    [InlineData("se_relations", """{"action":"add","type":"gear"}""", "'type' must be one of:")]
    [InlineData("se_annotation", """{"action":"note","text":"A"}""", "'action' must be one of:")]
    [InlineData("se_part_feature", """{"action":"thread","faces":["face:1"]}""", "'action' must be one of:")]
    [InlineData("se_part_feature", """{"action":"rib","sketch":"sketch:1","thickness":6,"side":"left"}""", "'side' must be one of:")]
    [InlineData("se_annotation", """{"action":"list","kind":"note"}""", "'kind' must be one of:")]
    [InlineData("se_annotation", """{"action":"weld","view":"drawing_view:2","points":[50,60,20],"weld":"butt"}""", "'weld' must be one of:")]
    [InlineData("se_annotation", """{"action":"weld","view":"drawing_view:2","points":[50,60,20],"weldSide":"top"}""", "'weldSide' must be one of:")]
    [InlineData("se_primitive", """{"shape":"cone","center":[0,0,0],"radius":5}""", "'shape' must be one of:")]
    [InlineData("se_surface", """{"action":"loft","sketch":"sketch:4","depth":20}""", "'action' must be one of:")]
    [InlineData("se_curve", """{"action":"spline","points":[0,0,0,10,0,0]}""", "'action' must be one of:")]
    [InlineData("se_primitive", """{"shape":"box","operation":"union","center":[0,0,0],"size":[10,10],"depth":5}""", "'operation' must be one of:")]
    [InlineData("se_annotation", """{"action":"feature_control_frame","characteristic":"roundness","tolerance":"0.1","position":[250,150]}""", "'characteristic' must be one of:")]
    [InlineData("se_annotation", """{"action":"surface_finish","view":"drawing_view:2","points":[100,30,20],"finish":"polished"}""", "'finish' must be one of:")]
    [InlineData("se_appearance", """{"action":"paint","color":"#FF0000"}""", "'action' must be one of:")]
    [InlineData("se_assembly_pattern", """{"action":"mirror","occurrences":["occurrence:2"]}""", "'action' must be one of:")]
    [InlineData("se_ref_plane", """{"action":"normal_to_edge","edge":"edge:1","at":"middle"}""", "'at' must be one of:")]
    [InlineData("se_appearance", """{"action":"apply","targets":"face:1","color":"#FF0000"}""", "'targets'")]
    [InlineData("se_layer", """{"action":"hide","name":"Construction"}""", "'action' must be one of:")]
    [InlineData("se_assembly_config", """{"action":"collapse"}""", "'action' must be one of:")]
    [InlineData("se_assembly_config", """{"action":"explode","by":"level"}""", "'by' must be one of:")]
    [InlineData("se_assembly_feature", """{"action":"cut","sketch":"sketch:8","occurrences":["occurrence:2"],"side":"both"}""", "'side' must be one of:")]
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
    [InlineData("se_sheetmetal", """{"action":"tab","sketch":"sketch:1","direction":"reverse"}""")]
    [InlineData("se_documents", """{"action":"new","kind":"weldment"}""")]
    [InlineData("se_geometry", """{"action":"summary","source":"occurrence:3"}""")]
    [InlineData("se_sheetmetal_form", """{"action":"normal_cutout","sketch":"sketch:1","method":"mid_plane","direction":"auto"}""")]
    [InlineData("se_material", """{"action":"apply","name":"Aluminum, 6061-T6","library":"Materials"}""")]
    [InlineData("se_loft_sweep", """{"action":"loft","operation":"cut","sketches":["sketch:1","sketch:2"],"origins":[0,0,5,0]}""")]
    [InlineData("se_loft_sweep", """{"action":"helix","sketches":["sketch:1"],"axis":"refaxis:2","pitch":8,"turns":5,"hand":"left"}""")]
    [InlineData("se_part_feature", """{"action":"rib","sketch":"sketch:1","thickness":6,"side":"symmetric","depth":10}""")]
    [InlineData("se_part_feature", """{"action":"mirror_body","plane":"2"}""")]
    [InlineData("se_part_feature", """{"action":"offset_faces","faces":["face:9","face:10"],"distance":-2.5}""")]
    [InlineData("se_part_feature", """{"action":"delete_holes","diameter":7}""")]
    [InlineData("se_part_feature", """{"action":"slot","sketch":"sketch:4","width":10,"depth":5,"squareEnds":true}""")]
    [InlineData("se_part_feature", """{"action":"slot","sketch":"sketch:4","width":10,"counterbore":[16,5]}""")]
    [InlineData("se_part_feature", """{"action":"scale_body","scale":[1,1,0.5]}""")]
    [InlineData("se_part_feature", """{"action":"copy_part","path":"C:\\work\\block.par","scale":[0.5],"plane":"2"}""")]
    [InlineData("se_part_feature", """{"action":"web_network","sketch":"sketch:7","thickness":2}""")]
    [InlineData("se_part_feature", """{"action":"lip","edges":["edge:1","edge:2"],"faces":["face:3"],"width":1,"height":2}""")]
    [InlineData("se_sketch", """{"action":"chamfer","sketch":"sketch:2","elements":["line2d:3","line2d:4"],"setbacks":[5,10]}""")]
    [InlineData("se_sheetmetal", """{"action":"bend_angle","faces":["face:14"],"fixedFace":"face:19","angle":60}""")]
    [InlineData("se_sheetmetal", """{"action":"bend_radius","faces":["face:14","face:15"],"radius":3}""")]
    [InlineData("se_assembly_config", """{"action":"apply","name":"Without covers","document":"doc:3"}""")]
    [InlineData("se_assembly_config", """{"action":"explode","spread":50,"by":"subassembly"}""")]
    [InlineData("se_assembly_feature", """{"action":"cut","sketch":"sketch:8","occurrences":["occurrence:2","occurrence:3"],"depth":4,"side":"symmetric","name":"Bore"}""")]
    [InlineData("se_appearance", """{"action":"apply","targets":["face:1","occurrence:2"],"color":"#FF8000","opacity":0.5}""")]
    [InlineData("se_appearance", """{"action":"create","name":"Orange","color":"#FF8000"}""")]
    [InlineData("se_assembly_pattern", """{"action":"create","occurrences":["occurrence:5"],"source":"occurrence:3","pattern":"Pattern_1","reference":"Hole_1"}""")]
    [InlineData("se_assembly_pattern", """{"action":"duplicate","occurrences":["occurrence:5"],"source":"occurrence:2","targets":["occurrence:3","occurrence:4"],"name":"Bolts"}""")]
    [InlineData("se_ref_plane", """{"action":"normal_to_edge","edge":"edge:1","at":"end","distance":30}""")]
    [InlineData("se_ref_plane", """{"action":"coordinate_system","origin":[10,20,30],"angles":[0,0,90]}""")]
    [InlineData("se_ref_plane", """{"action":"coordinate_system","edges":["edge:1","edge:2"],"origin":[100,0,20]}""")]
    [InlineData("se_ref_plane", """{"action":"normal_to_edge","edge":"edge:1","ratio":0.25}""")]
    [InlineData("se_ref_plane", """{"action":"tangent","face":"face:3","points":[10,0,30],"flip":true}""")]
    [InlineData("se_ref_plane", """{"action":"tangent","face":"face:3","plane":"3","angle":-30}""")]
    [InlineData("se_sketch", """{"action":"ellipse","sketch":"sketch:1","center":[0,0],"radius":20,"minorRadius":10,"angle":30}""")]
    [InlineData("se_sketch", """{"action":"fillet","sketch":"sketch:1","elements":["line2d:2","line2d:3"],"radius":5}""")]
    [InlineData("se_sketch_constraint", """{"action":"dimension","sketch":"sketch:1","dimension":"distance","elements":["line2d:2","line2d:4"],"keypoints":[0,1],"value":30}""")]
    [InlineData("se_sketch_constraint", """{"action":"relation","sketch":"sketch:1","relation":"connect","elements":["line2d:2","arc2d:3"],"keypoints":[1,1]}""")]
    [InlineData("se_options", """{"action":"set","scope":"application","name":"ColorBackground","value":"#FFFFFF"}""")]
    [InlineData("se_options", """{"action":"set_units","unitType":"distance","unit":"inch","precision":3}""")]
    [InlineData("se_ui", """{"action":"state","window":"doc:2","state":"maximized"}""")]
    [InlineData("se_ui", """{"action":"ribbon","environment":"SheetMetal","tab":"Home"}""")]
    [InlineData("se_variables", """{"action":"limits","name":"Thick","min":5,"max":20,"maxExclusive":true}""")]
    [InlineData("se_variables", """{"action":"limits","name":"Thick","values":[10,20,30]}""")]
    [InlineData("se_relations", """{"action":"add","type":"angle","element1":"face:3","element2":"face:4","angle":30,"plane":"2"}""")]
    [InlineData("se_relations", """{"action":"add","type":"rigid_set","occurrences":["occurrence:2","occurrence:3"]}""")]
    [InlineData("se_relations", """{"action":"add","type":"parallel","element1":"face:3","element2":"face:4","offset":30}""")]
    [InlineData("se_annotation", """{"action":"center_mark","view":"drawing_view:2","points":[50,30,20]}""")]
    [InlineData("se_annotation", """{"action":"callout","view":"drawing_view:2","points":[50,35,20],"text":"HOLE D10 THRU","position":[240,320]}""")]
    [InlineData("se_annotation", """{"action":"list","kind":"center_mark"}""")]
    [InlineData("se_view", """{"action":"save_view","name":"Detalle","description":"Vista del agujero"}""")]
    [InlineData("se_view", """{"action":"apply_view","name":"iso"}""")]
    [InlineData("se_primitive", """{"shape":"box","center":[50,30,0],"size":[100,60],"depth":20}""")]
    [InlineData("se_primitive", """{"shape":"box","center":[0,0,0],"size":[40,20],"depth":10,"angle":30}""")]
    [InlineData("se_primitive", """{"shape":"box","corners":[0,0,0,30,40,0,-8,6,0],"depth":10}""")]
    [InlineData("se_surface", """{"action":"revolve","sketch":"sketch:4","axis":"refaxis:6","angle":180,"direction":"symmetric","endCaps":true}""")]
    [InlineData("se_curve", """{"action":"points","points":[0,0,40,100,0,40,100,60,40],"closed":true}""")]
    [InlineData("se_curve", """{"action":"project","sketch":"sketch:4","onto":"face:9","direction":"reverse"}""")]
    [InlineData("se_primitive", """{"shape":"cylinder","operation":"cut","plane":"face:12","center":[50,30,20],"radius":5,"depth":20,"side":"reverse"}""")]
    [InlineData("se_annotation", """{"action":"surface_finish","view":"drawing_view:2","points":[100,30,20],"finish":"no_material_removal","text":"Ra 3.2"}""")]
    [InlineData("se_annotation", """{"action":"feature_control_frame","view":"drawing_view:2","points":[100,30,20],"characteristic":"total_runout","tolerance":"⌀0.1","datums":["A","B"]}""")]
    [InlineData("se_annotation", """{"action":"list","kind":"feature_control_frame"}""")]
    [InlineData("se_annotation", """{"action":"weld","view":"drawing_view:2","points":[50,60,20],"weld":"v_groove","weldSide":"both","text":"a5","allAround":true,"field":true,"position":[200,350]}""")]
    [InlineData("se_annotation", """{"action":"list","kind":"weld"}""")]
    [InlineData("se_drawing", """{"action":"bend_table","view":"drawing_view:2","position":[40,120],"style":"Normal"}""")]
    [InlineData("se_drawing", """{"action":"place_block","name":"Frame","position":[300,100],"scale":2,"angle":30,"sheet":"Sheet1"}""")]
    [InlineData("se_drawing", """{"action":"create_block","name":"Frame","elements":["line2d:3","circle2d:4"],"position":[50,50]}""")]
    [InlineData("se_annotation", """{"action":"feature_control_frame","characteristic":"position","tolerance":"⌀0.2","datums":["A"],"rows":["⌀0.05|A"],"composite":true,"position":[300,150]}""")]
    [InlineData("se_layer", """{"action":"set","name":"Construction","shown":false,"locatable":false}""")]
    [InlineData("se_layer", """{"action":"assign","name":"Notes","objects":["text_box:4","dimension:5"],"sheet":"Sheet1"}""")]
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
