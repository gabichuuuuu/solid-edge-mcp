# solid-edge-mcp plugin

Helps you work in Siemens Solid Edge from Claude Code: parts, sheet metal, assemblies, drawings, exports and Design
Manager tasks through a local MCP server, while you stay in control and approve the changes.

It needs Windows x64 with Solid Edge installed and licensed. On its first start for each version, the plugin downloads
the server package from the GitHub release, checks its SHA-256 and installs it in
`%LocalAppData%\SolidEdgeMcp\server\<version>`.

The server only works with files in the folders that your policy allows. Installation, policy, tools, privacy and
known limitations are in the [main README](https://github.com/gabichuuuuu/solid-edge-mcp#readme).

This project is independent and is not affiliated with, sponsored by or endorsed by Siemens. License: AGPL-3.0-only.
