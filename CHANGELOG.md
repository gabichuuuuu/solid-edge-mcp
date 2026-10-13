# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.7.0] - 2026-10-13

The base program at its maximum. The functions of the coverage map that failed through the API were tried again by
other routes (typed calls, other argument values and constants, collections, the selection), and new actions cover
more of Solid Edge: 274 of the 343 functions work through the 81 tools (79 %, from 261 of 339 in 0.6.0). Each of the
other 69 is documented with the reason and an alternative when there is one: 40 have no route through the API of
Solid Edge 2026, 9 leave it unstable and are refused by the generic layer, and 20 need another edition or
installation, an interactive command or a separate program. The acceptance scripts added since the last full run
(A54 to A89: 966 calls, 410 checks) and an end-to-end session from a request in plain language pass with this version.

### Added

- Parts: `se_loft_sweep solid`, a closed construction body (the tool) moved along a sketch path and cut from or added
  to the part.
- Sheet metal: `se_sheetmetal flange` takes a `dimension`: the length measured from the inner face of the sheet (inside,
  the default) or from its outer face (outside).
- Surfaces: `se_surface ruled`, a band of a given width along each edge of a planar face, square to the face or tapered
  away from it or over it, out of the material or reversed.
- Sheet metal: `se_sheetmetal tab_slot`, tabs on edges of one sheet body and the slots for them in faces of another (a
  number of tabs or a spacing, tab length, end offsets, depth and slot clearance), and `tab` with `newBody` to start
  another sheet body.
- Sheet metal: `se_sheetmetal unbend`, the chosen bends or every bend of the sheet flat about a face that stays in place;
  deleting the unbend bends the sheet again.
- Drawings: `se_drawing hole_table`, the holes of a view with their X and Y from an origin (a corner or a hole centre;
  by default the lower left corner) and their size.
- Sheet metal: `se_sheetmetal gusset`, gussets across a bend, rounded or square: one at a distance from an end of the
  bend, several spread over it or several at a spacing.
- Sheet metal: `se_sheetmetal_form bead` makes circular beads (`beadType circular`) and punched or formed ends (`beadEnd`,
  `punchWidth`).
- Sheet metal: `se_sheetmetal_form normal_cutout` takes an `extent`: through all, through next, finite (`depth`) or from
  the sketch plane to a plane or face (`to`).
- Assemblies: `se_assembly_pattern curve`, occurrences repeated along a line, arc or spline of an assembly layout, from
  either end, at a fixed spacing measured along the curve.
- Parts: `se_part_feature label_weld`, edges marked as welded without a bead, with an optional weld symbol (type, side,
  size note, all around, field weld).
- Drawings: `se_drawing dimension` kind `symmetric_diameter`, twice the distance from a centre line to a silhouette edge
  parallel to it (the diameter of a shaft seen from the side).
- Sketches: `se_sketch tear_off`, a linked copy of a part sketch's elements at the same 2D coordinates on another plane.

### Changed

- The generic COM layer refuses `Blends.Add`, which leaves Solid Edge unstable with the faces in a nested array,
  `CloseCorners.Add`, which does so in synchronous modeling and leaves the corner failed in ordered modeling, and
  `AssemblyFeaturesSweptProtrusions.Add`, which does so with the origins as an object array of numbers.

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
