// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolidEdgeMcp.Core;

/// <summary>Output limits so the client context is not saturated.</summary>
public sealed record PolicyLimits
{
    public int MaxListItems { get; init; } = 500;

    public int MaxImageWidth { get; init; } = 4096;

    public int MaxImageHeight { get; init; } = 4096;

    /// <summary>Maximum number of live handles; the least used ones are evicted.</summary>
    public int MaxHandles { get; init; } = 5000;
}

/// <summary>
/// Per-installation configuration (<c>policy.json</c>). Anything that depends on the machine is configured
/// here; it is never hard-coded.
/// </summary>
public sealed record ServerPolicy
{
    public const string EnvironmentVariable = "SOLID_EDGE_MCP_POLICY";

    /// <summary>Folders where opening, saving, exporting and inserting files is allowed.</summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>If no Solid Edge is open, start it on first use.</summary>
    public bool StartSolidEdgeIfNotRunning { get; init; } = true;

    /// <summary>Maximum time for each COM operation, in seconds.</summary>
    public int OperationTimeoutSeconds { get; init; } = 120;

    /// <summary>Maximum time for Solid Edge to start and register, in seconds.</summary>
    public int StartupTimeoutSeconds { get; init; } = 180;

    public PolicyLimits Limits { get; init; } = new();

    /// <summary>File it was loaded from, or <c>null</c> if the default values are used.</summary>
    [JsonIgnore]
    public string? SourceFile { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Looks for the policy in <c>--policy</c>, in <see cref="EnvironmentVariable"/> or at the default
    /// path. A file given explicitly must exist; the one at the default path is optional.
    /// </summary>
    public static ServerPolicy Load(IReadOnlyList<string> args, AppPaths paths, Func<string, string?> getEnvironmentVariable)
    {
        string? explicitPath = GetPolicyArgument(args) ?? NullIfBlank(getEnvironmentVariable(EnvironmentVariable));
        if (explicitPath is not null)
        {
            explicitPath = Environment.ExpandEnvironmentVariables(explicitPath.Trim('"'));
            if (!Path.IsPathFullyQualified(explicitPath))
            {
                throw new PolicyException($"The policy path must be absolute: '{explicitPath}'.");
            }

            if (!File.Exists(explicitPath))
            {
                throw new PolicyException($"The policy file does not exist: '{explicitPath}'.");
            }

            return Parse(File.ReadAllText(explicitPath), explicitPath);
        }

        return File.Exists(paths.DefaultPolicyFile)
            ? Parse(File.ReadAllText(paths.DefaultPolicyFile), paths.DefaultPolicyFile)
            : new ServerPolicy();
    }

    public static ServerPolicy Parse(string json, string? sourceFile = null)
    {
        ServerPolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<ServerPolicy>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PolicyException($"Invalid policy file{Where(sourceFile)}: {ex.Message}", ex);
        }

        if (policy is null)
        {
            throw new PolicyException($"The policy file{Where(sourceFile)} is empty.");
        }

        policy = policy with
        {
            SourceFile = sourceFile,
            AllowedRoots = policy.AllowedRoots ?? [],
            Limits = policy.Limits ?? new PolicyLimits(),
        };
        policy.Validate();
        return policy;
    }

    private void Validate()
    {
        RequireRange(OperationTimeoutSeconds, 1, 86_400, "operationTimeoutSeconds");
        RequireRange(StartupTimeoutSeconds, 5, 3_600, "startupTimeoutSeconds");
        RequireRange(Limits.MaxListItems, 1, 100_000, "limits.maxListItems");
        RequireRange(Limits.MaxImageWidth, 16, 16_384, "limits.maxImageWidth");
        RequireRange(Limits.MaxImageHeight, 16, 16_384, "limits.maxImageHeight");
        RequireRange(Limits.MaxHandles, 100, 1_000_000, "limits.maxHandles");

        foreach (string root in AllowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new PolicyException("allowedRoots contains an empty entry.");
            }

            if (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(root)))
            {
                throw new PolicyException($"allowedRoots entries must be absolute paths: '{root}'.");
            }
        }
    }

    private static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new PolicyException($"'{name}' must be between {min} and {max} (got {value}).");
        }
    }

    private static string? GetPolicyArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--policy")
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new PolicyException("--policy requires a file path.");
                }

                return args[i + 1];
            }

            if (args[i].StartsWith("--policy=", StringComparison.Ordinal))
            {
                return NullIfBlank(args[i]["--policy=".Length..])
                    ?? throw new PolicyException("--policy requires a file path.");
            }
        }

        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Where(string? sourceFile) => sourceFile is null ? "" : $" '{sourceFile}'";
}

/// <summary>Invalid configuration: the server must not start.</summary>
public sealed class PolicyException(string message, Exception? inner = null) : Exception(message, inner);
