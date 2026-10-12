## What changes

<!-- Summary and reason. Link the issue if there is one. -->

## Validation

- [ ] `dotnet build SolidEdgeMcp.slnx` with no warnings
- [ ] Tests of `SolidEdgeMcp.Core.Tests` and `SolidEdgeMcp.Server.Tests` pass
- [ ] Affected acceptance scripts (`run.ps1 A0x`) pass against real Solid Edge, with temporary
      documents closed without saving
- [ ] Measurable result checked (volume, bounding box, face count, exported file)

<!-- State which scripts you ran and with which Solid Edge version. -->

## New or changed tools

- [ ] Registered in `Program.ToolTypes`, with argument cases in `ToolRegistrationTests`
- [ ] Signatures and constants taken from `reference/api` or from `se_com_describe` / `se_com_constants`
- [ ] Paths go through `PathGuard`; each write is verified and undone if it fails

## Documentation

- [ ] `README.md` and `docs/COVERAGE.md` updated
- [ ] `CHANGELOG.md` updated

## Repository

- [ ] No interop, API dumps, `publish/`, `policy.json`, logs or secrets
- [ ] Conventional commits in English
- [ ] New source files start with the copyright and license header
- [ ] I have read the Contributor License Agreement in CLA.md and I agree to its terms
