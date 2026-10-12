// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Solid Edge UI: main window, document windows, environments and ribbon (tabs, groups and buttons with
/// their command id). The ribbon collections start at 0, unlike the rest of the API.
/// </summary>
public static class UiService
{
    public static readonly IReadOnlyList<string> WindowStates = ["normal", "minimized", "maximized"];
    public static readonly IReadOnlyList<string> ArrangeStyles = ["tiled", "horizontal", "vertical", "cascade"];

    public static object Status(SeContext context)
    {
        Application app = context.Application;
        return new
        {
            caption = app.Caption,
            version = app.Version,
            visible = app.Visible,
            state = StateName(app.WindowState),
            activeEnvironment = app.ActiveEnvironment,
            activeDocument = TryActiveDocument(context),
            windows = app.Windows.Count,
            statusBar = Late.TryGetString(app, "StatusBar"),
            displayAlerts = app.DisplayAlerts,
            screenUpdating = app.ScreenUpdating,
            delayCompute = app.DelayCompute,
            interactive = app.Interactive,
        };
    }

    public static readonly IReadOnlyList<string> DisplaySettings =
        ["status_bar", "edge_bar", "full_screen", "startup_screen", "recent_files", "recent_files_count", "theme", "left", "top", "width", "height"];

    /// <summary>
    /// How the Solid Edge window looks: status bar, edge bar (PathFinder pane), full screen, start-up screen, recent files,
    /// colour theme and scheme, interface language, main window geometry and active ribbon theme.
    /// </summary>
    public static object Display(SeContext context)
    {
        Application app = context.Application;
        object? theme = OptionService.ReadApplication(context, ApplicationGlobalConstants.seApplicationGlobalColorTheme);
        object? language = OptionService.ReadApplication(context, ApplicationGlobalConstants.seApplicationGlobalDefaultUserLangID);
        return new
        {
            statusBar = Try(() => app.StatusBarVisible),
            edgeBar = Try(() => app.EdgeBarVisible),
            fullScreen = Try(() => app.DisplayFullScreen),
            startupScreen = Try(() => app.ShowStartupScreen),
            recentFiles = Try(() => app.DisplayRecentFiles),
            recentFilesCount = Try(() => app.DisplayRecentFilesCount),
            theme = OptionService.Meaning("ColorTheme", theme),
            colorScheme = OptionService.ReadApplication(context, ApplicationGlobalConstants.seApplicationGlobalApplicationColorScheme) as string,
            language = Language(language),
            window = new
            {
                state = StateName(app.WindowState),
                left = Try(() => app.Left),
                top = Try(() => app.Top),
                width = Try(() => app.Width),
                height = Try(() => app.Height),
                usableWidth = Try(() => app.UsableWidth),
                usableHeight = Try(() => app.UsableHeight),
            },
            ribbonTheme = ActiveRibbonTheme(app),
            note = "Change them with se_ui action 'set_display' (name, value). Colours and every other option: se_options.",
        };
    }

    /// <summary>Changes one display setting (see <see cref="DisplaySettings"/>) and returns the previous value.</summary>
    public static object SetDisplay(SeContext context, string name, JsonElement value)
    {
        Application app = context.Application;
        string key = name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return key switch
        {
            "status_bar" => Flag(key, () => app.StatusBarVisible, v => app.StatusBarVisible = v, value, null),
            "edge_bar" => Flag(key, () => app.EdgeBarVisible, v => app.EdgeBarVisible = v, value,
                "The edge bar (PathFinder pane) only shows while a document window is open."),
            "full_screen" => Flag(key, () => app.DisplayFullScreen, v => app.DisplayFullScreen = v, value, null),
            "startup_screen" => Flag(key, () => app.ShowStartupScreen, v => app.ShowStartupScreen = v, value, null),
            "recent_files" => Flag(key, () => app.DisplayRecentFiles, v => app.DisplayRecentFiles = v, value, null),
            "recent_files_count" => Number(key, () => app.DisplayRecentFilesCount, v => app.DisplayRecentFilesCount = v, value, null),
            "theme" => OptionService.Set(context, "application", null, "ColorTheme", value),
            "left" => Geometry(app, key, () => app.Left, v => app.Left = v, value),
            "top" => Geometry(app, key, () => app.Top, v => app.Top = v, value),
            "width" => Geometry(app, key, () => app.Width, v => app.Width = v, value),
            "height" => Geometry(app, key, () => app.Height, v => app.Height = v, value),
            _ => throw ToolException.InvalidArgument($"Unknown display setting '{name}'.", $"Use one of: {string.Join(", ", DisplaySettings)}."),
        };
    }

    private static object Flag(string name, Func<bool> get, Action<bool> set, JsonElement value, string? hint)
    {
        bool wanted = value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => throw ToolException.InvalidArgument($"'{name}' takes true or false."),
        };
        bool before = get();
        set(wanted);
        bool after = get();
        if (after != wanted)
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept {name} {(after ? "on" : "off")}.", hint);
        }

        return new { name, previous = before, value = after };
    }

    private static object Number(string name, Func<int> get, Action<int> set, JsonElement value, string? hint)
    {
        int wanted = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : throw ToolException.InvalidArgument($"'{name}' takes a whole number.");
        int before = get();
        set(wanted);
        int after = get();
        if (after != wanted)
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept {name} at {after} instead of {wanted}.", hint);
        }

        return new { name, previous = before, value = after };
    }

    /// <summary>Position or size of the main window in pixels; only a window in the normal state can be moved or sized.</summary>
    private static object Geometry(Application app, string name, Func<int> get, Action<int> set, JsonElement value)
    {
        if (app.WindowState != 0)
        {
            throw new ToolException("ui_failed", $"The main window is {StateName(app.WindowState)}: it cannot be moved or sized.",
                "Set it to normal first with se_ui action 'state' (state normal).");
        }

        return Number(name, get, set, value, "Solid Edge keeps the window inside the screen and above its minimum size.");
    }

    private static object? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static object? Language(object? id)
    {
        if (id is null or DBNull)
        {
            return null;
        }

        int lcid = Convert.ToInt32(id, CultureInfo.InvariantCulture);
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(lcid);
            return new { id = lcid, culture = culture.Name, name = culture.EnglishName };
        }
        catch (CultureNotFoundException)
        {
            return new { id = lcid, culture = (string?)null, name = (string?)null };
        }
    }

    private static string? ActiveRibbonTheme(Application app)
    {
        try
        {
            RibbonBarThemes themes = app.Customization.RibbonBarThemes;
            for (int i = 0; i < themes.Count; i++)
            {
                if (themes.Item(i).Active)
                {
                    return themes.Item(i).Name;
                }
            }
        }
        catch (COMException)
        {
            // Without a ribbon (for example while Solid Edge starts) there is no theme.
        }

        return null;
    }

    public static object Windows(SeContext context)
    {
        Application app = context.Application;
        int active = ActiveHwnd(app);
        var items = new List<object>();
        int index = 0;
        foreach (object item in Late.Items(app.Windows, context.MaxListItems))
        {
            index++;
            items.Add(DescribeWindow(context, item, index, active));
        }

        return new { count = app.Windows.Count, activeEnvironment = app.ActiveEnvironment, windows = items };
    }

    /// <summary>Activates a window (by index, window handle or document handle), as when its tab is clicked.</summary>
    public static object Activate(SeContext context, string window)
    {
        object target = ResolveWindow(context, window);
        Late.Call(target, "Activate");
        int hwnd = Convert.ToInt32(Late.Get(target, "hWnd"), CultureInfo.InvariantCulture);
        if (ActiveHwnd(context.Application) != hwnd)
        {
            throw new ToolException("ui_failed", "Solid Edge did not activate the window.", "List the windows with se_ui 'windows' and try again.");
        }

        return new { activated = DescribeWindow(context, target, null, hwnd), activeEnvironment = context.Application.ActiveEnvironment };
    }

    public static object Arrange(SeContext context, string style)
    {
        ArrangeWindowsStyles value = style switch
        {
            "tiled" => ArrangeWindowsStyles.igWindowsTiled,
            "horizontal" => ArrangeWindowsStyles.igWindowsHorizontal,
            "vertical" => ArrangeWindowsStyles.igWindowsVertical,
            "cascade" => ArrangeWindowsStyles.igWindowsCascade,
            _ => throw ToolException.InvalidArgument($"Unknown arrangement '{style}'.", $"Use one of: {string.Join(", ", ArrangeStyles)}."),
        };
        context.Application.ArrangeWindows(value);
        return Windows(context);
    }

    /// <summary>State of the main window (without <paramref name="window"/>) or of a document window.</summary>
    public static object SetState(SeContext context, string? window, string state)
    {
        int wanted = WindowStates.ToList().IndexOf(state);
        if (wanted < 0)
        {
            throw ToolException.InvalidArgument($"Unknown window state '{state}'.", $"Use one of: {string.Join(", ", WindowStates)}.");
        }

        object target = window is null ? context.Application : ResolveWindow(context, window);
        int before = Convert.ToInt32(Late.Get(target, "WindowState"), CultureInfo.InvariantCulture);
        Late.Set(target, "WindowState", wanted);
        int after = Convert.ToInt32(Late.Get(target, "WindowState"), CultureInfo.InvariantCulture);
        if (after != wanted)
        {
            throw new ToolException("ui_failed", $"Solid Edge kept the window {StateName(after)} instead of {state}.");
        }

        return new { target = window ?? "application", previous = StateName(before), state = StateName(after) };
    }

    public static object Environments(SeContext context)
    {
        Application app = context.Application;
        var items = new List<object>();
        foreach (object item in Late.Items(app.Environments, context.MaxListItems))
        {
            var environment = (SolidEdgeFramework.Environment)item;
            items.Add(new { name = environment.Name, caption = Late.TryGetString(environment, "Caption"), index = environment.Index });
        }

        return new { active = app.ActiveEnvironment, count = app.Environments.Count, environments = items };
    }

    /// <summary>
    /// Ribbon of an environment (by default the active one): its tabs with the groups or, with <paramref name="tab"/>, the groups and
    /// buttons of that tab with their command id (se_command run executes it).
    /// </summary>
    public static object Ribbon(SeContext context, string? environment, string? tab)
    {
        (RibbonBar bar, string env, string theme) = FindRibbon(context, environment);
        RibbonBarTabs tabs = bar.RibbonBarTabs;
        if (tab is null)
        {
            var list = new List<object>();
            for (int i = 0; i < tabs.Count; i++)
            {
                RibbonBarTab t = tabs.Item(i);
                list.Add(new { index = i, name = t.Name, id = t.Id, visible = t.Visible, groups = Groups(t).Select(g => g.Name).ToArray() });
            }

            return new { environment = env, theme, tabs = list };
        }

        RibbonBarTab selected = FindTab(tabs, tab, env);
        var groups = Groups(selected).Select(g => new
        {
            name = g.Name,
            id = g.Id,
            visible = g.Visible,
            controls = Controls(g.RibbonBarControls, 0),
        }).ToList();
        return new
        {
            environment = env,
            theme,
            tab = selected.Name,
            id = selected.Id,
            visible = selected.Visible,
            groups,
            note = "Run a control with se_command action 'run' and its id as 'command'.",
        };
    }

    /// <summary>
    /// Finds buttons by the text they show in the ribbon of an environment (active theme and language of this installation,
    /// all tabs and drop-downs), with where they are and their command id: the ribbon is read each time, so a button that
    /// another installation shows elsewhere or under another caption is still found. Without a match it lists the closest
    /// captions.
    /// </summary>
    public static object FindControl(SeContext context, string? environment, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw ToolException.InvalidArgument("'query' is required: text of the button as Solid Edge shows it (any part of it).");
        }

        (RibbonBar bar, string env, string theme) = FindRibbon(context, environment);
        var found = new List<(string Tab, string Group, string Path, string Name, int Id, bool Visible)>();
        RibbonBarTabs tabs = bar.RibbonBarTabs;
        for (int i = 0; i < tabs.Count; i++)
        {
            RibbonBarTab t = tabs.Item(i);
            foreach (RibbonBarGroup g in Groups(t))
            {
                Collect(g.RibbonBarControls, t.Name, g.Name, "", 0, found);
            }
        }

        var matches = found.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => (c.Tab, c.Group, c.Path, c.Id)).Take(Math.Clamp(limit, 1, 200))
            .Select(c => new { tab = c.Tab, group = c.Group, menu = c.Path.Length == 0 ? null : c.Path, name = c.Name, id = c.Id, visible = c.Visible })
            .ToArray();
        return new
        {
            environment = env,
            theme,
            query,
            count = matches.Length,
            controls = matches,
            closest = matches.Length == 0 ? NameMatch.Closest(found.Select(c => c.Name), query, 8) : null,
            note = "Run a button with se_command action 'run' and its id as 'command' (activate its tab first with se_ui 'tab' if the command expects it).",
        };
    }

    private static void Collect(RibbonBarControls? controls, string tab, string group, string path, int depth,
        List<(string Tab, string Group, string Path, string Name, int Id, bool Visible)> found)
    {
        if (controls is null)
        {
            return;
        }

        for (int i = 0; i < controls.Count; i++)
        {
            RibbonBarControl control = controls.Item(i);
            string name = (control.Name ?? "").Trim();
            if (control.Id == 0 && name.Length == 0)
            {
                continue; // separator
            }

            if (name.Length > 0)
            {
                found.Add((tab, group, path, name, control.Id, control.Visible));
            }

            if (depth < 2)
            {
                RibbonBarControls? children = null;
                try
                {
                    children = control.RibbonBarControls;
                }
                catch (COMException)
                {
                    // A simple button has no child controls.
                }

                Collect(children, tab, group, path.Length == 0 ? name : $"{path} > {name}", depth + 1, found);
            }
        }
    }

    /// <summary>Activates a tab of the ribbon of the active environment, as when it is clicked.</summary>
    public static object ActivateTab(SeContext context, string tab)
    {
        (RibbonBar bar, string env, _) = FindRibbon(context, null);
        RibbonBarTab selected = FindTab(bar.RibbonBarTabs, tab, env);
        if (!selected.Visible)
        {
            throw new ToolException("ui_failed", $"The tab '{selected.Name}' is hidden in this environment.", "Choose a visible tab (se_ui 'ribbon').");
        }

        selected.Activate();
        return new { environment = env, activated = selected.Name, id = selected.Id };
    }

    private static (RibbonBar Bar, string Environment, string Theme) FindRibbon(SeContext context, string? environment)
    {
        Application app = context.Application;
        string env = string.IsNullOrWhiteSpace(environment) ? app.ActiveEnvironment : environment.Trim();
        RibbonBarThemes themes = app.Customization.RibbonBarThemes;
        RibbonBarTheme? theme = null;
        for (int i = 0; i < themes.Count; i++)
        {
            RibbonBarTheme candidate = themes.Item(i);
            if (candidate.Active || theme is null)
            {
                theme = candidate;
            }

            if (candidate.Active)
            {
                break;
            }
        }

        if (theme is null)
        {
            throw new ToolException("ui_failed", "Solid Edge has no ribbon theme.");
        }

        RibbonBars bars = theme.RibbonBars;
        var names = new List<string>();
        for (int i = 0; i < bars.Count; i++)
        {
            RibbonBar bar = bars.Item(i);
            string name = bar.Environment;
            if (string.Equals(name, env, StringComparison.OrdinalIgnoreCase))
            {
                return (bar, name, theme.Name);
            }

            names.Add(name);
        }

        throw ToolException.NotFound($"There is no ribbon for the environment '{env}'.", $"Ribbons exist for: {string.Join(", ", names)}.");
    }

    private static RibbonBarTab FindTab(RibbonBarTabs tabs, string tab, string environment)
    {
        var names = new List<string>();
        for (int i = 0; i < tabs.Count; i++)
        {
            RibbonBarTab candidate = tabs.Item(i);
            if (string.Equals(candidate.Name, tab, StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(tab, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index == i))
            {
                return candidate;
            }

            names.Add(candidate.Name);
        }

        throw ToolException.NotFound($"The {environment} ribbon has no tab '{tab}'.", $"Tabs: {string.Join(", ", names)}.");
    }

    private static IEnumerable<RibbonBarGroup> Groups(RibbonBarTab tab)
    {
        RibbonBarGroups groups = tab.RibbonBarGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            yield return groups.Item(i);
        }
    }

    /// <summary>Buttons of a group; drop-down menus have their own buttons (up to two levels).</summary>
    private static object[] Controls(RibbonBarControls? controls, int depth)
    {
        if (controls is null)
        {
            return [];
        }

        var items = new List<object>();
        for (int i = 0; i < controls.Count; i++)
        {
            RibbonBarControl control = controls.Item(i);
            if (control.Id == 0 && string.IsNullOrEmpty(control.Name))
            {
                continue; // separator
            }

            RibbonBarControls? children = null;
            if (depth < 2)
            {
                try
                {
                    children = control.RibbonBarControls;
                }
                catch (COMException)
                {
                    // A simple button has no child controls.
                }
            }

            object[] nested = Controls(children, depth + 1);
            items.Add(new { name = control.Name, id = control.Id, visible = control.Visible, controls = nested.Length > 0 ? nested : null });
        }

        return items.ToArray();
    }

    private static object ResolveWindow(SeContext context, string window)
    {
        Application app = context.Application;
        if (int.TryParse(window, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            if (index < 1 || index > app.Windows.Count)
            {
                throw ToolException.NotFound($"There is no window {index}; Solid Edge has {app.Windows.Count}.", "List them with se_ui 'windows'.");
            }

            return app.Windows.Item(index);
        }

        object resolved = context.Handles.Resolve(window, "window");
        if (resolved is not SolidEdgeDocument document)
        {
            return resolved;
        }

        foreach (object item in Late.Items(app.Windows))
        {
            if (Late.TryGet(item, "Parent") is SolidEdgeDocument parent && SameDocument(parent, document))
            {
                return item;
            }
        }

        throw ToolException.NotFound($"The document '{SeContext.SafeName(document)}' has no open window.");
    }

    private static object DescribeWindow(SeContext context, object window, int? index, int activeHwnd)
    {
        SolidEdgeDocument? document = Late.TryGet(window, "Parent") as SolidEdgeDocument;
        int hwnd = Convert.ToInt32(Late.TryGet(window, "hWnd") ?? 0, CultureInfo.InvariantCulture);
        object? state = Late.TryGet(window, "WindowState");
        return new
        {
            window = context.Register(window, "window", document),
            index,
            caption = Late.TryGetString(window, "Caption"),
            type = Late.TryGetString(window, "Type"),
            environment = Late.TryGetString(window, "Environment"),
            document = document is null ? null : context.DocumentHandle(document),
            active = hwnd != 0 && hwnd == activeHwnd,
            state = state is null ? null : StateName(Convert.ToInt32(state, CultureInfo.InvariantCulture)),
        };
    }

    private static int ActiveHwnd(Application app)
    {
        try
        {
            return app.ActiveWindow is { } active ? Convert.ToInt32(Late.Get(active, "hWnd"), CultureInfo.InvariantCulture) : 0;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static bool SameDocument(SolidEdgeDocument a, SolidEdgeDocument b)
    {
        try
        {
            return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Name, b.Name, StringComparison.Ordinal);
        }
        catch (COMException)
        {
            return ReferenceEquals(a, b);
        }
    }

    private static string? TryActiveDocument(SeContext context)
    {
        try
        {
            return context.Application.ActiveDocument is SolidEdgeDocument document ? context.DocumentHandle(document) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string StateName(int state) => state >= 0 && state < WindowStates.Count ? WindowStates[state] : state.ToString(CultureInfo.InvariantCulture);
}
