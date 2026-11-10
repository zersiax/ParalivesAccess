ParalivesAccess 0.52.18 - unified release

This accessibility mod adds screen-reader output and keyboard access to Paralives.

J and F7 room descriptions include each object's facing wall or corner. Shift+J
keeps the brief lot overview. Add a room on the other side accepts depth alone
or width; depth when that width matches the selected wall. The full selected
wall sets the new room's width; confirmation is still required before building.
The bundle includes BepInEx 5.4.23.5, Unity Doorstop 4.5.0 and Prism 0.17.3.
Tested with Paralives Early Access 0.1.9b.20995 (Unity 6).

Object labels include the game's interaction-menu heading when available, before
you open that menu. For example, Phoenix's Burn, Fite now also says Treadmill in
the build catalogue, placement feedback and F7 lot explorer. Existing product
titles, collection names and categories remain available.

INSTALLATION
1. Close Paralives.
2. Open the game's installation folder. In Steam, use Manage, Browse local files.
3. Extract the contents of this ZIP directly into the folder containing Paralives.exe.
   Merge the BepInEx folder and allow replacement of the included files.
   The mod must end up at BepInEx/plugins/ParalivesAccess.dll.
   prism.dll, winhttp.dll and doorstop_config.ini must be beside Paralives.exe.
4. Start your screen reader, then launch Paralives normally through Steam.
   The BepInEx log should report ParalivesAccess 0.52.18 and Speech=True.

PLACEMENT SELECTION
Selected desk slots and wall positions remain selected while the cursor catches
up. Moving the mouse after it arrives, or clicking, returns control to native
mouse placement. Keyboard placement still verifies the actual support, position,
facing and funds before a separate Enter.
Wall guides reframe a corner when the game interface covers it. Each new wall
still needs confirmation.

SHORTER ANNOUNCEMENTS
Routine wall and slot announcements give the selected position and current
status. Requested coordinates are repeated when they differ. F3 reviews the
position; F11's detailed report retains dimensions, price and supporting-object
coordinates. Price and size changes during wall placement are announced.
Coordinate reports now start with column and row. Nearby objects use shared room
headings and clear directions. Para selection hints appear on selectable Para
entries. Floor materials use readable names when game translations are missing.

F11's position command is Use current preview position for placement. The native
placement option is Try game placement anyway; its review still explains the
checks it bypasses. The facing/alignment target and surface approval labels keep
their existing wording.

WALL FIXTURE PLACEMENT
Floor-anchored wall fixtures such as sinks now aim inside the wall's height,
avoiding a camera ray that passes over its top before native rotation settles.
The game still determines their floor height. Doors retain their header target
above the temporary preview opening. Placement still checks the actual wall,
coordinates, overlap and funds before a separate Enter.

CEILING PLACEMENT AND ROOM ACCESS
Select a ceiling light, fan or other ceiling item from the catalogue. Choose a
room with the arrows and Enter. Arrows then move across its ceiling in half-metre
steps by default; Shift arrows use one-tenth-metre steps. F10 accepts room column
and row. Zero, zero is the room's interior reference point. End centres the
preview; Home reframes and centres. F11 can choose another room on the current
floor. F3 names the item and room, reads the actual coordinates, ceiling height,
lowest point above the floor, and current placement status.

Hanging height follows the game's ceiling support and the item's dimensions
and native offset. The controls move across the ceiling plane. The full preview
must fit beneath valid support and above the floor. A separate Enter places it;
Escape cancels. Existing ceiling objects can be moved through their F7 commands,
including to another room, with native Cancel, Undo/Redo and no new purchase.

Find a clear place in a room now also checks access. It protects doorway
approaches, keeps previously connected room walking space connected, and checks
known floor-level interaction entries such as chair sides and bed approaches.
The check uses the current native navigation mesh and conservative preview
footprints. If navigation cannot be checked or the bounded room graph is too
large, it reports that rather than claiming a usable location. A suggested
position still needs a separate Enter. This is a conservative current-layout
check; it does not execute every possible interaction or create missing routes.

NEW BUILD AND ROOM COMMANDS
Ctrl+F11 toggles a near-vertical top-down camera in Build mode and restores the
previous angle on the next press. It keeps the camera focus. The same command
is available in F7 and the preview's F11 menu. Text forms keep their input.

With a floor-furniture preview open, F11 offers Find a clear place in the
current room, or in a chosen room on this floor. The search tries a bounded
centre-first half-metre grid and checks the actual native preview, its full
footprint, linked occupants, other objects and sloped-roof mesh clearance.
It leaves a found position ready for review; press Enter separately to place.
It does not rotate or resize on your behalf, though native snapping can change
the angle, which is reported. No result means no place found within the search,
not proof that every possible arrangement is impossible. Escape cancels the
preview; arrows or F11 interrupt the search so you can adjust or choose again.

F11 also offers Accept actual preview coordinates. This discards an old numeric
target and keeps the normal placement checks. When those checks still disagree
with the game, Attempt native placement at the actual preview position reads
an explicit review. A separate Enter makes one native attempt. It bypasses the
mod's room, requested-coordinate, facing, alignment, overlap and roof-clearance
vetoes. Native legality, lot ownership, floor and funds checks still apply.
Moving or changing the preview cancels this approval. Undo remains available.

In Live mode, F7 offers Send selected Paras to a room. Select a room and Enter.
The mod searches for a complete native route for every selected Para, then
requests the normal Go Here interaction. It preserves the selected group.
An unreachable room is refused; the camera-only Shift+F7 room jump still works.
Paused play queues the request until you resume time. Normal game actions can
still interrupt a walking request.

FAST ROOM AND PARA NAVIGATION
Shift+F7 opens a flat list of rooms on the camera's current lot, across floors.
Choose a room and press Enter to move the camera to its centre and close the
list. For an irregular room, it uses a valid interior floor point. The jump
selects the room's floor and stops camera following; it does not move a Para
or change the selected group. F7 also has Go to room centre near the top.

In live mode, Ctrl+F7 opens the Paras on the camera's current lot directly.
Enter opens the usual Para commands. Ctrl+F7 again, or F7, closes the list.
The shortcut does not select or move a Para on its own. The function key is
configurable as Keyboard/LotParasKey; hold Control with that key. None disables
it. Occupied native bindings are refused. Text fields and modal windows retain
their input. These shortcuts also hand off from the N lot cursor.

RECOVER SAVED FURNITURE BELOW THE FLOOR
In build mode, open F7 and choose Recover saved objects below the floor.
It searches the owned lot's saved objects, including hidden ones. Choose an
object, review its location, then choose either Move this object to household
inventory or Sell this object. Opening, reviewing and backing out change
nothing. The final Enter performs the native transaction; native Undo/Redo
remains available. Sold or inventoried objects cannot be recovered twice.

Inventory recovery keeps the saved customizations and resets the height offset.
Use F7, Place saved furniture from household inventory, to start a preview.
Choose its position and confirm separately. Placing inventory costs no money;
Escape keeps it in inventory. A verified linked arrangement instead offers
Recover linked arrangement together. This starts one native move preview for
the parent, supported objects and attached chairs, preserving their slots.
Use the room fit search or choose a position, then confirm separately. Escape,
Undo and Redo restore the captured arrangement, including hidden positions.
This group move costs no money and does not split its members into inventory.
Carried, roped, wall/ceiling or inconsistent groups remain unavailable. Recovery
does not run automatically or operate on somebody else's lot.

NEARBY LANDMARKS
While the N lot cursor is open, F11 reads nearby objects and doorways within
4 m on the current floor. It reads up to eight, nearest first, with distances,
room positions and offsets in the fixed lot directions. Distances refer to
object reference points. The report does not move the cursor or start an action.
F3 still reports the current address and what is directly under the cursor.

BUILD PLACEMENT FEEDBACK
The overlap check recognises a computer and chair connected by the desk's native
linked slots. Their bounding boxes can overlap without blocking slot placement.
Use semicolon to select those slots. Other objects still receive overlap checks.
Wall targeting frames a usable viewing angle, including after top-down surface
placement. Floor-anchored doors aim above the opening while the game sets height.
Shift+arrows make fine floor moves without starting the game's mouse height
gesture. Enter refuses a preview below its actual floor or ground; cancel and
select the object again to reset a bad preview's height.

F7 objects, F11 object targets and compatible slots include room/region, floor
and grid addresses. Object targets also give their direction from the preview.
Wall choices and wall-mounted previews identify the left, right, front or back
wall of the named room. These directions follow the fixed lot axes.

If the native preview lands on an object, feedback names that supporting object
and its height. Enter requires a deliberate surface choice: use semicolon for
compatible slots, or F11, Allow placement on this surface at current position.
That approval only reviews the current position; a separate Enter buys the item.
Moving the preview or changing the supporting object requires another review.
The usual Stay in this room option follows the surface choice when one exists.

Floor-anchored wall items, including some sinks and showers, use the game's
required height. F3 explains why their height cannot be changed with arrows.
If interface controls cover a placement point, F3 reports the obstruction.
Enter reframes that spot without buying; review it, then press Enter again.
Initial framing uses an interior floor point in the selected room. If framing
fails, Home retries and Escape cancels. Native movement differences greater
than 0.1 m are announced after a grid move; F3 gives the detailed coordinates.

FREE LOT EXPLORATION
N opens a lot cursor in build or live mode, without selecting a construction tool.
From F7, choose a room or outdoor area, then press N to explore from there.
Arrows move along the fixed lot axes by the placement-grid step (normally 0.5 m).
Shift+arrows move one fifth as far. Camera rotation does not rotate these axes.
Each move reads the floor layer, column and row, room and position within it,
floor covering or terrain, and objects directly under the cursor. F3 repeats it.
F10 accepts column; row, for example 12; 8. Enter moves the cursor to that
address; Escape cancels the form. Addresses use the same fixed grid as keyboard
furniture placement.
Page Up and Page Down change the native visible floor. Empty space on an upper
floor is reported as having no floor; it cannot open ground actions.
In live mode with a Para selected, Enter opens the game's ground interaction
menu at the cursor. Choose Go Here, or an available ground activity. The game
checks requirements and pathfinding. Exploring and opening the menu do not move
the Para. Closing the menu returns to the same cursor address.
N or Escape closes the cursor. F6/F7 hand control to their browsers. Starting
a build tool, typing in another window or clicking the world releases the cursor.
The camera follows the cursor visually; movement remains inside the current lot.


DEFAULT CONTROLS
F2: read the current in-game time and full date, including while browsing,
placing an item or editing text. Uses the game's 12/24-hour preference. Does not
change focus, pause or speed. Configurable as Keyboard/ReadTimeDateKey in the
BepInEx config; use an unused function key or None to disable.
Arrow keys: move through the current accessible panel.
Enter or numpad Enter: activate the focused control.
Semicolon / apostrophe: previous / next accessible panel.
F3: current screen, panel and context; lot names/addresses, hours and building options.
Shift+F3: repeat the most recent important event.
F4: act on an announced context, such as available conversation topics.
F6: accessible build catalogue.
Getting started with building:
Enter build mode with Tab. On an empty owned lot, press B for walls, then R for
Room size. Keep the suggested size, or use Ctrl+A and type width; depth in the
announced unit. Enter prepares the walls around the camera focus, or the lot
centre when the camera is outside it. The actual size follows the game's wall
grid and is announced before purchase. Wait for each wall's price, then press
Enter to buy that wall. Escape stops; already purchased walls remain.
F3 gives a short review. F11 offers detailed measurements during placement.
During furniture placement, F11 offers Stay in this room. Enter
toggles it and returns to placement without buying the item. It starts off.
When on, arrows stop at that room's actual outline and announce its boundary.
F3 and F11 identify the chosen room. The toggle uses your existing movement
directions and requires no reference-wall setup. It limits the placement point;
the game's collision rules still decide whether the whole item can be placed.
F10 positions, alignment and surface/chair slots also respect the room. A native
snap outside it cannot be confirmed while the boundary is on. The last valid
position remains placeable after a stopped arrow.
The choice lasts until you change it or restart the game. Each new indoor
preview uses its own starting room; outdoor previews announce that no room
boundary applies. Changing or removing the selected room requires switching
the toggle off and on again. An explicitly selected room grid has its own
constraints; Use lot grid in F11 restores the ordinary lot grid.
F6 browses furniture; F7 explores the rooms and objects you have made.
Building a roof:
Open F6, Build, Roofs & Chimneys, Roofs, then Browse items and choose a roof piece.
During its preview, F11 offers Roof dimensions and Fit roof over room. Choose a
room to fit the piece over its actual ceiling, on the floor layer above it. The
fit uses a bounding rectangle with a quarter metre overhang on each side, in the
roof's current orientation. Native roof trim can extend beyond these dimensions.
It preserves height. Irregular outlines can leave
extra covered space; a missing ceiling or unsupported centre/bounds is refused.
Wait for the completed roof-preview report, then Enter places and buys it.
Escape cancels the preview without purchasing it. Fitting and sizing never buy.
Roof dimensions takes width; depth; height in metres, for example 8; 6; 2.5.
Enter updates the preview; Escape keeps its old size. F3 reads the current size
and floor layer. Existing arrow, fine-movement and rotation controls remain.
Moving or rotating explicitly leaves the automatic room-fit target. Roof pieces
can overlap other roof pieces so neighbouring sections can join.
To resize a placed roof, use F7, its floor layer, Outdoor area, the roof, then
Roof dimensions. Enter uses the game's resize transaction; native Undo/Redo
restores/reapplies it. Escape leaves the old size. Native Save/Load keeps roof
size and ceiling support. The game checks the roof's placement point against the
owned lot and can allow eaves beyond its edge. Dimension entry accepts up to
1000 m per axis and respects the piece's native resize limits.
Tab announces build mode even at town-map zoom. If the camera is still zoomed
out to the map, open F7 and choose a floor, then a room or outdoors to zoom in.
F6 becomes available when the game's furniture catalogue is visible again.
F6 can switch directly from F7 to the catalogue. F11 also reads camera details
while F7 is open, keeping your explorer page.
J describes the room under the camera, or the whole lot when outdoors. Shift+J
always describes the whole lot. This also works during a construction preview
and in live mode. Lot descriptions give the shape, overall width and depth,
rooms by floor, shared walls, and wall segments outside completed rooms.
Room descriptions include size, adjoining rooms, furniture and wall attachments.
No front-wall setup is needed: front means the Down-arrow side, back means Up,
and left/right follow the building controls. These directions stay fixed when
the camera turns. You can name rooms in F7; unnamed rooms use the same numbers
in both descriptions and F7. Numbers follow native room creation order on each
floor, counting named rooms too. Building to the left or front and renaming a
room do not change that order. Deleting, merging or rebuilding rooms can change
the remaining numbers; use names for lasting labels.
Sharing a wall does not imply a connecting door. Curved or unsupported
wall faces can leave some attachments out of the contents description.
Measurements follow the game's selected units. Shape describes the actual
boundary; overall width/depth describe its enclosing span, which may include
space outside an irregular outline. Descriptions happen only when requested.
DescribeLotKey in the Keyboard configuration can be J or N; conflicting native
bindings or other accessibility commands take priority.
With no preview or browser open, F3 describes the camera focus: room or outdoors,
rounded distance/direction from the lot centre, and floor. Close to an edge it
also reports distance to the actual lot boundary. J supplies the lot's scale
and shape. F11 reads its construction-grid coordinates
and the lot's minimum/maximum columns and rows. This also works inside F7.
Wall-tool F11 includes lot bounds beside the wall cursor coordinates and reports
the camera focus separately. Lot-grid directions stay fixed when the camera turns.
Wall-tool F11 also reads the current native angle markers. Holding a modifier
during a room guide stays quiet until you attempt a move or confirmation. That
attempt gives one warning until release; it does not buy a wall. Shift+J works
without triggering that warning.

To name a room, open F7, choose its floor and room, then Name room. Type a name
and press Enter, or Escape to keep its existing name. Rename room and Clear room
name are available afterward. F7, F3, J, adjoining-room descriptions and building
commands use your labels. Duplicate names include a room number and floor.
Names are stored when you save the game and travel with saved-game copies.
If a split, merge or other edit makes the destination uncertain, the name is
retained. On an unnamed room, choose Assign a retained room name to reuse it.
Undo can restore a name when the original room outline returns. These are your
own labels; the inspected game data does not provide a catalogue room type.

To add an adjoining room, open F7, choose a floor and room, then Add a room on
the other side. Each wall choice gives its side, such as left wall or front
wall, along with its width and what is on the other side. Choose one with
outdoors on its other side. Directions follow the same fixed lot axes as J,
including when an advanced front-wall reference has been selected. The depth
review repeats the selected side. Angled walls include their diagonal direction.
The new room uses the whole chosen wall as its width. Enter just its depth in
the announced unit; Ctrl+A replaces the suggested value. The mod checks the
space for existing rooms, crossing walls, furniture in the new walls' path,
and the lot boundary. It then selects the native straight-wall tool.
Review the size with F3. The first Enter keeps the shared wall without buying
it again. Wait for each new wall's price and press Enter separately for each
of the three new walls. Room completion is announced once the game recognizes
the enclosure, without an automatic idle wall prompt interrupting it.
Escape stops; purchased walls remain. Doors are added
separately. This supports complete straight, full-height walls on the native
construction grid. A shorter shared side or a second shared side is not yet
supported. F7 can leave the wall tool when it is idle; cancel any active
preview with Escape first.

Optional precision controls:
During a new or existing floor-item preview: arrows move the requested point along the lot's
accessibility grid. Default spacing is 0.5 metres; Shift+arrows uses 0.1 metres.
Right increases column and Up increases row, independent of the camera. From an
off-grid position, the first step reaches the next grid line in that direction;
the untouched axis retains its offset. Coordinates start at zero at the lower
intersection of the lot's enclosing grid rectangle. This point can be outside an
irregular lot. The game still decides whether the preview is inside an owned lot.
Framing a room in F7 makes that room the starting area for new furniture. Ordinary
movement and F3 report the preview's room and position within it, or explicitly
say Outdoors when it is outside every room. These describe the actual preview;
the game can still snap or offset it. Check the spoken position before buying.
F3 also reports placement status and item size. Choose Detailed position and measurements
in F11 for actual coordinates, world rotation and differences from a requested
point. Routine movement reports direction and distance in the game's chosen units.
F11 arrows read only the selected command. Facing/alignment requires another
placed object on the same floor; an empty target list explains this and offers Back.
Enter places the settled preview; Escape cancels. X and comma/period retain the
game's rotation controls. An off-screen request is refused: zoom out and use an
arrow again before confirming. A mouse movement returns control to native
placement; the next arrow starts from the actual preview position.
Grid spacing is configurable under BuildMode, GridSpacingMetres in the BepInEx
mod configuration (0.05 to 2 metres). Fine spacing is one fifth of that value.
During a new or existing floor-item preview, semicolon opens native surface and chair slots.
Arrows browse compatible slots on the current owned lot and floor layer. Each
entry names its supporting object, lot-grid position, occupancy and any native
linked chair or linked surface items. Occupied slots can be read but not targeted.
Enter targets a slot without buying; after native snapping settles, Enter places
the verified preview. The native slot controls position and facing. F3 reads the
slot, dimensions, price and status; Home frames a targeted slot. Arrows abandon
the target and browse again. Semicolon returns to the list, then to the floor grid.
Semicolon and Home check live game bindings. Release Alt, Control and Shift before
targeting or confirming. Escape, native undo, mouse, modal and text input retain
priority. Repeated item previews return to the floor grid; reopen slots as needed.
Confirmation requires the selected slot and support, matching position and facing,
native validity, no unrelated overlap, and sufficient funds for a purchase. Overlapping slots or
an obstructed camera ray can prevent the game from snapping to the chosen slot;
the mod reports that mismatch. It never assigns attachment fields itself.
A linked chair report verifies the authored slot relationship. It does not check
other interaction requirements or whether a Para can reach and use the furniture.
In the native straight, full-height wall tool (B with default game bindings):
arrows move along the game's construction grid; Shift uses the game's smaller
step. This spacing is separate from furniture spacing. The native wall grid must
be enabled. Enter chooses the start, then Enter confirms the settled wall preview.
F10 enters construction column; row, including endpoints for partitions on existing
walls. It moves the requested cursor; a later Enter starts or confirms the native
preview only when snapping agrees. Cancelling the form preserves the wall preview.
F3 reads actual length, native price and the next step. F11 reads exact coordinates. Home frames
the requested target when it is out of view. Existing-wall overlaps are refused.
With no wall preview active, Shift+R begins the advanced corner guide: choose the first corner
with arrows and Enter, then the opposite corner. Use full grid intersections.
Each new wall preview announces its own price and requires another Enter. An adjoining
rectangle can reuse one complete existing side: Enter first acknowledges that side
without buying it, then each of the three new walls requires separate confirmation.
The existing side may consist of connected straight segments of the same full height.
Partial overlaps, hidden/fence sections and multiple existing sides are refused by
this guide. A changed shared wall requires choosing the corners again.
Escape stops the guide and lets the game cancel its current preview. Walls already
placed remain, with native undo per wall action. A room is announced only if the
game supplies a matching new enclosed room. F6 reopens the catalogue when no wall
preview or rectangle guide is active. R is configurable as Keyboard.RectangleRoomKey;
live game binding conflicts are checked before using R, F10, F11 or Home.

In F7 build-mode exploration, choose existing walls or room floor platforms from
the root panel or a room. Walls give construction-grid endpoints, adjacent room names
and attached-item counts. Their endpoints use the wall tool's F10 spacing, independently
of furniture spacing. Commands remove an entire native segment, hide a vertical section
while keeping the separator, or restore a hidden section. Shared-wall edits affect both
rooms. Different native floor layouts may cause whole-wall removal to retain a separator;
feedback reports the observed result without assuming that the rooms merged.

Platform entries give relative height steps, physical height, nominal world elevation,
whole-room area and supported-item counts. Raise on a base floor creates an extra
platform; raise/lower on a raised platform changes its height by one native step.
Add creates another whole-room platform one step higher. Native creation transfers
the source platform's supported items to the new platform. Occupied heights and the
wall-top limit are refused. Lower stops above the base; use the separate removal command
to remove a raised platform. Native removal can move Paras standing on it and cancel
their queued interactions. A removed base floor can be restored. Read boundary gives
native room boundary samples in construction coordinates. Use room/separator walls to
define smaller platform boundaries; these controls do not build individual cells or
duplicate an entire storey.

Structural changes open a review of scope, affected contents and native price/refund
information. Confirm needs another Enter; Backspace or Back without applying leaves
the structure alone. Changed targets, layouts, prices, contents, grids or floor layers
invalidate review. Native undo owns restoration. After applying, F7 closes; reopen it
after native zone refresh for current rooms and heights. Immediate feedback describes
the observed state change, not the finalized geometry or transaction outcome.

During an ordinary floor-item preview, F11's object picker also offers bounding-gap
measurements and alignment. Align centres or matching minimum/maximum edges along the
current lot/room column or row axis. Wall-mounted targets such as windows also offer
centering along their own wall, preserving the preview's height and distance from it.
The wall picker reads a bounding-edge gap or requests zero/a specified positive gap.
For a numeric gap, enter metres; zero, for example 0.2; 0. The projected item width must
fit within the selected wall section. A separate Enter checks the observed native position
before placing. Target movement, resizing/rotation or changed wall/grid geometry requires
choosing the alignment again or clearing its requirement in F11. Choosing native slots
clears that geometric requirement. Signed projection gaps are bounding measurements:
negative means overlapping projections, not necessarily colliding meshes. No gap or
alignment command establishes walking clearance, attachment or interaction usability.

For floor finishes, use F6 to browse a flooring category and choose a surface.
Arrows then select an active floor on the current owned lot and layer. F3 reads
the selected material, current covering, room area and reference-point coordinates.
Home frames the room. Enter reviews applying the finish to the whole floor platform;
a second Enter applies it through the game's painting operation. This operation
has no native covering charge and records native undo. Arrows cancel the review.
Escape closes the controls; F6 returns to the catalogue. A change to the material,
colors, target or room geometry requires a new review. Native undo/redo and physical
mouse input close the floor controls and retain their normal game behavior.
The game creates a default floor when ordinary walls enclose a room. F7 handles
platform construction and height changes separately. Partial-floor finishes and
ceiling painting remain future work.
For a new window or other wall item selected in F6: arrows choose a room or the
remaining wall faces; Enter opens that group's faces. Each face reports width,
and height. Exact start/end endpoints are available through F11's detailed report. Enter chooses
the face and targets its centre without purchasing; supported doors request floor
height instead. Left/right then move along
the face, and up/down change centre height. Shift uses the fine placement step.
End centres horizontally while preserving height; Home frames the selected face.
Semicolon returns to faces, then rooms. Home, End and semicolon check live bindings.
F3 reports the actual centre along the wall, height, price and status.
F11's detailed report includes requested coordinates, dimensions and endpoints.
Distances run from the selected face's announced starting endpoint; height
is above the selected section's lower edge, which need not be the room floor.
Enter places only after the native preview settles on the chosen face at the
requested position. Native snapping can cause a refused alignment; Alt retains
the game's snap bypass. Escape cancels; mouse input releases keyboard targeting.
Ordinary overlap and changed funds/price are checked before the native purchase.
Native repeated-item previews restart room/face selection. Only straight vertical
rectangular sections are offered. F11 can join connected collinear sections for alignment.
Curved/half-wall construction and shared-wall replacement remain future work.
Ceiling placement has separate room controls described above. Native raycasts,
purchases, Undo and save/reload have runtime coverage; physical keys and audible
feedback remain manual checks.
F10 during floor-item placement: enter column; row, for example 5; 5, in a native
text form. Fractional and negative coordinates are accepted. Decimal commas follow
your locale; the separator is always a semicolon. Enter submits a position request
without placing; release Enter, inspect the resulting preview with F3, then use a
separate Enter to confirm. Escape closes the form without cancelling the item move.
The observed position must match the reviewed coordinates within 1 cm. Native snap
displacement is refused; F10 revises the request, or an arrow resumes grid stepping.
For a selected wall face, F10 takes distance along the face; centre height, both in
metres. Height is above that section's bottom. Off-screen or out-of-face requests
are refused. Return from slot targeting with semicolon before using coordinates.
F10 checks the current game binding before opening the form.
F11 during a floor or wall preview: open placement commands. Arrows browse and
Enter chooses a command. F11 closes without placing; a separate Enter is required
after reviewing the resulting preview. Native cancel, undo, mouse and modal input
retain priority. F11 checks live game bindings. Commands include:
- Face another object: choose an exact target by name and lot-grid location.
  Compatible native slots on that target are offered first, including chair slots.
  Geometric facing points the item's native forward axis at the target's centre
  through the native rotation action. Confirmation verifies the actual direction within half
  a degree; this alone does not establish a chair connection or usability. Existing
  edits retain native rotation undo. Native slots keep control of their own facing.
  Moving either object can invalidate the direction: choose the target again or
  Clear the facing target in F11. Clearing retains the current rotation.
- Choose a room grid: first select a front reference wall in the F7 room commands.
  F7 also offers Use this room's reference wall for placement coordinates. In the
  preview, F11 chooses among rooms with a reference or returns to lot coordinates.
  Room zero is the reference section's starting endpoint. Right increases columns
  along it; Up increases rows into the room, including when the room is angled.
  F10 uses that room frame; F11's detailed report gives room coordinates and its rotated footprint.
  The native room boundary rejects exterior reference points, including cutouts in
  irregular rooms. Bounding corners outside the room are reported separately and
  do not establish exact coverage. A changed boundary/reference/lot/floor requires
  a new selection. References and grid selection are not saved across reloads.
- Centre on the whole connected straight wall: after choosing a wall face, combine
  touching collinear sections that share native nodes, a height cluster, side plane,
  bottom and height. End and F10 then use the complete width. The game still owns
  individual sections and actual attachment. Gaps, corners, differing heights and
  unsupported faces are not combined. Geometry changes invalidate the review.
- Choose an equally spaced centre: enter position number; total positions, such as
  2; 3 for the middle of three centres across the whole wall. Centres divide the
  width into equal intervals, including the two end intervals. Only the current
  preview is moved; no additional items are purchased. Bounds and overlap still apply.
- Align a doorway's threshold with the native floor height: supported doors start
  with this target when a face is chosen. F11's detailed report gives the actual bottom-to-floor gap.
  Native door snapping can adjust height; a mismatch refuses confirmation. Doorway
  traversal, approach surfaces and split-height cases require native verification.
F11's Detailed position and measurements includes the preview's bounding footprint. It reports column and
row bounds plus the cell span, accounting for rotation and position across cell
boundaries. Bounds include empty space in irregular objects; they do not measure
walking clearance or exact mesh occupancy. Existing objects offer the same report
in their F7 build-mode commands.
F7: current-lot explorer and available actions.
In build mode, choose a floor and room or find an exact object by name. Choose
Move or rotate this existing object to start the native move preview. Floor-grid,
wall-face, ceiling and slot controls then apply to that object. X and comma/period keep
native rotation; Enter completes the move without repurchasing. Escape uses native
cancellation. The result distinguishes a committed move from a cancelled move and
checks original position, facing and reciprocal placement references on cancellation.
Native undo remains available; rotation may have its own undo action.
Lot-movement items are excluded. Identically named copies remain separate entries.
In an F7 room, choose a front reference wall, then Describe room layout. Left and
right are as you stand at the chosen wall looking into the room; back points away
from that wall. Reports use live native membership, wall attachments and object
bounds to describe positions such as a window centred on a back wall,
furniture near a side wall, and items on or attached to other items. Split sections
of equal height are combined when connected and collinear; other sections remain
numbered separately. Curved/sloped faces are omitted. Room regions refer
to the enclosing rectangle, with native membership excluding outside objects.
Changing the reference geometry invalidates it. Choose references again after reload.
Needs attention in F7: dirty, broken, burning, burnt or spoiled objects, water
spills and soot on the current lot. Enter opens the native menu to clean, mop or repair.
Bus departure stops in F7: loaded stops nearest the selected Para first, including
street signs outside the lot. Each stop announces its current straight-line distance
from that Para in your chosen units. Reopen the list to refresh its order after moving.
Choose the native Take the bus action to walk to that departure stop. When the destination map opens, press L
for its numbered stops. Enter chooses a stop; Cancel bus travel cancels the trip.
L: town and destination browser.
Shift+Enter on an F7 Para entry: add to or remove from the selected group.
Shift+Enter on other F7 entries: activate normally.
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
- After you finish the tutorial, use the L key to find and buy a house. Build-mode additions in this development build still need cumulative in-game verification. For a first household, a house with facilities such as a toilet, shower and cooking equipment provides a useful starting point.
- Your semicolon (;) and quote (') keys are essentially your tab and shift+tab. They move you between areas of the user interface.
- Your f7 is your current lot explorer. Lot essentially means area on the map. Your house is a lot. The beach is a lot. The community center is a lot. f7 gives you access to objects within the lot, as well as other paras and some convenience features.
- L is what is currently the accessible map overlay. You use it by pressing M first to focus the town map, after which you can press L for a list of houses, places to visit, etc.
This is basically your portal to the rest of the game world.
- f6 opens the accessible build catalog overlay. New floor items have an accessibility placement grid and live F3 position/size reports. Straight walls and a guided rectangular boundary use the native construction grid. Flooring surfaces open a review-and-apply workflow for existing room floors. Windows and other new wall items have wall-face selection and centring controls. See the controls and development limitations above.

Apart from that, the arrow keys should get you most places
