#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class GobmineOreChunkRuleTests
{
    [MenuItem("Tools/Gobmine/Ore Chunks/Run Generation and Save Tests")]
    public static void Run()
    {
        int passed=0;
        try
        {
            Vector3Int p=new Vector3Int(-48,12,100);
            long id=OreChunkGenerator.StableId(p,4);
            Require(id>0 && id==OreChunkGenerator.StableId(p,4),"stable positive ID");passed++;
            Require(id!=OreChunkGenerator.StableId(p,5) && id!=OreChunkGenerator.StableId(p+Vector3Int.right,4),"ID distinguishes cell and material");passed++;
            var catalogue=ScriptableObject.CreateInstance<TerrainMaterialCatalog>();
            var stone=ScriptableObject.CreateInstance<TerrainMaterialDefinition>();
            var ore=ScriptableObject.CreateInstance<TerrainMaterialDefinition>();
            stone.materialId=1;
            ore.materialId=2;ore.spawnMineableChunks=true;ore.generateDeposits=true;
            ore.depositGridSizeInCells=20;ore.depositChancePerThousand=1000;
            ore.minimumRadiusInCells=2;ore.maximumRadiusInCells=4;
            ore.minimumCellY=-10000;ore.maximumCellY=10000;
            catalogue.baseStoneId=1;catalogue.definitions=new[]{stone,ore};
            var generator=new OreChunkGenerator(1729,0.25f,0f,catalogue);
            var first=new List<OreChunkGenerator.Candidate>();var second=new List<OreChunkGenerator.Candidate>();
            generator.GetNear(new Vector3(-1f,-20f,2f),9f,first);
            generator.GetNear(new Vector3(-1f,-20f,2f),9f,second);
            Require(first.Count>0 && first.Count==second.Count,"nonempty deterministic generation");passed++;
            Require(OreChunkGenerator.IsProtectedWorkshopPosition(new Vector3(0f,-3f,0f),4f) &&
                !OreChunkGenerator.IsProtectedWorkshopPosition(new Vector3(6f,-3f,0f),4f) &&
                !OreChunkGenerator.IsProtectedWorkshopPosition(new Vector3(0f,-20f,0f),4f) &&
                !OreChunkGenerator.IsProtectedWorkshopPosition(new Vector3(0f,-3f,0f),0f),
                "workshop protection is a small configurable circle"); passed++;
            for(int i=0;i<first.Count;i++)
            {
                Require(first[i].Id==second[i].Id && first[i].Centre==second[i].Centre && first[i].Radius==second[i].Radius,"repeatable candidate geometry");
                Require(first[i].Radius>=0.28f && first[i].Radius<=0.65f,"ore radius is small and bounded");
                if(i>0)
                {
                    float prev=(first[i-1].Centre-new Vector3(-1f,-20f,2f)).sqrMagnitude;
                    float curr=(first[i].Centre-new Vector3(-1f,-20f,2f)).sqrMagnitude;
                    Require(prev<=curr+0.0001f,"nearby ore discovered in nearest-first order");
                }
            }
            passed+=3;
            var smaller=new OreChunkGenerator(1729,0.25f,0f,catalogue,0.18f,0.40f);
            var adjusted=new List<OreChunkGenerator.Candidate>();
            smaller.GetNear(new Vector3(-1f,-20f,2f),9f,adjusted);
            // The edge of the search radius can include different objects when their
            // radii change. Compare identities and centres only where both queries agree.
            var baselineById=new Dictionary<long,Vector3>();
            foreach(var node in first) baselineById[node.Id]=node.Centre;
            int compared=0;
            foreach(var node in adjusted)
            {
                Vector3 oldCentre;
                if (!baselineById.TryGetValue(node.Id,out oldCentre)) continue;
                Require(node.Centre==oldCentre && node.Radius<=0.40f,
                    "size controls do not move existing geological candidates");
                compared++;
            }
            Require(compared>0,"two radius configurations share candidate IDs");
            passed++;
            var nearby=first[0];
            Require(OreChunkGenerator.CouldBrushReveal(nearby,nearby.Centre,0.4f,0.25f),"near brush prewarms ore");
            Require(!OreChunkGenerator.CouldBrushReveal(nearby,nearby.Centre+Vector3.right*40f,0.4f,0.25f),
                "far brush does not materialize ore");
            passed++;
            Require(Mathf.Approximately(MineableOreChunk.VisibleRadius(2.25f,0.65f),0.65f),
                "oversized saved node is visually capped");
            passed++;
            // Copper must be eligible at shallow depths. Discoverability is separate
            // from whether its host-rock location has been exposed by a player.
            ore.useDepthBand=true;ore.minimumDepthMetres=0f;ore.maximumDepthMetres=20f;
            var copperGenerator=new OreChunkGenerator(1729,0.25f,0f,catalogue);
            var earlyCopper=new List<OreChunkGenerator.Candidate>();
            copperGenerator.GetNear(new Vector3(13f,-3f,0f),12f,earlyCopper);
            Require(earlyCopper.Exists(c => c.Centre.y<=0f && c.Centre.y>=-20f),
                "copper generation remains eligible at depth 3 metres");passed++;
            ore.useDepthBand=true;ore.minimumDepthMetres=60f;ore.maximumDepthMetres=120f;
            var depthGenerator=new OreChunkGenerator(1729,0.25f,0f,catalogue);
            var shallow=new List<OreChunkGenerator.Candidate>();var deep=new List<OreChunkGenerator.Candidate>();
            depthGenerator.GetNear(new Vector3(0f,-10f,0f),8f,shallow);
            depthGenerator.GetNear(new Vector3(0f,-80f,0f),8f,deep);
            Require(shallow.Count==0 && deep.Count>0,"ore chunks respect metre-based depth bands");passed++;
            var terrain=new TerrainMaterialGenerator(1729,catalogue,0.25f,0f);
            Require(terrain.GetSolidMaterial(new Vector3Int(1,-80,1))==1,"chunk-mode ores are excluded from old terrain veins");passed++;
            var state=new MineableOreRecord { Id=OreChunkGenerator.StableId(p,2),MaterialId=2,
                GeneratorCell=p,AnchorSample=p+Vector3Int.up,Position=new Vector3(1,2,3),
                Radius=0.85f,TotalUnits=31.75d,RemainingUnits=10.25d,Detached=true };
            var snapshot=new MaterialWorldPersistence.Snapshot {
                Seed=1729,ChunkSize=new Vector3Int(16,16,16),VoxelSize=0.25f,
                RulesSignature=new string('a',64)
            };
            snapshot.OreChunks.Add(state);
            byte[] encoded=MaterialWorldPersistence.Encode(snapshot);
            var decoded=MaterialWorldPersistence.Decode(encoded);
            Require(decoded.OreChunks.Count==1 && decoded.OreChunks[0].Id==state.Id &&
                decoded.OreChunks[0].RemainingUnits==10.25d && decoded.OreChunks[0].Detached,"V4 ore save roundtrip");passed++;
            // Old format remains decodable, with no new resources invented.
            snapshot.OreChunks.Clear();
            var old=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(snapshot));
            Require(old.OreChunks.Count==0,"old saves remain readable");passed++;
            var oldShape=new TerrainShapeRecord{Id=17,Ceiling=false,Position=new Vector3(2f,3f,4f)};
            oldShape.Samples.Add(new TerrainShapeSample(new Vector3Int(4,5,6),-1f));
            snapshot.Shapes.Add(oldShape);
            var previousFormat=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(snapshot));
            Require(previousFormat.Shapes.Count==1 && previousFormat.OreChunks.Count==0,"V3 detached-shape saves remain readable");passed++;
            UnityEngine.Object.DestroyImmediate(stone);UnityEngine.Object.DestroyImmediate(ore);
            UnityEngine.Object.DestroyImmediate(catalogue);
            Debug.Log("[Gobmine Ore Chunks] PASS: "+passed+" rule groups (not a Unity physics/multiplayer test).");
        }
        catch(Exception ex){Debug.LogError("[Gobmine Ore Chunks] FAILED after "+passed+" checks: "+ex);}
    }
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
}
#endif
