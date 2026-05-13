using FluentAssertions;
using NSubstitute;
using OpenMono.Rendering;
using Spectre.Console;

namespace OpenMono.Tests.Rendering;

public class TerminalRendererMarkdownTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly IAnsiConsole _console = Substitute.For<IAnsiConsole>();

    public TerminalRendererMarkdownTests()
    {
        Console.SetOut(_output);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _output.Dispose();
    }

    private TerminalRenderer CreateRenderer() => new(_console);

    [Fact]
    public void WriteMarkdown_RendersBoldWithoutRawAsterisks()
    {
        var renderer = CreateRenderer();

        renderer.WriteMarkdown("This is **bold** text.");

        _output.ToString().Should().NotContain("**");
    }

    [Fact]
    public void WriteMarkdown_RendersCodeBlockWithoutRawBackticks()
    {
        var renderer = CreateRenderer();

        renderer.WriteMarkdown("```csharp\nvar x = 1;\n```");

        _output.ToString().Should().NotContain("```");
    }

    [Fact]
    public void WriteMarkdown_RendersHeadingWithoutHashSymbol()
    {
        var renderer = CreateRenderer();

        renderer.WriteMarkdown("# Hello");

        var output = _output.ToString();
        output.Should().NotContain("# Hello");
        output.Should().Contain("Hello");
    }

    [Fact]
    public void EndAssistantResponse_RerendersStreamedMarkdownWithoutRawSymbols()
    {
        var renderer = CreateRenderer();

        renderer.StartAssistantResponse();
        renderer.StreamText("This is **bold** text.\n");
        renderer.StreamText("```csharp\nvar x = 1;\n```\n");
        renderer.EndAssistantResponse(tokens: 3);

        var output = _output.ToString();
        output.Should().NotContain("**");
        output.Should().NotContain("```");
        output.Should().Contain("bold");
        output.Should().Contain("var x = 1;");
    }
}
