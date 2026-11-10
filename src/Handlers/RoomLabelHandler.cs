using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using ParalivesAccess.Speech;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    /// <summary>Player-authored names shared by navigation, descriptions and saved games.</summary>
    internal static class RoomLabelHandler
    {
        internal static bool Enabled { get; set; }
        private static RoomLabelData _data;
        private static AssetSavedGame _save;
        private static bool _readFailed;
        private static readonly Dictionary<ulong, int> ReadFrames = new Dictionary<ulong, int>();
        private static GameSavingRequest _saving;
        private static RoomLabelData _snapshot;
        private static string _snapshotFolder;
        private static bool _written;

        internal static bool ApplyPatches(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(GameSavingManager), "UpdateRequest", new[] { typeof(GameSavingRequest) });
                var before = AccessTools.Method(typeof(RoomLabelHandler), nameof(BeforeSaveStep));
                var after = AccessTools.Method(typeof(RoomLabelHandler), nameof(AfterSaveStep));
                if (method == null) return false;
                harmony.Patch(method, prefix: new HarmonyMethod(before), postfix: new HarmonyMethod(after));
                var patches = Harmony.GetPatchInfo(method);
                return patches?.Prefixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == before) == true
                    && patches.Postfixes.Any(p => p.owner == Plugin.PluginGuid && p.PatchMethod == after);
            }
            catch (Exception ex) { Failure("Installing room-label save hooks", ex); return false; }
        }

        internal static void Tick()
        {
            if (GameLoadingManager.State != State.Game)
            {
                _data = null; _save = null; _readFailed = false; ReadFrames.Clear();
            }
        }

        private static bool EnsureData()
        {
            if (!Enabled) return false;
            var save = SavedGameManager.Instance?.CurrentSavedGame;
            if (save == null || string.IsNullOrEmpty(save.FilePath)) return false;
            if (_save == save) return !_readFailed && _data != null;
            _save = save; _data = null; _readFailed = false; ReadFrames.Clear();
            try { _data = RoomLabelFile.Read(save.FilePath); return true; }
            catch (Exception ex) { _readFailed = true; Failure("Reading room labels", ex); return false; }
        }

        internal static bool IsCurrent(ZoneWallObject room) => room != null
            && BuildModeRefreshManager.IsZoneStateRefreshed && ZoneManager.Instance != null
            && ZoneManager.Instance.DirtyWallZones.Count == 0
            && RoomSpatialReader.IsUsableRoom(room, room.LotPlacedOnGUID)
            && ZoneManager.Instance.GetLotZones(room.LotPlacedOnGUID).Contains(room);

        private static RoomLabelIdentity ReadRoom(ZoneWallObject room)
        {
            var result = new RoomLabelIdentity { Id = room.InstanceID, Layer = room.Layer,
                Edges = room.Segments.Select(e => Edge(e.Segment.InstanceID, e.FromAToB)).OrderBy(s => s, StringComparer.Ordinal).ToList() };
            if (!SpatialOutline.TryRead(room, new PlacementGrid(0, 0, 0, 1), out var outline)) return result;
            var points = outline.Points.Select(p => p.X.ToString("R", CultureInfo.InvariantCulture) + ","
                + p.Y.ToString("R", CultureInfo.InvariantCulture)).ToList();
            int first = points.IndexOf(points.OrderBy(p => p, StringComparer.Ordinal).First());
            string forward = string.Join(";", Enumerable.Range(0, points.Count).Select(i => points[(first + i) % points.Count]));
            string reverse = string.Join(";", Enumerable.Range(0, points.Count).Select(i => points[(first - i + points.Count) % points.Count]));
            result.Shape = string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
            return result;
        }

        private static string Edge(int id, bool direction) => id.ToString(CultureInfo.InvariantCulture) + (direction ? "+" : "-");

        private static RoomLabelLot ReadLot(ulong guid, bool force = false)
        {
            if (!EnsureData() || !BuildModeRefreshManager.IsZoneStateRefreshed
                || ZoneManager.Instance == null || ZoneManager.Instance.DirtyWallZones.Count != 0) return null;
            var lot = _data.Lot(guid);
            bool seen = ReadFrames.TryGetValue(guid, out int frame);
            if (!force && seen && frame == Time.frameCount) return lot;
            var rooms = ZoneManager.Instance.GetLotZones(guid).OfType<ZoneWallObject>()
                .Where(r => RoomSpatialReader.IsUsableRoom(r, guid)).Select(ReadRoom).ToList();
            // A newly loaded save must prove its contours. An ID alone cannot validate a
            // stale sidecar, a copied lot, or a layout saved while an earlier write failed.
            if (!seen) foreach (var label in lot.Labels) label.RoomId = -1;
            lot.Reconcile(rooms); ReadFrames[guid] = Time.frameCount;
            return lot;
        }

        internal static string CustomName(ZoneWallObject room) => IsCurrent(room)
            ? ReadLot(room.LotPlacedOnGUID)?.Find(room.InstanceID)?.Text : null;

        internal static string Name(ZoneWallObject room, int ordinal = 0, int count = 0)
        {
            if (room == null) return Loc.Get("explorer_outdoors");
            if (ordinal <= 0) ordinal = RoomSpatialReader.OrderedRooms(room.LotPlacedOnGUID, room.Layer).IndexOf(room) + 1;
            string fallback = count > 0 ? Loc.Get("explorer_unnamed_room", ordinal, count) : Loc.Get("build_camera_room", ordinal);
            if (!IsCurrent(room)) return fallback;
            var lot = ReadLot(room.LotPlacedOnGUID);
            string name = lot?.Find(room.InstanceID)?.Text;
            if (name == null) return fallback;
            return lot.Labels.Count(l => l.RoomId >= 0 && string.Equals(l.Text, name, StringComparison.OrdinalIgnoreCase)) > 1
                ? Loc.Get("room_label_duplicate", name, ordinal, room.Layer) : name;
        }

        internal static List<RoomLabel> Unassigned(ZoneWallObject room) => IsCurrent(room)
            ? ReadLot(room.LotPlacedOnGUID)?.Labels.Where(l => l.RoomId < 0).ToList() ?? new List<RoomLabel>() : new List<RoomLabel>();

        internal static void Open(ZoneWallObject room)
        {
            if (!IsCurrent(room) || ReadLot(room.LotPlacedOnGUID) == null)
            { Speak(Loc.Get("room_label_unavailable")); return; }
            var hybrid = PlayerManager.Instance?.HybridPlayer1;
            var save = _save; int id = room.InstanceID; var contour = ReadRoom(room);
            CoordinateEntryHandler.OpenText(hybrid, Loc.Get(CustomName(room) == null ? "room_label_name" : "room_label_rename"),
                Name(room), Loc.Get("room_label_hint"), CustomName(room),
                () => SavedGameManager.Instance?.CurrentSavedGame == save && IsCurrent(room) && room.InstanceID == id
                    && contour.SameShape(ReadRoom(room)),
                text => Set(room, text));
        }

        private static void Set(ZoneWallObject room, string text)
        {
            var lot = IsCurrent(room) ? ReadLot(room.LotPlacedOnGUID, true) : null;
            var identity = lot?.Rooms.FirstOrDefault(r => r.Id == room.InstanceID);
            string name = ScreenReader.NormalizeForOutput(text)?.Trim();
            if (identity == null || string.IsNullOrEmpty(identity.Shape)) { Speak(Loc.Get("room_label_unavailable")); return; }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) { Speak(Loc.Get("room_label_invalid")); return; }
            lot.Set(identity, name);
            Speak(Loc.Get("room_label_set", Name(room)));
        }

        internal static void Clear(ZoneWallObject room)
        {
            var lot = IsCurrent(room) ? ReadLot(room.LotPlacedOnGUID, true) : null;
            var label = lot?.Find(room.InstanceID);
            if (label == null) { Speak(Loc.Get("room_label_unavailable")); return; }
            lot.Labels.Remove(label); Speak(Loc.Get("room_label_cleared"));
        }

        internal static void Reassign(ZoneWallObject room, string key)
        {
            var lot = IsCurrent(room) ? ReadLot(room.LotPlacedOnGUID, true) : null;
            var identity = lot?.Rooms.FirstOrDefault(r => r.Id == room.InstanceID);
            if (identity == null || string.IsNullOrEmpty(identity.Shape) || !lot.Reassign(key, identity))
            { Speak(Loc.Get("room_label_unavailable")); return; }
            Speak(Loc.Get("room_label_set", Name(room)));
        }

        private static void BeforeSaveStep(GameSavingRequest request, out GameSavingPhase __state)
        {
            __state = request.CurrentPhase;
            if (!Enabled) return;
            try
            {
                if (__state == GameSavingPhase.Init)
                {
                    _saving = request; _written = false; _snapshot = null;
                    if (EnsureData()) { _snapshot = RoomLabelFile.Copy(_data); _snapshotFolder = _save.FilePath; }
                }
                // All asset write phases precede these phases; native copy/autosave follows.
                if (request == _saving && !_written && _snapshot != null
                    && (__state == GameSavingPhase.CopySaveAsDefaultTown || __state == GameSavingPhase.ShowEndFloatingText))
                { _written = true; RoomLabelFile.Write(_snapshotFolder, _snapshot); }
            }
            catch (Exception ex) { _written = true; Failure("Saving room labels", ex); }
        }

        private static void AfterSaveStep(GameSavingRequest request, GameSavingPhase __state)
        {
            if (request != _saving || _snapshot == null) return;
            try
            {
                if (__state == GameSavingPhase.SaveLotFetchData && request.CurrentLot != null)
                {
                    var asset = request.CurrentLot;
                    var live = ReadLot(asset.GUID, true);
                    var copy = RoomLabelFile.Copy(live);
                    if (copy != null)
                    {
                        // Match the data the game actually fetched. A later edit must not enter
                        // this save's labels; unloaded lots retain their prior saved records.
                        var saved = asset.Data.Zones.Select(z => {
                            var edges = z.SegmentInstanceIDs.Select(e => Edge(e.SegmentEntityInstanceID, e.FromAToB)).OrderBy(s => s, StringComparer.Ordinal).ToList();
                            var known = copy.Rooms.FirstOrDefault(r => r.Id == z.InstanceID && r.Layer == z.Layer && r.Edges.SequenceEqual(edges));
                            return known ?? new RoomLabelIdentity { Id = z.InstanceID, Layer = z.Layer, Edges = edges };
                        }).ToList();
                        copy.Reconcile(saved);
                        _snapshot.Lots.RemoveAll(l => l.Guid == copy.Guid); _snapshot.Lots.Add(copy);
                    }
                }
                if (request.CurrentPhase == GameSavingPhase.Completed) { _saving = null; _snapshot = null; }
            }
            catch (Exception ex) { _snapshot = null; Failure("Capturing room labels for save", ex); }
        }

        private static void Failure(string action, Exception ex)
        {
            Plugin.Log.LogError(action + ": " + ex);
            Speak(Loc.Get("room_label_storage_error"));
        }
        private static void Speak(string text) => ScreenReader.Speak(text);
    }

    /// <summary>Atomic mod-only sidecar, carried by native saved-game package copies.</summary>
    internal static class RoomLabelFile
    {
        internal const string FileName = "ParalivesAccess.RoomLabels.json";
        // Unity's native serializer omits collections of these mod-defined records in the
        // player. Use the same explicit contract for snapshots and disk, including histories.
        internal static T Copy<T>(T value) where T : class
        {
            if (value == null) return null;
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                stream.Position = 0;
                return (T)serializer.ReadObject(stream);
            }
        }
        internal static RoomLabelData Read(string folder)
        {
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return new RoomLabelData();
            RoomLabelData data;
            using (var stream = File.OpenRead(path))
                data = (RoomLabelData)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(RoomLabelData)).ReadObject(stream);
            if (data == null || data.Version != 1 || data.Lots == null
                || data.Lots.Any(l => l == null || l.Rooms == null || l.Labels == null || string.IsNullOrEmpty(l.Guid)
                    || l.Rooms.Any(r => r == null || r.Edges == null)
                    || l.Labels.Any(n => n == null || string.IsNullOrWhiteSpace(n.Text) || n.History == null || n.UnassignedDestinations == null
                        || string.IsNullOrEmpty(n.Key) || n.History.Concat(n.UnassignedDestinations).Any(r => r == null || r.Edges == null))))
                throw new InvalidDataException("Unrecognized room-label metadata; original file retained.");
            return data;
        }
        internal static void Write(string folder, RoomLabelData data)
        {
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path) && !data.Lots.Any(l => l.Labels.Count != 0)) return;
            string temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonWriter(
                stream, System.Text.Encoding.UTF8, false, true, "  "))
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(RoomLabelData)).WriteObject(writer, data);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
