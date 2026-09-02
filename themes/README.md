# The theme bundle

These are the themes that are **not** in the installer.

Idle Master ships three looks inside the exe — Minimalistic, Terminal and
Cortex — and stops there on purpose: the download stays one small file and
every update stays quick, instead of carrying a gallery most people never
open. Everything in this folder is fetched only if somebody asks for it, by
pressing **Get more themes** in the picker.

## How they get to people

They are published as `IdleMasterThemes.zip` on the same GitHub release as the
installer. The app pulls that asset, reads only the `.imtheme` entries in it,
and unpacks them into `themes\` next to the exe. Nothing else in the zip is
read, and a file that will not parse is deleted again rather than left sitting
in the picker doing nothing.

## Adding yours

Drop a `.imtheme` file here and open a pull request. That is the whole process.
A theme is text — nineteen colours, two font families and six keys for shape —
so there is nothing to review but taste and contrast.

Two things worth getting right:

- **`ready=`** is the corner arrow that means *a new release is waiting*, and
  nothing else in the app is allowed to wear it. If your accent is green, your
  `ready` must not be, or the one message people need to notice disappears.
- **`onaccent=`** is the writing on top of the BOOST and IDLE slabs. A light
  theme almost always needs it dark; the default is white.

Every shape key (`radius`, `gradient`, `glow`, `borderwidth`, `chrome`)
defaults to off, and off means the app paints the way it always has. You only
opt in to the new drawing by naming a number.
