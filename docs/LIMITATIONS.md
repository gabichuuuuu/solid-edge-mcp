# Known limitations

- Not every Solid Edge function can be used through the MCP: Solid Edge's API may fail or damage the document, a function
  may need a licence or add-on, or a command may wait for the person. [COVERAGE.md](COVERAGE.md) lists each one with the
  reason and, when there is one, the alternative.
- The server executable is not code-signed.
- After an `RPC_E_SERVERFAULT`, or when features that worked start failing (it happened once in the full acceptance
  run, without a cause that could be reproduced), Solid Edge is unstable and must be restarted with
  `se_session restart` (refused while a document has unsaved changes).
- Solid Edge does not record as an undoable step an operation created through the API from a sketch:
  `se_documents undo` only undoes what Solid Edge records. To remove an operation, use
  `se_feature_edit`.
- The `se_command` commands that ask for a selection or a dialog box wait for the person.
- The server generates the interop, and the plugin installs the server, with unsigned Windows PowerShell scripts. Where a
  policy only lets signed scripts run, both fail and the tools answer `interop_missing`: the scripts have to be allowed,
  or the interop generated with `scripts\generate-interop.ps1 -OutputDir` beside the server where they are.
- A Solid Edge dialog that waits for an answer (e.g. a first-use tip) blocks Solid Edge until it is
  answered: the blocked call fails with `dialog_open` and names the dialog with its buttons and options,
  and `se_session status` lists it. `se_session answer` presses one of its buttons, after choosing one of
  its options, and the blocked call then finishes; an option such as "don't prompt me again" changes
  Solid Edge's options, so it is chosen only when the person asks.
- The Boundary and SmoothBoundary display modes are not applied in Solid Edge 2026.
- Program settings: suspending the recent files list (`se_app suspend_recent`) does not always stop Solid Edge
  2026 from adding the files saved with `save_as`; with no document window open Solid Edge cannot read some
  drawing options and reports others as -1, so an options snapshot is restored in the state it was made in;
  and a folder option that had no value (such as Macros) cannot be emptied again through the API.
- View: Solid Edge 2026 does not set the colour of a solid background through its API (`se_view background`
  draws a gradient with the same colour at both ends instead) and changes environment backgrounds to solid.
- The radial menu cannot be customized through the API: its `DumpPallets` and `SavePallets` methods close
  Solid Edge 2026, so the COM layer refuses them.
- An exported drawing that is not saved shows "Error: No reference" in the title block fields that
  come from the file, such as the date; the server warns about it.
- Planar cam and path relations leave Solid Edge 2026 unstable through its API, so they are refused: add them with
  the Cam and Path commands of Solid Edge.
- Surfaces: Solid Edge 2026 does not trim surfaces with reference planes through its API (use a surface on the plane or a
  curve). Faces merged with `se_part_feature redefine_faces` become a B-spline fitted to the edges around them, which
  can change the volume: the answer warns when it changes by more than 0.01 %.
- Printing: only printing to files (Microsoft Print to PDF) is tested, not printing on paper. When Windows manages
  the default printer ("Let Windows manage my default printer"), it makes the last printer used the default:
  `se_print` reports the change but does not undo it, since it is a Windows setting.
- Windows x64 only; tested with Solid Edge 2026 Foundation (with Simulation Express) and the Community Edition 2026.
- Simulation: with Simulation Express only fixed constraints, force and pressure loads and static and modal studies
  solve; the other kinds are on the ribbon but refused by the licence, and `se_simulation` refuses them first.
