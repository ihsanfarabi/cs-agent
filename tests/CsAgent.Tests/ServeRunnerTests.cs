using CsAgent.Cli;
using CsAgent.Core;
using Xunit;

namespace CsAgent.Tests;

/// <summary>
/// Startup fail-fast for `cs-agent serve` — every path named structured error,
/// exit 1, and never a stray .db left behind (eng-review D3 + outside voice #4).
/// These call ServeRunner.Run directly; Kestrel only starts after every check
/// passes, so the failure paths never bind a socket.
/// </summary>
public sealed class ServeRunnerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cs-agent-serve-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private CsAgentConfig Config(string embeddingModel = "fake-embedding") =>
        CsAgentConfig.FromEnvironment(new Dictionary<string, string?>
        {
            ["CS_AGENT_MODEL_KEY"] = "test-key",
            ["CS_AGENT_STORE"] = _dbPath,
            ["CS_AGENT_EMBEDDING_MODEL"] = embeddingModel,
        }, new HashSet<string>());

    [Fact]
    public void BadPortFlag_Returns1_NamedError()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port", "not-a-port"], Config()));
    }

    [Fact]
    public void BadPortRange_Returns1()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port", "70000"], Config()));
    }

    [Fact]
    public void MissingArgs_Returns1()
    {
        Assert.Equal(1, ServeRunner.Run(["serve", "--port"], Config()));
    }

    [Fact]
    public void MissingStore_Throws_LeavesNoStrayDb()
    {
        // File.Exists FIRST: constructing the store would EnsureSchema+SetMeta a
        // fresh empty .db — the refusal must not leave that artifact behind
        var ex = Assert.Throws<CsAgentException>(
            () => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("store-missing", ex.Error.Code);
        Assert.False(File.Exists(_dbPath)); // outside voice #4: no stray .db on refusal
    }

    [Fact]
    public void EmptyStore_Throws()
    {
        using (new SqliteVectorStore(_dbPath, "fake-embedding")) { } // schema exists, zero documents

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("empty-store", ex.Error.Code);
    }

    [Fact]
    public void CorruptStore_Throws()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database");

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config()));

        Assert.Equal("corrupt-store", ex.Error.Code);
    }

    [Fact]
    public void EmbeddingModelMismatch_Throws()
    {
        using (var store = new SqliteVectorStore(_dbPath, "model-a"))
        {
            store.UpsertPage("docs/x.md", "h1", new[]
            {
                ("doc text", FakeEmbeddingGenerator.HashToVector("doc")),
            });
        }

        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config("model-b")));

        Assert.Equal("embedding-model-mismatch", ex.Error.Code);
    }

    // --- CS_AGENT_BIND (ParseBind, pure — Docker/queue item 3) ---

    [Fact]
    public void ParseBind_NullOrWhitespace_DefaultsLoopback()
    {
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind(null).ToString());
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind("").ToString());
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind("   ").ToString());
    }

    [Fact]
    public void ParseBind_IpLiterals_Accepted()
    {
        Assert.Equal("192.168.1.5", ServeRunner.ParseBind("192.168.1.5").ToString());
        Assert.Equal("::1", ServeRunner.ParseBind("::1").ToString());
        Assert.Equal("0.0.0.0", ServeRunner.ParseBind("0.0.0.0").ToString()); // container case
        Assert.Equal("::", ServeRunner.ParseBind("::").ToString());           // container case
    }

    [Fact]
    public void ParseBind_Ipv4Mapped_NormalizesToIpv4()
    {
        Assert.Equal("127.0.0.1", ServeRunner.ParseBind("::ffff:127.0.0.1").ToString());
    }

    [Theory]
    [InlineData("localhost")]   // hostname: DNS-dependent startup is not validate-loudly
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.256")]  // out-of-range octet
    [InlineData("127.0.0.1/32")] // CIDR, not a host address
    [InlineData("http://127.0.0.1")]
    public void ParseBind_Rejects_ThrowsBadBind(string raw)
    {
        var ex = Assert.Throws<CsAgentException>(() => ServeRunner.ParseBind(raw));
        Assert.Equal("serve", ex.Error.Component);
        Assert.Equal("bad-bind", ex.Error.Code);
    }

    // --- CS_AGENT_BIND wired into Run (bind honored, warning before store checks) ---

    private static void WithServeEnv(string? bind, Action act)
    {
        var prevBind = Environment.GetEnvironmentVariable("CS_AGENT_BIND");
        var prevPort = Environment.GetEnvironmentVariable("CS_AGENT_PORT");
        Environment.SetEnvironmentVariable("CS_AGENT_BIND", bind);
        Environment.SetEnvironmentVariable("CS_AGENT_PORT", null);
        try { act(); }
        finally
        {
            Environment.SetEnvironmentVariable("CS_AGENT_BIND", prevBind);
            Environment.SetEnvironmentVariable("CS_AGENT_PORT", prevPort);
        }
    }

    [Fact]
    public void BadBindEnv_Returns1_NoStoreTouched()
    {
        // bind is validated BEFORE the store checks (bad-port idiom): this config
        // points at a missing store, so reaching store-missing would prove wrong order
        WithServeEnv("localhost", () => Assert.Equal(1, ServeRunner.Run(["serve"], Config())));
    }

    // Warning is a SAFETY line (no-auth API now reachable): both directions live
    // in the suite, not only the one-off container smoke. It must print right
    // after the bind parse — BEFORE the store checks — so it lands in docker logs
    // even when a store error follows.
    [Fact]
    public void NonLoopbackBind_PrintsWarning_BeforeStoreChecks()
    {
        var origErr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            // missing store: throws AFTER the warning should have printed
            WithServeEnv("0.0.0.0",
                () => Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config())));
        }
        finally { Console.SetError(origErr); }
        Assert.Contains("warning: binding 0.0.0.0", captured.ToString());
    }

    [Fact]
    public void LoopbackDefault_NoWarning()
    {
        var origErr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            WithServeEnv(null,
                () => Assert.Throws<CsAgentException>(() => ServeRunner.Run(["serve"], Config())));
        }
        finally { Console.SetError(origErr); }
        Assert.DoesNotContain("warning: binding", captured.ToString());
    }
}