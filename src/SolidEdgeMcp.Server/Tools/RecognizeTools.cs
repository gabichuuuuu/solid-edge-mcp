// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum RecognizeAction
{
    Holes,
    Patterns,
    Chamfers,
}

/// <summary>Feature recognition on synchronous bodies (imported parts moved to synchronous, synchronous cuts).</summary>
[McpServerToolType]
public sealed class RecognizeTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_recognize", Title = "Recognise holes, patterns and chamfers", Destructive = false, OpenWorld = false)]
    [Description("""
        Turns plain geometry of a part or sheet metal part into features, as the Recognize commands of synchronous modelling; the volume does not change.
        holes: cylindrical cuts of the design body become hole features (with their diameter) |
        patterns: 'features' (two or more alike features; default all the holes) in a regular layout become patterns (with their occurrences) |
        chamfers: flat bevels between faces become chamfer features.
        Recognition works on synchronous geometry: the document switches to synchronous; an imported (ordered) body is moved there first with se_feature_edit move_to_synchronous.
        Example: {"action":"holes"}; {"action":"patterns","features":["feature:21","feature:22","feature:23"]}.
        """)]
    public Task<CallToolResult> Recognize(
        RecognizeAction action,
        [Description("patterns: feature handles to look for patterns among (se_feature_tree); default: all the holes of the part.")] string[]? features = null,
        [Description("Document handle; default: the features' document or the active one.")] string? document = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_recognize", Snake(action), c => RecognizeService.Run(c, document, Snake(action), features), cancellationToken);
}
