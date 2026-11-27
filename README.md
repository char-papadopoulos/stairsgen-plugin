# Stairs for Rhino 8 / 9

**Version 1.5.0**

Draw a curve, set the height, and StairsGen builds the whole staircase: steps,
structure, step numbers and railings, with a live preview. Every part is also
a Grasshopper component.

![Showcase](images/demo.gif)

## Features

- **Any flat curve**: lines, arcs, freeform curves, polylines. The curve can be
  the stair's middle, inner edge or outer edge.
- **Riser and tread, as in Revit.** **Fit to Curve** sizes the treads to fill
  the curve; typing a depth fixes it.
- **Landings at corners** (on by default). The two corners of a U-turn share
  one landing. **Pick Corners…** switches any corner to winders.
- **Winders** that never overlap, at any angle, including tight smooth bends.
- **Sharp or rounded** corners under 60°.
- **Top landing** or **leave unused** when the curve is longer than the stair.
- **Autoconfigure** from the total height alone, by the comfort rule
  (riser + tread ≈ 46 cm / 18").
- **Slab or floating planks**, rest steps anywhere, pyramid (narrowing) width.
- **Railings**: glass, metal, or concrete with balusters.
- **Metric or imperial**: fields read and show any unit (`7in`, `18cm`, `6'-8"`).
- **Follows the curve** while the dialog is open.
- **Grasshopper components** for every part, in the same package.

| Winders | Curved |
|:---:|:---:|
| ![A concrete stair with winders around a 90 degree corner and a metal railing on the outer side](images/winders.png) | ![A curved concrete stair with metal railings on both sides](images/curved.png) |
| **Autoconfigured** | **Pyramid width** |
| ![A stair with two sharp corners, autoconfigured with a square landing in each corner](images/autoconfigure.png) | ![A concrete stair whose steps narrow toward the top, with railings following the edges](images/pyramid.png) |
| **Concrete railings** | **Floating planks** |
| ![A curved stair with concrete walls and metal balusters up to the handrail](images/concrete.png) | ![Floating plank treads along a curve, with a metal railing](images/planks.png) |

## Install

**Package Manager** (easiest): run `PackageManager`, search **StairsGen**,
Install. One package installs the Rhino command and the Grasshopper components.

Or download `Stairs-<version>.zip` from
[Releases](https://github.com/char-papadopoulos/stairsgen-plugin/releases) and
follow its `INSTALL.txt`: `Stairs.rhp` goes to Rhino's plug-ins, `StairsGen.gha`
to Grasshopper's Libraries folder.

## Usage

Run `StairsGen` and click points along the stair (Enter to finish), or use
**SelectCurve** for an existing curve. Adjust the settings, then click
**Create Stairs**.

## Grasshopper

The **StairsGen** tab has one component per part. A **Stair** flows between
them, so each part can be changed on its own:

| Component | Does |
|---|---|
| **Stair** | Curve, height, riser, tread (empty = fit to curve), width, role, flip |
| **Stair Landings** | Landings or winders at corners, rest steps, extra curve, rounded corners |
| **Stair Pyramid** | Each step narrower than the one below |
| **Stair Autoconfigure** | Steps, rests and tread from the height alone |
| **Stair Structure** | Slab or planks, as solids |
| **Stair Railing** | Glass, metal or concrete railings |
| **Step Numbers** | Text and planes, flat on each tread |
| **Deconstruct Stair** | Each step's outline, height, depth, landing; warnings |

Empty lengths use round defaults in the model's units (17 cm or 7" risers).

## Build

Needs the .NET SDK 7 or later.

```bash
tools/package.sh
```

Builds `Stairs.rhp` and `StairsGen.gha` into `dist/`: a zip with both, and a
`.yak` Package Manager package with both (`yak push` publishes it).

## Tests

With Rhino running and `StartScriptServer` on:

```bash
tests/run_tests.sh
```

It runs every scenario in metres, millimetres and inches, and checks for valid
solids, treads that tile without gaps or overlaps, and the expected warnings.
`tests/run_tests.sh "" corner` runs only scenarios named with "corner".

## License

[MIT](LICENSE). RhinoCommon (McNeel) and Eto.Forms (BSD-3-Clause) are not
included.
