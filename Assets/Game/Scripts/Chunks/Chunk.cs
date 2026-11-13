using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer),typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;
    private Mesh _mesh;
    // Rebuild into this offscreen buffer; never Clear() the visible/collidable mesh.
    private Mesh _standbyMesh;
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
    private readonly List<int> _scratchTriangleMaterials = new List<int>();
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
        if (_mesh != null)
        {
            if (Application.isPlaying) Destroy(_mesh);
            else DestroyImmediate(_mesh);
        }
        if (_standbyMesh != null && _standbyMesh != _mesh)
        {
            if (Application.isPlaying) Destroy(_standbyMesh);
            else DestroyImmediate(_standbyMesh);
        }
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
        internal float[,,] DensitySnapshot;
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

    public PreparedMesh PrepareMeshWithoutPublishing(float[,,] densitySnapshot = null)
    {
        float[,,] source = densitySnapshot ?? _densities;
        if (source == null) throw new System.InvalidOperationException("Cannot prepare an uninitialized chunk.");
        // Separate scratch state: ordinary BuildMeshSync() and another staged
        // build must not mutate a mesh that is currently being shown.
        var prepared = new PreparedMesh();
        prepared.DensitySnapshot = densitySnapshot;
        prepared.Data = TerrainMeshBuilder.Build(source, _size, _voxelSize,
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
            Physics.BakeMesh(mesh.GetInstanceID(), false);
        return prepared;
    }

    public void PublishPreparedMesh(PreparedMesh prepared)
    {
        if (prepared == null || prepared.Mesh == null)
            throw new System.ArgumentException("A staged mesh must be prepared before publishing.");
        Mesh oldVisible = _mesh;
        Mesh oldStandby = _standbyMesh;
        // Publish the exact density snapshot used for the prepared mesh.
        // Never expose newer/older density and geometry as the same chunk view.
        if (prepared.DensitySnapshot != null)
        {
            // Preserve the chunk's reusable density allocation. The caller can
            // recycle the immutable staging buffer after publication.
            float[,,] snapshot = prepared.DensitySnapshot;
            if (_densities != null && !object.ReferenceEquals(_densities, snapshot) &&
                _densities.Length == snapshot.Length &&
                _densities.GetLength(0) == snapshot.GetLength(0) &&
                _densities.GetLength(1) == snapshot.GetLength(1) &&
                _densities.GetLength(2) == snapshot.GetLength(2))
                System.Array.Copy(snapshot, _densities, snapshot.Length);
            else _densities = snapshot;
        }
        Mesh next = prepared.Mesh;
        prepared.Mesh = null; // Transfer ownership to this chunk.
        // Neither the old rendered mesh nor its physics surface is cleared.
        // The prepared mesh becomes both representations in one synchronous call.
        _mesh = next;
        _meshFilter.sharedMesh = next;
        _triangleMaterials.Clear();
        _triangleMaterials.AddRange(prepared.TriangleMaterials);
        _meshRenderer.SetSharedMaterials(prepared.Materials);
        _meshRenderer.enabled = _triangleMaterials.Count > 0;
        _meshCollider.sharedMesh = _triangleMaterials.Count > 0 ? next : null;
        _standbyMesh = oldVisible; // Reuse this offscreen for a later ordinary cut.
        _hasBuiltMesh = true;
        World world = World.Instance;
        if (world != null) world.RefreshRadiationSurface(_globalOrigin, prepared.Data);
        prepared.Dispose();
        if (oldStandby != null && oldStandby != next && oldStandby != oldVisible)
        {
            if (Application.isPlaying) Destroy(oldStandby);
            else DestroyImmediate(oldStandby);
        }
    }

    public void BuildMeshSync()
    {
        if (_densities == null) { _hasBuiltMesh = false; return; }
        World world = World.Instance;
        var data = TerrainMeshBuilder.Build(_densities, _size, _voxelSize,
            _globalOrigin, _materialResolver, _meshData);
        // A MeshFilter/Collider using _mesh must NEVER see it cleared in-place.
        // Build the next complete triangle set in an unassigned Mesh instead.
        Mesh next = _standbyMesh;
        if (next == null)
        {
            next = new Mesh { name = "Gobmine terrain standby", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            next.MarkDynamic();
        }
        next.Clear(false);
        next.SetVertices(data.Vertices);
        next.subMeshCount = Mathf.Max(1, data.Triangles.Count);
        List<Material> materials = _materials;
        materials.Clear();
        var ids = _triangleMaterials;
        // Do not mutate the current triangle mapping until the replacement is ready.
        var newIds = _scratchTriangleMaterials;
        newIds.Clear();
        int submesh = 0;
        foreach (var group in data.Triangles)
        {
            next.SetTriangles(group.Value, submesh++);
            materials.Add(world != null ? world.GetMaterialForSurface(group.Key) : null);
            for (int i = 0; i < group.Value.Count; i += 3) newIds.Add(group.Key);
        }
        if (materials.Count == 0) materials.Add(world != null ? world.GetVoxelMaterial() : null);
        next.RecalculateNormals();
        next.RecalculateBounds();

        // No yields, no empty collider interval, no visible world-space hole.
        Mesh old = _mesh;
        _mesh = next;
        _meshFilter.sharedMesh = next;
        ids.Clear(); ids.AddRange(newIds);
        _meshRenderer.SetSharedMaterials(materials);
        _meshRenderer.enabled = ids.Count > 0;
        _meshCollider.sharedMesh = ids.Count > 0 ? next : null;
        _standbyMesh = old;
        _hasBuiltMesh = true;
        if (world != null) world.RefreshRadiationSurface(_globalOrigin, data);
    }
}
