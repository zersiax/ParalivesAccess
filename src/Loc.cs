using System.Collections.Generic;

namespace ParalivesAccess
{
    /// <summary>
    /// Central localisation for the accessibility mod.
    /// </summary>
    /// <remarks>
    /// The mod currently ships English only, but every string the screen reader speaks goes
    /// through here from the first feature. Adding a language later is then a data change
    /// (one dictionary plus an extra argument to <see cref="Add"/>) rather than a refactor
    /// that touches every handler.
    ///
    /// Usage:
    ///   Loc.Get("key")                - look up a string
    ///   Loc.Get("key", arg0, arg1)    - look up and format {0}, {1}
    ///
    /// Note: this localises the MOD's own strings. Game content (item names, menu labels) is
    /// already localised by the game and should be read from it via TranslationManager /
    /// TranslatedText, never re-translated here.
    /// </remarks>
    public static class Loc
    {
        private static bool _initialized;
        private static string _currentLang = "en";

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>();

        /// <summary>Initialises the string tables. Called automatically on first use.</summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            InitializeStrings();
            RefreshLanguage();
            _initialized = true;
        }

        /// <summary>
        /// Re-evaluates which language to use. Call when the player changes the game language.
        /// </summary>
        public static void RefreshLanguage()
        {
            // English-only for now. When more languages are added, map the game's language here.
            // The game's own system is GUID-keyed (see docs/game-api.md section 6); mod strings
            // are independent of it.
            _currentLang = "en";
        }

        /// <summary>Looks up a localised string, falling back to the key itself.</summary>
        /// <param name="key">String key.</param>
        /// <returns>The localised text, or the key when it is not defined.</returns>
        public static string Get(string key)
        {
            if (!_initialized)
            {
                Initialize();
            }

            string value;
            if (GetCurrentDictionary().TryGetValue(key, out value))
            {
                return value;
            }

            if (English.TryGetValue(key, out value))
            {
                return value;
            }

            // Returning the key makes a missing string audible rather than silent.
            return key;
        }

        /// <summary>Looks up a localised string and formats it with the given arguments.</summary>
        /// <param name="key">String key containing {0}, {1}, ... placeholders.</param>
        /// <param name="args">Format arguments.</param>
        /// <returns>The formatted string, or the unformatted template if formatting fails.</returns>
        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch (System.FormatException)
            {
                Plugin.Log.LogWarning($"Loc.Get: bad format for key '{key}'");
                return template;
            }
        }

        private static Dictionary<string, string> GetCurrentDictionary()
        {
            switch (_currentLang)
            {
                default:
                    return English;
            }
        }

        /// <summary>Registers one string. Extend the parameter list when adding a language.</summary>
        private static void Add(string key, string english)
        {
            English[key] = english;
        }

        private static void InitializeStrings()
        {
            Add("wall_item_floor_height", "Floor mounted. Height cannot be changed");
            Add("wall_side_exterior", "Outdoor face of the {0}");
            Add("placement_support", "Preview supported by {0}, placement height {1} above this floor");
            Add("placement_support_review", "Surface placement needs a deliberate choice. Use semicolon for compatible slots, or F11, Allow placement on this surface, to approve this exact position");
            Add("placement_below_floor", "Cannot place below the floor or ground. Cancel this preview and select the object again to reset its height");
            Add("quick_paras_live", "The direct lot Para list is available in live mode");
            Add("quick_rooms", "Go to room centre");
            Add("quick_rooms_panel", "Go to room centre. Choose a room; Enter moves only the camera and closes this list");
            Add("quick_rooms_empty", "No rooms are available on this lot");
            Add("quick_room_entry", "{0}, floor layer {1}");
            Add("quick_room_arrived", "Camera at the centre of {0}, floor layer {1}");
            Add("nearby_empty", "No nearby objects or doorways within 4 m on this floor");
            Add("nearby_intro", "{0} nearby objects within 4 m; reading the nearest {1}");
            Add("nearby_intro_all", "{0} nearby objects within 4 m");
            Add("nearby_intro_one", "1 nearby object within 4 m");
            Add("nearby_door", "Doorway");
            Add("nearby_entry", "{0}, {1}");
            Add("recovery_open", "Recover saved objects below the floor");
            Add("recovery_panel", "Saved objects below the floor. Choose an object to review recovery or sale; opening this list changes nothing");
            Add("recovery_empty", "No saved objects below the floor were found on this owned lot");
            Add("recovery_item", "{0}, floor layer {1}, height {2} relative to the floor");
            Add("recovery_changed", "Recovery is unavailable. The object or build context changed; reopen the list");
            Add("recovery_linked", "This object has linked, supported or carried items. Recovery cannot safely separate this group");
            Add("recovery_review", "Review {0}. Enter on the next choice changes this object. Escape or Backspace leaves it unchanged");
            Add("recovery_to_inventory", "Move this object to household inventory and reset its height offset. Keeps its customizations; native Undo is available");
            Add("recovery_sell", "Sell this object for its current game value. Native Undo is available");
            Add("recovery_inventory_requested", "Requested recovery to household inventory with its height offset reset. Use F7, Place saved furniture from household inventory, to place it again; native Undo is available");
            Add("recovery_selling", "Requested sale of this object at its current game value. Native Undo is available");
            Add("recovery_inventory_open", "Place saved furniture from household inventory");
            Add("recovery_inventory_panel", "Saved furniture in household inventory. Enter starts a placement preview; confirm its position separately");
            Add("recovery_inventory_empty", "No saved furniture in household inventory");
            Add("placement_support_allow", "Allow placement on this surface at the current position");
            Add("placement_support_approved", "Surface and position selected. Enter places object when ready. Moving clears this selection");
            Add("placement_support_changed", "The supporting object or preview has changed; choose the surface again");
            Add("placement_view_reframed", "Reframing the view to clear the interface. Nothing purchased. Wait for the preview to settle, then press Enter again");
            Add("target_location", "{0}, floor {1}");
            Add("target_relative", "From the preview in the fixed lot directions: {0}");
            Add("target_right", "{0} right");
            Add("target_left", "{0} left");
            Add("target_back", "{0} towards the back");
            Add("target_front", "{0} towards the front");
            Add("target_same_position", "At the preview's horizontal position");
            Add("wall_side_room", "{0} of {1}");
            Add("wall_side_outside", "{0}, facing the outdoor side in the fixed lot directions");
            Add("cursor_open", "Lot cursor. Arrows move {1}, Shift arrows move one fifth as far. F3 repeats your position, F10 enters column and row. Enter opens ground actions in live mode. {0} or Escape closes");
            Add("cursor_report", "Lot cursor, floor layer {0}. {1}. {2}. {3}. {4}");
            Add("cursor_terrain", "Terrain under cursor");
            Add("cursor_floor", "Floor under cursor: {0}");
            Add("cursor_no_ground", "No floor or ground at this position on this layer. Ground actions unavailable");
            Add("cursor_no_objects", "No objects under cursor");
            Add("cursor_objects", "Objects under cursor: {0}");
            Add("cursor_edge", "Lot boundary. Cursor has not moved");
            Add("cursor_closed", "Lot cursor closed");
            Add("cursor_unavailable", "Lot cursor unavailable");
            Add("cursor_lot_required", "Move the camera to a loaded lot before opening the lot cursor");
            Add("cursor_actions_unavailable", "Ground interactions are unavailable for the selected Para here");
            Add("cursor_coordinate_title", "Lot cursor coordinates");
            Add("cursor_coordinate_hint", "Enter column; row. This moves the exploration cursor only. Press Enter to review that position, or Escape to cancel");
            Add("attention_finder", "Needs attention on this lot, {0}");
            Add("attention_panel", "Needs attention. Enter opens the object's actions");
            Add("attention_none", "No loaded objects on this lot currently need attention.");
            Add("attention_fire", "on fire");
            Add("attention_broken", "broken");
            Add("attention_burnt", "burnt");
            Add("attention_dirty", "dirty");
            Add("attention_spoiled", "spoiled food");
            Add("attention_puddle", "water spill; needs mopping");
            Add("attention_soot", "soot; needs cleaning");
            Add("transit_finder", "Bus departure stops, nearest first, {0}");
            Add("transit_panel", "Bus departure stops, nearest to the selected Para first. Distances are straight-line. Enter opens the stop's actions");
            Add("transit_departure_distance", "about {0} from {1}");
            Add("transit_distance_unavailable", "distance from the selected Para unavailable");
            Add("transit_departure_location_unavailable", "Cannot locate the selected Para to order bus departure stops.");
            Add("transit_town", "Town");
            Add("transit_stop", "Bus stop");
            Add("transit_stop_label", "{0}, stop {1}");
            Add("transit_none", "No loaded bus stops are available in this town.");
            Add("transit_destinations", "Choose a bus destination. Use arrows and Enter. L reopens this list");
            Add("transit_destinations_pending", "The bus destination map is opening. Press L to list its stops.");
            Add("transit_destination", "Stop {0}, {1}");
            Add("transit_destination_gone", "This bus destination is no longer available. Press L to refresh the list.");
            Add("transit_cancel", "Cancel bus travel");
            Add("transit_cancelled", "Bus travel cancelled.");
            Add("transit_chosen", "Travelling to {0}.");
            Add("para_switch_unavailable", "Household switching is unavailable in the current screen or activity.");
            Add("para_switch_none", "No household Para is available to select.");
            Add("para_switch_selected", "Controlling and following {0}, {1} of {2} in the household.");
            Add("para_switch_selected_pending", "Controlling {0}, {1} of {2} in the household. Camera follow requested.");
            Add("para_switch_failed", "Could not switch household Para. Details were written to the log.");
            // ===== General =====
            Add("mod_loaded", "Paralives Access loaded.");
            Add("speech_unavailable", "Paralives Access loaded, but no screen reader was found.");

            // ===== Focus / hover =====
            // {0} is the element label.
            Add("focus_unlabeled", "unlabeled");

            // ===== Load game =====
            // Filename and playtime come from AssetSavedGame; {2} is the exact local save time.
            Add("save_slot_label", "{0}, {1} played, saved {2}");
            Add("save_slot_label_no_date", "{0}, {1} played");
            // {1} is the current household stored inside this exact save package.
            Add("save_slot_label_household",
                "{0}, household: {1}, {2} played, saved {3}");
            Add("save_slot_label_household_no_date",
                "{0}, household: {1}, {2} played");
            Add("save_slot_no_current_household", "none selected");
            Add("save_slot_unnamed", "Unnamed save");
            Add("save_slot_unavailable", "Save slot unavailable");

            // ===== Control state =====
            // Spoken for any ParaToggle-derived control, which is most selectable items.
            Add("state_selected", "selected");
            Add("state_not_selected", "not selected");
            // Exclusive tabs describe the panel they display, not an ordinary selected value.
            Add("state_current_panel", "current panel");
            Add("creator_voice_choice", "Voice: {0}");
            Add("creator_life_stage_choice", "Life stage: {0}");
            Add("creator_gender_choice", "Gender: {0}");
            Add("camera_zoom_value", "Camera zoom {0} percent");
            Add("state_open", "open");
            Add("panel_still_current", "This is already the current panel.");
            Add("panel_still_current_named", "Still the current panel: {0}.");
            // {0} is the element label, {1} its state.
            Add("label_with_state", "{0}, {1}");
            Add("character_thumbnail", "Para: {0}");

            // ===== Character creator: top bar =====
            // Spoken for icon-only buttons, chosen from the click action the prefab wired to them.
            // The wording states what the method body does, not what the button is named.
            Add("action_confirm_and_exit", "Confirm and exit");
            Add("action_cancel_and_exit", "Cancel and exit");
            // ButtonClose, wired to UITrainHouseholdInfoPanel.Back - StopStoryboard(); Hide();
            Add("action_close", "Close");
            Add("action_close_inventory", "Close inventory");
            Add("action_close_lot_details", "Close lot details");
            Add("action_close_work_schedule", "Close work schedule");
            Add("action_back_storyteller_selection", "Back to the Storyteller prompt");
            Add("action_choose_developer_tool", "Choose developer tool");
            Add("action_close_goals", "Close goals");
            Add("action_show_completed_life_goals", "Show completed life goals");
            Add("action_donate_selected_items", "Donate selected items");
            Add("action_close_museum_donations", "Close museum donations");
            Add("action_stop_camera_follow", "Stop following this Para with the camera");
            Add("action_toggle_autosaves", "Show or hide autosaves");
            Add("action_show_autosaves", "Show autosaves");
            Add("action_hide_autosaves", "Hide autosaves");
            Add("action_center_camera_on_para", "Center camera on Para");
            Add("action_center_camera_on_named_para", "Center camera on {0}");
            Add("action_sell_item", "Sell item");
            Add("action_return_to_build_mode", "Return to build mode");
            Add("action_adjust_half_wall_height", "Adjust half-wall height");
            Add("action_increase_half_wall_height", "Increase half-wall height");
            Add("action_decrease_half_wall_height", "Decrease half-wall height");
            Add("action_goal_help", "Get help with this objective");
            Add("action_goal_help_for", "Get help with: {0}");
            Add("action_goal_show_on_map", "Show this objective on the map");
            Add("action_goal_show_on_map_for", "Show on map: {0}");
            Add("action_more_household_options", "More household options");
            Add("action_close_conversation_choices", "Close conversation choices");
            Add("action_close_conversation_results", "Close conversation results");
            Add("action_apply_for_job", "Apply for this job");
            Add("action_show_job_location", "Show this workplace on the map");
            Add("action_show_named_job_location", "Show the {0} workplace on the map");
            Add("action_confirm_work_schedule", "Confirm work schedule");

            // ===== Job offers and work schedules =====
            // The native offer card is itself the Apply control; these parts keep its action first
            // while preserving the visible position, company, rank, salary and qualification gate.
            Add("panel_job_offers", "Job offers");
            Add("job_offers_none", "Job offers for {0}. No positions are available.");
            Add("job_offers_one", "Job offers for {0}. 1 position is available.");
            Add("job_offers_many", "Job offers for {0}. {1} positions are available.");
            Add("job_offers_unknown_para", "the selected Para");
            Add("job_offers_first", "{0} First offer: {1}");
            Add("job_offer_apply", "Apply for {0}");
            Add("job_offer_apply_at", "Apply for {0} at {1}");
            Add("job_offer_rank", "starting rank {0}");
            Add("job_offer_salary", "{0} per day");
            Add("job_offer_qualified", "application requirements met");
            Add("job_offer_qualified_points",
                "application requirements met: {0} of {1} points");
            Add("job_offer_not_qualified",
                "cannot apply: {0} of {1} application points");
            Add("occupation_offer_unavailable", "That job offer is no longer available.");

            // A qualified offer always continues into UIOccupationsChangeSchedule. Its two native
            // controls are raw TMP dropdowns, so the scoped virtual panel exposes their live values.
            Add("occupation_schedule_panel", "Choose a work schedule");
            Add("occupation_schedule_panel_named", "Choose a work schedule for {0}");
            Add("occupation_schedule_change_panel", "Change work schedule");
            Add("occupation_schedule_change_panel_named", "Change work schedule for {0}");
            Add("occupation_schedule_loading", "Schedule choices are loading");
            Add("occupation_schedule_days", "Work days");
            Add("occupation_schedule_hours", "Work hours");
            Add("occupation_schedule_choice", "{0}: {1}, {2} choices");
            Add("occupation_schedule_fixed", "{0}: {1}, fixed");
            Add("occupation_schedule_selected", "{0} set to {1}.");
            Add("occupation_schedule_selected_conflict", "{0} set to {1}. {2}");
            Add("occupation_schedule_unnamed_choice", "Unnamed schedule choice");
            Add("occupation_schedule_choice_unavailable", "That schedule choice is unavailable.");
            Add("occupation_schedule_conflict", "This schedule conflicts with another occupation.");
            Add("occupation_schedule_choose_another", "Choose a different work schedule.");
            Add("occupation_schedule_accept_job", "Accept this job with this schedule");
            Add("occupation_schedule_accept_named_job", "Accept the {0} job with this schedule");
            Add("occupation_schedule_save_changes", "Save this work schedule");
            Add("occupation_schedule_cancel_application", "Cancel this job application");
            Add("occupation_schedule_cancel_changes", "Cancel schedule changes");
            Add("occupation_schedule_cannot_confirm", "This work schedule cannot be confirmed.");
            Add("occupation_schedule_gone", "The work schedule is no longer available.");
            Add("occupation_handler_failed",
                "Accessible work-schedule selection stopped after an internal error.");

            // ===== Outfits panel =====
            // The individual outfits carry no text - they are thumbnails of the character wearing
            // them. {0} is the outfit type, {1} the position, {2} the number of outfits of that type.
            Add("outfit_option", "{0} {1} of {2}");
            // Same, for when the outfit type cannot be resolved. {0} position, {1} total.
            Add("outfit_option_untyped", "Outfit {0} of {1}");
            Add("outfit_add", "Add outfit");
            // The "..." button on the selected outfit type tab. Named by what the next press does.
            Add("outfit_show", "Show outfits");
            Add("outfit_hide", "Hide outfits");

            // ===== Character creator: opening a category =====
            // A category is a door. Pressing Enter on one used to be completely silent, so these
            // say what appeared. {0} is the category name, {1} the number of subcategories.
            Add("creator_subcategories", "{0}, {1} subcategories");
            Add("creator_subcategories_one", "{0}, 1 subcategory");
            // Nothing was revealed anywhere. Said aloud deliberately: silence reads as a broken mod.
            Add("creator_category_empty", "{0}, empty");
            Add("creator_pick_one", "Pick one");
            Add("creator_trait_value", "{0}: {1}");
            Add("creator_choice_selected", "Personality choice selected.");
            Add("creator_choice_selected_named", "Selected {0}.");
            Add("creator_choice_unavailable", "That personality choice is unavailable.");

            // Layout containers inside the contextual list. They are focusable in their own right,
            // so they announce what follows and how much of it. {0} is the number of items.
            Add("group_items", "Items, {0}");
            Add("group_sliders", "Sliders, {0}");
            Add("group_body_sections", "Body sections, {0}");
            // Only UIProfilePickPersonalityTraitItem and the simple-trait evolution row use this:
            // {0} is the visible trait name and {1} the exact visible tooltip description.
            Add("trait_with_description", "{0}. {1}");

            // Generic recipe/variant selection. The game reveals these actions in the same window,
            // so the announcement names the newly selected item and moves to its first next choice.
            Add("skin_choice_unknown", "Unknown choice");
            Add("skin_choice_unavailable", "{0}. Unavailable: {1}");
            Add("skin_choice_selected", "{0} selected.");
            Add("skin_choice_chance", "Success chance {0}.");
            Add("skin_choice_action_one", "Next choice: {0}.");
            Add("skin_choice_actions", "{0} choices. First: {1}.");
            Add("skin_choice_no_actions", "{0} opened, but no available next choice appeared.");

            // Visual swatches carry no display name. Prefer their game-authored palette names and
            // use an approximate color only for direct RGB values.
            Add("swatch_position", "Color swatch {0} of {1}");
            Add("swatch_position_colors", "Color swatch {0} of {1}: {2}");
            Add("swatch_colors", "Color swatch: {0}");
            Add("swatch_generic", "Color swatch");
            Add("swatch_default", "{0}, default");
            Add("color_transparent", "transparent");
            Add("color_black", "black");
            Add("color_dark_gray", "dark gray");
            Add("color_gray", "gray");
            Add("color_light_gray", "light gray");
            Add("color_white", "white");
            Add("color_brown", "brown");
            Add("color_red", "red");
            Add("color_orange", "orange");
            Add("color_yellow", "yellow");
            Add("color_green", "green");
            Add("color_cyan", "cyan");
            Add("color_blue", "blue");
            Add("color_purple", "purple");
            Add("color_pink", "pink");
            Add("color_dark", "dark {0}");
            Add("color_light", "light {0}");

            // Direct RGB skin chips have no native text. Their broad generic hue names can be
            // identical despite a visible depth/undertone difference, so the Skin swatch group
            // receives a compact perceptual description. These fragments remain separate for
            // future localization and are not used for clothing, hair or object swatches.
            Add("skin_tone", "{0} skin tone, {1} undertone");
            Add("skin_tone_muted", "{0} skin tone, muted {1} undertone");
            Add("skin_tone_rich", "{0} skin tone, rich {1} undertone");
            Add("skin_depth_pale", "pale");
            Add("skin_depth_very_light", "very light");
            Add("skin_depth_light", "light");
            Add("skin_depth_medium_light", "medium light");
            Add("skin_depth_medium", "medium");
            Add("skin_depth_medium_dark", "medium dark");
            Add("skin_depth_dark", "dark");
            Add("skin_depth_deep", "deep");
            Add("skin_depth_very_deep", "very deep");
            Add("skin_depth_deepest", "deepest");
            Add("skin_undertone_neutral", "neutral");
            Add("skin_undertone_rosy", "rosy");
            Add("skin_undertone_reddish", "reddish");
            Add("skin_undertone_peach", "peach");
            Add("skin_undertone_golden", "golden");

            // A window with nothing to navigate, read for its content instead of its name. Tutorials
            // are the main case. {0} is the window name, {1} its text.
            Add("window_informational", "{0}. {1}");

            // ===== Body and face sliders =====
            // {0} is the slider name, {1} its value over 100 - the scale the game itself displays.
            Add("slider_value", "{0}, {1} of 100");
            Add("slider_height_value", "{0}, {1}, slider {2} of 100");

            // Body-diagram handles are 2D drag targets. One axis: {0} name, {1} value.
            Add("handle_one_axis", "{0}, {1} of 100");
            // Two axes: {0} name, {1}/{3} axis names, {2}/{4} their values.
            Add("handle_two_axis", "{0}, {1} {2}, {3} {4}");
            Add("axis_horizontal", "width");
            Add("axis_vertical", "height");
            Add("adjust_no_second_axis", "This has only one value.");

            // ===== Household library =====
            Add("library_panel", "Household Library");
            Add("library_filters", "Library filters");
            Add("library_households", "Households");
            Add("library_actions", "Library actions");
            Add("library_navigation_hint", "Up and Down browse households. Right and Left inspect a household's details. Semicolon and apostrophe switch filters, households and actions. Enter selects the focused control.");

            // ===== Town map lot list (L) =====
            Add("lots_panel", "Town map");
            Add("lots_details_hint", "Press F3 for the highlighted lot's name, address, hours, and building options.");
            Add("lot_activity_hours", "Hours: {0} to {1}. {2}");
            Add("lot_activity_open", "Open now");
            Add("lot_activity_closed", "Closed now");
            Add("lot_activity_offerings", "Building options: {0}");
            Add("lot_activity_group", "{0}: {1}");
            Add("lot_activity_subgroup", "{0}, {1}");
            Add("lot_activity_objects", "Building");
            Add("lot_activity_blocked", "{0}, unavailable: {1}");
            Add("lot_activity_unavailable", "Requirements not met");
            Add("lot_activity_error", "Building information could not be read.");
            Add("relationship_location", "{0} is at {1}.");
            Add("relationship_location_unknown", "Current lot unavailable for {0}.");
            Add("relationship_location_out_of_town", "{0} is out of town.");
            Add("relationship_location_error", "The Para's current location could not be read.");
            Add("lots_closed", "Lot list closed.");
            Add("lots_none", "No lots here.");
            Add("lots_none_in_category", "No lots remain in that category.");
            Add("lots_unavailable", "The town map is not open.");
            Add("lots_unnamed", "Unnamed lot");
            Add("lots_unknown_type", "Unknown lot type");
            Add("lots_visitable_category", "Places to visit");
            Add("lots_households_category", "Households");
            Add("lots_for_sale_category", "Lots for sale");
            Add("lots_types_category", "Lot types");
            Add("lots_all_category", "All lots");
            Add("lots_category_one", "{0}, 1 lot");
            Add("lots_category_many", "{0}, {1} lots");
            Add("lots_types_one", "Browse by lot type, 1 type");
            Add("lots_types_many", "Browse by lot type, {0} types");
            // {0} is the game's own lot label, which already carries the type and the price.
            Add("lots_for_sale", "{0}, for sale");
            Add("lots_actions_panel", "{0} actions");
            Add("lots_visit_command", "Visit this lot");
            Add("lots_go_here_command", "Go here on this lot");
            Add("lots_info_command", "View lot information");
            Add("lots_target_gone", "That lot is no longer available on the town map.");
            Add("lots_visit_live_required", "Return to live mode before opening travel actions.");
            Add("lots_visit_para_required", "Select a Para before opening travel actions.");
            Add("lots_visit_ui_blocked", "Close the current dialog before opening travel actions.");
            Add("lots_visit_unavailable",
                "The game's travel action menu is not currently available for that lot.");
            Add("lot_detail_separator", ". ");
            Add("panel_lot_info", "Lot information");
            Add("lot_info_summary", "Lot information. {0}");
            Add("lot_for_sale_status", "For sale");
            Add("lot_owned", "Owned");
            Add("lot_owned_by", "Owned by {0}");
            Add("lot_preview_image", "Lot preview image");
            Add("lot_structure_empty",
                "No walls, zones, or placed objects; 0 bedrooms; 0 bathrooms");
            Add("lot_structure_items_only",
                "No walls or zones; {0} placed objects across {1} layers; {2} bedrooms; {3} bathrooms");
            Add("lot_structure_built",
                "{0} walls; {1} zones or rooms; {2} layers; {3} placed objects; {4} bedrooms; {5} bathrooms");
            Add("lot_facilities_unavailable", "Facility classification unavailable");
            Add("lot_facilities_none",
                "Essential facilities found: none; missing bed, toilet, shower, and fridge");
            Add("lot_facilities_complete", "Essential facilities present: {0}");
            Add("lot_facilities_partial", "Essential facilities present: {0}; missing: {1}");
            Add("lot_cooking_none", "Cooking equipment found: none");
            Add("lot_cooking_found", "Cooking equipment found: {0}");
            Add("lot_readiness", "{0}. {1}");
            Add("facility_bed", "bed");
            Add("facility_toilet", "toilet");
            Add("facility_shower", "shower");
            Add("facility_fridge", "fridge");
            Add("facility_counter", "counter");
            Add("facility_stovetop", "stovetop");
            Add("facility_oven", "oven");
            Add("facility_microwave", "microwave");
            Add("facility_shop_purchase", "shop purchase");
            Add("facility_museum_donation", "museum donation box");
            Add("shop_product_with_price", "{0}, {1}");
            Add("item_product_identity", "{0}, {1}");
            Add("shop_product_named_kind", "{0} {1}");
            Add("shop_product_kind_plush", "plush toy");
            Add("shop_product_kind_mug", "mug");
            Add("shop_product_kind_poster", "poster");
            Add("shop_product_kind_tote", "tote bag");
            Add("shop_product_kind_puzzle", "puzzle");
            Add("shop_product_kind_puzzle_box", "puzzle box");
            Add("shop_product_kind_snow_globe", "snow globe");
            Add("shop_product_i_love_prlv", "I love P R L V");
            Add("shop_product_identical_displays", "{0} identical display copies");

            // ===== Build catalogue and item placement =====
            Add("hotkey_missing", "The configured {0} key is not available on this keyboard.");
            Add("hotkey_unverified", "Cannot verify whether {0} conflicts with a game binding.");
            Add("hotkey_conflict", "{0} is currently bound to the game action {1}.");
            Add("virtual_audio_on", "Close audio perspective on.");
            Add("virtual_audio_off", "Close audio perspective off.");
            Add("virtual_audio_live_mode_only",
                "Close audio perspective is available in a loaded game's live mode.");
            Add("virtual_audio_not_ready",
                "Close audio perspective could not start because the game's audio listener is not ready.");
            Add("virtual_audio_patch_unavailable",
                "Close audio perspective is unavailable because its audio hooks did not load.");
            Add("virtual_audio_failed", "Close audio perspective stopped after an internal error.");
            Add("catalog_item_affordable", "{0}, {1}, affordable");
            Add("catalog_item_unaffordable", "{0}, {1}, unaffordable");
            Add("catalog_panel", "Build catalogue");
            Add("catalog_categories_level", "Build catalogue, category level {0}");
            Add("catalog_browse_items", "Browse {0} items");
            Add("catalog_back", "Back");
            Add("catalog_closed", "Returned to the game's build controls. F6 reopens the catalogue browser.");
            Add("catalog_unavailable", "The build catalogue is not open. Enter build mode first.");
            Add("catalog_no_content", "No ordinary furniture is available in this category.");
            Add("catalog_entry_unavailable", "That catalogue entry is no longer available.");
            Add("catalog_preview_failed", "The game did not create an item preview. Catalogue browser restored.");
            Add("catalog_handler_failed", "The build catalogue browser was disabled after an internal error.");

            Add("placement_ready", "{0}. {1}. Arrows move; Enter places; Escape cancels. F3 reviews; F11 opens more options.");
            Add("placement_grid_position", "Column {0}, row {1}");
            Add("wall_ready", "Wall tool. Press {0} to make a room by width and depth. Arrows and Enter draw individual walls. Escape cancels.");
            Add("wall_position", "Construction grid column {0}, row {1}, floor layer {2}");
            Add("wall_grid_context", "Construction grid spacing {0}; Shift spacing {1}. Right increases column; Up increases row. Coordinates count grid intersections from zero at the lower corner of the lot's enclosing grid rectangle. The axes do not turn with the camera");
            Add("wall_angle_markers", "Angle markers: {0}");
            Add("wall_preview", "Wall from {0}. Length {1}, price {2}. Enter places this wall");
            Add("wall_snap", "Native snapping moved the cursor from the requested {0}");
            Add("wall_illegal", "Cannot build a wall here");
            Add("wall_wait", "Wall not ready. Wait; F3 checks position");
            Add("wall_view_reframed", "Reframing this wall point to clear the interface. Nothing built. Wait for the next wall review");
            Add("wall_pointer_over_ui", "Interface blocks this wall point. Home reframes; review before confirming");
            Add("wall_grid_disabled", "The game's wall grid is off. Enable the wall grid before using keyboard construction");
            Add("wall_grid_unavailable", "The construction grid, owned lot, or floor changed. Leave and reopen the wall tool on the intended lot and floor");
            Add("wall_release_modifiers", "Release Control and Alt before confirming a straight wall");
            Add("wall_placed", "Wall placed. Native price {0}");
            Add("wall_refused", "Wall not placed. F3 reads its position, length and price");
            Add("wall_start_refused", "The game did not start a wall here. Move to a valid position inside the owned lot");
            Add("wall_rectangle_busy", "Finish or cancel the current wall preview or rectangle guide before starting another rectangle");
            Add("wall_rectangle_first", "Rectangle guide. Move to the first corner on a full construction grid intersection, then press Enter");
            Add("wall_rectangle_second", "First corner: {0}. Move to the opposite corner and press Enter");
            Add("wall_rectangle_invalid", "Choose two different corners on full grid intersections, with both width and depth greater than zero");
            Add("wall_rectangle_plan", "Room: {0} by {1}. Each wall has its own price and needs Enter to buy. Escape stops");
            Add("wall_rectangle_locked", "Rectangle edges are fixed. Release modifiers, then Enter confirms the announced wall. Escape stops the guide");
            Add("wall_rectangle_edge", "Rectangle wall {0} of 4. Enter confirms this wall only");
            Add("wall_rectangle_preparing", "Preparing wall {0} of 4. Wait for its price before pressing Enter");
            Add("wall_replacement_unsupported", "This wall overlaps an existing wall. Keyboard construction does not yet support replacing or sharing wall sections. No keyboard confirmation was sent");
            Add("wall_rectangle_snap", "The native wall start does not match the requested corner. Rectangle guide stopped");
            Add("wall_rectangle_endpoint", "Native snapping moved this wall away from the planned endpoint. It cannot be confirmed as part of this rectangle. Escape stops the guide");
            Add("wall_rectangle_stopped", "Rectangle guide stopped after {0} confirmed walls. Placed walls remain; use native undo to remove them");
            Add("wall_room_verified", "Room complete. The game recognizes a new enclosed room here, area {0}");
            Add("wall_room_unverified", "The planned boundary was completed. A matching new room could not be verified from the game's room data");
            Add("wall_handler_failed", "Keyboard wall construction stopped after an error. Details are in the mod log");
            Add("wall_frame_failed", "The camera could not frame this construction target");
            Add("wall_lot_required", "Move the camera over the owned lot where you want to build, then reopen the wall tool");
            Add("wall_anchor_unavailable", "No verified starting point is available on this lot. Move the camera to a buildable area and reopen the wall tool");
            Add("wall_outside_lot", "The cursor is outside the intended owned lot");
            Add("surface_unknown", "Unknown surface");
            Add("surface_flooring", "{0} flooring");
            Add("catalog_surface", "{0}, surface material");
            Add("catalog_surface_selected", "{0} selected for the native painting tool");
            Add("catalog_surface_not_selected", "The game did not select this surface. Choose it again before applying a covering");
            Add("floor_cover_open", "Floor covering: {0}. Arrows choose a floor; Enter reviews, then applies. F3 details; Home frames the room; F6 catalogue");
            Add("floor_cover_context", "Floor covering {0}. {1}. {2}");
            Add("floor_cover_choose", "Enter reviews the selected floor");
            Add("floor_cover_confirm", "Enter applies the reviewed covering; arrows cancel the review and choose another floor");
            Add("floor_cover_target", "{7}. Floor {0} of {1}, level {2}, area {3}, height {4}. Current covering: {5}. Room centre: {6}");
            Add("floor_cover_target_short", "{0}. Current covering: {1}");
            Add("floor_cover_platform", "{0}, floor height {1}");
            Add("floor_cover_no_targets", "No usable floors here. Enclose a room or choose another floor level");
            Add("floor_cover_unavailable", "Floor covering is unavailable. Select a floor surface in the build catalogue while on an owned lot");
            Add("floor_cover_not_ready", "The floor or selected covering is not ready. Wait for the room update, then review again");
            Add("floor_cover_review", "Apply {0} to the {2} floor in {1}? Can place. Enter applies; arrows choose another floor");
            Add("floor_cover_review_changed", "The floor or covering changed after review. Nothing was applied. Press Enter to review it again");
            Add("floor_cover_context_changed", "Lot or floor changed. Select the covering again");
            Add("floor_cover_targets_changed", "Available floor targets changed. Review the current target again");
            Add("floor_cover_release_modifiers", "Release Control, Alt and Shift before choosing or applying a covering");
            Add("floor_cover_unchanged", "This floor already has the selected covering and colors. Nothing changed");
            Add("floor_cover_applied", "{0} applied to the floor. Native undo can restore the previous covering");
            Add("floor_cover_not_verified", "The game did not confirm {0} on this floor. Inspect it before trying again");
            Add("floor_cover_failed", "Floor covering stopped after an error. The result is unverified; inspect the floor before trying again. Details are in the mod log");
            Add("floor_cover_closed", "Floor covering controls closed");
            Add("floor_cover_framed", "Camera focused on the selected floor's room");
            Add("wall_item_open", "Placing {0} on a wall. Arrows choose a room or the other wall faces; Enter opens its faces. Semicolon returns to the previous list. Escape cancels the native preview");
            Add("wall_item_no_faces", "No supported straight wall faces are available on this owned lot and layer. Curved, sloping and disabled sections are excluded. Enter refreshes the list; Escape cancels");
            Add("wall_item_room", "{0}, area {1}, layer {2}");
            Add("wall_item_outside_group", "Faces outside the enclosed rooms");
            Add("wall_item_group", "{0}. {1} wall faces. Enter chooses a face");
            Add("wall_item_face", "Face {0} of {1}, {2}. Width {3}, height {4}. Enter targets this section; floor-anchored items and supported doors request floor height, other items request the centre. Semicolon returns to rooms");
            Add("wall_item_address", "This wall's lower edge runs from starting endpoint {0} to ending endpoint {1}. Right increases along-distance in that direction. Accessibility grid spacing {2}");
            Add("wall_item_address_unavailable", "Lot grid addresses for this wall are unavailable");
            Add("wall_item_position_controls", "Wall selected. Left/right move along it; up/down change height. Shift: fine steps. End centres; Home frames. F10 coordinates; F11 options. Semicolon returns to faces. Enter places object; Escape cancels");
            Add("wall_item_position_controls_fixed", "Wall selected. Left/right move along it; height is fixed. Shift: fine steps. End centres; Home frames. F10 coordinates; F11 options. Semicolon returns to faces. Enter places object; Escape cancels");
            Add("wall_item_position", "Face {0}. Centre {1} from the start of a {2} section, height {3}. Price {6}. {7}");
            Add("wall_item_ready", "Can place");
            Add("wall_item_wrong_face", "Target wall not reached. Home reframes; F3 checks position");
            Add("wall_item_alignment_changed", "Position differs from your request. F3 reads both positions; F11 offers placement options");
            Add("wall_item_outside", "Object extends beyond this wall section. Move it inward or choose a larger section");
            Add("wall_item_out_of_view", "Wall target outside the view. Home reframes");
            Add("wall_item_move", "{0}. Centre {1} from the start, height {2}");
            Add("wall_item_room_side", "{0}, {1}");
            Add("wall_item_requested", "Requested centre {0}, height {1}");
            Add("wall_item_price_changed", "Price now {0}");
            Add("wall_item_context_changed", "The intended lot or layer changed. Choose a wall again");
            Add("wall_item_targets_changed", "The wall geometry or target changed. Choose a face again before placing");
            Add("wall_item_manual", "Mouse input released the wall target. Enter in the face list chooses a keyboard target again. F11 offers a reviewed native placement attempt");
            Add("wall_item_unaffordable", "Not enough money. Price now {0}");
            Add("wall_item_result", "Wall attachment verified on face {0}, centre {1} from its left edge and {2} above its lower edge");
            Add("wall_item_result_unverified", "Attachment at the selected wall coordinates was not verified");
            Add("wall_item_not_placed", "Object not placed. F3 checks the preview");
            Add("wall_item_unavailable", "Wall-item keyboard placement is unavailable. Escape cancels the native preview. Details are in the mod log");
            Add("placement_grid_context", "{0} preview. {1}. {2}. Grid spacing {3}; fine spacing {4}. Right increases column; Up increases row. Coordinates start at zero at the lower corner of the lot's enclosing grid rectangle; camera rotation does not change the axes. Semicolon opens surface and chair slots.");
            Add("slot_open", "Placement positions. Arrows browse; Enter selects a position without buying. F3 reviews; F11 has details. Semicolon returns to the floor grid; Escape cancels");
            Add("slot_none", "No compatible native slots found on this owned lot and floor layer. Enter refreshes the list. Semicolon returns to the floor grid");
            Add("slot_entry", "Slot {0} of {1}. {2}");
            Add("slot_surface", "Surface slot on {0}, {1}. {2}");
            Add("slot_chair", "Chair position at {0}. {1}. {2}");
            Add("slot_owner_details", "{0} coordinates: {1}");
            Add("slot_available", "Available for this preview");
            Add("slot_occupied", "This slot is occupied by another item");
            Add("slot_targeted", "Slot targeted. Wait for native snapping, then Enter places the verified preview. The slot sets its facing. Home frames the target. Arrows browse other slots; semicolon returns to the list");
            Add("slot_preview", "Preview {0}. Price {1}");
            Add("slot_ready", "Can place. Enter places object");
            Add("slot_wrong_target", "Object has not reached this position. Home reframes; arrows choose another position");
            Add("slot_alignment_changed", "Position or facing does not match the selected slot. Wait or choose another slot");
            Add("slot_changed", "The selected slot, supporting item or compatibility changed. Select a slot again");
            Add("slot_context_changed", "The owned lot or floor layer changed. Select a slot in the current list");
            Add("slot_out_of_view", "This slot is outside the camera view. Home frames it; zoom out if needed");
            Add("slot_release_modifiers", "Release Control, Alt and Shift before targeting or confirming a slot");
            Add("slot_manual", "Native mouse placement took control. Slot confirmation was cleared; choose a slot again to target it");
            Add("slot_closed", "Slot picker closed. Arrows resume the floor grid from the preview's current position");
            Add("slot_not_placed", "The game did not complete placement in this slot. F3 reads the current preview; nothing will be retried automatically");
            Add("slot_unavailable", "Keyboard slot placement is unavailable. Details are in the mod log. Semicolon returns to the floor grid if the picker is open");
            Add("slot_no_chair_link", "This surface slot has no native chair link");
            Add("slot_link_empty", "Linked chair slot is empty");
            Add("slot_link_chair", "Linked chair present: {0}. Other interaction requirements and access paths are not checked");
            Add("slot_no_surface_link", "No surface slot links to this chair slot");
            Add("slot_link_surfaces_empty", "Linked surface slots have no verified placed items");
            Add("slot_link_items", "Linked surface items: {0}. Other interaction requirements and access paths are not checked");
            Add("slot_link_unverified", "A linked slot relationship could not be verified");
            Add("slot_chair_placed", "Native chair slot and attachment to {0} verified");
            Add("slot_surface_placed", "Native surface slot and support on {0} verified");
            Add("slot_result_unverified", "The selected slot relationship could not be verified after placement. Inspect the item before placing another");
            Add("explorer_build_panel", "Build-mode lot explorer. Choose a floor and room, or find an object by name");
            Add("placement_actions_open", "Placement commands. Arrows browse; Enter selects. F11 closes without placing");
            Add("facing_no_targets", "No other placed objects on this floor to use for facing or alignment. Enter goes back; F11 returns to placement.");
            Add("room_stay_off", "Stay in this room: off");
            Add("room_stay_on", "Stay in {0}: on");
            Add("room_stay_on_next", "Stay in room: on for new indoor placements");
            Add("room_stay_enabled", "Staying in {0}");
            Add("room_stay_disabled", "Room boundary off");
            Add("room_stay_edge", "{0} boundary. Stay in room is on");
            Add("room_stay_no_room", "Move the preview inside a room before turning this on");
            Add("room_stay_changed", "The room has changed. Turn Stay in this room off and on again in F11");
            Add("room_stay_unavailable", "Room boundary control is unavailable");
            Add("room_stay_outdoors", "Outdoor placement; no room boundary");
            Add("placement_actions_close", "Close placement commands");
            Add("placement_actions_back", "Back to placement commands");
            Add("placement_actions_closed", "Placement commands closed. Review the preview before confirming");
            Add("placement_actions_unavailable", "Placement commands are unavailable. Details are in the mod log");
            Add("facing_choose", "Choose an object for facing, alignment or gap measurements");
            Add("alignment_unavailable", "Alignment is unavailable. Use an ordinary floor preview with readable geometry and leave native slots or attachments before requesting alignment.");
            Add("alignment_lot_axes", "lot coordinates");
            Add("alignment_gaps", "Bounding projection gaps to {0}: column axis {1}; row axis {2}, in {3}. Positive means separated; negative means overlapping projections. These are bounding measurements, not walking clearance or proof of collision.");
            Add("alignment_wall_gap", "Nearest bounding edge is {0} from this face's plane, positive toward the announced face normal. Negative means the bound crosses or lies behind the plane. This is not walking clearance.");
            Add("alignment_wall_extent", "The object's projected width extends beyond this wall section. Choose a section covering it or align along the wall first.");
            Add("alignment_requested", "Alignment requested. Review the native preview with F3, then press Enter separately to place. The command does not attach items or prove usability.");
            Add("alignment_changed", "The alignment target, object shape or coordinate frame changed. Choose the alignment again or clear it in F11.");
            Add("alignment_mismatch", "Alignment does not match. Choose it again or clear it in F11.");
            Add("alignment_observed", "Requested alignment position observed. Bounding measurements do not establish walking clearance.");
            Add("alignment_walls", "Choose a wall for gap measurements or edge distance");
            Add("alignment_clear", "Clear the alignment requirement");
            Add("alignment_cleared", "Alignment requirement cleared; the current preview position is retained.");
            Add("alignment_measure", "Read bounding gaps");
            Add("alignment_commands", "Align bounding centres or edges");
            Add("alignment_wall_centre", "Centre along the target object's wall, preserving distance from that wall and current height");
            Add("alignment_axis_command", "Align {1} on the {0} axis of the current coordinate frame");
            Add("alignment_rows", "row");
            Add("alignment_columns", "column");
            Add("alignment_centres", "centres");
            Add("alignment_minimum", "minimum edges");
            Add("alignment_maximum", "maximum edges");
            Add("alignment_wall_touch", "Request zero bounding-edge distance from this wall face");
            Add("alignment_wall_distance", "Enter a bounding-edge distance from this wall face");
            Add("alignment_distance_hint", "Enter wall gap in metres; then zero, for example 0.2; 0. Gap must be nonnegative. This moves only the preview.");
            Add("wall_coordinate_hint", "Enter construction column; row. Each unit is {0}. Native snapping still applies. Use endpoints on existing walls to draw a partition, or two opposite corners for the rectangle guide.");
            Add("wall_coordinate_mismatch", "Native snapping differs from the entered wall coordinate. Adjust the coordinate or use arrows before confirming.");
            Add("wall_shared_unsupported", "This rectangle overlaps existing walls beyond the supported adjoining-room layout. It may reuse one complete side made of connected, full-height straight walls with endpoints at both corners. Partial overlaps and multiple existing sides are not supported by this guide.");
            Add("wall_shared_review", "One complete side already exists, using {0} native wall segments, beginning at {1}. Press Enter to reuse it without buying or replacing it. The remaining three sides require separate wall confirmations.");
            Add("wall_shared_accepted", "Existing side acknowledged. Preparing the three new sides; the shared wall remains in place.");
            Add("wall_shared_changed", "The reviewed shared wall changed. Cancel this guide and choose the corners again.");
            Add("structure_walls", "Browse and edit existing walls on this floor");
            Add("structure_floors", "Construct or change room floor platforms");
            Add("structure_no_walls", "No supported straight walls on this floor.");
            Add("structure_no_adjacent_rooms", "none");
            Add("structure_no_floors", "No room floor platforms on this floor. Enclose a room first; the game creates its base floor.");
            Add("structure_unavailable", "This structure command is unavailable in the current context or at this height. Wait for construction to finish. Platform steps must stay above the base floor and below the wall top, without another floor at that height.");
            Add("structure_changed", "The reviewed structure or context changed. Open the command again and review the current result before applying.");
            Add("structure_unverified", "The native structure operation did not produce a verified result. Reopen F7 to inspect the structure; the command was not retried.");
            Add("structure_funds", "There is not enough money for the reviewed structure operation.");
            Add("structure_wall", "Wall {0}, construction-grid endpoints from {1} to {2}, using the wall tool's spacing. Adjacent room numbers: {3}. Attached items: {4}. This native segment is shared by every listed room; editing it affects both sides.");
            Add("structure_floor", "Platform {0}, {1}. Native height step {2}, {3} above this floor's base; nominal world elevation {4}. Covers the whole room, area {5}. Supported items: {6}. {7}");
            Add("structure_floor_present", "Floor present");
            Add("structure_floor_removed", "Floor removed or hidden");
            Add("structure_boundary", "Whole-room boundary in construction-grid coordinates, using the wall tool's spacing and following native boundary samples: {0}. These are boundary points, not individually editable flooring cells.");
            Add("structure_delete_wall", "Review removal of this entire native wall segment");
            Add("structure_hide_section", "Review hiding vertical section {0}, retaining the room separator");
            Add("structure_restore_section", "Review restoring vertical section {0}");
            Add("structure_whole_wall", "whole segment");
            Add("structure_section", "vertical section {0}");
            Add("structure_delete_wall_review", "Review removal: {0}. Scope: {1}. Native section-value estimate {2}. Attached items and room boundaries may change. Different platform layouts can cause the game to retain a separator. Native undo owns restoration.");
            Add("structure_restore_wall_review", "Review restoration: {0}. Scope: {1}. Native cost {2}.");
            Add("structure_wall_restored", "Native wall section restoration observed. Reopen F7 for the refreshed boundary.");
            Add("structure_wall_deleted", "Native wall segment removal observed. Room merging must be read after the game refreshes its zones; native undo can restore the operation.");
            Add("structure_separator_retained", "Native wall section removal observed; the underlying room separator remains. Reopen F7 after the native zone refresh.");
            Add("structure_floor_raise", "Review raising this platform one native step; a base floor creates an additional platform");
            Add("structure_floor_lower", "Review lowering this raised platform one native step");
            Add("structure_floor_add", "Review creating an additional whole-room platform one step above this one");
            Add("structure_floor_remove", "Review removing this raised platform");
            Add("structure_floor_remove_review", "Review removal: {0}. Native refund {1}. Supported items lose this floor; the native operation can move Paras using it and cancel their queued interactions. The base floor and other platforms remain under native control. Native undo owns restoration.");
            Add("structure_floor_restore", "Review restoring this room's removed base floor");
            Add("structure_floor_boundary", "Read the full native room boundary");
            Add("structure_floor_create_review", "Review new whole-room platform: {0}. New relative height step {1}, {2} above the floor base. Native cost {3}. The game transfers this platform's supported items to the new platform. Confirm applies one native construction transaction.");
            Add("structure_floor_move_review", "Review platform height: {0}. New relative height step {1}, {2} above the floor base. Native cost {3}. Supported items follow the native floor update. Confirm applies one native transaction.");
            Add("structure_floor_restore_review", "Review base floor restoration: {0}. Restore the whole native room boundary using the game's undoable restoration operation.");
            Add("structure_floor_changed", "Native platform state change observed. Reopen F7 after the native zone refresh to read its resulting boundary and height. Traversability is a separate check.");
            Add("structure_confirm", "Confirm this reviewed native operation");
            Add("structure_cancel", "Back without applying");
            Add("facing_clear", "Clear the facing target");
            Add("facing_cleared", "Facing target cleared; current rotation retained");
            Add("facing_target", "Target {0}: {1}. {2}");
            Add("facing_prefer_slots", "Choose a compatible native slot on this object, including chair slots where available");
            Add("facing_geometric", "Point the item's native forward direction toward this object");
            Add("facing_requested", "Facing requested toward {0}. Review the settled preview with F3; a separate Enter confirms placement");
            Add("facing_verified", "Native forward direction faces {0} within half a degree. This geometric alignment does not verify a chair connection, walking access or usability");
            Add("facing_mismatch", "Facing differs from the target by {0} degrees. Choose the target again, use a native slot, or clear the facing target in F11 before confirming");
            Add("facing_changed", "The facing target or placement context changed. Choose or clear the target in F11 before confirming");
            Add("facing_slot_controls", "The native slot controls facing. Use its slot controls, or move clear of the slot before geometric rotation");
            Add("facing_unavailable", "Geometric facing requires an upright floor-item preview and a different live target on the same lot and floor");
            Add("room_grid_use", "Use this room's reference wall for placement coordinates");
            Add("placement_room_context", "{0} preview. {1}. {2}. Room grid spacing {3}; fine spacing {4}. Origin and axes use the chosen room reference wall. F10 enters room coordinates; F11 changes grids or facing targets");
            Add("room_grid_choose", "Choose a room placement grid");
            Add("room_grid_use_lot", "Use lot placement coordinates");
            Add("room_grid_lot", "Using lot placement coordinates");
            Add("room_grid_coordinate_label", "Room grid");
            Add("room_grid_choice", "{0}. {1}");
            Add("room_grid_reference_ready", "Front reference wall available");
            Add("room_grid_selected", "Room grid selected. Zero is the chosen reference section's starting endpoint. Right increases column along that wall; Up increases row into the room. F10 enters room coordinates; F11 can return to lot coordinates");
            Add("room_grid_changed", "The selected room boundary, reference, lot or floor changed. Choose a room grid again or use lot coordinates in F11");
            Add("room_grid_outside", "Position outside the selected room. Move it inside before placing");
            Add("room_grid_inside", "Reference point inside the selected native room");
            Add("room_grid_bounds_cross", "Part of the object's bounding box is outside the room. Clearance not checked");
            Add("room_grid_report", "Room coordinates: {0}. {1}");
            Add("coordinate_room_hint", "Enter column then row in the selected room grid, separated by a semicolon. Spacing {0}; zero is the chosen reference section's starting endpoint, columns run along the wall and rows into the room");
            Add("wall_span_centre", "Centre on the whole connected straight wall");
            Add("wall_span_divide", "Choose an equally spaced centre on the whole wall");
            Add("wall_span_selected", "Whole straight wall: {0} native sections, total width {1}. Along-distance and centring now use the complete wall");
            Add("wall_span_result", "Placement on the whole straight wall verified across {0} native sections: centre {1} along, height {2} above its bottom");
            Add("wall_span_division_hint", "Enter position number then total positions, separated by a semicolon, for example 2; 3 for the middle of three equally spaced centres. Positions and totals are whole numbers from 1 to 100. Only the current item is moved");
            Add("wall_span_division_invalid", "Enter a whole position number between 1 and the total, and a total from 1 to 100. No spacing request applied");
            Add("door_align_floor", "Align a doorway's threshold with the native floor height");
            Add("door_floor_unknown", "Doorway floor height unavailable. Choose a supported door and wall face");
            Add("door_floor_requested", "Floor-level doorway requested. Native door snapping still decides the result; F3 reports the actual threshold gap before confirmation");
            Add("door_threshold", "Door threshold is {0} relative to the native wall floor height. {1}. Doorway traversal and approach surfaces are not verified");
            Add("door_native_height", "The game adjusts this item's height automatically");
            Add("door_manual_height", "This item has no automatic bottom-of-wall snap");
            Add("edit_command", "Move or rotate this existing object");
            Add("edit_started", "Editing {0}. Arrows move the preview; X and comma or period use the game's rotation controls. Semicolon opens slots. F10 enters coordinates. Enter confirms the move without repurchasing; Escape uses the game's move cancellation");
            Add("edit_unavailable", "This object cannot be moved right now");
            Add("edit_build_required", "Enter Build mode before moving an object");
            Add("edit_finish_placement", "Finish the current placement before moving another object");
            Add("edit_close_window", "Close the current window before moving an object");
            Add("edit_finish_rotation", "Finish rotating the current object first");
            Add("edit_keyboard_required", "Switch to keyboard controls before using this move command");
            Add("edit_target_gone", "This object is no longer available. Choose another object");
            Add("edit_owned_lot", "Only objects on your own lot can be moved");
            Add("edit_type_unsupported", "This type of object cannot use the move command");
            Add("edit_disabled", "Keyboard object movement is disabled");
            Add("edit_ceiling_disabled", "Keyboard ceiling placement is disabled");
            Add("edit_completed", "Moved {0}. {1}. Existing item; no purchase. Native undo is available");
            Add("edit_cancelled", "Move cancelled. Original position, facing and placement references verified");
            Add("edit_unverified", "The existing item's final placement or restoration could not be verified. Inspect it before continuing");
            Add("coordinate_title", "Placement coordinates");
            Add("build_mode_intro", "Build mode. F6 opens furniture and materials. B opens walls. F7 explores this lot.");
            Add("build_mode_map_intro", "Build mode. F6 opens furniture and materials. B opens walls. At town-map zoom, use F7 and choose a room or outdoors to zoom in first.");
            Add("catalog_town_map", "Furniture and materials are hidden at town-map zoom. Use F7 and choose a room or outdoors to zoom in first.");
            Add("build_camera_location", "Camera focus: {0}. Floor layer {1}");
            Add("build_camera_area", "{0}, {1}");
            Add("build_camera_room", "room {0}");
            Add("build_camera_outdoors", "outdoors");
            Add("build_camera_unavailable", "Camera position is not available yet");
            Add("build_camera_lot_unavailable", "Cannot identify a build lot for this camera position");
            Add("build_camera_coordinates", "Camera focus on the construction grid: {0}");
            Add("build_camera_grid", "Grid spacing {0}. Right increases column; Up increases row. These directions stay fixed when the camera turns");
            Add("layout_unavailable", "The lot layout is not available yet");
            Add("layout_updating", "The game is updating the rooms. Request the description again in a moment");
            Add("layout_lot", "Lot outline: {0}. {1}");
            Add("layout_size", "Overall span {0} wide by {1} from front to back");
            Add("layout_region", "{0} {1}");
            Add("layout_centre_outside", "The middle of that overall span lies outside the lot outline");
            Add("layout_shape_square", "square");
            Add("layout_shape_rectangle", "rectangular");
            Add("layout_shape_l", "L-shaped");
            Add("layout_shape_l_recess", "L-shaped, with the {0} corner cut out");
            Add("layout_shape_curved", "outline includes curved edges");
            Add("layout_shape_polygon", "{0}-sided outline");
            Add("layout_shape_indented", "irregular {0}-sided outline, with inward corners at {1}");
            Add("layout_no_rooms", "No enclosed rooms. {0} placed objects");
            Add("layout_room_count", "{0} enclosed rooms");
            Add("layout_floor", "Floor layer {0}");
            Add("layout_room_focus", "Camera focus in {0}, floor layer {1}");
            Add("room_label_name", "Name room");
            Add("room_label_rename", "Rename room");
            Add("room_label_clear", "Clear room name");
            Add("room_label_reassign", "Assign a retained room name");
            Add("room_label_retained", "Names from changed rooms. Choose a name to assign to this room");
            Add("room_label_duplicate", "{0}, room {1}, floor layer {2}");
            Add("room_label_hint", "Type a room name. Enter accepts; Escape keeps the current name. Names are stored when you save the game");
            Add("room_label_set", "Room named {0}");
            Add("room_label_cleared", "Room name cleared");
            Add("room_label_cancelled", "Room name unchanged");
            Add("room_label_invalid", "Enter a name of 1 to 64 characters. The previous name is unchanged");
            Add("room_label_unavailable", "Room naming is unavailable for this room right now");
            Add("room_label_storage_error", "Room names could not be read or saved. See the log for details; keep the game open if you have unsaved names");
            Add("layout_room", "{0}, {1} of the lot. {2}. {3}");
            Add("layout_room_unavailable", "{0}: its outline is not available yet");
            Add("layout_shared_wall", "Shares {0} with {1}");
            Add("layout_shared_boundary", "Shares a boundary without a full visible wall with {0}");
            Add("layout_room_detached", "No shared boundaries with other rooms on this floor");
            Add("layout_curved_wall", "curved wall");
            Add("layout_unenclosed_walls", "{0} wall segments outside completed rooms");
            Add("layout_contents", "{0} objects located in this room");
            Add("room_facing_wall", "facing the {0}");
            Add("room_facing_corner", "facing the {0} {1} corner");
            Add("room_facing_unavailable", "facing unavailable");
            Add("layout_contents_partial", "Attachments on curved or unsupported wall faces may be omitted");
            Add("layout_on_wall", "on the {0}");
            Add("layout_against_wall", "against the {0}");
            Add("layout_near_centre", "near the lot centre");
            Add("layout_from_centre", "about {0} {1} of the lot centre");
            Add("layout_near_boundary", "within {0} of the lot boundary");
            Add("layout_right", "right");
            Add("layout_back_right", "up-right");
            Add("layout_back", "up");
            Add("layout_back_left", "up-left");
            Add("layout_left", "left");
            Add("layout_front_left", "down-left");
            Add("layout_front", "down");
            Add("layout_front_right", "down-right");
            Add("build_lot_bounds", "Lot bounds: columns {0} to {1}, rows {2} to {3}. Irregular lots may not fill these bounds");
            Add("build_bounds_unavailable", "Lot bounds are unavailable");
            Add("build_location_unavailable", "position within the lot unavailable");
            Add("build_outside_lot", "outside the build lot");
            Add("build_region_lower_left", "in the lower left part of the lot grid");
            Add("build_region_lower", "in the lower part of the lot grid");
            Add("build_region_lower_right", "in the lower right part of the lot grid");
            Add("build_region_left", "in the left part of the lot grid");
            Add("build_region_centre", "in the central part of the lot");
            Add("build_region_right", "in the right part of the lot grid");
            Add("build_region_upper_left", "in the upper left part of the lot grid");
            Add("build_region_upper", "in the upper part of the lot grid");
            Add("build_region_upper_right", "in the upper right part of the lot grid");
            Add("build_empty_area", "Empty outdoor area. Camera centred here. You are still in the lot explorer; F7 closes it.");
            Add("build_form_context", "{0}. {1}. Current text: {2}. {3}");
            Add("build_moved_right", "Right {0}");
            Add("build_moved_left", "Left {0}");
            Add("build_moved_up", "Up {0}");
            Add("build_moved_down", "Down {0}");
            Add("placement_context_short", "{0}. {1}. {2}. Enter places; Escape cancels. F11 has detailed measurements and more options.");
            Add("placement_room_location", "In {0}, {1}");
            Add("placement_room_location_brief", "{0}, {1}");
            Add("placement_outdoor_location", "Outdoors, {0}");
            Add("placement_details", "Detailed position and measurements");
            Add("wall_idle", "Wall start point. {0} makes a room; Enter starts an individual wall. F11 reads exact position.");
            Add("wall_preview_short", "Wall length {0}, price {1}. Enter buys; Escape cancels");
            Add("wall_item_position_short", "Wall {0}. Centre {1} from the left, {2} above the wall bottom. Price {3}");
            Add("room_size_title", "Room size");
            Add("extension_open", "Add a room on the other side");
            Add("extension_choose_wall", "Choose the shared wall");
            Add("extension_title", "Extend from this wall");
            Add("extension_wall_direction", "Wall {0}, {1}");
            Add("extension_side", "{0}, {1} wide. {2}");
            Add("extension_outside", "Outdoors on the other side");
            Add("extension_other_room", "{0} on the other side; extension unavailable");
            Add("extension_depth_hint", "{0}. The new room shares this full width and extends away from the existing room. Enter its depth in {1}");
            Add("extension_depth_instructions", "Enter depth only, or width; depth using the selected wall's width. Enter reviews; Escape cancels");
            Add("extension_width_fixed", "Wall width is fixed at {0}. Enter depth only. Nothing built; choose the wall again in F7 to retry");
            Add("extension_depth_invalid", "Enter a positive decimal depth. Nothing built. Choose the wall again in F7 to retry");
            Add("extension_review", "Extend {0} through {1}: {2} wide, {3} deep, away from the existing room. The shared wall stays. Enter begins three new walls, each bought separately; Escape cancels");
            Add("extension_wall_edge", "New wall {0} of 3. Enter confirms this wall only");
            Add("extension_wall_preparing", "Preparing new wall {0} of 3. Wait for its price");
            Add("extension_no_sides", "No supported straight room walls available for an extension");
            Add("extension_changed", "The selected wall, room or lot changed, or its geometry is unavailable. Choose the wall again in F7");
            Add("extension_room_blocked", "Another room occupies that space. Choose a different wall or a smaller depth");
            Add("extension_lot_blocked", "That extension crosses the lot boundary. Choose a smaller depth or a different wall");
            Add("extension_wall_blocked", "An existing wall crosses that space. Choose a smaller depth or a different wall");
            Add("extension_item_blocked", "A new wall would cross the bounding area of {0}. Move that object or choose a different depth");
            Add("extension_shared_unsupported", "This extension needs one complete, visible, full-height shared wall. Partial or additional shared sides are not supported yet");
            Add("extension_grid_required", "This wall does not line up with the current construction grid. An extension cannot be planned here yet");
            Add("extension_tool_unavailable", "The native straight-wall tool could not match this wall. No extension started");
            Add("extension_updating", "Waiting for the game to update the rooms. No wall confirmed");
            Add("room_size_hint", "Width; depth in {0}, using decimal numbers. The room will be centred near the camera focus, or the lot centre");
            Add("room_size_instructions", "Ctrl+A selects the current text. Type width; depth, then Enter to review the walls. Sizes fit the game's wall grid; the actual size will be announced. Escape cancels");
            Add("room_size_result", "Room {0} by {1}");
            Add("room_size_invalid", "Width and depth must both be greater than zero. Press R to try again");
            Add("room_size_outside", "That room extends beyond this lot. Press R to try a smaller size, or focus a different area first");
            Add("room_size_unavailable", "The game's measurement unit is unavailable. No room planned");
            Add("coordinate_grid_hint", "Enter column then row in the lot's accessibility grid, separated by a semicolon. Grid spacing {0}; fractional and negative coordinates are allowed");
            Add("coordinate_wall_hint", "Enter along-distance from the announced starting endpoint, then centre height above the selected face or whole wall's bottom, in metres, separated by a semicolon");
            Add("coordinate_hint", "Ctrl+A selects the current text. Type two numbers separated by a semicolon. Enter reviews; Escape cancels. F10 reopens this form");
            Add("coordinate_review", "Coordinates submitted. F3 reviews the preview; Enter places object");
            Add("coordinate_cancelled", "Coordinate entry cancelled; no coordinate request applied");
            Add("coordinate_invalid", "Enter two decimal numbers separated by a semicolon, for example 5; 5. Nothing changed. Reopen the form to try again");
            Add("coordinate_changed", "The preview, coordinate frame, floor or input form changed. No coordinate request applied");
            Add("coordinate_mismatch", "The actual position or coordinate frame does not match the reviewed coordinates. No confirmation sent. F10 revises the request; an arrow returns to grid stepping");
            Add("coordinate_choose_face", "Choose a wall face first, then press F10 to enter distance and height");
            Add("coordinate_leave_slots", "A native slot sets its own coordinates. Return to the floor grid with semicolon before entering coordinates");
            Add("coordinate_unavailable", "Coordinate entry is unavailable. Details are in the mod log");
            Add("coordinate_busy", "Close the current text form before opening coordinate entry");
            Add("footprint_command", "Read object footprint on the lot grid");
            Add("footprint_report", "Bounding footprint spans {0} by {1} grid cells. Column bounds {2} to {3}; row bounds {4} to {5}; cell spacing {6}. This enclosing rectangle includes rotation; it does not measure walking clearance or exact mesh occupancy");
            Add("room_choose_front", "Choose this room's front reference wall");
            Add("room_describe", "Describe room layout using the chosen front wall");
            Add("room_reference_face", "Wall section {0}, width {1}; endpoints {2}. Enter makes this the front reference wall");
            Add("room_reference_set", "Front reference wall selected. Back points into the room away from this wall. Left and right are as you stand at this wall looking into the room. Choose Describe room layout to hear the contents");
            Add("room_reference_required", "Choose this room's front reference wall before requesting a layout description");
            Add("room_reference_changed", "The room or reference wall changed. Choose a front reference wall again");
            Add("room_no_faces", "No supported straight rectangular wall faces are available as a reference in this room");
            Add("room_front_wall", "front wall");
            Add("room_back_wall", "back wall");
            Add("room_left_wall", "left wall");
            Add("room_right_wall", "right wall");
            Add("room_angled_wall", "angled wall");
            Add("room_front_left_wall", "angled front-left wall");
            Add("room_front_right_wall", "angled front-right wall");
            Add("room_back_left_wall", "angled back-left wall");
            Add("room_back_right_wall", "angled back-right wall");
            Add("room_wall_direction_unavailable", "direction unavailable");
            Add("room_front", "front");
            Add("room_middle", "middle");
            Add("room_back", "back");
            Add("room_left", "left");
            Add("room_centre", "centre");
            Add("room_right", "right");
            Add("room_centred", "centred");
            Add("room_wall_distance", "centre {0} from its starting endpoint, wall width {1}");
            Add("room_on_wall", "{0} on {1}, section {2}");
            Add("room_region", "{0}, {1} portion of the room's enclosing rectangle");
            Add("room_near_wall", "near {0}, section {1}, bounding gap {2}");
            Add("room_on_item", "on {0}");
            Add("room_attached_item", "attached to {0}");
            Add("room_item", "{0}: {1}");
            Add("room_wall_size", "{0}, section {1}, width {2}");
            Add("room_summary", "{0} objects. Overall width {1}, depth {2}. Directions follow your chosen front wall");
            Add("placement_dimensions", "Dimensions: width {0}, depth {1}, height {2}");
            Add("placement_geometry_unavailable", "Item dimensions unavailable");
            Add("placement_grid_unavailable", "The placement grid is unavailable. Keyboard placement cannot continue. Press Escape to cancel.");
            Add("placement_grid_changed", "The lot grid changed. Coordinates now use the updated grid.");
            Add("placement_target_out_of_view", "Target outside the view. Zoom out, then use an arrow to adjust it.");
            Add("placement_target_difference", "The actual horizontal position is {0} from the requested point");
            Add("placement_snapped", "Snapped by the game");
            Add("placement_rotation", "World rotation {0} degrees");
            Add("placement_moved", "Moved {0}, {1}.");
            Add("placement_rotated", "Rotated {0}, {1}.");
            Add("placement_unsupported_wall", "A keyboard wall target could not be established. F11 offers a reviewed native placement attempt at the actual preview position. Escape cancels.");
            Add("placement_unsupported_ceiling", "Keyboard movement of ceiling items is unavailable. F11 offers a reviewed native placement attempt at the actual preview position. Escape cancels.");
            Add("placement_lot_required", "Escape cancels. Move the camera to an owned lot, then select the item again.");
            Add("placement_framing", "Framing the owned lot for placement.");
            Add("placement_anchor_failed", "Cannot find a floor point here. Home retries; Escape cancels.");
            Add("placement_mouse_missing", "Game pointer unavailable. Escape cancels.");
            Add("placement_wait", "Position not ready. Wait, then press Enter again.");
            Add("placement_pointer_over_ui", "Interface blocks this point. Enter reframes; confirm placement after reviewing it.");
            Add("placement_valid", "Can place here");
            Add("placement_invalid", "Cannot place: {0}");
            Add("placement_occupied", "occupied by {0}");
            Add("placement_occupied_move", "Blocked by {0}. Move the preview.");
            Add("placement_placed", "{0} purchased and placed, {1}. Spent {2}.");
            Add("placement_cancelled", "{0} preview cancelled. No purchase was made.");
            Add("placement_repeat", "{0} purchased and placed, {1}. Spent {2}. Another copy is ready; press Escape to stop.");
            Add("placement_status_changed", "{0}, {1}.");
            Add("placement_handler_failed", "Keyboard placement was disabled after an internal error.");

            // ===== Newspaper and tracked goals =====
            Add("newspaper_article_action", "{0}. Action: {1}");
            Add("newspaper_article_status", "{0}. {1}");
            Add("newspaper", "Newspaper");
            Add("newspaper_unread_one", "Newspaper, 1 unread issue");
            Add("newspaper_unread_many", "Newspaper, {0} unread issues");
            Add("goal_summary", "{0}. Objectives: {1}");
            Add("want_empty_slot", "Empty want slot {0} of {1}");
            Add("emotion_no_reaction", "No reaction is available for this emotion.");
            Add("goal_summary_read_only", "This goal summary is for reading.");
            Add("goal_objective_read_only", "This objective is for reading.");

            // ===== Semantic HUD, thoughts and objectives =====
            Add("list_separator", "; ");
            Add("list_comma", ", ");
            Add("list_pair", "{0} and {1}");
            Add("list_final_pair", "{0}, and {1}");
            Add("label_with_detail", "{0}, {1}");
            Add("panel_summary", "{0}. {1}");

            Add("panel_thoughts", "Thoughts");
            Add("panel_progress_events", "Progress and upcoming events");
            Add("panel_active_wants", "Active wants");
            Add("panel_needs", "Needs");
            Add("panel_emotions", "Emotions");
            Add("panel_thought_bubbles", "Thought bubbles");
            Add("panel_character_tabs", "Character information tabs");
            Add("panel_time_controls", "Time controls");
            Add("roof_dimensions", "Roof dimensions");
            Add("roof_dimensions_hint", "Width; depth; height, in metres");
            Add("roof_size_instructions", "Enter width; depth; height in metres. Enter updates this preview without buying it. Escape keeps the previous size.");
            Add("roof_resize_instructions", "Enter width; depth; height in metres. Enter resizes this roof with native undo. Escape keeps the previous size.");
            Add("roof_size_report", "Roof width {0}, depth {1}, height {2}, floor layer {3}");
            Add("roof_dimensions_cancelled", "Roof dimensions cancelled; the previous size is unchanged");
            Add("roof_preview_sized", "{0}. Preview updated; nothing purchased");
            Add("roof_resized", "{0}. Roof resized; Undo restores its previous size");
            Add("roof_invalid_dimensions", "Enter three positive dimensions separated by semicolons, in metres, within this roof's supported size limits. No size changed");
            Add("roof_unavailable", "Roof sizing is unavailable in the current state; no resize confirmed");
            Add("roof_resize_outside", "That size extends outside the owned lot. The previous roof size is restored");
            Add("roof_fit_room", "Fit roof over room");
            Add("roof_room_choice", "{0}, floor layer {1}");
            Add("roof_fitting", "Fitting the roof preview over the room ceiling; please wait");
            Add("roof_fitted", "Roof preview over {0}'s ceiling, covering its bounding rectangle with a quarter metre overhang on each side. {1}. Nothing purchased. Enter places; Escape cancels");
            Add("roof_fit_unavailable", "Cannot fit this roof here. Check the room ceiling or choose another room");
            Add("roof_review", "Read roof dimensions");
            Add("time_date_report", "{0}. {1}.");
            Add("time_date_unavailable", "The in-game time and date are unavailable until a game is loaded.");
            Add("panel_game_controls", "Game controls");
            Add("panel_household", "Household");
            Add("panel_interactions", "Interactions");
            Add("panel_offered_wants", "Offered wants");
            Add("panel_offered_requests", "Townie requests");
            Add("panel_story_cards", "Story cards");

            // ===== Contextual actions =====
            Add("context_action_none", "No contextual action is available.");
            Add("context_action_opening", "Conversation choices are opening.");
            Add("context_action_closed", "Contextual actions closed.");
            Add("context_action_busy", "Close the current accessibility panel before responding.");
            Add("context_action_gone", "That contextual action is no longer available.");
            Add("context_action_choices_missing",
                "The game opened the conversation view, but no choices became available.");
            Add("context_action_failed", "Contextual actions stopped after an internal error.");
            Add("context_conversations_panel", "Available conversations");
            Add("context_choices_panel", "Conversation choices");
            Add("context_replies_panel", "Conversation replies: {0}");
            Add("context_targets_panel", "Choose a conversation target");
            Add("context_targets_for_card", "Choose a target for {0}");
            Add("context_panel_with_participants", "{0}. Participants: {1}");
            Add("context_high_stakes_check", "High-stakes success check.");
            Add("context_change_card_variant", "Change variant for {0}, {1} available");
            Add("context_card_variant_changed", "Variant changed to {0}");
            Add("context_choice_controls", "Use the arrows to move and Enter to choose");
            Add("context_open_conversation", "Open conversation choices");
            Add("context_open_conversation_with", "Open conversation choices with {0}");

            // Interaction queue rows activate cancellation rather than opening details.
            Add("queue_cancel_running", "Cancel {0}, currently running");
            Add("queue_cancel_queued", "Cancel {0}, queued");
            Add("queue_cancelling", "{0}, cancellation in progress");
            Add("queue_tutorial_locked", "{0}, cannot be canceled during the tutorial");

            Add("objective_guidance", "Guidance: {0}");
            Add("objective_completed", "{0}, completed");
            Add("objective_time_remaining", "{0} remaining");
            Add("objectives_updated", "Objectives updated. {0}");
            Add("objectives_none", "No active objective or want.");
            Add("active_wants_summary", "Active wants: {0}");
            Add("upcoming_events_summary", "Upcoming: {0}");
            Add("daily_story_cards_ready", "Daily story cards are ready");
            Add("household_members", "Household members: {0}");
            Add("offered_wants_summary", "Choices: {0}");
            Add("offered_requests_one", "1 request: {0}");
            Add("offered_requests_many", "{0} requests: {1}");

            // ===== Cross-game state reporting =====
            // These follow only actions the player explicitly requested. Autonomous simulation is
            // intentionally silent so ordinary play does not become a running commentary.
            Add("state_selected_para", "Selected Para");
            Add("state_paras", "{0} Paras");
            Add("state_action_queued", "{0}: {1} queued.");
            Add("state_action_queued_behind", "{0}: {1} queued while {2} is running.");
            Add("state_action_started", "{0} started {1}.");
            Add("state_action_started_following",
                "{0} started {1}. Camera now following {0}.");
            Add("state_action_finished", "{0} finished {1}.");
            Add("state_action_ended", "{0}: {1} ended.");
            Add("state_action_stopped", "{0}: {1} stopped.");
            Add("state_action_could_not_start", "{0} could not start {1}.");
            Add("state_notification", "Notification: {0}");
            Add("state_notification_detail", "{0}. {1}");
            Add("state_shop_item_purchased", "Purchased {0}");
            // Invitation responses identify their Para visually through a portrait rather than in
            // the localized sentence. {0} is that Para; {1} is the game's response text.
            Add("state_invitation_response", "{0}: {1}");
            Add("state_invited_visitor_arrived", "{0} has arrived for the visit.");
            Add("state_invited_visitor_not_arriving", "{0} is no longer coming to visit.");
            Add("state_invited_visitor_left", "{0} has left.");
            Add("state_invited_visit_ended", "{0}'s visit has ended.");
            Add("state_request_available", "{0}: request available.");
            Add("state_request_completed", "{0}: request ready to turn in.");
            Add("state_request_available_named", "{0}: request available. {1}");
            Add("state_requests_available_named", "{0}: {1} requests available. {2}");
            Add("state_request_offer", "{0}: {1}");
            Add("state_request_completed_named", "{0}: request ready to turn in. {1}");
            Add("state_requests_completed_named", "{0}: {1} requests ready to turn in. {2}");
            Add("state_request_turn_in_entry", "{0}: {1}");
            Add("state_request_unknown", "Unknown request");
            Add("state_unknown_para", "Unknown Para");
            Add("important_event_none", "No recent important gameplay event.");
            Add("activity_summary", "{0}: {1}.");
            Add("activity_summary_running", "running {0}");
            Add("activity_summary_queued", "queued {0}");
            Add("activity_summary_ending", "ending {0}");
            Add("activity_summary_idle", "{0}: no running or queued activities.");
            Add("activity_summary_no_selection", "No Para selected for the activity report.");
            Add("activity_summary_unavailable",
                "Selected Para activities are unavailable while the game is updating.");
            Add("activity_summary_names_unavailable",
                "{0} has activities, but their names are unavailable.");
            Add("activity_summary_some_names_unavailable", "some activity names unavailable");
            Add("state_reporting_failed", "Action status reporting stopped after an internal error.");

            // ===== Followed-Para scene narration =====
            // These are queued, low-frequency descriptions of authoritative visual state changes.
            Add("scene_together_bar_ready",
                "Together Bar full. Press {0} for conversation topics.");
            Add("scene_story_topic", "Story conversation topic: {0}.");
            Add("scene_conversation_topic", "{0} chose the conversation topic: {1}.");
            Add("scene_conversation_topic_target",
                "{0} chose the conversation topic: {1}, with {2}.");
            Add("scene_conversation_reply", "{0} replied: {1}.");
            Add("scene_conversation_reply_chosen", "Reply chosen: {0}.");
            Add("scene_conversation_result", "Conversation result");
            Add("scene_conversation_with_results", "{0}. Results: {1}.");
            Add("scene_conversation_results", "Conversation results: {0}.");
            Add("scene_conversation_started",
                "{0} is talking with {1}. {2} is speaking.");
            Add("scene_conversation_speaker", "{0} takes a turn speaking.");

            Add("scene_emotion_increased",
                "For {0}, {1} increased from {2} to {3}.");
            Add("scene_emotion_increased_reason",
                "For {0}, {1} increased from {2} to {3} because of {4}.");
            Add("scene_emotion_increased_ended_reason",
                "For {0}, {1} increased from {2} to {3} because the effect from {4} ended.");
            Add("scene_emotion_decreased",
                "For {0}, {1} decreased from {2} to {3}.");
            Add("scene_emotion_decreased_reason",
                "For {0}, {1} decreased from {2} to {3} because the effect from {4} ended.");
            Add("scene_emotion_decreased_cause",
                "For {0}, {1} decreased from {2} to {3} because of {4}.");
            Add("scene_emotion_ended", "{0} is no longer {1}.");
            Add("scene_emotion_ended_reason",
                "{0} is no longer {1}; the effect from {2} ended.");
            Add("scene_emotion_ended_cause", "{0} is no longer {1} because of {2}.");
            Add("scene_reason_with_para", "{0}, involving {1}");

            Add("scene_goes_upstairs", "{0} goes upstairs.");
            Add("scene_goes_downstairs", "{0} goes downstairs.");
            Add("scene_ascends_stairs", "{0} ascends stairs.");
            Add("scene_descends_stairs", "{0} descends stairs.");
            Add("scene_climbs_up", "{0} climbs up a ladder.");
            Add("scene_climbs_down", "{0} climbs down a ladder.");
            Add("scene_steps_outdoors", "{0} steps outdoors.");
            Add("scene_room_on_floor", "a room on floor {0}");
            Add("scene_enters_area", "{0} enters {1}.");
            Add("scene_enters_area_contents", "{0} enters {1}, containing {2}.");

            Add("scene_takes_out_phone", "{0} takes out a phone.");
            Add("scene_plays_phone_game", "{0} plays a game on the phone.");
            Add("scene_types_on_phone", "{0} types on the phone.");
            Add("scene_puts_away_phone", "{0} puts the phone away.");

            Add("scene_tag_joke_success",
                "{0}'s joke lands, and {1} reacts positively.");
            Add("scene_tag_joke_failed", "{0}'s joke falls flat with {1}.");
            Add("scene_tag_flirt_success", "{1} responds positively to {0}'s flirting.");
            Add("scene_tag_flirt_failed", "{1} responds poorly to {0}'s flirting.");
            Add("scene_tag_question", "{0} asks {1} a question.");
            Add("scene_tag_awkward", "The conversation turns awkward.");
            Add("scene_tag_angry", "The conversation turns angry.");
            Add("scene_tag_mean", "The conversation turns mean.");
            Add("scene_tag_neutral", "The conversation turns neutral.");
            Add("scene_tag_yelling", "{0} starts yelling.");
            Add("scene_tag_introduction", "{0} introduces themself to {1}.");
            Add("scene_tag_complaining", "{0} starts complaining.");
            Add("scene_tag_mischievous", "The conversation turns mischievous.");
            Add("scene_tag_motivational", "The conversation turns motivational.");
            Add("scene_tag_praise", "{0} praises {1}.");
            Add("scene_tag_funny", "The conversation becomes playful and funny.");
            Add("scene_tag_tips", "{0} shares tips with {1}.");
            Add("scene_tag_startled", "{0} reacts with surprise.");
            Add("scene_tag_bragging", "{0} starts bragging.");
            Add("scene_tag_sad", "The conversation turns sad.");
            Add("scene_tag_meaningful", "The conversation becomes more meaningful.");
            Add("scene_tag_flirty", "The conversation turns flirty.");

            Add("scene_result_emotion", "{0}: {1} {2}.");
            Add("scene_result_learned", "Learned about {0}: {1}.");
            Add("scene_result_relationship", "{0}: relationship {1} {2}.");
            Add("scene_result_skill", "{0}: {1} skill experience {2}.");
            Add("scene_result_status", "{0} gained {1}.");
            Add("scene_result_success", "Conversation action succeeded.");
            Add("scene_result_failure", "Conversation action failed.");
            Add("scene_result_need", "{0}: {1} relieved.");
            Add("scene_result_want", "{0} gained the want: {1}.");
            Add("scene_result_money", "{0}: money changed by {1}.");
            Add("scene_result_item_added", "{0} received {1}.");
            Add("scene_result_item_removed", "{0} lost {1}.");
            Add("scene_result_group", "{0}: {1}");
            Add("scene_result_emotion_detail", "{0} {1}");
            Add("scene_result_learned_detail", "learned {0}");
            Add("scene_result_relationship_detail", "relationship {0} {1}");
            Add("scene_result_skill_detail", "{0} skill experience {1}");
            Add("scene_result_status_detail", "gained {0}");
            Add("scene_result_success_detail", "action succeeded");
            Add("scene_result_failure_detail", "action failed");
            Add("scene_result_need_detail", "{0} relieved");
            Add("scene_result_want_detail", "gained the want {0}");
            Add("scene_result_money_detail", "money changed by {0}");
            Add("scene_result_item_added_detail", "received {0}");
            Add("scene_result_item_removed_detail", "lost {0}");
            Add("scene_narration_failed",
                "Followed Para scene narration stopped after an internal error.");

            Add("interaction_success", "success chance {0}");
            Add("interaction_cost", "cost {0}");
            Add("interaction_cooldown", "available in {0}");
            Add("interaction_unavailable", "unavailable: {0}");
            Add("interaction_character_cannot_perform", "cannot perform this interaction");
            Add("interaction_with_details", "{0}, {1}");
            Add("interaction_list_one", "1 action");
            Add("interaction_list_many", "{0} actions");
            Add("interaction_submenu_label", "{0}, submenu");
            Add("interaction_submenu_opened_one", "{0} opened, 1 choice. {1}");
            Add("interaction_submenu_opened_many", "{0} opened, {1} choices. {2}");
            Add("interaction_submenu_empty", "{0} opened, but no choices are currently available.");
            Add("interaction_search", "Search interactions");

            Add("need_offers", "View offered wants");
            Add("need_offers_for", "View wants for {0}");
            Add("need_ignored", "ignored");
            Add("need_critical", "critical");
            Add("need_relieving", "being relieved");
            Add("need_value", "{0}, {1} of {2}");
            Add("need_value_state", "{0}, {1} of {2}, {3}");
            Add("emotion_value", "{0}, value {1}");
            Add("emotion_with_breakdown", "{0}, breakdown: {1}");
            Add("emotion_breakdown_entry", "{0} from {1}");
            Add("emotion_reaction_available", "{0}, reaction available");
            Add("thought_bubble", "Thought bubble");
            Add("thought_request_available", "Request available thought bubble");
            Add("thought_request_completed", "Request completed thought bubble");

            Add("tab_thoughts", "Thoughts");
            Add("tab_profile", "Profile");
            Add("tab_skills", "Skills");
            Add("tab_relationships", "Relationships");
            Add("tab_occupations", "Occupations");
            Add("tab_inventory", "Inventory");
            Add("tab_memories", "Memories");
            Add("tab_goals", "Goals");
            Add("notification_one", "{0}, 1 notification");
            Add("notification_many", "{0}, {1} notifications");
            Add("notification_present", "{0}, notification");

            Add("money", "Money");
            Add("money_balance", "Money, {0}");
            Add("mailbox", "Mailbox");
            Add("mailbox_unread_one", "Mailbox, 1 unread letter");
            Add("mailbox_unread_many", "Mailbox, {0} unread letters");
            Add("offered_want_with_reason", "{0}. {1}");
            Add("action_accept_request", "Accept request");
            Add("profile_level_up", "Level up personality");
            Add("profile_level_number", "Personality level {0}");
            Add("profile_level", "Personality level {0}. {1}");

            // ===== Placed objects =====
            Add("objects_panel", "Objects in the house");
            Add("objects_closed", "Object browser closed.");
            Add("objects_live_required", "Return to live mode before browsing object actions.");
            Add("objects_character_required", "Select a character before browsing object actions.");
            Add("objects_lot_required", "No currently loaded owned lot could be resolved.");
            Add("objects_ui_blocked", "Close the current game window before browsing house objects.");
            Add("objects_town_map", "Move the camera back inside the lot before browsing house objects.");
            Add("objects_special_mode", "House-object actions are not available during the current guided selection.");
            Add("objects_none", "No objects with action-menu content were found in this house.");
            Add("objects_copy", "{0}, copy {1} of {2}");
            Add("objects_description", "{0}, {1}");
            Add("objects_action_unavailable", "That object no longer has action-menu content.");
            Add("objects_open_failed", "The interaction menu could not be opened.");
            Add("objects_handler_failed", "The house-object browser was disabled after an internal error.");
            Add("item_unknown", "unknown object");
            Add("item_floor_layer", "floor layer {0}");
            Add("item_location_distance", "{0}, {1} away");
            Add("item_location_relation", "{0}, {1}");
            Add("item_location_relation_distance", "{0}, {1}, {2} away");
            Add("item_on_parent", "on {0}");
            Add("item_attached_parent", "attached to {0}");
            Add("item_on_floor", "on a floor surface");
            Add("item_on_terrain", "on terrain");

            // ===== Camera-first current-lot explorer =====
            Add("explorer_panel", "Current lot explorer");
            Add("explorer_controls", "Arrows browse; Enter opens; Backspace goes back; F7 closes");
            Add("explorer_para_selection_hint", "Shift+Enter toggles selection");
            Add("explorer_closed", "Current lot explorer closed.");
            Add("explorer_live_required", "Return to live or build mode before exploring the current lot.");
            Add("explorer_lot_required",
                "No loaded lot could be resolved from the camera, a Para, or the sole owned lot.");
            Add("explorer_ui_blocked", "Close the current game window before exploring the lot.");
            Add("explorer_town_map", "Leave the town map before exploring a loaded lot.");
            Add("explorer_handler_failed",
                "The current lot explorer was disabled after an internal error.");
            Add("explorer_floor", "Floor layer {0}");
            Add("explorer_floor_entry", "{0}, {1}, {2}, {3}");
            Add("explorer_unnamed_room", "Unnamed room {0} of {1}");
            Add("explorer_outdoors", "Outdoor area");
            Add("explorer_area_entry", "{0}, {1}, {2}");
            Add("explorer_area_count_one", "1 area");
            Add("explorer_area_count_many", "{0} areas");
            Add("explorer_object_count_one", "1 object");
            Add("explorer_object_count_many", "{0} objects");
            Add("explorer_para_count_one", "1 Para");
            Add("explorer_para_count_many", "{0} Paras");
            Add("explorer_area_surface", "area {0}");
            Add("explorer_area_contents", "contains {0}");
            Add("explorer_object_category", "category: {0}");
            Add("explorer_find_all", "Find all objects by name, {0}");
            Add("explorer_find_actions", "Find objects with current actions by name, {0}");
            Add("explorer_find_actions_tutorial",
                "Find objects with current actions and the active tutorial target by name, {0}");
            Add("explorer_find_facilities", "Find useful facilities, {0}");
            Add("explorer_find_paras", "Paras on this lot, {0}");
            Add("explorer_view_actionable",
                "Showing only objects and Paras with action menus. Show all objects and Paras");
            Add("explorer_view_all",
                "Showing all objects and Paras. Show only those with action menus");
            Add("explorer_tutorial_conductors", "Find tutorial conductor, {0}");
            Add("explorer_tutorial_conductor_loading",
                "The tutorial conductor's character visual is still loading.");
            Add("explorer_tutorial_conductor_unavailable",
                "No selectable tutorial conductor is available among the loaded character visuals.");
            Add("explorer_tutorial_conductor_target", "required tutorial conductor");
            Add("explorer_selected", "selected");
            Add("explorer_not_selected", "not selected");
            Add("explorer_para_selection_state", "selection state: {0}");
            Add("explorer_select_only_para", "Select only this Para");
            Add("explorer_add_to_selection", "Add this Para to the selected group");
            Add("explorer_remove_from_selection", "Remove this Para from the selected group");
            Add("explorer_clear_selection", "Clear selected group; keep {0} selected");
            Add("explorer_selection_cleared", "Selected group cleared. Only {0} remains selected.");
            Add("creator_delete_para", "Delete {0}");
            Add("creator_household_controls", "Left and Right choose a Para or Add Para. Down reaches that Para's Delete button; Up returns to the portrait. Enter on Delete opens a confirmation.");
            Add("explorer_para_selected_only", "Only {0} is now selected.");
            Add("explorer_para_selected",
                "{0} added to the selected group. {1} Paras now selected.");
            Add("explorer_para_unselected",
                "{0} removed from the selected group. {1} Paras now selected.");
            Add("explorer_name_group", "Names beginning with {0}, {1} objects");
            Add("explorer_facility_name_group",
                "Names beginning with {0}, {1} products or facilities");
            Add("explorer_name_other", "numbers or symbols");
            Add("explorer_no_objects", "No matching loaded objects remain in this lot.");
            Add("explorer_no_paras", "No matching loaded Paras remain in this lot.");
            Add("explorer_path", "{0}, {1}");
            Add("explorer_object_function", "functions: {0}");
            Add("explorer_actions_available", "action menu available");
            Add("explorer_actions_unavailable", "no current action menu");
            Add("explorer_object_commands", "Object commands for {0}");
            Add("explorer_para_commands", "Para commands for {0}");
            Add("explorer_household_commands", "Household commands for {0}");
            Add("explorer_frame_object", "Frame this object with the camera");
            Add("explorer_frame_para", "Frame this Para with the camera");
            Add("explorer_frame_household", "Frame this household's world representation");
            Add("explorer_open_actions", "Open the native action menu");
            Add("explorer_follow_para", "Follow this Para with the camera");
            Add("explorer_framed", "Camera focused on {0}.");
            Add("explorer_framed_stopped_following",
                "Camera focused on {0}. Stopped following {1}.");
            Add("explorer_frame_failed", "The camera could not frame that target.");
            Add("explorer_following", "Camera now following {0}.");
            Add("explorer_locate_selected_para", "Locate and follow selected Para, {0}");
            Add("explorer_selected_para_already_followed", "Reframe selected Para, {0}, currently followed");
            Add("explorer_locate_selected_para_unavailable", "No selected Para is available to locate.");
            Add("explorer_follow_started_frame_pending", "Camera is following {0}; framing is still settling.");
            Add("explorer_tutorial_target", "Tutorial target: {0}");
            Add("explorer_tutorial_households", "Find tutorial households, {0}");
            Add("explorer_tutorial_storytellers", "Find tutorial Storytellers, {0}");
            Add("explorer_household_representation",
                "represented in the world by {0}, {1}");
            Add("explorer_household_representation_name",
                "represented in the world by {0}");
            Add("explorer_storyteller_object_representation",
                "represented by object {0}, {1}, {2}");
            Add("explorer_storyteller_para_representation",
                "represented by Para {0}, {1}, {2}");
            Add("explorer_loaded_world_representation", "loaded in the tutorial scene");
            Add("explorer_create_household_target",
                "Create a new household at {0}, {1}, {2}");
            Add("explorer_review_household_command", "Review household {0}");
            Add("explorer_create_household_command", "Create New Household");
            Add("explorer_select_storyteller_command", "Review {0}");
            Add("explorer_household_choice_available", "household review available");
            Add("explorer_create_household_available", "household creation available");
            Add("explorer_storyteller_choice_available", "Storyteller review available");
            Add("explorer_selected_para_required",
                "Select a Para before opening this target's action menu.");
            Add("explorer_action_unavailable",
                "The game's action menu is not currently available for that target.");
            Add("explorer_target_gone", "That exact target is no longer loaded in this lot.");
            Add("explorer_target_gone_returned",
                "That exact target is no longer loaded. Returned to the previous list.");
            Add("explorer_at_camera_focus", "at the camera focus");
            Add("explorer_camera_relation", "{0}, {1} from the camera focus");
            Add("direction_ahead", "ahead");
            Add("direction_ahead_right", "ahead and right");
            Add("direction_right", "right");
            Add("direction_behind_right", "behind and right");
            Add("direction_behind", "behind");
            Add("direction_behind_left", "behind and left");
            Add("direction_left", "left");
            Add("direction_ahead_left", "ahead and left");

            // ===== Train-intro tutorial =====
            // Game-authored tutorial copy is passed into these wrappers after TranslationManager
            // localizes it; only the accessibility directions are owned by this string table.
            Add("tutorial_announcement", "Tutorial. {0}");
            Add("tutorial_camera_step", "{0}. {1}");
            Add("tutorial_camera_move_controls",
                "Use W A S D until the meter fills");
            Add("tutorial_camera_rotate_controls",
                "Use Q or E until the meter fills");
            Add("tutorial_camera_zoom_controls",
                "Use minus or equals until the meter fills");
            Add("tutorial_world_target",
                "{0}. Press {1} to open Current Lot, choose Find objects with current actions, then choose the tutorial target and its native action");
            Add("tutorial_select_household",
                "{0}. Press {1} to open Current Lot, choose Find tutorial households, then review a household or choose Create New Household");
            Add("tutorial_household_loading",
                "{0}. The tutorial character visuals are still loading");
            Add("tutorial_household_unavailable",
                "{0}. No selectable tutorial household is available among the loaded character visuals");
            Add("tutorial_household_targets_loading",
                "Tutorial character visuals are still loading.");
            Add("tutorial_household_targets_unavailable",
                "No selectable tutorial household is available among the loaded character visuals.");
            Add("tutorial_household_review", "{0}. {1}");
            Add("tutorial_household_choice", "{0}. {1}");
            Add("tutorial_select_storyteller",
                "{0}. Press {1} to open Current Lot, choose Find tutorial Storytellers, then choose a Storyteller to review");
            Add("tutorial_storyteller_loading",
                "{0}. The Storyteller world representations are still loading");
            Add("tutorial_storyteller_targets_loading",
                "Tutorial Storyteller targets are still loading.");
            Add("tutorial_storyteller_review", "{0}. {1}");
            Add("tutorial_storyteller_choice", "{0}. {1}");
            Add("tutorial_ui_step", "{0}. {1}");
            Add("tutorial_panel_controls",
                "Use semicolon or apostrophe to choose a panel, the arrows to move, and Enter to activate");
            Add("tutorial_ok_continue",
                "{0}. The tutorial's OK button is focused; press Enter to continue");
            Add("tutorial_control_context", "{0}. Tutorial: {1}");
            Add("tutorial_multiselect",
                "{0}. Press Control U to select every Para in your household. Then press {1}, choose Find tutorial conductor, choose the conductor, and add that Para to the selected group");
            Add("tutorial_household_selected",
                "All {0} household Paras selected.");
            Add("tutorial_household_selection_failed",
                "Select all was not accepted. {0} of {1} household Paras are selected.");
            Add("tutorial_together_filling", "{0}. The Together Bar is still filling");
            Add("tutorial_together_ready",
                "{0}. Together Bar full; press {1} for conversation topics");
            Add("tutorial_topics_open", "Choose a conversation topic. {0}");
            Add("tutorial_topics_choices", "Choose a conversation topic. Choices: {0}. {1}");
            Add("tutorial_npc_prompt_named", "{0} says: {1}");
            Add("tutorial_npc_prompt", "An NPC says: {0}");
            Add("tutorial_replies_open", "{0}. Choose a reply. {1}");
            Add("tutorial_replies_choices", "{0}. Replies: {1}. {2}");
            Add("tutorial_topic_target", "Choose the target for this conversation topic. {0}");
            Add("tutorial_topics_results", "Conversation topic chosen. Wait for the results");
            Add("tutorial_wait_eating", "Your Para is eating. Wait for the interaction to finish");
            Add("tutorial_transition", "The next tutorial scene is starting");
            Add("tutorial_finished", "Tutorial finished");
            Add("tutorial_marker", "Tutorial marker {0}. {1}");
            Add("tutorial_food_target", "Food. {0}");
            Add("tutorial_handler_failed",
                "Tutorial accessibility encountered an internal error; press F3 for current context");

            Add("together_bar_filling", "Together Bar, {0} percent");
            Add("together_bar_ready", "Together Bar full, choose a conversation topic");
            Add("together_card_success", "{0}, success chance {1}");
            Add("together_card_chance", "success chance {0}");
            Add("together_card_trait_requirement", "requires {0}");
            Add("together_card_inventory_requirement", "requires {0} in inventory");
            Add("together_card_relationship_preview", "relationship preview {0}");
            Add("together_card_variants_available", "variants available");
            Add("together_card_forced_participants", "{0}; involves {1}");
            Add("character_together_chance", "{0}, success chance {1}");
            Add("character_main_emotion", "{0}, main emotion {1}");

            // Fallback labels used only when the corresponding source-verified UnityEvent is
            // present and the control has no game-authored text or tooltip.
            Add("action_create_household", "Create New Household");
            Add("action_edit_household", "Edit in Paramaker");
            Add("action_play_household", "Play With This Household");
            Add("action_continue_tutorial", "Continue tutorial");
            Add("action_open_together_choices", "Choose a conversation topic");
            Add("action_select_together_group", "Select everyone in this conversation");
            Add("action_select_all_household", "Select all household Paras");
            Add("action_confirm_storyteller", "Select this Storyteller");
            Add("action_invite_selected_paras", "Invite selected Paras");
            Add("invite_picker", "Choose Paras to invite");
            Add("invite_picker_none", "Choose Paras to invite. No Paras are available.");
            Add("invite_picker_one", "Choose Paras to invite. 1 Para is available.");
            Add("invite_picker_many", "Choose Paras to invite. {0} Paras are available.");

            // ===== Undo / redo =====
            // The game gives these no feedback at all, and UndoRedoAction carries no description, so
            // the mod reports only that it happened - never a guess at what changed.
            Add("undo_done", "Undone.");
            Add("redo_done", "Redone.");

            // ===== Text fields =====
            // Read from the field's placeholder and contents, so every route to the same control
            // says the same thing. {0} is the field name, {1} its contents.
            Add("field_value", "{0}, {1}");
            Add("field_empty", "{0}, empty");

            // Native Settings inspector controls.
            Add("settings_slider", "{0}, {1}, range {2} to {3}. Left and right adjust; Enter to type a value.");
            Add("settings_numeric", "{0}, {1}, range {2} to {3}, edit field");
            Add("value_entry_started", "Edit value, {0}. Enter to finish.");
            Add("settings_axis_x", "{0}, X");
            Add("settings_axis_y", "{0}, Y");
            Add("slider_invalid_value", "Enter a number from 0 to 100. Value unchanged.");
            Add("action_customize_storyteller", "Customize this storyteller");
            Add("action_collect_stamp", "Collect stamp");
            Add("mailbox_stamp_owned", "Stamp, already in your collection");
            Add("mailbox_stamp_collected", "Stamp collected");
            Add("settings_toggle", "{0}, {1}, checkbox");
            Add("settings_on", "on");
            Add("settings_off", "off");
            Add("settings_choice", "{0}, {1}, dropdown");
            Add("settings_binding", "{0}, {1}, key binding");
            Add("settings_reset_binding", "Reset {0} to default, currently {1}");
            Add("settings_unassigned", "not assigned");
            Add("settings_read_only", "{0}, read only");
            Add("settings_unchanged", "{0}, unchanged");
            Add("settings_error", "A settings control could not be accessed. Details are in the mod log.");
            Add("settings_categories", "Settings categories");
            Add("settings_panel", "Settings");
            Add("panel_context_menu", "Options");
            Add("dropdown_search", "Search options. Type to filter; Down to browse the options.");
            Add("dropdown_search_value", "Search options, {0}. Down to browse the options.");
            Add("dropdown_no_results", "No options available. Edit the search to change the results.");
            Add("settings_actions", "Settings actions");
            Add("settings_values", "Settings values");
            Add("settings_category_values", "{0} settings");
            Add("settings_reset_category", "Reset current settings category to defaults");
            Add("settings_binding_conflict", "{0}, duplicate binding warning");

            // ===== Personality randomize buttons =====
            // These ship a prefab placeholder caption ("Label"), so they are named from the section
            // they randomize instead. {0} is the trait category.
            Add("randomize_section", "Randomize {0}");
            Add("randomize_personality", "Randomize personality");

            // ===== Personality stats =====
            // A stat is a vertical gauge of one focusable per point. Announced as a single value.
            // {0} is the stat name, {1} the current points, {2} the maximum.
            Add("stat_value", "{0}, {1} of {2}");
            // Same, plus the game's own points-left label, which it shows only on first creation.
            Add("stat_value_with_points", "{0}, {1} of {2}, {3}");

            // ===== Value adjustment ('-' and '=') =====
            Add("adjust_not_adjustable", "Nothing to adjust here.");
            Add("adjust_locked", "This cannot be edited right now.");
            Add("adjust_at_max", "Already at maximum.");
            Add("adjust_at_min", "Already at minimum.");

            // ===== Name Details =====
            Add("name_panel", "Name Details");
            Add("name_first", "First name");
            Add("name_last", "Last name");
            Add("name_middle", "Middle name");
            Add("name_nickname", "Nickname");
            Add("name_has_first", "Has a first name");
            Add("name_has_last", "Has a last name");
            Add("name_has_middle", "Has a middle name");
            Add("name_has_nickname", "Has a nickname");
            Add("name_invert", "Show last name first");
            // {0} is the field name, {1} its current contents.
            Add("name_field", "{0}, {1}");
            Add("name_field_empty", "{0}, empty");
            // Spoken as typing begins, so the switch into typing mode is never silent. {0} is the field.
            Add("name_editing", "Editing {0}. Type, then press Enter.");

            Add("room_travel_open", "Send selected Paras to a room");
            Add("room_travel_here", "Send selected Paras here");
            Add("room_travel_panel", "Choose a room to walk to");
            Add("room_travel_unavailable", "Room walking is unavailable in the current context.");
            Add("room_travel_search", "Checking a walking route to {0}.");
            Add("room_travel_changed", "Room walking cancelled because the room or selected Paras changed.");
            Add("room_travel_no_path", "No complete walking route to {0} was found.");
            Add("room_travel_requested", "Asked the selected Paras to walk to {0}.");
            Add("camera_pitch_toggle", "Toggle top-down camera. Control F11");
            Add("camera_pitch_unavailable", "Top-down camera is available in Build mode with no form open.");
            Add("camera_pitch_top", "Top-down camera.");
            Add("camera_pitch_restored", "Previous camera angle restored.");
            Add("group_recovery_unavailable", "This linked arrangement cannot be recovered safely as a group.");
            Add("group_recovery_command", "Recover linked arrangement together, {0} objects");
            Add("group_recovery_start", "Recovering {0} linked objects as one native move preview. Review a clear location and press Enter to place, or Escape to restore the arrangement.");
            Add("placement_accept_actual", "Use current preview position for placement");
            Add("placement_actual_accepted", "Using current position: {0}. Enter places object when ready.");
            Add("native_attempt_command", "Try game placement anyway");
            Add("native_attempt_unavailable", "Native placement cannot be requested in this context.");
            Add("native_attempt_illegal", "The game marks the actual preview position or its required support as invalid. Native placement was not requested.");
            Add("native_attempt_armed", "Native placement review: {0}. {1}. This one attempt bypasses the mod's requested coordinates, room boundary, facing, alignment, overlap and roof clearance checks. The game's placement checks, lot ownership, floor and funds checks still apply. Press Enter separately to attempt placement. Moving or changing the preview cancels this approval.");
            Add("native_attempt_changed", "Native placement approval cancelled because the preview changed. Review it again in F11.");
            Add("native_attempt_not_placed", "The game did not place the preview. No further native attempt is armed.");
            Add("roof_clearance_blocked", "The preview intersects the sloped roof: {0}.");
            Add("roof_clearance_unknown", "Roof clearance could not be verified for this roof.");
            Add("ceiling_unavailable", "Keyboard ceiling placement is unavailable.");
            Add("ceiling_no_support", "There is no usable ceiling at that point. Nothing placed.");
            Add("ceiling_no_rooms", "No rooms with usable ceilings were found on this owned floor. Escape cancels the preview.");
            Add("ceiling_room_choice", "Choose a ceiling room. {0}, {1} of {2}. Arrows browse; Enter targets its ceiling; Escape cancels.");
            Add("ceiling_controls", "Ceiling of {0}. Arrows move {1}; Shift arrows {2}. F10 coordinates; End centres; Home frames and centres. F3 height and support; F11 rooms. Enter places object; Escape cancels.");
            Add("ceiling_choose_room", "Choose a ceiling room");
            Add("ceiling_centre", "Centre the preview in the ceiling room");
            Add("ceiling_outside", "The whole preview must remain under this room's ceiling. Nothing placed.");
            Add("ceiling_changed", "The ceiling room or support changed. Choose the room again in F11 before placing.");
            Add("ceiling_out_of_view", "Ceiling target outside the view. Home frames and centres this room.");
            Add("ceiling_wrong_support", "Object has not attached to the selected ceiling.");
            Add("ceiling_mismatch", "Ceiling position differs from your request. F3 reads it; F11 can use the current position.");
            Add("ceiling_height_invalid", "The hanging item crosses the ceiling or reaches below the floor. Nothing placed.");
            Add("ceiling_coordinate_hint", "Column and row from this room's interior reference point, separated by a semicolon. Each grid step is {0}; zero, zero is the reference point.");
            Add("ceiling_status", "Ceiling of {0}. {1}. {2}. {3}");
            Add("ceiling_height", "Ceiling {0} above the floor; lowest point {1} above the floor");
            Add("ceiling_attached", "Attached to the native ceiling support");
            Add("fit_access_unavailable", "Walking access cannot be checked on this floor yet. Wait for navigation to update, then try again. Nothing placed.");
            Add("fit_access_too_large", "Room too large for automatic access checks. Choose a position manually.");
            Add("fit_access_changed", "Room fit search cancelled because the navigation or existing furniture changed. Nothing placed.");
            Add("fit_access_door", "That position would obstruct a doorway approach.");
            Add("fit_access_target", "That position would block access to {0}.");
            Add("fit_access_route", "That position would block a walking route.");
            Add("fit_access_preview", "No clear walking approach to the preview was found there.");
            Add("fit_access_checked", "Walking space and known entry points checked");
            Add("fit_find_current", "Find a clear place in the current room");
            Add("fit_choose_room", "Find a clear place in a chosen room");
            Add("fit_room_choice", "Search {0}");
            Add("fit_unavailable", "Room fit search is available for floor furniture in a room on the current owned floor.");
            Add("fit_searching", "Searching {0} for a place where the preview fits. Nothing will be purchased. Escape cancels.");
            Add("fit_wait", "Still searching. Nothing has been placed.");
            Add("fit_changed", "Room fit search cancelled because the preview or room changed.");
            Add("fit_found", "Found a place: {0}. Facing {1} degrees. {2}. Enter places object.");
            Add("fit_none", "No clear place found in {0} within this search. Try another rotation, size or room.");

            // ===== Prompts (modal dialogs) =====
            // A prompt often explains why an action was REFUSED, and the game raises the same window
            // for every refusal - only the message differs. It must be read in full.
            // {0} is the title, {1} the message.
            Add("prompt_body", "{0}. {1}");
            Add("prompt_opened", "Prompt opened");
            // {0} is the prompt above, {1} the comma-separated button labels.
            Add("prompt_with_choices", "{0}. Choices: {1}");

            // ===== Panel navigation =====
            // {0} is the panel name, {1} the number of items it contains.
            Add("panel_selected", "{0}, {1} items");
            Add("panel_selected_one", "{0}, 1 item");
            // {0} is the panel announcement above, {1} its position, {2} the total panel count.
            Add("panel_position", "{0}, panel {1} of {2}");
            // {0} is the panel announcement above, {1} the element that took focus.
            Add("panel_with_focus", "{0}, {1}");
            Add("panel_only_one", "Only one panel here.");
            Add("panel_none", "No panels here.");

            // ===== HUD visibility =====
            // Ctrl+H (ToggleUI) hides every game window at once, with no other feedback.
            Add("hud_hidden", "Game interface hidden.");
            Add("hud_shown", "Game interface shown.");

            // ===== Screens and orientation =====
            // {0} is the screen name, {1} the number of elements on it.
            Add("window_opened", "{0}, {1} items");
            Add("window_opened_one", "{0}, 1 item");
            Add("window_closed", "{0} closed.");
            // {0} screen, {1} panel, {2} focused element.
            Add("context_report", "{0}, panel {1}, on {2}");
            Add("context_with_tutorial", "{0}. Current tutorial: {1}");
            Add("context_no_window", "No screen");
            Add("context_no_panel", "no panel");
            Add("context_no_focus", "nothing focused");
            Add("context_unavailable_no_player",
                "Context unavailable: the local player is changing. Game state: {0}.");
            Add("context_unavailable_transition",
                "Context unavailable because the game interface is changing.");
        }
    }
}
