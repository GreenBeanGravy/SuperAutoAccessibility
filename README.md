# SuperAutoAccessibility

This is the development repo for [SuperAutoAccessibility](https://github.com/GreenBeanGravy/SuperAutoAccessibility), a [MelonLoader](https://melonwiki.xyz/) mod that adds screen reader support to Super Auto Pets for blind and visually impaired players.

## Installing the mod

The easiest way to install the mod is to download `SAPAccess Installer.exe` from [Releases](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases) and run it. The installer finds your Super Auto Pets installation, installs the latest MelonLoader, and copies the mod DLL into the `Mods/` folder.

To install it manually:

1. Install [MelonLoader](https://melonloader.co/) and select your `Super Auto Pets.exe`. If you haven't used MelonLoader before, follow its [installation guide](https://melonwiki.xyz/#/?id=automated-installation).
2. Run Super Auto Pets once so MelonLoader creates the `Mods/` folder.
3. Download `SuperAutoAccessibility.dll` from [Releases](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases) and copy it into `Super Auto Pets/Mods/`.

The mod supports automatic updates with either installation method. Each time you start the game, it compares its SHA-256 hash with the latest release asset and prompts you when an update is available.

## Building the mod from source

You'll need the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) and a Steam installation of Super Auto Pets with [MelonLoader](https://melonloader.co/) installed. Follow the [MelonLoader installation guide](https://melonwiki.xyz/#/?id=automated-installation) if you need to set it up.

```bash
git clone https://github.com/GreenBeanGravy/SuperAutoAccessibility.git
cd SuperAutoAccessibility/SuperAutoAccessibility
dotnet build -c Release
```

The build finds your Super Auto Pets installation through the Steam registry. If the game is outside the default Steam library, specify its path:

```bash
dotnet build -c Release /p:SAPPath="C:\path\to\Super Auto Pets"
```

The built DLL is at `SuperAutoAccessibility/bin/Release/net6.0/SuperAutoAccessibility.dll`. Copy it into your `Super Auto Pets/Mods/` folder.

## Building the installer from source

The installer source is in [installer/installer.py](installer/installer.py). It's a wxPython app that finds your Super Auto Pets installation and downloads the latest MelonLoader and mod DLL. It also updates itself from this repo's latest release.

You'll need Python 3.10 or later:

```bash
pip install pyinstaller wxpython requests
cd installer
pyinstaller --onefile --windowed --name "SAPAccess Installer" --uac-admin installer.py
```

The built installer is at `installer/dist/SAPAccess Installer.exe`.
