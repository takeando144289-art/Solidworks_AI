# Shaft drawing reconstruction (2026-09-06)

Source: `workspace/parts/Milestone2_Shaft.SLDPRT`.

The measured solid consists of diameter 12 x length 8, diameter 10 x length 20,
and diameter 8 x length 5 mm. No holes, grooves, fillets or chamfers are present.
Material is not assigned.

The drawing uses a longitudinal exterior view and its aligned right-hand end
projection in third-angle arrangement. A section adds no internal information for
this solid shaft and is deliberately omitted. Cylindrical faces supply centerlines.

The three diameters refer directly to model circular edges. The axial dimensions
8, 28 and 33 refer to pairs of model edges, all from the large-diameter left end.
This defines both shoulder positions and the overall length with no closed chain
of redundant controlling lengths. The intermediate lengths are 28-8=20 and
33-28=5 mm. A functional shoulder datum and fit tolerances can replace this layout
when the mating requirements are specified.

Dimensions are generated with IView.SelectEntity and AddHorizontalDimension2 /
AddDimension2. No coordinate-only CreateLinearDim4 / CreateDiamDim4 dimensions or
numeric value overrides are used. The builder checks values against topology,
requires attached entities, rejects dangling references and overrides, rebuilds,
saves, reopens the drawing, and repeats the reference/value checks.

The layout is A4 landscape, 3:1. The current planner is restricted to plain,
coaxial, three-stage solid shafts within 40 mm length and 20 mm diameter. Other
geometry fails explicitly rather than receiving an incomplete automatic drawing.

Material, dimensional tolerances, fits, finish, heat treatment, chamfers and
shoulder radii remain unspecified. This is a dimensioned geometry drawing;
manufacturing release requires those design decisions. The old unsupported
bearing-seat h6 note and automatic +/-0.01 assignment have been removed.

Japanese-containing notes were visible in SOLIDWORKS but absent in exported PDF
on this installation, including after reopening. ASCII English notes with Arial
export correctly. Verify the actual exported PDF, not only a SOLIDWORKS bitmap.

Commands:

```powershell
dotnet run --project src/SolidWorksAgent.Tools -- drawing create-shaft `
  --model workspace/parts/Milestone2_Shaft.SLDPRT `
  --output Shaft_Machining_Dimensioned.SLDDRW `
  --pdf Shaft_Machining_Dimensioned.pdf --json

dotnet run --project src/SolidWorksAgent.Tools -- drawing inspect `
  --drawing workspace/drawings/Shaft_Machining_Dimensioned.SLDDRW --json
```

`--length` and `--diameter` are optional expected-size checks, never source values.
`--tolerance` is optional and applies an explicitly requested symmetric tolerance
to all six dimensions. Existing output files require explicit `--overwrite`.

API references:
- [SelectEntity](https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IView~SelectEntity.html)
- [CreateUnfoldedViewAt3](https://help.solidworks.com/2024/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.idrawingdoc~createunfoldedviewat3.html)
- [InsertCenterLine2](https://help.solidworks.com/2025/English/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IDrawingDoc~InsertCenterLine2.html)
