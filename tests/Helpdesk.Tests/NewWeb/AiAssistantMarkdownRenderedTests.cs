extern alias NewWeb;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Markdown = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantMarkdown;

namespace Helpdesk.Tests.NewWeb;

public sealed class AiAssistantMarkdownRenderedTests
{
    [Theory]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    public async Task Tables_preserve_alignment_empty_ragged_cells_and_copy_only_the_selected_table(bool outerPipes, string newline)
    {
        await using var context = Context();
        var lines = new[] { "Host | Free | Note", ":--- | ---: | :---:", "node-01 | **17.8 GiB** | `a\\|b`", "node-02 | | ok", "node-03 |", "node-04 | 2 GiB | extra | ignored" };
        var table = string.Join(newline, lines.Select(line => outerPipes ? $"| {line} |" : line));
        const string other = "| Value |\n| --- |\n| <img src=x onerror=alert(1)> |";
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, $"Disk report\n{table}\n\nAdjacent prose.\n\n{other}"));

        Assert.Equal(2, rendered.FindAll("table").Count);
        var first = rendered.FindAll(".chat-table-block")[0];
        Assert.Equal(["Host", "Free", "Note"], first.QuerySelectorAll("th[scope=col]").Select(cell => cell.TextContent));
        Assert.Equal(["text-align: left", "text-align: right", "text-align: center"], first.QuerySelectorAll("th").Select(cell => cell.GetAttribute("style")));
        Assert.Equal("17.8 GiB", first.QuerySelector("td strong")!.TextContent);
        Assert.Equal("a|b", first.QuerySelector("td code")!.TextContent);
        var rows = first.QuerySelectorAll("tbody tr");
        Assert.Equal("", rows[1].QuerySelectorAll("td")[1].TextContent);
        Assert.Equal(["node-03", "", ""], rows[2].QuerySelectorAll("td").Select(cell => cell.TextContent));
        Assert.Equal(3, rows[3].QuerySelectorAll("td").Length);
        Assert.Empty(rendered.FindAll("img, script"));
        Assert.Equal("<img src=x onerror=alert(1)>", rendered.FindAll("table")[1].QuerySelector("td")!.TextContent);
        Assert.Equal("0", first.QuerySelector("[role=region]")!.GetAttribute("tabindex"));

        await first.QuerySelector("button")!.ClickAsync(new());
        Assert.Equal(table.ReplaceLineEndings("\n"), context.JSInterop.Invocations["navigator.clipboard.writeText"][0].Arguments[0]);
        // FindAll returns an element snapshot; read the current DOM after the click rerenders feedback.
        Assert.Equal("Table copied as Markdown.", rendered.Find(".chat-table-block [role=status]").TextContent);
        await rendered.FindAll(".chat-table-block button")[1].ClickAsync(new());
        Assert.Equal(other, context.JSInterop.Invocations["navigator.clipboard.writeText"][1].Arguments[0]);
        Assert.Empty(rendered.FindAll(".chat-table-toolbar [role=status]")[0].TextContent);
        Assert.Equal("Table copied as Markdown.", rendered.FindAll(".chat-table-toolbar [role=status]")[1].TextContent);
    }

    [Theory]
    [InlineData("| Host | Free |\n| --- |\n| node-01 | 17 GiB |")]
    [InlineData("| Host | Free |\n| words | --- |\n| node-01 | 17 GiB |")]
    [InlineData("| Host | Free |\n| --- | :")]
    [InlineData("| Host | Free |\n| -- | --- |")]
    public async Task Malformed_or_incomplete_tables_remain_readable(string text)
    {
        await using var context = Context();
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, text));
        Assert.Empty(rendered.FindAll("table"));
        Assert.Equal(text, rendered.Find("p").TextContent);
    }

    [Fact]
    public async Task Incremental_table_parsing_keeps_one_final_table_and_fenced_examples_remain_code()
    {
        await using var context = Context();
        const string header = "| Host | Free |\n| :--- | ---: |";
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, "| Host | Free |"));
        Assert.Empty(rendered.FindAll("table"));
        rendered.Render(p => p.Add(x => x.Text, header + "\n| node-01 |"));
        Assert.Equal(["node-01", ""], rendered.FindAll("tbody td").Select(cell => cell.TextContent));
        var final = header + "\n| node-01 | **17 GiB** |\n\n```markdown\n" + header + "\n```\n\n### Notes\n- Kept";
        rendered.Render(p => p.Add(x => x.Text, final));
        rendered.Render(p => p.Add(x => x.Text, final));
        Assert.Single(rendered.FindAll("table"));
        Assert.Equal("17 GiB", rendered.Find("td strong").TextContent);
        Assert.Equal(header + "\n", rendered.Find("pre code").TextContent);
        Assert.Equal("Notes", rendered.Find("h4").TextContent);
        Assert.Contains("• Kept", rendered.Markup);
        await rendered.Find(".chat-code button").ClickAsync(new());
        Assert.Equal(header + "\n", context.JSInterop.Invocations["navigator.clipboard.writeText"][0].Arguments[0]);
    }

    [Fact]
    public async Task Empty_cells_escaped_outer_pipes_and_adjacent_tables_keep_their_boundaries()
    {
        await using var context = Context();
        const string first = "| Left | Right |\n| --- | --- |\n| | |\n| a\\|b | tail\\| |";
        const string second = "One | Two\n--- | ---\n1 | 2";
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, first + "\n" + second));
        Assert.Equal(2, rendered.FindAll("table").Count);
        Assert.Equal(["", "", "a|b", "tail|"], rendered.FindAll("table")[0].QuerySelectorAll("td").Select(x => x.TextContent));
        await rendered.FindAll(".chat-table-block button")[0].ClickAsync(new());
        Assert.Equal(first, context.JSInterop.Invocations["navigator.clipboard.writeText"][0].Arguments[0]);
    }

    [Theory]
    [InlineData("~~~", "~~~")]
    [InlineData("````", "````")]
    [InlineData("```", "")]
    public async Task Fenced_or_unfinished_code_cannot_turn_an_example_into_a_table(string opening, string closing)
    {
        await using var context = Context();
        const string table = "| Host |\n| --- |\n| <img src=x onerror=alert(1)> |";
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, $"{opening}markdown\n{table}\n{closing}"));
        Assert.Empty(rendered.FindAll("table, img, script"));
        Assert.Equal(table + "\n", rendered.Find("pre code").TextContent);
    }

    [Fact]
    public async Task Clipboard_denial_is_announced_beside_the_action_and_exposes_exact_manual_markdown()
    {
        await using var context = Context();
        context.JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new JSException("Permission denied"));
        const string table = "| Host |\n| --- |\n| **<script>alert(1)</script>** |";
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, table));
        await rendered.Find(".chat-table-toolbar button").ClickAsync(new());
        Assert.Equal("Copy failed. Select the Markdown below and copy it manually.", rendered.Find("[role=status]").TextContent);
        Assert.Equal(table, rendered.Find("textarea[readonly]").TextContent);
        Assert.Single(rendered.FindAll("table"));
        Assert.Empty(rendered.FindAll("script, img"));
        Assert.Equal("<script>alert(1)</script>", rendered.Find("td strong").TextContent);
        rendered.Render(p => p.Add(x => x.Text, "A new response."));
        Assert.Empty(rendered.FindAll("textarea, [role=status]"));
    }

    [Fact]
    public async Task Delayed_clipboard_completion_cannot_attach_feedback_to_a_new_response()
    {
        await using var context = Context();
        var clipboard = context.JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, "| Host |\n| --- |\n| one |"));
        var copy = rendered.Find(".chat-table-toolbar button").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations["navigator.clipboard.writeText"]));
        rendered.Render(p => p.Add(x => x.Text, "| Host |\n| --- |\n| two |"));
        clipboard.SetVoidResult();
        await copy;
        Assert.Empty(rendered.Find("[role=status]").TextContent);
        Assert.Equal("two", rendered.Find("td").TextContent);
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        return context;
    }
}
