# Changelog

## 1.3.0

- Plug-in Manager description now lists the actual functionality instead of the
  original one-line summary. The text is set with an explicit
  `[assembly: AssemblyDescription]` rather than the `.csproj` `Description`
  property, which did not reach Rhino reliably.
- Version and description consolidated into `PlugInInfo`, so the Plug-in Manager
  entry, the command banner and the version number cannot drift apart.

## 1.2.0

- `CutDirection` gains `Mixed` (now the default): chord-perpendicular over the
  aerodynamic span, parallel to world Z over the root end, with a smoothstep
  blend across `TransitionBand` so the lofted surface has no crease.
- `TransitionRR`, `TransitionBand` and `WorldZSide` control the changeover.
- Pure `WorldZ` and `PerpendicularToChord` modes remain available.

## 1.1.0

- Spar cutting surface at a chord fraction `x/c` from the leading edge, with the
  same distribution forms as the edges. Off by default.
- `Extension` overhang at both ends of every cut line, with `ExtensionBasis`
  selecting whether the percentage is of the local chord or the cut line length.
- LE/TE nose regions baked as planar surfaces on `CUT_LE_AREAS` and
  `CUT_TE_AREAS`, so `_Area` on one reproduces the reported value.

## 1.0.0

- First version. LE and TE cutting surfaces from a blade solid, solved to a
  target cross-sectional area by bisection.
- Target area variable with `r/R`: constant, linear, power or table, set
  independently for the leading and trailing edge.
- Span axis, LE/TE identification and station range derived automatically, with
  overrides where the heuristics can go wrong.
- Per-station report of `r/R`, target area, achieved area and solved offset.

---

Bumping the version touches three places: `Version`, `AssemblyVersion` and
`FileVersion` in `MejzlikEdgeCut.csproj`; `PlugInInfo.Version` and
`PlugInInfo.Description` in `MejzlikEdgeCutPlugIn.cs`; and this file.

`PlugInInfo.Description` is what Rhino's Plug-in Manager displays, so describe
the release there, not just here.

Rhino may hold the old description until the plug-in is reloaded. If the text
looks stale after a rebuild, restart Rhino, and if it persists, uninstall the
plug-in in the Plug-in Manager and re-install the new `.rhp`.
