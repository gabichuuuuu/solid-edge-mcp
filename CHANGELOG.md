# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `se_frame`: structural frames and pipes in an assembly, along a polyline or assembly sketch lines. Sections, pipes and
  fittings come from the libraries installed with Solid Edge (`library` lists them; they are copied into the work folder
  and used from there) or from your own parts; frames are mitred or butted at the corners. Each frame reports its cut
  length and its members with their extent; each pipe its segments with their cut lengths and its fittings. Solid Edge
  stops giving the assembly features of an assembly that gets a frame before any assembly feature (and welds on the
  members fail): `se_frame` prevents it by making and deleting a 1 mm³ assembly protrusion first, and leaves the Frame
  environment the frame opens. A frame or pipe that fails also removes the line segments made for its path.
- `se_assembly_feature` `pattern` (rectangular patterns of assembly cutouts and holes along the sketch axes of a plane),
  `protrusion` and `revolved_protrusion` (assembly bodies, checked by their volume), `fillet_weld` (equal or unequal
  legs, checked by the bead) and `label_weld` in weldment documents. A weld that Solid Edge rejects leaves nothing behind.
- `se_part_feature` `split` (a reference plane cuts the body into design bodies) and `unite` (joins them again), checked
  by the number and volume of the bodies.
- Solid Edge dialogs: a call that a dialog waiting for an answer blocks (e.g. a first-use tip shown when a drawing opens)
  fails after 10 s with `dialog_open`, naming the dialog, its message and its buttons, instead of waiting for the time limit;
  timeouts and `solid_edge_busy` name the open dialog too, and `se_session status` lists it. The server never presses a
  dialog button.
- `se_pattern curve`: copies features along an open edge, with a count and a spacing measured along it, towards the end
  of the edge or its start (`reverse`).
- `se_part_feature replace_faces`: moves faces onto a reference plane, a construction surface or a face of one, checked
  by the volume change.
- `se_sheetmetal gauge`: applies a sheet metal gauge of a material library (thickness, bend radius and relief, e.g.
  `16 gage`); `settings` shows the current gauge.
- `se_assembly_feature mirror`: mirrors of assembly cutouts about an assembly plane, checked by their status and faces (holes
  are refused: Solid Edge reports a mirrored hole fine even outside the parts).
- Checked through the COM layer: live sections, suppressed relationships and live rules of family members, editing the
  graphics of drawing blocks. Simulation set-up works but does not solve with the Community Edition licence, and material
  libraries can only be edited in the installation folder (documented in `docs/COVERAGE.md`).
- Checked through the COM layer: sweeps along two paths and harnesses. Every function listed in `docs/COVERAGE.md` is now
  reviewed: each one works through a tool or the COM layer, or its row says why it does not.
- Checked through the COM layer: 3D sketch lines, PMI model views, family of parts options, the list of material libraries,
  drawing block views and labels, and converting the body to a mesh (A07, A08, A19).
- Coverage progress: `scripts/update-progress.ps1` writes a per-area summary at the top of
  `docs/COVERAGE.md` and the "functions reviewed" badge of the README (`-Check` only verifies).
- Part display configurations (`PartConfigurations`) checked through the COM layer (A19).
- `se_assembly_feature` `revolve` (revolved cutouts about a sketch axis, by an angle) and `hole`
  (simple or countersink holes at centers on an assembly plane, through all or to a depth).
- `se_curve` `contour` (a curve on one face through points on it, open or closed) and `wrap` (a
  sketch wrapped onto cylindrical or conical faces, checked by its length).
- `se_surface` `loft`, `sweep` (along a sketch or a curve), `stitch` and `bounded` (fills a closed
  chain of edges).
- `se_part_family`: family of parts (family variables, members with values and suppressed
  features, apply a member to the master with a check, member files, copy, rename and delete).
- `se_drawing` blocks: create a block from 2D elements of a sheet, place it scaled and turned,
  import a saved drawing as a block, list and delete blocks.
- `se_assembly_config explode` and `exploded`: automatic exploded views (spread distance, by part
  or subassembly) and the exploded placement of each component next to its assembly placement.
- `se_curve`: construction curves through 3D points, where faces, surfaces and the body meet,
  sketches projected onto faces, crossings of two sketches and chains of edges; list and delete.
- `se_loft_sweep sweep` takes a curve from `se_curve` as `path` (sweeps along 3D paths).
- `se_surface`: construction surfaces extruded or revolved from a sketch (open or closed, with end
  caps) and offset from a face, checked by their faces; list and delete.
- `se_assembly_feature`: extruded cutouts made in the assembly through chosen parts, from a sketch
  drawn in the assembly (`se_sketch create` now works in assemblies, on their planes).
- `se_primitive corners`: boxes by two opposite corners or by three points.
- `se_part_feature slot counterbore`: counterbored slots (total width and depth).
- `se_ref_plane tangent`: planes tangent to any curved face at an edge end (`points`), and tangent
  to a cylinder at an angle about its axis from a plane through it (`angle`).
- `se_assembly_pattern duplicate`: occurrences copied onto other occurrences at the same relative
  position and orientation (e.g. the bolts of one flange onto the others).
- The generic layer (`se_com_*`) takes faces and edges read from an occurrence as their reference in
  the assembly.
- `se_part_feature copy_part`: copy of a saved part file, scaled or mirrored, as the base body or
  united with the body.
- `se_ref_plane`: planes normal to an edge at a fraction of its length (`ratio`), and coordinate
  systems along two straight edges with the origin at an end of either.
- `se_primitive angle`: boxes turned about the plane normal, kept centred on the given point.
- `se_drawing bend_table`: bend table of a sheet metal view, placed above the title block or at a
  position.
- `se_annotation weld`: weld symbols on a leader from an edge of a view (weld type, arrow side,
  other side or both, size, all around and field weld).
- `se_annotation feature_control_frame`: up to three more rows, each with its characteristic or
  sharing the first one in a composite frame.
- `se_sketch chamfer`: corner chamfer between two lines with one or two setbacks.
- `se_sheetmetal bend_angle` and `bend_radius`: change the angle or the inside radius of existing bends.
- `se_part_feature`: slots along an open path (rounded or square ends, through or to a depth),
  web networks and lips in shelled parts, and scaling of the body (uniform or per axis).
- `se_assembly_config`: display configurations of assemblies (list, save, apply and delete).
- `se_part_feature`: direct edits for imported or finished parts: delete faces (the solid
  heals), delete holes up to a diameter, delete rounds and offset faces along their normal.
- `se_sketch` and `se_sketch_constraint` draw and constrain 2D geometry on drawing sheets: pass
  a sheet handle or a drawing handle (its active sheet) as `sketch`.
- `se_layer`: layers of parts, sheet metal, assemblies and drawing sheets (list, create,
  activate, show or hide, selectable, delete empty ones and move elements between them).
  Acceptance script A16.
- `se_view`: named views (list, save the current view, apply and delete), checked by the
  direction of the camera.
- `se_primitive`: box, cylinder and sphere, added or cut, placed by their centre on a plane or a
  planar face. Built in synchronous modeling and checked by the volume. Acceptance script A15.
- `se_com_invoke`: `{"handle":null}` passes Nothing (a null object) to a VARIANT parameter.
- `se_annotation`: surface finish symbols, datum frames and feature control frames, attached to
  an edge of a view (directly or on a leader) or free on the sheet.
- `se_ref_plane`: planes through three points, midway, normal to an edge and tangent to a cylinder,
  checked against their geometry; coordinate systems with their origin and axes.
- `se_assembly_pattern`: component patterns that follow a part pattern (list, create, delete and
  dissolve). Acceptance script A14.
- `se_appearance`: colours of parts and assembly components through face styles (list, create,
  delete, apply by style or #RRGGBB with opacity, read back and remove). Acceptance script A13.
- `se_part_feature`: ribs from an open sketch, thicken faces and mirror the whole body. Threads
  on cylinders are not offered: `Threads.Add` is rejected by Solid Edge 2026 (see the coverage matrix).
- `se_annotation`: text, balloons, callouts, centre marks, centre lines and leaders on drawings,
  attached to the views and checked after they are created.
- `se_relations`: parallel (axes at a distance), tangent, angle and rigid set relations.
- `se_variables`: rename, comment, expose and limit variables.
- `se_options`: read and change Solid Edge options and document units.
- `se_ui`: windows, environments and ribbon tabs, groups and commands.
- `se_sketch info` and `se_sketch_constraint list`: elements of a sketch with their keypoints, and
  its relations with their elements.
- Acceptance scripts A11 (options and units) and A12 (user interface); A06 and A07 cover the new
  relations and annotations.
- License: GNU AGPL-3.0-only ([LICENSE](LICENSE)), with an additional permission to combine it
  with Solid Edge and a separate commercial license (see the README). The code first published on
  2026-10-11 was released under the MIT License and remains available under it.

### Changed

- `se_geometry` with an occurrence as `source` reads the body the assembly shows (`Occurrence.Body`): the faces left by
  assembly features and the real, cut-to-length frame members, which welds and relations can use. Before, it read the
  part file's body.
- Parts with several design bodies (after a split): `se_geometry summary`, `faces` and `edges` cover every body (with the
  volume and box of each one), and `se_physical` gives the mass properties of the whole part and the volume and mass of
  each body. Before, only the first body was measured.
- Tool descriptions stay within the 2,048 characters that Claude Code keeps (the descriptions of `se_drawing`,
  `se_sheetmetal`, `se_part_feature` and `se_sketch` were cut off): the details of each argument moved to the argument
  descriptions, and a test checks the limit for every tool and the server instructions.
- README redesigned: banner, highlights, a compact tool overview and collapsible sections for the
  full tool table, the clients other than Claude Code and the acceptance scripts.
- The internal development notes and AI assistant instructions are no longer published.
- Code comments, scripts and documentation are in English, including the project notes
  (`AGENTS.md`, `STATUS.md`, `PLAN.md`). `ESTADO.md` and `docs/COBERTURA.md` were renamed to
  `STATUS.md` and `docs/COVERAGE.md`. Test data are in English too, and the project notes no longer
  mention the local development environment.

### Fixed

- `se_geometry summary` on a body converted to a mesh fails with `no_brep_body` (it said only E_FAIL).
- `se_part_feature scale_body` on a part with several design bodies checks the volume of the scaled (first) body instead of
  failing, and `unite` always keeps the first model's body as the target, so the part stays measurable.
- `se_options set`: when Solid Edge rejects an application option while no document is open (display colors such as
  `ColorBackground` need a document window), the error says to open or create a document first.
- `se_assembly_feature`: deleting a feature, or removing one that failed, no longer deletes a base plane of the assembly
  (Solid Edge deletes the plane of the feature's sketch with it, so the sketch is now deleted first; later sketches on
  plane '1' landed on another plane). A side that fails is retried by turning the same feature to the other side, and a sketch
  already used by another feature is refused.
- After the body is converted to a mesh, `se_feature_tree` lists the features with a note instead of failing, and
  `se_geometry` answers `no_brep_body` with the reason (Solid Edge can no longer read the body).
- `se_assembly_feature`: the parts of a feature keep the occurrence handles the caller already has
  (they used to get new ones).
- `se_drawing projected_view` returns the drawing's `projection` angle and its description explains
  which side of the model a view placed beside another shows (first or third angle).
- `se_documents save_as` saves once more when Solid Edge leaves the document modified right after
  saving it.
- `se_export` notes when Solid Edge marks the document modified after exporting it (a saved drawing
  exported to PDF); `move_view` explains how projected views follow.
- `se_sketch`: the end of a spline was reported as its second point; it is now the last one, with
  the number of nodes.

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
  ([docs/COVERAGE.md](docs/COVERAGE.md)).
- End-to-end runs with Claude Code and Codex are summarized in the README.
- README rewritten, this changelog, and issue and pull request templates.
