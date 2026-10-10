// MCP acceptance client: runs call scripts against the server in a single session and checks the results.
// Usage: SolidEdgeMcp.Acceptance <server exe> <script> [server arguments...] (normally from run.ps1)
// Script, one command per line:
//   # comment
//   [@var =] tool {json}                 call; an unexpected error counts as a failure
//   ! [code] tool {json}                 call that must fail (with that error code, if given)
//   ?= path value [tolerance]            checks a number (absolute tolerance, default 1e-3) or a text
//   ?~ path text                         checks that the value contains the text
//   !list | !schemas                     summary or schemas of the tools
// {{var.field[0].sub}} is replaced by the stored value; {{work}} is the work folder (SE_MCP_WORK or
// %LocalAppData%\SolidEdgeMcp\work\acceptance), already escaped for JSON.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

string exe = args[0];
string script = args[1];
string work = Environment.GetEnvironmentVariable("SE_MCP_WORK")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidEdgeMcp", "work", "acceptance");
Directory.CreateDirectory(work);

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = exe,
    Arguments = args.Skip(2).ToArray(),
    Name = "solid-edge",
    StandardErrorLines = line => Console.Error.WriteLine("  [server] " + line),
});
await using McpClient client = await McpClient.CreateAsync(transport);
var vars = new Dictionary<string, JsonElement>
{
    ["work"] = JsonDocument.Parse(JsonSerializer.Serialize(work.Replace("\\", "\\\\", StringComparison.Ordinal))).RootElement.Clone(),
};
int calls = 0, failures = 0, checks = 0;
var failed = new List<string>();
int number = 0;
foreach (string raw in File.ReadAllLines(script))
{
    number++;
    string line = raw.Trim();
    if (line.Length == 0 || line.StartsWith('#'))
    {
        continue;
    }

    try
    {
        if (line == "!list" || line == "!schemas")
        {
            IList<McpClientTool> tools = await client.ListToolsAsync();
            foreach (McpClientTool t in tools)
            {
                Console.WriteLine(line == "!list"
                    ? $"{t.Name}: {t.ProtocolTool.InputSchema.GetRawText().Length} chars schema, {t.Description.Length} chars desc"
                    : $"## {t.Name}\n{t.Description}\n{t.ProtocolTool.InputSchema.GetRawText()}\n");
            }

            Console.WriteLine($"TOTAL tools={tools.Count} json={tools.Sum(t => JsonSerializer.Serialize(t.ProtocolTool).Length)} chars");
            continue;
        }

        if (line.StartsWith("?=", StringComparison.Ordinal) || line.StartsWith("?~", StringComparison.Ordinal))
        {
            checks++;
            // The expected value also accepts {{var.path}} (e.g. the same handle as another call).
            line = line[..2] + " " + line[2..].Trim().Split(' ', 2) switch
            {
                [string path, string rest] => path + " " + Regex.Replace(rest, @"\{\{([^{}]+)\}\}", m => Resolve(vars, m.Groups[1].Value)),
                [string path] => path,
                _ => "",
            };
            string[] parts = line[2..].Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string actual = Resolve(vars, parts[0]);
            string expected = parts.Length > 1 ? parts[1] : "";
            bool pass;
            if (line[1] == '~')
            {
                expected = line[2..].Trim()[parts[0].Length..].Trim();
                pass = actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            }
            else if (double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double want)
                && double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double got))
            {
                double tolerance = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 1e-3;
                pass = Math.Abs(got - want) <= tolerance;
            }
            else
            {
                pass = actual == expected;
            }

            Console.WriteLine($"   {(pass ? "PASS" : "FAIL")} {parts[0]} = {actual} (expected {expected})");
            if (!pass)
            {
                failures++;
                failed.Add($"line {number}: {parts[0]} = {actual}, expected {expected}");
            }

            continue;
        }

        bool expectError = false;
        string? expectedCode = null;
        if (line.StartsWith('!'))
        {
            expectError = true;
            line = line[1..].Trim();
            Match code = Regex.Match(line, @"^\[(\w+)\]\s*");
            if (code.Success)
            {
                expectedCode = code.Groups[1].Value;
                line = line[code.Length..];
            }
        }

        string? target = null;
        Match assign = Regex.Match(line, @"^@(\w+)\s*=\s*(.*)$");
        if (assign.Success)
        {
            target = assign.Groups[1].Value;
            line = assign.Groups[2].Value;
        }

        line = Regex.Replace(line, @"\{\{([^{}]+)\}\}", m => Resolve(vars, m.Groups[1].Value));
        int space = line.IndexOf(' ');
        string tool = space < 0 ? line : line[..space];
        string json = space < 0 ? "{}" : line[(space + 1)..];
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        Console.WriteLine($">> {(expectError ? "! " : "")}{tool} {json}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        calls++;
        bool isError = result.IsError == true;
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        foreach (ContentBlock block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t:
                    Console.WriteLine((isError ? "ERROR " : "") + t.Text);
                    if (target is not null && !isError)
                    {
                        try { vars[target] = JsonDocument.Parse(t.Text).RootElement.Clone(); } catch (JsonException) { }
                    }

                    break;
                case ImageContentBlock image:
                    string file = Path.Combine(work, $"{target ?? "image"}.png");
                    File.WriteAllBytes(file, image.DecodedData.ToArray());
                    Console.WriteLine($"[image {image.MimeType} {image.DecodedData.Length} bytes -> {file}]");
                    if (target is not null)
                    {
                        vars[target] = JsonDocument.Parse(JsonSerializer.Serialize(new { bytes = image.DecodedData.Length })).RootElement.Clone();
                    }

                    break;
            }
        }

        bool ok = expectError
            ? isError && (expectedCode is null || text.Contains($"[{expectedCode}]", StringComparison.Ordinal))
            : !isError;
        Console.WriteLine($"   ({sw.ElapsedMilliseconds} ms){(ok ? "" : expectError ? " FAIL: expected an error" + (expectedCode is null ? "" : $" [{expectedCode}]") : " FAIL: unexpected error")}");
        if (!ok)
        {
            failures++;
            failed.Add($"line {number}: {tool} {(expectError ? "did not fail as expected" : "failed")}: {Shorten(text)}");
        }
    }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException or IndexOutOfRangeException or FormatException)
    {
        failures++;
        failed.Add($"line {number}: script error {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"   FAIL script error at line {number}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {Path.GetFileName(script)}: {calls} calls, {checks} checks, {failures} failures");
foreach (string f in failed)
{
    Console.WriteLine("  - " + f);
}

return failures == 0 ? 0 : 1;

static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

static string Resolve(Dictionary<string, JsonElement> vars, string path)
{
    // Dots inside a filter ([?name="a.par"]) do not separate segments.
    string[] parts = Regex.Split(path, @"\.(?![^\[]*\])");
    JsonElement current = vars[parts[0]];
    foreach (string part in parts.Skip(1))
    {
        Match filter = Regex.Match(part, @"^(\w*)\[\?(\w+)=(.+)\]$");
        Match index = Regex.Match(part, @"^(\w*)\[(\d+)\]$");
        if (filter.Success)
        {
            // items[?name=X]: first element whose field matches (numbers with tolerance).
            if (filter.Groups[1].Value.Length > 0) current = current.GetProperty(filter.Groups[1].Value);
            string key = filter.Groups[2].Value;
            JsonElement want = JsonDocument.Parse(filter.Groups[3].Value).RootElement;
            current = current.EnumerateArray().First(e => e.TryGetProperty(key, out JsonElement v) && Same(v, want));
        }
        else if (index.Success)
        {
            if (index.Groups[1].Value.Length > 0) current = current.GetProperty(index.Groups[1].Value);
            current = current[int.Parse(index.Groups[2].Value, CultureInfo.InvariantCulture)];
        }
        else
        {
            current = current.GetProperty(part);
        }
    }

    return current.ValueKind == JsonValueKind.String ? current.GetString()! : current.GetRawText();
}

static bool Same(JsonElement a, JsonElement b)
{
    if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return Math.Abs(a.GetDouble() - b.GetDouble()) < 1e-3;
    if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
    {
        var x = a.EnumerateArray().ToList(); var y = b.EnumerateArray().ToList();
        return x.Count == y.Count && x.Zip(y).All(t => Same(t.First, t.Second));
    }
    return a.GetRawText() == b.GetRawText();
}
