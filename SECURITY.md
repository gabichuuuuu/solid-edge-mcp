# Security policy

## Supported versions

The project is in alpha: fixes go to the `main` branch and to the next release. Use the latest
release or the latest commit of `main`.

## Reporting a vulnerability

Do not open a public issue. Report it privately through GitHub:
[Security → Report a vulnerability](https://github.com/gabichuuuuu/solid-edge-mcp/security/advisories/new).

Include the version or commit, the Solid Edge version, the MCP client, the steps or the tool calls
that reproduce it and what an attacker could do with it. The project has a single maintainer, who
answers as soon as possible and agrees with you on the disclosure once a fix is available.

## Scope

In scope, for example:

- A file path that escapes `allowedRoots` (a way around `PathGuard`), in any tool or in the generic
  layer.
- A tool that reads, writes or runs something without the approval that the MCP client asks for.
- Credentials, licence values or file contents written to the logs or returned where they should not
  be.

Out of scope:

- What Solid Edge itself does: report it to Siemens.
- The generic layer (`se_com_*`, `se_command`) doing what it was asked: it reaches the whole API by
  design, which is why it is announced as destructive and needs approval.

How the server limits what it can do is described in the [Security](README.md#security) section of the
README.
