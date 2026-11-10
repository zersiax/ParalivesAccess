using System;
using System.Linq;
using UnityEngine;

namespace ParalivesAccess.Handlers
{
    internal static class RoofClearanceReader
    {
        private static BoxCollider _box;
        private static MeshCollider _mesh;
        private static int _frame = -1;
        private static ItemObjectRoot _root;
        private static Vector3 _position, _size;
        private static Quaternion _rotation;
        private static string _result;
        private static void Prepare()
        {
            if (_box != null && _mesh != null) return;
            var a = new GameObject("ParalivesAccess clearance box") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            var b = new GameObject("ParalivesAccess clearance mesh") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            a.transform.position = new Vector3(0,-10000,0); b.transform.position = new Vector3(0,-10000,10);
            UnityEngine.Object.DontDestroyOnLoad(a); UnityEngine.Object.DontDestroyOnLoad(b);
            _box = a.AddComponent<BoxCollider>(); _mesh = b.AddComponent<MeshCollider>(); _mesh.convex = false;
        }
        internal static string Problem(ItemObjectRoot item)
        {
            if (item?.CubeTransform == null || RoofConstructionHandler.IsRoof(item) || item.IsLotMovementItem) return null;
            var size = item.CubeTransform.FinalSize; var position = item.CubeTransform.LocalPosition; var rotation = item.transform.rotation;
            if (_frame == Time.frameCount && _root == item && Vector3.Distance(position,_position)<.001f
                && Vector3.Distance(size,_size)<.001f && Quaternion.Angle(rotation,_rotation)<.01f) return _result;
            _frame=Time.frameCount; _root=item; _position=position; _size=size; _rotation=rotation; _result=null;
            var corners=ItemFootprintReader.Corners(item);
            if(corners.Count==0)return _result=Loc.Get("placement_geometry_unavailable");
            var bounds=new Bounds(corners[0],Vector3.zero);foreach(var p in corners)bounds.Encapsulate(p);
            var player=PlayerManager.Instance?.HybridPlayer1?.Player;
            foreach(var roof in ItemManager.Instance.GetLotItems(item.LotPlacedOnGUID))
            {
                if(roof==null||roof==item||!RoofConstructionHandler.IsRoof(roof)||Removed(player,roof))continue;
                bool hasMesh=false;
                foreach(var source in roof.GetComponentsInChildren<MeshCollider>(true))
                {
                    var geometry=source.sharedMesh;if(geometry==null)continue;hasMesh=true;
                    if(!WorldBounds(geometry.bounds,source.transform).Intersects(bounds))continue;
                    Prepare();_box.size=new Vector3(Mathf.Max(.001f,size.x-.004f),Mathf.Max(.001f,size.y-.004f),Mathf.Max(.001f,size.z-.004f));
                    // Cook the same native triangles on an off-scene query collider. This works
                    // when the actual roof collider is disabled or hidden by floor visibility.
                    _mesh.sharedMesh=null;_mesh.transform.localScale=source.transform.lossyScale;_mesh.sharedMesh=geometry;Physics.SyncTransforms();
                    if(Physics.ComputePenetration(_box,item.GetWorldBoundingBox().Center,rotation,_mesh,source.transform.position,
                        source.transform.rotation,out var direction,out float distance)&&distance>.004f)
                        return _result=Loc.Get("roof_clearance_blocked",ItemDescription.GetProductIdentity(roof)??Loc.Get("item_unknown"));
                }
                if(!hasMesh&&ItemFootprintReader.Corners(roof).Any(bounds.Contains))return _result=Loc.Get("roof_clearance_unknown");
            }
            return _result;
        }
        private static bool Removed(Player p,ItemObjectRoot item)=>p!=null&&p.UndoRedoStack.Any(a=>
            (a is DeleteItemAction d&&d.Item==item&&(p.UndoRedo.ActionCursor<=0||a.ActionID<p.UndoRedo.ActionCursor))
            ||(a is CreateItemAction c&&c.Item==item&&p.UndoRedo.ActionCursor>0&&a.ActionID>=p.UndoRedo.ActionCursor));
        private static Bounds WorldBounds(Bounds local,Transform t)
        {
            var result=new Bounds(t.TransformPoint(local.center),Vector3.zero);
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                result.Encapsulate(t.TransformPoint(local.center+Vector3.Scale(local.extents,new Vector3(x,y,z))));
            return result;
        }
    }
}
