# Next Island for Windows

Next Island is a small pill at the top of your screen. Hover there when you want it, or let a track change bring it out. It tucks away when you move away. In a fullscreen game, it widens to show FPS, frame time, CPU and GPU load, temperatures, and system RAM usage.

## Run it

Download **NextIsland-win-Setup.exe** from [Releases](https://github.com/jaxels1/next-island/releases/latest) and run it. If you use an older ZIP build, exit it from the tray first. Setup installs for your Windows account and adds Next Island to the Start menu. It includes .NET and runs on Windows 10 version 2004 or later and Windows 11 on x64.

Installed copies check GitHub for stable updates at launch and every six hours. They download updates in the background and apply them when you exit Next Island. The tray menu also has **Check for updates**.

- Hover at the top center, then click the pill for playback controls. Use the tabs to switch to the file shelf, clipboard history, or timer.
- Press **Ctrl+Alt+F9** to open the island. During a game, the same shortcut expands music controls beneath the live readings. Press **Esc** to close.
- Press **Ctrl+Alt+F10** to pin or unpin the game HUD. If automatic detection misses your game, focus it and use **Game detection** from the tray.

Borderless fullscreen works best. Game detection uses local information and can miss some titles. FPS capture and temperature readings depend on Windows permissions, drivers, and hardware. A dash means a reading is unavailable. The installer is unsigned, so Windows may block it under Smart App Control.

## Build from source

With the .NET 10 SDK installed, run this from the repository root.

~~~powershell
dotnet run --project .\WindowsGameHud\NextIsland.csproj -c Release
powershell -NoProfile -File .\WindowsGameHud\Build-LocalBundle.ps1
powershell -NoProfile -File .\WindowsGameHud\Build-Installer.ps1
~~~

The second command builds the self-contained bundle under WindowsGameHud/bin/Next Island. PresentMon is included. Some CPUs need the optional [PawnIO driver](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) for temperatures.

The installer command writes setup and update files under `WindowsGameHud/bin/installer/<version>`. To publish a new version, bump `Version` in `NextIsland.csproj`, commit it, and push a matching tag such as `v0.2.1`. GitHub Actions packages the tagged commit and publishes the installer, full update package, and update feed together.

To check the interface, run `dotnet run --project Verification -c Release`. This exercises the island states and writes preview images to the verification output folder. Add `-- --interactive` to open a temporary panel with sample media for mouse and keyboard checks. See [the interface notes](DESIGN.md) for the visual system and testing limits.

Next Island began as the Windows overlay in a [Sapphire fork](https://github.com/jaxels1/Sapphire). The motion and controls take inspiration from [Boring Notch’s Flying Rabbit release](https://github.com/TheBoredTeam/boring.notch/releases/tag/v2.7.3). The app is licensed under [AGPL-3.0](LICENSE). See [third-party notices](WindowsGameHud/THIRD-PARTY.md).
