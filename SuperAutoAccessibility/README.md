# Super Auto Pets Mod Template

## Setup

1. Update the paths in `SuperAutoAccessibility.csproj` to match your Super Auto Pets installation
2. Build the mod:
   ```
   dotnet build -c Release
   ```
3. Copy `bin/Release/net6.0/SuperAutoAccessibility.dll` to `Super Auto Pets/Mods/`

## Testing

Launch Super Auto Pets and check the MelonLoader console for "Super Auto Pets Mod initialized!"
Press F5 in-game to see a test message.

## Customization

Edit `SuperAutoAccessibility.cs` to add your mod functionality.
