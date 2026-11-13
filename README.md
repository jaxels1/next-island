# Next Island for Windows

Next Island is a small pill at the top of your screen. Hover there when you want it, or let a track change bring it out. It tucks away when you move away. In a fullscreen game, it widens to show FPS, frame time, CPU and GPU load, and temperatures.

## Run it

Download the Windows x64 ZIP from [Releases](https://github.com/jaxels1/next-island/releases). Extract the whole folder and run **NextIsland.exe**. The bundle includes .NET and runs on Windows 10 version 2004 or later and Windows 11 on x64.

- Hover at the top center, then click the pill for playback controls. The Tools menu has a file shelf, clipboard history, and a timer.
- Press **Ctrl+Alt+F9** to open the island. During a game, the same shortcut expands music controls beneath the live readings. Press **Esc** to close.
- Press **Ctrl+Alt+F10** to pin or unpin the game HUD. If automatic detection misses your game, focus it and use **Game detection** from the tray.

Borderless fullscreen works best. Game detection uses local information and can miss some titles. FPS capture and temperature readings depend on Windows permissions, drivers, and hardware. A dash means a reading is unavailable. The first build is unsigned, so Windows may block it under Smart App Control.

## Build from source

With the .NET 10 SDK installed, run this from the repository root.

~~~powershell
dotnet run --project .\WindowsGameHud\NextIsland.csproj -c Release
powershell -NoProfile -File .\WindowsGameHud\Build-LocalBundle.ps1
~~~

The second command builds the self-contained bundle under WindowsGameHud/bin/Next Island. PresentMon is included. Some CPUs need the optional [PawnIO driver](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) for temperatures.

Next Island began as the Windows overlay in a [Sapphire fork](https://github.com/jaxels1/Sapphire). The motion and controls take inspiration from [Boring Notch’s Flying Rabbit release](https://github.com/TheBoredTeam/boring.notch/releases/tag/v2.7.3). The app is licensed under [AGPL-3.0](LICENSE). See [third-party notices](WindowsGameHud/THIRD-PARTY.md).
