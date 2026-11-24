to install, add https://raw.githubusercontent.com/Robert5204/FFLogsUploader/refs/heads/main/repo.json to your custom plugin repositories in dalamud

## Building

Install the .NET SDK used by Dalamud and a stable Rust toolchain. `dotnet build -c Release` builds the plugin and bundles the parser worker for the host platform. Set `DALAMUD_HOME` if your Dalamud assemblies are outside the SDK's default directory. The release workflow combines the four platform workers into one plugin ZIP.

Run the worker integration tests with `cargo test --locked --manifest-path worker/Cargo.toml`. On Windows, if the Cargo cache and checkout are on different drives, set `CARGO_TARGET_DIR` to a directory on the cache drive or enable Developer Mode. Released plugins include the worker and its V8 runtime.
