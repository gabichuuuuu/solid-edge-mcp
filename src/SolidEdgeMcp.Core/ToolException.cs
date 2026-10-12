// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

namespace SolidEdgeMcp.Core;

/// <summary>
/// Actionable error returned to the MCP client. The message must not contain sensitive data
/// or stack traces: the server passes it on to the model as is.
/// </summary>
public sealed class ToolException : Exception
{
    public ToolException(string code, string message, string? nextStep = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        NextStep = nextStep;
    }

    /// <summary>Stable code in snake_case (e.g. <c>path_not_allowed</c>).</summary>
    public string Code { get; }

    /// <summary>What the client can do to resolve it.</summary>
    public string? NextStep { get; }

    /// <summary>Full text for the client: code, cause and next step.</summary>
    public string ToClientMessage() =>
        NextStep is null ? $"[{Code}] {Message}" : $"[{Code}] {Message} Next step: {NextStep}";

    public static ToolException InvalidArgument(string message, string? nextStep = null) =>
        new("invalid_argument", message, nextStep);

    public static ToolException NotFound(string message, string? nextStep = null) =>
        new("not_found", message, nextStep);
}
