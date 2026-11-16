using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Pure-data marching cubes. Material selection is at the same CELL granularity as rewards.</summary>
public static class TerrainMeshBuilder
{
    public sealed class Result
    {
        public readonly List<Vector3> Vertices=new List<Vector3>();
        public readonly SortedDictionary<int,List<int>> Triangles=new SortedDictionary<int,List<int>>();
        internal readonly Dictionary<Vector3,int> VertexMap=new Dictionary<Vector3,int>();
        private readonly Stack<List<int>> _spareTriangleLists=new Stack<List<int>>();
        // Filled by BuildGeometry only: every triangle in build order, and the local cell that produced it.
        internal readonly List<int> FlatTriangles=new List<int>();
        internal readonly List<Vector3Int> TriangleCells=new List<Vector3Int>();
        internal void Clear()
        {
            Vertices.Clear(); VertexMap.Clear(); FlatTriangles.Clear(); TriangleCells.Clear();
            foreach(var triangles in Triangles.Values) { triangles.Clear(); _spareTriangleLists.Push(triangles); }
            Triangles.Clear();
        }
        internal List<int> NewTriangles() => _spareTriangleLists.Count>0 ? _spareTriangleLists.Pop() : new List<int>();
    }
    public static Result Build(float[,,] density,Vector3Int size,float spacing,Vector3Int globalOrigin,Func<Vector3Int,int> material,Result reuse=null)
    {
        var result=reuse??new Result(); result.Clear(); var map=result.VertexMap;
        // Entirely solid/air chunks have no surface. Avoid the full 8-corners-per-cell pass.
        bool firstSolid=density[0,0,0]<0f, mixed=false;
        for(int x=0;x<=size.x && !mixed;x++)for(int y=0;y<=size.y && !mixed;y++)for(int z=0;z<=size.z;z++)
            if((density[x,y,z]<0f)!=firstSolid) { mixed=true; break; }
        if(!mixed)return result;
        for(int x=0;x<size.x;x++)for(int y=0;y<size.y;y++)for(int z=0;z<size.z;z++)
        {
            int cube=0;
            for(int i=0;i<8;i++)
            {
                Vector3Int c=MarchingTable.Corners[i];
                if(density[x+c.x,y+c.y,z+c.z]<0f)cube|=1<<i;
            }
            if(MarchingTable.Triangles[cube,0]==-1)continue;
            int id=material(globalOrigin+new Vector3Int(x,y,z));
            if(id<=0)throw new InvalidOperationException("A surface cell needs a solid material ID, not Air.");
            List<int> triangles;
            if(!result.Triangles.TryGetValue(id,out triangles)) {triangles=result.NewTriangles();result.Triangles.Add(id,triangles);}
            for(int i=0;MarchingTable.Triangles[cube,i]!=-1;i+=3)
            {
                Vector3 a=Vertex(x,y,z,MarchingTable.Triangles[cube,i],density,spacing);
                Vector3 b=Vertex(x,y,z,MarchingTable.Triangles[cube,i+1],density,spacing);
                Vector3 c=Vertex(x,y,z,MarchingTable.Triangles[cube,i+2],density,spacing);
                if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)continue;
                triangles.Add(Index(c,result.Vertices,map));triangles.Add(Index(b,result.Vertices,map));triangles.Add(Index(a,result.Vertices,map));
            }
        }
        return result;
    }
    /// <summary>
    /// Geometry only: no material lookup and no Unity objects, so this is safe on a worker thread
    /// as long as nothing else writes to <paramref name="density"/> or <paramref name="result"/> meanwhile.
    /// Call <see cref="AssignMaterials"/> on the main thread afterwards.
    /// </summary>
    public static Result BuildGeometry(float[,,] density,Vector3Int size,float spacing,Result result)
    {
        result.Clear(); var map=result.VertexMap;
        bool firstSolid=density[0,0,0]<0f, mixed=false;
        for(int x=0;x<=size.x && !mixed;x++)for(int y=0;y<=size.y && !mixed;y++)for(int z=0;z<=size.z;z++)
            if((density[x,y,z]<0f)!=firstSolid) { mixed=true; break; }
        if(!mixed)return result;
        for(int x=0;x<size.x;x++)for(int y=0;y<size.y;y++)for(int z=0;z<size.z;z++)
        {
            int cube=0;
            for(int i=0;i<8;i++)
            {
                Vector3Int c=MarchingTable.Corners[i];
                if(density[x+c.x,y+c.y,z+c.z]<0f)cube|=1<<i;
            }
            if(MarchingTable.Triangles[cube,0]==-1)continue;
            Vector3Int cell=new Vector3Int(x,y,z);
            for(int i=0;MarchingTable.Triangles[cube,i]!=-1;i+=3)
            {
                Vector3 a=Vertex(x,y,z,MarchingTable.Triangles[cube,i],density,spacing);
                Vector3 b=Vertex(x,y,z,MarchingTable.Triangles[cube,i+1],density,spacing);
                Vector3 c=Vertex(x,y,z,MarchingTable.Triangles[cube,i+2],density,spacing);
                if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)continue;
                result.FlatTriangles.Add(Index(c,result.Vertices,map));
                result.FlatTriangles.Add(Index(b,result.Vertices,map));
                result.FlatTriangles.Add(Index(a,result.Vertices,map));
                result.TriangleCells.Add(cell);
            }
        }
        return result;
    }
    /// <summary>
    /// Main-thread half of <see cref="BuildGeometry"/>: groups its triangles by material, producing
    /// the same <see cref="Result.Triangles"/> that <see cref="Build"/> would have produced.
    /// </summary>
    public static void AssignMaterials(Result result,Vector3Int globalOrigin,Func<Vector3Int,int> material)
    {
        bool have=false; Vector3Int last=default(Vector3Int); List<int> triangles=null;
        for(int t=0;t<result.TriangleCells.Count;t++)
        {
            Vector3Int cell=result.TriangleCells[t];
            if(!have || cell!=last)
            {
                int id=material(globalOrigin+cell);
                if(id<=0)throw new InvalidOperationException("A surface cell needs a solid material ID, not Air.");
                if(!result.Triangles.TryGetValue(id,out triangles)) {triangles=result.NewTriangles();result.Triangles.Add(id,triangles);}
                have=true; last=cell;
            }
            triangles.Add(result.FlatTriangles[t*3]);triangles.Add(result.FlatTriangles[t*3+1]);triangles.Add(result.FlatTriangles[t*3+2]);
        }
    }
    private static int Index(Vector3 v,List<Vector3> vertices,Dictionary<Vector3,int> map)
    {int index;if(map.TryGetValue(v,out index))return index;index=vertices.Count;vertices.Add(v);map.Add(v,index);return index;}
    private static Vector3 Vertex(int x,int y,int z,int edge,float[,,] d,float spacing)
    {
        Vector3 a=MarchingTable.Edges[edge,0],b=MarchingTable.Edges[edge,1];
        float da=d[x+(int)a.x,y+(int)a.y,z+(int)a.z],db=d[x+(int)b.x,y+(int)b.y,z+(int)b.z];
        Vector3 pa=(new Vector3(x,y,z)+a)*spacing,pb=(new Vector3(x,y,z)+b)*spacing;
        if(Mathf.Abs(da)<0.00001f)return pa;
        if(Mathf.Abs(db)<0.00001f)return pb;
        if(Mathf.Abs(da-db)<0.00001f)return pa;
        return pa+(pb-pa)*(-da/(db-da));
    }
}
