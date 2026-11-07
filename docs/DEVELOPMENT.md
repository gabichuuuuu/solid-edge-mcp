# Development

You need Windows x64, Solid Edge (validated with the 2026 Foundation and Community Edition) and the .NET 10 SDK.
From the repository root (required by `global.json`):

```powershell
.\scripts\generate-interop.ps1    # interop from your Solid Edge installation, into lib\interop
dotnet build SolidEdgeMcp.slnx    # warnings are errors
dotnet test --project tests\SolidEdgeMcp.Core.Tests\SolidEdgeMcp.Core.Tests.csproj
dotnet test --project tests\SolidEdgeMcp.Server.Tests\SolidEdgeMcp.Server.Tests.csproj
```

The tests do not need Solid Edge and do not exercise COM. If `dotnet` is not on the `PATH`, use the
one in `C:\Program Files\dotnet`. The interop (`lib/interop`) and the API dumps (`reference/`, from
`scripts\dump-api.ps1`) are generated from your own Solid Edge installation and are derived from Siemens
software: they are not versioned.

### Repository structure

```text
solid-edge-mcp/
├── src/
│   ├── SolidEdgeMcp.Server/       MCP executable: Program.cs, Tools/, Infrastructure/
│   ├── SolidEdgeMcp.SolidEdge/    COM: Com/ (StaComDispatcher, generic layer), Services/, Session/
│   └── SolidEdgeMcp.Core/         no COM: PathGuard, policy, units, handles, errors
├── tests/
│   ├── SolidEdgeMcp.Core.Tests/   unit tests, no Solid Edge
│   ├── SolidEdgeMcp.Server.Tests/ tool registration and arguments, no Solid Edge
│   └── SolidEdgeMcp.Acceptance/   MCP client, run.ps1 and scripts A01–A136 against real Solid Edge
├── scripts/                       generate-interop.ps1, dump-api.ps1, package.ps1, update-docs.ps1
├── plugin/                        Claude Code plugin: manifest and launcher (start.cmd, install.ps1, bridge.ps1)
├── .claude-plugin/                marketplace.json, the marketplace of the plugin
├── config/                        policy.example.json
├── docs/                          COVERAGE.md, TOOLS.md, EXAMPLES.md, VALIDATION.md, LIMITATIONS.md, ARCHITECTURE.md, DEVELOPMENT.md, assets/
├── build/                         SolidEdgeInterop.props, the interop references of the projects
├── .github/                       issue templates and the CI workflow
├── lib/interop/  reference/       generated on each machine, not versioned
└── CHANGELOG.md  LICENSE  NOTICE  CONTRIBUTING.md  SECURITY.md  CODE_OF_CONDUCT.md
```

### Acceptance suite

Tools are validated against real Solid Edge with `tests/SolidEdgeMcp.Acceptance`: scripts of MCP
calls (`A01`–`A136`, listed in [VALIDATION.md](VALIDATION.md)) with expected volumes, measurements
and errors written in advance. Each script creates and closes its documents without saving:

```powershell
dotnet build SolidEdgeMcp.slnx
.\tests\SolidEdgeMcp.Acceptance\run.ps1            # all; or e.g. run.ps1 A03 A09
.\tests\SolidEdgeMcp.Acceptance\run.ps1 -Server publish\SolidEdgeMcp.Server.exe   # the published build
```

The script syntax is described in the header of `tests/SolidEdgeMcp.Acceptance/Program.cs`.

### Republishing while the server is in use

While a session uses the server, `publish/` is locked. Publish to another folder and replace only the
files that change, moving the old ones into a subfolder: Windows allows renaming a loaded executable or
DLL, but not overwriting it. This is safe if the changed files are already loaded in that process
(usually only `SolidEdgeMcp.*`); the session keeps running the old version until the server restarts.

### Distribution package and plugin

`.\scripts\package.ps1` builds `artifacts\solid-edge-mcp-<version>-win-x64.zip` (self-contained,
without the Solid Edge interop, with `generate-interop.ps1` beside the server) and its `.sha256`. It
fails if an interop file or reference, or a local path or user name, ends up in the package. Both files go to the GitHub release of
the version. The plugin in `plugin/` installs the package of its own version, which
`scripts\update-docs.ps1` keeps equal to the project version. To try a package before its
release, point the launcher to it with `SOLID_EDGE_MCP_PACKAGE` (its `.sha256` beside it); it is
installed in a folder named after its hash, so a rebuilt package is installed again:

```powershell
claude plugin validate . --strict
$env:SOLID_EDGE_MCP_PACKAGE = "$PWD\artifacts\solid-edge-mcp-<version>-win-x64.zip"
claude --plugin-dir .\plugin
```

When the server is not installed, `start.cmd` hands over to `bridge.ps1`. It waits up to 10 seconds
for the install and, if it lasts longer, answers the client as a server with no tools (the
`server/discover` probe gets "method not found", so the client falls back to `initialize`) until it
can start the server, repeat the client's `initialize` to it and send
`notifications/tools/list_changed`; from then on it passes the messages both ways.

### Adding or changing a tool

1. Do not guess signatures or constants: look them up in `reference/api` or with `se_com_describe` /
   `se_com_constants`.
2. Register it in `Program.ToolTypes` and add argument cases to `ToolRegistrationTests`.
3. Validate it against real Solid Edge with temporary documents in an allowed folder, closing them
   without saving, and check the measurable result (volume, bounding box, face count, exported file).
4. Write its rows in [COVERAGE.md](COVERAGE.md) with the status found in step 3, describe it in
   [TOOLS.md](TOOLS.md) and add it to [CHANGELOG.md](../CHANGELOG.md). Raise the `<Version>` of
   `src/SolidEdgeMcp.Server/SolidEdgeMcp.Server.csproj` with the release.

Tools are named `se_<area>` and take an `action`, with enumerations in snake_case and short English
descriptions with units and an example; they work in millimetres and degrees, the Solid Edge API in metres
and radians. Every file path goes through `PathGuard`, and every C# and PowerShell file starts with the
copyright and license header (the build checks it for C#, the CI for both):

```csharp
// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
```

Commits follow Conventional Commits, in English.

The tool count and the version shown in the README, and the version of the plugin, are generated by
`scripts\update-docs.ps1` from the tools declared in `src` and the project version. It also checks every row of
`docs/COVERAGE.md`, and lists there as not tested a tool that the file does not mention yet. Run it before committing, or
from a local pre-commit hook with `-Staged`, which regenerates them from what the commit contains.
