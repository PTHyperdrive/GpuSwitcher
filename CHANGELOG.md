# Changelog

All notable changes to GPU Switcher are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-25

First release.

### Routing
- Three roles: **Games** (main GPU), **Apps** (second GPU or iGPU), **Media** (Intel Quick Sync for video decode/encode).
- Routing uses Windows' own per-app GPU preference (`SpecificAdapter=` in `UserGpuPreferences`), the same store as
  Settings ▸ Graphics. It covers D3D10/11/12 and the Vulkan device order.
- Classifies apps automatically: Steam / Epic / GOG / Ubisoft / EA / Battle.net / Riot / Xbox libraries, publisher
  folders, game-engine and anti-cheat files next to the exe, media players, overlays and capture tools.
- Learns which programs use a GPU, routes new apps at launch, and drops entries for programs that never touch a GPU.
- Never overwrites entries you made in Windows Settings; the original key is backed up before the first change.

### In-process hook
- `gshook64.dll` / `gshook32.dll` filter DXGI adapter enumeration (Reorder or Exclusive) by patching the factory
  vtable. No code bytes are patched.
- Injected into apps and media players only. Games, anti-cheat processes and services are never hooked, and game
  processes are never opened with more than query rights.

### Boot guard service
- `GpuSwitcherSvc.exe` runs as an auto-start service from boot, at High priority.
- ETW kernel-process events (process start + image load, 10 ms flush) route and hook new processes within milliseconds.
- Reads and writes your profile while impersonating your session token; its install folder is locked to admins.
- Opens the tray app at logon, ahead of other startup apps.

### Hardware
- Each card is identified by VEN&DEV&SUBSYS plus PCI slot. AMD or NVIDIA second cards take the Apps role
  automatically; the Games card is sticky.
- Identical twin cards are kept apart: apps are pinned to the exact card by LUID, and games use Windows' default.
- Indirect-display adapters (e.g. Parsec's virtual display) are recognised and never used as a target.

### Tools
- Tray app: live per-GPU engine load, assigned vs. actual GPU per app, restarts, rules, libraries, settings, log.
- `gpusw` CLI: `gpus`, `ps`, `rules`, `route`, `pre-route`, `probe`, `inject`, `service`, `autostart`, and more.
