# Windows release packaging

The default release remains self-contained for Windows x64. The .NET publish
profile already enables single-file compression, native-library extraction and
no ReadyToRun image. It does not enable trimming, AOT, an external packer or a
separate installed runtime requirement. Those are not needed to remove demo
code or debug symbols.

## Build and assemble

From the repository root, use the SDK selected by global.json and restore locked
dependencies before publishing. Build the helper separately:

```powershell
cargo build --manifest-path native/desk-puff-ble/Cargo.toml --release --locked
dotnet restore ./desk_Puff.slnx --locked-mode
dotnet build ./desk_Puff.slnx -c Release --no-restore
dotnet test ./desk_Puff.slnx -c Release --no-build --no-restore
dotnet format ./desk_Puff.slnx --verify-no-changes --no-restore
dotnet publish ./src/DeskPuff.App/DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64
$runtimeLicenses = Get-ChildItem '.nuget/packages/microsoft.netcore.app.runtime.win-x64' -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
./tools/release/Assemble-Release.ps1 `
    -PublishDirectory 'artifacts/publish/win-x64' `
    -HelperPath 'native/desk-puff-ble/target/release/desk-puff-ble.exe' `
    -OutputDirectory 'artifacts/release/win-x64' `
    -RuntimeLicenseDirectory $runtimeLicenses.FullName
```

If the NuGet package cache is overridden, pass the runtime package's actual
version directory. The assembler also reads available license texts and package
attribution for the application's locked dependencies from that same cache.
Choose a new output path for later builds; existing output directories/ZIPs are
refused rather than deleted. CI uses this same script after its build/test gate.

## Package contents

- Published runtime files, including any accompanying runtime dependencies.
- The required helper at `ble/desk-puff-ble.exe`; an existing helper in the input
  must match the supplied one.
- Project LICENSE.md, NOTICE and THIRD_PARTY_NOTICES.md without modification.
- Bundled .NET runtime license/notices and supplied locked-dependency attribution.
- `release-manifest.json`: included files, byte sizes, SHA-256 hashes, reasons,
  and excluded symbol/diagnostic/developer-tool filenames.

Debug symbols (.pdb), logs, test results and developer capture scripts are not
needed for the runnable release. The opt-in capture tool remains in source at
`tools/capture`; it is not removed from the repository. Unexpected JSON in the
publish input is rejected, so local heating profiles/captures are not packaged.
No cache, user preferences, source directory or previous package is deleted.

The script creates `<OutputDirectory>.zip` using supported ZIP compression and
prints its SHA-256 and size. Extract the full archive, retaining `ble` and
`licenses`, before running desk_Puff.exe. The installed local launch path remains
`artifacts/publish/win-x64/desk_Puff.exe`; rebuilding source alone does not update
an executable copied elsewhere.

## Source and evidence

The shipping application has no simulator or `--demo` mode. Tests retain an
injected simulator under `tests/DeskPuff.App.Tests/Support`, isolated profile and
preferences storage, and actual-window rendering. The four README screenshots
are generated through those tests. The removed flag is rejected before app or
Bluetooth initialization, so an old demo shortcut does not fall back to a real
connection.

Commit source, tests, build/release scripts and documentation. Build output,
SDK/package caches, TestResults and local captures remain ignored. Dated protocol evidence and WPF reference text are retained for reference,
not as shipping implementations. Current architecture and runtime notes
describe the supported behavior.
