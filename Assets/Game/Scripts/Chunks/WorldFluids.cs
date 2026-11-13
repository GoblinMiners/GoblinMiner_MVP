using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public partial class World
{
    private GobmineFluidStore _fluids;
    private readonly Dictionary<Vector3Int,bool> _fluidOpenCache = new Dictionary<Vector3Int,bool>();
    // Each face is keyed by its LOWER coarse-grid cell and axis. A fluid cell
    // containing a rock edge may hold water, but a complete rock membrane on
    // a shared face must stop liquid from crossing into the next cell.
    private struct FluidFaceKey : IEquatable<FluidFaceKey>
    {
        public Vector3Int Lower;
        public byte Axis;
        public bool Equals(FluidFaceKey other) => Lower == other.Lower && Axis == other.Axis;
        public override bool Equals(object value) => value is FluidFaceKey && Equals((FluidFaceKey)value);
        public override int GetHashCode() { unchecked { return Lower.GetHashCode()*397 ^ Axis; } }
    }
    private readonly Dictionary<FluidFaceKey,bool> _fluidFaceCache = new Dictionary<FluidFaceKey,bool>();
    // Newly excavated fluid cells have priority over ordinary distant discovery.
    // Dedupe thousands of fine terrain edits into a bounded coarse-cell queue.
    private readonly Queue<Vector3Int> _newFluidOpenings = new Queue<Vector3Int>();
    private readonly HashSet<Vector3Int> _queuedFluidOpenings = new HashSet<Vector3Int>();
    public int PendingFluidRefills => _newFluidOpenings.Count;
    public int PendingFluidWork => _fluids == null ? 0 : _fluids.Pending;
    public int PrimedFluidCells { get; private set; }
    /// <summary>Sample the same trilinear terrain field used by marching cubes.
    /// The fluid renderer clips its shoreline to the rock at visual-only resolution.</summary>
    public float SampleFluidTerrainDensity(Vector3 worldPosition)
        => _densityReader == null ? -1f : TerrainQuery.SampleDensity(worldPosition / voxelSize, _densityReader);

    private readonly Dictionary<Vector3Int,GobmineFluidCell> _fluidOutgoing = new Dictionary<Vector3Int,GobmineFluidCell>();
    private readonly List<Vector3Int> _fluidScanOffsets = new List<Vector3Int>();
    private readonly List<Vector3Int> _fluidScanCentres = new List<Vector3Int>();
    private int _fluidDiscoveryCursor;
    private float _nextDiscoveryCycle;
    public int PendingFluidDiscovery => Mathf.Max(0, _fluidScanOffsets.Count*_fluidScanCentres.Count-_fluidDiscoveryCursor);
    private readonly List<Vector3Int> _fluidPlayerRegions = new List<Vector3Int>();
    private readonly List<FluidDelta> _bufferedFluidDeltas = new List<FluidDelta>();
    private GobmineFluidRenderer _fluidRenderer;
    private float _nextFluidRefresh, _nextFluidSend;
    private long _fluidRevision, _lastFluidClientRevision;
    private struct FluidDelta { public long Revision; public GobmineFluidPoint[] Points; }
    public float LastFluidMilliseconds { get; private set; }
    private readonly System.Diagnostics.Stopwatch _fluidWatch = new System.Diagnostics.Stopwatch();
    private static readonly Unity.Profiling.ProfilerMarker FluidMarker = new Unity.Profiling.ProfilerMarker("Gobmine.FluidSimulation");

    private void InitializeExpeditionFluids()
    {
        _fluidOpenCache.Clear(); _fluidFaceCache.Clear(); _fluidScanCentres.Clear(); _fluidDiscoveryCursor=0; _fluidPlayerRegions.Clear();
        _newFluidOpenings.Clear(); _queuedFluidOpenings.Clear(); PrimedFluidCells = 0;
        _fluidScanOffsets.Clear(); _nextDiscoveryCycle=0f;
        if (ExpeditionEnabled)
        {
            int radius=Mathf.CeilToInt(ExpeditionProfile.fluidActiveRadiusMetres/ExpeditionProfile.fluidCellMetres)+1;
            for(int x=-radius;x<=radius;x++)for(int y=-radius;y<=radius;y++)for(int z=-radius;z<=radius;z++)
                if(x*x+y*y+z*z<=radius*radius)_fluidScanOffsets.Add(new Vector3Int(x,y,z));
            _fluidScanOffsets.Sort((a,b)=>a.sqrMagnitude.CompareTo(b.sqrMagnitude));
        }
        if (_fluidRenderer != null) { Destroy(_fluidRenderer.gameObject); _fluidRenderer = null; }
        _fluids = _caverns == null ? null : new GobmineFluidStore(_caverns.BaseFluid,
            IsFluidCellOpen, IsFluidFacePassable);
        if (_fluids != null) _fluids.Changed += FluidChanged;
        // Do not discard buffered live deltas when a late-join baseline begins.
    }
    private void ResetFluidRuntime()
    {
        if (_fluidRenderer != null) Destroy(_fluidRenderer.gameObject);
        _fluidRenderer = null; _fluids = null; _fluidRevision = 0; _lastFluidClientRevision = 0;
        _fluidOpenCache.Clear(); _fluidFaceCache.Clear(); _fluidOutgoing.Clear(); _bufferedFluidDeltas.Clear();
        _newFluidOpenings.Clear(); _queuedFluidOpenings.Clear(); PrimedFluidCells = 0;
        _fluidScanCentres.Clear(); _fluidScanOffsets.Clear(); _fluidDiscoveryCursor=0; _fluidPlayerRegions.Clear();
        _nextFluidRefresh = _nextFluidSend = 0f; LastFluidMilliseconds = 0f;
    }
    public GobmineFluidCell FluidCell(Vector3Int cell) => _fluids == null ? default : _fluids.Get(cell);
    public GobmineFluidKind FluidAt(Vector3 position)
    {
        if (!ExpeditionEnabled || _fluids == null || !IsReady || !IsAirAt(position)) return GobmineFluidKind.None;
        float size = ExpeditionProfile.fluidCellMetres;
        Vector3Int cell = Vector3Int.FloorToInt(position / size); var state = _fluids.Get(cell);
        float surface = (cell.y + (float)state.Amount/GobmineFluidStore.Capacity) * size;
        return position.y < surface && state.Amount > 0 ? state.Kind : GobmineFluidKind.None;
    }
    public GobmineSpaceKind SpaceAt(Vector3 position)
    {
        if (!IsReady || _terrain == null) return GobmineSpaceKind.Unavailable;
        if (!IsAirAt(position)) return GobmineSpaceKind.Solid;
        GobmineFluidKind fluid = FluidAt(position);
        return fluid == GobmineFluidKind.Water ? GobmineSpaceKind.Water : fluid == GobmineFluidKind.Lava ? GobmineSpaceKind.Lava : GobmineSpaceKind.Air;
    }
    public float WaterDepthBelow(Vector3 position)
    {
        if (_fluids == null || !ExpeditionEnabled) return 0f;
        float size = ExpeditionProfile.fluidCellMetres;
        Vector3Int cell = Vector3Int.FloorToInt(position / size); var state = _fluids.Get(cell);
        if (state.Kind != GobmineFluidKind.Water || state.Amount <= 0) return 0f;
        float depth = ((float)state.Amount/GobmineFluidStore.Capacity)*size;
        for (int i = 1; i <= 16; i++)
        {
            var below = _fluids.Get(cell + Vector3Int.down*i);
            if (below.Kind != GobmineFluidKind.Water || below.Amount < GobmineFluidStore.Capacity) break;
            depth += size;
        }
        return depth;
    }
    public bool IsFluidCellOpen(Vector3Int cell)
    {
        if (_terrain == null || !ExpeditionEnabled) return false;
        bool open;
        if (_fluidOpenCache.TryGetValue(cell, out open)) return open;
        int n = Mathf.RoundToInt(ExpeditionProfile.fluidCellMetres / voxelSize);
        Vector3Int start = cell * n;
        int air = 0, required = Mathf.Max(1, Mathf.CeilToInt(n*n*n*0.25f));
        // The old rule rejected a WHOLE 1m fluid cell when even one lattice
        // sample contained stone. This produced permanent square dry pockets
        // after a shape was detached. Accept partly open edge cells instead.
        for(int x=0;x<n && air<required;x++) for(int y=0;y<n && air<required;y++)
        for(int z=0;z<n && air<required;z++)
            if (TerrainQuery.SampleDensity((Vector3)start+new Vector3(x+0.5f,y+0.5f,z+0.5f),
                    _densityReader) >= 0f) air++;
        // Also require a clear centre so an internal rock sheet cannot turn
        // two disconnected cavities into one permeable coarse fluid cell.
        open = air>=required && SampleFluidTerrainDensity(((Vector3)cell+Vector3.one*0.5f)*
            ExpeditionProfile.fluidCellMetres) >= 0f;
        if (_fluidOpenCache.Count>=32768) _fluidOpenCache.Clear();
        _fluidOpenCache[cell]=open;
        return open;
    }

    /// <summary>Prevent the looser edge-cell occupancy rule from allowing fluid
    /// to cross solid walls. Require a contiguous 2x2 fine-cell air passage on
    /// BOTH sides of the shared coarse-cell face.</summary>
    public bool IsFluidFacePassable(Vector3Int from, Vector3Int to)
    {
        Vector3Int delta = to-from;
        int axis = delta.x!=0 ? 0 : delta.y!=0 ? 1 : 2;
        if (Mathf.Abs(delta.x)+Mathf.Abs(delta.y)+Mathf.Abs(delta.z)!=1) return false;
        Vector3Int lower = delta.x+delta.y+delta.z>0 ? from : to;
        var key = new FluidFaceKey{Lower=lower,Axis=(byte)axis};
        bool cached;
        if(_fluidFaceCache.TryGetValue(key,out cached)) return cached;
        float size=ExpeditionProfile.fluidCellMetres;
        int n=Mathf.Max(2,Mathf.RoundToInt(size/voxelSize));
        // Face at +X/+Y/+Z of lower cell. Sample just to either side of it.
        Vector3 baseWorld=(Vector3)lower*size;
        Vector3 normal=axis==0?Vector3.right:axis==1?Vector3.up:Vector3.forward;
        Vector3 face=baseWorld+normal*size;
        float inset=Mathf.Min(voxelSize*0.35f,size*0.18f);
        var samples=new bool[n,n];
        for(int u=0;u<n;u++) for(int v=0;v<n;v++)
        {
            float a=(u+0.5f)*size/n,b=(v+0.5f)*size/n;
            Vector3 position=face+(axis==0?new Vector3(0,a,b):axis==1?
                new Vector3(a,0,b):new Vector3(a,b,0));
            samples[u,v]=SampleFluidTerrainDensity(position-normal*inset)>=0f &&
                SampleFluidTerrainDensity(position+normal*inset)>=0f;
        }
        bool pass=false;
        for(int u=0;u<n-1 && !pass;u++)for(int v=0;v<n-1;v++)
            if(samples[u,v] && samples[u+1,v] && samples[u,v+1] && samples[u+1,v+1])
            {pass=true;break;}
        if (_fluidFaceCache.Count>=32768) _fluidFaceCache.Clear();
        _fluidFaceCache[key]=pass;
        return pass;
    }
    private void NotifyFluidTerrainChange(Vector3Int sample)
    {
        if (_fluids == null || !ExpeditionEnabled) return;
        int n = Mathf.RoundToInt(ExpeditionProfile.fluidCellMetres/voxelSize);
        Vector3Int owner = new Vector3Int(TerrainDensityStore.FloorDiv(sample.x,n), TerrainDensityStore.FloorDiv(sample.y,n), TerrainDensityStore.FloorDiv(sample.z,n));
        for (int x=0;x<(sample.x%n==0?2:1);x++) for (int y=0;y<(sample.y%n==0?2:1);y++) for (int z=0;z<(sample.z%n==0?2:1);z++)
        {
            Vector3Int c=owner-new Vector3Int(x,y,z);
            _fluidOpenCache.Remove(c);
            for (byte axis=0;axis<3;axis++)
            {
                Vector3Int direction=axis==0?Vector3Int.right:axis==1?Vector3Int.up:Vector3Int.forward;
                _fluidFaceCache.Remove(new FluidFaceKey{Lower=c,Axis=axis});
                _fluidFaceCache.Remove(new FluidFaceKey{Lower=c-direction,Axis=axis});
            }
            // Terrain can change with no fluid delta (e.g., carving a shoreline).
            // Keep client visuals coherent with the new actual rock surface.
            if (_fluidRenderer != null) _fluidRenderer.MarkCellDirty(c);
            if (isServer)
            {
                _fluids.WakeAround(c);
                if (_queuedFluidOpenings.Count < 8192 && _queuedFluidOpenings.Add(c))
                    _newFluidOpenings.Enqueue(c);
            }
        }
    }
    private bool FluidActive(Vector3Int cell)
    {
        Vector3 p = ((Vector3)cell+Vector3.one*0.5f)*ExpeditionProfile.fluidCellMetres;
        float r=ExpeditionProfile.fluidActiveRadiusMetres;
        return NearestPlayerDistance(p) <= r*r;
    }
    private void FluidChanged(Vector3Int cell, GobmineFluidCell value)
    {
        if (isServer) _fluidOutgoing[cell]=value;
        if (_fluidRenderer != null) _fluidRenderer.MarkCellDirty(cell);
    }
    private void UpdateFluids()
    {
        if (_fluids == null) return;
        if (NetworkClient.active && NetworkClient.localPlayer != null && _fluidRenderer == null)
        {
            var go = new GameObject("Expedition fluids (visual only)"); go.transform.SetParent(transform, false);
            _fluidRenderer = go.AddComponent<GobmineFluidRenderer>(); _fluidRenderer.Initialize(this);
        }
        if (!isServer) return;
        using (FluidMarker.Auto())
        {
            _fluidWatch.Restart();
            if (Time.unscaledTime >= _nextFluidRefresh)
            {
                RefreshFluidDiscovery(); _nextFluidRefresh = Time.unscaledTime+0.5f;
            }
            int scans = Mathf.Max(1, ExpeditionProfile.fluidDiscoveryCellsPerFrame);
            while (scans-- > 0 && PendingFluidDiscovery > 0)
            {
                if (_fluidWatch.Elapsed.TotalMilliseconds >= ExpeditionProfile.fluidMillisecondsPerFrame) break;
                int player=_fluidDiscoveryCursor%_fluidScanCentres.Count;
                int offset=_fluidDiscoveryCursor/_fluidScanCentres.Count;
                _fluidDiscoveryCursor++;
                Vector3Int c=_fluidScanCentres[player]+_fluidScanOffsets[offset];
                if (FluidActive(c) && _fluids.Get(c).Amount>0) _fluids.Wake(c);
            }
            // Hydrate new openings (including cavities left by detached physics
            // chunks) from existing water/lava BEFORE their normal flow tick.
            // This is finite-volume: no cells or resources are conjured from air.
            int priming = Mathf.Min(128, Mathf.Max(8, ExpeditionProfile.fluidOperationsPerFrame * 2));
            while (priming-- > 0 && _newFluidOpenings.Count > 0)
            {
                if (_fluidWatch.Elapsed.TotalMilliseconds >= ExpeditionProfile.fluidMillisecondsPerFrame) break;
                Vector3Int c = _newFluidOpenings.Dequeue();
                _queuedFluidOpenings.Remove(c);
                if (_fluids.PrimeNewOpening(c, FluidActive)) PrimedFluidCells++;
            }
            int operations = Mathf.Max(1, ExpeditionProfile.fluidOperationsPerFrame);
            while (operations-- > 0 && _fluids.Pending > 0)
            {
                if (operations < ExpeditionProfile.fluidOperationsPerFrame-1 && _fluidWatch.Elapsed.TotalMilliseconds >= ExpeditionProfile.fluidMillisecondsPerFrame) break;
                _fluids.Step(FluidActive);
            }
            LastFluidMilliseconds=(float)_fluidWatch.Elapsed.TotalMilliseconds;
        }
        if (Time.unscaledTime >= _nextFluidSend)
        { FlushFluidDeltas(); _nextFluidSend=Time.unscaledTime+0.1f; }
    }
    private void RefreshFluidDiscovery()
    {
        float size=ExpeditionProfile.fluidCellMetres;
        bool changed=_fluidPlayerRegions.Count!=_connectedPlayers.Count;
        if (!changed)
            for (int i=0;i<_connectedPlayers.Count;i++)
                if (_connectedPlayers[i]==null || _fluidPlayerRegions[i]!=Vector3Int.FloorToInt(_connectedPlayers[i].position/(size*4f))) { changed=true;break; }
        if (!changed && (PendingFluidDiscovery>0 || Time.unscaledTime<_nextDiscoveryCycle)) return;
        _fluidPlayerRegions.Clear();_fluidScanCentres.Clear();_fluidDiscoveryCursor=0;
        _nextDiscoveryCycle=Time.unscaledTime+4f;
        foreach(Transform player in _connectedPlayers)
        {
            if(player==null){_fluidPlayerRegions.Add(Vector3Int.zero);continue;}
            _fluidPlayerRegions.Add(Vector3Int.FloorToInt(player.position/(size*4f)));
            _fluidScanCentres.Add(Vector3Int.FloorToInt(player.position/size));
        }
        // No per-player cubic scan/sort here. The precomputed near-first offsets
        // are consumed incrementally inside the measured discovery allowance.
    }
    private void FlushFluidDeltas()
    {
        if (_fluidOutgoing.Count==0) return;
        // Bound each send and total work. Newer absolute values replace pending
        // values in the dictionary; no unbounded stream of obsolete updates.
        int batches=0;
        while (_fluidOutgoing.Count>0 && batches++<4)
        {
            var points=new List<GobmineFluidPoint>(128);
            foreach (var e in _fluidOutgoing)
            {
                points.Add(new GobmineFluidPoint{Cell=e.Key,Kind=e.Value.Kind,Amount=e.Value.Amount});
                if (points.Count==128) break;
            }
            foreach(var p in points) _fluidOutgoing.Remove(p.Cell);
            RpcFluidDelta(++_fluidRevision,points.ToArray());
        }
    }
    [ClientRpc]
    private void RpcFluidDelta(long revision,GobmineFluidPoint[] points)
    {
        if (isServer) return;
        var delta=new FluidDelta{Revision=revision,Points=points};
        if (!IsReady || _receivingSnapshot || _snapshotRequested || _fluids==null) { _bufferedFluidDeltas.Add(delta); return; }
        if (revision<=_lastFluidClientRevision) return;
        if (revision!=_lastFluidClientRevision+1) { _bufferedFluidDeltas.Add(delta); RequestSnapshot(); return; }
        ApplyFluidDelta(delta);
    }
    private void ApplyFluidDelta(FluidDelta delta)
    {
        if (_fluids!=null)
            foreach(var p in delta.Points)
            { _fluids.Apply(p.Cell,new GobmineFluidCell(p.Kind,p.Amount)); if(_fluidRenderer!=null)_fluidRenderer.MarkCellDirty(p.Cell); }
        _lastFluidClientRevision=delta.Revision;
    }
    [TargetRpc]
    private void TargetFluidSnapshotPart(NetworkConnectionToClient target,GobmineFluidPoint[] points)
    { if(!isServer && _fluids!=null)_fluids.Import(points); }
    private bool FinishFluidSnapshot()
    {
        _bufferedFluidDeltas.Sort((a,b)=>a.Revision.CompareTo(b.Revision));
        foreach(var d in _bufferedFluidDeltas)
        {
            if(d.Revision<=_lastFluidClientRevision)continue;
            if(d.Revision!=_lastFluidClientRevision+1) { _bufferedFluidDeltas.Clear();return false; }
            ApplyFluidDelta(d);
        }
        _bufferedFluidDeltas.Clear();return true;
    }
}
