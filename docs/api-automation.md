# SOLIDWORKS API operations from chat

The chat can discover and invoke document-local SOLIDWORKS COM APIs instead of
requiring a dedicated CLI command for each feature. `api search` and `api describe`
read interface signatures and enum values from the installed SOLIDWORKS interop
assemblies. They do not execute CAD methods. In V6 these operations are tools available to the native AI agent. The pane does
not decide the search/execute/retry sequence or require a separate execution message.
The agent can also use its native tools and inspect the project source.

`api execute` accepts a structured JSON plan. The Windows host checks the entire
plan's member signatures before creating the working document, executes calls in
order, rebuilds created features and checks resulting part bodies. False/null
results stop execution unless explicitly declared to be an expected observation.
An optional `equal` value checks a returned measurement/count; `sizeMm` checks the
final part's three bounding dimensions. Saving and overwrite permission belong to
the host. Recipes cannot call application-wide commands, macros, raw pointer APIs,
or save/export methods. This is a document automation interface, not a shell or a
sandbox for arbitrary third-party code.

A plan creates a new part, assembly or drawing from configured templates, or edits
a copy of an active part. Existing assembly/drawing editing through `copy-active`
is not implemented. Those workflows still use their dedicated commands where
available. Unsaved source part changes are preserved in a separate working copy;
failed trials are closed, and successful saved output remains open. Working copies
are retained on disk as recovery artifacts. No Windows/WSL runtime switching is needed.

## Example plan

```json
{
  "summary": "Create a cylindrical part, diameter 20 mm, length 30 mm",
  "document": "new-part",
  "sizeMm": [20, 20, 30],
  "steps": [
    {"id":"selected","target":"plane","interface":"IFeature","member":"Select2","kind":"call","args":[false,0]},
    {"id":"opened","target":"sketch","interface":"ISketchManager","member":"InsertSketch","kind":"call","args":[true]},
    {"id":"circle","target":"sketch","interface":"ISketchManager","member":"CreateCircleByRadius","kind":"call","args":[{"mm":0},{"mm":0},{"mm":0},{"mm":10}]},
    {"id":"closed","target":"sketch","interface":"ISketchManager","member":"InsertSketch","kind":"call","args":[true]},
    {"id":"boss","target":"features","interface":"IFeatureManager","member":"FeatureExtrusion2","kind":"call","args":[true,false,false,0,0,{"mm":30},{"mm":0},false,false,false,false,0,0,false,false,false,false,true,true,true,0,{"mm":0},false]}
  ]
}
```

Send this plan through standard input to `swagent api execute --plan-stdin
--output Example.SLDPRT --json`. The native agent can invoke this directly through its CAD tool.
Public lengths use `{ "mm": number }`, angles use `{ "deg": number }`; conversions
happen at the COM boundary. Plain numbers are counts, flags, enum values or ratios.
`{ "enum": "swEndConditions_e.swEndCondBlind" }` resolves installed constants.
`{ "ref": "stepId" }` refers to a prior result; arrays support `stepId[0]` and
by-reference output arguments support `stepId_out[0]`. Roots are `model`, `plane`,
`sketch`, `features`, `extension` and `math`. Property access uses `kind: get/set`.

## Plate convenience command

`part create-plate --width 150 --height 20 --thickness 3 --radius 5
--holes "[[10,10],[20,10],[30,10]]" --thread M3x0.5 --output Plate.SLDPRT --json`

This builds a native extrusion, edge fillet and ISO Hole Wizard tapped-hole feature
using the same generic API interpreter. Hole coordinates are in mm from the plate's
lower-left sketch corner. Coordinate conversion uses the sketch's actual transform,
so the initial reference plane's global orientation is immaterial. The recipe
checks dimensions, thread diameter and number of hole positions before saving.
M3 tapped holes are represented by the Hole Wizard and cosmetic thread; a helical
solid thread is not modeled. Pitch alone does not determine edge margins/row layout,
so the AI must ask for those when they are missing.

## Coverage

This removes the fixed catalog as the only route for feature operations. It does
not guarantee every SOLIDWORKS UI command or every API sequence will work. API
availability, method semantics, selected geometry, templates and installed licenses
still matter. Use metadata discovery and inspect actual execution errors instead
of claiming an unsupported feature is supported. External application operations,
equations/macros and direct file lifecycle operations remain outside recipe execution.
