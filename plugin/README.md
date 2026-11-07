# solid-edge-mcp plugin

Helps you work in Siemens Solid Edge from Claude Code: parts, sheet metal, assemblies, drawings, exports and Design
Manager tasks through a local MCP server, while you stay in control and approve the changes. This project is independent
and is not affiliated with, sponsored by or endorsed by Siemens.

It needs Windows x64 with Solid Edge installed and licensed.

## What it runs and fetches

- `scripts/start.cmd` starts the server, through `scripts/install.ps1` with Windows PowerShell.
- On the first start for each version, `install.ps1` downloads `solid-edge-mcp-<version>-win-x64.zip` and its `.sha256`
  from the [GitHub release](https://github.com/gabichuuuuu/solid-edge-mcp/releases) of that version, checks the SHA-256
  and installs the server in `%LocalAppData%\SolidEdgeMcp\server\<version>`. It runs the executable in that package.
- While that first install lasts, `scripts/bridge.ps1` answers the client as a server with no tools, so that the
  client does not give up on it; when the install ends, it starts the server, passes the messages between both and
  tells the client that the tool list changed. The tools appear then, in the same session.
- On its first start, the server generates the Solid Edge interop from your installation, with Windows PowerShell, in
  `%LocalAppData%\SolidEdgeMcp\interop`.

The server sends nothing over the network of its own and has no telemetry. Tool results go to your MCP client, which may
send them to its AI provider. Details are in the [privacy section](https://github.com/gabichuuuuu/solid-edge-mcp#privacy).

## Try it

The server only works with files in the folders that your policy allows (see the
[main README](https://github.com/gabichuuuuu/solid-edge-mcp#quick-start)). With Solid Edge installed, ask for example:

- "Model a 100 x 60 x 10 steel plate with four 8 mm holes 10 mm from the corners and R5 vertical corner rounds, and tell me
  its volume and mass."
- "Make a sheet metal part with a 100 x 60 tab and two 20 mm flanges, and export its flat pattern to DXF."
- "Make an assembly of a 120 x 80 x 10 base and two 20 x 20 x 30 blocks, check for interference and give me its bill of
  materials."

## Support

Questions and bugs go to the [issues](https://github.com/gabichuuuuu/solid-edge-mcp/issues), and security reports to the
[security policy](https://github.com/gabichuuuuu/solid-edge-mcp/security/policy). License: PolyForm Noncommercial 1.0.0; use in or for a business needs a commercial license.
