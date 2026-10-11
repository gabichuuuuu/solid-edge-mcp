# Solid Edge coverage

Which Solid Edge functions can be used from the MCP and through which route. Checked with Solid Edge
Community Edition 2026 (226.00.01.004). Date of the last review: 2026-10-10.

## How to read this table

Every Solid Edge function is accessible through one of these routes:

- **High level**: a specific `se_*` tool, in mm and degrees, with verification of the result and
  reversal if it fails. It is the preferred route.
- **COM**: the generic layer (`se_com_describe` to see members and signatures, `se_com_get`,
  `se_com_set`, `se_com_invoke`, `se_com_items`, `se_com_constants`). It reaches any API object and
  method, in meters and radians, without automatic verification: check the result with
  `se_geometry`, `se_physical` or `se_view capture`. Faces and edges read from an occurrence go in
  as their reference in the assembly.
- **Command**: `se_command` runs any interface command (`find` searches by name and environment).
  Commands that ask for a selection or a dialog box wait for the person.

Status: **✔** tested in Solid Edge 2026; **·** available in the API but not tested; **✘** fails or
damages the document in Solid Edge 2026 (do not use).

## Application, documents and data

| Function | Route | Status |
|---|---|---|
| Connect, start Solid Edge with a license, reconnect | High level `se_session` | ✔ |
| Create part, sheet metal, assembly, drawing, weldment (template optional; without a `.pwd` template, as in Solid Edge 2026, the weldment is an assembly with the weldment assembly template) | `se_documents new` | ✔ |
| Open, activate, save, save as, save a copy, close | `se_documents` | ✔ |
| Undo, redo, recalculate | `se_documents undo/redo/recompute` | ✔ (only what Solid Edge records; see the note) |
| Document properties (summary, custom) | `se_properties` | ✔ |
| Variables, dimensions and formulas; rename, comment, expose (assemblies, part families) and limits (range or allowed values) | `se_variables` | ✔ |
| Solid Edge options (the Options dialog and global settings) and document options; document display units | `se_options` | ✔ |
| Windows (list, activate, arrange, state), environments, ribbon tabs, groups and command ids | `se_ui` | ✔ |
| Feature tree; suppress, delete, rename | `se_feature_tree`, `se_feature_edit` | ✔ |
| Geometry (body, faces, edges with filters) | `se_geometry` | ✔ |
| Measure distances and angles (in part and in assembly) | `se_measure` | ✔ |
| Mass, volume, center of gravity, inertia | `se_physical` | ✔ |
| Active selection (read, change) | `se_selection` | ✔ |
| View: fit (on drawings, the whole sheet with the title block), orientation, mode, PNG capture | `se_view` | ✔ |
| Named views: list (the 6 standard ones and the saved ones), save the current view with a description, apply, delete | `se_view named_views`, `save_view`, `apply_view`, `delete_view` | ✔ |
| Display modes Boundary and SmoothBoundary | — | ✘ Solid Edge 2026 changes them to Smooth (`not_supported` error) |
| Export STEP, IGES, Parasolid, STL, JT, SAT, 3MF, PDF, DXF, DWG | `se_export` | ✔ |
| Material: libraries, properties, assignment (updates density and mass) | `se_material` | ✔ |
| Create or edit materials and libraries; sheet metal gauges | COM (`Application.GetMaterialTable()`; `GetMaterialList` and `GetMatLibFileName` return E_NOTIMPL) | · |
| Colours: list, create and delete face styles; apply them (or a #RRGGBB colour with opacity) to the body, faces, features or assembly components; read and remove them | `se_appearance` | ✔ |
| Remove the style of a feature | — | ✘ `SetStyle` with an empty style keeps it (`not_supported`; apply another one) |
| Layers of models and drawings: list, create, activate, show or hide, selectable, delete empty ones, move elements (sketch geometry, drawing views, dimensions, annotations); per sheet in drawings | `se_layer` | ✔ |
| Part families (`FamilyMembers`), configurations (`PartConfigurations`) | COM | · |
| 3D PMI (`PMI`), live sections | COM | · |
| Simulation and generative studies | COM / Command | · |

## Sketches and reference planes

| Function | Route | Status |
|---|---|---|
| Sketch on a principal plane, a reference plane or a planar face | `se_sketch create` | ✔ |
| Line, rectangle, polyline, circle, arc, ellipse, spline, point, corner fillet and chamfer | `se_sketch` | ✔ |
| Revolve axis (also one side of the profile: a copy is used and the profile stays closed) | `se_sketch axis` | ✔ |
| Validate a closed profile or an open chain | `se_sketch close` | ✔ |
| List the elements of a sketch (with keypoints) and its relations and dimensions | `se_sketch info`, `se_sketch_constraint list` | ✔ |
| Project model edges onto the sketch | `se_sketch project` | ✔ |
| 2D relations (horizontal, parallel, tangent, equal, symmetric…) and dimensions (length, true distance, interior angle, radius, diameter) | `se_sketch_constraint` | ✔ |
| Sketch text | — | ✘ `Profile.TextProfiles` has no `Add`: text profiles are created only with the Text command in Solid Edge |
| 3D sketches | COM (`Sketches3D`) | · |
| Offset and angled planes | `se_ref_plane` | ✔ |
| Planes through 3 points (vertices or edge midpoints), midway, normal to an edge (at an end, a distance or a fraction of its length), tangent to a cylinder or cone (parallel to a plane or at an angle about the axis); coordinate systems by origin and angles or along two edges | `se_ref_plane` | ✔ |
| Planes tangent to a curved surface (cylinder, sphere…) at an edge end | `se_ref_plane tangent points` | ✔ |
| Reference axes | — | ✘ `RefAxes` has no Add (the axes come from features such as revolves) |

## Part (ordered modeling)

| Function | API collection | Route | Status |
|---|---|---|---|
| Boss and cut by extrusion (blind, through, up to next, symmetric) | `ExtrudedProtrusions`, `ExtrudedCutouts` | `se_extrude` | ✔ |
| Revolve (boss and cut) | `RevolvedProtrusions`, `RevolvedCutouts` | `se_revolve` | ✔ |
| Holes (simple, countersunk, counterbored, threaded; through, up to next or finite) | `Holes`, `HoleDataCollection` | `se_hole` | ✔ |
| Edge fillet and chamfer | `Rounds`, `Chamfers` | `se_round`, `se_chamfer` | ✔ |
| Rectangular and circular pattern; mirror | `Patterns`, `MirrorCopies` | `se_pattern` | ✔ |
| Shell (thin wall) | `Thinwalls` | `se_shell` | ✔ |
| Draft | `Drafts` | `se_draft` | ✔ |
| Loft, sweep, helix (boss and cut) | `LoftedProtrusions`, `SweptProtrusions`, `HelixProtrusions`… | `se_loft_sweep` | ✔ |
| Loft with guide curves or tangencies, sweep along several paths, conical or variable-pitch helix, thin wall | `LoftedProtrusions.Add`, `AddWithThinWall`, `HelixProtrusions.AddFinite` (optional) | COM | · |
| Rib from an open sketch (up to the part or to a finite depth; symmetric or to one side) | `Ribs` | `se_part_feature rib` | ✔ |
| Thread on a cylinder | `Threads` | — | ✘ `Threads.Add` returns E_INVALIDARG for every combination tried (faces or edges as ends, `HoleData` from `Add` or `AddEx` with the ISO Metric table); use `se_hole` tapped (cosmetic) or a helix cut with `se_loft_sweep` (physical) |
| Web network from open lines (to the next face or a finite depth) | `WebNetworks` | `se_part_feature web_network` | ✔ |
| Lip on a chain of rim edges (flush with the side face) | `Lips` | `se_part_feature lip` | ✔ |
| Vent | `Vents` | COM | · |
| Mounting boss | `MountingBossCollection` | — | ✘ `Add` leaves the feature failed with a sketch point on a plane above or at the open face of a shelled part, on either side, with or without the optional arguments; the sketch with only a point cannot be included (`CreateIncludedProfile` E_FAIL) |
| Thicken faces (the side that adds material by default) | `Thickens` | `se_part_feature thicken` | ✔ |
| Split, boolean operations between bodies | `Splits`, `Unions`, `Subtracts`, `Intersects`, `BooleanFeatures` | COM | · |
| Primitives: box, cylinder and sphere, added or cut, by their centre on a plane or face (built in synchronous modeling) | `BoxFeatures`, `CylinderFeatures`, `SphereFeatures` | `se_primitive` | ✔ |
| Slot along an open path (rounded or square ends; through or to a depth; counterbored) | `Slots` | `se_part_feature slot` | ✔ |
| Box turned about the plane normal (kept centred on the given point) | `BoxFeatures.AddByCenter` (angle) | `se_primitive angle` | ✔ |
| Box by two opposite corners or by three points (an edge, then a point on the opposite edge) | `BoxFeatures.AddByCenter` (corners turned into centre, size and angle) | `se_primitive corners` | ✔ |
| Tubes | `TubeFeatures` | — | ✘ `TubeFeatures` has no `Add`: tubes are made only with the Tube command |
| Mirror the whole body about a plane, keeping the original | `MirrorParts` | `se_part_feature mirror_body` | ✔ |
| Scale the body (uniform or per axis, about the part origin) | `ScaleBodyFeatures` | `se_part_feature scale_body` | ✔ |
| Copied part (scaled, mirrored about a reference plane): base body of an empty part or united with the body | `Models.AddCopiedPart` | `se_part_feature copy_part` | ✔ |
| User-defined pattern, pattern along a curve | `UserDefinedPatterns`, `PatternParts` | COM | · |

## Synchronous modeling and surfaces

| Function | Route | Status |
|---|---|---|
| Switch between synchronous and ordered modeling | COM (`ModelingMode` 1 synchronous, 2 ordered) | ✔ |
| Delete faces (the solid heals), delete holes up to a diameter, delete rounds; offset faces along their normal (ordered) | `se_part_feature delete_faces`, `delete_holes`, `delete_rounds`, `offset_faces` | ✔ |
| Move and rotate faces, replace faces | COM (`FaceMoves`, `FaceRotates`, `ReplaceFaces`; in ordered, `FaceMoves.Add` along a face normal leaves the feature failed) | · |
| Surfaces by extrusion, revolve, sweep, loft, boundaries, offset, stitched | COM (`Constructions.ExtrudedSurfaces`, `SweptSurfaces`, `SurfaceByBoundaries`, `StitchSurfaces`…) | · |
| Curves (intersection, projected, helical, from a table) | COM (`Constructions.IntersectionCurves`, `ProjectCurves`, `HelicalCurves`…) | · |

## Sheet metal

| Function | Route | Status |
|---|---|---|
| Thickness, radius, reliefs and K-factor | `se_sheetmetal settings/set_settings` | ✔ |
| Base and additional tab | `se_sheetmetal tab` | ✔ |
| Base contour flange (always toward the sketch normal) | `se_sheetmetal contour_flange` | ✔ |
| Flange (several edges, angle, side, radius) | `se_sheetmetal flange` | ✔ |
| Hem: closed, open, S-shaped, closed loop | `se_sheetmetal hem` | ✔ |
| Round or chamfered corner break | `se_sheetmetal break_corner` | ✔ |
| Flat pattern and cut size; flat pattern DXF | `se_sheetmetal flatten`, `se_export flatPattern` | ✔ |
| Dimple, drawn cutout, normal cutout, etch, jog, bend, U/V bead | `se_sheetmetal_form` | ✔ |
| Contour flange on an edge, lofted flange, multi-edge flange | COM (`ContourFlanges.AddSync`, `LoftedFlanges`, `MultiEdgeFlanges`) | · |
| Change the angle or the inside radius of existing bends | `se_sheetmetal bend_angle`, `bend_radius` | ✔ |
| Unbend and rebend | — | ✘ `Unbends.Add` with the fixed face and the bend face of a flange throws RPC_E_SERVERFAULT |
| Tab and slot, enclosures, edge rip, convert to sheet metal | COM (`TabAndSlotFeatures`, `Enclosures`, `RipEdges`, `ConvToSMs`) | · |
| Close corners (`CloseCorners`) | — | ✘ RPC_E_SERVERFAULT; broken graphics until restart |
| Gussets (`Gussets`) | — | ✘ corrupts the document |
| Louvers (`Louvers`) | — | ✘ does not resolve |
| Curl, open-loop and centered-loop hems | — | ✘ fail (RPC_E_SERVERFAULT) |
| S-shaped hem or loop on an edge adjacent to another hem | — | ✘ RPC_E_SERVERFAULT (undone) |
| Circular bead; punched and formed ends | — | ✘ no effect or E_FAIL |
| Flange in ordered modeling; `DimSide`; normal cutout `igBoth` or finite | — | ✘ |

## Assemblies

| Function | Route | Status |
|---|---|---|
| Insert, move, rotate, fix, replace, show/hide, suppress, delete components | `se_assembly` | ✔ |
| Planar (mate and flush), axial, insert, parallel (axes at a distance), tangent, angle (with a measurement plane) and rigid set relations; list and delete | `se_relations` | ✔ |
| Faces, edges and summary (volume and exact box, also rotated) of each occurrence in assembly coordinates (own handles; the occurrence is inferred in relations and measurements) | `se_geometry source` = occurrence | ✔ |
| Interferences | `se_interference` | ✔ |
| Bill of materials | `se_bom` | ✔ |
| Component pattern that follows a part pattern; list, delete and dissolve patterns | `se_assembly_pattern` | ✔ |
| Duplicate pattern (occurrences copied onto others at the same relative position) | `se_assembly_pattern duplicate` | ✔ |
| Component pattern along a curve | COM (`AssemblyPatterns.CreateAlongCurveEx3`: E_NOINTERFACE with part edges, their references and assembly layout lines) | ✘ |
| Component mirror | — | ✘ `AssemblyMirrors` has no Add (only lists existing mirrors) |
| Assembly features, parts driven by the assembly | COM (`AssemblyFeatures`, `AssemblyDrivenPartFeatures`) | · |
| Display configurations (shown and hidden components): list, save, apply, delete; the assembly must be saved (.cfg file) | `se_assembly_config` | ✔ |
| Exploded views, groups | COM (`AssemblyDocument.AutoExplode` answers "Operation successful" but the explode is not measurable outside the explode environment) / Command | · |
| Structural frames, pipes, harnesses, fasteners, weldment | COM (`StructuralFrames`, `Pipes`, `Harnesses`, `FastenerSystems`, `WeldmentAssembly`) / Command | · |

## Drawings

| Function | Route | Status |
|---|---|---|
| Sheets: add, configure, activate, delete | `se_drawing` | ✔ |
| Base, projected, auxiliary, section and detail views; move, scale, delete | `se_drawing` | ✔ |
| Dimensions (length, distance, diameter, radius, angle) with automatic placement; retrieve dimensions | `se_drawing` | ✔ |
| Parts list | `se_drawing parts_list` | ✔ |
| Update views | `se_drawing update` | ✔ |
| Text, balloons, callouts, center marks, centerlines and leaders attached to the views; list and delete | `se_annotation` | ✔ |
| Surface finish symbols, datum frames and feature control frames (14 characteristics, diameter zone, material condition, datums), attached to the views or free | `se_annotation` | ✔ |
| Weld symbols (14 weld types on the arrow side, the other side or both, size, all around, field weld) on a leader from an edge | `se_annotation weld` | ✔ |
| Composite and multi-row feature control frames (up to 4 rows) | `se_annotation feature_control_frame rows`, `composite` | ✔ |
| Hole tables | — | ✘ `HoleTables2.Add` creates an empty table, but `AttachDrawingView` gives E_UNEXPECTED and, after an origin is added (`AddNewXYoriginEx` with the `Reference` of a view line), `AddHole` gives RPC_E_SERVERFAULT; use the Hole Table command in Solid Edge |
| Bend table of a sheet metal view (flat pattern or bent), placed above the title block or at a position | `se_drawing bend_table` | ✔ |
| Blocks | COM (`Blocks`) | · |
| 2D geometry on the sheet (lines, rectangles, circles, arcs, ellipses, splines, points, fillets) with relations and driving dimensions | `se_sketch`, `se_sketch_constraint` with a sheet or drawing handle | ✔ |
| Export PDF, DXF, DWG (a drawing that is not saved warns: the title block fields that come from the file, such as the date, show "Error: No reference") | `se_export` | ✔ |

## Notes

- **Undo**: Solid Edge does not record as an undoable step an operation created through the API from
  a sketch, and creating a sketch clears the history; after that, in that session of the document, it
  does not record holes or deletions either (in a reopened document it does). `se_documents
  undo/redo` read `UndoTransactionCount`/`RedoTransactionCount`, return `nothing_to_undo` when there
  are no steps, and check that the counter goes down. To remove an operation, use
  `se_feature_edit delete/suppress`.
