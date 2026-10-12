# Changelog

Relevant changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Solid Edge as a program, not only modelling and drawing:
  - `se_app`: version, edition, language, operating system and folders; the Add-In Manager (`set_addin` loads or
    unloads an add-in found by index, ProgID, GUID or description); the default template of each document type
    (`set_template`, checked by creating a document from it); the recent files list (read, suspend, resume, clear).
  - `se_customize`: ribbon themes (create from the active or another theme, activate, rename, button size and labels,
    remove), tabs, groups and command buttons of a theme (add, show, hide, rename, remove), and keyboard shortcuts of
    each environment (`'Ctrl+Shift+K'`, `'Alt+F5'`…; keys in use are refused unless `replace` is set). The Solid Edge
    default theme, read from its settings, is never edited.
  - `se_options`: `categories` and `list` by page of the Options dialog (21 pages, every option of the API);
    `snapshot` saves every option to a .json file and `restore` puts back the ones that differ (`dryRun` lists them);
    coded options take their name (`ColorTheme`: `light`, `medium`, `dark`, `framed`); the answer explains the value.
  - `se_ui display` and `set_display`: colour theme, status bar, edge bar, start screen, recent files shown, language,
    and position and size of the main window.
  - `se_command check`: whether this installation knows a command, its licence includes it and it can run now.
  - `se_styles`: styles of a document (dimension, text, character, line, fill, hatch, dash, drawing view, table and
    frame styles of drawings; view and face styles of models). `types`, `list`, `get` every property, `set` one (read
    back, with the previous value; colours as `#RRGGBB`, enumerations by name or by a unique ending such as `bold`),
    `add` from another style, `activate`, `remove` (the active one is refused) and `import` from another drawing.
  - `se_view`: `style` (the view style of a model window), `background` (solid, gradient or an image of the
    installation or an allowed folder; returns the previous one to put it back), `camera`, `set_camera` (eye, target,
    up, perspective and field of view), `zoom` and `rotate`; `capture` with `samples` returns the colours at the
    corners, edges and centre of the image.
  - `se_options` scope `drawing`: the preferences a drawing saves (projection angle, drawing view defaults, grid, the
    line style of each kind of edge, `DVEdgeStyleName[HiddenEdge]`…); style names are checked against the drawing.
- Acceptance scripts `A21` (program settings), which puts back every setting it changes, and `A22` (styles, drawing
  preferences, view style, background and camera). The acceptance runner checks colours with a tolerance (`?#`).
- `se_sketch_edit`: edits the 2D elements of a sketch or a drawing sheet, as the sketch editing commands do. `move`,
  `rotate`, `scale` and `mirror` in place or as copies (`copy`); elements tied by relations move as a set and keep the
  relations among them (a quarter turn makes horizontal lines vertical), and copies stay connected like the originals;
  an element held by relations to elements left out (or by a driving dimension it would change) is refused with
  `held_by_relations`. `trim`, `extend`, `split`, `corner`, `offset` of a connected chain, `construction`,
  `pattern_rectangular`, `pattern_circular` and `delete`. Each result is measured and a failed edit is put back.
- `se_drawing_view`: the display of a drawing view as in its Properties dialog (`display`, `set_display`: view
  properties and the properties of each model member of an assembly view, such as hidden and tangent edges, hiding a
  part or showing it as reference, checked by the edges drawn before and after), break lines (`add_break`, `breaks`,
  `set_broken`, `remove_break`) and conversion of a view to 2D (`to2d`).
- `se_documents import`: opens a STEP, IGES, Parasolid, JT, SAT, STL, OBJ, 3MF, DXF or DWG file as a new, unsaved
  document made from a template (default part, or draft for DXF and DWG); returns the volume or the 2D elements, and
  closes the document when nothing was translated (`import_empty`).
- Acceptance scripts `A23` (view display, break lines, conversion to 2D and import) and `A24` (sketch editing).
- `se_assembly`: `activate` and `inactivate` components (in a saved assembly; `all` activates every one), `bundle` (a new
  subassembly saved from the chosen occurrences, inserted at the origin and holding them at the same positions),
  `transfer` (occurrences into an existing subassembly) and `disperse` (the components of a subassembly up into the
  assembly; `all` = every occurrence of it). `list` tells whether each occurrence is active.
- `se_links`: the links between files, as Info > Links, Replace and Where Used do. `list` (component files of an
  assembly with their quantities, models of a drawing with their views and whether they are up to date, copied parts of
  a part; missing files flagged), `replace` a referenced file (every occurrence of it, the model of a drawing with its
  views updated, the source of copied parts; read back), `break` copied parts, `where_used` (directly or up to the
  top), `top_level` and `search` (Solid Edge documents of a folder with title, subject and date; text filter).
- `se_physical` measures assemblies: volume and centre of volume of all their parts, and mass, centre of gravity and
  inertia when every part has a density (otherwise `partsWithoutDensity` lists the parts to give a material).
- Acceptance script `A25` (assembly structure, links, where used, document search, physical properties of assemblies
  and attribute sets through the generic layer). The acceptance runner gives each run an id (`{{run}}`) for files that
  must not exist yet.
- `se_region`: closed regions of drawing sheets and sketches, as the Fill and Area commands do. `fill` round a point
  among the elements (a line across a region splits it and the point chooses the side; the point may be omitted when
  the elements form one closed loop) or by the corners of a polygon, with fill style, colour, angle and spacing;
  `fills`, `style` and `delete`. `area`: area, perimeter, centroid, second moments about the centroid, principal
  moments and angle, radii of gyration and elastic section moduli, minus `holes`. The point is proved to be inside a
  closed loop of the elements before Solid Edge looks for the region (`point_outside`, `no_closed_region`): a point
  outside every region brings Solid Edge 2026 down. A closed loop inside the region (an island), which Solid Edge
  fills instead of the region, is refused with `region_with_island`.
- Acceptance script `A26` (fills and area properties).
- `se_drawing` background sheets (the frame and title block): `sheets` lists them with their content and the sheets
  that use them, and tells whether the working or the background view is open; `add_sheet` and `setup_sheet` take
  `showBackground`, and `setup_sheet` renames or resizes a background sheet; `add_background` (paper or custom size);
  `replace_background` copies a background of another drawing (`replaceBlocks` with its blocks), compares the content
  before and after and warns about a frame made for another paper size; `delete_sheet` refuses a background in use;
  `activate_sheet` opens the background view. `se_sketch` and `se_annotation` draw and write on a background sheet.
- `se_section_view`: 3D section views of parts, sheet metal and assemblies (View > Section) from a sketch profile:
  `keep` (inside or outside of a closed profile, left or right of an open line), `depth` and `direction` along the
  sketch normal, caption, fasteners cut or not, what is drawn and the cutting plane; `list`, `show`, `hide`, `edit`
  and `delete` (Solid Edge deletes the profile too, without undo, and any local plane that only it used: the result
  lists those planes).
- Acceptance scripts `A27` (background sheets) and `A28` (3D section views, checked by the colours of a capture).
- `se_publish`: 3D PDF documents of saved parts, sheet metal and assemblies with the 3D PDF templates of the
  installation (`templates` lists them; `pdf3d` takes a file or a folder, optionally with its subfolders, a template by
  name or path, the output folder and name, STEP AP242 and JT files to embed and an HTML page). Each PDF is checked for
  its pages, its 3D view and its embedded files. Solid Edge publishes only while no document is open, and within one
  session it carries the pages of earlier single-file publishes over: the server publishes each file through a folder
  of its own and never lets Solid Edge meet an existing file.
- Acceptance script `A29` (3D PDF publishing).
- `se_ref_plane` in assemblies: `list`; `offset` from an assembly plane or a planar face of an occurrence; `angle`
  about the line where the parent meets a pivot plane; `normal_to_edge` on an edge of an occurrence (at an end, a
  distance or a fraction of its length); `coordinate_system` by origin and angles. Each new plane is checked against
  its geometry. New actions in parts and assemblies: `show` and `hide` (a plane, a coordinate system or `all`
  planes), `rename` and `delete`, which refuses the base planes and the base coordinate system and a plane that
  planes, sketches, features or assembly relations are built on.
- Acceptance script `A30` (reference planes and coordinate systems in a part and an assembly).
- `se_component_group`: component groups of an assembly (the groups of PathFinder): `list`, `create`, `add`, `remove`,
  `rename`, `show`, `hide`, `ungroup`, `pack` (every ungrouped occurrence of a file) and `unpack`, and `delete`, which
  deletes the components too, as Solid Edge does, and needs `withComponents`. A component already in a group is
  refused (Solid Edge would leave an empty group or nest the groups).
- `se_component_query`: finds components as the Select Tools do: `find` by occurrence name or by the properties and
  material of their files (all criteria at once), among all, shown, hidden or selected components and inside
  subassemblies; `zone` inside a box, with or without the components partly inside; the matches are selected and
  listed (parts inside subassemblies with their path) and `display` shows them alone (a matched subassembly whole),
  hides or shows them, lists what changed and undoes it if Solid Edge refuses a component. Queries and zones can be
  saved in the assembly, `run`, `list`ed, renamed and deleted.
- Acceptance script `A31` (component groups, queries and zones).
- `se_print`: prints the sheets of a drawing as the Print command does. `printers` lists the Windows printers (default,
  connection local, network or remote desktop, port, driver, state, whether they ask for a file name) and the print
  settings of Solid Edge; `print` sends every working sheet or the chosen ones to a printer, or to files in a folder
  (one PDF, PostScript or printer file per sheet, named `<document>_<sheet>`, checked for its pages and page size), with
  paper size, orientation, scale or fit to the paper, colours as black and copies. The print settings of Solid Edge are
  put back afterwards. The result says when Solid Edge has recorded the print date in the drawing (which marks it
  as modified) and when Windows has made the printer used its default printer.
- Acceptance script `A32` (printing to PDF files with the Microsoft Print to PDF printer).
- `se_relations`: `center_plane` (one face of the moving part, or the plane midway between two of its faces, centred
  between two faces or planes), `connect` (the start, end, middle or centre of an edge onto a point of another edge, a
  planar face, a plane or a straight edge), `coordinate_system` (a coordinate system of a part onto one of another part
  or of the assembly, by name) and `gear` (two gears in teeth or turns, or a gear and a rack in turns and mm; `flip`
  turns the driven part the other way). Each result is measured. `lockRotation` frees (or locks) the rotation of an
  axial or insert relation, which gears need: an axial relation made through the API locks it.
- Acceptance script `A33` (center plane, connect, coordinate system, gear and rack relations).
- `se_drawing dimension`: kinds `coordinate` (an origin at a vertex or circle centre and the points measured from it,
  horizontal or vertical distances), `angular_coordinate` (about a centre from a zero direction, counterclockwise or
  clockwise) and `chamfer`; each value is checked against the points at the view scale.
- `se_annotation`: `bolt_circle` (through three holes, checked against their circle), `datum_target` ('A1', on a leader)
  and `datum_point`.
- Acceptance script `A34` (coordinate, angular coordinate and chamfer dimensions, bolt circle, datum target and point).
- `se_sheet_content`: tables of one's own on drawing sheets (headers, rows of cells split by '|', column widths,
  position; `cell` and `rows` change them) and pictures (embedded or linked, sized by width or height with their
  proportions kept); `list` and `delete`. Everything written is read back.
- Acceptance script `A35` (a revision table and a logo on a sheet).
- `se_drawing_view broken_out`: a broken-out section through an outline of model points, cut to a depth from the front
  of the part; the view is checked for the cut, and an outline away from the part is refused before anything is made.
- `se_drawing base_view configuration`: a base view of a display configuration of an assembly (saved with
  `se_assembly_config`); an unknown configuration is refused.
- Acceptance script `A36` (a broken-out section and a view of a display configuration).
- `se_feature_edit` actions `group`, `ungroup`, `reorder`, `roll_to` and `roll_to_end`: feature groups (features next to
  each other in the tree), moving a feature before or after another (moved back if a feature then fails), and rolling
  the model back to a feature (new features go after it) and forward to its end.
- `se_round radii`: variable-radius rounds, with a radius at the start and the end of each edge.
- `se_part_feature resize_holes` and `resize_rounds`: a new diameter for holes (their type and extent are kept) and a
  new radius for constant-radius rounds.
- Acceptance script `A37` (feature tree, variable-radius rounds, resized holes and rounds, user-defined sets).

### Changed

- The project is now named `solid-edge-mcp` and the repository moved to `github.com/gabichuuuuu/solid-edge-mcp`; the old
  URL redirects. Update existing clones with
  `git remote set-url origin https://github.com/gabichuuuuu/solid-edge-mcp.git`.
- A folder or file option can only point into the allowed folders, the Solid Edge installation or back to the value it
  had before the server changed it (`path_not_allowed` otherwise).
- `se_com_invoke` and `se_com_get` refuse the members that close Solid Edge 2026 or leave it unstable
  (`RadialMenu.DumpPallets`, `SavePallets`, `ViewStyle.BeginPropertyBuffer`, `FlushPropertyBuffer`) with
  `unsafe_member`.
- `se_view fit`, `orient` and `capture` keep the field of view of a perspective camera (the fit of Solid Edge and its
  orientation commands narrow it).
- `se_com_get` and `se_com_invoke` check a numeric `Item`/`ItemEx` index against the collection's `Count` and refuse
  one out of range with `index_out_of_range` (`ModelMembers.Item` beyond the end crashes Solid Edge 2026 with
  `RPC_E_SERVERFAULT`).
- The arguments are also checked inside the objects of a list (the criteria of `se_component_query`): an unknown field,
  a missing one or a value out of its enumeration is named with its position (`'criteria[0].property' must be one
  of: …`).
- PDF checks read the page sizes (MediaBox, in mm) besides the page count, embedded files and 3D views.
- `se_relations list` names point relations `connect`, as Solid Edge does, shows whether an axial relation locks the
  rotation (`rotationLocked`) and the ratio of gear relations (a rack in mm).
- The COM layer refuses `Relations3d.AddPlanarCam` and `AddPath` (`unsafe_member`): they leave Solid Edge 2026
  unstable (RPC_E_SERVERFAULT).
- The COM layer also refuses `Rounds.AddBlend` (a blend between faces closed Solid Edge once and is otherwise created
  failed) and `ResizeHoles.Add` (without a HoleData it leaves Solid Edge unstable); the refusal names the tool to use.
- `se_feature_edit`: `feature` is optional (`group` and `roll_to_end` do not use it). `se_round`: `radius` is optional
  when `radii` is given.
- The acceptance runner compares texts by value in list filters, so a filter can write a space as `\u0020`
  (`[?name="Microsoft\u0020Print\u0020to\u0020PDF"]`).

### Fixed

- `se_primitive`: the description of `side` was that of a rib.
- An operation that Solid Edge cannot build and removes while creating it (for example a hole exactly as wide as the
  bar) is reported as `feature_failed`, saying so, and its profile is removed. Before, the error was `object_deleted`
  and the copy of the profile stayed in the part: undoing and cleaning up were skipped whenever Solid Edge deleted an
  object during a call, as if Solid Edge had closed.
- `se_relations` undoes a relation and puts the occurrences back also when Solid Edge rejects the call with an error
  that .NET raises as another exception (E_INVALIDARG, E_NOINTERFACE); `list` no longer fails on a suppressed
  center-plane, coordinate system or gear relation.
- `se_sketch_edit offset` finds the chain of lines, arcs and circles from their ends instead of with `ChainLocate`,
  which picks with a tolerance in pixels and offset one wrong element while the Solid Edge window was minimized; a
  Solid Edge that the server starts is no longer left minimized.
- `se_options set` with `null` on a text option no longer writes the text "null".
- The generic layer describes the drawing views and blocks of a drawing with the draft interfaces, not with the part
  library's interfaces that share their IIDs.
- A drawing view converted to 2D (also by Solid Edge's own command) is listed without a model; parts lists and bend
  tables refuse it with `wrong_view` instead of an unexpected error.
- `se_com_items` reads collections through their interop `Count` and `Item`: by name, an attribute set with an
  attribute called `Count` answered with that attribute and the call failed.
- `se_com_invoke` gives Solid Edge the output arrays (`out Array`) it expects to find already made, through IDispatch
  (`GetTopLevelAssemblyFileNames` failed with E_POINTER).
- Arguments for file types, filters and search text (`file_types`, `varFileFilterOrText`…) are no longer taken as
  paths (`'*.par'` was refused by the path policy); file names such as `TextureFileName` still are.
- `se_assembly list` with `recursive` and `se_bom` with `flat` no longer fail on a subassembly without components.
- A value that .NET cannot convert (`Query.Criteria` gives an invalid OLE variant) fails with `unreadable_value`
  instead of an unexpected internal error.
- `se_ref_plane` makes global planes in parts, as the Planes commands do. They were local planes: without a name,
  outside the feature tree, impossible to rename or delete, and Solid Edge deleted them with a section view drawn on
  them.
- `se_com_invoke` and `se_com_get`: an argument whose type does not fit its parameter (an array for a number) fails
  with `invalid_argument` instead of an unexpected internal error.

## [0.1.0] - 2026-10-12

First published release (alpha), validated against Solid Edge Community Edition 2026. It is source only: the
interop is generated from your own Solid Edge installation, so no binaries are published.

### Added

- `se_sheetmetal convert`: turns a thin-walled part copied into a sheet metal document into sheet metal, with a base face
  that stays flat and the corner edges to rip (a 100 x 60 x 40 box with 2 mm walls gives four bends and a flat pattern of
  173.2 x 133.2 mm). The call goes through the typed interface: through IDispatch Solid Edge rejects the edge array.
- `se_frame` reports the cut length of each member (`sectionCutLengths`, in member order), read through the typed
  interface.
- The generic layer (`se_com_invoke`) retries through the typed interface, with empty arrays and zeros for the omitted
  typed arguments, a call that IDispatch rejects with DISP_E_TYPEMISMATCH (e.g. `ConvToSMs.Add` with only the face).

- No installation detail is assumed where it can be read: view orientations run the interop command of the active
  environment (looked up by name), an automatic explode is checked by the components it moved instead of a status text
  in Solid Edge's language, and planes made normal to an edge or tangent to a face and the angle relation's measurement
  plane use the base planes found by their orientation.
- Base planes by orientation: `'xy'`, `'yz'` and `'xz'` find the plane through the origin with that normal in any template
  (parts and assemblies); `'1'`-`'3'` keep following the template's order.
- Each installation is read instead of assumed: `se_ui find` finds a button by the text this Solid Edge shows (any
  language or ribbon theme) with its tab, group and command id; `se_documents templates` lists the default template of
  each kind (Solid Edge's options) and the templates in its folders; a weldment uses the weldment template set in Solid
  Edge's options; `se_frame library` also lists a library folder of your own. Material, library and style names that
  differ only in case are taken as written by Solid Edge, and a name that is not found is answered with the closest names
  this installation has.
- MCP prompts for common workflows (`/mcp__solid-edge__<name>` in Claude Code): `model_part`, `part_from_template`,
  `manufacturing_drawing` and `welded_frame`.
- `se_session quit` and `restart`: close Solid Edge (refused while a document has unsaved changes) and start it again,
  to recover a Solid Edge that misbehaves.
- `se_frame repair`: makes again the frames of an assembly that got them before any assembly feature (outside
  `se_frame`), where Solid Edge refuses assembly features; deleting the frames brings them back (verified).
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
- The coverage progress (`scripts/update-progress.ps1`, README badge and `docs/COVERAGE.md`) is the share of
  functions that work through the MCP (✔); functions that fail in Solid Edge (✘) no longer count as done. The
  script also draws the progress chart (`docs/assets/progress.svg`, one square per function and one row per area, in
  a light and a dark version), shown in the README and in `docs/COVERAGE.md`.
- The banner is a plain line drawing in DIN lettering, in a light and a dark version, and the README has no emoji.
- The generated documentation follows the code: `scripts/update-progress.ps1` also writes the README tool count and
  version (from the `<Version>` of the server project, also shown as a badge) and lists the tools that
  `docs/COVERAGE.md` does not mention yet under "New tools" as not tested. With `-Staged`, from a
  pre-commit hook, it regenerates them from what each commit contains.
- `docs/COVERAGE.md` lists every function of Solid Edge 2026 found in its type libraries and command sets, not only
  the ones already reviewed: 107 functions not tried yet were added as `·` (among them a new "Program configuration"
  area for the Options dialog settings, add-ins, customization and license), so the progress measures the whole
  program: 112 of 254 functions work (44 %).
- The README header names Siemens Solid Edge, and the banner has its own background (light or dark grey) so that it
  stands out from the page in both GitHub themes. `docs/assets/social-preview.png` is the image for link previews.
- Contributing guide, code of conduct and security policy (`CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`);
  vulnerabilities are reported privately through GitHub.
- The internal development notes and AI assistant instructions are no longer published.
- Code comments, scripts and documentation are in English, including the project notes
  (`AGENTS.md`, `STATUS.md`, `PLAN.md`). `ESTADO.md` and `docs/COBERTURA.md` were renamed to
  `STATUS.md` and `docs/COVERAGE.md`. Test data are in English too, and the project notes no longer
  mention the local development environment.

### Fixed

- `se_drawing dimension`: a diameter is placed with its text outside the circle, away from the view centre or towards
  `position` (it was inside circles over 14 mm on the sheet); a radius puts its arrow and text outside.
- `se_assembly_feature fillet_weld`: a busy Solid Edge can report the weld ok before its body exists; the server now lets it
  finish before deciding, instead of removing a good weld.
- `se_sheetmetal flatten` on a sheet that cannot be laid flat (e.g. a copied part not yet converted) fails with
  `flatten_failed` and the next step (it was an internal E_INVALIDARG error).
- `se_geometry summary` on a body converted to a mesh fails with `no_brep_body` (it said only E_FAIL).
- `se_assembly_feature list` in an assembly reopened from its file: Solid Edge can hand back the parts of a hole or
  cutout as dead objects; the parts are now found by the faces the feature leaves on them.
- Closing a document releases the wrappers of its objects, so a new object that Solid Edge creates at the same address
  is not mistaken for a dead one (RPC_E_DISCONNECTED on a reopened assembly).
- Quitting Solid Edge through `se_com_invoke` no longer reports an internal error.
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

## [0.1.0-alpha] - not released

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
