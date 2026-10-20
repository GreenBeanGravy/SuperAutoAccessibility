# SuperAutoAccessibility: Super Auto Pets accessibility for blind and visually impaired players

SuperAutoAccessibility is a [MelonLoader](https://melonwiki.xyz/) mod that makes Super Auto Pets playable with a screen reader. Speech goes through Tolk, which works with most screen readers.

## What it does

In the menus, the mod:

- shows the game's menus to Windows as real controls through [UnityA11y](https://github.com/GreenBeanGravy/UnityA11y), so your screen reader reads them natively, with each control's position in its section
- lets you move with the arrow keys, jump between sections with Tab or Page Up and Page Down, and back out with Escape
- reads each button's description as soon as you reach it
- makes the Pack Shop and its leaderboard, Achievements, Stats, News, the Versus Lobby, pickers and Settings usable
- has a keyword dictionary of pets, food, toys, triggers, statuses and game mechanics, with search

In a run, the mod:

- lets you jump between the pet shop, the food shop, your team and the action buttons with one key each, and buy, sell, roll, freeze and end your turn from the keyboard
- reads each pet and food line by line: stats, ability, tier and experience
- speaks your gold, lives, turn, wins, the turn timer and snacks on request
- narrates battles, and lets you pause, step through, fast forward or skip them
- announces level-up rewards and "Choose one" screens, and opens the deck viewer and Last battle
- reads the Versus scoreboard opponent by opponent and pet by pet
- shows the upcoming bullies in Daily

Press H anywhere for a list of every key on the screen you're on. A controller works everywhere the keyboard does.

## Installing

1. Download `SAPAccess.Installer.exe` from the latest release on the [releases page](https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases).
2. Run it. It finds Super Auto Pets, installs or updates MelonLoader, and installs the mod.
3. Start Super Auto Pets as usual.

The mod checks for a new release each time the game starts and offers to install it.

### Installing by hand

1. Install [MelonLoader](https://melonloader.co/) 0.7.3 or newer for `Super Auto Pets.exe`. See its [installation guide](https://melonwiki.xyz/#/?id=automated-installation).
2. Start the game once, so MelonLoader creates the `Mods` folder.
3. Download `SuperAutoAccessibility.dll` from the latest release and put it in `Super Auto Pets/Mods`.

## Settings

In `UserData/MelonPreferences.cfg`, under `[SAPAccess]`:

| Setting | What it does |
| --- | --- |
| `UseAccessibilityTree` | `true` (default): your screen reader reads the menus. `false`: the mod speaks them through Tolk. |
| `ControllerSupport` | `true` (default): a controller works in menus, the shop and battles. `false` turns it off. |
| `DebugLogging` | `true` writes detailed tracing to the MelonLoader log, for bug reports. Default `false`. |

Change them with the game closed. The game saves this file when it quits.

## Default keybinds

### Menus

| Key | Action |
| --- | --- |
| Up / Down arrow | Previous / next item |
| Tab or Page Down | Next section |
| Shift+Tab or Page Up | Previous section |
| Home / End | First / last item |
| Enter | Activate |
| Escape | Back, or the pause menu |
| R | Read the current screen |
| B | Snacks |
| J | Keyword dictionary (Ctrl+F searches inside it) |
| H | Help |

### Shop

| Key | Action |
| --- | --- |
| S / F / T | Pet shop / food shop / team |
| D | Action buttons: roll, freeze, sell, deck viewer, last battle |
| V | Upcoming bullies (Daily) |
| Left / Right arrow | Previous / next slot |
| Home / End | First / last slot |
| Up / Down arrow | Detail lines |
| Space | Repeat the detail line |
| Enter | Buy or select |
| X | Sell |
| Q | Roll |
| Z | Freeze or unfreeze |
| E | End turn |
| Backspace | Cancel |

### Information

| Key | Action |
| --- | --- |
| I | Detailed info |
| A | Gold |
| L | Lives |
| N | Turn and tier |
| W | Wins |
| C | Turn timer |
| R | Full board status |
| O | Versus scoreboard |

### Battle

| Key | Action |
| --- | --- |
| P | Pause or resume. With autoplay off, plays the next step |
| U | Autoplay on or off |
| G | Fast forward on or off |
| K | Skip the battle |
| Space | Repeat the last announcement |

### Versus scoreboard

| Key | Action |
| --- | --- |
| Tab or Page Down | Next opponent |
| Shift+Tab or Page Up | Previous opponent |
| Left / Right arrow | Previous / next pet |
| Home / End | First / last pet |
| Up / Down arrow | The pet's details |
| Enter | Read all of the pet's details |
| O or Escape | Close |

## Controller

Plug in an Xbox style controller, or any controller that Steam Input presents as one, and play without the keyboard. The mod says "Controller connected" and everything it speaks for the keyboard is spoken for the controller too. Press View for the controller help on the screen you're on.

### Menus

| Button | Action |
| --- | --- |
| D-pad or left stick | Previous / next item (left / right adjust sliders) |
| Left / right bumper | Previous / next section |
| Right stick up / down | First / last item |
| A | Activate |
| B | Back |
| X | Space (read again, accept) |
| Y | Read the screen |
| Start | Back, or the pause menu |
| View | Open or close help |
| Left stick click | Dictionary |

### Shop

| Button | Action |
| --- | --- |
| Left / right bumper | Previous / next zone: pet shop, food shop, team, actions |
| D-pad or left stick, left / right | Previous / next slot |
| D-pad or left stick, up / down | Browse the detail lines |
| Right stick up / down | First / last slot |
| A | Buy, select or use |
| X | Sell |
| B | Cancel, leave the zone, then the pause menu |
| Y | Read the whole board |
| Right stick click | Read the current detail line again |
| Hold left trigger, then A / B / X / Y | Gold / lives / turn / wins |
| Hold left trigger, then right / left bumper | Details / scoreboard |
| Hold right trigger, then A / X / Y | Roll / freeze / end turn |

### Battle

| Button | Action |
| --- | --- |
| A | Pause or play, and continue on result screens |
| X | Fast forward |
| Y | Autoplay on or off |
| Right trigger | Skip the battle |
| Left bumper or right stick click | Repeat the last line |
| Right bumper | Read the screen |
| B or Start | Pause menu |

## Troubleshooting

- The game crashes at startup: Super Auto Pets runs on Unity 6, which needs MelonLoader 0.7.3 or newer. Run the installer again to update it.
- Menus speak, but your screen reader's own reading of them is missing: check that `UseAccessibilityTree` is `true`. Without it, or if the game window can't be reached, the mod speaks the menus itself.
- The mod loads twice: an old `SuperAutoPetsMod.dll` is still in `Mods`. Delete it, or run the installer, which removes it.
- The controller does nothing: the mod reads XInput pads. A PlayStation or Switch controller needs Steam Input turned on for Super Auto Pets (Steam, game properties, Controller), so Steam presents it as an Xbox controller. Check that `ControllerSupport` is `true`.
- Something else: the log is `MelonLoader/Latest.log` in the game folder. Set `DebugLogging` to `true`, reproduce the problem, and attach the log to an issue.

## Building from source

You need the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) and Super Auto Pets with MelonLoader installed and started once.

```bash
git clone --recurse-submodules https://github.com/GreenBeanGravy/SuperAutoAccessibility.git
cd SuperAutoAccessibility/SuperAutoAccessibility
dotnet build -c Release
```

UnityA11y is a git submodule, compiled into the mod's DLL. In a clone without it, run `git submodule update --init`.

The build finds the game through Steam. If it's somewhere else, pass its folder:

```bash
dotnet build -c Release /p:SAPPath="C:\path\to\Super Auto Pets"
```

The DLL is at `SuperAutoAccessibility/bin/Release/net6.0/SuperAutoAccessibility.dll`. It carries `accesskit.dll` inside and extracts it to `Mods/AccessKitLibs` on first start, so a release is that one file. Put an empty file named `SAPAccess.dev` next to a development build to stop it offering to update itself.

### Building the installer

The installer is a wxPython app in [installer/installer.py](installer/installer.py). It also updates itself from the latest release. With Python 3.10 or newer:

```bash
pip install pyinstaller wxpython requests
cd installer
pyinstaller --onefile --windowed --name "SAPAccess Installer" --uac-admin installer.py
```

The installer is at `installer/dist/SAPAccess Installer.exe`. GitHub renames it `SAPAccess.Installer.exe` on upload, and installed copies look for that name.

SuperAutoAccessibility is a fan-made mod and is not affiliated with or endorsed by Team Wood Games, the developer of Super Auto Pets.
