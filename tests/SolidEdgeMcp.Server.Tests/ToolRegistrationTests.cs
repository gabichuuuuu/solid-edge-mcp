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
    public void Tool_descriptions_and_instructions_fit_the_client_limit()
    {
        // Claude Code truncates each tool description and the server instructions at 2,048 characters by default.
        const int limit = 2048;
        var services = new ServiceCollection();
        services.AddLogging();
        string root = Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"));
        Program.ConfigureServices(services, new ServerPolicy { StartSolidEdgeIfNotRunning = false }, new AppPaths(root));
        using ServiceProvider provider = services.BuildServiceProvider();

        string[] tooLong = [.. provider.GetServices<McpServerTool>()
            .Where(t => (t.ProtocolTool.Description?.Length ?? 0) > limit)
            .Select(t => $"{t.ProtocolTool.Name} ({t.ProtocolTool.Description!.Length})")];
        Assert.True(tooLong.Length == 0, "Descriptions over the limit: " + string.Join(", ", tooLong));
        string? instructions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions;
        Assert.InRange(instructions?.Length ?? 0, 1, limit);
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
    [InlineData("se_surface", """{"action":"ruled","sketch":"sketch:4","depth":20}""", "'action' must be one of:")]
    [InlineData("se_surface", """{"action":"stitch","surfaces":"surface:3"}""", "'surfaces'")]
    [InlineData("se_curve", """{"action":"spline","points":[0,0,0,10,0,0]}""", "'action' must be one of:")]
    [InlineData("se_curve", """{"action":"contour","faces":"face:3"}""", "faces")]
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
    [InlineData("se_frame", """{"action":"frame","section":"DIN/Angle/x","points":[0,0,0,1,0,0],"endCondition":"round"}""", "'endCondition' must be one of:")]
    [InlineData("se_frame", """{"action":"library","kind":"wires"}""", "'kind' must be one of:")]
    [InlineData("se_assembly_feature", """{"action":"cut","sketch":"sketch:8","occurrences":["occurrence:2"],"side":"both"}""", "'side' must be one of:")]
    [InlineData("se_assembly_feature", """{"action":"hole","plane":"1","centers":[0,0,0],"diameter":8,"extent":"to_next","occurrences":["occurrence:2"]}""", "'extent' must be one of:")]
    [InlineData("se_part_family", """{"action":"create_member","name":"Short"}""", "'action' must be one of:")]
    [InlineData("se_part_family", """{"action":"add_member","name":"Short","values":"60"}""", "'values'")]
    [InlineData("se_styles", """{"action":"list","type":"color"}""", "'type' must be one of:")]
    [InlineData("se_view", """{"action":"background","kind":"environment"}""", "'kind' must be one of:")]
    [InlineData("se_view", """{"action":"background","kind":"gradient","colors":["#000000"],"gradient":"radial"}""", "'gradient' must be one of:")]
    [InlineData("se_drawing_view", """{"action":"add_break","view":"drawing_view:2","direction":"diagonal","from":120,"to":160}""", "'direction' must be one of:")]
    [InlineData("se_drawing_view", """{"action":"add_break","view":"drawing_view:2","direction":"vertical","from":120,"to":160,"shape":"zigzag"}""", "'shape' must be one of:")]
    [InlineData("se_documents", """{"action":"import","path":"C:\\x\\a.stp","kind":"surface"}""", "'kind' must be one of:")]
    [InlineData("se_sketch_edit", """{"action":"stretch","sketch":"sketch:1","elements":["line2d:2"]}""", "'action' must be one of:")]
    [InlineData("se_assembly", """{"action":"bundle","occurrences":"occurrence:2","path":"C:\\x\\sub.asm"}""", "'occurrences'")]
    [InlineData("se_assembly", """{"action":"explode_subassembly","occurrence":"occurrence:2"}""", "'action' must be one of:")]
    [InlineData("se_links", """{"action":"where_used","path":"C:\\x\\a.par","folders":"C:\\x"}""", "'folders'")]
    [InlineData("se_links", """{"action":"rename","from":"a.par","to":"C:\\x\\b.par"}""", "'action' must be one of:")]
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
    [InlineData("se_documents", """{"action":"import","path":"C:\\x\\a.stp","kind":"part","template":"C:\\x\\iso.par"}""")]
    [InlineData("se_drawing_view", """{"action":"set_display","view":"drawing_view:2","property":"ShowHiddenEdges","value":true,"member":"1"}""")]
    [InlineData("se_drawing_view", """{"action":"add_break","view":"drawing_view:2","direction":"horizontal","from":120,"to":160,"shape":"short_curved_break","gap":5}""")]
    [InlineData("se_drawing_view", """{"action":"set_broken","view":"drawing_view:2","broken":false}""")]
    [InlineData("se_drawing_view", """{"action":"to2d","view":"drawing_view:2"}""")]
    [InlineData("se_sketch_edit", """{"action":"mirror","sketch":"sketch:1","elements":["line2d:2","arc2d:3"],"line":[0,0,0,10],"copy":false}""")]
    [InlineData("se_sketch_edit", """{"action":"trim","sketch":"sketch:1","element":"line2d:2","at":[0,30],"bounds":["line2d:3"]}""")]
    [InlineData("se_sketch_edit", """{"action":"pattern_rectangular","sketch":"sketch:1","elements":["circle2d:4"],"count":3,"rows":2,"spacing":[20,-15],"angle":30}""")]
    [InlineData("se_sketch_edit", """{"action":"pattern_circular","sketch":"sketch:1","elements":["circle2d:4"],"center":[0,0],"count":6,"angle":-90}""")]
    [InlineData("se_sketch_edit", """{"action":"corner","sketch":"sketch:1","elements":["line2d:2","line2d:3"],"keep":[10,0,50,30]}""")]
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
    [InlineData("se_pattern", """{"features":["feature:1"],"type":"curve","curve":"edge:2","count":3,"spacing":20,"reverse":true}""")]
    [InlineData("se_part_feature", """{"action":"offset_faces","faces":["face:9","face:10"],"distance":-2.5}""")]
    [InlineData("se_part_feature", """{"action":"delete_holes","diameter":7}""")]
    [InlineData("se_part_feature", """{"action":"replace_faces","faces":["face:9"],"replaceWith":"surface:4"}""")]
    [InlineData("se_part_feature", """{"action":"split","plane":"refplane:5","bodies":["body:3"]}""")]
    [InlineData("se_part_feature", """{"action":"unite","bodies":["body:3","body:4"]}""")]
    [InlineData("se_part_feature", """{"action":"slot","sketch":"sketch:4","width":10,"depth":5,"squareEnds":true}""")]
    [InlineData("se_part_feature", """{"action":"slot","sketch":"sketch:4","width":10,"counterbore":[16,5]}""")]
    [InlineData("se_part_feature", """{"action":"scale_body","scale":[1,1,0.5]}""")]
    [InlineData("se_part_feature", """{"action":"copy_part","path":"C:\\work\\block.par","scale":[0.5],"plane":"2"}""")]
    [InlineData("se_part_feature", """{"action":"web_network","sketch":"sketch:7","thickness":2}""")]
    [InlineData("se_part_feature", """{"action":"lip","edges":["edge:1","edge:2"],"faces":["face:3"],"width":1,"height":2}""")]
    [InlineData("se_sketch", """{"action":"chamfer","sketch":"sketch:2","elements":["line2d:3","line2d:4"],"setbacks":[5,10]}""")]
    [InlineData("se_sheetmetal", """{"action":"bend_angle","faces":["face:14"],"fixedFace":"face:19","angle":60}""")]
    [InlineData("se_sheetmetal", """{"action":"bend_radius","faces":["face:14","face:15"],"radius":3}""")]
    [InlineData("se_sheetmetal", """{"action":"convert","faces":["face:3"],"edges":["edge:5","edge:6"]}""")]
    [InlineData("se_sheetmetal", """{"action":"gauge","gauge":"16 gage","library":"Materials"}""")]
    [InlineData("se_assembly_config", """{"action":"apply","name":"Without covers","document":"doc:3"}""")]
    [InlineData("se_assembly", """{"action":"activate","all":true,"document":"doc:3"}""")]
    [InlineData("se_assembly", """{"action":"inactivate","occurrence":"occurrence:4"}""")]
    [InlineData("se_assembly", """{"action":"bundle","occurrences":["occurrence:2","occurrence:3"],"path":"C:\\x\\sub.asm","template":"C:\\x\\iso.asm"}""")]
    [InlineData("se_assembly", """{"action":"transfer","occurrences":["occurrence:2"],"subassembly":"occurrence:5"}""")]
    [InlineData("se_assembly", """{"action":"disperse","occurrence":"occurrence:5","all":true}""")]
    [InlineData("se_links", """{"action":"list","recursive":true,"document":"doc:3"}""")]
    [InlineData("se_links", """{"action":"replace","from":"bracket.par","to":"C:\\x\\bracket_v2.par"}""")]
    [InlineData("se_links", """{"action":"break","from":"C:\\x\\block.par"}""")]
    [InlineData("se_links", """{"action":"where_used","path":"C:\\x\\a.par","folders":["C:\\x","C:\\y"],"subfolders":true,"indirect":true}""")]
    [InlineData("se_links", """{"action":"top_level","files":["C:\\x\\a.asm","C:\\x\\b.asm"]}""")]
    [InlineData("se_links", """{"action":"search","folder":"C:\\x","filter":"*.par;*.asm","text":"bracket","subfolders":true,"offset":0,"limit":20}""")]
    [InlineData("se_assembly_config", """{"action":"explode","spread":50,"by":"subassembly"}""")]
    [InlineData("se_frame", """{"action":"library","kind":"pipes","filter":"Elbow","offset":0,"limit":20}""")]
    [InlineData("se_frame", """{"action":"list","document":"doc:3"}""")]
    [InlineData("se_frame", """{"action":"frame","section":"DIN/Square Tubing/SquareTubing 20x20x1.6","points":[0,0,0,500,0,0,500,300,0],"endCondition":"butt1","name":"Rail"}""")]
    [InlineData("se_frame", """{"action":"frame","section":"D:\\CAD\\section.par","lines":["line2d:4","line2d:5"]}""")]
    [InlineData("se_frame", """{"action":"pipe","diameter":114.3,"pipe":"DIN/Pipe/Pipe_100_mm","elbow":"DIN/Fitting/Butt/Butt_Elbow90_100_mm","tee":"t","cross":"c","coupling":"k","points":[0,0,0,500,0,0,500,0,500],"name":"Line A"}""")]
    [InlineData("se_frame", """{"action":"delete","name":"Rail"}""")]
    [InlineData("se_frame", """{"action":"repair","document":"doc:4"}""")]
    [InlineData("se_session", """{"action":"quit"}""")]
    [InlineData("se_documents", """{"action":"templates","kind":"weldment"}""")]
    [InlineData("se_ui", """{"action":"find","query":"Hole","environment":"Part","limit":10}""")]
    [InlineData("se_session", """{"action":"restart"}""")]
    [InlineData("se_assembly_feature", """{"action":"cut","sketch":"sketch:8","occurrences":["occurrence:2","occurrence:3"],"depth":4,"side":"symmetric","name":"Bore"}""")]
    [InlineData("se_assembly_feature", """{"action":"revolve","sketch":"sketch:4","axis":"refaxis:11","occurrences":["occurrence:2"],"angle":180,"side":"normal"}""")]
    [InlineData("se_assembly_feature", """{"action":"hole","plane":"1","centers":[35,20,0,65,-20,0],"diameter":8,"type":"countersink","countersinkDiameter":12,"extent":"finite","depth":4,"direction":"reverse","occurrences":["occurrence:2"]}""")]
    [InlineData("se_assembly_feature", """{"action":"mirror","features":["Cutout 1","Hole 1"],"plane":"3","name":"Mirror A"}""")]
    [InlineData("se_assembly_feature", """{"action":"pattern","features":["Cutout 1"],"plane":"1","count":[3,2],"spacing":[30,15],"angle":90,"name":"Row"}""")]
    [InlineData("se_assembly_feature", """{"action":"protrusion","sketch":"sketch:8","depth":10,"side":"symmetric","name":"Boss"}""")]
    [InlineData("se_assembly_feature", """{"action":"revolved_protrusion","sketch":"sketch:4","axis":"refaxis:11","angle":360,"side":"normal"}""")]
    [InlineData("se_assembly_feature", """{"action":"fillet_weld","faces":["face:19"],"targetFaces":["face:20"],"leg":5,"leg2":3,"document":"doc:16"}""")]
    [InlineData("se_assembly_feature", """{"action":"label_weld","edges":["edge:57","edge:58"],"name":"Seam"}""")]
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
    [InlineData("se_ui", """{"action":"set_display","name":"theme","value":"dark"}""")]
    [InlineData("se_ui", """{"action":"set_display","name":"status_bar","value":false}""")]
    [InlineData("se_options", """{"action":"list","category":"save"}""")]
    [InlineData("se_options", """{"action":"snapshot","path":"C:\\work\\options.json"}""")]
    [InlineData("se_options", """{"action":"restore","path":"C:\\work\\options.json","dryRun":true}""")]
    [InlineData("se_command", """{"action":"check","command":"PartToolsOptions","environment":"Part"}""")]
    [InlineData("se_app", """{"action":"set_addin","addin":"qsSEAddIn.qsSEExporter.1","connect":false}""")]
    [InlineData("se_app", """{"action":"set_template","documentType":"part","path":"C:\\work\\Company.par"}""")]
    [InlineData("se_app", """{"action":"suspend_recent"}""")]
    [InlineData("se_customize", """{"action":"create_ribbon_theme","name":"Mine","basedOn":"0"}""")]
    [InlineData("se_customize", """{"action":"set_ribbon_theme","theme":"Mine","controlSize":"large","controlText":"off"}""")]
    [InlineData("se_customize", """{"action":"add_command","theme":"Mine","environment":"Part","tab":"Home","group":"Clipboard","command":"25042","position":0}""")]
    [InlineData("se_customize", """{"action":"set_item","tab":"Home","group":"Clipboard","control":"25042","visible":false,"newName":"Options"}""")]
    [InlineData("se_customize", """{"action":"add_shortcut","environment":"Part","keys":"Ctrl+Shift+K","command":"PartToolsOptions","replace":true}""")]
    [InlineData("se_variables", """{"action":"limits","name":"Thick","min":5,"max":20,"maxExclusive":true}""")]
    [InlineData("se_variables", """{"action":"limits","name":"Thick","values":[10,20,30]}""")]
    [InlineData("se_part_family", """{"action":"add_member","name":"Short","variables":["Length"],"values":[60],"suppress":["feature:7"],"description":"short"}""")]
    [InlineData("se_part_family", """{"action":"edit_member","name":"Short","unsuppress":["feature:7"],"newName":"Shorter"}""")]
    [InlineData("se_part_family", """{"action":"populate","name":"Short","path":"C:\\work\\Short.par","overwrite":true}""")]
    [InlineData("se_relations", """{"action":"add","type":"angle","element1":"face:3","element2":"face:4","angle":30,"plane":"2"}""")]
    [InlineData("se_relations", """{"action":"add","type":"rigid_set","occurrences":["occurrence:2","occurrence:3"]}""")]
    [InlineData("se_relations", """{"action":"add","type":"parallel","element1":"face:3","element2":"face:4","offset":30}""")]
    [InlineData("se_annotation", """{"action":"center_mark","view":"drawing_view:2","points":[50,30,20]}""")]
    [InlineData("se_annotation", """{"action":"callout","view":"drawing_view:2","points":[50,35,20],"text":"HOLE D10 THRU","position":[240,320]}""")]
    [InlineData("se_annotation", """{"action":"list","kind":"center_mark"}""")]
    [InlineData("se_view", """{"action":"save_view","name":"Detail","description":"Hole view"}""")]
    [InlineData("se_view", """{"action":"apply_view","name":"iso"}""")]
    [InlineData("se_primitive", """{"shape":"box","center":[50,30,0],"size":[100,60],"depth":20}""")]
    [InlineData("se_primitive", """{"shape":"box","center":[0,0,0],"size":[40,20],"depth":10,"angle":30}""")]
    [InlineData("se_primitive", """{"shape":"box","corners":[0,0,0,30,40,0,-8,6,0],"depth":10}""")]
    [InlineData("se_surface", """{"action":"revolve","sketch":"sketch:4","axis":"refaxis:6","angle":180,"direction":"symmetric","endCaps":true}""")]
    [InlineData("se_surface", """{"action":"loft","sketches":["sketch:2","sketch:9"],"origins":[-10,-10,-10,-10],"endCaps":true}""")]
    [InlineData("se_surface", """{"action":"sweep","sketches":["sketch:5"],"path":"curve:7"}""")]
    [InlineData("se_surface", """{"action":"stitch","surfaces":["surface:3","surface:4"],"tolerance":0.01}""")]
    [InlineData("se_surface", """{"action":"bounded","edges":["edge:1","edge:2","edge:3","edge:4"]}""")]
    [InlineData("se_curve", """{"action":"points","points":[0,0,40,100,0,40,100,60,40],"closed":true}""")]
    [InlineData("se_curve", """{"action":"project","sketch":"sketch:4","onto":"face:9","direction":"reverse"}""")]
    [InlineData("se_curve", """{"action":"contour","faces":["face:3"],"points":[30,20,20,70,20,20,50,45,20],"closed":true}""")]
    [InlineData("se_curve", """{"action":"wrap","sketch":"sketch:12","faces":["face:8"]}""")]
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
    [InlineData("se_styles", """{"action":"set","type":"dimension","name":"ISO (mm)","property":"Height","value":0.005}""")]
    [InlineData("se_styles", """{"action":"import","type":"drawing_view","from":"doc:2","replace":true}""")]
    [InlineData("se_styles", """{"action":"list","type":"face","filter":"Blue","offset":0,"limit":10}""")]
    [InlineData("se_view", """{"action":"background","kind":"gradient","colors":["#203040","#FFFFFF"],"gradient":"vertical"}""")]
    [InlineData("se_view", """{"action":"set_camera","eye":[0,0,500],"target":[0,0,0],"up":[0,1,0],"perspective":true,"angle":30,"fit":false}""")]
    [InlineData("se_view", """{"action":"rotate","angle":-30,"axis":[1,0,0]}""")]
    [InlineData("se_view", """{"action":"capture","samples":true,"width":320,"height":240}""")]
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
