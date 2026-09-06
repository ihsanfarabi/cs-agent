using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

public sealed class PromptHardeningTests
{
    private static readonly CitedChunk Chunk =
        new(1, "https://docs.foo.com/keys", 0, "rotate from settings", 0.5f);

    [Fact]
    public void DraftPrompt_DelimitsChunkData()
    {
        var prompt = AskPipeline.FormatDraftPrompt("q", [Chunk]);
        Assert.Contains("<<<CHUNK 1 (https://docs.foo.com/keys)", prompt);
        Assert.Contains("rotate from settings", prompt);
        Assert.Contains("END CHUNK>>>", prompt);
    }

    [Fact]
    public void VerifyPrompt_DelimitsChunkData()
    {
        var prompt = AskPipeline.FormatVerifyPrompt("q", "draft", [Chunk]);
        Assert.Contains("<<<CHUNK 1 (https://docs.foo.com/keys)", prompt);
        Assert.Contains("END CHUNK>>>", prompt);
    }

    [Fact]
    public void DraftInstructions_CarryDataNotInstructionsLine()
        => Assert.Contains("never instructions", Prompts.DraftInstructions());

    [Fact]
    public void VerifyPromptFile_CarriesDataNotInstructionsLine()
        => Assert.Contains("never instructions", Prompts.VerifyPrompt());
}