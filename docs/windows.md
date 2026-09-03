# Building and running NInfer on Windows

This guide covers native Windows 11 x64 source builds of NInfer for an NVIDIA GeForce RTX 5090
(`sm_120a`). There is no prebuilt Windows distribution: the same `.ninfer` artifacts, CLI, and
HTTP server options apply as on Linux. See the [project README](../README.md), the
[CLI guide](cli.md), and [HTTP serving](serving.md) for model downloads, CLI options, and the
serving API.

## Requirements

- Windows 11 x64;
- NVIDIA GeForce RTX 5090 with a driver supporting CUDA 13.1;
- [CUDA Toolkit 13.1](https://developer.nvidia.com/cuda-downloads) or newer;
- Visual Studio 2022 with the **Desktop development with C++** workload;
- CMake 3.28 or newer;
- [vcpkg](https://github.com/microsoft/vcpkg); the repository pins the dependency baseline in
  `vcpkg.json`.

The build rejects CUDA architectures other than `120a`, matching the upstream RTX 5090 target.
On Windows, FFmpeg and libcurl come from vcpkg during configure; no system package installation
is required. CUDA 13.1 uses MSVC's conforming preprocessor automatically.

## Installing vcpkg

```powershell
git clone https://github.com/microsoft/vcpkg C:\src\vcpkg
C:\src\vcpkg\bootstrap-vcpkg.bat
```

With the vcpkg toolchain file passed to CMake (below), vcpkg installs `curl`, `ffmpeg` (with the
`zlib` feature), and `pkgconf` into the build directory's `vcpkg_installed/` tree, following the
`vcpkg.json` manifest. `vcpkg_installed/` is git-ignored.

## Building from source

Run every command below from the **x64 Native Tools Command Prompt for VS 2022** (or a shell that
has sourced `VsDevCmd.bat -arch=x64 -host_arch=x64`). The Ninja generator does not set up the MSVC
environment on its own: without `INCLUDE` and `LIB`, `cl.exe` fails to find `corecrt.h` and
`link.exe` fails to find `secur32.lib`.

```powershell
git clone https://github.com/natpate/ninfer-windows.git
cd ninfer-windows

cmake -S . -B build-ninja -G "Ninja Multi-Config" `
  -DCMAKE_CUDA_COMPILER="C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.3/bin/nvcc.exe" `
  -DCMAKE_TOOLCHAIN_FILE=C:/src/vcpkg/scripts/buildsystems/vcpkg.cmake `
  -DVCPKG_TARGET_TRIPLET=x64-windows
cmake --build build-ninja --config Release --parallel
```

Point `-DCMAKE_CUDA_COMPILER` at the toolkit you intend to build with. With several toolkits
installed, CMake and the MSBuild CUDA integration can settle on different ones, and the build then
fails with `CUDA compiler and CUDA toolkit headers are incompatible` on the translation units that
include CCCL headers. Naming `nvcc` explicitly keeps the compiler and its headers in the same
toolkit. If `ninja.exe` is not on `PATH`, add
`-DCMAKE_MAKE_PROGRAM="<VS>/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe"`.

The Visual Studio generator also works, but on solution-wide builds the CUDA custom build step can
leave the MSBuild nodes without `INCLUDE`, and unrelated C++ projects then fail with spurious
`C1083` errors for system headers such as `crtdbg.h`. The failing set changes from run to run.
Prefer Ninja.

The default configuration builds:

```text
build-ninja/apps/Release/ninfer.exe
build-ninja/apps/Release/ninfer-serve.exe
```

Tests, benchmarks, and maintainer tools are excluded from the default build, as on Linux.
The release binaries use the FFmpeg, libcurl, zlib, and Winsock DLLs from the
`build-ninja/` vcpkg output tree; the build copies them next to the executables. The CUDA runtime is linked statically, so no `cudart*.dll` is required from the
toolkit.

## NInfer Control (WinUI 3)

The repository also contains a native Windows UI for configuring and launching `ninfer-serve`:

```powershell
dotnet build apps\ninfer-control\NInferControl.csproj -c Release -p:Platform=x64
dotnet run --project apps\ninfer-control\NInferControl.csproj -c Release -p:Platform=x64 --no-build
```

The app opens a file picker for `.ninfer` artifacts, detects
`build-ninja\apps\Release\ninfer-serve.exe` when launched from the checkout, exposes the HTTP,
context, KV, concurrency, speculative, Vision, thinking, and CORS options, and streams the child
server's stdout/stderr into the operation log. The generated command can be copied before starting.
The operation card can be expanded or collapsed, and the same live log can be opened in a separate
window for monitoring while working with the configuration.
It uses the Windows App SDK through the single-project MSIX/WinUI 3 template and requires the .NET
SDK plus the Windows App SDK packages restored by NuGet.

Generate the portable x64 Release (no installer, MSIX, certificate, or administrator
permission) with:

```powershell
.\apps\ninfer-control\build-portable-release.ps1
```

The script builds the native server first, publishes the app, and then copies `ninfer-serve.exe`,
`ninfer.exe`, and their DLLs next to `NInferControl.exe`. A bundled server takes precedence over a
path saved from an earlier session, so the extracted package runs without pointing at a server by
hand. Run it from a developer prompt, as above; `-SkipServerBuild` reuses an existing build tree,
`-ServerBuildDirectory`, `-VcpkgRoot`, and `-CudaRoot` override the defaults, and `-SkipZip` leaves
the folder without archiving it. Close any `NInferControl.exe` running from the output folder
first, or the cleanup step fails.

Distribute `dist\NInferControl-Portable-x64.zip`. After extracting it, run
`NInferControl.exe` directly.

The MSIX variant is available only for environments that can trust a local signing
certificate:

```powershell
.\apps\ninfer-control\build-release.ps1
```

The resulting package is under `dist\ninfer-control-release\`. Distribute
`dist\NInferControl-Control-Release-x64.zip`; after extracting it, run
`Install-Release.ps1`. It requests administrator permission, trusts the local
test signing certificate for the machine, and then installs the MSIX.

## Releases from CI

`.github/workflows/windows-release.yml` reproduces the build above on a `windows-2022` runner: it
sets up the MSVC environment, installs the CUDA Toolkit, configures with Ninja Multi-Config, builds
the server, and then runs `build-portable-release.ps1` with `-SkipServerBuild` so the packaged app
carries the server that job produced. The runner needs no NVIDIA hardware; `nvcc` emits `sm_120a`
without a GPU present.

Two archives are produced, uploaded as workflow artifacts and attached to the release:

```text
dist/NInferControl-Portable-x64.zip   app plus ninfer-serve.exe, ninfer.exe, and their DLLs
dist/ninfer-server-x64.zip            server binaries only
```

Pushing a `v*` tag creates a draft GitHub Release. `workflow_dispatch` runs the same build on
demand, takes the CUDA version as an input, and only publishes a release when asked. vcpkg builds
FFmpeg from source, so the first run is long; later runs reuse the GitHub Actions binary cache.

## Running the CLI

Download an artifact as described in the [project README](../README.md), then:

```powershell
.\build-windows\apps\Release\ninfer.exe models\qwen3_6_27b.ninfer `
  --prompt "Explain prefill and decode in three sentences." `
  --max-context 16384 `
  --max-new 256 `
  --spec mtp --draft-tokens 3 `
  --lm-head-draft
```

Answer content is written to stdout; loading progress, reasoning, timing, throughput, memory, and
speculative-decoding statistics are written to stderr, exactly as on Linux.

## Running the HTTP server

```powershell
.\build-windows\apps\Release\ninfer-serve.exe models\qwen3_6_27b.ninfer `
  --max-context 16384 `
  --kv-capacity auto `
  --max-concurrency 2 `
  --spec mtp --draft-tokens 3 `
  --lm-head-draft
```

The API is then available at `http://127.0.0.1:8080/v1`. To listen on the network instead of
localhost, pass `--host 0.0.0.0` and allow TCP 8080 through Windows Firewall for the
`ninfer-serve.exe` process.

## Notes and differences from Linux

- Windows uses the Visual Studio generator in the examples above; Ninja Multi-Conf is also
  supported if installed. The build tree layout for multi-config generators is
  `build-windows/apps/Release/`.
- On Windows the CUDA runtime is linked statically (`CUDA::cudart_static`) and the project forces
  a single MSVC runtime library across the CUDA static runtime and the vcpkg dependencies.
- The artifact reader uses memory-mapped files plus unbuffered overlapped reads on Windows and
  `O_DIRECT`/`pread` on POSIX; the 4096-byte alignment contract is identical.
- `ninfer.exe` and `ninfer-serve.exe` are the only required outputs; the Docker path in the
  [project README](../README.md) remains Linux-only.
