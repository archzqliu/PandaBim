# PandaBim PlanGen

PlanGen is a Rhino/Eto-based panel for generating building plan views from Rhino models. The tool persists panel layout and per-row settings, lets you manage level rows with elevation, cut height, absolute elevations, and plotting options, and can refresh related Wall2D content after plotting.

## Features
- **Persistent state**: Saves window size, position, base elevation, and row details (e.g., names, elevations, cut heights, lighting toggles, mode selection, parent layers, and bound model IDs) into the 3DM document for automatic restoration in later sessions. 【F:PlanGen-1.5.6.cs†L23-L159】
- **Level row editor**: Provides a dark-themed Eto panel with configurable columns for name, elevation, cut height, absolute elevation display, lighting toggle, edit/plot buttons, and mode selection per row. 【F:PlanGen-1.5.6.cs†L620-L760】
- **Wall cap generation**: Builds wall cap curves between paired wall lines for each axis, tagging results and refreshing views to keep plan geometry synchronized. 【F:PlanGen-1.5.6.cs†L20721-L20750】

## Getting Started
1. Open the `PlanGen-1.5.6.cs` script in Rhino (Rhino 7/8 with RhinoCommon and Eto.Forms available).
2. Run the script to launch the Plan Generation panel. Window size, location, and row data will restore automatically if present in the current document.
3. Add or edit level rows, configure elevations/modes, and use the plot controls to generate plan geometry. Wall caps can be created when matching wall lines are present.

## Notes
- The panel caches the last open document’s serial to avoid mixing state across Rhino documents. 【F:PlanGen-1.5.6.cs†L48-L55】
- Base elevation values are stored with five-decimal-meter precision for consistent absolute height calculations. 【F:PlanGen-1.5.6.cs†L43-L83】
