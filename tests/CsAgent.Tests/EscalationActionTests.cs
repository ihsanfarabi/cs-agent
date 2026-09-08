using System.Text.Json;
using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// escalation_action on the ONE result object: null-omitted (default-off output
/// byte-identical across CLI --json / HTTP / eval), present when the
/// escalation agent acted. Same shared serializer as every surface.
/// </summary>
public sealed class EscalationActionTests
{
    private static AskResult Result(EscalationActionRecord? action = null) => new(
        "What is your SLA uptime?", false, null,
        [new MissingItem("SLA uptime is 99.9%", "no supporting chunk states this claim explicitly")],
        [], [], Calls: 2, Seconds: 1.0, CostEstimate: "— (non-default model)", EscalationAction: action);

    [Fact]
    public void NullOmitted_DefaultOffIsByteIdentical()
    {
        var json = JsonSerializer.Serialize(Result(), CsAgentJson.SerializerOptions);
        Assert.DoesNotContain("escalation_action", json);
        // 10-arg construction is unchanged (positional default) — deserializing
        // the default-off JSON round-trips without the field
        var roundtrip = JsonSerializer.Serialize(JsonSerializer.Deserialize<AskResult>(json, CsAgentJson.SerializerOptions), CsAgentJson.SerializerOptions);
        Assert.Equal(json, roundtrip);
    }

    [Fact]
    public void Present_SerializedUnderPinnedName()
    {
        var json = JsonSerializer.Serialize(Result(
            new EscalationActionRecord("file_ticket", "sent", "SLA uptime question", "HTTP 200")),
            CsAgentJson.SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var action = doc.RootElement.GetProperty("escalation_action");
        Assert.Equal("file_ticket", action.GetProperty("tool").GetString());
        Assert.Equal("sent", action.GetProperty("status").GetString());
        Assert.Equal("SLA uptime question", action.GetProperty("title").GetString());
        Assert.Equal("HTTP 200", action.GetProperty("detail").GetString());
    }
}