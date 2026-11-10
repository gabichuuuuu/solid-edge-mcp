// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SeEnvironment = SolidEdgeFramework.Environment;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Customization of Solid Edge, as its Customize dialog does: ribbon themes (create, activate, remove, button size and
/// labels), their tabs, groups and commands, and the keyboard shortcuts of each environment. Verified in Solid Edge 2026:
/// edits go between BeginCustomization and EndCustomization and are kept with RibbonBarThemes.Commit (also after a
/// restart; a removed theme does not come back); after EndCustomization the ribbon objects read before are
/// disconnected, so they are read again. The themes are listed by name, so the Solid Edge default theme is the one
/// named in PrefSets\CustomizationOptions\Default Theme; it is never edited: changes go to a theme created from it.
/// </summary>
public static class CustomizationService
{
    public static readonly IReadOnlyList<string> ControlSizes = ["default", "small", "large"];
    public static readonly IReadOnlyList<string> ControlTexts = ["default", "on", "off"];

    public static object RibbonThemes(SeContext context)
    {
        RibbonBarThemes themes = context.Application.Customization.RibbonBarThemes;
        var items = new List<object>();
        for (int i = 0; i < themes.Count; i++)
        {
            items.Add(Describe(context, themes.Item(i), i));
        }

        return new
        {
            count = themes.Count,
            themes = items,
            note = "The Solid Edge default theme is read-only: create a theme from it (create_ribbon_theme), edit that one and activate it.",
        };
    }

    public static object CreateRibbonTheme(SeContext context, string name, string? basedOn)
    {
        string wanted = RequireName(name, "name");
        Customization customization = context.Application.Customization;
        RibbonBarThemes themes = customization.RibbonBarThemes;
        if (TryFind(themes, wanted) is not null)
        {
            throw new ToolException("already_exists", $"There is already a ribbon theme named '{wanted}'.", "Choose another name or edit that theme.");
        }

        RibbonBarTheme source = basedOn is null ? Active(themes).Theme : Find(themes, basedOn).Theme;
        Customize(customization, () =>
        {
            RibbonBarTheme created = themes.Create(source);
            created.Name = wanted;
            themes.Commit();
        });

        (RibbonBarTheme theme, int index) = Find(context.Application.Customization.RibbonBarThemes, wanted);
        return new { created = Describe(context, theme, index), note = "It is not active yet: activate it with activate_ribbon_theme." };
    }

    public static object ActivateRibbonTheme(SeContext context, string theme)
    {
        Customization customization = context.Application.Customization;
        RibbonBarThemes themes = customization.RibbonBarThemes;
        string previous = Active(themes).Theme.Name;
        (RibbonBarTheme target, _) = Find(themes, theme);
        string name = target.Name;
        Customize(customization, () =>
        {
            themes.ActivateTheme(target);
            themes.Commit();
        });

        (RibbonBarTheme active, int index) = Active(context.Application.Customization.RibbonBarThemes);
        if (!string.Equals(active.Name, name, StringComparison.Ordinal))
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept the ribbon theme '{active.Name}' active.");
        }

        return new { active = Describe(context, active, index), previous, restore = $"To undo, activate '{previous}' again." };
    }

    public static object RemoveRibbonTheme(SeContext context, string theme)
    {
        Customization customization = context.Application.Customization;
        RibbonBarThemes themes = customization.RibbonBarThemes;
        (RibbonBarTheme target, int index) = Find(themes, theme);
        Editable(context, target);
        if (target.Active)
        {
            throw new ToolException("theme_active", $"'{target.Name}' is the active ribbon theme.", "Activate another theme first (activate_ribbon_theme).");
        }

        string name = target.Name;
        Customize(customization, () =>
        {
            themes.Remove(target);
            themes.Commit();
        });

        if (TryFind(context.Application.Customization.RibbonBarThemes, name) is not null)
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept the ribbon theme '{name}'.");
        }

        return new { removed = name };
    }

    /// <summary>Renames a theme or changes the size and labels of all its buttons.</summary>
    public static object SetRibbonTheme(SeContext context, string theme, string? newName, string? controlSize, string? controlText)
    {
        if (newName is null && controlSize is null && controlText is null)
        {
            throw ToolException.InvalidArgument("Action 'set_ribbon_theme' requires 'newName', 'controlSize' and/or 'controlText'.");
        }

        Customization customization = context.Application.Customization;
        RibbonBarThemes themes = customization.RibbonBarThemes;
        (RibbonBarTheme target, int index) = Find(themes, theme);
        Editable(context, target);
        object before = Describe(context, target, index);
        RibbonBarControlSize? size = controlSize is null ? null : (RibbonBarControlSize)Choice(controlSize, ControlSizes, "controlSize");
        RibbonBarControlText? text = controlText is null ? null : (RibbonBarControlText)Choice(controlText, ControlTexts, "controlText");
        string current = target.Name;
        string name = newName is null ? current : RequireName(newName, "newName");
        if (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && TryFind(themes, name) is not null)
        {
            throw new ToolException("already_exists", $"There is already a ribbon theme named '{name}'.", "Choose another name.");
        }
        Customize(customization, () =>
        {
            RibbonBarTheme live = Find(themes, current).Theme;
            if (size is { } s)
            {
                live.GlobalControlSize = s;
            }

            if (text is { } t)
            {
                live.GlobalControlText = t;
            }

            if (newName is not null)
            {
                live.Name = name;
            }

            themes.Commit();
        });

        (RibbonBarTheme after, int afterIndex) = Find(context.Application.Customization.RibbonBarThemes, name);
        return new { previous = before, theme = Describe(context, after, afterIndex) };
    }

    /// <summary>Adds a tab (position 0-based; default at the end) to the ribbon of an environment.</summary>
    public static object AddTab(SeContext context, string? theme, string? environment, string name, int? position)
    {
        string wanted = RequireName(name, "name");
        return EditRibbon(context, theme, environment, bar =>
        {
            RibbonBarTabs tabs = bar.RibbonBarTabs;
            RibbonBarTab tab = tabs.Insert(wanted, At(position, tabs.Count), RibbonBarInsertMode.seRibbonBarInsertCreate);
            return new { added = "tab", name = tab.Name };
        });
    }

    public static object AddGroup(SeContext context, string? theme, string? environment, string tab, string name, int? position)
    {
        string wanted = RequireName(name, "name");
        return EditRibbon(context, theme, environment, bar =>
        {
            RibbonBarGroups groups = FindTab(bar, tab).RibbonBarGroups;
            RibbonBarGroup group = groups.Insert(wanted, At(position, groups.Count), RibbonBarInsertMode.seRibbonBarInsertCreate);
            return new { added = "group", tab, name = group.Name };
        });
    }

    /// <summary>Adds a button that runs a command (name or id, as se_command find or se_ui ribbon show them) to a group.</summary>
    public static object AddCommand(SeContext context, string? theme, string? environment, string tab, string group, string command, int? position)
    {
        return EditRibbon(context, theme, environment, bar =>
        {
            (string name, int id) = CommandService.ResolveId(command, bar.Environment);
            RibbonBarControls controls = FindGroup(FindTab(bar, tab), group).RibbonBarControls;
            RibbonBarControl control = controls.Insert(id, At(position, controls.Count), RibbonBarInsertMode.seRibbonBarInsertCreateButton);
            if (control.Id != id)
            {
                throw new ToolException("option_not_applied", $"Solid Edge did not add the command {id} to the group '{group}'.");
            }

            return new { added = "command", tab, group, command = name, id, caption = control.Name };
        });
    }

    /// <summary>Shows, hides or renames a tab, a group or a button (control = its caption or command id).</summary>
    public static object SetItem(SeContext context, string? theme, string? environment, string tab, string? group, string? control, bool? visible, string? newName)
    {
        if (visible is null && newName is null)
        {
            throw ToolException.InvalidArgument("Action 'set_item' requires 'visible' and/or 'newName'.");
        }

        string? renamed = newName is null ? null : RequireName(newName, "newName");
        return EditRibbon(context, theme, environment, bar =>
        {
            RibbonBarTab t = FindTab(bar, tab);
            object target = group is null ? t : control is null ? FindGroup(t, group) : FindControl(FindGroup(t, group), control);
            object before = new { name = Late.TryGetString(target, "Name"), visible = Late.TryGet(target, "Visible") };
            if (visible is { } v)
            {
                Late.Set(target, "Visible", v);
            }

            if (renamed is not null)
            {
                Late.Set(target, "Name", renamed);
            }

            return new
            {
                item = group is null ? "tab" : control is null ? "group" : "control",
                previous = before,
                name = Late.TryGetString(target, "Name"),
                visible = Late.TryGet(target, "Visible"),
            };
        });
    }

    public static object RemoveItem(SeContext context, string? theme, string? environment, string tab, string? group, string? control)
    {
        return EditRibbon(context, theme, environment, bar =>
        {
            RibbonBarTab t = FindTab(bar, tab);
            if (group is null)
            {
                string name = t.Name;
                bar.RibbonBarTabs.Remove(t);
                return new { removed = "tab", name };
            }

            RibbonBarGroup g = FindGroup(t, group);
            if (control is null)
            {
                string name = g.Name;
                t.RibbonBarGroups.Remove(g);
                return new { removed = "group", name };
            }

            RibbonBarControl c = FindControl(g, control);
            object removed = new { name = c.Name, id = c.Id };
            g.RibbonBarControls.Remove(c);
            return new { removed = "control", control = removed };
        });
    }

    /// <summary>Keyboard shortcuts of an environment (by default the active one).</summary>
    public static object Shortcuts(SeContext context, string? environment)
    {
        (SeEnvironment env, Accelerator table) = Keys(context, environment);
        var items = new List<object>();
        for (int i = 1, count = table.Count; i <= count; i++)
        {
            KeyBinding binding = table.Item(i);
            items.Add(DescribeBinding(binding));
        }

        return new { environment = env.Name, count = items.Count, shortcuts = items };
    }

    /// <summary>Assigns a key combination such as 'Ctrl+Shift+K' to a command in an environment.</summary>
    public static object AddShortcut(SeContext context, string? environment, string keys, string command, bool replace)
    {
        (SeEnvironment env, Accelerator table) = Keys(context, environment);
        (int modifiers, int key) = ParseKeys(keys);
        int code = table.BuildKeyCode(modifiers, key);
        (string name, int id) = CommandService.ResolveId(command, env.Name);
        KeyBinding? existing = Binding(table, code);
        object? replaced = existing is null ? null : DescribeBinding(existing);
        if (existing is not null)
        {
            if (!replace)
            {
                throw new ToolException("already_exists", $"{KeyText(code)} already runs the command {existing.CommandID} ('{existing.CommandString?.Trim()}') in {env.Name}.",
                    "Pass replace = true to assign it anyway, or choose other keys.");
            }

            table.Remove(code);
        }

        table.Add(id, code);
        KeyBinding added = Binding(table, code)
            ?? throw new ToolException("option_not_applied", $"Solid Edge did not assign {KeyText(code)} in {env.Name}.");
        if (added.CommandID != id)
        {
            throw new ToolException("option_not_applied", $"{KeyText(code)} runs the command {added.CommandID}, not {id}.");
        }

        return new { environment = env.Name, shortcut = DescribeBinding(added), command = name, replaced };
    }

    public static object RemoveShortcut(SeContext context, string? environment, string keys)
    {
        (SeEnvironment env, Accelerator table) = Keys(context, environment);
        (int modifiers, int key) = ParseKeys(keys);
        int code = table.BuildKeyCode(modifiers, key);
        KeyBinding existing = Binding(table, code)
            ?? throw ToolException.NotFound($"{KeyText(code)} has no command in {env.Name}.", "List them with se_customize action 'shortcuts'.");
        object removed = DescribeBinding(existing);
        table.Remove(code);
        if (Binding(table, code) is not null)
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept {KeyText(code)} in {env.Name}.");
        }

        return new { environment = env.Name, removed, restore = "To undo, add the same keys and command again with add_shortcut." };
    }

    /// <summary>Puts back the Solid Edge keyboard shortcuts of an environment, removing the person's own.</summary>
    public static object ResetShortcuts(SeContext context, string? environment)
    {
        (SeEnvironment env, Accelerator table) = Keys(context, environment);
        int before = table.Count;
        table.Reset();
        return new { environment = env.Name, before, after = table.Count };
    }

    private static object EditRibbon(SeContext context, string? theme, string? environment, Func<RibbonBar, object> edit)
    {
        Customization customization = context.Application.Customization;
        RibbonBarThemes themes = customization.RibbonBarThemes;
        (RibbonBarTheme target, _) = theme is null ? Active(themes) : Find(themes, theme);
        Editable(context, target);
        string name = target.Name;
        bool active = target.Active;
        string env = string.IsNullOrWhiteSpace(environment) ? context.Application.ActiveEnvironment : environment.Trim();
        string barEnvironment = FindBar(target, env).Environment;
        object? result = null;
        Customize(customization, () =>
        {
            // Verified: the ribbon bars read before BeginCustomization are disconnected inside it, so they are read again.
            RibbonBarThemes live = customization.RibbonBarThemes;
            result = edit(FindBar(Find(live, name).Theme, barEnvironment));
            live.Commit();
        });

        return new { theme = name, environment = barEnvironment, result, note = active ? null : "The theme is not active: activate it to see the change." };
    }

    /// <summary>Runs an edit between BeginCustomization and EndCustomization (always closed), mapping COM failures.</summary>
    private static void Customize(Customization customization, Action edit)
    {
        customization.BeginCustomization();
        try
        {
            edit();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            throw new ToolException("customization_failed", $"Solid Edge rejected the change: {ComErrors.Unwrap(ex).Message}",
                "Check the names with se_ui ribbon or se_customize ribbon_themes.", ex);
        }
        finally
        {
            customization.EndCustomization();
        }
    }

    private static void Editable(SeContext context, RibbonBarTheme theme)
    {
        if (IsDefault(context, theme.Name))
        {
            throw new ToolException("read_only", $"'{theme.Name}' is the Solid Edge default ribbon theme and is not edited.",
                "Create a theme from it with se_customize create_ribbon_theme, edit that one and activate it.");
        }
    }

    private static object Describe(SeContext context, RibbonBarTheme theme, int index)
    {
        string name = theme.Name;
        bool isDefault = IsDefault(context, name);
        return new
        {
            index,
            name,
            active = theme.Active,
            controlSize = Option(ControlSizes, (int)theme.GlobalControlSize),
            controlText = Option(ControlTexts, (int)theme.GlobalControlText),
            isDefault,
            editable = !isDefault,
        };
    }

    /// <summary>Whether a theme is the Solid Edge default one (named in the settings of the running version).</summary>
    private static bool IsDefault(SeContext context, string name)
    {
        string? configured = SolidEdgeSettings.Read(context.Application, @"PrefSets\CustomizationOptions", "Default Theme");
        return configured is not null
            ? string.Equals(name, configured, StringComparison.OrdinalIgnoreCase)
            : name.Contains("Solid Edge Default", StringComparison.OrdinalIgnoreCase);
    }

    private static (RibbonBarTheme Theme, int Index) Active(RibbonBarThemes themes)
    {
        for (int i = 0; i < themes.Count; i++)
        {
            if (themes.Item(i).Active)
            {
                return (themes.Item(i), i);
            }
        }

        return themes.Count > 0 ? (themes.Item(0), 0) : throw new ToolException("ui_failed", "Solid Edge has no ribbon theme.");
    }

    private static (RibbonBarTheme Theme, int Index)? TryFind(RibbonBarThemes themes, string theme)
    {
        for (int i = 0; i < themes.Count; i++)
        {
            RibbonBarTheme candidate = themes.Item(i);
            if (string.Equals(candidate.Name, theme.Trim(), StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(theme, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index == i))
            {
                return (candidate, i);
            }
        }

        return null;
    }

    private static (RibbonBarTheme Theme, int Index) Find(RibbonBarThemes themes, string theme) =>
        TryFind(themes, theme) ?? throw ToolException.NotFound($"There is no ribbon theme '{theme}'.",
            $"Themes: {string.Join(", ", Enumerable.Range(0, themes.Count).Select(i => $"{i} {themes.Item(i).Name}"))}.");

    private static RibbonBar FindBar(RibbonBarTheme theme, string environment)
    {
        RibbonBars bars = theme.RibbonBars;
        var names = new List<string>();
        for (int i = 0; i < bars.Count; i++)
        {
            RibbonBar bar = bars.Item(i);
            if (string.Equals(bar.Environment, environment, StringComparison.OrdinalIgnoreCase))
            {
                return bar;
            }

            names.Add(bar.Environment);
        }

        throw ToolException.NotFound($"There is no ribbon for the environment '{environment}'.", $"Ribbons exist for: {string.Join(", ", names)}.");
    }

    private static RibbonBarTab FindTab(RibbonBar bar, string tab)
    {
        RibbonBarTabs tabs = bar.RibbonBarTabs;
        for (int i = 0; i < tabs.Count; i++)
        {
            RibbonBarTab candidate = tabs.Item(i);
            if (string.Equals(candidate.Name, tab.Trim(), StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(tab, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index == i))
            {
                return candidate;
            }
        }

        throw ToolException.NotFound($"The {bar.Environment} ribbon has no tab '{tab}'.",
            $"Tabs: {string.Join(", ", Enumerable.Range(0, tabs.Count).Select(i => tabs.Item(i).Name))}.");
    }

    private static RibbonBarGroup FindGroup(RibbonBarTab tab, string group)
    {
        RibbonBarGroups groups = tab.RibbonBarGroups;
        for (int i = 0; i < groups.Count; i++)
        {
            RibbonBarGroup candidate = groups.Item(i);
            if (string.Equals(candidate.Name, group.Trim(), StringComparison.OrdinalIgnoreCase)
                || (int.TryParse(group, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index == i))
            {
                return candidate;
            }
        }

        throw ToolException.NotFound($"The tab '{tab.Name}' has no group '{group}'.",
            $"Groups: {string.Join(", ", Enumerable.Range(0, groups.Count).Select(i => groups.Item(i).Name))}.");
    }

    /// <summary>A button of a group by caption or command id.</summary>
    private static RibbonBarControl FindControl(RibbonBarGroup group, string control)
    {
        RibbonBarControls controls = group.RibbonBarControls;
        bool numeric = int.TryParse(control, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
        for (int i = 0; i < controls.Count; i++)
        {
            RibbonBarControl candidate = controls.Item(i);
            if ((numeric && candidate.Id == id) || string.Equals((candidate.Name ?? "").Trim(), control.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw ToolException.NotFound($"The group '{group.Name}' has no button '{control}'.",
            $"Buttons: {string.Join(", ", Enumerable.Range(0, controls.Count).Select(i => $"{controls.Item(i).Name} ({controls.Item(i).Id})"))}.");
    }

    private static object At(int? position, int count) => Math.Clamp(position ?? count, 0, count);

    private static string RequireName(string? name, string argument) =>
        string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument($"'{argument}' must not be empty.") : name.Trim();

    private static int Choice(string value, IReadOnlyList<string> choices, string argument)
    {
        int index = choices.ToList().FindIndex(c => string.Equals(c, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw ToolException.InvalidArgument($"Unknown {argument} '{value}'.", $"Use one of: {string.Join(", ", choices)}.");
    }

    private static string Option(IReadOnlyList<string> choices, int value) =>
        value >= 0 && value < choices.Count ? choices[value] : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Environment by name (default the active one) and its main shortcut table (the one of the Solid Edge window).</summary>
    private static (SeEnvironment Environment, Accelerator Table) Keys(SeContext context, string? environment)
    {
        Application app = context.Application;
        string wanted = string.IsNullOrWhiteSpace(environment) ? app.ActiveEnvironment : environment.Trim();
        SeEnvironment? env = null;
        var names = new List<string>();
        foreach (object item in Late.Items(app.Environments))
        {
            var candidate = (SeEnvironment)item;
            if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                env = candidate;
                break;
            }

            names.Add(candidate.Name);
        }

        if (env is null)
        {
            throw ToolException.NotFound($"There is no environment '{wanted}'.", $"Environments: {string.Join(", ", names)}.");
        }

        Accelerators tables = env.Accelerators;
        Accelerator? main = null;
        for (int i = 1, count = tables.Count; i <= count; i++)
        {
            Accelerator table = tables.Item(i);
            if (table.Type == AcceleratorTypeConstants.seExecutable)
            {
                main = table;
                break;
            }

            main ??= table;
        }

        return main is null
            ? throw new ToolException("not_supported", $"The {env.Name} environment has no keyboard shortcuts.")
            : (env, main);
    }

    private static KeyBinding? Binding(Accelerator table, int code)
    {
        try
        {
            return table.KeyBinding(code);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    private static object DescribeBinding(KeyBinding binding) => new
    {
        keys = KeyText(binding.KeyCode),
        shown = binding.KeyString,
        command = binding.CommandID,
        name = binding.CommandString?.Trim(),
    };

    // Key codes of Solid Edge 2026: (modifiers << 8) | virtual key, modifiers Shift 1, Ctrl 2, Alt 4 (BuildKeyCode).
    private static readonly (string Name, int Bit)[] Modifiers = [("Ctrl", 2), ("Shift", 1), ("Alt", 4)];

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26,
        ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E,
        [";"] = 0xBA, ["="] = 0xBB, ["Plus"] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["Minus"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF,
        ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
    };

    /// <summary>"Ctrl+Shift+K" → modifier bits and virtual key.</summary>
    internal static (int Modifiers, int Key) ParseKeys(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            throw ToolException.InvalidArgument("'keys' is required, e.g. 'Ctrl+Shift+K' or 'Alt+F5'.");
        }

        string text = keys.Trim();
        int modifiers = 0;
        string rest = text;
        bool more = true;
        while (more)
        {
            more = false;
            foreach ((string name, int bit) in Modifiers.Concat([("Control", 2), ("Ctl", 2)]))
            {
                if (rest.Length > name.Length + 1 && rest.StartsWith(name + "+", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= bit;
                    rest = rest[(name.Length + 1)..];
                    more = true;
                }
            }
        }

        int key;
        if (rest.Length == 1 && char.IsAsciiLetterOrDigit(rest[0]))
        {
            key = char.ToUpperInvariant(rest[0]);
        }
        else if (rest.Length is 2 or 3 && (rest[0] is 'F' or 'f') && int.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            key = 0x6F + f;
        }
        else if (NamedKeys.TryGetValue(rest, out int named))
        {
            key = named;
        }
        else
        {
            throw ToolException.InvalidArgument($"'{keys}' is not a key combination.",
                "Use modifiers Ctrl, Shift, Alt joined with '+' and a letter, digit, F1-F24 or a key name (Delete, Home, PageUp, Left...).");
        }

        return (modifiers, key);
    }

    /// <summary>Key code as "Ctrl+Shift+K" (the text Solid Edge shows is in the language of Windows).</summary>
    internal static string KeyText(int code)
    {
        int modifiers = code >> 8;
        int key = code & 0xFF;
        string name = key switch
        {
            >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (key - 0x6F).ToString(CultureInfo.InvariantCulture),
            _ => NamedKeys.FirstOrDefault(k => k.Value == key && k.Key.Length > 1).Key
                ?? NamedKeys.FirstOrDefault(k => k.Value == key).Key
                ?? $"0x{key:X2}",
        };
        return string.Concat(Modifiers.Where(m => (modifiers & m.Bit) != 0).Select(m => m.Name + "+")) + name;
    }
}
