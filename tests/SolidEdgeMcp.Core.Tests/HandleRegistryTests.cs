using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class HandleRegistryTests
{
    [Fact]
    public void Same_object_keeps_its_id()
    {
        var registry = new HandleRegistry(100);
        var value = new object();
        string first = registry.Register(value, "Feature");
        string second = registry.Register(value, "feature");
        Assert.Equal(first, second);
        Assert.StartsWith("feature:", first, StringComparison.Ordinal);
        Assert.Same(value, registry.Resolve(first));
    }

    [Fact]
    public void Composite_values_are_identified_by_their_parts()
    {
        var registry = new HandleRegistry(100);
        object occurrence1 = new(), occurrence2 = new(), face = new();
        string first = registry.Register(new Pair(occurrence1, face), "face");
        Assert.Equal(first, registry.Register(new Pair(occurrence1, face), "face"));
        Assert.NotEqual(first, registry.Register(new Pair(occurrence2, face), "face"));
        Assert.NotEqual(first, registry.Register(face, "face"));
        Assert.Equal(3, registry.OfKind("face").Count);
    }

    private sealed record Pair(object Occurrence, object Element) : ICompositeHandleValue;

    [Fact]
    public void Kind_is_sanitized()
    {
        var registry = new HandleRegistry(100);
        Assert.StartsWith("ref_plane:", registry.Register(new object(), " Ref Plane! "), StringComparison.Ordinal);
        Assert.StartsWith("obj:", registry.Register(new object(), "***"), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalidating_a_scope_expires_only_its_handles()
    {
        var registry = new HandleRegistry(100);
        string a = registry.Register(new object(), "face", "doc:1");
        string b = registry.Register(new object(), "face", "doc:2");
        Assert.Equal(1, registry.InvalidateScope("doc:1", "the document was closed"));

        ToolException ex = Assert.Throws<ToolException>(() => registry.Resolve(a));
        Assert.Equal("handle_expired", ex.Code);
        Assert.Contains("document was closed", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(registry.Resolve(b));
    }

    [Fact]
    public void Invalidating_by_kind_keeps_other_kinds_of_the_scope()
    {
        var registry = new HandleRegistry(100);
        string face = registry.Register(new object(), "face", "doc:1");
        string edge = registry.Register(new object(), "edge", "doc:1");
        string feature = registry.Register(new object(), "feature", "doc:1");
        Assert.Equal(2, registry.InvalidateScope("doc:1", "the model changed", "face", "edge"));

        Assert.Equal("handle_expired", Assert.Throws<ToolException>(() => registry.Resolve(face)).Code);
        Assert.Equal("handle_expired", Assert.Throws<ToolException>(() => registry.Resolve(edge)).Code);
        Assert.NotNull(registry.Resolve(feature));
    }

    [Fact]
    public void Evicts_least_recently_used()
    {
        var registry = new HandleRegistry(2);
        string a = registry.Register(new object(), "x");
        string b = registry.Register(new object(), "x");
        registry.Resolve(a);
        string c = registry.Register(new object(), "x");

        Assert.Equal(2, registry.Count);
        Assert.Equal("handle_expired", Assert.Throws<ToolException>(() => registry.Resolve(b)).Code);
        Assert.NotNull(registry.Resolve(a));
        Assert.NotNull(registry.Resolve(c));
    }

    [Fact]
    public void Unknown_and_wrong_type_handles_fail_clearly()
    {
        var registry = new HandleRegistry(10);
        Assert.Equal("handle_not_found", Assert.Throws<ToolException>(() => registry.Resolve("doc:99")).Code);
        Assert.Equal("invalid_argument", Assert.Throws<ToolException>(() => registry.Resolve("")).Code);
        string id = registry.Register("texto", "s");
        Assert.Equal("handle_wrong_type", Assert.Throws<ToolException>(() => registry.Resolve<List<int>>(id)).Code);
        Assert.Equal("texto", registry.Resolve<string>(id));
    }

    [Fact]
    public void Re_registering_after_removal_gives_a_new_id()
    {
        var registry = new HandleRegistry(10);
        var value = new object();
        string first = registry.Register(value, "x");
        registry.Remove(first);
        Assert.NotEqual(first, registry.Register(value, "x"));
    }
}
