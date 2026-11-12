using UnityEngine;

/// <summary>
/// A separate procedural low-poly ore body. A single sphere proxy handles collision;
/// it is NOT a second terrain density field. A body remains mineable after detaching.
/// All health/loot is managed by World on the server.
/// </summary>
[DisallowMultipleComponent]
public sealed class MineableOreChunk : MonoBehaviour
{
    public long OreId { get; private set; }
    public int MaterialId { get; private set; }
    public Rigidbody Body { get; private set; }
    public bool ServerOwned { get; private set; }
    public bool Detached { get; private set; }
    public float Radius { get; private set; }
    public double RemainingUnits { get; private set; }
    public double TotalUnits { get; private set; }
    private Mesh _mesh;
    private SphereCollider _collider;
    private MeshRenderer _renderer;
    private World _world;
    private Vector3 _lastVelocity;
    private void FixedUpdate()
    {
        if (!ServerOwned || !Detached || Body==null || Body.isKinematic || _world==null) return;
        Vector3 current=Velocity;
        _world.ServerTickOreImpact(this,_lastVelocity.sqrMagnitude>current.sqrMagnitude?_lastVelocity:current);
        _lastVelocity=current;
    }

    public void Init(World world, MineableOreRecord state, bool server)
    {
        OreId=state.Id; MaterialId=state.MaterialId; ServerOwned=server; _world=world;
        gameObject.name="MineableOre_"+MaterialId+"_"+OreId;
        gameObject.layer=world.terrainLayer;
        var filter=gameObject.AddComponent<MeshFilter>();
        _renderer=gameObject.AddComponent<MeshRenderer>();
        _mesh=MakeBoulderMesh(OreId);
        filter.sharedMesh=_mesh;
        Material surface=world.MaterialCatalog.Debris(MaterialId);
        if (surface == null) surface=world.GetMaterialForSurface(MaterialId);
        _renderer.sharedMaterial=surface;
        _collider=gameObject.AddComponent<SphereCollider>();
        _collider.radius=0.72f; // Approximate shape, kept smaller than its visible spikes.
        Body=gameObject.AddComponent<Rigidbody>();
        Body.interpolation=RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode=CollisionDetectionMode.ContinuousSpeculative;
        ApplyState(state,true);
    }

    public void ApplyState(MineableOreRecord state, bool pose)
    {
        bool detaching=!Detached && state.Detached;
        Detached=state.Detached;
        // Existing saved worlds can contain giant 2.25 m-radius ore outcrops.
        // Cap their VISUAL/PHYSICAL radius without changing saved remaining resources.
        Radius=VisibleRadius(state.Radius,_world.oreChunkMaximumRadius);
        RemainingUnits=state.RemainingUnits; TotalUnits=state.TotalUnits;
        float fraction=TotalUnits>0d?Mathf.Clamp01((float)(RemainingUnits/TotalUnits)):0f;
        transform.localScale=Vector3.one*(Radius*Mathf.Pow(Mathf.Max(0.0001f,fraction),1f/3f));
        if (pose || !ServerOwned)
            transform.SetPositionAndRotation(state.Position,state.Rotation);
        if (Body != null)
        {
            Body.mass=Mathf.Clamp((float)RemainingUnits*12f,1f,1200f);
            Body.isKinematic=!ServerOwned || !Detached;
            Body.useGravity=ServerOwned && Detached;
            if (detaching && ServerOwned)
            {
                DebrisPool.SetLinearVelocity(Body,state.Velocity);
                Body.angularVelocity=state.AngularVelocity;
                Body.WakeUp();
            }
        }
    }

    public void ApplyPose(Vector3 position, Quaternion rotation)
    {
        if (!ServerOwned) transform.SetPositionAndRotation(position,rotation);
    }

    // Legacy saved nodes may store much larger radii. Visual/physics sizing
    // is capped while inventory yield is left untouched and never awarded twice.
    public static float VisibleRadius(float savedRadius,float maxVisibleRadius)
    {
        return Mathf.Min(Mathf.Max(0.01f,savedRadius),Mathf.Max(0.15f,maxVisibleRadius));
    }

    public Vector3 Velocity => Body==null ? Vector3.zero : ReadVelocity(Body);
    public static Vector3 ReadVelocity(Rigidbody body)
    {
#if UNITY_6000_0_OR_NEWER
        return body.linearVelocity;
#else
        return body.velocity;
#endif
    }

    private static Mesh MakeBoulderMesh(long key)
    {
        const int rings=9, sides=14;
        var vertices=new Vector3[(rings+1)*(sides+1)];
        var triangles=new int[rings*sides*6];
        for (int y=0; y<=rings; y++)
        for (int x=0; x<=sides; x++)
        {
            float v=(float)y/rings,u=(float)x/sides;
            float theta=v*Mathf.PI,phi=u*Mathf.PI*2f;
            Vector3 direction=new Vector3(Mathf.Sin(theta)*Mathf.Cos(phi),Mathf.Cos(theta),Mathf.Sin(theta)*Mathf.Sin(phi));
            // Periodic at the longitude seam. Seeded noise is stable across reloads/clients.
            int column=x%sides;
            float rough=0.84f+0.30f*Noise(key,y,column);
            vertices[y*(sides+1)+x]=direction*rough;
        }
        int idx=0;
        for (int y=0;y<rings;y++) for (int x=0;x<sides;x++)
        {
            int a=y*(sides+1)+x,b=a+sides+1;
            triangles[idx++]=a;triangles[idx++]=a+1;triangles[idx++]=b;
            triangles[idx++]=a+1;triangles[idx++]=b+1;triangles[idx++]=b;
        }
        var mesh=new Mesh { name="Gobmine low-poly mineable ore" };
        mesh.vertices=vertices;mesh.triangles=triangles;
        mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
    }
    private static float Noise(long key,int row,int column)
    {
        unchecked
        {
            ulong h=(ulong)key ^ ((ulong)(row+1)*0x9e3779b97f4a7c15ul) ^ ((ulong)(column+1)*0xbf58476d1ce4e5b9ul);
            h^=h>>30;h*=0xbf58476d1ce4e5b9ul;h^=h>>27;h*=0x94d049bb133111ebul;h^=h>>31;
            return (float)((h&0xfffffful)/16777216.0);
        }
    }
    private void OnDestroy() {if(_mesh!=null)Destroy(_mesh);}
}

/// <summary>Single authoritative record per deterministic ore body; never respawn depleted IDs.</summary>
[System.Serializable]
public sealed class MineableOreRecord
{
    public long Id;
    public int MaterialId;
    public Vector3Int GeneratorCell;
    public Vector3Int AnchorSample;
    public Vector3 Position;
    public Quaternion Rotation=Quaternion.identity;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public float Radius;
    public double TotalUnits;
    public double RemainingUnits;
    public bool Detached;
    public MineableOreRecord Copy() => (MineableOreRecord)MemberwiseClone();
}
