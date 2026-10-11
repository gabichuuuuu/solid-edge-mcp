# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
- License: the project is released under the MIT License ([LICENSE](LICENSE)).

### Changed

- Code comments, scripts and documentation are in English.

### Fixed

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
  ([docs/COBERTURA.md](docs/COBERTURA.md)).
- End-to-end runs with Claude Code and Codex are summarized in the README.
- README rewritten, this changelog, and issue and pull request templates.
