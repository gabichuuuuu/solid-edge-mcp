# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.6.0] - 2026-10-13

First release of solid-edge-mcp, an MCP server through which Claude or another MCP client works in Solid Edge 2026 for
the designer, who stays in control: it models, assembles, details, measures and exports on request, checks every change
by its measured result and only reads and writes files in the allowed folders. 81 tools; 261 of the 339 Solid Edge
functions of the coverage map work through them (76 %), and each of the other 78 is documented with the reason it does
not and an alternative when there is one. This release replaces the development releases published before (0.2.0 to
1.5.0), which were withdrawn: 1.0.0 is kept for the complete version (see the roadmap in the README). Source only: the
interop is generated from your own Solid Edge installation, so no binaries are published.

### Added

- Server and session: stdio server for Claude Code, Codex and other MCP clients; connects to Solid Edge or starts it;
  a policy file with the allowed folders; progress notifications during long calls; every error with a code and a next
  step.
- Documents and data: new, open, save, save as and close; document properties, variables and dimensions with formulas
  and limits, families of parts and of assemblies, materials, colours and face styles, layers, and file links (list,
  replace, break, where used, search, Pack and Go).
- Query and measurement: feature tree, faces and edges with their geometry, distances and angles, physical properties
  of parts and assemblies, selection, sensors, draft analysis and interferences.
- View and application: fit, orientation, camera, named views, view styles and backgrounds, PNG capture, section views,
  PMI model views, options, ribbon and interface, customization and styles.
- Sketches and reference planes: 2D sketches with relations and edits, 3D sketches, regions and reference planes.
- Part: extrusions, revolutions, holes, threads, rounds, chamfers, patterns, shells, thin walls, drafts, lofts, sweeps,
  tubes, primitives, decals, face edits (move, rotate, replace, split), bodies (split, unite, construction bodies),
  surfaces, construction curves (through points, intersections, projections, contours across faces, wraps, isoclines,
  helices), PMI and simplified models; synchronous modeling with face relations.
- Sheet metal: tabs, flanges, contour flanges, bends, louvers, dimples, etching, mounting bosses, conversion from a
  part, flat patterns and checker sensors.
- Assemblies: placing and moving components, relations, patterns and mirrors, configurations and alternate positions,
  component groups, assembly features (cutouts, holes, counterbores), frames, tubes, bills of materials, copies,
  simplified assemblies and layouts.
- Drawings: base, projected, auxiliary, section, detail and broken views; dimensions with tolerances and fits;
  annotations (notes, balloons, callouts, symbols, technical requirements); parts lists and other tables, title blocks,
  sheets and smart frames.
- Export, publishing and printing: STEP, IGES, Parasolid, STL, JT, SAT, 3MF, PDF, DXF and DWG, read back after
  writing; 3D PDF; 3D print meshes; printing.
- Full API access: describe, read, write and call any object of the Solid Edge API, list collections, search constants
  and run commands, with guards against the calls verified to bring Solid Edge down.
- The coverage map ([docs/COVERAGE.md](docs/COVERAGE.md)) with the status of every function and the reason for each one
  that does not work, and acceptance scripts that check every tool against a real Solid Edge.
