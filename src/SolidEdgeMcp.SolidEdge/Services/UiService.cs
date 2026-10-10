using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Interfaz de Solid Edge: ventana principal, ventanas de documento, entornos y cinta (pestañas, grupos y botones con
/// su id de orden). Las colecciones de la cinta empiezan en 0, a diferencia del resto del API.
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

    /// <summary>Activa una ventana (por índice, handle de ventana o documento), como al pulsar su pestaña.</summary>
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

    /// <summary>Estado de la ventana principal (sin <paramref name="window"/>) o de una ventana de documento.</summary>
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
    /// Cinta de un entorno (por defecto el activo): sus pestañas con los grupos o, con <paramref name="tab"/>, los grupos y
    /// botones de esa pestaña con su id de orden (se_command run lo ejecuta).
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

    /// <summary>Activa una pestaña de la cinta del entorno activo, como al pulsarla.</summary>
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

    /// <summary>Botones de un grupo; los desplegables traen sus propios botones (hasta dos niveles).</summary>
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
                continue; // separador
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
                    // Un botón simple no tiene controles hijos.
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
