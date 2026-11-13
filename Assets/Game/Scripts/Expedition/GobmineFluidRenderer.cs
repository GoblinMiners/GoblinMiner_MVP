using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Visual-only liquid skin. The authoritative finite-volume simulation is NOT
/// modified by smoothing. One continuous, interpolated wetness/height field is
/// evaluated across coarse fluid-cell centres, including neighbouring regions.
/// This eliminates exposed cell-side cubes and the old overlapping 0.8m fringes.
/// The resulting triangles are clipped against the terrain signed density field.
/// </summary>
public sealed class GobmineFluidRenderer : MonoBehaviour
{
    private const int RegionCells = 4;
    private const float WetThreshold = 0.34f;
    private const int MaxSubdivisions = 8;
    private const int MaxSteps = RegionCells * MaxSubdivisions;
    private World _world;
    private Vector3Int _lastCentre = new Vector3Int(int.MaxValue, 0, 0);
    private readonly Dictionary<Vector3Int, View> _views = new Dictionary<Vector3Int, View>();
    private readonly HashSet<Vector3Int> _wanted = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> _dirty = new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> _queue = new Queue<Vector3Int>();
    private readonly Queue<View> _pool = new Queue<View>();
    private readonly List<View> _allViews = new List<View>();
    private readonly List<Vector3> _vertices = new List<Vector3>(2048);
    private readonly List<Vector3> _normals = new List<Vector3>(2048);
    private readonly List<Color> _colors = new List<Color>(2048);
    private readonly List<int> _water = new List<int>(2048);
    private readonly List<int> _lava = new List<int>(2048);
    private readonly SurfacePoint[,] _samples = new SurfacePoint[MaxSteps + 1, MaxSteps + 1];
    // Reuse a 6x6 cache. Without this, every fine vertex would query 4
    // procedural fluid cells and their above neighbours over and over.
    private readonly float[,] _sourceWet = new float[RegionCells + 2, RegionCells + 2];
    private readonly float[,] _sourceHeight = new float[RegionCells + 2, RegionCells + 2];
    private Vector3Int _sourceOrigin;
    private float _fallbackSurfaceHeight;
    private readonly SurfacePoint[] _clipA = new SurfacePoint[8];
    private readonly SurfacePoint[] _clipB = new SurfacePoint[8];
    private bool _initialRevealPending = true;
    private float _initialStart;

    private sealed class View { public GameObject Object; public Mesh Mesh; }
    private struct SurfacePoint
    {
        public Vector3 Position;
        public float Wetness;
        public float RockDensity;
    }
    private static Vector3Int Region(Vector3Int cell) => new Vector3Int(
        TerrainDensityStore.FloorDiv(cell.x, RegionCells),
        TerrainDensityStore.FloorDiv(cell.y, RegionCells),
        TerrainDensityStore.FloorDiv(cell.z, RegionCells));

    public void Initialize(World world)
    {
        _world = world;
        _initialRevealPending = true;
        _initialStart = Time.unscaledTime;
    }

    public void MarkCellDirty(Vector3Int cell)
    {
        // The interpolated liquid skin samples adjacent cells at its boundary.
        // A mutation can affect diagonal regions, not just the owning region.
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        for (int z = -1; z <= 1; z++) Mark(Region(cell + new Vector3Int(x, y, z)));
    }
    private void Mark(Vector3Int region)
    {
        if (_wanted.Contains(region) && _dirty.Add(region)) _queue.Enqueue(region);
    }

    private void Update()
    {
        if (_world == null || !_world.IsReady || !_world.ExpeditionEnabled ||
            !NetworkClient.active || NetworkClient.localPlayer == null) return;
        float size = _world.ExpeditionProfile.fluidCellMetres;
        Vector3Int centre = Region(Vector3Int.FloorToInt(NetworkClient.localPlayer.transform.position / size));
        if (centre != _lastCentre)
        {
            _lastCentre = centre;
            Refresh(centre);
        }
        int budget = Mathf.Clamp(_world.ExpeditionProfile.fluidRegionsPerFrame, 1, 16);
        while (budget-- > 0 && _queue.Count > 0)
        {
            Vector3Int region = _queue.Dequeue();
            _dirty.Remove(region);
            if (_wanted.Contains(region)) Build(region);
        }
        // First appearance is a single set of completed region meshes, not
        // dozens of visible, independently filling cubes. Later changes keep
        // the old surfaces visible until each replacement mesh is ready.
        if (_initialRevealPending && (_queue.Count == 0 ||
            Time.unscaledTime - _initialStart > 2.5f))
        {
            _initialRevealPending = false;
            foreach (var item in _views) item.Value.Object.SetActive(true);
        }
    }

    private void Refresh(Vector3Int centre)
    {
        var settings = _world.ExpeditionProfile;
        int radius = Mathf.CeilToInt((settings.fluidActiveRadiusMetres + 2f) /
            (settings.fluidCellMetres * RegionCells));
        var regions = new List<Vector3Int>();
        for (int x = -radius; x <= radius; x++)
        for (int y = -radius; y <= radius; y++)
        for (int z = -radius; z <= radius; z++)
            if (x * x + y * y + z * z <= (radius + 1) * (radius + 1))
                regions.Add(centre + new Vector3Int(x, y, z));
        regions.Sort((a, b) => (a - centre).sqrMagnitude.CompareTo((b - centre).sqrMagnitude));
        if (regions.Count > settings.maxRenderedFluidRegions)
            regions.RemoveRange(settings.maxRenderedFluidRegions, regions.Count - settings.maxRenderedFluidRegions);
        HashSet<Vector3Int> previouslyDirty = new HashSet<Vector3Int>(_dirty);
        _wanted.Clear();
        foreach (Vector3Int region in regions) _wanted.Add(region);
        var remove = new List<Vector3Int>();
        foreach (var entry in _views)
        {
            if (_wanted.Contains(entry.Key)) continue;
            entry.Value.Object.SetActive(false);
            _pool.Enqueue(entry.Value);
            remove.Add(entry.Key);
        }
        foreach (Vector3Int region in remove) _views.Remove(region);
        _queue.Clear(); _dirty.Clear();
        // Keep already prepared views; rebuilding all 160 regions every time
        // the player moves four metres caused the old delayed-pool effect.
        foreach (Vector3Int region in regions)
            if (!_views.ContainsKey(region) || previouslyDirty.Contains(region)) Mark(region);
    }

    private void Build(Vector3Int region)
    {
        _vertices.Clear(); _normals.Clear(); _colors.Clear(); _water.Clear(); _lava.Clear();
        float size = _world.ExpeditionProfile.fluidCellMetres;
        int divisions = Mathf.Clamp(Mathf.RoundToInt(size / Mathf.Max(.01f, _world.voxelSize)), 2, MaxSubdivisions);
        int steps = RegionCells * divisions;
        Vector3Int baseCell = region * RegionCells;
        Vector3 worldOrigin = (Vector3)baseCell * size;
        for (int y = 0; y < RegionCells; y++)
        {
            int layer = baseCell.y + y;
            BuildLayer(GobmineFluidKind.Water, layer, size, divisions, steps, baseCell, worldOrigin, _water);
            BuildLayer(GobmineFluidKind.Lava, layer, size, divisions, steps, baseCell, worldOrigin, _lava);
        }
        View view;
        if (_vertices.Count == 0)
        {
            if (_views.TryGetValue(region, out view))
            {
                view.Object.SetActive(false);
                _pool.Enqueue(view);
                _views.Remove(region);
            }
            return;
        }
        if (!_views.TryGetValue(region, out view))
        {
            view = _pool.Count > 0 ? _pool.Dequeue() : NewView();
            _views[region] = view;
        }
        // Mesh.Clear operates on this region's mesh only, with no yield in the
        // middle of this method. No old surface is erased while its update waits.
        view.Object.transform.position = worldOrigin;
        view.Mesh.Clear(false);
        view.Mesh.SetVertices(_vertices);
        view.Mesh.SetNormals(_normals);
        view.Mesh.SetColors(_colors);
        view.Mesh.subMeshCount = 2;
        view.Mesh.SetTriangles(_water, 0);
        view.Mesh.SetTriangles(_lava, 1);
        view.Mesh.RecalculateBounds();
        view.Object.SetActive(!_initialRevealPending);
    }

    private bool PrepareSources(GobmineFluidKind kind, int y, Vector3Int origin, float size)
    {
        bool any = false;
        float heightSum = 0f, wetCount = 0f;
        for (int x = -1; x <= RegionCells; x++)
        for (int z = -1; z <= RegionCells; z++)
        {
            Vector3Int cell = new Vector3Int(origin.x + x, y, origin.z + z);
            GobmineFluidCell fluid = _world.FluidCell(cell);
            float wet = 0f;
            if (fluid.Kind == kind && fluid.Amount > 0 &&
                _world.FluidCell(cell + Vector3Int.up).Kind != kind)
            {
                wet = Mathf.Clamp01(fluid.Amount / 180f);
                any = true;
                heightSum += wet * (y + (float)fluid.Amount / GobmineFluidStore.Capacity) * size;
                wetCount += wet;
            }
            _sourceWet[x + 1, z + 1] = wet;
            _sourceHeight[x + 1, z + 1] = (y + (float)fluid.Amount / GobmineFluidStore.Capacity) * size;
        }
        _fallbackSurfaceHeight = wetCount > 0.0001f ? heightSum / wetCount : (y + .5f) * size;
        return any;
    }

    private void BuildLayer(GobmineFluidKind kind, int layer, float size, int divisions,
        int steps, Vector3Int baseCell, Vector3 worldOrigin, List<int> indices)
    {
        _sourceOrigin = baseCell;
        if (!PrepareSources(kind, layer, baseCell, size)) return;
        for (int x = 0; x <= steps; x++)
        for (int z = 0; z <= steps; z++)
        {
            float u = (float)x / divisions, v = (float)z / divisions;
            _samples[x, z] = Sample(baseCell.x + u, baseCell.z + v, worldOrigin, size);
        }
        for (int x = 0; x < steps; x++)
        for (int z = 0; z < steps; z++)
        {
            SurfacePoint a = _samples[x, z], b = _samples[x + 1, z];
            SurfacePoint c = _samples[x + 1, z + 1], d = _samples[x, z + 1];
            if (a.Wetness < WetThreshold && b.Wetness < WetThreshold &&
                c.Wetness < WetThreshold && d.Wetness < WetThreshold) continue;
            ClipTriangle(a, c, b, indices);
            ClipTriangle(a, d, c, indices);
        }
    }

    private SurfacePoint Sample(float x, float z, Vector3 origin, float size)
    {
        float fx = x - .5f, fz = z - .5f;
        int cx = Mathf.FloorToInt(fx), cz = Mathf.FloorToInt(fz);
        float tx = fx - cx, tz = fz - cz;
        float w00 = (1f - tx) * (1f - tz), w10 = tx * (1f - tz);
        float w01 = (1f - tx) * tz, w11 = tx * tz;
        float coverage = 0f, heightSum = 0f;
        AddSource(cx, cz, w00, ref coverage, ref heightSum);
        AddSource(cx + 1, cz, w10, ref coverage, ref heightSum);
        AddSource(cx, cz + 1, w01, ref coverage, ref heightSum);
        AddSource(cx + 1, cz + 1, w11, ref coverage, ref heightSum);
        float height = coverage > 0.0001f ? heightSum / coverage : _fallbackSurfaceHeight;
        Vector3 world = new Vector3(x * size, height, z * size);
        return new SurfacePoint
        {
            Position = world - origin,
            Wetness = coverage,
            RockDensity = coverage > 0.0001f ?
                _world.SampleFluidTerrainDensity(world - Vector3.up * 0.015f) : -1f
        };
    }

    private void AddSource(int x, int z, float weight, ref float coverage, ref float height)
    {
        if (weight <= 0f) return;
        // Source coordinates are global, but the buffer uses indices relative
        // to the current region. _sourceOrigin changes for every BuildLayer().
        int ix = x - _sourceOrigin.x + 1;
        int iz = z - _sourceOrigin.z + 1;
        if (ix < 0 || iz < 0 || ix >= RegionCells + 2 || iz >= RegionCells + 2) return;
        float factor = weight * _sourceWet[ix, iz];
        coverage += factor;
        height += factor * _sourceHeight[ix, iz];
    }

    private void ClipTriangle(SurfacePoint a, SurfacePoint b, SurfacePoint c, List<int> indices)
    {
        _clipA[0] = a; _clipA[1] = b; _clipA[2] = c;
        int n = Clip(_clipA, 3, _clipB, true);
        if (n < 3) return;
        n = Clip(_clipB, n, _clipA, false);
        if (n < 3) return;
        for (int i = 1; i + 1 < n; i++) Emit(_clipA[0], _clipA[i], _clipA[i + 1], indices);
    }
    private static float Field(SurfacePoint p, bool wet) => wet ? p.Wetness - WetThreshold : p.RockDensity;
    private static SurfacePoint Interpolate(SurfacePoint a, SurfacePoint b, float t)
    {
        t = Mathf.Clamp01(t);
        return new SurfacePoint
        {
            Position = Vector3.LerpUnclamped(a.Position, b.Position, t),
            Wetness = Mathf.LerpUnclamped(a.Wetness, b.Wetness, t),
            RockDensity = Mathf.LerpUnclamped(a.RockDensity, b.RockDensity, t)
        };
    }
    private static int Clip(SurfacePoint[] input, int count, SurfacePoint[] output, bool wet)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            SurfacePoint a = input[i], b = input[(i + 1) % count];
            float va = Field(a, wet), vb = Field(b, wet);
            bool aInside = va >= 0f, bInside = vb >= 0f;
            if (aInside != bInside)
                output[result++] = Interpolate(a, b, va / (va - vb));
            if (bInside) output[result++] = b;
        }
        return result;
    }
    private void Emit(SurfacePoint a, SurfacePoint b, SurfacePoint c, List<int> indices)
    {
        if (Vector3.Cross(b.Position - a.Position, c.Position - a.Position).sqrMagnitude < 1e-10f)
            return;
        int first = _vertices.Count;
        Add(a); Add(b); Add(c);
        indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
    }
    private void Add(SurfacePoint point)
    {
        _vertices.Add(point.Position);
        _normals.Add(Vector3.up);
        float shore = Mathf.Clamp01(1f - Mathf.Abs(point.RockDensity) / 0.7f);
        _colors.Add(new Color(Mathf.Clamp01(point.Wetness), shore, 0f, 1f));
    }
    private View NewView()
    {
        var go = new GameObject("Smooth fluid region");
        go.transform.SetParent(transform, false);
        var mesh = new Mesh { name = "Gobmine continuous fluid skin", indexFormat = IndexFormat.UInt32 };
        mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = new[] { _world.ExpeditionProfile.waterMaterial, _world.ExpeditionProfile.lavaMaterial };
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        var view = new View { Object = go, Mesh = mesh };
        _allViews.Add(view);
        return view;
    }
    private void OnDestroy()
    {
        foreach (View view in _allViews)
            if (view.Mesh != null) Destroy(view.Mesh);
    }
}
