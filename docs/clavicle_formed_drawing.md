# CLAVICLE formed drawing

Input: `workspace/parts/PM20015-0A_CLAVICLE_copy.SLDPRT` (original part retained).
Output: `workspace/drawings/PM20015-0A_CLAVICLE_Formed_01.SLDDRW` and matching PDF in `workspace/exports`.

Two A2 sheets, 1:1 orthographic and auxiliary views, 1:2 reference isometric. Uses the existing formed configuration. No flat pattern, section, or model edits. Material 5052-H36 and 2 mm thickness were read from the part. JIS B 0001:2019 presentation; tolerances omitted as requested.

The dimension plan covers formed envelope, flange offsets and heights, thickness, 115 degree included bend angle, bend and profile radii, clearance-hole patterns, upright holes, platform holes, thread-hole locations, and reliefs. Inclined-tip hole pitch is measured in a true auxiliary projection, not its foreshortened orthographic projection. All ordinary dimensions select model edges/vertices through the drawing view and retain native SolidWorks associations. No numeric overrides are used.

Thread annotations are an explicit limitation: native Hole Wizard callouts in this installation lose variable text in PDF and include an unwanted thread class. The drawing therefore uses two edge-associated notes, generated from the Hole Wizard `hw-threaddesc` value M2x0.4. Their leaders follow the selected holes; changing the thread size requires regenerating these notes. They are not represented as automatically updating size dimensions.

`workspace/exports/Clavicle_dimension_plan.json` records selected edge indices, view, position, and expected value; `make_clavicle_plan.py` reconstructs the plan from geometry probes. Edge indices are specific to this model revision and must be re-inspected after topology changes. This is a reviewed part-specific workflow, not a general automatic sheet-metal dimensioning algorithm.

`SheetMetalDrawingBuilder` keeps COM calls in SwApi. The drawing command copies input to a new output, refuses an existing destination, checks dimension values and associations, rebuilds, saves, closes, reopens, and compares values and associations. PDF export preferences are restored after use. `PM20015-0A_CLAVICLE_Verification.json` contains the saved-document verification.

## Revision 02

Moved the side-tab hole and profile-radius dimensions from BACK to the lower-left FRONT view, in response to review. The FRONT view now contains the three visible through holes (diameter 3.2 mm, six total across the two sides), hole pitches and locations, R5 outer corners, R3.5 open notch, and R2 notch-entry rounds. FRONT and TOP were shifted together to make room; duplicate dimensions were removed from BACK. Native associations and values are revalidated after saving and reopening. Files: `PM20015-0A_CLAVICLE_Formed_02.SLDDRW`, matching PDF, and `PM20015-0A_CLAVICLE_Verification_02.json`.

Revision 03 adjusts the R2 notch-entry label to clear the existing inside-bend radius label. Final delivery uses `PM20015-0A_CLAVICLE_Formed_03.SLDDRW` and `.pdf`; verification is in `PM20015-0A_CLAVICLE_Verification_03.json`.

## Single-sheet delivery

User preference: normally one sheet per part (also recorded in AGENTS.md). `PM20015-0A_CLAVICLE_SingleSheet_02.SLDDRW` and its matching PDF consolidate the drawing onto one A2 sheet. The dimensioned LEFT and BOTTOM views are transferred with their native dimensions. The redundant, undimensioned BACK view and second sheet are removed. All 70 dimension values are compared before and after consolidation, and associations and tolerances are checked again after saving and reopening. The sheet contains seven views including the auxiliary and reference isometric views. Thread-note limitations described above still apply.
