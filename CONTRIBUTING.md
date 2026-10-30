# Contributing

Thank you for your interest. The project has a single maintainer, so please start with an issue before
writing code.

## Before you start

- **Open an issue first**, with the bug report or feature request template. For a bug, include the
  tool call, the error and the measurable result you expected (volume, bounding box, exported file).
- **Contributor license agreement.** To keep the AGPL and the commercial license possible, pull
  requests are merged only from contributors who agree to the [contributor license agreement](CLA.md)
  (CLA), which lets the author relicense their contribution; you keep its copyright. To agree, write in
  the pull request: "I have read the Contributor License Agreement in CLA.md and I agree to its terms."
- **Security problems** are not reported in public issues: see [SECURITY.md](SECURITY.md).

## Development set-up

You need Windows x64, Solid Edge (validated with the 2026 Foundation and Community Edition) and the .NET 10 SDK.

```powershell
.\scripts\generate-interop.ps1    # interop from your Solid Edge installation, into lib\interop
dotnet build SolidEdgeMcp.slnx    # warnings are errors
dotnet test --project tests\SolidEdgeMcp.Core.Tests\SolidEdgeMcp.Core.Tests.csproj
dotnet test --project tests\SolidEdgeMcp.Server.Tests\SolidEdgeMcp.Server.Tests.csproj
```

The interop (`lib/interop`) and the API dumps (`reference/`) are generated from your own Solid Edge
installation and are derived from Siemens software: never commit them.

## Conventions

- Tools are named `se_<area>` and take an `action`; enumerations are in snake_case; descriptions are
  short, in English, with units and an example.
- Tools work in millimetres and degrees; the Solid Edge API in metres and radians.
- Look up API signatures and constants with `se_com_describe` / `se_com_constants` instead of guessing.
- Every file path goes through `PathGuard`. Every write is verified (status and volume change) and
  undone if it fails, with an error that has a code, a message and the next step.
- Unit tests do not exercise COM: validate each new or changed tool against a real Solid Edge, with
  temporary documents in an allowed folder closed without saving, and check the measurable result.
- In the same pull request, update the tool table of [README.md](README.md), the rows of
  [docs/COVERAGE.md](docs/COVERAGE.md) with the status of the real test (✔, or ✘ with the reason and
  the alternative) and an entry under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md). Then run
  `scripts\update-progress.ps1`, which rewrites the progress section, chart and badge.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/), in English.
- Every C# and PowerShell file starts with the copyright and license header (the build checks it for
  C#):

  ```csharp
  // Copyright (c) 2026 Gabriel Spoleto Couto
  // SPDX-License-Identifier: AGPL-3.0-only
  ```

By taking part you agree to follow the [code of conduct](CODE_OF_CONDUCT.md).
