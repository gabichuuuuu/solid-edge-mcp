# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A package that installs without compiling: `scripts/package.ps1` builds `solid-edge-mcp-<version>-win-x64.zip`
  (self-contained, it does not need .NET) and its SHA-256, without the Solid Edge interop. The interop derives from
  Siemens files, so the server now generates it from the installed Solid Edge on its first start, in a few seconds, and
  keeps it in `%LocalAppData%\SolidEdgeMcp\interop\<Solid Edge version>`; a build from source still has it beside the
  server. When it cannot be generated, the server starts anyway and its tools answer `interop_missing`.
- A Claude Code plugin and its marketplace (`plugin/`, `.claude-plugin/marketplace.json`):
  `claude plugin install solid-edge-mcp --marketplace gabichuuuuu/solid-edge-mcp`. On its first start the plugin
  downloads the package of its version from the GitHub release, checks it against the SHA-256 published with it,
  installs it in `%LocalAppData%\SolidEdgeMcp\server\<version>` and starts the server from there.

### Changed

- `se_assembly_config apply` of an explode configuration says that it shows in drawing views: applied to the assembly,
  its components do not read as exploded.
- `se_design_manager copy` takes the top document ('path') in 'files' too, as Design Manager does: a drawing or an
  assembly is copied with its files, the copy uses the copies and the original is left as it was. The documents between
  the top and a copied file must be copied as well (the refusal lists them), or the original would use the copies.
- `se_documents open` of a STEP, IGES, Parasolid, DXF or other file of another CAD system says to use `import`: Solid Edge
  failed with an unspecified error (E_FAIL).
- An internal fault of Solid Edge (`RPC_E_SERVERFAULT`) is reported as such, with `se_session restart` as the next
  step, and other Solid Edge errors mention the restart when calls that worked earlier start failing.
- `se_app recent` says that Solid Edge may add the files saved with `save_as` while the list is suspended: it does not
  always.
- The server instructions point to the work on closed files (`se_design_manager`, `se_properties` with `path`,
  `se_export` with `files` or `folders`).

### Fixed

- `se_properties set` takes a date for a date property (ISO 8601, `2026-11-20` or `2026-11-20T10:30`): a text was
  refused with a type mismatch, or written and then read back shifted. In a closed file the date is now read back as
  given (the file property library stores it as UTC and Solid Edge reads it in local time), and so is the status date
  written by `se_design_manager status`, which was off by the UTC offset.
- `se_properties set` in an open document refuses the dates Solid Edge keeps in SummaryInformation (Total Editing Time,
  Last Print Date, Origination Date, Last Save Date): written there, Last Print Date disappeared from the document and
  saving it failed. They can be written to the closed file with `path`.

## [0.9.0] - 2026-10-16

The programs installed with Solid Edge, as far as their automation allows. Design Manager works on closed files through
the new `se_design_manager` tool: the links of a document at every level, its actions (rename, copy, replace, move,
revise, release) on the files of a set with every document that uses them updated, document status, redefined links,
broken links and duplicates. Closed files also give their properties and thumbnails; `se_export` writes batches of
saved files and folders as Translation Services does, in more formats; and `se_server_info` reports the installation.
Every program of the Start menu is named in the coverage map, with the reason and an alternative for those that only
work through their own windows or need a server: 364 of the 501 functions work through the 86 tools (72 %). The
acceptance scripts A74, A131 and A132 pass, and an end-to-end session from a request in plain language used Design
Manager, the batches and the thumbnails on the files it had modelled.

### Added

- `se_bom` with `path` writes the report to a .csv or .xlsx file of an allowed folder (`savedTo`): every line with a
  header row, also beyond the lines the result lists; `overwrite` replaces a file that exists.
- `se_export` with `table` writes the shown cells of a table of a drawing (a parts list, a hole, bend, tolerance, family
  or block table, or a table of `se_sheet_content`) to a .csv or .xlsx file with its header row. Through the API, Solid
  Edge only copies a parts list to the clipboard.
- `se_assembly_config explode` takes `orientation`: the window is turned to it before exploding, because the explode
  configuration saved with the assembly keeps the orientation the window had, and a drawing view of it shows that one.
- `se_design_manager`: Design Manager on closed files, started hidden for each call and closed after it. `links` lists
  what a document refers to at every level (whether each file exists, its status, revision and document number);
  `rename`, `copy`, `replace`, `move`, `revise` and `release` run Design Manager's actions on files of a set and update
  every document of the set, and for rename and move the documents outside the set, in the folders given, that use the
  files; `status` gives closed files a document status, forward only as Solid Edge's workflow does; `relink` redefines
  links (one file or a whole folder pointed at another) in one document or in every document of some folders;
  `broken_links` and `duplicates` (the same document ID or the same name) look through folders. Every change is checked
  by reading the files and their links back. Refused before Design Manager acts: documents open in Solid Edge, documents
  it would rewrite outside the allowed folders, released documents whose links would change and a new name of another
  type; a library the set only reads may be read-only. A Design Manager step that stops answering is ended.
- `se_properties` with `path` lists, sets and deletes the properties of a closed file without opening it in Solid Edge.
- `se_export` with `files`, `format` and `subfolders`: batches of saved files or whole folders written to one folder, as
  Solid Edge Translation Services does. A file already open is exported from its document, which stays open; the others
  are opened and closed without saving; each file reports its result or error, and a file of a kind the format does not
  take is reported without opening it. New formats: CATIA V5 (.catpart) and CATIA
  V4 (.model) of parts and sheet metal, OBJ, PLM XML, U3D and XGL; the files a translator writes beside the target (OBJ
  materials, PLM XML bodies, logs) are listed in `alsoWritten`.
- `se_documents import` opens CATIA V5 parts (.catpart) and CATIA V4 files (.model).
- `se_documents thumbnail`: the picture saved in a closed Solid Edge file, as Windows Explorer shows it, without opening
  the file.
- `se_server_info` reports the Solid Edge installation: version, program and preferences folders, language and Parasolid
  version, without starting Solid Edge.

### Changed

- `se_drawing base_view` of an explode configuration asked in another orientation than the one stored with it says in
  its `note` which orientation the view keeps and how to change it.
- The coverage map has a section for the programs installed with Solid Edge (Design Manager, Translation Services,
  thumbnails, installation data and the programs that only work through their own windows or need a server), and the
  ribbon commands that start programs not installed (KeyShot, CAM Pro, Femap, Technical Publications, Standard Parts) are
  marked as such.

### Fixed

- A component that Solid Edge needs for an operation and that is not registered (such as a CATIA translator for
  assemblies) was reported as "Solid Edge is not registered on this computer"; it is now `component_not_registered`.

## [0.8.0] - 2026-10-15

The base program complete. Every command on the ribbons of the part, sheet metal, assembly and drawing environments of
Solid Edge (731 commands) is named in the coverage map, and those the API allows are available: 351 of the 481
functions work through the 85 tools (72 %; the map grew from 343 functions in 0.7.0 as every ribbon command was
added). Each of the other 130 is documented with the reason and an alternative when there is one: 63 have no route
through the API of Solid Edge 2026, 10 leave it unstable and are refused by the generic layer, 13 need another edition
or installation, 35 are interactive commands and 9 belong to separate programs. The acceptance scripts added since 0.7.0
(A90 to A130) pass, and end-to-end sessions from requests in plain language found the defects fixed below.

### Added

- `se_material apply` on a weldment gives its weld beads the material and its library density (Bead Material), so the
  weldment has a mass; `current` reads it back. `se_physical` on a weldment whose beads have no material says so
  (`weldBeadsWithoutDensity`) instead of listing them by a number among the parts without density.
- `se_options format`: a value in mm, degrees or kg as the document displays it (60 mm reads "2.36 in" in a document
  in inches), or a typed text read back in tool units ("2.5 in" is 63.5 mm; a number alone is in the document's unit).
- Inspection: `se_measure distance` takes `type` maximum (the farthest points of two elements, also in assemblies),
  `normal_distance` measures from a reference plane or planar face to an end of an edge (with its x, y, z components)
  and `wall_thickness` runs Solid Edge's wall thickness analysis of a body: thinnest and thickest wall and where, and
  the area of the walls thinner than a `threshold`.
- Synchronous modelling: `se_feature_edit move_to_synchronous` moves ordered features (e.g. the body of an imported
  part) and what depends on them to synchronous; the new tool `se_recognize` turns the holes, patterns and chamfers of
  synchronous geometry without history into hole, pattern and chamfer features; `se_part_feature radiate` grows or
  shrinks the radius of round faces (optionally with the coaxial ones). Both refuse what Solid Edge would answer with a
  modal dialog that blocks it (ordered features, planar faces, faces of hole features).
- Parts: `se_pattern table`, copies of features at given offsets (pattern by table; the server writes the Excel sheet
  Solid Edge reads, so no Excel is needed); `se_sheetmetal_form etch` also works in a part.
- Sketches: `se_sketch polygon`, a regular polygon by its centre, the distance to its vertices and its number of sides.
- 3D sketches: `se_sketch3d` arcs through three points and tangent to a line (`arc_through`, `tangent_arc`),
  `rectangle`, `mirror` about a plane, `split` and `trim`, curves driven by equations of t (`equation`) and model edges
  copied into the sketch (`include`).
- PMI: `se_pmi copy` copies the dimensions of a feature's sketch to PMI dimensions (Copy to PMI).
- Drawings: `se_drawing_view centerlines` adds the automatic centre lines and centre marks of a view
  (`remove_centerlines` takes them away) and `activation` activates or inactivates the drawing views;
  `se_sheet_content watermark` adds a watermark and `line_up` lines texts up with another; `se_sketch_edit order`
  sets the drawing order of elements of a sheet, and `group` and `ungroup` group them.
- Variables: `se_variables goal_seek` (Goal Seek: Solid Edge varies one variable until another reaches a value; the
  variable is put back when the value is not reached) and `adjustable` (the variables an assembly may adjust in an
  adjustable part, with notes).
- Parts: `se_documents save_mirror` saves a part as its mirror image about a plane (Save As Mirrored Part), linked or
  not; `se_body heal` heals and optimizes the bodies and reports the faces and edges by type before and after, with
  Solid Edge's report written to a file; `se_body emboss` also works in sheet metal.
- Sketches: `se_sketch_constraint autoscale` scales a whole sketch by its only driving dimension (Auto-Scale Sketch).
- Sheet metal: `se_sheetmetal bend_table` lists the bends of the model in their sequence (feature, radius, angle,
  direction, calculation method) and moves a bend to another place of the sequence.
- Assemblies: `se_assembly_pattern clone` repeats occurrences on the faces of other occurrences that match the faces
  they sit on (Clone Component); `se_assembly insert` takes `create` (a new empty part, sheet metal part or subassembly
  from a template) and `adjustable` (an adjustable part), `replace` takes `source` = copy (a copy of the occurrence's
  file) or new (an empty part), and the new actions `adjustable` (make an occurrence adjustable) and `save_model` (Save
  Model As: an occurrence's part saved as a part file of its own in its assembly position).
- Drawings: `se_dimension tracker` lists the dimensions a drawing update changed or detached (Track Dimension Changes)
  and empties the list; `alignment_set` reads and sets Maintain Alignment Set.
- Assemblies: the new tool `se_internal_component` creates internal components (empty parts, sheet metal parts and
  subassemblies kept inside the assembly file, grounded or free), lists and deletes them and publishes them as files
  of a folder; `se_assembly_family` lists, adds and removes the alternate components of an occurrence;
  `se_assembly_config performance` reads and sets the Large Assembly Performance mode.
- Links: `se_links missing` lists the component files an assembly cannot find and looks for them again once they are
  back; `cad_direct` places a STEP or other CAD file as a component linked to it and `cad_update` reads the file again,
  each with the box of the translated part; `list` marks internal components (`internal`) instead of reporting them as
  missing.
- Simplified models: `se_simplify visible_faces` simplifies an assembly to the faces or whole bodies seen from outside
  (Simplify > Visible Faces), `update` makes the simplified assembly again from its components, and `save_as` writes
  the simplified model of a part as a Parasolid file (.x_t or .x_b).
- Sketches: the new tool `se_sketch_tools` cleans a sketch or a drawing sheet of duplicate, overlapping and small
  elements (Clean Sketch), turns elements into B-spline curves (Convert to Curve), moves the local origin of a sketch
  or puts it back (Reposition and Zero Origin), places a picture (Image), makes section sketches of the design bodies
  (Section Sketches), measures the total length of curves (Total Length) and copies an assembly layout into the part
  of an occurrence (Copy Sketch).
- Parts: `se_body publish` writes chosen design bodies of a saved part as part files of their own, optionally gathered
  in a new assembly (Multi-Body Publish); `se_export` takes `bodies` to write one design body, or all of them, as
  Parasolid (Save Selected Model).
- PMI: `se_pmi size` sets the PMI text size (fixed pixels or with the model), `plane` locks the plane PMI is placed on
  and `axis` sets its axis; `se_model_view update` gives a model view the window's orientation.
- PMI: `se_pmi dimension` places new PMI dimensions (Smart Dimension, Distance Between, Angle Between): linear between
  two vertices or along a straight edge, diameter and radius of a circular edge and the angle between two straight
  edges, each picked by model points and read in a reference plane, `distance` standing the dimension line off along
  the plane's normal; the value is checked against the geometry and a wrong dimension removed.
- PMI: new tool `se_pmi_annotation` for the annotations of the PMI tab, attached to edges of the model picked by model
  points and drawn in a reference plane (or the locked PMI plane): text, balloon, callout, leader, datum frame, datum
  target, datum point, surface texture symbol, feature control frame (lower and composite rows), weld and GOST weld
  symbols, edge condition, centre mark, centre line and bolt hole circle; each is checked (attached, content and leader
  end read back) and removed if wrong; list and delete them.
- Coverage: rows for the PMI annotations and new PMI dimensions, and, as not in the API, PMI coordinate, angular
  coordinate and symmetric diameter dimensions and Auto Dimension.
- PMI: `se_pmi_annotation move` and `se_pmi move` (Move, PMI tab): the leader end of an annotation, or a text, goes to a
  new position projected onto its plane (a datum frame's leader stays square to its edge), and a dimension's line to a
  new stand-off, keeping its value; each move is read back and undone if wrong.
- Bodies: `se_body heal` takes `fullBody` for Full body optimization (Optimize); the default stays Simplify body only.
- Coverage: rows for the Coincident Plane (`se_ref_plane offset` at 0) and, as not in the API, Flatten in a part
  document; Draw (shapes pressed and dragged) joins the interactive FreeSketch row.
- Drawings: `se_drawing cutting_plane` draws the cutting line of a section, with its caption and side, without its view
  (Cutting Plane); `section_view` makes the view of a cutting plane, `views` lists the cutting planes and `delete_view`
  deletes one with its section view.
- Options: `se_options` scope drawing also reads and sets the drawing's sketching buttons: Maintain Relationships,
  Alignment Indicator, Relationship Handles, Locate Background and Maintain Alignment Set.
- Views: `se_view orient` takes `plane` (a sketch, a reference plane or a planar face) to look square at it (Sketch
  View).
- Coverage: every ribbon command of the base environments is now named: rows for Open Draft, the Physical Properties
  Manager, view clipping planes, Show Non-Stitched Edges and, in part sketches, Maintain Relationships (not reachable),
  and the remaining names added to their rows.
- Assemblies: `se_assembly properties` and `set_property` read and set the occurrence properties (name, quantity,
  bill of materials, physical properties, interference analysis, reference, display) and Show/Hide Component;
  `se_physical` on an assembly also gives the table of its parts with material, density, volume, mass and occurrences
  (Physical Properties Manager).
- Geometry: `se_geometry edges` with type open lists the free edges of a surface (Show Non-Stitched Edges).
- Assemblies: `se_assembly reorder` moves a component before or after another (PathFinder order); `show` and `hide`
  with `all` show or hide every component, or show one alone (Show Only).
- Assemblies: `se_interference compare` checks occurrences against every other occurrence (others) or against the
  visible ones (visible).
- Drawings: `se_drawing rotate_view` turns a view on the sheet to an angle and `crop_view` crops it to a rectangle of
  the sheet or shows it whole again (View Properties).
- Views: `se_view capture` warns when the image comes out a single colour (nothing drawn) and names a locked Windows
  session, which leaves drawings blank, as the likely cause.
- Drawings: `se_dimension arrange` spaces dimensions of a sheet, or PMI dimensions of a model, out from the outline they
  measure (Arrange Dimensions); `se_drawing unblock` and `unblock_all` turn block occurrences back into 2D elements;
  `se_sheet_content connector` draws line, jump, corner, step and gap connectors, free or tied to two elements, which
  `list` and `delete` also handle; `se_drawing parts_list` takes `family` or `members` for a family of assemblies parts
  list (a quantity column per member).
- Assemblies: `se_bom` takes `indented` (bill of materials, each level under its subassembly), `exploded` (every
  occurrence on its own line) and `statistics` (parts and subassemblies in total and unique, levels and unique files),
  the reports of the Reports and Assembly Statistics dialogs.
- Planes: `se_ref_plane angle` takes a straight edge lying on the plane or face as `pivot` (Perpendicular at 90 degrees,
  Coincident by Axis at 0), with `at` choosing the end the plane's x axis starts from, also on an occurrence in an
  assembly; planes report their `xAxis`.
- Sketches: `se_sketch_edit offset` takes `symmetric` (a copy on each side: Symmetric Offset) and `cap` (arc, line or
  none) to close the ends of an open chain.
- Drawings: `se_drawing_view hide_edges` and `show_edges` hide or show single edges of a view, each picked by a model
  point, and `clear_edges` removes those overrides (Hide Edges, Show Edges).
- Coverage: rows for the Update group (recompute and the Automatic Update option) and, as not in the API, Edge
  Painter, Frame Origin, Change Coordinate Origin of PMI dimensions and Split of construction surfaces.
- Coverage: rows for the thin region and the equal, symmetric and aligned-holes face relations, which Solid Edge 2026
  offers only as interactive commands, and for the revision table, hyperlinks and copy attributes (interactive); for the
  separate programs the ribbons start (simulation and
  generative design, KeyShot, CAM Pro and 2D Nesting, Femap, Illustrations, Design Configurator, Electrical Routing,
  Engineering Reference, 3Dfindit.com), the commands that need a server or add-on (managed libraries, requirements,
  Solid Edge Inspector) and the interactive aids (IntelliSketch, FreeSketch, Edge Locate, FlashFit and the assembly
  assistants, Create 3D, Automatic Drawings, 3D Print, mesh editing, the 3D PDF template editor, the assistant panes);
  and for what the API does not offer: the Geometry Inspector, Design for Cost, Thin Part to Synchronous Sheet Metal,
  the values of an adjustable occurrence and annotation alignment shapes, each with the route that gives the result.
- `se_assembly_config explode` and `save` say how to keep an exploded view: saving the assembly stores it as the explode
  configuration 'explode,Solid Edge', which `se_drawing base_view` shows exploded; a display configuration does not keep it.
- `se_variables list` takes `kind` physical: the physical property variables of a part (Mass, Volume, Surface_Area,
  centres and moments of inertia), with their current values.
- `se_documents modeling_mode` reads a part's modeling mode or switches it between synchronous and ordered.

### Changed

- A model point that is on no edge of a drawing view (`se_drawing dimension`, annotations, `se_drawing_view` edges) now
  gets the nearest edge of the view in model coordinates in its error. A flat pattern view shows the flat pattern,
  whose coordinates are not those of the folded part.
- `se_dimension`: `dimension` is required only by get, tolerance and text.
- `se_geometry` with an occurrence kept inside the assembly file (an internal or CAD Direct component) gives
  `not_supported` with the next step instead of an access error.
- `se_relations edit` changes the offset of a relation (mm) or the angle of an angle relation (degrees), or suppresses
  and restores it, as editing its value in Solid Edge: the parts follow, a gear pair included, and the occurrences that
  moved are returned. A value the relations cannot solve is put back.
- `se_relations` takes the assembly base planes by name (`xy`, `yz`, `xz`) wherever it takes `1`-`3`: the elements, the
  measurement plane of an angle and the planes of a center plane. With the occurrence given, `xy`, `yz`, `xz` or an
  index name a base plane of that occurrence's part.
- `se_ref_plane list` with an occurrence handle lists its part's planes in assembly coordinates, with handles for
  `se_relations` and `se_measure`.
- A synchronous part stays synchronous: `se_round` (constant radius), `se_chamfer` and `se_part_feature` offset_faces,
  delete_faces and delete_rounds are made in synchronous modeling there (the last three change the body without a
  feature) instead of switching the part to ordered. Variable rounds, resize_holes and resize_rounds stay ordered.
- `se_feature_edit move_to_synchronous` saves a copy of the part first, which clears Solid Edge's undo list.

### Fixed

- A drawing view of an open model with unsaved changes no longer loses them: Solid Edge shows them in the view but clears
  the model's changed mark, so closing the model with `saveChanges` wrote nothing. The mark is kept, also when the view
  fails, and the result says the file does not have the changes yet (it said the drawing showed the saved file).
- `se_drawing hole_table` without chosen holes lists each hole of the part once, by its narrowest circle: the outline of
  a round part, a boss and the edge of a chamfer or a round were listed as holes too, a counterbore gave two rows and a
  tapered hole none. A view out of date with its model lists every circle it shows and says so.
- A drawing view that does not fit its sheet says so also when its result has another note.
- `se_sheet_content delete` deletes the tables made by `se_drawing` (parts lists, hole, bend, tolerance, family and block
  tables), as `se_drawing tables` lists them.
- `se_sheetmetal contour_flange` now unfolds with the document's bend calculation (neutral factor, bend deduction or
  bend allowance), as flanges do: its bends unfolded with a neutral factor of 0, so a U 100 x 40 x 40 flattened to
  170.28 mm instead of 172.36. Its result notes that the sketch dimensions no longer drive it.
- Features built from a sketch with construction elements (a centerline, a reference line) no longer fail: Solid
  Edge's copy of the sketch puts them in a second profile, which is now removed before the feature is made.
- A section view that Solid Edge refuses no longer leaves its empty cutting plane on the drawing.
- `se_interference` no longer fails when an occurrence is excluded from interference analysis: it is left out, or
  refused when named.
- `se_view set_camera` (and Sketch View) on a new window, whose camera reads zeros until it is first fitted, no longer
  fails.
- `se_documents close` no longer leaves the hidden window of a closed part with features or drawing with views behind
  in Solid Edge.
- `se_measure wall_thickness` now measures the walls square to the Z axis: Solid Edge's analysis left them out, so the
  5 mm floor of an open box read 10 mm. It also gives the thinnest uniform wall (`minUniformThickness`) and says when
  the thinnest reading is at an edge: on the open rim of a shelled box with draft the analysis read 0.086 mm for a 2 mm
  wall.
- `se_variables goal_seek` now reaches a mass, volume or surface area of a part (`Mass`, `Volume`, `Surface_Area`):
  Solid Edge's Goal Seek fails on them, so the server changes the variable and measures the model at each step.
- `se_variables get` of a physical property variable (`Volume`, `Mass`...) returns the current value: Solid Edge kept the
  value of the last stored computation.
- A material applied before the part had a solid is now used for the mass (`se_physical`, its parts table, `se_material`
  and goal seek): Solid Edge stores no density then until the part is saved, and they reported none.
- `se_part_feature thicken` works in a part that has only surfaces: the thickened surface becomes its first solid
  (it answered `no_model`).
- `se_feature_edit move_to_synchronous` no longer breaks the body of a part whose synchronous body was edited (a face
  relation, a recognised hole resized): Solid Edge failed the move and left the body unreadable with nothing to undo
  unless the part had been saved, so a copy is saved first; if the body still breaks, the error names that copy. The
  volume change is measured in ordered mode (in synchronous mode the body leaves the ordered features out).
- `se_com_set`, `se_com_invoke` and the other generic calls accept an enumeration name sent with its quotes
  (`"\"seModelingModeSynchronous\""`), and a boolean or a list given for an enumeration is refused as
  `invalid_argument` instead of an internal error.
- `se_drawing dimension` works in a detail view: it answered `nothing_to_dimension`, because a detail view that
  follows its source view has no edges of its own; it now dimensions the source view's edges inside the detail circle.
  A `diameter` dimension where the view shows no circle (a hole in a section) names `symmetric_diameter` in its error.
- `se_pmi dimension` linear between vertices of a loft no longer fails with `dimension_failed`: Solid Edge measured to
  another end of the B-spline edge picked for a vertex; every edge at each vertex is now tried until the value matches.
- `se_material apply` on a plain assembly is refused with `wrong_document_type` (an assembly has no material of its
  own) instead of an internal error.
- STL files are written and read in mm. Solid Edge uses the unit of its STL translator options, inches as installed,
  so an exported part came out 25.4 times too small for a slicer and an STL in mm was imported 25.4 times too large.
  `se_export` now scales the file to mm and returns its triangles and box; `se_documents import` reads it in mm.
- `se_component_query` find, zone and run work on an assembly that is not the active document: Solid Edge failed with
  0x80040225, so the assembly is activated for the query and the document that was active is activated again after it.
- `se_documents close` with `saveChanges` true on a document with nothing to save says so in a `note` (`saved` is
  false because nothing was written).
- `se_documents recompute` updates an assembly (its Update) instead of answering `not_supported`; on a drawing the error
  points to `se_drawing update`.
- `se_relations add` connect onto an assembly plane is refused before anything moves, with the alternatives: Solid Edge
  rejects it (E_NOINTERFACE).
- `se_assembly rotate` of a gear about its axis turns the gears related to it, as dragging it does in Solid Edge: a
  placement written through the API does not drive gear relations, so the turn goes through a temporary angle relation
  (deleted after) and the parts it drove are returned. When that is not possible (an axis not along an assembly base
  axis), the result says the gears were not driven and how to drive them.

## [0.7.0] - 2026-10-13

The base program at its maximum. The functions of the coverage map that failed through the API were tried again by
other routes (typed calls, other argument values and constants, collections, the selection), and new actions cover
more of Solid Edge: 274 of the 343 functions work through the 81 tools (79 %, from 261 of 339 in 0.6.0). Each of the
other 69 is documented with the reason and an alternative when there is one: 40 have no route through the API of
Solid Edge 2026, 9 leave it unstable and are refused by the generic layer, and 20 need another edition or
installation, an interactive command or a separate program. The acceptance scripts added since the last full run
(A54 to A89, 35 scripts: 966 calls, 410 checks) and an end-to-end session from a request in plain language pass with this version.

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
