using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Server-authoritative, separate mineable ore bodies exposed as players uncover walls.
/// Geometry is independent from the marching-cubes terrain; no per-voxel Rigidbodies.
/// Non-networked visual proxies are synchronized by this already-spawned World.
/// </summary>
public partial class World
{
    [Header("Mineable Ore Chunks")]
    [Tooltip("Ore definitions with 'Spawn Mineable Chunks' use separate bodies instead of colored terrain veins. Requires a NEW material save when first enabled.")]
    [Min(1)] public int maxActiveOreChunks = 64;
    [Min(4f)] public float oreChunkStreamDistance = 36f;
    [UnityEngine.Range(1,32)] public int oreChunkDiscoveryChecksPerFrame = 12;
    [Header("Ore Size and Early Reveal")]
    [Tooltip("Smallest generated ore boulder radius, in metres (not diameter).")]
    [Range(0.15f,0.9f)] public float oreChunkMinimumRadius = 0.28f;
    [Tooltip("Largest generated/visible ore boulder radius, in metres. Also visually caps older saved oversized nodes, without changing their saved resource quantity.")]
    [Range(0.3f,1.5f)] public float oreChunkMaximumRadius = 0.65f;
    [Tooltip("How far around a player to prospect for ore. Hidden deposits stay virtual until a mining brush reaches them.")]
    [Range(6f,40f)] public float oreChunkPreloadDistance = 12f;
    [Tooltip("Priority checks immediately before each accepted mining brush; keeps the ore visible as the terrain opens.")]
    [Range(1,64)] public int oreChunkImmediateMiningChecks = 32;
    [Tooltip("Protect a small circular workshop area from geological ore nodes. The old 14m-square rule excluded too much shallow mining. 0 disables this exclusion.")]
    [Range(0f,14f)] public float oreChunkWorkshopSafeRadiusMetres = 4f;
    [Tooltip("Rock-backed ore bodies detach after their anchor is mined and enough space exists to fall.")]
    public bool oreChunksDetachWhenUndermined = true;

    private OreChunkGenerator _oreGenerator;
    private readonly Dictionary<long,MineableOreRecord> _oreRecords = new Dictionary<long,MineableOreRecord>();
    private readonly Dictionary<long,MineableOreChunk> _oreViews = new Dictionary<long,MineableOreChunk>();
    private readonly Queue<OreChunkGenerator.Candidate> _oreDiscovery = new Queue<OreChunkGenerator.Candidate>();
    private readonly HashSet<long> _oreQueued = new HashSet<long>();
    // Underground candidates remain virtual until they are exposed or the player's
    // mining brush is about to uncover them. They consume no renderer/collider budget.
    private readonly HashSet<long> _oreDormant = new HashSet<long>();
    // Excavated air cannot become virgin host rock in the current removal-only terrain.
    private readonly HashSet<long> _oreRejected = new HashSet<long>();
    private struct OreImpactKey : IEquatable<OreImpactKey>
    {
        public long Ore; public uint Player;
        public bool Equals(OreImpactKey other) => Ore==other.Ore && Player==other.Player;
        public override bool Equals(object obj) => obj is OreImpactKey other && Equals(other);
        public override int GetHashCode() => unchecked(Ore.GetHashCode()*397 ^ (int)Player);
    }
    private readonly Dictionary<OreImpactKey,float> _oreImpactCooldown=new Dictionary<OreImpactKey,float>();
    private readonly List<OreChunkGenerator.Candidate> _oreDiscoveryScratch = new List<OreChunkGenerator.Candidate>();
    private readonly List<long> _oreIDsScratch = new List<long>();
    private float _nextOreDiscoveryTick;
    private float _nextOrePoseTick;
    private int _oreViewTick;
    private int _orePrewarmViews;
    private int _orePassiveViews;

    public int ActiveMineableOreChunks => _oreViews.Count;
    public int SavedOreChunkStates => _oreRecords.Count;

    private void InitializeOreChunkGenerator()
    {
        float small=Mathf.Clamp(oreChunkMinimumRadius,0.15f,1.5f);
        float large=Mathf.Max(small,Mathf.Clamp(oreChunkMaximumRadius,0.15f,1.5f));
        _oreGenerator = new OreChunkGenerator(seed,voxelSize,
            ExpeditionEnabled ? ExpeditionProfile.surfaceReferenceY : 0f,materialCatalog,small,large);
    }

    private void ResetOreChunks()
    {
        foreach (MineableOreChunk ore in _oreViews.Values)
            if (ore != null) Destroy(ore.gameObject);
        _oreViews.Clear();_oreRecords.Clear();_oreDiscovery.Clear();
        _oreQueued.Clear();_oreDormant.Clear();_oreRejected.Clear();
        _oreDiscoveryScratch.Clear();_oreIDsScratch.Clear();_oreImpactCooldown.Clear();_oreGenerator=null;
        _nextOreDiscoveryTick=0f;_nextOrePoseTick=0f;_oreViewTick=0;
        _orePrewarmViews=0;_orePassiveViews=0;
    }

    private bool IsOreNearPlayer(Vector3 point, float radius)
    {
        float r2=radius*radius;
        foreach (Transform player in _connectedPlayers)
            if (player != null && (player.position-point).sqrMagnitude <= r2) return true;
        return false;
    }

    private void QueueOreDiscovery(Vector3 position,float radius)
    {
        if (!isServer || _oreGenerator==null || !_oreGenerator.Enabled || _oreQueued.Count>=192) return;
        _oreGenerator.GetNear(position,Mathf.Clamp(radius,1f,20f),_oreDiscoveryScratch);
        // GetNear is nearest-first. A distant background queue must never delay ore
        // newly exposed by the next mining action.
        foreach (OreChunkGenerator.Candidate c in _oreDiscoveryScratch)
        {
            // IMPORTANT: re-queue dormant geology. Mining may have exposed it
            // since the last scan. The bounded discovery budget prevents spikes.
            if (_oreRecords.ContainsKey(c.Id) ||
                _oreRejected.Contains(c.Id) || !_oreQueued.Add(c.Id)) continue;
            _oreDiscovery.Enqueue(c);
            if (_oreQueued.Count>=192) break;
        }
    }

    private void ProcessOreDiscovery(int limit)
    {
        int completed=0;
        while (completed<limit && _oreDiscovery.Count>0)
        {
            OreChunkGenerator.Candidate c=_oreDiscovery.Dequeue();
            if (!_oreQueued.Remove(c.Id)) continue; // Already discovered by a mining brush.
            DiscoverOreCandidate(c,false);
            completed++;
        }
    }

    // This runs synchronously before the FIRST density edit of a mining brush.
    // Even if background discovery is behind, nearby ore is created/replicated
    // before rock is removed, not placed on the freshly exposed air surface later.
    private void PrewarmOreForMining(Vector3 point,float radius)
    {
        if (!isServer || _oreGenerator==null || !_oreGenerator.Enabled) return;
        float reach=Mathf.Clamp(radius+oreChunkMaximumRadius+voxelSize*1.5f,1.5f,8f);
        _oreGenerator.GetNear(point,reach,_oreDiscoveryScratch);
        int processed=0;
        foreach (OreChunkGenerator.Candidate c in _oreDiscoveryScratch)
        {
            if (!OreChunkGenerator.CouldBrushReveal(c,point,radius,voxelSize)) continue;
            _oreQueued.Remove(c.Id); // A stale background queue entry is harmless.
            MineableOreRecord record;
            if (!_oreRecords.TryGetValue(c.Id,out record)) record=DiscoverOreCandidate(c,true);
            if (record!=null) TryStreamOre(record,true);
            if (++processed>=Mathf.Clamp(oreChunkImmediateMiningChecks,1,64)) break;
        }
    }

    // Called when a direct mining brush finishes, after terrain density has changed.
    // The pre-brush hook catches nodes BEFORE rock disappears; this second pass
    // catches previously hidden candidates that have just become exposed.
    private void RefreshOreAroundCompletedCut(Vector3 point, float radius)
    {
        if (!isServer || _oreGenerator == null || !_oreGenerator.Enabled) return;
        float reach = Mathf.Clamp(radius + oreChunkMaximumRadius + voxelSize * 1.5f, 1.5f, 8f);
        _oreGenerator.GetNear(point, reach, _oreDiscoveryScratch);
        int remaining = Mathf.Clamp(oreChunkImmediateMiningChecks, 1, 64);
        foreach (OreChunkGenerator.Candidate c in _oreDiscoveryScratch)
        {
            if (remaining <= 0) break;
            if (!OreChunkGenerator.CouldBrushReveal(c, point, radius, voxelSize)) continue;
            MineableOreRecord record;
            if (_oreRecords.TryGetValue(c.Id, out record))
            {
                TryStreamOre(record, true);
                --remaining;
                continue;
            }
            if (_oreRejected.Contains(c.Id) ||
                OreChunkGenerator.IsProtectedWorkshopPosition(c.Centre, oreChunkWorkshopSafeRadiusMetres) ||
                !HasOreExposure(c.Centre, c.Radius)) continue;
            // Still requires intact host rock. Never respawn mined-out ore.
            _oreQueued.Remove(c.Id);
            DiscoverOreCandidate(c, true);
            --remaining;
        }
    }

    private static readonly Vector3Int[] OreDirections = {
        Vector3Int.right,Vector3Int.left,Vector3Int.up,Vector3Int.down,
        Vector3Int.forward,Vector3Int.back
    };

    // Sample surrounding host-rock densities, not a renderer/collider raycast.
    // If a part of the nodule reaches air, it can be seen from an existing cave
    // or excavation. Otherwise it stays a virtual geological candidate.
    private static readonly Vector3Int[] OreExposureDirections = {
        Vector3Int.right,Vector3Int.left,Vector3Int.up,Vector3Int.down,
        Vector3Int.forward,Vector3Int.back,
        new Vector3Int(1,1,1),new Vector3Int(1,1,-1),
        new Vector3Int(1,-1,1),new Vector3Int(1,-1,-1),
        new Vector3Int(-1,1,1),new Vector3Int(-1,1,-1),
        new Vector3Int(-1,-1,1),new Vector3Int(-1,-1,-1)
    };

    private bool HasOreExposure(Vector3 position,float radius)
    {
        if (_terrain==null) return false;
        float distance=Mathf.Max(voxelSize,radius*1.12f+voxelSize*0.5f);
        foreach (Vector3Int d in OreExposureDirections)
        {
            Vector3 sample=position+((Vector3)d).normalized*distance;
            if (_terrain.Get(Vector3Int.RoundToInt(sample/voxelSize))>=0f) return true;
        }
        return false;
    }

    /// <summary>Never shift a nodule to whichever excavated surface was found last.</summary>
    private bool TryLocateEmbeddedOre(OreChunkGenerator.Candidate c,out Vector3Int anchor,out Vector3 placement)
    {
        anchor=Vector3Int.RoundToInt(c.Centre/voxelSize);
        placement=c.Centre;
        return _terrain!=null && _terrain.Get(anchor)<0f;
    }

    private bool OreHasRoomToFall(MineableOreRecord record)
    {
        if (_terrain.Get(record.AnchorSample)<0f) return false;
        int step=Mathf.Max(1,Mathf.CeilToInt(Mathf.Min(record.Radius,oreChunkMaximumRadius)/voxelSize));
        int open=0;
        foreach (Vector3Int dir in OreDirections)
            if (_terrain.Get(record.AnchorSample+dir*step)>=0f) open++;
        return open>=3;
    }

    private MineableOreRecord DiscoverOreCandidate(OreChunkGenerator.Candidate c,bool immediate)
    {
        MineableOreRecord prior;
        if (_oreRecords.TryGetValue(c.Id,out prior)) return prior;
        if (_oreRejected.Contains(c.Id) || !IsOreNearPlayer(c.Centre,oreChunkStreamDistance)) return null;
        // Keep the starting workshop safe from random ore colliders.
        if (OreChunkGenerator.IsProtectedWorkshopPosition(c.Centre, oreChunkWorkshopSafeRadiusMetres))
        { return null; } // Don't permanently blacklist nodes when the radius is edited.
        Vector3Int anchor; Vector3 centre;
        if (!TryLocateEmbeddedOre(c,out anchor,out centre))
        { _oreRejected.Add(c.Id); return null; }
        if (!immediate && !HasOreExposure(centre,c.Radius))
        { _oreDormant.Add(c.Id); return null; }
        TerrainMaterialDefinition definition=materialCatalog.Get(c.MaterialId);
        if (definition==null || definition.inventoryItem==null || definition.itemsPerCubicMetre<=0d)
        { _oreRejected.Add(c.Id); return null; }
        double volume=(4d/3d)*Math.PI*c.Radius*c.Radius*c.Radius*0.70d;
        double units=volume*definition.itemsPerCubicMetre;
        if (units<=0d || double.IsInfinity(units)) return null;
        var record=new MineableOreRecord { Id=c.Id,GeneratorCell=c.Grid,MaterialId=c.MaterialId,
            AnchorSample=anchor,Position=centre,Radius=c.Radius,TotalUnits=units,RemainingUnits=units };
        _oreDormant.Remove(c.Id);
        _oreRecords.Add(record.Id,record);
        TryStreamOre(record,immediate);
        return record;
    }

    private MineableOreChunk CreateOreView(MineableOreRecord state,bool server)
    {
        if (_oreViews.ContainsKey(state.Id) || state.RemainingUnits<=0) return null;
        var go=new GameObject("MineableOre_"+state.Id);
        var view=go.AddComponent<MineableOreChunk>();
        view.Init(this,state,server);
        _oreViews.Add(state.Id,view);
        return view;
    }

    private void TryStreamOre(MineableOreRecord record,bool priority=false)
    {
        if (record.RemainingUnits<=0d || _oreViews.ContainsKey(record.Id) ||
            !IsOreNearPlayer(record.Position,oreChunkStreamDistance)) return;
        // Background scans never put unexposed rocks into the physics scene.
        // Immediate mining prewarm deliberately creates the proxy while enclosed.
        if (!priority && !record.Detached && !HasOreExposure(record.Position,
            Mathf.Min(record.Radius,oreChunkMaximumRadius))) return;
        int cap=Mathf.Max(1,maxActiveOreChunks);
        if (_oreViews.Count>=cap)
        {
            if (!priority) return;
            // A newly exposed ore must not wait indefinitely behind distant ore.
            // Preserve detached physics bodies when choosing a view to stream out.
            long evict=0; float farthest=float.NegativeInfinity;
            foreach (MineableOreChunk view in _oreViews.Values)
            {
                if (view==null || view.Detached) continue;
                float distance=(view.transform.position-record.Position).sqrMagnitude;
                if (distance>farthest){farthest=distance;evict=view.OreId;}
            }
            if (evict==0) return;
            RemoveOreView(evict,true);
        }
        CreateOreView(record,true);
        if (priority) _orePrewarmViews++; else _orePassiveViews++;
        RpcOreState(record.Copy());
    }

    private void RemoveOreView(long id,bool broadcast)
    {
        MineableOreChunk view;
        if (_oreViews.TryGetValue(id,out view))
        {
            _oreViews.Remove(id);
            if (view!=null) Destroy(view.gameObject);
        }
        if (broadcast) RpcRemoveOreView(id);
    }

    private void UpdateOreChunks()
    {
        if (!isServer || !IsReady || _oreGenerator==null) return;
        if (Time.unscaledTime>=_nextOreDiscoveryTick)
        {
            _nextOreDiscoveryTick=Time.unscaledTime+0.40f;
            foreach (Transform player in _connectedPlayers)
                if(player!=null)QueueOreDiscovery(player.position,
                    Mathf.Min(oreChunkPreloadDistance,oreChunkStreamDistance*0.8f));
        }
        // The search budget bounds surface scans; never iterate all voxel cells every frame.
        ProcessOreDiscovery(Mathf.Clamp(oreChunkDiscoveryChecksPerFrame,1,32));
        // Spread active-object inspection over frames when many records are saved.
        if ((_oreViewTick++ & 3)==0)
        {
            _oreIDsScratch.Clear();
            foreach (long id in _oreViews.Keys) _oreIDsScratch.Add(id);
            foreach (long id in _oreIDsScratch)
            {
                MineableOreRecord record;
                if (!_oreRecords.TryGetValue(id,out record))continue;
                MineableOreChunk view;
                if (!_oreViews.TryGetValue(id,out view) || view==null) continue;
                if (record.Detached)
                {
                    record.Position=view.transform.position;record.Rotation=view.transform.rotation;
                    record.Velocity=view.Velocity;record.AngularVelocity=view.Body.angularVelocity;
                }
                else if (oreChunksDetachWhenUndermined && OreHasRoomToFall(record))
                {
                    record.Detached=true;
                    record.Velocity=Vector3.down*0.15f;
                    view.ApplyState(record,false);
                    RpcOreState(record.Copy());
                }
                if (!IsOreNearPlayer(record.Position,oreChunkStreamDistance*1.35f))RemoveOreView(id,true);
            }
            // Stream previously exposed/saved bodies again when someone approaches.
            foreach (MineableOreRecord record in _oreRecords.Values)
                if (_oreViews.Count<Mathf.Max(1,maxActiveOreChunks))TryStreamOre(record);
        }
        if (Time.unscaledTime>=_nextOrePoseTick)
        {
            _nextOrePoseTick=Time.unscaledTime+0.15f;
            var ids=new List<long>();var positions=new List<Vector3>();var rotations=new List<Quaternion>();
            foreach (MineableOreChunk view in _oreViews.Values)
            {
                if (view==null || !view.Detached)continue;
                ids.Add(view.OreId);positions.Add(view.transform.position);rotations.Add(view.transform.rotation);
            }
            if (ids.Count>0)RpcOrePoses(ids.ToArray(),positions.ToArray(),rotations.ToArray());
        }
    }

    [Server]
    public bool ServerMineOre(PlayerInventory miner, MineableOreChunk target, float amount)
    {
        if (!isServer || !CanMine || miner==null || !miner.isServer || target==null ||
            target.OreId<=0 || amount<=0f || float.IsNaN(amount) || float.IsInfinity(amount))return false;
        MineableOreRecord record;
        if (!_oreRecords.TryGetValue(target.OreId,out record) || record.RemainingUnits<=0d ||
            !_oreViews.ContainsKey(record.Id) || !IsOreNearPlayer(record.Position,oreChunkStreamDistance))return false;
        TerrainMaterialDefinition ore=materialCatalog.Get(record.MaterialId);
        if (ore==null || ore.inventoryItem==null)return false;
        string reason;
        if (!CanCollectExpeditionMaterial(miner,record.MaterialId,out reason))
        { NotifyEquipmentMessage(miner,reason);return false; }
        // This yields proportional resources per hit, with no per-click integer rounding.
        double extracted=Math.Min(record.RemainingUnits,
            Math.Max(0.001d,amount/Math.Max(0.05f,ore.hardness)*ore.itemsPerCubicMetre*0.2d));
        if (!miner.ServerAddUnits(ore.inventoryItem.mineralID,extracted))return false;
        NotifyMinedExposure(miner,record.MaterialId,extracted);
        if (record.Detached)
        { record.Position=target.transform.position;record.Rotation=target.transform.rotation; }
        record.RemainingUnits=Math.Max(0d,record.RemainingUnits-extracted);
        if (record.RemainingUnits<=0.000001d)
        {
            record.RemainingUnits=0d;
            RemoveOreView(record.Id,true);
        }
        else
        {
            target.ApplyState(record,false);
            RpcOreState(record.Copy());
        }
        return true;
    }

    /// <summary>Approximate contact test for real moving ore bodies, including CharacterControllers.</summary>
    public void ServerTickOreImpact(MineableOreChunk ore,Vector3 velocity)
    {
        if (!isServer || !collapseDamagesPlayers || ore==null || ore.Body==null || ore.Body.isKinematic)return;
        float speed=velocity.magnitude;
        if (speed<minimumShapeImpactSpeed)return;
        // Broken-off ore is much smaller than an entire collapsing ceiling.
        float volume=(4f/3f)*Mathf.PI*ore.Radius*ore.Radius*ore.Radius*
            (float)(ore.TotalUnits>0d?Math.Max(0d,ore.RemainingUnits/ore.TotalUnits):0d);
        foreach (Transform player in _connectedPlayers)
        {
            if (player==null || (player.position-ore.transform.position).sqrMagnitude>25f)continue;
            GobminePlayerHealth health=player.GetComponent<GobminePlayerHealth>();
            if (health==null || health.IsDead || health.netIdentity==null)continue;
            Collider body=player.GetComponent<Collider>();
            if (body==null || !body.enabled)continue;
            OreImpactKey key=new OreImpactKey {Ore=ore.OreId,Player=health.netIdentity.netId};
            float next;
            if (_oreImpactCooldown.TryGetValue(key,out next) && Time.time<next)continue;
            Bounds sweep=ore.GetComponent<Collider>().bounds;
            sweep.Encapsulate(new Bounds(sweep.center-velocity*Time.fixedDeltaTime,sweep.size));
            if (!sweep.Intersects(body.bounds))continue;
            float damage=TerrainShapeImpactRules.Damage(volume,speed,minimumShapeImpactSpeed,
                damagePerShapeSizeAndSpeed,maximumShapeImpactDamage);
            if (!health.ServerApplyCollapseDamage(damage,shapeImpactCooldown))continue;
            _oreImpactCooldown[key]=Time.time+shapeImpactCooldown;
            var movement=player.GetComponent<PlayerMovement>();
            if (movement!=null && movement.connectionToClient!=null)
            {
                Vector3 direction=player.position-ore.transform.position;direction.y=0f;
                if (direction.sqrMagnitude<0.001f)direction=Vector3.forward;
                direction.Normalize();
                float force=TerrainShapeImpactRules.Knockback(volume,speed,minimumShapeImpactSpeed,
                    knockbackPerShapeSizeAndSpeed,maximumShapeKnockbackSpeed);
                movement.TargetReceiveChunkKnockback(movement.connectionToClient,
                    direction*force+Vector3.up*Mathf.Min(force*0.32f,4f));
            }
        }
    }

    private List<MineableOreRecord> SnapshotOreChunksForSave()
    {
        var result=new List<MineableOreRecord>(_oreRecords.Count);
        foreach (MineableOreRecord r in _oreRecords.Values)
        {
            MineableOreChunk view;
            if (r.Detached && _oreViews.TryGetValue(r.Id,out view) && view!=null)
            {
                r.Position=view.transform.position;r.Rotation=view.transform.rotation;
                r.Velocity=view.Velocity;r.AngularVelocity=view.Body.angularVelocity;
            }
            result.Add(r.Copy());
        }
        return result;
    }
    private void LoadOreChunkRecords(List<MineableOreRecord> records)
    {
        if(records==null)return;
        foreach(MineableOreRecord r in records)
        {
            if (materialCatalog.Get(r.MaterialId)==null ||
                !materialCatalog.Get(r.MaterialId).spawnMineableChunks ||
                OreChunkGenerator.StableId(r.GeneratorCell,r.MaterialId)!=r.Id ||
                r.TotalUnits<=0d || r.RemainingUnits<0d || r.RemainingUnits>r.TotalUnits ||
                r.Radius<=0f || r.Radius>2.25f || _oreRecords.ContainsKey(r.Id))
                throw new InvalidOperationException("Invalid or duplicate saved ore-chunk record.");
            _oreRecords.Add(r.Id,r.Copy());
        }
    }

    [ClientRpc]
    private void RpcOreState(MineableOreRecord record)
    {
        if (isServer) return;
        ApplyClientOreState(record);
    }
    [TargetRpc]
    private void TargetOreState(NetworkConnectionToClient target,MineableOreRecord record)
    {
        if (isServer) return;
        ApplyClientOreState(record);
    }
    private void ApplyClientOreState(MineableOreRecord record)
    {
        if (record==null || record.Id<=0 || materialCatalog==null || materialCatalog.Get(record.MaterialId)==null)return;
        if (record.RemainingUnits<=0d){RemoveOreView(record.Id,false);return;}
        MineableOreChunk view;
        if (!_oreViews.TryGetValue(record.Id,out view) || view==null)
            view=CreateOreView(record,false);
        else view.ApplyState(record,true);
    }
    [ClientRpc]
    private void RpcRemoveOreView(long id)
    {
        if (!isServer)RemoveOreView(id,false);
    }
    [ClientRpc(channel=Channels.Unreliable)]
    private void RpcOrePoses(long[] ids,Vector3[] positions,Quaternion[] rotations)
    {
        if (isServer || ids==null || positions==null || rotations==null || ids.Length!=positions.Length || ids.Length!=rotations.Length)return;
        for (int i=0;i<ids.Length;i++)
        {
            MineableOreChunk view;
            if (_oreViews.TryGetValue(ids[i],out view) && view!=null)view.ApplyPose(positions[i],rotations[i]);
        }
    }
    private IEnumerator SendOreChunksForLateJoin(NetworkConnectionToClient target)
    {
        int count=0;
        foreach (MineableOreChunk view in _oreViews.Values)
        {
            if (target==null || !NetworkServer.connections.ContainsKey(target.connectionId))yield break;
            if (view==null)continue;
            MineableOreRecord r;
            if (!_oreRecords.TryGetValue(view.OreId,out r))continue;
            var snapshot=r.Copy();
            if (r.Detached){snapshot.Position=view.transform.position;snapshot.Rotation=view.transform.rotation;}
            TargetOreState(target,snapshot);
            if (++count%12==0)yield return null;
        }
    }

    public string OreChunkStatus()
    {
        var result=new System.Text.StringBuilder();
        result.Append("[Gobmine Ore Chunks] ready=").Append(IsReady)
            .Append(" server=").Append(isServer).Append(" seed=").Append(seed)
            .Append(" chunk rules enabled=").Append(_oreGenerator!=null && _oreGenerator.Enabled)
            .Append(" saved nodes=").Append(_oreRecords.Count)
            .Append(" active=").Append(_oreViews.Count)
            .Append(" discovery backlog=").Append(_oreQueued.Count)
            .Append(" dormant candidates=").Append(_oreDormant.Count)
            .Append(" mining-prewarmed views=").Append(_orePrewarmViews)
            .Append(" naturally-exposed views=").Append(_orePassiveViews)
            .Append(" radius(m)=").Append(oreChunkMinimumRadius).Append("..").Append(oreChunkMaximumRadius)
            .Append(" preload(m)=").Append(oreChunkPreloadDistance)
            .Append(" workshop safe radius(m)=").Append(oreChunkWorkshopSafeRadiusMetres)
            .Append(" players=").Append(_connectedPlayers.Count);
        if (isServer && _oreGenerator!=null)
        {
            foreach (Transform player in _connectedPlayers)
            {
                if (player==null)continue;
                var candidates=new List<OreChunkGenerator.Candidate>();
                _oreGenerator.GetNear(player.position,8f,candidates);
                result.Append("\nplayer ").Append(player.position).Append(" depth=").Append(DepthAt(player.position))
                    .Append("m; nearby geological candidates=").Append(candidates.Count);
                for (int i=0;i<Mathf.Min(4,candidates.Count);i++)
                {
                    OreChunkGenerator.Candidate c=candidates[i];
                    string state=_oreViews.ContainsKey(c.Id)?"active":
                        _oreRecords.ContainsKey(c.Id)?"recorded":
                        _oreDormant.Contains(c.Id)?"hidden":
                        _oreQueued.Contains(c.Id)?"queued":
                        _oreRejected.Contains(c.Id)?"no host rock":"not scanned";
                    result.Append("\n  ").Append(c.MaterialId).Append(" near ").Append(c.Centre)
                        .Append(" radius=").Append(c.Radius).Append(" state=").Append(state);
                }
            }
        }
        return result.ToString();
    }

    /// <summary>Read-only host diagnostic. Does not create ore or modify the world.</summary>
    public string ProbeNearbyOreCandidates(float radius = 20f)
    {
        var report = new System.Text.StringBuilder(OreChunkStatus());
        if (!isServer || _oreGenerator == null || !_oreGenerator.Enabled || _terrain == null)
        {
            report.Append("\nOre generator is unavailable. Check World material catalogue and Spawn Mineable Chunks.");
            return report.ToString();
        }
        int types = 0;
        if (materialCatalog != null && materialCatalog.definitions != null)
            foreach (TerrainMaterialDefinition definition in materialCatalog.definitions)
                if (definition != null && definition.generateDeposits && definition.spawnMineableChunks) types++;
        report.Append("\nEnabled ore types: ").Append(types)
            .Append("; catalogue: ").Append(materialCatalog != null ? materialCatalog.name : "MISSING")
            .Append("; ore world save: ").Append(materialSaveFileName);
        if (_connectedPlayers.Count == 0)
            report.Append("\nNo registered players: check PlayerSetup/World.RegisterPlayer.");
        float range = Mathf.Clamp(radius, 2f, 25f);
        var candidates = new List<OreChunkGenerator.Candidate>();
        foreach (Transform player in _connectedPlayers)
        {
            if (player == null) continue;
            _oreGenerator.GetNear(player.position, range, candidates);
            int protectedCount = 0, missingRock = 0, hidden = 0, visible = 0;
            int active = 0, recorded = 0;
            OreChunkGenerator.Candidate nearest = default;
            float nearestDistance = float.MaxValue;
            OreChunkGenerator.Candidate nearestBuried = default;
            float nearestBuriedDistance = float.MaxValue;
            foreach (OreChunkGenerator.Candidate c in candidates)
            {
                if (OreChunkGenerator.IsProtectedWorkshopPosition(c.Centre, oreChunkWorkshopSafeRadiusMetres))
                { protectedCount++; continue; }
                if (_oreViews.ContainsKey(c.Id)) { active++; continue; }
                if (_oreRecords.ContainsKey(c.Id)) { recorded++; continue; }
                if (_oreRejected.Contains(c.Id)) { missingRock++; continue; }
                Vector3Int sample = Vector3Int.RoundToInt(c.Centre/voxelSize);
                if (_terrain.Get(sample) >= 0f) { missingRock++; continue; }
                if (!HasOreExposure(c.Centre,c.Radius))
                {
                    hidden++;
                    float d = (c.Centre-player.position).sqrMagnitude;
                    if (d < nearestBuriedDistance) { nearestBuriedDistance=d; nearestBuried=c; }
                    continue;
                }
                visible++;
                float dist = (c.Centre - player.position).sqrMagnitude;
                if (dist < nearestDistance) {nearestDistance = dist;nearest = c;}
            }
            report.Append("\nPlayer ").Append(player.position)
                .Append(" radius ").Append(range).Append("m: candidates=").Append(candidates.Count)
                .Append(" protected=").Append(protectedCount)
                .Append(" buried=").Append(hidden)
                .Append(" missing-host-rock=").Append(missingRock)
                .Append(" visible-but-unspawned=").Append(visible)
                .Append(" active=").Append(active).Append(" saved-not-visible=").Append(recorded);
            if (nearestDistance < float.MaxValue)
                report.Append("\n  Nearest EXPOSED but unspawned candidate: material=").Append(nearest.MaterialId)
                    .Append(" position=").Append(nearest.Centre)
                    .Append(" distance=").Append(Mathf.Sqrt(nearestDistance).ToString("F1")).Append("m");
            if (nearestBuriedDistance < float.MaxValue)
                report.Append("\n  Nearest BURIED candidate: material=").Append(nearestBuried.MaterialId)
                    .Append(" position=").Append(nearestBuried.Centre)
                    .Append(" distance=").Append(Mathf.Sqrt(nearestBuriedDistance).ToString("F1"))
                    .Append("m. Mine toward this position through solid rock to reveal the nodule.");
            if (candidates.Count == 0)
                report.Append("\n  No eligible ore candidates: verify mineral depth bands, spawn flags, and world seed.");
            else if (visible == 0 && active == 0)
                report.Append("\n  Mine into intact stone beyond the workshop safe radius to uncover buried ore.");
        }
        return report.ToString();
    }

    private float RadiationAtOreChunks(Vector3 position)
    {
        if (!isServer || !ExpeditionEnabled)return 0f;
        float radius=ExpeditionProfile.radiationRadiusMetres, total=0f;
        foreach (MineableOreChunk view in _oreViews.Values)
        {
            if (view==null)continue;
            var ore=materialCatalog.Get(view.MaterialId);
            if (ore==null || ore.radiationRate<=0f)continue;
            float distance=Vector3.Distance(position,view.transform.position);
            if (distance<radius)total+=ore.radiationRate*Mathf.Clamp01(1-distance/radius);
        }
        return total;
    }
}
