// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.Server.Tests;

/// <summary>The COM dispatcher with a fake dialog finder: no Solid Edge needed.</summary>
public sealed class StaComDispatcherTests
{
    private static readonly DialogInfo Tips = new("Command Tips", "The Command Tips are currently providing information.", ["OK", "Cancel"], false);
    private static readonly DialogInfo Progress = new("Exporting", "Writing the file", ["Cancel"], true);

    private static StaComDispatcher Dispatcher(params DialogInfo[] dialogs) =>
        new(NullServerLog.Instance, TimeSpan.FromSeconds(1), () => dialogs, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task A_dialog_that_waits_for_an_answer_fails_the_call_early_and_names_it()
    {
        using var release = new ManualResetEventSlim();
        using StaComDispatcher dispatcher = Dispatcher(Tips);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            ToolException error = await Assert.ThrowsAsync<ToolException>(() =>
                dispatcher.InvokeAsync("se_drawing.sheets", () => release.Wait(TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(30), CancellationToken.None));

            Assert.Equal("dialog_open", error.Code);
            Assert.Contains("Command Tips", error.Message, StringComparison.Ordinal);
            Assert.Contains("[OK, Cancel]", error.Message, StringComparison.Ordinal);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");

            // The abandoned call still holds Solid Edge: the next one is refused at once and names the dialog too.
            ToolException busy = await Assert.ThrowsAsync<ToolException>(() =>
                dispatcher.InvokeAsync("se_documents.list", () => 1, TimeSpan.FromSeconds(30), CancellationToken.None));
            Assert.Equal("solid_edge_busy", busy.Code);
            Assert.Contains("Command Tips", busy.Message, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_progress_window_does_not_fail_the_call()
    {
        using StaComDispatcher dispatcher = Dispatcher(Progress);
        int result = await dispatcher.InvokeAsync("se_export.step", () =>
        {
            Thread.Sleep(600);
            return 42;
        }, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task A_timeout_names_the_open_dialog()
    {
        using var release = new ManualResetEventSlim();
        using StaComDispatcher dispatcher = Dispatcher(Progress);
        try
        {
            ToolException error = await Assert.ThrowsAsync<ToolException>(() =>
                dispatcher.InvokeAsync("se_export.step", () => release.Wait(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(300), CancellationToken.None));

            Assert.Equal("timeout", error.Code);
            Assert.Contains("'Exporting'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void A_dialog_with_a_choice_names_its_options_and_the_selected_one()
    {
        var links = new DialogInfo("Update Model Links", "Some part copies are out of date.", ["OK", "Help"], false)
        {
            Options = ["Update now", "Don't update now"],
            Selected = ["Update now"],
        };

        Assert.True(links.WaitsForAnswer);
        Assert.Equal("'Update Model Links': \"Some part copies are out of date.\" [OK, Help] options [Update now (selected), Don't update now]", links.ToString());
    }

    [Fact]
    public async Task Without_dialogs_a_call_returns_its_result()
    {
        using StaComDispatcher dispatcher = Dispatcher();
        Assert.Equal("ok", await dispatcher.InvokeAsync("status", () => "ok", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Empty(dispatcher.OpenDialogs());
    }
}
