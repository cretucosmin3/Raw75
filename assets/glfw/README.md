# GLFW for Linux

`libglfw.so.3.3` is GLFW 3.3.0 (X11), the same binary Blossom vendors in its repository (`glfw/`) and TGK ships.
Silk.NET loads `libglfw.so.3.3` before `libglfw.so.3`; without this file it falls back to the GLFW 3.4 build from its
native package, which runs as a native Wayland client, and Wayland ignores the window icon Blossom sets, so the title
bar shows a generic one. The build copies this file next to the executable (linux-x64; there is no arm64 build).
On a Wayland session it runs through XWayland. GLFW is licensed under the [zlib license](https://www.glfw.org/license.html).
