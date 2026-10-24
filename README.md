# GPU Switcher

Put every program on the GPU it belongs on, on a multi-GPU Windows PC:

| Role  | Default GPU | What lands there |
|-------|-------------|------------------|
| **Games** | the discrete GPU that drives the primary monitor | anything in a Steam / Epic / GOG / Ubisoft / EA / Battle.net / Riot / Xbox library, game-engine or anti-cheat files next to the exe, launchers, OBS and in-game overlays |
| **Apps**  | a second discrete GPU, else the iGPU | browsers, Discord, Electron apps, terminals, everything else that draws |
| **Media** | the Intel iGPU (Quick Sync) | video players and transcoders: VLC, mpv, MPC, PotPlayer, HandBrake, ffmpeg… |

Give an old graphics card a second life: games keep the main GPU to themselves while browsers, chat and video run on the
other one. When a GPU appears or disappears, the roles are worked out again and every entry GPU Switcher wrote moves to
the new GPU automatically.

## Install

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
   Requires Windows 10 2004 or later, or Windows 11, x64.
2. Download `GpuSwitcher-<version>-win-x64.zip` from [Releases](https://github.com/PTHyperdrive/GpuSwitcher/releases) and
   extract it to a permanent folder, e.g. `C:\Tools\GpuSwitcher-1.0.0-win-x64`.
3. Run `GpuSwitcher.exe`. It starts routing right away and backs up your existing GPU preferences first.
4. Recommended: **Settings ▸ Boot guard ▸ Install** (one admin prompt) and tick **Start with Windows**.
   The boot guard routes every app from boot, before anything else starts.

The three executables are also attached to each release on their own. Each one carries the hook DLLs and unpacks them on
first use, so a single downloaded `GpuSwitcher.exe` works too. Installing the boot guard from a shared folder such as
Downloads copies the app to `Program Files\GpuSwitcher` first: the installer only ever locks down a folder that belongs
to GPU Switcher alone.

## How it routes (two layers)

1. **Windows' own per-app GPU preference.** It writes `SpecificAdapter=VEN&DEV&SUBSYS;GpuPreference=1073741824;` into
   `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, the same value Settings ▸ Display ▸ Graphics writes.
   DXGI reads it when an app creates its device, so it works for D3D10/11/12, and the Vulkan loader follows it too.
   Nothing is injected for this layer. It is applied the moment a new process appears and learned for any process seen
   using a GPU.
2. **In-process hook (apps and media only, never games).** `gshook64.dll` / `gshook32.dll` is loaded into the app with
   a remote `LoadLibraryW` thread. It swaps three pointers in DXGI's factory vtable
   (`EnumAdapters`, `EnumAdapters1`, `EnumAdapterByGpuPreference`) so the app sees its assigned GPU first
   (**Reorder**) or as the only hardware GPU (**Exclusive**, the default). No code bytes are patched.
   This catches apps that pick "the GPU with the most VRAM" themselves.
   A process is hooked the moment it loads `d3d11/d3d12/dxgi/vulkan-1/opengl32`, which is usually before it creates a device.

### Boot guard (Windows service): runs before anything else

`GpuSwitcherSvc.exe` is installed as **GPU Switcher Boot Guard** (auto-start, LocalSystem, High priority), so it is up before you log on.
- It runs a real-time ETW session on `Microsoft-Windows-Kernel-Process`: **process start** and **image load** events reach it
  within ~10 ms, with no polling. It routes a new process the moment it exists and hooks it the moment it loads
  `d3d11/dxgi/vulkan-1`. Measured: **10 of 10** never-seen apps routed and hooked on their first launch, the hook landing
  3–10 ms after routing; 32-bit apps are hooked across sessions too (`NtCreateThreadEx`).
- At logon it starts the tray app in your session (your normal token), ahead of the Run-key startup apps.
- Every read/write of your profile (config, `HKU\<SID>` preferences, event file) is done **impersonating your session
  token**, never as SYSTEM. Only opening and hooking processes uses SYSTEM rights, which is why elevated apps get hooked too.
- The install folder is locked to Administrators/SYSTEM (read-only for users and sandboxed apps), because a SYSTEM service
  loads its binaries from there.
- While the service runs, the tray app hands it launch handling and only learns, reports and restarts apps.
  Idle cost: service ~0.16%, tray ~0.4% of one core.
- Without the service, the tray app handles launches itself by polling every 50 ms.
- `gpusw service install|uninstall|status`, `gpusw autostart on|off`; log in `<install folder>\logs\service.log`.

### Games are never injected
A process is treated as a game, and then is never hooked and never opened with more than query rights, if **any** of these hold:
its exe is in a detected game library or a user-added game folder; a path segment is a publisher/launcher folder
(`steamapps`, `Riot Games`, `Ubisoft`, `Roblox`, `Epic Games`, `XboxGames`…); the exe or up to three parent folders contain
Steamworks / EOS / GOG Galaxy / Unity / Unreal / EasyAntiCheat / BattlEye files; it is on the game list (OBS, overlays);
or it is a known anti-cheat process. Services (session 0) and everything under `C:\Windows` are ignored entirely.
`gpusw inject` refuses game processes as well.

### Adding or changing cards
- Each card is identified by its VEN&DEV&SUBSYS key plus PCI slot (`D3DKMT` adapter address).
- A second card of any other model (AMD, or another NVIDIA) takes the Apps role automatically; Media stays on Intel.
- The Games card is *sticky*: a bigger new card, or moving the primary monitor, does not take the Games role away from it.
  Only removing it, or choosing explicitly in Settings, does.
- **Two identical cards** (same VEN/DEV/SUBSYS) are kept apart and labelled by slot ("RTX 2060 (bus 5)"). Windows'
  preference store can only name the model, so apps are pinned to the exact card by the hook (by LUID). Games get no
  entry at all and use Windows' default, the card driving the primary monitor, because a model-only entry could land
  a game on the wrong twin.
- Indirect-display adapters (e.g. Parsec's virtual display, which shows up in DXGI as a copy of the real GPU) are
  recognised (`D3DKMT_ADAPTERTYPE.IndirectDisplayDevice`) and never used as a target.

## Verified on a test machine (RTX 2060 + UHD 770)

| Probe | No entry | Registry → Intel | Hook only, Exclusive → Intel |
|---|---|---|---|
| `EnumAdapters1` | RTX first | Intel first | Intel only (+WARP) |
| D3D11 default device | RTX | **Intel** | **Intel** |
| D3D12 default device | RTX | **Intel** | **Intel** |
| Vulkan device order | RTX first | **Intel first** | **Intel first** (RTX still listed) |
| OpenGL renderer | RTX | RTX | RTX |

- Real app: a fresh Edge profile was routed at launch, its sandboxed GPU process was hooked, and the canvas animation ran
  on the UHD 770 (status "on target") while its window sat on an RTX monitor.
- In everyday use, with browsers and chat moved off the RTX 2060, a game ran about 26% faster and the browser no longer
  caused stutter.

## Limits (by design of Windows, not settings)

- **Already-running apps keep their GPU** until restarted: an app picks its adapter when it creates its device.
  The Apps tab marks them "⟳ restart to apply" and can close and relaunch them (graceful `WM_CLOSE` first).
- **OpenGL** always renders on the GPU that drives the window's monitor (`opengl32.dll` picks the ICD from the HDC).
  To move an OpenGL app, put its window on a monitor plugged into the GPU you want, or use the vendor control panel.
- **D3D9 / D3D9Ex** (e.g. WPF) adapters are monitors, so the same rule applies.
- In Exclusive mode, Vulkan still lists the other GPUs after the assigned one: apps that take device 0 move, and apps
  that insist on "discrete" may not.
- Apps that use CUDA or NVENC directly pick their NVIDIA device themselves.
- Heavy GPU *applications* (Blender, Resolve, Photoshop, CAD) are Apps by default. Right-click ▸ *Use the Games GPU*.

## Using it

- `GpuSwitcher.exe` opens the window; closing it hides to the tray. `--tray` starts hidden. Settings ▸ *Start with Windows*.
- **Apps tab**: what every process is assigned vs. what it is *actually* running on (from the same GPU counters Task Manager
  uses), GPU / video-engine load and memory. Right-click to change roles, restart, or exclude an app from the hook.
- **Rules tab**: every entry in the Windows store, who wrote it (GPU Switcher / you in Settings / Windows' game DB), and
  stale entries pointing at a GPU that is no longer installed. Entries you made in Windows Settings are never overwritten
  unless you choose *Take over*.
- **Libraries & lists**: detected game libraries, extra game folders, the media / game / ignore / no-hook lists, and
  *Pre-route all games* / *Pre-route Start-menu apps* to write rules ahead of the first launch.
- The original registry key is exported to `%APPDATA%\GpuSwitcher\backups\` before the first change.
  *Rules ▸ Remove everything GpuSwitcher wrote* undoes all of it.

### Command line (`gpusw.exe`)

```
gpusw gpus                                  GPUs, PCI slots, monitors, kernel adapter type, role assignment
gpusw ps [--all]                            per-app assigned vs actual GPU, load, video engines, memory
gpusw rules                                 the whole preference store, owners, stale entries
gpusw classify <exe>                        role + reason
gpusw route <exe|AUMID> game|app|media|ignore|clear
gpusw unroute <exe|AUMID>                   remove the entry, rule and memory for one app
gpusw unroute-all                           remove every entry GPU Switcher wrote
gpusw set-gpu game|app|media auto|windows|#index|VEN&DEV&SUBSYS
gpusw pre-route games|startmenu [--dry-run]
gpusw restart <exe> [--force]
gpusw inject <pid> [--mode reorder|exclusive] [--target #index]
gpusw probe [--load S] [--hook exclusive --target #1]   what D3D11/12, DXGI, Vulkan, OpenGL pick in this process
gpusw service install|uninstall|status|start|stop
gpusw autostart on|off
gpusw guard                                 run the boot guard in an elevated console
gpusw watch                                 run the engine in a console
gpusw version
```

Copy `gpusw.exe` to any path, `gpusw route <that path> media`, run `<that path> probe`, and you see the routing exactly as an app would.

## Build

Needs the .NET 10 SDK, plus MSVC (Visual Studio 2022 or later with the C++ workload) for the native hook in `src\Hook`.

```
.\release.ps1                     # release package → out\release\ (zip, the three exes, SHA256SUMS.txt)
.\publish.ps1                     # build and install locally (elevated): updates the boot guard's folder in place,
                                  #   else installs to %ProgramFiles%\GpuSwitcher
.\publish.ps1 -InstallDir <dir>
dotnet test                       # role assignment across AMD / second NVIDIA / identical twins / removed cards
```

The native binaries are built by `src\Hook\build.ps1` into `native\` and are not kept in git; without MSVC, take them
from a release zip. Updating while hooked apps still run is fine: the pinned `gshook*.dll` is renamed aside and replaced.
The version lives in `Directory.Build.props`; changes are listed in `CHANGELOG.md`.

Layout: `src\Core` (topology via DXGI + D3DKMT + QueryDisplayConfig, role resolver, preference store, GPU counters,
classifier, injector, routing engine, ETW launch guard), `src\App` (WinForms tray UI), `src\Cli` (`gpusw`),
`src\Service` (boot-guard service), `src\Hook` (C hook + 32-bit injector helper), `tests` (xUnit).
Config and log: `%APPDATA%\GpuSwitcher\` (override with `GPUSWITCHER_HOME`).
