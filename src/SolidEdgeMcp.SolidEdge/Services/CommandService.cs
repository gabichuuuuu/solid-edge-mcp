using System.Globalization;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// UI commands (<c>StartCommand</c>), as if the person pressed the button. The identifiers
/// repeat across environments, so a name is preferably resolved in the active environment.
/// </summary>
public static class CommandService
{
    private const string Suffix = "CommandConstants";

    private static readonly Dictionary<string, string> EnvironmentEnums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Part"] = "PartCommandConstants",
        ["SheetMetal"] = "SheetMetalCommandConstants",
        ["Assembly"] = "AssemblyCommandConstants",
        ["Detail"] = "DetailCommandConstants",
        ["Draft"] = "DetailCommandConstants",
        ["Profile"] = "ProfileCommandConstants",
        ["Layout"] = "LayoutCommandConstants",
        ["Weldment"] = "WeldmentCommandConstants",
    };

    private static IEnumerable<Type> CommandEnums =>
        InteropCatalog.Enums.Where(e => e.Name.EndsWith(Suffix, StringComparison.Ordinal)).DistinctBy(e => e.Name);

    /// <summary>
    /// Id of the interop command constant &lt;environment prefix&gt;&lt;name&gt; (e.g. Part + ViewISOView) of an environment, falling
    /// back to the part environment; null when the interop has no such constant.
    /// </summary>
    internal static int? IdOf(string? environment, string name)
    {
        foreach (string env in new[] { environment, "Part" }.OfType<string>())
        {
            string enumName = EnvironmentEnums.GetValueOrDefault(env, env + Suffix);
            string prefix = enumName[..^Suffix.Length];
            Type? constants = CommandEnums.FirstOrDefault(e => string.Equals(e.Name, enumName, StringComparison.OrdinalIgnoreCase));
            string member = prefix + name;
            if (constants is not null && Enum.GetNames(constants).Contains(member, StringComparer.Ordinal))
            {
                return Convert.ToInt32(Enum.Parse(constants, member), CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>Searches commands by text in their name (does not need Solid Edge).</summary>
    public static object Find(string query, string? environment, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw ToolException.InvalidArgument("'query' is required, e.g. 'ISOView', 'Interference' or 'Hole'.");
        }

        IEnumerable<Type> enums = CommandEnums;
        if (!string.IsNullOrWhiteSpace(environment))
        {
            string wanted = EnvironmentEnums.GetValueOrDefault(environment, environment + Suffix);
            enums = enums.Where(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }

        var matches = enums
            .SelectMany(e => Enum.GetNames(e).Select(n => (Enum: e, Name: n)))
            .Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int take = Math.Clamp(limit, 1, 500);
        return new
        {
            query,
            count = matches.Count,
            offset = Math.Max(0, offset),
            commands = matches.Skip(Math.Max(0, offset)).Take(take).Select(m => new
            {
                name = m.Name,
                environment = m.Enum.Name[..^Suffix.Length],
                id = Convert.ToInt64(Enum.Parse(m.Enum, m.Name), CultureInfo.InvariantCulture),
            }).ToList(),
        };
    }

    public static object Run(SeContext context, string command)
    {
        Application app = context.Application;
        string? environment = Late.TryGetString(app, "ActiveEnvironment");
        (string name, string group, int id) = Resolve(command, environment);
        app.StartCommand((SolidEdgeCommandConstants)id);
        return new
        {
            command = name,
            environment = group,
            id,
            activeEnvironment = environment,
            note = "Started as if clicked in Solid Edge. Commands that need picks or a dialog wait for the person; check the result with se_view capture.",
        };
    }

    /// <summary>
    /// Whether a command exists in this installation, is licensed (the Community Edition lacks some) and can run now in an
    /// environment (by default the active one), without running it.
    /// </summary>
    public static object Check(SeContext context, string command, string? environment)
    {
        Application app = context.Application;
        string? active = Late.TryGetString(app, "ActiveEnvironment");
        string env = string.IsNullOrWhiteSpace(environment) ? active ?? "Part" : environment.Trim();
        (string name, string group, int id) = Resolve(command, env);
        bool enabled = app.CommandEnabled(id, env, out bool licensed, out bool unknown);
        return new
        {
            command = name,
            group,
            id,
            environment = env,
            known = !unknown,
            licensed = !unknown && licensed,
            enabled = !unknown && enabled,
            note = unknown
                ? $"Solid Edge does not know the command {id} in the {env} environment."
                : !licensed
                    ? "The licence of this installation does not include the command."
                    : enabled ? null : "The command exists but cannot run now (for example it needs a document of that environment or a selection).",
        };
    }

    /// <summary>Id and name of a command given by name (resolved in <paramref name="environment"/>) or numeric id.</summary>
    internal static (string Name, int Id) ResolveId(string command, string? environment)
    {
        (string name, _, int id) = Resolve(command, environment);
        return (name, id);
    }

    private static (string Name, string Group, int Id) Resolve(string command, string? environment)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw ToolException.InvalidArgument("'command' is required: a name from action 'find' (e.g. 'PartViewISOView') or a numeric id.");
        }

        string text = command.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            return (text, "id", numeric);
        }

        string? enumName = null;
        int dot = text.LastIndexOf('.');
        if (dot > 0)
        {
            enumName = text[..dot];
            text = text[(dot + 1)..];
            if (!enumName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                enumName += Suffix;
            }
        }

        var candidates = CommandEnums
            .Where(e => enumName is null || string.Equals(e.Name, enumName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => Enum.GetNames(e)
                .Where(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase))
                .Select(n => (Name: n, Group: e.Name[..^Suffix.Length], Id: Convert.ToInt32(Enum.Parse(e, n), CultureInfo.InvariantCulture))))
            .ToList();
        if (candidates.Count == 0)
        {
            throw ToolException.NotFound($"There is no command named '{command}'.", "Search with se_command action 'find'.");
        }

        if (candidates.Select(c => c.Id).Distinct().Count() == 1)
        {
            return candidates[0];
        }

        string? preferred = environment is null ? null : EnvironmentEnums.GetValueOrDefault(environment);
        var inEnvironment = candidates.Where(c => preferred is not null && string.Equals(c.Group + Suffix, preferred, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inEnvironment.Count == 1)
        {
            return inEnvironment[0];
        }

        throw ToolException.InvalidArgument($"'{command}' exists in several environments with different ids.",
            $"Qualify it, e.g. '{candidates[0].Group}.{candidates[0].Name}', or pass the numeric id: {string.Join(", ", candidates.Select(c => $"{c.Group}={c.Id}"))}.");
    }
}
