using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace ParalivesAccess.Handlers
{
    // These are mod-owned save records, not additions to the native lot schema.
    [DataContract]
    internal sealed class RoomLabelData
    {
        /// <summary>Version of the mod's sidecar schema.</summary>
        [DataMember(IsRequired = true)] public int Version = 1;
        /// <summary>Room labels grouped by native lot GUID.</summary>
        [DataMember(IsRequired = true)] public List<RoomLabelLot> Lots = new List<RoomLabelLot>();
        internal RoomLabelLot Lot(ulong guid)
        {
            string key = guid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lot = Lots.FirstOrDefault(l => l.Guid == key);
            if (lot == null) { lot = new RoomLabelLot { Guid = key }; Lots.Add(lot); }
            return lot;
        }
    }

    [DataContract]
    internal sealed class RoomLabelIdentity
    {
        /// <summary>Saved native room ID and floor layer.</summary>
        [DataMember(IsRequired = true)] public int Id, Layer;
        /// <summary>Canonical horizontal contour, when available.</summary>
        [DataMember] public string Shape;
        /// <summary>Directed native boundary segment IDs.</summary>
        [DataMember(IsRequired = true)] public List<string> Edges = new List<string>();
        internal bool SameShape(RoomLabelIdentity other) => other != null && Layer == other.Layer
            && !string.IsNullOrEmpty(Shape) && Shape == other.Shape;
        internal bool Related(RoomLabelIdentity other) => Layer == other.Layer
            && (Id == other.Id || SameShape(other) || Edges.Intersect(other.Edges).Any());
    }

    [DataContract]
    internal sealed class RoomLabel
    {
        /// <summary>Identity of the label for explicit reassignment.</summary>
        [DataMember(IsRequired = true)] public string Key = System.Guid.NewGuid().ToString("N");
        /// <summary>Player-authored name.</summary>
        [DataMember(IsRequired = true)] public string Text;
        /// <summary>Assigned native room ID, or minus one when retained unassigned.</summary>
        [DataMember(IsRequired = true)] public int RoomId = -1;
        /// <summary>Previously verified contours for reconstruction after undo.</summary>
        [DataMember(IsRequired = true)] public List<RoomLabelIdentity> History = new List<RoomLabelIdentity>();
        /// <summary>Ambiguous destination contours that must not acquire this label on a later read.</summary>
        [DataMember(IsRequired = true)] public List<RoomLabelIdentity> UnassignedDestinations = new List<RoomLabelIdentity>();
    }

    [DataContract]
    internal sealed class RoomLabelLot
    {
        /// <summary>Native lot GUID serialized without numeric precision loss.</summary>
        [DataMember(IsRequired = true)] public string Guid;
        /// <summary>Last observed room identities for detecting splits and merges.</summary>
        [DataMember(IsRequired = true)] public List<RoomLabelIdentity> Rooms = new List<RoomLabelIdentity>();
        /// <summary>Assigned and retained labels.</summary>
        [DataMember(IsRequired = true)] public List<RoomLabel> Labels = new List<RoomLabel>();

        internal RoomLabel Find(int id) => Labels.FirstOrDefault(l => l.RoomId == id);

        internal void Reconcile(List<RoomLabelIdentity> current)
        {
            var proposals = new Dictionary<RoomLabel, RoomLabelIdentity>();
            var ambiguousDestinations = new HashSet<RoomLabelIdentity>();
            foreach (var label in Labels)
            {
                var previous = Rooms.FirstOrDefault(r => r.Id == label.RoomId);
                var related = previous == null ? new List<RoomLabelIdentity>() : current.Where(previous.Related).ToList();
                bool countChanged = Rooms.Count != current.Count;
                bool ambiguous = previous != null && (related.Count > 1
                    || related.Any(next => Rooms.Count(old => old.Related(next)) > 1)
                    || (countChanged && related.Any(next => !previous.SameShape(next))));
                if (ambiguous)
                {
                    foreach (var next in related) ambiguousDestinations.Add(next);
                    // Remember ambiguity across reads. Otherwise a half-room matching an older
                    // contour could acquire the retained name on the very next frame.
                    foreach (var next in countChanged ? current.Where(r => r.Layer == previous.Layer) : related)
                        if (!string.IsNullOrEmpty(next.Shape) && !label.UnassignedDestinations.Any(r => r.SameShape(next)))
                            label.UnassignedDestinations.Add(next);
                    proposals[label] = null;
                    continue;
                }
                // Exact historical contours can recover undo, including reconstructed native IDs.
                var exact = current.Where(r => label.History.Any(h => h.SameShape(r))
                    && !label.UnassignedDestinations.Any(blocked => blocked.SameShape(r))).ToList();
                RoomLabelIdentity target = exact.Count == 1 ? exact[0] : null;
                if (exact.Count == 0 && previous != null)
                {
                    if (!countChanged && related.Count == 1
                        && !label.UnassignedDestinations.Any(blocked => blocked.SameShape(related[0])))
                        target = related[0];
                }
                proposals[label] = target;
            }
            foreach (var pair in proposals)
            {
                var target = pair.Value;
                // A retained name must not win a merge that made another assigned name
                // ambiguous, even if this result matches the retained name's old contour.
                if (target != null && ambiguousDestinations.Contains(target))
                {
                    if (!string.IsNullOrEmpty(target.Shape) && !pair.Key.UnassignedDestinations.Any(r => r.SameShape(target)))
                        pair.Key.UnassignedDestinations.Add(target);
                    target = null;
                }
                if (target == null || proposals.Values.Count(r => r == target) != 1)
                { pair.Key.RoomId = -1; continue; }
                pair.Key.RoomId = target.Id;
                if (!string.IsNullOrEmpty(target.Shape) && !pair.Key.History.Any(h => h.SameShape(target)))
                    pair.Key.History.Add(target);
            }
            Rooms = current;
        }

        internal void Set(RoomLabelIdentity room, string text)
        {
            var label = Find(room.Id);
            if (label == null) { label = new RoomLabel { RoomId = room.Id }; Labels.Add(label); }
            label.Text = text;
            if (!label.History.Any(h => h.SameShape(room))) label.History.Add(room);
        }

        internal bool Reassign(string key, RoomLabelIdentity room)
        {
            var label = Labels.FirstOrDefault(l => l.Key == key && l.RoomId < 0);
            if (label == null || Find(room.Id) != null) return false;
            label.RoomId = room.Id;
            // Explicit reassignment establishes a new identity, including for later undo.
            label.History = new List<RoomLabelIdentity> { room };
            label.UnassignedDestinations.Clear();
            return true;
        }
    }
}
