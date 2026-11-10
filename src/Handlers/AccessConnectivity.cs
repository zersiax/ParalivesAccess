using System;
using System.Collections.Generic;

namespace ParalivesAccess.Handlers
{
    /// <summary>Check that removing hypothetical nodes/edges does not split a baseline component.</summary>
    internal static class AccessConnectivity
    {
        internal static bool Preserved(IReadOnlyList<int[]> edges,IReadOnlyList<int> baseline,IReadOnlyList<bool> blocked,Func<int,int,bool> allowsEdge)
        {
            if(edges.Count!=baseline.Count||edges.Count!=blocked.Count)throw new ArgumentException("Navigation graph sizes differ");
            var seen=new bool[edges.Count];var components=new HashSet<int>();var queue=new Queue<int>();
            for(int start=0;start<edges.Count;start++) {
                if(blocked[start]||seen[start])continue;
                if(!components.Add(baseline[start]))return false;
                queue.Enqueue(start);seen[start]=true;
                while(queue.Count>0) {
                    int from=queue.Dequeue();foreach(int to in edges[from])
                        if(!blocked[to]&&!seen[to]&&allowsEdge(from,to)){seen[to]=true;queue.Enqueue(to);}
                }
            }
            return true;
        }
    }
}
