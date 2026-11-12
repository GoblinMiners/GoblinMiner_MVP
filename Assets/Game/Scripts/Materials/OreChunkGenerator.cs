using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Deterministic candidate ore bodies. This generator never creates GameObjects,
/// edits densities or advances UnityEngine.Random. Discovery is proximity/surface-based.
/// One candidate has one stable ID regardless of chunk loading or query order.
/// </summary>
public sealed class OreChunkGenerator
{
    public struct Candidate
    {
        public long Id;
        public int MaterialId;
        public Vector3Int Grid;
        public Vector3 Centre;
        public float Radius;
    }

    private struct Rule
    {
        public int Material, Grid, Chance, MinimumRadius, MaximumRadius, MinY, MaxY;
        public float Voxel;
        public bool UseDepthBand;
        public float MinimumDepth, MaximumDepth;
    }
    private readonly List<Rule> _rules = new List<Rule>();
    private readonly int _seed;
    private readonly float _voxel;
    private readonly float _surfaceY;
    private readonly float _minimumRadius;
    private readonly float _maximumRadius;

    public bool Enabled => _rules.Count != 0;

    public OreChunkGenerator(int seed, float voxelSize, float surfaceY, TerrainMaterialCatalog catalogue,
        float minimumRadius = 0.28f, float maximumRadius = 0.65f)
    {
        if (catalogue == null || voxelSize <= 0f ||
            float.IsNaN(minimumRadius) || float.IsNaN(maximumRadius) ||
            float.IsInfinity(minimumRadius) || float.IsInfinity(maximumRadius) ||
            minimumRadius <= 0f || maximumRadius < minimumRadius)
            throw new ArgumentException("Invalid ore-chunk generator settings.");
        _seed = seed; _voxel = voxelSize; _surfaceY = surfaceY;
        _minimumRadius = minimumRadius; _maximumRadius = maximumRadius;
        foreach (TerrainMaterialDefinition d in catalogue.definitions)
            if (d != null && d.materialId != catalogue.baseStoneId && d.generateDeposits && d.spawnMineableChunks)
                _rules.Add(new Rule {
                    Material=d.materialId, Grid=d.depositGridSizeInCells, Chance=d.depositChancePerThousand,
                    MinimumRadius=d.minimumRadiusInCells, MaximumRadius=d.maximumRadiusInCells,
                    MinY=d.minimumCellY, MaxY=d.maximumCellY, Voxel=voxelSize,
                    UseDepthBand=d.useDepthBand,MinimumDepth=d.minimumDepthMetres,MaximumDepth=d.maximumDepthMetres
                });
    }

    /// <summary>Near one position, return candidates whose bodies might be exposed; bounded by supplied radius.</summary>
    public void GetNear(Vector3 position, float searchRadius, List<Candidate> output)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        output.Clear();
        searchRadius = Mathf.Max(0.25f, searchRadius);
        foreach (Rule r in _rules)
        {
            float pitch = r.Grid * _voxel;
            float reach = searchRadius + _maximumRadius;
            int lx = Mathf.FloorToInt((position.x - reach) / pitch);
            int ux = Mathf.FloorToInt((position.x + reach) / pitch);
            int ly = Mathf.FloorToInt((position.y - reach) / pitch);
            int uy = Mathf.FloorToInt((position.y + reach) / pitch);
            int lz = Mathf.FloorToInt((position.z - reach) / pitch);
            int uz = Mathf.FloorToInt((position.z + reach) / pitch);
            // Guards against accidentally asking to enumerate an entire world.
            if ((long)(ux-lx+1)*(uy-ly+1)*(uz-lz+1) > 4096) continue;
            for (int x = lx; x <= ux; x++)
            for (int y = ly; y <= uy; y++)
            for (int z = lz; z <= uz; z++)
            {
                Vector3Int grid = new Vector3Int(x,y,z);
                Candidate candidate;
                if (!TryMake(r, grid, out candidate)) continue;
                if ((candidate.Centre-position).sqrMagnitude <=
                    (searchRadius+candidate.Radius)*(searchRadius+candidate.Radius)) output.Add(candidate);
            }
        }
        // Important: discovery uses a bounded frame budget. Spawn the nearest geology
        // first so an old, far-away queue cannot make an outcrop pop in front of a miner.
        output.Sort((a,b) => {
            int order=(a.Centre-position).sqrMagnitude.CompareTo((b.Centre-position).sqrMagnitude);
            return order!=0 ? order : a.Id.CompareTo(b.Id);
        });
    }

    private bool TryMake(Rule r, Vector3Int g, out Candidate candidate)
    {
        candidate = default;
        uint h = Hash(g, _seed, r.Material);
        if (h % 1000 >= r.Chance) return false;
        float pitch = r.Grid * _voxel;
        Vector3 centre = new Vector3(
            (g.x + 0.15f + 0.70f * Next(ref h)) * pitch,
            (g.y + 0.15f + 0.70f * Next(ref h)) * pitch,
            (g.z + 0.15f + 0.70f * Next(ref h)) * pitch);
        int yCell = Mathf.FloorToInt(centre.y / _voxel);
        if (yCell < r.MinY || yCell > r.MaxY) return false;
        if (r.UseDepthBand)
        {
            float depth=Mathf.Max(0f,_surfaceY-centre.y);
            if (depth < r.MinimumDepth || depth > r.MaximumDepth) return false;
        }
        int radiusCells = r.MinimumRadius + Mathf.FloorToInt(Next(ref h) * (r.MaximumRadius-r.MinimumRadius+1));
        candidate = new Candidate {
            Id=StableId(g, r.Material), Grid=g, MaterialId=r.Material,
            // A node is a small boulder, not an entire ore-vein-sized rock.
            Centre=centre, Radius=Mathf.Clamp(radiusCells*_voxel*0.40f,_minimumRadius,_maximumRadius)
        };
        return true;
    }

    // Stable, isolated brush-to-node overlap test. Used BEFORE density removal.
    public static bool IsProtectedWorkshopPosition(Vector3 point, float safeRadius)
    {
        if (safeRadius <= 0f) return false;
        // A compact cylinder around the starting workshop, not the previous
        // 28m-wide square which excluded most shallow copper prospecting.
        float horizontalSquared = point.x*point.x + point.z*point.z;
        return horizontalSquared < safeRadius*safeRadius && point.y > -6f && point.y < 10f;
    }

    public static bool CouldBrushReveal(Candidate c,Vector3 brushCentre,float brushRadius,float voxelSize)
    {
        float reach=Mathf.Max(0f,brushRadius)+c.Radius+Mathf.Max(0f,voxelSize)*1.5f;
        return (c.Centre-brushCentre).sqrMagnitude <= reach*reach;
    }

    public static long StableId(Vector3Int cell, int materialId)
    {
        // 64-bit FNV-1a over signed integers, independent of system endianness.
        ulong h = 14695981039346656037ul;
        Add(ref h, cell.x); Add(ref h, cell.y); Add(ref h, cell.z); Add(ref h, materialId);
        return (long)(h & 0x7ffffffffffffffeul) + 1;
    }
    private static void Add(ref ulong h, int v)
    {
        unchecked { uint u=(uint)v; for(int j=0;j<4;j++) {h^=(byte)(u>>(8*j));h*=1099511628211ul;} }
    }
    private static float Next(ref uint h) {h=Mix(h);return (h & 0xffff)/65536f;}
    private static uint Hash(Vector3Int p,int seed,int id)
    {
        unchecked {return Mix((uint)p.x*0x8da6b343u ^ (uint)p.y*0xd8163841u ^
            (uint)p.z*0xcb1ab31fu ^ (uint)seed ^ (uint)id*0x9e3779b9u);}
    }
    private static uint Mix(uint h)
    {
        unchecked {h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;return h^(h>>16);}
    }
}
