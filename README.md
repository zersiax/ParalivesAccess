# ParalivesAccess 0.52.18

User manual

ParalivesAccess adds keyboard navigation and screen-reader output to Paralives
on Windows. This manual explains the default controls and how to use them.
Mod instructions are currently in English.

A lot is a place on the map, such as your home or the community centre. A Para
is a character. A native menu or action is one provided by the game itself.

You need Paralives and a running screen reader, such as NVDA or JAWS. The full
installation ZIP includes the mod loader and speech bridge. This version was
tested with Paralives Early Access 0.1.9b.20995, on Windows x64.


## Contents

1. [Install or update](#1-install-or-update)
2. [Navigation and key reference](#2-navigation-and-key-reference)
3. [Start your first game](#3-start-your-first-game)
4. [Everyday play: Paras, objects and travel](#4-everyday-play-paras-objects-and-travel)
5. [Find your way around a lot](#5-find-your-way-around-a-lot)
6. [Build rooms and walls](#6-build-rooms-and-walls)
7. [Place furniture and connected objects](#7-place-furniture-and-connected-objects)
8. [Place doors, windows and ceiling fixtures](#8-place-doors-windows-and-ceiling-fixtures)
9. [Add floor finishes and roofs](#9-add-floor-finishes-and-roofs)
10. [Edit structures and recover furniture](#10-edit-structures-and-recover-furniture)
11. [Precision placement tools](#11-precision-placement-tools)
12. [Settings and key configuration](#12-settings-and-key-configuration)
13. [Troubleshooting and support](#13-troubleshooting-and-support)


## 1. Install or update

### Use the installer

Download [ParalivesAccess-Installer.exe](https://github.com/zersiax/ParalivesAccess/releases/latest/download/ParalivesAccess-Installer.exe)
and run it. The installer detects Paralives in your Steam libraries and checks
GitHub for the latest stable release. If the game folder is not found, use
Browse or type the folder containing Paralives.exe.

Close Paralives, review the game folder and version summary, then choose
**Install or update**. This downloads the full package and repairs missing or
changed files. **Reinstall latest** replaces every included file. Both options
keep your saves, mod configuration and other plugins. Start your screen reader
and launch the game through Steam after installation completes.

Use Tab and Shift+Tab to move through the controls. Alt+G focuses the game
folder, Alt+B opens Browse, Alt+C checks for updates, Alt+I installs or updates,
and Alt+R reinstalls. The status box and progress log can be reviewed with the
keyboard. During an operation, Cancel stops it and restores files changed by
that attempt. If Windows denies access, the installer offers a restart as
administrator; approve the Windows prompt yourself if you choose that option.

The installer requires Windows with .NET Framework 4.8. It always checks the
public GitHub release; public and Patreon builds are unified. Keep the EXE to
install later updates. Logs and backups are in
`%LOCALAPPDATA%\ParalivesAccess\Installer`, with an Open log file button in the UI.

### Install the ZIP manually

1. Close Paralives.
2. Open the game's installation folder. In Steam, use Manage, Browse local files.
3. Extract the full ParalivesAccess ZIP directly into the folder containing
   Paralives.exe. Merge folders and allow replacement of the included files.
4. Start your screen reader, then launch Paralives normally through Steam.

Check the extracted layout if the mod does not load:

- BepInEx/plugins/ParalivesAccess.dll is inside the game's folder.
- prism.dll, winhttp.dll and doorstop_config.ini are beside Paralives.exe.

Use the full installation ZIP for a first installation. Updating from it uses
the same steps. Keep your existing saves and configuration files.


## 2. Navigation and key reference

Use the arrow keys to explore the current accessible panel. Enter activates
the selected control. Semicolon (;) and apostrophe (') move between panels,
serving a similar purpose to Tab and Shift+Tab in many applications.
Escape backs out or closes the current screen. In F7, Backspace goes up a level.

When editing text, Ctrl+A selects the current value. Enter submits it; Escape
cancels. For forms containing several numbers, separate them with semicolons,
not commas. Decimal commas follow your locale; decimal points are also accepted.

The main shortcuts are:

- **F2:** read the in-game time and date without changing focus.
- **F3:** read the current screen, selected control or activity's context.
- **Shift+F3:** repeat the most recent important event.
- **F4:** act on an announced context, such as available conversation topics.
- **F6:** open the accessible build catalogue.
- **F7:** explore the current lot, its rooms, objects and Paras.
- **Shift+F7:** move the camera to a room chosen from a list.
- **Ctrl+F7:** open the current lot's Para list directly, in Live mode.
- **Ctrl+F11:** toggle the top-down camera in Build mode.
- **F8:** toggle the close audio perspective, in Live mode.
- **F9 / Shift+F9:** select and follow the next / previous household Para.
- **J:** describe the room at the camera focus, or the lot when outdoors.
- **Shift+J:** describe the whole lot in a more global manner.
- **N:** open or close the free lot cursor so you can spatially explore the current lot. This works in Live and Build mode both.
- **M, then L:** open the town map and its accessible destination browser.
- **Tab:** switch between Live and Build modes.
- **Minus / equals:** use the game's camera zoom controls.

While placing or editing an object:

- **Arrows:** move the preview; Shift+arrows make smaller movements.
- **X and comma / period:** use the game's rotation controls.
- **F3:** review the preview's position, size and placement status.
- **F10:** enter a position numerically.
- **F11:** open placement commands and detailed measurements.
- **Semicolon:** choose compatible surface or chair slots for furniture, e.g. the top of a desk and the chair in front of a desk.
- **Enter:** place the ready preview or confirm an existing object's move.
- **Escape:** cancel the current preview.

Some keys have a different purpose in the wall tool, slot picker or lot cursor.
Those controls are explained below. F3 is the quickest way to review the current
context. These shortcuts are defaults; several can be changed in the mod's
configuration. The game's own key bindings still apply.


## 3. Start your first game

For an introduction to Paralives, choose New game, Town, then Yes to take the
tutorial. Follow its spoken prompts and use F3 when you need the current context.
Some loading screens take a while; allow the game time to open the next screen.

In the creator, navigate to a control before adjusting it. Use 7 and 8 to
change its value. For a body control with a second axis, use 9 and 0 for that
axis. Minus and equals remain camera zoom controls.

In the creator's Household panel, Left and Right move between Para portraits
and Add Para. Down from a portrait reaches Delete; Up returns to the portrait.
Enter on Delete opens the game's confirmation when deletion is available.

After the tutorial, M followed by L lets you browse places and houses. A
furnished house with a toilet, sink, shower and cooking equipment is a useful
starting point while learning Live mode. You can furnish or build your own
home using the building instructions in this manual.


## 4. Everyday play: Paras, objects and travel

### Use an object


1. Press F7 and choose the current floor, then a room or outdoor area.
2. Find the object. You can also use the lot's object lists or name search.
3. Open its commands and choose Open the native action menu.
4. Choose the action you want the selected Para to perform.

The available actions depend on the object, selected Para and game state.
Opening a menu does not itself start an action. F7's Needs attention list helps
you find dirty, broken, burning, burnt or spoiled objects, spills and soot.
Choose an entry to open its normal cleaning, repair or other action menu.

### Choose Paras


F9 selects and follows the next household Para; Shift+F9 selects the previous
one. This selects that Para alone. Use Ctrl+F7 to browse all Paras on the
current lot and open their commands.

On an eligible Para entry in F7, Shift+Enter adds that Para to or removes them
from the selected group. Clear selected group keeps one household Para selected,
as required by the game. On other F7 entries, Shift+Enter activates normally.
The native F key follows the selected Para.

### Move to a room or a particular spot


In Live mode, open F7 and choose Send selected Paras to a room. Choose the room
and press Enter. The mod requests normal walking for the selected group if
routes are available. Paused play waits for you to resume time. Other game
actions can interrupt a walking request.

For a particular floor position or a ground activity, use the N lot cursor
as described in section 5. A camera room jump with Shift+F7 only moves the
camera; it does not send a Para there.

### Travel


Open the town map with M, then press L to browse destinations and available
lot actions. For bus travel, F7's Bus departure stops list offers loaded stops,
with the nearest to your selected Para first. Choose a stop and its native
Take the bus action. When the destination map opens, press L and choose a
numbered destination stop. Cancel bus travel cancels the trip.
Bus stops are still experimental and it's currently not super clear what stop is near what location, this will be added in a future build.

### Time and sound


F2 reads the current time and full date while a game is loaded, including during
menus, placement and text entry. It follows the game's 12/24-hour preference.
F8 toggles a close audio perspective near the selected Para without moving the
visual camera. It starts off. Camera zoom remains on minus and equals.


## 5. Find your way around a lot

### Room and lot descriptions


Press J to describe the room at the camera focus: its size, adjoining rooms,
objects, their positions, and which wall or corner they face. Outdoors, J reads
the lot overview. Shift+J always gives that overview, including rooms by floor,
the lot's shape and dimensions, and shared walls.

These directions stay fixed when the camera turns:

- Front is the Down-arrow side of the lot grid; back is the Up-arrow side.
- Left and right follow the Left and Right arrows.
- Positions such as back centre describe where an object is within the room.
- Facing the back wall describes the direction its front points.

A shared wall does not mean there is a connecting door.

Shift+F7 opens a room list across floors. Choose a room and Enter to move the
camera to its centre, or a valid interior point in an irregular room. The jump
also selects that floor. F7 offers the same command as Go to room centre.

### Name rooms


In F7, choose a floor and room, then Name room. Enter applies the name; save
the game normally to retain it. Rename room and Clear room name are available
afterward. Names appear in descriptions and building menus. Please note that room names are a mod function; this functionality normally doesn't exist in game.

### Explore freely with the lot cursor


Press N in Live or Build mode to explore without choosing a wall or furniture
preview. To start in a particular room, choose it in F7 first, then press N.

- Arrows move the cursor along the fixed lot grid, normally 0.5 m per step.
- Shift+arrows move in smaller steps, normally 0.1 m.
- Each movement reads the floor, grid address, room or outdoors, floor covering
  or terrain, and objects directly under the cursor. F3 repeats that report.

- F11 describes nearby objects and doorways, nearest first, within 4 m.
- F10 accepts column; row to move directly to a grid address.
- Page Up / Page Down change the visible floor.

In Live mode, with a Para selected, Enter opens the ground action menu at that
spot. Choose Go Here or an available activity. Exploring the cursor alone does
not move the Para. Closing the action menu returns to the same cursor position.

N or Escape closes the cursor.


## 6. Build rooms and walls

### Start a rectangular room


1. Enter Build mode with Tab. Use F7 to choose the lot's floor and a room or
   outdoor area. If the camera is still on the town map, this brings it in.
2. Press B for the straight-wall tool, then R for the room-size form.
3. Keep the suggested size or use Ctrl+A and type width; depth, such as 4; 3,
   in the announced measurement unit. Enter prepares a room near the camera
   focus, or near the lot centre if the camera was outside the lot.
4. Review the announced size and each wall's price. Press Enter separately
   to build each of the four walls.
5. When the enclosure is complete, use F7 to find and name the room.

Ctrl+F11 switches to a near-vertical top-down view without moving the camera's
focus. Press it again to restore the previous angle. The same command is in F7
and the preview's F11 menu. Close any text form before using it.

### Add an adjoining room


In F7, choose the existing room, Add a room on the other side, then a wall with
outdoors on its other side. The new room shares the entire selected wall, so
that wall fixes its width. Enter the new depth alone. Using this function it's currently not possible to add an adjoining room with a different width yet.

Enter opens the size review without building. The next Enter acknowledges
keeping the shared wall without buying it again. Then wait for and confirm
each of the three new walls separately. Add a connecting door afterward.

This guide supports a complete straight, full-height shared side. Partial sides,
multiple existing sides, curved walls and diagonal shared walls are not supported.

### Draw individual walls and partitions


In the B wall tool, arrows move the construction cursor and Shift+arrows use the
smaller wall-grid step. Enter chooses a starting point. Move to the endpoint,
review its length and price with F3, then Enter builds the wall. F10 enters
construction column; row, including an endpoint on an existing wall. Home
reframes the cursor; F11 reads exact coordinates and lot bounds.

For a rectangle defined by its corners instead of its size, press Shift+R while
no wall preview is active. Choose the first and opposite corners with arrows
and Enter, then confirm each wall separately. The wall grid must be enabled.


## 7. Place furniture and connected objects

### Choose and place an item


1. In Build mode, frame the intended room through F7.
2. Press F6. Browse categories or choose Search, enter a name, and choose an item.
   This opens a preview; it does not buy the item yet.
3. Move with arrows, using Shift+arrows for fine steps. Rotate with X or
   comma / period. F3 reads the actual position, size and placement status.
4. Wait for the preview to settle and report that it can be placed, then Enter
   places it. Escape cancels the preview.

The ordinary furniture grid uses half-metre steps, with one-tenth-metre fine
steps. Directions do not turn with the camera. If the game offers another copy
after a purchase, Escape cancels that next preview.

### Find a fitting position


With a preview open, press F11 and choose Find a clear place in the current
room, or choose another room on the current floor. The search moves the preview
to a suggested position without buying it. Review with F3, then Enter to place.

The search checks space and known access points. It does not rotate or resize
the item for you, and cannot guarantee every interaction will work. If it finds
nothing, try another position, rotation, size or room. You can interrupt with
arrows or F11; Escape cancels the preview.

F11's Stay in this room option keeps the placement point within the selected
room. It starts off. If you find yourself leaving the room you want to be furnishing this might be a good way to prevent that from happening.


### Put an object on another object


Place the supporting furniture first. While previewing the second object,
press semicolon to browse compatible surface and chair slots on the current
owned lot and floor. Entries identify the supporting object, room and position.

Choose an available slot and Enter to target it. Wait for the snapped preview,
then press Enter again to place it. The slot sets its position and facing.
Home frames the target. Arrows browse other slots; semicolon returns to the
list and then to the floor grid.

If the preview rests freely on an object rather than a slot, F11 offers Allow
placement on this surface at the current position. Choose it to review that
support, then use a separate Enter to place. Moving requires a fresh review.


### Example: desk, computer and chair


1. Place the desk and close any repeated preview.
2. Press J to check its position and facing. In a rectangular room, a desk
   against the back wall should normally face the front wall so its chair side
   is inside the room. Move or rotate the desk first if needed.
3. Choose a computer in F6, press semicolon, and choose a compatible surface
   slot on that desk. Enter targets the slot; another Enter places the computer.
4. Choose a chair and use semicolon to select the desk's compatible chair slot.
   Target it, wait, then Enter to place it. A nearby unconnected chair may not
   satisfy the computer's requirements.
5. In Live mode, ask a Para to use the computer to check access and the setup.

The game can attach a chair through a wall. A slot connection alone does not
prove there is room to sit or walk there. Check the desk's facing before adding
its chair, and leave space for the chair and a walking route.

### Example: wall TV and couch


Place the TV using the wall controls in section 8. Preview the couch and open
F11's Choose an object for facing, alignment or gap measurements command. Choose
the TV, then the command to point the couch toward it. Review and place the
couch, leaving space to reach it. Try Watch in Live mode to check the setup.


## 8. Place doors, windows and ceiling fixtures

### Doors, windows and other wall items


Choose the item in F6. Browse to the room and Enter, choose its wall face and
Enter to target it. Review the room and wall name before placing.

Left/right move along the selected wall. Up/down change centre height when the
item supports it; Shift gives fine steps. Doors and some sinks or showers have
a fixed floor height. F3 reports their actual position and placement status.
End centres the item along the wall; Home frames the wall. Semicolon returns
to the wall-face list and then the room list.

For a wall item, F10 takes distance along the selected face; centre height,
both in metres. Distance starts at that face's endpoint; height is above the
section's lower edge. F11's detailed report identifies the endpoints.

F11 also offers whole-wall centring and equal-spacing commands.

### Ceiling lights and other ceiling items


Choose the item in F6, then select a room and Enter. Arrows move it across that
room's ceiling; Shift+arrows make fine steps. F10 enters room column; row.
End centres the preview; Home centres and frames it. F11 lets you choose
another room on the current floor.

F3 reports position, ceiling height, clearance above the floor and status.
Hanging height follows the ceiling and the item's dimensions. Review, then
Enter to place. Move an existing ceiling fixture through its F7 object commands.


## 9. Add floor finishes and roofs

### Floor finishes


Choose a flooring surface through F6. Arrows then select a floor platform on
the current owned lot and floor layer. F3 reads the chosen material, current
finish, room and area; Home frames it.

Enter reviews applying the finish to that whole floor platform. A second Enter
applies it. Arrows leave the review to choose another floor; Escape closes the
controls, and F6 returns to the catalogue. Partial-floor finishes and ceiling
painting are not covered by this workflow.

### Roofs


In F6, choose Build, Roofs & Chimneys, Roofs, Browse items, then a roof piece.
Open F11 and choose Fit roof over room. Select the room to fit the preview over
its ceiling on the floor above. Review the completed preview with F3, then
Enter to buy it. Fitting alone does not buy anything.

Fit roof over room uses a rectangle around the room, so an irregular outline
may leave extra covered space. It requires a suitable ceiling. Use multiple
pieces when needed; roof pieces can overlap to make joins.

F11's Roof dimensions takes width; depth; height in metres, for example
8; 6; 2.5. Enter updates a preview's dimensions without buying it. You can also
move and rotate the preview manually. Escape cancels it.

To resize a placed roof, use F7, its floor layer, Outdoor area, the roof, then
Roof dimensions. Enter applies the resize; Escape keeps its old size.


## 10. Edit structures and recover furniture

### Move existing objects


In Build mode, find the object in F7 and choose Move or rotate this existing
object. Use the same movement, rotation, slot and F11 controls as for new
furniture. Enter confirms the move without buying it again. Escape cancels
and restores the original arrangement. The game's Undo/Redo remains available.

### Walls and floor platforms


F7 offers Browse and edit existing walls and room floor-platform commands.
Wall entries identify endpoints, adjacent rooms and attachments. Choose the
appropriate command to remove a segment, hide a wall section, or restore it.
A shared-wall change affects both rooms.

Floor-platform commands can raise or lower platforms, add a whole-room raised
platform, remove one, or restore a missing base floor. Use walls to define
smaller room boundaries; these commands do not build individual floor cells
or duplicate a whole storey.

Read the scope and affected contents in the review, then confirm separately.
Backspace or Back without applying leaves the structure alone. Changes to
occupied platforms can move Paras and cancel their actions. After applying,
reopen F7 once the game finishes updating the rooms.

### Recover objects below the floor


In Build mode, open F7 and choose Recover saved objects below the floor. Choose
an object, review its location, then move it to household inventory or sell it.
The final confirmation performs the change.

When offered, Recover linked arrangement together moves the parent, supported
objects and chairs as one arrangement. Use the fit search or position it yourself,
then confirm. This retains its links; Escape cancels the move.

Use F7, Place saved furniture from household inventory, to place an inventoried
item again. Choose its position and confirm. Placing inventory is not a new
purchase; cancelling leaves the item in inventory.


## 11. Precision placement tools

### Numeric positions and room coordinates


During floor-furniture placement, F10 takes column; row, such as 12; 8. These
are grid addresses, not distances in metres. F3 and F11's detailed report help
you identify the current position and coordinate frame. Negative and fractional
addresses are accepted when the resulting position is valid.

Enter in the form requests a position without buying. Wait for the new report,
then use a separate Enter to place. Escape closes the form and keeps the item
preview. If the game snaps elsewhere, revise the coordinates or resume with arrows.

For a room-relative grid, first choose a front reference wall in that room's
F7 commands. Use this room's reference wall for placement coordinates, or choose
the room placement grid in a preview's F11 menu. Zero starts at the reference
section's endpoint; columns run along it and rows into the room. Choose Use lot
placement coordinates in F11 to return to the normal grid. References need to
be chosen again after reloading or changing the relevant room geometry.

The optional F7 room-layout description also uses your chosen front wall.
This does not change the fixed directions used by J or the adjoining-room guide.

### Facing, alignment and gaps


With a floor-item preview, F11 lets you choose a placed object as a facing or
alignment target. You can face it, measure gaps, align centres or align edges
along the current grid axes. For a wall-mounted target such as a window, you
can centre the preview along its wall.

The wall picker offers wall-gap measurements and alignment. If entering a gap
numerically, use metres; 0, for example 0.2; 0 for a 20 cm gap. The second value
must be zero. These commands adjust the preview; confirm placement separately.
Geometric alignment does not attach a chair or establish an interaction link.
Use a native slot when the objects need that connection.

### If the game snaps away from your request


F11, Use current preview position for placement, accepts the game's current
snapped position instead of the old requested coordinates. Review with F3
and confirm separately. Normal placement checks still apply.

F11, Try game placement anyway, offers a reviewed attempt using the current
preview. Read its explanation: it bypasses several mod checks, including
requested position, room boundary and overlap checks. The game's own legality
and funds checks remain. A separate Enter makes one attempt. Moving or changing
the preview clears that approval. Use the game's Undo if the result is unwanted.


## 12. Settings and key configuration

In the game's settings, Enter selects a category and Right enters its controls.
Up/down move between controls; Left/right adjust sliders, and Enter edits their
numeric values. Enter also activates checkboxes, dropdowns and buttons.
Semicolon and apostrophe move between settings panels, values and Close/Reset.

For searchable dropdowns, type a filter and press Down to enter its results.
Up from the first result returns to the search field. Enter chooses a result;
Escape closes the dropdown.

Mod-specific options are stored in:
BepInEx/config/com.florianbeijers.paralivesaccess.cfg

The file is created after the mod first runs. Close the game before editing it.
The Keyboard section contains configurable mod shortcuts. Several allow None
to disable them. Native game bindings can prevent a conflicting shortcut from
being used; follow the announced conflict rather than assuming the key failed.

BuildMode, GridSpacingMetres changes the ordinary furniture/lot-cursor step
from its default 0.5 m. The allowed range is 0.05 to 2 m; the fine step is one
fifth of that setting. Wall construction uses the game's separate wall grid.


## 13. Troubleshooting and support

### No speech or no mod controls

Check the installation paths in section 1 and that your screen reader is running.
Look in BepInEx/LogOutput.log for ParalivesAccess 0.52.18 and Speech=True.

### A shortcut does not respond

Use F3 to check your current context. Finish text editing or close a modal
window before using world/build shortcuts. Check for a spoken binding conflict.
F2 can still read time and date while you are typing.

### The build catalogue is unavailable

If the camera is on the town map, open F7 and choose a floor and room or outdoors
to bring it in, then try F6. Finish or cancel an active wall preview first.

### An object will not place

Read F3's reason. Check the room, support, selected slot, facing, price and
available space. Home can reframe an off-screen or obstructed target. If the
camera reframes a point covered by the interface, wait for the new review before
confirming. Keep the mouse still while using a keyboard target; mouse movement
or clicks can return control to native placement.

### A linked object is outside the room or cannot be used

Check the supporting object's facing with J and leave enough room for its chair
and walking approach. Move or rotate the supporting furniture, then test use in
Live mode. A confirmed link or clear-place suggestion is not a complete use test.

### A room-size form refuses the value

R takes width; depth. Add a room on the other side takes depth alone, or a pair
with the selected wall's width. See section 6 for examples. F10 positions use
grid addresses; roof dimensions use three values in metres.

### Building scope

The room guides use straight, full-height walls on the construction grid.
Curved/half-wall construction and replacing shared walls are outside those
guides. Floor finishes apply to whole platforms. Other native building tools
may still need mouse input.

For help or bug reports, use:
https://github.com/zersiax/ParalivesAccess/issues

Include the game and mod versions, the mode you were in, the keys or menu choices
you used, and what was announced. For a form problem, include the exact text
you entered. The BepInEx log can help explain a failed action.
