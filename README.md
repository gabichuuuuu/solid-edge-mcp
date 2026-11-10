<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/banner-dark.svg">
  <img src="docs/assets/banner.svg" alt="solid-edge-mcp: MCP server for Siemens Solid Edge. Model, assemble and draft from any MCP client" width="100%">
</picture>

**Unofficial local [MCP](https://modelcontextprotocol.io) server that helps you work in Siemens Solid Edge from Claude Code,
Claude Desktop, Codex or any other MCP client.**

[![Core](https://github.com/gabichuuuuu/solid-edge-mcp/actions/workflows/core.yml/badge.svg)](https://github.com/gabichuuuuu/solid-edge-mcp/actions/workflows/core.yml)
![Version 1.0.4](https://img.shields.io/badge/version-1.0.4-2F6FEB)
![Status: stable](https://img.shields.io/badge/status-stable-2EA043)
[![License: PolyForm Noncommercial](https://img.shields.io/badge/license-PolyForm%20Noncommercial-blue)](#license)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)
![Siemens Solid Edge 2026](https://img.shields.io/badge/Siemens%20Solid%20Edge-2026-009999)
![MCP stdio](https://img.shields.io/badge/MCP-stdio-111111?logo=modelcontextprotocol&logoColor=white)
![87 tools](https://img.shields.io/badge/tools-87-2F6FEB)

[Quick start](#quick-start) · [Examples](docs/EXAMPLES.md) · [Tools](docs/TOOLS.md) · [Coverage](docs/COVERAGE.md) · [Limitations](docs/LIMITATIONS.md) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

</div>

It lets an AI assistant help you work in Solid Edge through COM: querying models, drawing
sketches, creating part and sheet metal features, building assemblies, making drawings,
exporting, and managing closed files as Design Manager does. You stay in control and approve the
changes. Nothing in it is tied to particular files or workflows.

- Parts, sheet metal, assemblies, drawings, simulation, export and Design Manager with 87 tools, plus a generic layer
  for the rest of the Solid Edge API.
- Each modeling operation is measured. If it fails or has no effect, it is undone and the error explains why and what
  to do next.
- Files are opened and saved only inside the folders you allow, and tools that can change anything are flagged so your
  MCP client asks for your approval.
- 136 acceptance scripts run against real Solid Edge and compare the results with values computed in advance.

> [!NOTE]
> Stable version (1.0.4), tested with Solid Edge **2026 Foundation** and **Community Edition 2026** on Windows 11 x64
> ([validation](docs/VALIDATION.md)). Versions follow [Semantic Versioning](https://semver.org): tool names and parameters
> are stable, and a change that breaks them waits for a new major version and is listed in the changelog. Not every Solid
> Edge function can be used through its API: [docs/COVERAGE.md](docs/COVERAGE.md) lists each one with its route, or the
> reason and the alternative.

## What you can ask for

Natural-language requests completed using only this server. The result was measured afterwards, separately, by reopening
the generated files. [More examples](docs/EXAMPLES.md).

| Request (summary) | Client | Verified result |
|---|---|---|
| Sheet metal part with a 100 × 60 tab and two 20 flanges; flat pattern to DXF and an A3 drawing with the flat pattern and an isometric view to PDF | Claude Code | 9,871.239 mm³ = 6000 + 2·(75π + 1700); box 100 × 60 × 21; DXF with the contour and 2 bend lines; one-page A3 PDF |
| Assembly with a 120 × 80 × 10 base and two 20 × 20 × 30 blocks: mates, interference check, bill of materials, and an A3 drawing with a parts list to PDF | Claude Code | 50 calls, 0 errors; 6 mates solved and both blocks fully defined; no interference; BOM 1 + 2; one-page A3 PDF |
| Plate with 4 holes and 4 R5 fillets, 6061-T6 aluminum, exported to STEP | Codex | 20 calls, 0 errors; 58,416.92 mm³ = 58,631.52 − 4·(25 − 25π/4)·10; 0.15843 kg |

## Quick start

### Requirements

- Windows x64 with Solid Edge installed and licensed (the Community Edition and Foundation work; simulation needs a
  licence with Simulation, such as the Simulation Express of Foundation).
- Windows PowerShell 5.1, which comes with Windows: the server uses it to generate the Solid Edge
  interop, and the plugin to install the server.
- To build from source, the .NET 10 SDK (version pinned in `global.json`).

The Community Edition, and Foundation with a license server, get the license through the machine
variable `SE_LICENSE_SERVER`. Processes opened before Solid Edge was installed do not have it; the
server copies it when it starts `Edge.exe`.

### Install

The Solid Edge interop derives from Siemens files, so it is not distributed: the server generates it
from your installation on its first start, in a few seconds, and keeps it in
`%LocalAppData%\SolidEdgeMcp\interop\<Solid Edge version>`. Install the server in one of three ways.

**As a Claude Code plugin:**

```text
/plugin marketplace add gabichuuuuu/solid-edge-mcp
/plugin install solid-edge-mcp@solid-edge-mcp
```

Or from a terminal:

```powershell
claude plugin install solid-edge-mcp --marketplace gabichuuuuu/solid-edge-mcp
```

On its first start the plugin downloads the server package of its version from the GitHub release,
checks it against the SHA-256 published with it and installs it in
`%LocalAppData%\SolidEdgeMcp\server\<version>`; it then starts the server from there. The download takes
about 40 MB: its tools appear when it ends, in the same session. Its tools are named `mcp__plugin_solid-edge-mcp_solid-edge__se_*`. Then write
your policy (below).

**From the release package**, for any MCP client: download `solid-edge-mcp-<version>-win-x64.zip` and
its `.sha256` from the [releases](https://github.com/gabichuuuuu/solid-edge-mcp/releases), check the
hash and extract it to a folder of your own (`%LocalAppData%\SolidEdgeMcp\server` belongs to the
plugin). The package is self-contained: it does not need .NET.

```powershell
(Get-FileHash .\solid-edge-mcp-<version>-win-x64.zip -Algorithm SHA256).Hash   # the hash in the .sha256
Expand-Archive .\solid-edge-mcp-<version>-win-x64.zip "$env:LOCALAPPDATA\Programs\solid-edge-mcp"
```

**From source:**

```powershell
git clone https://github.com/gabichuuuuu/solid-edge-mcp.git
cd solid-edge-mcp

# 1. Interop from the type libraries of your Solid Edge (not distributed; goes to lib\interop)
.\scripts\generate-interop.ps1

# 2. Build and publish (self-contained for win-x64)
dotnet publish src\SolidEdgeMcp.Server -c Release -r win-x64 --self-contained -o publish
```

Optional: `.\scripts\dump-api.ps1` dumps the API to `reference\api\*.txt` so you can look up
signatures and constants. It is not versioned either.

**Policy.** Without one, the server only works in `%LocalAppData%\SolidEdgeMcp\work`. Copy the
example and adjust the allowed folders. It is in `config\` of the repository and beside the server in
the package; the plugin keeps it in `%LocalAppData%\SolidEdgeMcp\server\<version>`, once its first
start has installed the server.

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\SolidEdgeMcp" | Out-Null
# From the repository; with the plugin, use the path of the example given above
Copy-Item config\policy.example.json "$env:LOCALAPPDATA\SolidEdgeMcp\policy.json"
```

Restart the client after changing the policy, which the server reads when it starts.

### Register with your client

The plugin registers itself. Otherwise replace `<path>\publish` with the folder of your build or of
the extracted package, and `<path>\policy.json` with your policy; without `--policy`, the server
loads `%LocalAppData%\SolidEdgeMcp\policy.json`.

**Claude Code**

```powershell
# -s user: in all your projects; -s local: only in the current project
claude mcp add -s user solid-edge -- "<path>\publish\SolidEdgeMcp.Server.exe" --policy "<path>\policy.json"
```

If long operations exceed the client's limit, adjust the `MCP_TOOL_TIMEOUT` variable. When the client asks for progress notifications, the server sends one every 5 seconds while a call runs, so clients that reset their time limit on progress keep waiting.

<details>
<summary><b>Claude Desktop</b> (<code>claude_desktop_config.json</code>)</summary>

```json
{
  "mcpServers": {
    "solid-edge": {
      "command": "<path>\\publish\\SolidEdgeMcp.Server.exe",
      "args": ["--policy", "<path>\\policy.json"]
    }
  }
}
```

</details>

<details>
<summary><b>Codex</b> (<code>~/.codex/config.toml</code>, or <code>.codex/config.toml</code> in a project that Codex trusts)</summary>

```toml
[mcp_servers.solid_edge]
command = "<path>\\publish\\SolidEdgeMcp.Server.exe"
args = ["--policy", "<path>\\policy.json"]
env_vars = ["USERPROFILE", "LOCALAPPDATA", "APPDATA"]  # the policy expands %USERPROFILE%
startup_timeout_sec = 30
tool_timeout_sec = 300
```

</details>

## How it works

- **Few tools, many actions.** Each `se_<area>` tool groups its operations under an `action`
  parameter, so the model does not have to load hundreds of schemas.
- **Millimeters and degrees.** Lengths in mm and angles in degrees. Only the generic `se_com_*`
  layer uses API units (meters and radians, collections starting at 1).
- **Handles.** Objects are returned as identifiers (`doc:3`, `sketch:5`, `face:12`) that are passed
  unchanged to other calls. They expire when the document is closed, undone or recalculated; face
  and edge handles also expire when the model changes.
- **Verification.** Each new operation is checked (status and volume change). If it fails or has no
  effect, it is deleted or undone, and the error carries a code, the cause and the next step.
- **Three layers.** Specific tools verified for common tasks; access to any API object and method
  (`se_com_*`); and any interface command (`se_command`). What each route covers, function by
  function: [docs/COVERAGE.md](docs/COVERAGE.md).

Illustrative example of the sequence an assistant follows for a 100 × 50 × 20 mm block:

```text
se_session    {"action":"connect"}
se_documents  {"action":"new","kind":"part"}
se_sketch     {"action":"create","plane":"1"}                                → sketch:5
se_sketch     {"action":"rectangle","sketch":"sketch:5","points":[0,0,100,50]}
se_sketch     {"action":"close","sketch":"sketch:5"}
se_extrude    {"sketch":"sketch:5","operation":"add","distance":20}
se_physical   {"density":7850}                       → 100 000 mm³ (100 · 50 · 20), 0.785 kg
```

## Tools

87 tools, grouped by area. [What each one does](docs/TOOLS.md).

| Area | Tools |
|---|---|
| **Server and session** | `se_server_info` `se_session` |
| **Documents and data** | `se_documents` `se_properties` `se_variables` `se_part_family` `se_material` `se_appearance` `se_layer` `se_links` |
| **Query** | `se_feature_tree` `se_geometry` `se_measure` `se_physical` `se_selection` `se_sensor` |
| **Simulation** | `se_simulation` |
| **View and application** | `se_view` `se_section_view` `se_model_view` `se_options` `se_ui` `se_app` `se_customize` `se_styles` |
| **Sketches** | `se_sketch` `se_sketch_constraint` `se_sketch_edit` `se_sketch_tools` `se_sketch3d` `se_region` `se_ref_plane` |
| **Part** | `se_extrude` `se_revolve` `se_hole` `se_round` `se_chamfer` `se_pattern` `se_shell` `se_draft` `se_loft_sweep` `se_part_feature` `se_recognize` `se_body` `se_face_relation` `se_face_split` `se_pmi` `se_pmi_annotation` `se_primitive` `se_surface` `se_curve` `se_feature_edit` `se_simplify` `se_thread` `se_thin_wall` `se_decal` |
| **Sheet metal** | `se_sheetmetal` `se_sheetmetal_form` |
| **Assemblies** | `se_assembly` `se_relations` `se_assembly_pattern` `se_assembly_config` `se_assembly_family` `se_component_group` `se_internal_component` `se_component_query` `se_assembly_feature` `se_frame` `se_tube` `se_interference` `se_bom` |
| **Drawings** | `se_drawing` `se_drawing_view` `se_dimension` `se_annotation` `se_sheet_content` |
| **Export, publishing and printing** | `se_export` `se_publish` `se_print` |
| **Programs installed with Solid Edge** | `se_design_manager` |
| **Full API access** | `se_com_describe` `se_com_get` `se_com_set` `se_com_invoke` `se_com_items` `se_com_constants` `se_command` |

Workflow prompts: `model_part`, `part_from_template`, `manufacturing_drawing` and `welded_frame`
([details](docs/TOOLS.md#prompts)).

## Configuration

`policy.json` accepts comments and trailing commas; an unknown property is an error. It is looked up,
in this order, in `--policy <path>`, in the `SOLID_EDGE_MCP_POLICY` variable and in
`%LocalAppData%\SolidEdgeMcp\policy.json`.

| Key | Purpose | Example |
|---|---|---|
| `allowedRoots` | The only folders where files are opened, saved, exported and inserted. Absolute paths; environment variables are expanded. `%LocalAppData%\SolidEdgeMcp\work` is always allowed. | `["%USERPROFILE%\\Documents"]` |
| `startSolidEdgeIfNotRunning` | Starts Solid Edge on first use if none is open. | `true` |
| `operationTimeoutSeconds` | Maximum time of each COM operation, in seconds. | `120` |
| `startupTimeoutSeconds` | Maximum time for Solid Edge to start, in seconds. | `180` |
| `limits` | `maxListItems`, `maxImageWidth`, `maxImageHeight`, `maxHandles`. | `500`, `4096`, `4096`, `5000` |

Full example in [config/policy.example.json](config/policy.example.json). `se_server_info` shows the
effective folders and the configuration warnings.

Logs go to `%LocalAppData%\SolidEdgeMcp\logs` (one UTF-8 file per day; each operation with its
`op#`, time and result size). Standard output is reserved for the MCP protocol. When a packaged client such as Claude Desktop starts the
server, Windows keeps what the server writes under `%LocalAppData%` in that client's own folder, for Claude Desktop
`%LocalAppData%\Packages\Claude_<id>\LocalCache\Local\SolidEdgeMcp` (logs, interop and work files); `se_server_info`
shows the folders in use.

## Security

- **Allowed folders only.** Every file path goes through `PathGuard`, which resolves `..`, links,
  junctions, 8.3 names, network drives and letter case before comparing, and rejects anything outside
  `allowedRoots`: including `\\?\` paths, alternate data streams, device names and links that leave
  the folder.
- **Generic layer behind approval.** `se_com_*` also filters arguments that look like paths or go to
  file methods, but it can do anything the API allows. For that reason, the tools that write
  (`se_com_set`, `se_com_invoke`, `se_command`…) are announced as destructive, so the client asks for
  approval.
- **Documents are data.** Text read from documents (names, properties, notes) is treated as
  information, never as instructions.
- **Errors without stack traces.** The client receives a code, the cause and the next step; the
  details stay in the log.
- **Residual risk.** Solid Edge can open, on its own, linked files of an assembly that are outside
  the allowed folders, and between a path check and its use another process could change a link
  inside an allowed folder.

To report a vulnerability, see [SECURITY.md](SECURITY.md).


## Privacy

- **The server collects nothing.** It has no telemetry or accounts and makes no network connections
  of its own. It talks to your MCP client over standard input and output, and to Solid Edge through COM.
- **What your AI client receives.** Tool results, such as names, properties, measurements and
  captures of your documents, go to the MCP client, which may send them to its AI provider under that
  provider's terms. Only use it on documents you are allowed to share that way.
- **Local files.** The policy, the generated interop, the installed server and the logs stay in
  `%LocalAppData%\SolidEdgeMcp` (or in the client's own folder, see [Configuration](#configuration)). The logs record operations and can include file paths; they are
  kept for 14 days and never sent anywhere.
- **Network use.** The plugin only downloads the server package from the GitHub release on its first
  start for each version, and GitHub handles that request under its own terms. Solid Edge contacts its
  license server on its own.


## Documentation

| Document | Contents |
|---|---|
| [Tools](docs/TOOLS.md) | What each tool does, and the workflow prompts |
| [Coverage](docs/COVERAGE.md) | Every Solid Edge function, its route and its status |
| [Examples](docs/EXAMPLES.md) | Requests completed end to end, with their measured results |
| [Known limitations](docs/LIMITATIONS.md) | What Solid Edge or the server does not allow |
| [Validation](docs/VALIDATION.md) | Test results and every acceptance script |
| [Architecture](docs/ARCHITECTURE.md) | How the server is built |
| [Development](docs/DEVELOPMENT.md) | Build, tests, packaging and adding a tool |

## Project status

Stable. The current version (1.0.4) covers the base program and the programs installed with Solid Edge where their
automation allows ([what it cannot do](docs/LIMITATIONS.md)), and it installs without compiling, from the release package
or as a Claude Code plugin. Maintained by
a single author: fixes go to `main` and to the next release, and versions follow Semantic Versioning. Change history in
[CHANGELOG.md](CHANGELOG.md).

## Acknowledgements

Thanks to [@AReq401](https://github.com/AReq401) for the first outside contribution: the report and the fix for where
view captures are written when a packaged client such as Claude Desktop starts the server (1.0.1).

## License

Copyright (c) 2026 Gabriel Spoleto Couto.

Released under the [PolyForm Noncommercial License 1.0.0](LICENSE). The source code is public, and
you may use, change and share it for noncommercial purposes: personal use (research, study, testing,
hobby projects) without any anticipated commercial application, and use by charities, educational
institutions, public research organizations and government institutions. Any other use, such as use
in or for a business, needs a commercial license. This summary does not replace the license text;
the [NOTICE](NOTICE) file carries the required notice, and every source file the copyright and
license header.

**Commercial license.** To use it for a commercial purpose, a commercial license is available from
the author: open an issue with the
[Commercial license](https://github.com/gabichuuuuu/solid-edge-mcp/issues/new/choose) template, without
confidential details, and you will get a private contact.

**Contributions.** Bug reports and feature requests are welcome as issues; pull requests are not
accepted. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Trademarks and disclaimer

Solid Edge is a trademark or registered trademark of Siemens Industry Software Inc. or its
subsidiaries or affiliates. Siemens is a registered trademark of Siemens Trademark GmbH & Co. KG.
Other trademarks belong to their respective owners. This project is independent and is not
affiliated with, sponsored by or endorsed by Siemens. The COM interop and the API dumps are generated locally from your
own Solid Edge installation. They are not part of this repository or of the release packages.
