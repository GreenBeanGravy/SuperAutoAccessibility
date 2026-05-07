# SuperAutoAccessibility

Development repo for the [SuperAutoAccessibility](https://github.com/GreenBeanGravy/SuperAutoAccessibility) mod — a [MelonLoader](https://melonwiki.xyz/) mod for Super Auto Pets that adds screen reader support for blind and visually impaired players.

Grab the latest `SuperAutoAccessibility.dll` from [Releases](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases) and drop it into `Super Auto Pets/Mods/` if you just want to use the mod.

## Building from source

Requires the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) and a Steam install of Super Auto Pets with [MelonLoader](https://melonwiki.xyz/#/?id=requirements) already set up.

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
