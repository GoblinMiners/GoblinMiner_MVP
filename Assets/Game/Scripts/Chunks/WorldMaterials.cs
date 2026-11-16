using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

public partial class World
{
    [Header("Materials and resources")]
    [SerializeField] private TerrainMaterialCatalog materialCatalog;
    [SerializeField] private GameObject resourcePickupPrefab;
    [Min(1)] public int maxVisibleResourcePickups=128;
    [Min(0.25f)] public float resourcePickupReach=2f;
    [Tooltip("Seconds between coherent terrain/resource snapshots. Zero disables periodic saves, not shutdown saves.")]
    [Min(0)] public float materialAutosaveSeconds=60f;
    [Tooltip("Use a separate filename for test worlds. File only, no directories.")]
    public string materialSaveFileName="world_materials_v1.dat";
    [Header("Ground items")]
    [Tooltip("Seconds an uncollected resource or dropped item stays on the ground before it disappears. 0 keeps it forever.")]
    [Min(0)] public float groundItemLifetimeSeconds=300f;
    [Tooltip("Seconds before anyone else can pick up an item a player dropped. The dropper must first walk out of pickup reach.")]
    [Min(0)] public float droppedItemPickupDelaySeconds=0.5f;
    public bool importLegacyDensitySave=true;
    private string MaterialSavePath
    {
        get
        {
            if(string.IsNullOrWhiteSpace(materialSaveFileName) || System.IO.Path.GetFileName(materialSaveFileName)!=materialSaveFileName)
                throw new InvalidOperationException("Material save name must be a filename without directories.");
            return System.IO.Path.Combine(Application.persistentDataPath,materialSaveFileName);
        }
    }
    public TerrainMaterialCatalog MaterialCatalog=>materialCatalog;
    public int UncollectedResourcePiles=>_resourcePiles==null?0:_resourcePiles.Count;
    private TerrainMaterialGenerator _materials;
    private readonly Dictionary<Vector3Int,int> _materialCache=new Dictionary<Vector3Int,int>();
    private ResourcePileStore _resourcePiles;
    private readonly Dictionary<long,OrePickup> _resourceViews=new Dictionary<long,OrePickup>();
    private readonly HashSet<PlayerInventory> _inventories=new HashSet<PlayerInventory>();
    private float _nextResourceRefresh,_nextAutosave;
    // Ground-item lifetimes and who dropped what. Runtime only; a loaded pile starts a fresh lifetime.
    private readonly Dictionary<long,float> _pileSeenAt=new Dictionary<long,float>();
    private readonly Dictionary<long,float> _pileOpenToAllAt=new Dictionary<long,float>();
    private readonly Dictionary<long,PlayerInventory> _pileDroppedBy=new Dictionary<long,PlayerInventory>();
    private readonly Dictionary<int,double> _expiredGroundUnits=new Dictionary<int,double>();
    /// <summary>Server: units of each item that expired on the ground this session, by item ID. Kept for a future reward chest.</summary>
    public IReadOnlyDictionary<int,double> ExpiredGroundUnits=>_expiredGroundUnits;
    private bool _materialWorldValid;
    private string _worldRulesSignature;

    private void ResolveMaterialAssets()
    {
        if(materialCatalog==null)materialCatalog=Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if(resourcePickupPrefab==null)resourcePickupPrefab=Resources.Load<GameObject>("Gobmine/OrePickup");
        if(materialCatalog==null)throw new InvalidOperationException("Gobmine terrain material catalogue is missing.");
        materialCatalog.ValidateOrThrow();
        if(resourcePickupPrefab==null || resourcePickupPrefab.GetComponent<OrePickup>()==null)
            throw new InvalidOperationException("Gobmine OrePickup prefab is missing or invalid.");
    }
    private bool InitializeMaterialWorld()
    {
        try
        {
            ResolveMaterialAssets();
            SharedStorage.ClearPendingLoads();
            _worldRulesSignature=ComputeWorldRulesSignature();
            MaterialWorldPersistence.Snapshot saved; string error;
            bool loaded=MaterialWorldPersistence.TryLoad(MaterialSavePath,out saved,out error);
            if(!loaded && error!=null)throw new InvalidOperationException(error+" Back up/repair the save; no new world will overwrite it.");
            if(loaded)
            {
                if(saved.ChunkSize!=chunkSize || saved.VoxelSize!=voxelSize || saved.RulesSignature!=_worldRulesSignature)
                    throw new InvalidOperationException("Saved world rules differ from current dimensions/material IDs/yields/generation. Restore the original settings or move the material save AND its .bak aside for a new world.");
                seed=saved.Seed;
            }
            InitializeExpeditionGenerator();
            _terrain=new TerrainDensityStore(chunkSize,voxelSize,_caverns);_densityReader=_terrain.Get;
            _materials=new TerrainMaterialGenerator(seed,materialCatalog,voxelSize,ExpeditionEnabled ? ExpeditionProfile.surfaceReferenceY : 0f);
            InitializeOreChunkGenerator();
            _resourcePiles=new ResourcePileStore();
            if(loaded)
            {
                foreach(var p in saved.Density) _terrain.Set(p.Position,p.Value);
                foreach(var p in saved.Resources)
                {
                    if(p.MaterialId==0)
                    {
                        if(GobmineItems.Find(p.ItemId)==null)
                            throw new InvalidOperationException("Saved dropped item "+p.ItemId+" is not in the item database.");
                        continue;
                    }
                    var definition=materialCatalog.Get(p.MaterialId);
                    if(definition==null || definition.item==null || definition.item.itemID!=p.ItemId)
                        throw new InvalidOperationException("Saved resource item/material mapping is invalid.");
                }
                _resourcePiles.Import(saved.Resources);
                foreach(var storage in saved.Storages) SharedStorage.LoadWhenReady(storage.Key,storage.Slots);
                LoadFallingShapeRecords(saved.Shapes);
                LoadOreChunkRecords(saved.OreChunks);
            }
            else if(importLegacyDensitySave && !ExpeditionEnabled) _terrain.ImportLegacyChunks(WorldSaver.LoadWorld(chunkSize)); // Never import legacy densities into a different cavern generator.
            InitializeExpeditionFluids();
            if (!loaded) ConvertSceneWorldItems();
            if (loaded) { _fluids?.Import(saved.Fluids); expeditionUnlocks = saved.ExpeditionUnlocks; }
            else expeditionUnlocks = 0;
            _materialWorldValid=true;
            _nextAutosave=Time.unscaledTime+Mathf.Max(1f,materialAutosaveSeconds);
            return true;
        }
        catch(Exception ex) {Debug.LogError("[Gobmine Materials] "+ex);_materialWorldValid=false;return false;}
    }
    private void InitializeClientMaterials(int worldSeed,string signature)
    {
        ResolveMaterialAssets();
        if(ComputeWorldRulesSignature()!=signature)throw new InvalidOperationException("Host and client material catalogues differ. Install the same patch/assets on both.");
        _worldRulesSignature=signature;
        seed=worldSeed; InitializeExpeditionGenerator(); _materialCache.Clear();
        _materials=new TerrainMaterialGenerator(seed,materialCatalog,voxelSize,ExpeditionEnabled ? ExpeditionProfile.surfaceReferenceY : 0f);
        InitializeOreChunkGenerator();
    }
    private void ResetMaterialRuntime()
    {
        foreach(var pair in _resourceViews)
            if(pair.Value!=null) { if(NetworkServer.active)NetworkServer.Destroy(pair.Value.gameObject);else Destroy(pair.Value.gameObject); }
        _resourceViews.Clear();_inventories.Clear();_resourcePiles=null;_materials=null;
        _materialWorldValid=false;_worldRulesSignature=null;_nextResourceRefresh=0f;_materialCache.Clear();_miningRewardBatch.Clear();
        _pileSeenAt.Clear();_pileOpenToAllAt.Clear();_pileDroppedBy.Clear();_expiredGroundUnits.Clear();
    }
    public int GetSolidMaterialId(Vector3Int cell)
    {
        if(_materials==null)return 1;
        int id;if(_materialCache.TryGetValue(cell,out id))return id;
        if(_materialCache.Count>=131072)_materialCache.Clear();
        id=_materials.GetSolidMaterial(cell);_materialCache[cell]=id;return id;
    }
    public TerrainCellInfo GetCellInfo(Vector3Int cell)
    {
        if(_terrain==null)throw new InvalidOperationException("Terrain not initialized; an unavailable world is not air.");
        TerrainCellState state=TerrainQuery.CellState(cell,_densityReader);
        return new TerrainCellInfo {Cell=cell,State=state,MaterialId=state==TerrainCellState.Air?TerrainMaterialCatalog.Air:GetSolidMaterialId(cell)};
    }
    public bool IsAirAt(Vector3 worldPosition)
    {
        if(_terrain==null)throw new InvalidOperationException("Terrain not initialized.");
        return TerrainQuery.SampleDensity(worldPosition/voxelSize,_densityReader)>=0;
    }
    public int GetMaterialAt(Vector3 worldPosition)=>IsAirAt(worldPosition)?TerrainMaterialCatalog.Air:GetSolidMaterialId(Vector3Int.FloorToInt(worldPosition/voxelSize));
    public ItemSO GetInventoryItem(int itemId)=>materialCatalog==null?null:materialCatalog.GetItem(itemId);
    public Material GetMaterialForSurface(int id)=>materialCatalog==null?GetVoxelMaterial():materialCatalog.Surface(id);

    private float HardnessAtSample(Vector3Int p)
    {
        float hardness=0.05f;
        for(int x=0;x<=1;x++)for(int y=0;y<=1;y++)for(int z=0;z<=1;z++)
        {
            var d=materialCatalog.Get(GetSolidMaterialId(p-new Vector3Int(x,y,z)));
            if(d!=null)hardness=Mathf.Max(hardness,d.hardness);
        }
        return hardness; // Conservative hardest-touching-cell rule at interfaces.
    }
    private struct BatchedMiningReward
    {
        public int MaterialId, ItemId;
        public double Units;
        public Vector3 Moment;
    }
    private readonly Dictionary<int, BatchedMiningReward> _miningRewardBatch = new Dictionary<int, BatchedMiningReward>();
    private void BeginRemovalRewardBatch() => _miningRewardBatch.Clear();
    private void FlushRemovalRewardBatch(PlayerInventory miner)
    {
        // One inventory write per material per commit, rather than one per cell.
        // This retains fractions and avoids hundreds of SyncDictionary operations.
        foreach (BatchedMiningReward reward in _miningRewardBatch.Values)
        {
            if (miner != null && miner.isServer)
            {
                NotifyMinedExposure(miner, reward.MaterialId, reward.Units);
                string reason;
                if (CanCollectExpeditionMaterial(miner, reward.MaterialId, out reason))
                {
                    double overflow = miner.ServerAddUnits(reward.ItemId, reward.Units);
                    if (overflow <= 1e-9d) continue;
                    // Bag full: what does not fit stays on the ground where it was cut.
                    NotifyEquipmentMessage(miner, "Bag full: the rest stays on the ground for a while.");
                    _resourcePiles.Add(reward.MaterialId, reward.ItemId, overflow, reward.Moment / (float)reward.Units);
                    continue;
                }
                NotifyEquipmentMessage(miner, reason);
            }
            _resourcePiles.Add(reward.MaterialId, reward.ItemId, reward.Units, reward.Moment / (float)reward.Units);
        }
        _miningRewardBatch.Clear();
    }
    private void RewardRemoval(Vector3Int cell,Vector3 centre,float cubicMetres,bool collapse,PlayerInventory miner)
    {
        int id=GetSolidMaterialId(cell);
        var d=materialCatalog.Get(id);
        if (!collapse || !preserveDisconnectedShapes) QueueDebris(cell,centre,cubicMetres,id,collapse);
        if (collapse && !preserveDisconnectedShapes) QueueCollapseHazard(cell,centre,cubicMetres);
        // Default: collapses become genuine shaped rigidbodies, not invisible hazards.
        if(d==null || d.item==null || d.itemsPerCubicMetre<=0)return;
        double units=cubicMetres*d.itemsPerCubicMetre;
        if(!collapse && miner!=null && miner.isServer)
        {
            BatchedMiningReward reward;
            _miningRewardBatch.TryGetValue(id, out reward);
            reward.MaterialId=id; reward.ItemId=d.item.itemID;
            reward.Units+=units; reward.Moment+=centre*(float)units;
            _miningRewardBatch[id]=reward;
            return;
        }
        _resourcePiles.Add(id,d.item.itemID,units,centre);
        // A disconnected miner does not cause queued edits to destroy their rewards.
    }
    public void RegisterInventory(PlayerInventory inventory) {if(inventory!=null)_inventories.Add(inventory);}
    public void PreserveDepartingInventory(PlayerInventory inventory)
    {
        if(inventory==null || !isServer || _resourcePiles==null || !_materialWorldValid)return;
        foreach(var pair in inventory.ServerTakeAll())
            if(pair.Value>0)_resourcePiles.Add(MaterialForItem(pair.Key),pair.Key,pair.Value,inventory.transform.position+Vector3.up*0.4f);
        _inventories.Remove(inventory);
    }
    /// <summary>The terrain material that yields this item, or 0 for items that are not terrain resources.</summary>
    private int MaterialForItem(int item)
    {
        foreach(var d in materialCatalog.definitions)if(d!=null && d.item!=null && d.item.itemID==item)return d.materialId;
        return 0;
    }

    /// <summary>
    /// Server: puts items a player dropped on the ground in front of them as a pickup.
    /// The dropper cannot pick it straight back up until they have walked out of reach.
    /// </summary>
    [Server]
    public bool ServerDropItem(PlayerInventory dropper,ItemSO item,int count)
    {
        if(!isServer || _resourcePiles==null || !_materialWorldValid || dropper==null || item==null || item.itemID<=0 || count<=0)return false;
        Vector3 forward=Vector3.ProjectOnPlane(dropper.transform.forward,Vector3.up);
        if(forward.sqrMagnitude<0.01f)forward=Vector3.forward;
        Vector3 position=dropper.transform.position+Vector3.up*0.8f+forward.normalized*dropper.DropDistance;
        if(!IsAirAt(position))position=dropper.transform.position+Vector3.up*0.8f; // Never drop into a wall.
        long id=_resourcePiles.Add(MaterialForItem(item.itemID),item.itemID,count,position);
        _pileDroppedBy[id]=dropper;
        _pileOpenToAllAt[id]=Time.unscaledTime+droppedItemPickupDelaySeconds;
        return true;
    }

    /// <summary>Scene-placed test items (WorldItem) become network pickups in a new world.</summary>
    private void ConvertSceneWorldItems()
    {
        foreach(WorldItem placed in FindObjectsByType<WorldItem>(FindObjectsInactive.Exclude,FindObjectsSortMode.None))
        {
            if(placed.Item==null || placed.Count<=0)continue;
            if(placed.Item.itemID<=0)
            { Debug.LogWarning("[Items] "+placed.name+" holds "+placed.Item.name+", which has no item ID. Run Tools > Gobmine > Items > Set Up Items.",placed);continue; }
            _resourcePiles.Add(MaterialForItem(placed.Item.itemID),placed.Item.itemID,placed.Count,placed.transform.position+Vector3.up*0.3f);
        }
    }

    /// <summary>Clients and server: scene-placed test items are replaced by network pickups.</summary>
    private static void RemoveSceneWorldItems()
    {
        foreach(WorldItem placed in FindObjectsByType<WorldItem>(FindObjectsInactive.Exclude,FindObjectsSortMode.None))
            Destroy(placed.gameObject);
    }

    private void ExpireGroundItems()
    {
        if(groundItemLifetimeSeconds<=0f)return;
        float now=Time.unscaledTime;
        List<long> expired=null;
        foreach(var pile in _resourcePiles.All)
        {
            // Measured from when the pile first appeared, so changing the lifetime applies at once.
            float seenAt;
            if(!_pileSeenAt.TryGetValue(pile.Id,out seenAt))_pileSeenAt[pile.Id]=now;
            else if(now-seenAt>=groundItemLifetimeSeconds)(expired??(expired=new List<long>())).Add(pile.Id);
        }
        if(expired==null)return;
        foreach(long id in expired)
        {
            ResourcePileStore.Pile pile;
            if(!_resourcePiles.Remove(id,out pile))continue;
            double previous;_expiredGroundUnits.TryGetValue(pile.ItemId,out previous);_expiredGroundUnits[pile.ItemId]=previous+pile.Units;
            ForgetPile(id);
        }
    }

    private void ForgetPile(long id)
    {
        _pileSeenAt.Remove(id);_pileOpenToAllAt.Remove(id);_pileDroppedBy.Remove(id);
        OrePickup view;
        if(_resourceViews.TryGetValue(id,out view))
        {
            if(view!=null)NetworkServer.Destroy(view.gameObject);
            _resourceViews.Remove(id);
        }
    }

    /// <summary>A freshly dropped item waits briefly, and its dropper must walk away from it first.</summary>
    private bool PickupBlockedFor(long id,PlayerInventory inventory,Vector3 position)
    {
        float openAt;
        if(_pileOpenToAllAt.TryGetValue(id,out openAt) && Time.unscaledTime<openAt)return true;
        PlayerInventory dropper;
        if(!_pileDroppedBy.TryGetValue(id,out dropper))return false;
        if(dropper==null){_pileDroppedBy.Remove(id);return false;}
        if(!ResourcePickupRules.WithinReach(dropper.transform.position,position,resourcePickupReach+0.5f)){_pileDroppedBy.Remove(id);return false;}
        return dropper==inventory;
    }
    public void UpdateResourcePosition(long id,Vector3 position)
    {
        if(isServer && _resourcePiles!=null)_resourcePiles.UpdatePosition(id,position);
    }
    private RaycastHit[] _resourceCollectionHits = new RaycastHit[32];

    // Called by the server pickup view on a staggered interval. Trigger contact and
    // the project's layer collision matrix are no longer required for collection.
    public bool TryCollectNearbyResource(long id)
    {
        if (!isServer || !IsReady || _resourcePiles == null) return false;
        foreach (PlayerInventory inventory in _inventories)
            if (inventory != null && TryCollectResource(id, inventory)) return true;
        return false;
    }

    public bool TryCollectResource(long id, PlayerInventory inventory)
    {
        if (!isServer || !IsReady || _resourcePiles == null) return false;
        ResourcePileStore.Pile pile;
        if (!_resourcePiles.TryGet(id, out pile)) return false;
        OrePickup view;
        if (_resourceViews.TryGetValue(id, out view) && view != null)
        {
            // Use the server's latest physical position, not a previous fixed tick.
            if (!ResourcePickupRules.IsFinite(view.transform.position)) return false;
            pile.Position = view.transform.position;
            _resourcePiles.UpdatePosition(id, pile.Position);
        }
        ResourcePickupAccess access = GetResourcePickupAccess(pile, inventory);
        if (access != ResourcePickupAccess.Reachable)
        {
            if (access == ResourcePickupAccess.NeedsEquipment && inventory != null &&
                ResourcePickupRules.WithinReach(inventory.transform.position, pile.Position, resourcePickupReach))
            {
                string reason; CanCollectExpeditionMaterial(inventory, pile.MaterialId, out reason);
                NotifyEquipmentMessage(inventory, reason);
            }
            return false;
        }
        if (PickupBlockedFor(id, inventory, pile.Position)) return false;
        double left = inventory.ServerAddUnits(pile.ItemId, pile.Units);
        if (left >= pile.Units - 1e-9d) return false; // No room.
        // Main-thread transaction: successful credit followed by removal, no await/yield.
        // Subsequent players/trigger callbacks cannot collect this record a second time.
        if (left > 1e-9d)
        {
            // Partial pickup: the rest stays on the ground.
            _resourcePiles.SetUnits(id, left);
            if (_resourceViews.TryGetValue(id, out view) && view != null && _resourcePiles.TryGet(id, out pile)) view.Refresh(pile);
            return true;
        }
        _resourcePiles.Remove(id, out pile);
        ForgetPile(id);
        return true;
    }

    private ResourcePickupAccess GetResourcePickupAccess(ResourcePileStore.Pile pile, PlayerInventory inventory)
    {
        if (!isServer || !IsReady || _terrain == null || inventory == null ||
            !inventory.isServer || !inventory.isActiveAndEnabled || !ResourcePickupRules.IsFinite(pile.Position))
            return ResourcePickupAccess.Invalid;
        GobminePlayerHealth health = inventory.GetComponent<GobminePlayerHealth>();
        if (health != null && health.IsDead) return ResourcePickupAccess.PlayerDown;
        string equipmentReason;
        if (pile.MaterialId != 0 && !CanCollectExpeditionMaterial(inventory, pile.MaterialId, out equipmentReason)) return ResourcePickupAccess.NeedsEquipment;
        if (!ResourcePickupRules.WithinReach(inventory.transform.position, pile.Position, resourcePickupReach))
            return ResourcePickupAccess.TooFar;
        // Raycasts alone cannot determine whether an endpoint is inside solid terrain.
        // A buried resource is retained; do not move it through a wall or award it anyway.
        if (!IsAirAt(pile.Position)) return ResourcePickupAccess.BuriedInTerrain;

        Collider body = inventory.GetComponent<Collider>();
        Vector3 centre = inventory.transform.position + Vector3.up * 0.2f;
        Vector3 upper = inventory.transform.position + Vector3.up * 0.6f;
        if (body != null && body.enabled && !body.isTrigger)
        {
            Bounds bounds = body.bounds;
            centre = bounds.center;
            upper = centre + Vector3.up * Mathf.Min(0.6f, bounds.extents.y * 0.5f);
        }
        bool waiting;
        if (HasResourceCollectionPath(upper, pile.Position, out waiting)) return ResourcePickupAccess.Reachable;
        bool lowerWaiting;
        if (HasResourceCollectionPath(centre, pile.Position, out lowerWaiting)) return ResourcePickupAccess.Reachable;
        return waiting || lowerWaiting ? ResourcePickupAccess.TerrainUpdating : ResourcePickupAccess.SolidObstacle;
    }

    private bool HasResourceCollectionPath(Vector3 from, Vector3 to, out bool waiting)
    {
        waiting = false;
        if (!ResourcePickupRules.IsFinite(from) || !IsAirAt(from)) return false;
        // A missing, sleeping or dirty collision mesh is NOT an opening. Check the
        // small chunk box enclosing this short path before trusting physical hits.
        // This is deliberately conservative at corners; it retries next interval.
        Vector3Int low = GetChunkCoordFromPosition(Vector3.Min(from, to));
        Vector3Int high = GetChunkCoordFromPosition(Vector3.Max(from, to));
        long cells = ((long)high.x - low.x + 1) * ((long)high.y - low.y + 1) * ((long)high.z - low.z + 1);
        if (cells <= 0 || cells > 64) { waiting = true; return false; }
        for (int x = low.x; x <= high.x; x++)
        for (int y = low.y; y <= high.y; y++)
        for (int z = low.z; z <= high.z; z++)
        {
            Vector3Int coord = new Vector3Int(x, y, z);
            Chunk chunk;
            if (!_activeChunks.TryGetValue(coord, out chunk) || chunk == null ||
                !chunk.gameObject.activeInHierarchy || _dirtyMeshes.Contains(coord))
            { waiting = true; return false; }
            MeshCollider collider = chunk.GetComponent<MeshCollider>();
            if (collider == null || !collider.enabled) { waiting = true; return false; }
        }
        return !ResourcePickupRules.HasSolidObstacle(gameObject.scene.GetPhysicsScene(), from, to, ref _resourceCollectionHits);
    }

    // Read-only runtime diagnostic. Does not collect, delete, or move any resources.
    public string DescribeNearbyResourcePickups()
    {
        if (!isServer || !IsReady || _resourcePiles == null) return "[Gobmine Pickups] Start a ready host/server first.";
        var text = new System.Text.StringBuilder();
        text.AppendLine($"[Gobmine Pickups] Reach={resourcePickupReach:0.00}m; visible={_resourceViews.Count}; stored={_resourcePiles.Count}; players={_inventories.Count}");
        int shown = 0;
        foreach (var entry in _resourceViews)
        {
            ResourcePileStore.Pile pile;
            if (entry.Value == null || !_resourcePiles.TryGet(entry.Key, out pile)) continue;
            pile.Position = entry.Value.transform.position;
            foreach (PlayerInventory inventory in _inventories)
            {
                if (inventory == null || Vector3.Distance(inventory.transform.position, pile.Position) > Mathf.Max(8f, resourcePickupReach + 2f)) continue;
                text.AppendLine($"Pile {pile.Id}; material={pile.MaterialId}; units={pile.Units:0.###}; position={pile.Position}; player={inventory.netId}; distance={Vector3.Distance(inventory.transform.position, pile.Position):0.00}m; status={GetResourcePickupAccess(pile, inventory)}");
                if (++shown >= 24) return text.AppendLine("Report limited to 24 nearby player/pile pairs.").ToString();
            }
        }
        if (shown == 0) text.AppendLine("No visible pickup views near registered players. Stored records can exceed the visible view limit.");
        return text.ToString();
    }
    private void UpdateMaterialWorld()
    {
        if(!isServer || !_materialWorldValid || !IsReady)return;
        if(materialAutosaveSeconds>0 && Time.unscaledTime>=_nextAutosave)
        {
            // Skip a tick rather than queue up saves if the disk is slower than the interval.
            if(_backgroundSave==null || _backgroundSave.IsCompleted)SaveMaterialWorld(true);
            _nextAutosave=Time.unscaledTime+Mathf.Max(1f,materialAutosaveSeconds);
        }
        if(Time.unscaledTime<_nextResourceRefresh)return;
        _nextResourceRefresh=Time.unscaledTime+0.5f;
        ExpireGroundItems();
        var nearest=new List<ResourcePileStore.Pile>();
        foreach(var p in _resourcePiles.All)
            if(_activeChunks.ContainsKey(GetChunkCoordFromPosition(p.Position)) && !_dirtyMeshes.Contains(GetChunkCoordFromPosition(p.Position)) && NearestPlayerDistance(p.Position)<40f*40f)nearest.Add(p);
        nearest.Sort((a,b)=>NearestPlayerDistance(a.Position).CompareTo(NearestPlayerDistance(b.Position)));
        int visibleLimit=Mathf.Max(1,maxVisibleResourcePickups);
        if(nearest.Count>visibleLimit)nearest.RemoveRange(visibleLimit,nearest.Count-visibleLimit);
        var wanted=new HashSet<long>();foreach(var p in nearest)wanted.Add(p.Id);
        var remove=new List<long>();
        foreach(var pair in _resourceViews)if(!wanted.Contains(pair.Key)||pair.Value==null)
        {if(pair.Value!=null)NetworkServer.Destroy(pair.Value.gameObject);remove.Add(pair.Key);}
        foreach(long id in remove)_resourceViews.Remove(id);
        foreach(var p in nearest)
        {
            OrePickup view;
            if(!_resourceViews.TryGetValue(p.Id,out view))
            {
                var obj=Instantiate(resourcePickupPrefab,p.Position,Quaternion.identity);
                view=obj.GetComponent<OrePickup>();view.Configure(this,p);
                _resourceViews[p.Id]=view;NetworkServer.Spawn(obj);
            }
            else view.Refresh(p);
        }
    }
    private float NearestPlayerDistance(Vector3 p)
    {
        float result=float.PositiveInfinity;
        foreach(var t in _connectedPlayers)if(t!=null)result=Mathf.Min(result,(t.position-p).sqrMagnitude);
        return result;
    }
    public bool SaveWorldNow() => isServer && SaveMaterialWorld();
    private System.Threading.Tasks.Task _backgroundSave;
    /// <summary>
    /// background=false blocks until the file is on disk (shutdown, explicit saves).
    /// background=true (autosave) only serialises here and writes on a worker thread;
    /// it returns true once the save is queued, and a write failure is logged.
    /// </summary>
    private bool SaveMaterialWorld(bool background=false)
    {
        if(!_materialWorldValid || _terrain==null)return false;
        try
        {
            if(ComputeWorldRulesSignature()!=_worldRulesSignature)
                throw new InvalidOperationException("Material rules changed while the world was running. Revert them before saving; use a new save slot for different generation or reward rules.");
            var s=new MaterialWorldPersistence.Snapshot {Seed=seed,ChunkSize=chunkSize,VoxelSize=voxelSize,RulesSignature=_worldRulesSignature};
            foreach(var p in _terrain.CopyOverrides())s.Density.Add(new MaterialWorldPersistence.DensityPoint {Position=p.Key,Value=p.Value});
            foreach(var view in _resourceViews.Values)if(view!=null)_resourcePiles.UpdatePosition(view.pileId,view.transform.position);
            s.Resources=_resourcePiles.Copy();
            // No account/authenticator exists in the upload. Snapshot carried items as
            // recoverable world bundles, never assign inventories using an untrusted client ID.
            foreach(var inventory in _inventories)
            {
                if(inventory==null || !inventory.isServer)continue;
                foreach(var p in inventory.Totals)
                    if(p.Value>0)s.Resources.Add(new ResourcePileStore.Pile {MaterialId=MaterialForItem(p.Key),ItemId=p.Key,Units=p.Value,Position=inventory.transform.position+Vector3.up*0.4f});
            }
            var storageKeys=new HashSet<string>();
            foreach(var storage in SharedStorage.All)
            {
                if(storage==null || !storage.isServer)continue;
                if(!storageKeys.Add(storage.StorageKey))
                { Debug.LogError("[Gobmine Material Save] Two storage boxes share the key '"+storage.StorageKey+"'; only the first is saved. Give each box its own Storage Key.",storage);continue; }
                s.Storages.Add(new MaterialWorldPersistence.StorageRecord {Key=storage.StorageKey,Slots=storage.Snapshot()});
            }
            s.ExpeditionState = ExpeditionEnabled;
            s.ExpeditionUnlocks = expeditionUnlocks;
            if (_fluids != null) s.Fluids = _fluids.CopyOverrides();
            s.Shapes = SnapshotFallingShapesForSave();
            s.OreChunks = SnapshotOreChunksForSave();
            if(background)_backgroundSave=MaterialWorldPersistence.WriteInBackground(MaterialSavePath,s);
            else MaterialWorldPersistence.Write(MaterialSavePath,s);
            return true;
        }
        catch(Exception ex){Debug.LogError("[Gobmine Material Save] "+ex);return false;}
    }
}
