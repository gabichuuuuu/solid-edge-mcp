// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text.Json;

namespace SolidEdgeMcp.SolidEdge.Arguments;

/// <summary>The values of the enumerations the tools take, as their callers write them (snake_case).</summary>
public static class ArgumentNames
{
    public static string Snake(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
