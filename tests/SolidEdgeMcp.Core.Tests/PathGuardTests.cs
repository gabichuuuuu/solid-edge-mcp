using System.Diagnostics;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Core.Tests;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _base;
    private readonly string _allowed;
    private readonly string _allowed2;
    private readonly string _work;
    private readonly string _outside;
    private readonly List<string> _links = [];

    public PathGuardTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "SolidEdgeMcpTests", Guid.NewGuid().ToString("N"));
        _allowed = Directory.CreateDirectory(Path.Combine(_base, "Allowed")).FullName;
        _allowed2 = Directory.CreateDirectory(Path.Combine(_base, "Allowed2")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "Outside")).FullName;
        _work = Path.Combine(_base, "work");
    }

    public void Dispose()
    {
        // Junctions are removed first so their target is not traversed.
        foreach (string link in _links)
        {
            try
            {
                Directory.Delete(link, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PathGuard Guard() => new([_allowed], _work);

    [Fact]
    public void Allows_file_inside_root_even_if_it_does_not_exist()
    {
        string path = Path.Combine(_allowed, "sub", "part.par");
        string checkedPath = Guard().Check(path);
        Assert.EndsWith(@"Allowed\sub\part.par", checkedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allows_root_itself_and_work_folder()
    {
        PathGuard guard = Guard();
        Assert.True(guard.IsAllowed(_allowed));
        Assert.True(guard.IsAllowed(Path.Combine(_work, "captura.png")));
    }

    [Fact]
    public void Rejects_misleading_prefix()
    {
        // C:\...\Allowed2 is not inside C:\...\Allowed.
        ToolException ex = Assert.Throws<ToolException>(() => Guard().Check(Path.Combine(_allowed2, "a.par")));
        Assert.Equal("path_not_allowed", ex.Code);
    }

    [Fact]
    public void Rejects_parent_traversal()
    {
        string path = _allowed + @"\..\Outside\a.par";
        Assert.Equal("path_not_allowed", Assert.Throws<ToolException>(() => Guard().Check(path)).Code);
    }

    [Fact]
    public void Accepts_traversal_that_stays_inside()
    {
        string path = _allowed + @"\sub\..\a.par";
        Assert.True(Guard().IsAllowed(path));
    }

    [Fact]
    public void Is_case_insensitive_and_accepts_forward_slashes()
    {
        string path = _allowed.ToUpperInvariant().Replace('\\', '/') + "/x.par";
        Assert.True(Guard().IsAllowed(path));
    }

    [Theory]
    [InlineData(@"relative\a.par")]
    [InlineData(@"\no\drive.par")]
    [InlineData(@"C:no\slash.par")]
    [InlineData(@"\\?\C:\x\a.par")]
    [InlineData(@"\\.\C:\x\a.par")]
    public void Rejects_non_absolute_and_device_prefixes(string path)
    {
        string code = Assert.Throws<ToolException>(() => Guard().Check(path)).Code;
        Assert.Equal("invalid_path", code);
    }

    [Theory]
    [InlineData("a.par:stream")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1.par")]
    [InlineData("acaba.")]
    [InlineData("acaba ")]
    [InlineData("a*.par")]
    public void Rejects_dangerous_names_inside_root(string name)
    {
        string path = _allowed + "\\" + name;
        string code = Assert.Throws<ToolException>(() => Guard().Check(path)).Code;
        Assert.Equal("invalid_path", code);
    }

    [Fact]
    public void Rejects_empty()
    {
        Assert.Equal("invalid_argument", Assert.Throws<ToolException>(() => Guard().Check("  ")).Code);
    }

    [Fact]
    public void Junction_inside_root_pointing_outside_is_rejected()
    {
        string link = Path.Combine(_allowed, "enlace");
        if (!CreateJunction(link, _outside))
        {
            Assert.Skip("mklink /J is not available.");
        }

        Assert.Equal("path_not_allowed", Assert.Throws<ToolException>(() => Guard().Check(Path.Combine(link, "a.par"))).Code);
    }

    [Fact]
    public void Junction_outside_pointing_inside_root_is_allowed()
    {
        string link = Path.Combine(_outside, "to_allowed");
        if (!CreateJunction(link, _allowed))
        {
            Assert.Skip("mklink /J is not available.");
        }

        Assert.True(Guard().IsAllowed(Path.Combine(link, "a.par")));
    }

    [Fact]
    public void Short_8dot3_name_is_resolved()
    {
        string longDir = Directory.CreateDirectory(Path.Combine(_allowed, "A very long folder name")).FullName;
        string? shortPath = GetShortPath(longDir);
        if (shortPath is null || string.Equals(shortPath, longDir, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("8.3 names are disabled on this volume.");
        }

        string checkedPath = Guard().Check(Path.Combine(shortPath, "a.par"));
        Assert.Contains("A very long folder name", checkedPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_and_invalid_roots_produce_warnings_not_errors()
    {
        var guard = new PathGuard([Path.Combine(_base, "no_existe"), "relativa"], _work);
        Assert.Empty(guard.ConfiguredRoots);
        Assert.Equal(3, guard.Warnings.Count);
        Assert.Single(guard.AllowedRoots);
    }

    [Fact]
    public void Expands_environment_variables_in_roots()
    {
        Environment.SetEnvironmentVariable("SEMCP_TEST_ROOT", _allowed);
        var guard = new PathGuard(["%SEMCP_TEST_ROOT%"], _work);
        Assert.Single(guard.ConfiguredRoots);
        Assert.True(guard.IsAllowed(Path.Combine(_allowed, "a.par")));
    }

    [Theory]
    [InlineData(@"C:\x\a.par", true)]
    [InlineData("C:/x", true)]
    [InlineData(@"\\server\share\a", true)]
    [InlineData("Extrude 1", false)]
    [InlineData("C:", false)]
    public void LooksLikePath_detects_absolute_paths(string value, bool expected) =>
        Assert.Equal(expected, PathGuard.LooksLikePath(value));

    private bool CreateJunction(string link, string target)
    {
        _links.Add(link);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process? process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    private static string? GetShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = NativeShortPath.GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 ? null : new string(buffer, 0, (int)length);
    }
}

internal static class NativeShortPath
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern uint GetShortPathNameW(string lpszLongPath, [System.Runtime.InteropServices.Out] char[] lpszShortPath, uint cchBuffer);
}
