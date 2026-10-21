# UI previews

These 460 by 760 images show the production window with an explicitly injected
test-only simulator. Names, colors, battery and state are fixture data. They do
not demonstrate a physical device connection or hardware validation.

| file | source and content |
|---|---|
| home.png | InterfaceRenderTests: connected idle fixture, session circle, boosts and profile strip |
| profiles.png | NamedVaporRenderTests: local draft with an explicit named vapor choice |
| color.png | InterfaceRenderTests: test colorway and custom wheel |
| settings.png | InterfaceRenderTests: fixture controls and settings |

Tests render through Avalonia's headless Skia backend, inspect nonblank/distinct
frames and check control layout. Refresh these checked-in previews after UI
changes; direct custom runners may place generated media under their own output
root. Rendering results can vary with backend/fonts, so byte-identical files
across all machines are not a requirement.

No image may contain a real serial number, Bluetooth address or other private
device identifier. The simulator is compiled only into test support and is not
available through the shipping executable.
