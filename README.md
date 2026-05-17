# SuperAutoAccessibility

Development repo for the [SuperAutoAccessibility](https://github.com/GreenBeanGravy/SuperAutoAccessibility) mod — a [MelonLoader](https://melonwiki.xyz/) mod for Super Auto Pets that adds screen reader support for blind and visually impaired players.

## Installing the mod

The easiest path: download `SAPAccess Installer.exe` from [Releases](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases) and run it. It auto-detects your Super Auto Pets install, installs the latest MelonLoader, and drops the mod DLL into `Mods/` for you.

If you'd rather do it by hand:

1. Install [MelonLoader](https://melonloader.co/) and point it at your `Super Auto Pets.exe`. See the [MelonLoader installation guide](https://melonwiki.xyz/#/?id=automated-installation) if you've never used it before.
2. Run Super Auto Pets once so MelonLoader generates the `Mods/` folder.
3. Download `SuperAutoAccessibility.dll` from [Releases](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases) and drop it into `Super Auto Pets/Mods/`.

Either way, the mod auto-updates: on game start it compares its SHA-256 against the latest release asset and prompts you when a newer version ships.

## Building the mod from source

Requires the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) and a Steam install of Super Auto Pets with [MelonLoader](https://melonloader.co/) already installed (see the [installation guide](https://melonwiki.xyz/#/?id=automated-installation)).

```
git clone https://github.com/GreenBeanGravy/SuperAutoAccessibility.git
cd SuperAutoAccessibility/SuperAutoAccessibility
dotnet build -c Release
```

The SAP install is auto-detected from the Steam registry. If you installed SAP outside the default Steam library, pass the path explicitly:

```
dotnet build -c Release /p:SAPPath="C:\path\to\Super Auto Pets"
```

Output DLL: `SuperAutoAccessibility/bin/Release/net6.0/SuperAutoAccessibility.dll`. Copy it into your `Super Auto Pets/Mods/` folder.

## Building the installer from source

The installer lives in [`installer/installer.py`](installer/installer.py) — a wxPython app that auto-detects SAP, downloads the latest MelonLoader, downloads the mod DLL, and self-updates itself against this repo's latest release. Requires Python 3.10+:

```
pip install pyinstaller wxpython requests
cd installer
pyinstaller --onefile --windowed --name "SAPAccess Installer" --uac-admin installer.py
```

Output: `installer/dist/SAPAccess Installer.exe`.
