# SolidWorksAgent development notes

- Keep all SolidWorks COM calls inside `src/SolidWorksAgent.SwApi`.
- Public dimensions use millimetres; convert to metres only at the COM boundary.
- Never overwrite an existing CAD file unless the caller explicitly passes `--overwrite`.
- Every feature operation must rebuild and validate the resulting geometry.
- CLI commands must support machine-readable JSON where applicable.
- Default to one drawing sheet per part. Arrange necessary views and dimensions on that sheet while preserving legibility. Clearly communicate any necessary exception and the sheet count.
- After generation, keep the saved final CAD output open and close input/intermediate documents opened by the command, including unsaved failed trials. Do not leave trial documents open for inspection. Protect preexisting user documents and unsaved user changes; closing a document does not authorize deleting its disk file.
