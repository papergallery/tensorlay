# Contributing to TensorLay

Thanks for taking the time! This guide covers how to build the app, what to know about the codebase, and how to submit a change.

## Reporting bugs

Open an issue at <https://github.com/papergallery/tensorlay/issues>. Useful info to include:

- Windows version (10 / 11 + build number)
- GPU model + driver version
- Output of `.\TensorLay.exe 2>&1` from PowerShell when the app crashes
- Steps to reproduce

## Suggesting features

Open an issue and describe the use case. New AI services (a la SD Forge, Ollama) are especially welcome — the registry lives in [`app/TensorLay/ServiceRegistry.cs`](./app/TensorLay/ServiceRegistry.cs).

## Building from source

You need the **.NET 8 SDK**. Cross-compile from Linux to win-x64 works (`-p:EnableWindowsTargeting=true`).

```bash
cd app
dotnet build TensorLay/TensorLay.csproj -c Release -p:EnableWindowsTargeting=true
```

For a single-file self-contained release build:

```bash
cd app
dotnet publish TensorLay/TensorLay.csproj -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableWindowsTargeting=true
```

The output is `app/bin/Release/net8.0-windows/win-x64/publish/GpuHub.exe` (see _Internal naming_ below for why it's still `GpuHub.exe`).

The full release pipeline (`app/publish.sh`) additionally signs the binary with `osslsigncode`, builds an NSIS installer, signs it, and copies it to the deployment directory. The signing step requires the maintainer's private code-signing certificate which is not in the repo — comment out steps 3 and 5 of `publish.sh` for unsigned builds.

## Internal naming: GpuHub vs TensorLay

You'll see `GpuHub` in a few places where you'd expect `TensorLay`:

- `<AssemblyName>GpuHub</AssemblyName>` in `TensorLay.csproj` — the build output is `GpuHub.exe`.
- `AutoUpdater.cs` historical references in this file's history.

This is intentional. The project was originally called **GpuHub** and was rebranded to **TensorLay**. The auto-updater in already-installed v0.7.x clients downloads `GpuHub.exe` from a hard-coded URL. Renaming the assembly would break those clients' update path.

The NSIS installer (`installer.nsi`) renames the binary to `TensorLay.exe` when copying to `C:\Program Files\TensorLay\`. End users always see `TensorLay`. If you're contributing, just leave the `AssemblyName` alone.

This will be cleaned up once we're confident no v0.7.x clients are in the wild.

## Architecture overview

- **MVVM** via [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/windows/communitytoolkit/mvvm/introduction). ViewModels are in `app/TensorLay/ViewModels/`.
- **Services** (`app/TensorLay/Services/`) handle the platform integration: SSH (Renci.SshNet), nvidia-smi polling, HTTP health checks, the auto-updater, etc.
- **Models** (`app/TensorLay/Models/`) are plain DTOs serialized to JSON in `%APPDATA%\TensorLay\`.
- **Single-instance** is enforced via a named Mutex; `tensorlay://` deep links are forwarded to the running instance via a named pipe (see `App.xaml.cs`).
- **No internal cyclic deps**: ViewModels depend on Services, never the other way around.

## Submitting a pull request

1. Fork and create a feature branch.
2. Keep the PR focused on one thing. Big rewrites are hard to review.
3. Make sure `dotnet build` passes (the CI workflow runs this for you).
4. If you touch UI, attach a before/after screenshot in the PR description.
5. Don't bump `<Version>` in `csproj` — releases are cut by the maintainer.

That's it. We'll review and either merge or leave concrete feedback.
