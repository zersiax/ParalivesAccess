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
            Add("transit_finder", "Bus stops in town, {0}");
            Add("transit_panel", "Bus stops in town. Enter opens the stop's actions");
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
            Add("catalog_closed", "Build catalogue browser closed.");
            Add("catalog_unavailable", "The build catalogue is not open. Enter build mode first.");
            Add("catalog_no_content", "No ordinary furniture is available in this category.");
            Add("catalog_entry_unavailable", "That catalogue entry is no longer available.");
            Add("catalog_preview_failed", "The game did not create an item preview. Catalogue browser restored.");
            Add("catalog_handler_failed", "The build catalogue browser was disabled after an internal error.");

            Add("placement_ready", "{0} preview, {1}. Arrows move it. Enter places it. Escape cancels.");
            Add("placement_moved", "Moved {0}, {1}.");
            Add("placement_rotated", "Rotated {0}, {1}.");
            Add("placement_unsupported_wall", "Wall items are not supported in this first version. Press Escape to cancel.");
            Add("placement_unsupported_ceiling", "Ceiling items are not supported in this first version. Press Escape to cancel.");
            Add("placement_lot_required", "Move the camera over an owned lot, then press Escape to cancel this preview.");
            Add("placement_framing", "Framing the owned lot for placement.");
            Add("placement_anchor_failed", "Could not establish a placement point inside the owned lot. Press Escape to cancel.");
            Add("placement_mouse_missing", "No mouse device is available for the game's placement raycast. Press Escape to cancel.");
            Add("placement_wait", "Wait for the new position to be validated, then press Enter again.");
            Add("placement_pointer_over_ui", "The placement point is over the game interface. Move it away before confirming.");
            Add("placement_valid", "valid");
            Add("placement_invalid", "invalid: {0}");
            Add("placement_occupied", "occupied by {0}");
            Add("placement_occupied_move", "That point is occupied by {0}. Move the preview before placing it.");
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
            Add("explorer_controls",
                "Arrows move, Enter opens or frames, Shift Enter adds or removes a highlighted Para from the selected group, Backspace goes back, and F7 closes");
            Add("explorer_closed", "Current lot explorer closed.");
            Add("explorer_live_required", "Return to live mode before exploring the current lot.");
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
            Add("explorer_selection_shortcut_requires_para", "Highlight a Para to add or remove them from the selected group with Shift Enter.");
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
