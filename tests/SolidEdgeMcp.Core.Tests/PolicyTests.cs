using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class PolicyTests : IDisposable
{
    private readonly string _base = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Parses_full_policy_with_comments_and_trailing_commas()
    {
        const string json = """
            {
              // Carpeta de proyectos
              "allowedRoots": ["D:\\CAD"],
              "startSolidEdgeIfNotRunning": false,
              "operationTimeoutSeconds": 60,
              "startupTimeoutSeconds": 90,
              "limits": { "maxListItems": 50, },
            }
            """;
        ServerPolicy policy = ServerPolicy.Parse(json);
        Assert.Equal(["D:\\CAD"], policy.AllowedRoots);
        Assert.False(policy.StartSolidEdgeIfNotRunning);
        Assert.Equal(60, policy.OperationTimeoutSeconds);
        Assert.Equal(90, policy.StartupTimeoutSeconds);
        Assert.Equal(50, policy.Limits.MaxListItems);
        Assert.Equal(4096, policy.Limits.MaxImageWidth);
    }

    [Fact]
    public void Defaults_when_properties_are_missing()
    {
        ServerPolicy policy = ServerPolicy.Parse("{}");
        Assert.Empty(policy.AllowedRoots);
        Assert.True(policy.StartSolidEdgeIfNotRunning);
        Assert.Equal(120, policy.OperationTimeoutSeconds);
    }

    [Theory]
    [InlineData("""{ "allowedRoot": [] }""")]
    [InlineData("""{ "limits": { "maxItems": 3 } }""")]
    [InlineData("""{ "operationTimeoutSeconds": 0 }""")]
    [InlineData("""{ "allowedRoots": ["relativa"] }""")]
    [InlineData("""{ "allowedRoots": [""] }""")]
    [InlineData("""{ "limits": { "maxImageWidth": 1 } }""")]
    [InlineData("not json")]
    [InlineData("null")]
    public void Rejects_invalid_policies(string json) =>
        Assert.Throws<PolicyException>(() => ServerPolicy.Parse(json));

    [Fact]
    public void Load_prefers_argument_then_environment_then_default()
    {
        var paths = new AppPaths(Path.Combine(_base, "data"));
        Directory.CreateDirectory(paths.DataRoot);
        string fromArg = Path.Combine(_base, "arg.json");
        string fromEnv = Path.Combine(_base, "env.json");
        File.WriteAllText(fromArg, """{ "operationTimeoutSeconds": 11 }""");
        File.WriteAllText(fromEnv, """{ "operationTimeoutSeconds": 22 }""");
        File.WriteAllText(paths.DefaultPolicyFile, """{ "operationTimeoutSeconds": 33 }""");

        Assert.Equal(11, ServerPolicy.Load(["--policy", fromArg], paths, _ => fromEnv).OperationTimeoutSeconds);
        Assert.Equal(11, ServerPolicy.Load([$"--policy={fromArg}"], paths, _ => fromEnv).OperationTimeoutSeconds);
        Assert.Equal(22, ServerPolicy.Load([], paths, _ => fromEnv).OperationTimeoutSeconds);
        ServerPolicy byDefault = ServerPolicy.Load([], paths, _ => null);
        Assert.Equal(33, byDefault.OperationTimeoutSeconds);
        Assert.Equal(paths.DefaultPolicyFile, byDefault.SourceFile);
    }

    [Fact]
    public void Load_without_any_file_uses_defaults()
    {
        var paths = new AppPaths(Path.Combine(_base, "vacio"));
        ServerPolicy policy = ServerPolicy.Load([], paths, _ => null);
        Assert.Null(policy.SourceFile);
        Assert.Empty(policy.AllowedRoots);
    }

    [Fact]
    public void Explicit_missing_or_relative_file_is_an_error()
    {
        var paths = new AppPaths(Path.Combine(_base, "x"));
        Assert.Throws<PolicyException>(() => ServerPolicy.Load(["--policy", Path.Combine(_base, "no.json")], paths, _ => null));
        Assert.Throws<PolicyException>(() => ServerPolicy.Load(["--policy", "rel.json"], paths, _ => null));
        Assert.Throws<PolicyException>(() => ServerPolicy.Load(["--policy"], paths, _ => null));
    }
}
