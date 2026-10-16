# SolidWorksAgent development notes

- Keep all SolidWorks COM calls inside `src/SolidWorksAgent.SwApi`.
- Public dimensions use millimetres; convert to metres only at the COM boundary.
- Never overwrite an existing CAD file unless the caller explicitly passes `--overwrite`.
- Every feature operation must rebuild and validate the resulting geometry.
- By default, fully define every sketch created or edited as part of the requested task, including sketches used by generated features, without waiting for a separate user instruction. Do not modify unrelated preexisting sketches.
- Use meaningful, editable driving dimensions and geometric relations that express the design intent. Matching coordinates or sizes alone is not sufficient. Do not use blanket Fix/Block constraints merely to achieve fully defined status, or duplicate dimensions/relations that already exist.
- After sketch changes, rebuild and inspect the SOLIDWORKS solver state: verify that the sketch is fully defined and has no conflicting or over-defining dimensions/relations. Preserve intended geometry; when constraining an existing sketch, do not resize or move it unless requested. Diagnose and resolve remaining degrees of freedom using available tools before reporting completion. If verification or full definition cannot be achieved, clearly report the remaining issue instead of claiming completion.
- Explicit user instructions take precedence. Leave degrees of freedom only when the user requests it or the requested design requires them (for example, an intentionally movable layout); explain which degrees of freedom remain and why. Ask only when missing design intent prevents a sound choice.
- CLI commands must support machine-readable JSON where applicable.
- Default to one drawing sheet per part. Arrange necessary views and dimensions on that sheet while preserving legibility. Clearly communicate any necessary exception and the sheet count.
- After generation, keep the saved final CAD output open and close input/intermediate documents opened by the command, including unsaved failed trials. Do not leave trial documents open for inspection. Protect preexisting user documents and unsaved user changes; closing a document does not authorize deleting its disk file.
