using System;
using System.Collections.Generic;
using System.Linq;
using Setting;
using UnityEngine;
using UnityEngine.AI;

namespace ParalivesAccess.Handlers
{
    /// <summary>A read-only room navigation graph with hypothetical preview footprints.</summary>
    internal sealed class RoomAccessPlan
    {
        private const float Step = .25f, ApproachDistance = .45f;
        private const int MaximumCells = 8192;
        // Native locomotion/entry locator assets in game 0.1.9, including chair sides and bed entries.
        private static readonly HashSet<ulong> FeetTags = new HashSet<ulong> {
            6996072453060391974UL,7179538376431173144UL,4336806274309240895UL,
            2540398331913000529UL,5479144738183256213UL,719297296838115127UL,
            3330522122511665159UL,2617983848501542305UL,1280333560214457935UL };
        private readonly ZoneWallObject _room;
        private readonly List<Node> _nodes = new List<Node>();
        private readonly List<Footprint> _doors = new List<Footprint>();
        private readonly List<Target> _targets = new List<Target>();
        private readonly List<Observation> _observations;
        private readonly float _radius, _floor, _bodyHeight;
        private int _components;
        private int[][] _edges;
        private int[] _baseline;
        internal int NodeCount => _nodes.Count;
        internal int DoorCount => _doors.Count;
        internal int TargetCount => _targets.Count;

        private sealed class Node { internal Vector3 Point; internal readonly List<int> Neighbours = new List<int>(); internal int Component = -1; }
        private sealed class Option { internal Vector3 Point; internal int[] Nodes; }
        private sealed class Target { internal string Name; internal List<Option> Options; }
        private sealed class Observation
        {
            private readonly ItemObjectRoot _item; private readonly Vector3 _point, _size; private readonly Quaternion _rotation;
            internal Observation(ItemObjectRoot item) { _item=item;_point=item.CubeTransform.LocalPosition;_size=item.CubeTransform.FinalSize;_rotation=item.transform.rotation; }
            internal bool Current => _item!=null&&_item.gameObject.activeInHierarchy&&Vector3.Distance(_point,_item.CubeTransform.LocalPosition)<.01f
                &&Vector3.Distance(_size,_item.CubeTransform.FinalSize)<.001f&&Quaternion.Angle(_rotation,_item.transform.rotation)<.1f;
        }
        internal bool Current => RoomLabelHandler.IsCurrent(_room)&&_observations.All(o=>o.Current)
            && PathfindingManager.Instance.CurrentNavMeshState==PathfindingManager.NavMeshState.Idle;

        private RoomAccessPlan(ZoneWallObject room, List<ItemObjectRoot> existing)
        {
            _room=room;_floor=room.AveragePosition.y;
            var settings=NavMesh.GetSettingsByIndex(0);_radius=Mathf.Max(.2f,settings.agentRadius);_bodyHeight=Mathf.Max(1.5f,settings.agentHeight);
            _observations=existing.Select(i=>new Observation(i)).ToList();
        }

        internal static RoomAccessPlan Build(ZoneWallObject room, ItemObjectRoot preview, out string problem)
        {
            problem=null;
            if(room==null||PathfindingManager.Instance==null||NavMesh.GetSettingsCount()==0
                ||PathfindingManager.Instance.CurrentNavMeshState!=PathfindingManager.NavMeshState.Idle)
            {problem=Loc.Get("fit_access_unavailable");return null;}
            var existing=ItemManager.Instance.GetLotItems(room.LotPlacedOnGUID).Where(i=>i!=null&&i.gameObject.activeInHierarchy&&i.Layer==room.Layer
                &&i.CubeTransform!=null&&!ExistingItemEditHandler.MovesWith(preview,i)).ToList();
            var plan=new RoomAccessPlan(room,existing);
            var vertices=room.Segments.SelectMany(s=>s.Segment.SegmentPoints).ToArray();
            float minX=vertices.Min(p=>p.x),maxX=vertices.Max(p=>p.x),minZ=vertices.Min(p=>p.z),maxZ=vertices.Max(p=>p.z);
            int columns=Mathf.CeilToInt((maxX-minX)/Step),rows=Mathf.CeilToInt((maxZ-minZ)/Step);
            if(columns<1||rows<1||(long)columns*rows>MaximumCells){problem=Loc.Get("fit_access_too_large");return null;}
            int mask=PathfindingManager.GetGenericNavmeshAreaMaskUsable();var cells=new Dictionary<int,int>();
            for(int z=0;z<rows;z++)for(int x=0;x<columns;x++) {
                var p=new Vector3(minX+(x+.5f)*Step,plan._floor,minZ+(z+.5f)*Step);
                if(!ZoneGeometry.Contains(room,p)||!NavMesh.SamplePosition(p,out var hit,.18f,mask)||Math.Abs(hit.position.y-plan._floor)>.18f
                    ||!ZoneGeometry.Contains(room,hit.position))continue;
                cells[z*columns+x]=plan._nodes.Count;plan._nodes.Add(new Node{Point=hit.position});
            }
            if(plan._nodes.Count<4){problem=Loc.Get("fit_access_unavailable");return null;}
            foreach(var cell in cells) {
                int x=cell.Key%columns,z=cell.Key/columns;
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++) {
                    if((dx==0&&dz==0)||x+dx<0||x+dx>=columns||z+dz<0||z+dz>=rows)continue;
                    if(cells.TryGetValue((z+dz)*columns+x+dx,out int other)&&other>cell.Value
                        &&!NavMesh.Raycast(plan._nodes[cell.Value].Point,plan._nodes[other].Point,out _,mask)) {
                        plan._nodes[cell.Value].Neighbours.Add(other);plan._nodes[other].Neighbours.Add(cell.Value);
                    }
                }
            }
            plan.LabelComponents();
            plan._edges=plan._nodes.Select(n=>n.Neighbours.ToArray()).ToArray();plan._baseline=plan._nodes.Select(n=>n.Component).ToArray();
            foreach(var item in existing) {
                if(item.GetComponent<ItemObjectDoor>() is ItemObjectDoor door) {
                    var box=Footprint.Read(item,plan._floor,plan._bodyHeight);
                    var centre=box.Centre;var normal=box.Forward;
                    if(ZoneGeometry.Contains(room,centre+normal*.45f)||ZoneGeometry.Contains(room,centre-normal*.45f)) {
                        box.HalfDepth=Mathf.Max(box.HalfDepth,.4f);plan._doors.Add(box);
                    }
                }
                plan._targets.AddRange(plan.ReadTargets(item));
            }
            return plan;
        }

        private void LabelComponents()
        {
            var queue=new Queue<int>();
            for(int i=0;i<_nodes.Count;i++)if(_nodes[i].Component<0) {
                int component=_components++;_nodes[i].Component=component;queue.Enqueue(i);
                while(queue.Count>0)foreach(int n in _nodes[queue.Dequeue()].Neighbours)if(_nodes[n].Component<0){_nodes[n].Component=component;queue.Enqueue(n);}
            }
        }
        private IEnumerable<Target> ReadTargets(ItemObjectRoot root)
        {
            foreach(var group in root.GetComponentsInChildren<ItemObjectLocator>(true).Where(l=>l.gameObject.activeInHierarchy&&FeetTags.Contains(l.Tag)
                &&Math.Abs(l.transform.position.y-_floor)<.4f&&ZoneGeometry.Contains(_room,l.transform.position)).GroupBy(l=>l.SlotGUID)) {
                // Side-entry markers are the approach to seated/lying slots. FeetUse/Butt-adjacent
                // markers can be inside an occupied desk or seat and are not alternative entries.
                var entry=group.Where(l=>l.Tag==2540398331913000529UL||l.Tag==5479144738183256213UL).ToArray();
                var locators=entry.Length>0?(IEnumerable<ItemObjectLocator>)entry:group;
                var options=locators.Select(l=>l.transform.position).Distinct()
                    .Where(p=>NavMesh.SamplePosition(p,out var hit,.25f,PathfindingManager.GetGenericNavmeshAreaMaskUsable())&&Math.Abs(hit.position.y-_floor)<.2f)
                    .Select(p=>new Option{Point=p,Nodes=Near(p)}).Where(o=>o.Nodes.Length>0).ToList();
                if(options.Count>0)yield return new Target{Name=ItemDescription.GetProductIdentity(root)??Loc.Get("item_unknown"),Options=options};
            }
        }
        private int[] Near(Vector3 point) {var result=new List<int>();for(int i=0;i<_nodes.Count;i++)if(Horizontal(_nodes[i].Point,point)<=ApproachDistance)result.Add(i);return result.ToArray();}
        private static float Horizontal(Vector3 a,Vector3 b)=>Vector2.Distance(new Vector2(a.x,a.z),new Vector2(b.x,b.z));

        internal string Problem(IReadOnlyList<ItemObjectRoot> members)
        {
            if(!Current)return Loc.Get("fit_access_changed");
            var boxes=members.Where(i=>i.PathfindingImpact!=PathfindingImpact.None
                &&(i.PathfindingImpact==PathfindingImpact.AlwaysBlock||i.CubeTransform.FinalSize.y>=Settings.Get<CharacterLocomotion>().FloorObstaclesMinimumHeight))
                .Select(i=>Footprint.Read(i,_floor,_bodyHeight)).Where(b=>b.OccupiesBody).ToArray();
            if(boxes.Any(b=>_doors.Any(d=>b.Overlaps(d,_radius))))return Loc.Get("fit_access_door");
            var blocked=_nodes.Select(n=>boxes.Any(b=>b.Contains(n.Point,_radius))).ToArray();
            foreach(var target in _targets)if(!target.Options.Any(o=>!boxes.Any(b=>b.Contains(o.Point,.05f))&&o.Nodes.Any(i=>!blocked[i])))
                return Loc.Get("fit_access_target",target.Name);
            if(!AccessConnectivity.Preserved(_edges,_baseline,blocked,
                (from,to)=>!boxes.Any(b=>b.Crosses(_nodes[from].Point,_nodes[to].Point,_radius))))return Loc.Get("fit_access_route");
            // New items also need a walkable approach; linked seats retain their native entries.
            foreach(var member in members) {
                var targets=ReadTargets(member).ToList();
                foreach(var target in targets)if(!target.Options.Any(o=>o.Nodes.Any(i=>!blocked[i])))return Loc.Get("fit_access_preview");
                if(targets.Count==0&&boxes.Any(b=>b.Item==member)) {
                    var box=boxes.First(b=>b.Item==member);
                    if(!_nodes.Where((n,i)=>!blocked[i]).Any(n=>box.Distance(n.Point)<=_radius+.45f))return Loc.Get("fit_access_preview");
                }
            }
            return null;
        }

        internal sealed class Footprint
        {
            internal ItemObjectRoot Item; internal Vector3 Centre,Right,Forward; internal float HalfWidth,HalfDepth;internal bool OccupiesBody;
            internal static Footprint Read(ItemObjectRoot item,float floor,float height) {
                var corners=ItemFootprintReader.Corners(item);var result=new Footprint{Item=item,Centre=item.GetWorldBoundingBox().Center};
                result.Right=new Vector3(item.transform.right.x,0,item.transform.right.z).normalized;if(result.Right.sqrMagnitude<.5f)result.Right=Vector3.right;
                result.Forward=new Vector3(-result.Right.z,0,result.Right.x);
                foreach(var p in corners){var d=p-result.Centre;result.HalfWidth=Mathf.Max(result.HalfWidth,Mathf.Abs(Vector3.Dot(d,result.Right)));result.HalfDepth=Mathf.Max(result.HalfDepth,Mathf.Abs(Vector3.Dot(d,result.Forward)));}
                result.OccupiesBody=corners.Count>0&&corners.Max(p=>p.y)>floor+.05f&&corners.Min(p=>p.y)<floor+height;return result;
            }
            internal bool Contains(Vector3 p,float padding){var d=p-Centre;return Math.Abs(Vector3.Dot(d,Right))<HalfWidth+padding&&Math.Abs(Vector3.Dot(d,Forward))<HalfDepth+padding;}
            internal float Distance(Vector3 p){var d=p-Centre;float x=Mathf.Max(0,Mathf.Abs(Vector3.Dot(d,Right))-HalfWidth),z=Mathf.Max(0,Mathf.Abs(Vector3.Dot(d,Forward))-HalfDepth);return Mathf.Sqrt(x*x+z*z);}
            internal bool Overlaps(Footprint other,float padding) {
                var d=other.Centre-Centre;foreach(var axis in new[]{Right,Forward,other.Right,other.Forward})
                    if(Math.Abs(Vector3.Dot(d,axis))>Extent(axis)+other.Extent(axis)+padding)return false;return true;
            }
            private float Extent(Vector3 axis)=>HalfWidth*Math.Abs(Vector3.Dot(Right,axis))+HalfDepth*Math.Abs(Vector3.Dot(Forward,axis));
            internal bool Crosses(Vector3 a,Vector3 b,float padding) {
                var d=a-Centre;var delta=b-a;float min=0,max=1;
                return Clip(Vector3.Dot(d,Right),Vector3.Dot(delta,Right),HalfWidth+padding,ref min,ref max)
                    &&Clip(Vector3.Dot(d,Forward),Vector3.Dot(delta,Forward),HalfDepth+padding,ref min,ref max);
            }
            private static bool Clip(float from,float delta,float half,ref float min,ref float max) {
                if(Math.Abs(delta)<.000001f)return Math.Abs(from)<half;float a=(-half-from)/delta,b=(half-from)/delta;if(a>b){float swap=a;a=b;b=swap;}
                min=Mathf.Max(min,a);max=Mathf.Min(max,b);return min<=max;
            }
        }
    }
}
