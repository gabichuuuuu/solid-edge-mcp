# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- License: the project is released under the MIT License ([LICENSE](LICENSE)).

## [0.1.0-alpha] - unreleased

First alpha version, developed in a private repository. Validated against Solid Edge Community
Edition 2026.

### Added

- Local MCP server over stdio (C#/.NET 10, `ModelContextProtocol` SDK 2.2.0) that controls Solid
  Edge through COM, with 40 `se_*` tools: session, documents, data, query, view, sketches, part,
  sheet metal, assemblies, drawings, export and generic API access (`se_com_*`, `se_command`).
- Policy through `policy.json` and `PathGuard`: files are only opened, saved, exported and inserted
  in the allowed folders.
- Verification of each modeling operation (status and volume change) and reversal if it fails.
- `se_material`: libraries, properties and material assignment.
- `se_loft_sweep`: lofts, sweeps and helices, as a boss or a cut.
- `se_sketch`: ellipses, splines, points, corner fillet and edge projection;
  `se_sketch_constraint`: sketch relations and dimensions.
- `se_geometry`: summary of an assembly occurrence in assembly coordinates.
- Acceptance suite against real Solid Edge (`tests/SolidEdgeMcp.Acceptance`, scripts A01–A10 and
  `run.ps1`), also runnable against another server build with `-Server`.
- Self-contained win-x64 publication, with lock files shared by build and publish.

### Fixed

- COM: translation of the errors that .NET converts into other exceptions, and `null` in unused
  object arguments.
- Sketches: real distance and interior-angle dimensions, projected edges returned, and a closed
  profile when one side is the revolve axis.
- Holes: countersink angle in degrees, and threading of finite holes.
- View: display modes that Solid Edge does not apply are rejected.
- Material: faces and edges keep their handles after a material is applied.
- Assemblies: face and edge handles belong to each occurrence, and a flush mate is recreated in the
  opposite direction when Solid Edge leaves it unsatisfied.
- Documents: handles of documents unloaded when another is closed are released; undo and redo are
  checked with the Solid Edge transaction counter; welds are created with the weldment assembly
  template when there is no `.pwd` template.
- Session: `status` no longer reconnects after `disconnect`.
- Sheet metal: the contour flange returns all the operations it creates and rejects the reverse
  direction, which Solid Edge ignores; the tab only offers the normal and reverse directions.
- Drawings: framing and capture of the whole sheet, and a warning when a drawing that is not saved is
  exported.
- Properties: all property sets are listed, even when some properties have no name.
- Acceptance: scripts accept dots inside the filter values of paths.

### Documentation

- `README.md`, example policy and coverage matrix
  ([docs/COBERTURA.md](docs/COBERTURA.md)).
- End-to-end runs with Claude Code and Codex are summarized in the README.
- README rewritten, this changelog, and issue and pull request templates.
