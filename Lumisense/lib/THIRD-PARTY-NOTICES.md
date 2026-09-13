# Third-party notices

## LAME (libmp3lame.dll)

This folder contains a compiled build of libmp3lame, the MP3 encoding
library from the LAME project, used by Lumisense as an MP3 encoder
independent of the operating system's own codecs (see
`LameMp3Encoder.cs`).

Lumisense loads this library dynamically via P/Invoke at runtime; it is
not statically linked into the application. LAME is licensed under the
GNU Lesser General Public License (LGPL) version 2.1 or later, which
permits this kind of dynamic linking without requiring Lumisense itself
to be licensed under the LGPL.

- Project homepage: https://lame.sourceforge.io/
- Full license text: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- Source code for the LAME project is available from the project
  homepage above; this repository does not modify LAME's source, only
  links against an unmodified build of it.

You may replace `lib/libmp3lame.dll` with a build of your own, or with
a newer official release, as long as it exposes the same `lame_*`
C API that `LameMp3Encoder.cs` calls into.
