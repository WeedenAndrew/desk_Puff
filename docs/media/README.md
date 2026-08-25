# Media

Screenshots referenced by the repository README.

| file | what it must show |
|---|---|
| `home.png` | Home, connected and idle, with the session circle, the boost buttons, and the profile strip visible |
| `profiles.png` | the Profiles page with the editor populated |
| `color.png` | the Color page with a colorway loaded |
| `settings.png` | the Settings page |

These are not captured by hand. `InterfaceRenderTests` renders the real window
offscreen through Avalonia's headless Skia backend, at the window's own 460 by
760, driving the same view model and demo client that `--demo` uses, and writes
the four files here. Running the test suite regenerates them, so an image can
never quietly stop matching the build. The output is byte-identical between
runs, so a regenerated image is not a spurious change.

The same test asserts each frame is not blank, that the middle of the session
circle is not empty, and that the four pages differ from one another. A frame
that failed to render fails the build rather than landing here as a picture of
nothing.

Nothing here may contain a serial number, Bluetooth address, or any other
device identifier. `--demo` invents its device, so a demo capture is safe by
construction.
