# MejzlikEdgeCut — Rhino 7 plug-in

Version 1.3.0. See `CHANGELOG.md` for what changed.

Generates constant-area LE and TE cutting surfaces from a propeller blade solid.
Command name in Rhino: `MejzlikEdgeCut`.

## Files

| File | Purpose |
|---|---|
| `CHANGELOG.md` | Version history; the `.csproj` description is kept in step with it |
| `MejzlikEdgeCut.csproj` | SDK-style project, targets `net48`, output extension `.rhp` |
| `MejzlikEdgeCutPlugIn.cs` | Plug-in entry point, persistent settings |
| `MejzlikEdgeCutCommand.cs` | The command: object picking, distribution entry, layer baking, reporting |
| `AreaDistribution.cs` | Target area as a function of r/R — constant, linear, power, or table |
| `EdgeCutCore.cs` | All geometry. No document or UI dependencies, so it can be reused from Grasshopper or unit tests |

## Before you build

Two things must be changed:

**Generate a new plug-in GUID.** `MejzlikEdgeCutPlugIn.cs` contains a placeholder
`[assembly: Guid(...)]`. `build.cmd` replaces it automatically on the first run,
so this is only manual work if you build without the script. Shipping the
placeholder risks a collision with another plug-in.

**Check the RhinoCommon package version.** The `csproj` pins an example 7.x
version that I have not verified. Look up the newest `7.*` release on
nuget.org/packages/RhinoCommon and use that. Do not use an 8.x package — the
plug-in will not load in Rhino 7.

## Area distribution

The target cross-sectional area varies with `r/R`, and the leading and trailing
edge each carry their own distribution. Four forms are available:

| Form | Parameters | Shape |
|---|---|---|
| `Constant` | one area | same at every station, the original behaviour |
| `Linear` | root area, tip area | straight line in `r/R` |
| `Power` | root area, tip area, exponent | `A = A_tip + (A_root − A_tip)·(1 − r/R)^n`; `n = 1` is linear, higher `n` holds the root value further outboard |
| `Table` | control points | piecewise linear through `rR:area` pairs, held flat outside the first and last point |

Table entry is free text on the command line, for example
`0.2:12.0, 0.5:9.5, 0.8:6.0, 1.0:3.5`. Use a dot as the decimal separator
regardless of your Windows locale. Values are areas in the document's units
squared.

`CopyLeToTe` duplicates the LE distribution onto the TE if you want them to
start the same and then diverge.

**How `r/R` is computed.** `RadiusDatum` controls where `r = 0` sits:

- `WorldOrigin` (default) — `r` is the span-axis coordinate measured from the
  world origin, so this is a true radius ratio only if the blade is modelled in
  its as-mounted position with the hub axis through the origin. `R` is taken as
  the span coordinate of the blade tip.
- `BladeRoot` — `r` is measured from the blade's own root end. Then `r/R` means
  "fraction of the modelled blade length", not true radius ratio. Use this only
  when the blade is modelled away from the hub axis.

The command prints which datum it used and the resulting `R` before it runs, and
warns if the blade root sits exactly at the origin under `WorldOrigin` — that
usually means the model is not in its as-mounted position and `r/R` will silently
mean span fraction. Check this line before trusting the output.

Every station reports its `r/R`, its target area, the area actually achieved, and
the solved offset, so you can verify the distribution came out as intended.

## Spar surface

`Spar` generates a third cutting surface at a chord fraction `x/c` measured from
the leading edge. It uses the same four distribution forms, but the values are
fractions in (0,1) rather than areas — `SparChordDistribution` prompts and
validates accordingly, and a table looks like `0.2:0.35, 0.6:0.30, 1.0:0.25`.
Output goes to `CUT_SPAR_SURFACE`. It is off by default.

**One structural difference from the edge surfaces, worth understanding.** The LE
and TE surfaces work in planes normal to the LE and TE curves, because that is
where your manual polygons are drawn. The spar works in the spanwise station
planes — the true airfoil sections — because a chord fraction is only meaningful
there. At each station the chord runs LE to TE, the point sits at `x/c` along it,
and the cut line is perpendicular to the chord, trimmed to the section.

If you wanted the spar cut taken in LE-normal planes instead, say so; it is a
small change but it produces a different surface, so I did not guess.

## Cut direction

`CutDirection` sets which way the cut lines run inside each section plane:

- `Mixed` (default) — chord-perpendicular over the aerodynamic part of the blade,
  parallel to world Z over the other part.
- `WorldZ` — vertical everywhere.
- `PerpendicularToChord` — tilted with the twist everywhere, as the earliest
  versions did.

`Mixed` is controlled by three parameters:

- `TransitionRR` — the `r/R` where the changeover sits (default 0.25).
- `WorldZSide` — which side of it gets world Z. `Inboard` (default) puts the
  vertical cut at the root end and keeps the chord-normal cut over the aerodynamic
  span; `Outboard` swaps them.
- `TransitionBand` — width in `r/R` over which the direction rotates, centred on
  `TransitionRR` (default 0.10). The rotation uses a smoothstep, so the lofted
  surface has no crease where the two regimes meet. Set it to `0` for a hard switch
  if you actually want the crease.

The cut is still *positioned* along the chord in every mode — the LE/TE offset is
solved as a distance from the edge point along the chord line, and the spar sits at
`x/c` from the LE. Only the direction the line runs changes.

Changing direction changes the LE and TE areas, since a vertical cut encloses a
different region than a chord-perpendicular one at the same offset. The solver
accounts for it by landing on a different offset to hit the same target area, so the
areas stay correct but the reported `d` values shift.

If world Z happens to lie nearly normal to a section plane, or its projection lands
nearly along the chord, a Z-parallel cut cannot cross the section cleanly; that
station falls back to the chord perpendicular.

## Surface extension

Every cut line is lengthened at both ends so the lofted surface stands clear of
the upper and lower blade surfaces instead of ending exactly on them, where a trim
or split is unreliable. Two settings control it, and they apply to the LE, TE and
spar surfaces alike:

- `Extension` — the fraction added at *each* end. Default `0.10`, accepts up to `5.0`.
- `ExtensionBasis` — what that fraction is measured against:
  - `LocalChord` (default) — a blade is much wider than it is thick, so 10% of chord
    gives a clearly visible overhang past both surfaces.
  - `CutLineLength` — the cut line's own length, which equals the local blade
    thickness. 10% here is only a millimetre or two.

Both scale per station, so the overhang stays proportional from root to tip rather
than being a fixed distance that is too small at one end and too large at the other.
Set `Extension` to `0` for lines that stop at the surface.

The extension affects only the cut lines and the surfaces lofted from them. The
baked area regions stay exactly as measured, so `_Area` on one of them still
reproduces the number in the report.

## Build

Double-click `build.cmd`. It generates a unique plug-in GUID on the first run
(replacing the placeholder), finds either the dotnet CLI or Visual Studio's
MSBuild, restores, builds Release, and opens Explorer with the resulting `.rhp`
selected. On failure it prints the causes worth checking first.

Manual equivalent:

```
dotnet restore
dotnet build -c Release
```

Output: `bin\Release\MejzlikEdgeCut.rhp`. Drag that file onto an open Rhino 7
window to install, or use `_PlugInManager` ▸ Install.

For debugging, set the project's start action to the Rhino executable
(`C:\Program Files\Rhino 7\System\Rhino.exe`) and attach; breakpoints in
`EdgeCutCore` will hit.

## Command options

After you pick the blade, an option loop appears. Set what you need, then press
Enter or pick `Run`. All settings persist between sessions.

- `LeAreaDistribution` / `TeAreaDistribution` — set each edge's area law (see above)
- `CopyLeToTe` — duplicate the LE distribution onto the TE
- `RadiusDatum` — `WorldOrigin` or `BladeRoot`
- `Stations` — number of spanwise stations (default 20)
- `Extension` — overhang at each end of every cut line (default 0.10)
- `ExtensionBasis` — `LocalChord` (default) or `CutLineLength`
- `CutDirection` — `Mixed` (default), `WorldZ` or `PerpendicularToChord`
- `TransitionRR`, `TransitionBand`, `WorldZSide` — the Mixed changeover
- `RootFraction` — fraction of span skipped at the root, to clear the hub fillet (default 0.15)
- `TipFraction` — fraction of span where stations stop (default 0.97)
- `SwapLeTe` — flip the LE/TE identification if the heuristic guesses backwards
- `LeadingEdge` / `TrailingEdge` / `Spar` — which surfaces to generate
- `BakeCutLines`, `BakeAreaRegions`, `BakeEdgeCurves` — also bake the intermediate geometry, useful for checking

Output layers: `CUT_LE_SURFACE`, `CUT_TE_SURFACE`, `CUT_SPAR_SURFACE`,
`CUT_LE_AREAS`, `CUT_TE_AREAS`, `CUT_LINES`, `DERIVED_EDGES`.

`CUT_LE_AREAS` and `CUT_TE_AREAS` hold the closed nose regions the bisection
measured against, baked as planar surfaces. Select one and run `_Area` to check
it against that station's reported value.

Stations that could not converge are listed with the reason rather than silently
approximated.

## Status

Untested. I have no Rhino installation, so this has never been compiled or run.
Expect to fix compile errors on the first build — RhinoCommon overload
signatures are the likely culprits, particularly the `GetNumber` option calls
and `Curve.Split`.

The geometry logic is a direct port of the Python script and carries the same
unverified assumptions:

- The span axis is the longest world bounding-box axis. A blade not aligned to a
  world axis will produce skewed stations. `EdgeCutOptions.SpanAxisOverride`
  exists but is not yet exposed as a command option.
- The chord is the longest point-pair on each section; the blunter end is called
  the leading edge. A heuristic, not a proof.
- The nose polygon is: the section curve from the edge point, closed by a single
  straight line perpendicular to the local chord. If your manual polygon is a
  triangle or a four-sided wedge instead, `EdgeCutCore.NoseRegion` is the one
  function to rewrite — everything downstream is independent of that choice.
- Tip stations whose whole section area falls below the target cannot converge.
  They are skipped, so the loft ends short of the tip. A decreasing distribution
  toward the tip largely removes this problem, which is one reason to use one.

Validate the reported areas and the surface position against a blade you have
already cut by hand before using this on live work.
