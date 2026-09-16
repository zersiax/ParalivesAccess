ParalivesAccess 0.40.0 - Patreon build

This accessibility mod adds screen-reader output and keyboard access to Paralives.
The bundle includes BepInEx 5.4.23.5, Unity Doorstop 4.5.0 and Prism 0.17.3.
The development installation uses Paralives Early Access 0.1.8b.20663.

INSTALLATION
1. Close Paralives.
2. Open the game's installation folder. In Steam, use Manage, Browse local files.
3. Extract the contents of this ZIP directly into the folder containing Paralives.exe.
   Merge the BepInEx folder and allow replacement of the included files.
   The mod must end up at BepInEx/plugins/ParalivesAccess.dll.
   prism.dll, winhttp.dll and doorstop_config.ini must be beside Paralives.exe.
4. Start your screen reader, then launch Paralives normally through Steam.
   The BepInEx log should report ParalivesAccess 0.40.0 and Speech=True.


DEFAULT CONTROLS
Arrow keys: move through the current accessible panel.
Enter or numpad Enter: activate the focused control.
Semicolon / apostrophe: previous / next accessible panel.
F3: current screen, panel and context; lot names/addresses, hours and building options.
Shift+F3: repeat the most recent important event.
F4: act on an announced context, such as available conversation topics.
F6: accessible build catalogue.
F7: current-lot explorer and available actions.
Needs attention in F7: dirty, broken, burning, burnt or spoiled objects, water
spills and soot on the current lot. Enter opens the native menu to clean, mop or repair.
Bus stops in town in F7: loaded stops, including street signs outside the lot.
Choose the native Take the bus action; when the destination map opens, press L
for its numbered stops. Enter chooses a stop; Cancel bus travel cancels the trip.
L: town and destination browser.
Shift+Enter on an F7 Para entry: add to or remove from the selected group.
Clear selected group: available at the F7 root and on Para command pages;
keeps one household Para selected, as required by the game's selection behavior.
In the creator Household panel, Down on a Para portrait reaches Delete;
Up returns to the portrait. Enter opens the game's confirmation when deletion is allowed.
F: the game's normal follow-selected-Para control.
F8: toggle close audio perspective; off by default.
F9 / Shift+F9: select and follow the next / previous household Para. This selects
that Para alone. Text entry, modal windows and restricted activities take priority.
Minus / equals: native camera zoom; release in live mode to hear the final value.
7 / 8 in the creator: decrease / increase the focused value.
9 / 0 in the creator: adjust a supported body control's second axis.

Function keys can have different values if previously changed in the mod settings.
The game keeps its own camera, simulation and text-entry controls.

SETTINGS CONTROLS
Enter selects a category; Right enters its displayed settings.
Up/Down walks controls. Semicolon/apostrophe switch between categories, settings
values, and Close/Reset. Left/Right adjusts sliders; Enter edits their numeric value.
Enter activates checkboxes, dropdowns and buttons.
Searchable dropdowns: type to filter, then Down to enter the options. Up from the
first option returns to search. Enter selects an option; Escape closes the dropdown.


First Run

The game simultaneously simple and hard to come to grips with. I will give some tips on how to get started here:

- I recommend playing through the tutorial first. It can be reached by selecting New game -> Town -> Yes.
- This game can take quite a while to load in. After pressing enter on Yes, or on a save, give it time. Stuff is definitely happening. This is not the mod's doing, it can at times just take up to a minute for the main menu to take a hike.
- After you finish the tutorial, use the L key to find and buy a house. For now, given build mode doesn't really work yet, I recommend getting a house with facilities like a toilet, shower, microwave etc. present, so your folk don't keel over and starve.
- Your semicolon (;) and quote (') keys are essentially your tab and shift+tab. They move you between areas of the user interface.
- Your f7 is your current lot explorer. Lot essentially means area on the map. Your house is a lot. The beach is a lot. The community center is a lot. f7 gives you access to objects within the lot, as well as other paras and some convenience features.
- L is what is currently the accessible map overlay. You use it by pressing M first to focus the town map, after which you can press L for a list of houses, places to visit, etc.
This is basically your portal to the rest of the game world.
- f6 opens the accessible build catalog overlay. It lets you find and place objects onto your lot. Right now, all it does to help you place them accessibly is center your camera on your lot and give you overlap reports when moving with the arrow keys. A comprehensive build mode pass will follow later.

Apart from that, the arrow keys should get you most places
