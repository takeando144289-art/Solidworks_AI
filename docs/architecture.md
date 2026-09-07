# Architecture

`SolidWorksAgent.Core` contains units, design inputs, output models, and validation and has no SolidWorks dependency.
`SolidWorksAgent.SwApi` owns all COM automation and presents task-level operations.
`SolidWorksAgent.Tools` is the initial AI-facing CLI. Dimensions at the CLI/Core boundary are millimetres; the API layer converts them to metres.

Assembly creation resolves mating entities by measured cylindrical diameter, maps part faces into assembly context with `IComponent2.GetCorrespondingEntity`, then creates mates. It does not depend on localized face or feature names.

Assembly analysis uses the interference manager for volumetric collision results, `IAssemblyDoc.GetBox` for approximate display bounds, and a mass-property object for mass, volume, area, and center of mass.

Drawing generation creates standard orthographic views, chooses the longitudinal view from measured view outlines, builds an A-A section, adds explicit overall/diameter dimensions and tolerance, writes manufacturing notes, and exports SLDDRW/PDF plus an optional bitmap for visual review.

The COM API is late-bound through the registered `SldWorks.Application` automation server. This avoids binding the agent to one locally installed SolidWorks interop assembly while still calling the official API.
