using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer),typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;
    private Mesh _mesh;
    private bool _hasBuiltMesh;
    // Empty (fully air or solid) chunks correctly have no surface mesh/collider.
    // A null mesh is only a fault when this chunk actually contains triangles.
    public bool CollisionReady => _hasBuiltMesh && gameObject.activeInHierarchy &&
        _meshCollider != null && _meshCollider.enabled && !_meshCollider.isTrigger && !_meshCollider.convex &&
        (_triangleMaterials.Count == 0 || _meshCollider.sharedMesh != null);
    [System.NonSerialized] public float[,,] _densities;
    private Vector3Int _size,_globalOrigin;
    private float _voxelSize;
    private readonly List<int> _triangleMaterials=new List<int>();
    private readonly TerrainMeshBuilder.Result _meshData=new TerrainMeshBuilder.Result();
    private readonly List<Material> _materials=new List<Material>();
    private System.Func<Vector3Int,int> _materialResolver;
    private void Awake()
    {
        _meshFilter=GetComponent<MeshFilter>();_meshRenderer=GetComponent<MeshRenderer>();_meshCollider=GetComponent<MeshCollider>();
        _materialResolver=ResolveMaterial;
        _mesh=new Mesh {name="Gobmine terrain",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};_meshFilter.sharedMesh=_mesh;
        _mesh.MarkDynamic();
    }
    public void Init(Vector3Int size,float voxelSize,Vector3 worldPos,float noiseScale,int seed,float[,,] loadedData=null)
    {
        _size=size;_voxelSize=voxelSize;_globalOrigin=Vector3Int.RoundToInt(worldPos/voxelSize);
        _densities=loadedData??new float[size.x+1,size.y+1,size.z+1];BuildMeshSync();
    }
    private void OnDestroy()
    {
        if (_mesh == null) return;
        if (Application.isPlaying) Destroy(_mesh);
        else DestroyImmediate(_mesh);
    }
    public float[,,] GetDensities()=>_densities;
    public int GetHitMaterialId(int triangleIndex)=>triangleIndex>=0 && triangleIndex<_triangleMaterials.Count?_triangleMaterials[triangleIndex]:0;
    public void EditTerrain(Vector3 hit,float radius,float amount)
    {if(amount==0){BuildMeshSync();return;}if(World.Instance!=null)World.Instance.RequestModifyTerrain(hit,radius,amount);}
    private int ResolveMaterial(Vector3Int cell) => World.Instance!=null?World.Instance.GetSolidMaterialId(cell):1;
    /// <summary>
    /// Prebuild a new terrain surface without changing the currently rendered or
    /// collidable surface. A falling-shape transaction publishes all staged
    /// neighbouring chunk meshes together with its Rigidbody in one frame.
    /// </summary>
    public sealed class PreparedMesh : System.IDisposable
    {
        internal Mesh Mesh;
        internal TerrainMeshBuilder.Result Data;
        internal readonly List<int> TriangleMaterials = new List<int>();
        internal readonly List<Material> Materials = new List<Material>();
        public void Dispose()
        {
            if (Mesh != null)
            {
                if (Application.isPlaying) Object.Destroy(Mesh);
                else Object.DestroyImmediate(Mesh);
                Mesh = null;
            }
            Data = null;
        }
    }

    public PreparedMesh PrepareMeshWithoutPublishing()
    {
        if (_densities == null) throw new System.InvalidOperationException("Cannot prepare an uninitialized chunk.");
        // Separate scratch state: ordinary BuildMeshSync() and another staged
        // build must not mutate a mesh that is currently being shown.
        var prepared = new PreparedMesh();
        prepared.Data = TerrainMeshBuilder.Build(_densities, _size, _voxelSize,
            _globalOrigin, _materialResolver, new TerrainMeshBuilder.Result());
        var data = prepared.Data;
        var mesh = new Mesh { name = "Gobmine prepared terrain", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(data.Vertices);
        mesh.subMeshCount = Mathf.Max(1, data.Triangles.Count);
        int sub = 0;
        World world = World.Instance;
        foreach (var group in data.Triangles)
        {
            mesh.SetTriangles(group.Value, sub++);
            prepared.Materials.Add(world != null ? world.GetMaterialForSurface(group.Key) : null);
            for (int i = 0; i < group.Value.Count; i += 3)
                prepared.TriangleMaterials.Add(group.Key);
        }
        if (prepared.Materials.Count == 0)
            prepared.Materials.Add(world != null ? world.GetVoxelMaterial() : null);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        prepared.Mesh = mesh;
        // Bake while the old collider is still active; assigning the prepared
        // collider during the eventual swap then avoids much of the cook cost.
        if (prepared.TriangleMaterials.Count > 0)
            Physics.BakeMesh(mesh.GetEntityId(), false);
        return prepared;
    }

    public void PublishPreparedMesh(PreparedMesh prepared)
    {
        if (prepared == null || prepared.Mesh == null)
            throw new System.ArgumentException("A staged mesh must be prepared before publishing.");
        var old = _mesh;
        _hasBuiltMesh = false;
        _meshCollider.sharedMesh = null;
        _mesh = prepared.Mesh;
        prepared.Mesh = null; // Ownership passes to this Chunk.
        _meshFilter.sharedMesh = _mesh;
        _triangleMaterials.Clear();
        _triangleMaterials.AddRange(prepared.TriangleMaterials);
        _meshRenderer.SetSharedMaterials(prepared.Materials);
        _meshRenderer.enabled = _triangleMaterials.Count > 0;
        if (_triangleMaterials.Count > 0) _meshCollider.sharedMesh = _mesh;
        _hasBuiltMesh = true;
        World world = World.Instance;
        if (world != null) world.RefreshRadiationSurface(_globalOrigin, prepared.Data);
        prepared.Dispose();
        if (old != null)
        {
            if (Application.isPlaying) Destroy(old);
            else DestroyImmediate(old);
        }
    }

    public void BuildMeshSync()
    {
        _hasBuiltMesh = false;
        if(_densities==null)return;
        World world=World.Instance;
        var data=TerrainMeshBuilder.Build(_densities,_size,_voxelSize,_globalOrigin,_materialResolver,_meshData);
        _meshCollider.sharedMesh=null;_mesh.Clear();_mesh.SetVertices(data.Vertices);_triangleMaterials.Clear();
        _mesh.subMeshCount=Mathf.Max(1,data.Triangles.Count);
        var materials=_materials; materials.Clear();int submesh=0;
        foreach(var group in data.Triangles)
        {
            _mesh.SetTriangles(group.Value,submesh++);
            materials.Add(world!=null?world.GetMaterialForSurface(group.Key):null);
            for(int i=0;i<group.Value.Count;i+=3)_triangleMaterials.Add(group.Key);
        }
        if(materials.Count==0)materials.Add(world!=null?world.GetVoxelMaterial():null);
        _meshRenderer.SetSharedMaterials(materials);_meshRenderer.enabled=_triangleMaterials.Count>0;
        _mesh.RecalculateNormals();_mesh.RecalculateBounds();_meshFilter.sharedMesh=_mesh;
        if(_triangleMaterials.Count>0)_meshCollider.sharedMesh=_mesh;
        _hasBuiltMesh = true;
        if (world != null) world.RefreshRadiationSurface(_globalOrigin, data);
    }
}
