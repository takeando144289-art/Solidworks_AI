# SolidWorks API notes

- SolidWorks API lengths are metres. The public agent API uses millimetres.
- COM automation must run in an STA process; the CLI entry point is configured for Windows x64.
- A new part is based on an installed `.prtdot`; hard-coded localized plane names are avoided by finding the first `RefPlane` feature.
- A feature is only considered successful after `EditRebuild3`, measured bounding-box validation, successful `SaveAs3`, and an on-disk file check.
- Existing CAD files are protected unless `--overwrite` is explicit.
- This installation requires interactive startup authentication. The CLI only attaches to an authenticated, running instance and does not automate login.
- SOLIDWORKS Connected 2026 may return `false` from `SaveAs3` even though the document was saved. The wrapper therefore verifies both the on-disk file and the active document path, and records a warning when the return value disagrees.
- Face `PlaneParams` is not a reliable trimmed-face location. Successive shaft stages select the farthest planar face using its `GetBox` centre projected onto an axis read from `CylinderParams`.
- A successful stepped shaft is re-inspected from topology: each cylindrical face reports diameter, axial length, origin, axis, and a persistent reference ID.
- `IAssemblyDoc.AddMate3` has a by-reference `ErrorStatus`; Reflection calls must mark that argument as by-ref. `swAddMateError_NoError` is `1` (not `0`).
- `IAssemblyDoc.GetBox` is approximate and is reported as such; interference volume comes from `IInterferenceDetectionMgr.GetInterferences` and is converted from m3 to mm3.
- Japanese SOLIDWORKS did not resolve the English single-view name through `CreateDrawViewFromModelView3`. `Create3rdAngleViews2` is used to avoid localized standard-view names.
- Shaft drawing dimensions now reference model edges and are checked after save/reopen for attachment, numeric overrides, and dangling references. No fit or tolerance is assumed. See `shaft_drawing_reconstruction.md`.
- A drawing is only accepted after rebuild, SLDDRW/PDF on-disk checks, and bitmap visual review for clipping and annotation overlap.
- `GetPartBox` is suitable for the Milestone 1 verification but is approximate. Later precision inspection should use body/face geometry and tessellation-independent measurements.

