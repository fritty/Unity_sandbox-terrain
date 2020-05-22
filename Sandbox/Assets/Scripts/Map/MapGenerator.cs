﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Threading;

/* Handles generation of chunks */
public class MapGenerator : MonoBehaviour {

    [Header ("Genrators Settings")]
    public float noiseScale = Chunk.size.height;
    public float noiseFrequency = 0.025f;
    public ComputeShader mapShader;

    Queue<GeneratedDataInfo<MapData>> mapDataQueue = new Queue<GeneratedDataInfo<MapData>>();
    Queue<Vector3Int> requestedCoords = new Queue<Vector3Int>();

    int maxThreadsPerUpdate = 8;

    // Set up from map
    Action<GeneratedDataInfo<MapData>> mapCallback;
    Transform viewer;
    int viewDistance;


    void FixedUpdate () {
        ManageRequests();
    }

    /* Interface */
    public void RequestMapData (Vector3Int coord) {
		requestedCoords.Enqueue(coord);
	}

    public void SetMapCallback (Action<GeneratedDataInfo<MapData>> callback) {
        mapCallback = callback;
    }

    public void SetViewer (Transform viewer) {
        this.viewer = viewer;
    }

    public void SetViewDistance (int viewDistance) {
        this.viewDistance = viewDistance;
    }
    
    // Generation thread
	void MapDataThread (Vector3Int coord) {
		MapData mapData = Generate(coord);
		lock (mapDataQueue) {
			mapDataQueue.Enqueue (new GeneratedDataInfo<MapData>(mapData, coord));
		}
	}   

    void ManageRequests () {
        // Return requested data
        if (mapDataQueue.Count > 0) {
			for (int i = 0; i < mapDataQueue.Count; i++) {
				GeneratedDataInfo<MapData> mapData = mapDataQueue.Dequeue ();
				mapCallback (mapData);
			}
		}    

        // Go through requested coordinates and start generation threads if still relevant
        if (requestedCoords.Count > 0) {
            Vector3Int viewerCoord = new Vector3Int(Mathf.RoundToInt(viewer.position.x / Chunk.size.width), 0, Mathf.RoundToInt(viewer.position.z / Chunk.size.width));
            int maxThreads = Mathf.Min(maxThreadsPerUpdate, requestedCoords.Count);
            for (int i = 0; i < maxThreads && requestedCoords.Count > 0; i++) {
                Vector3Int coord = requestedCoords.Dequeue();

                // skip outdated coordinates
                while ((Mathf.Abs(coord.x - viewerCoord.x) > viewDistance || Mathf.Abs(coord.z - viewerCoord.z) > viewDistance) && requestedCoords.Count > 0) {
                    coord = requestedCoords.Dequeue();
                }
                
                if (Mathf.Abs(coord.x - viewerCoord.x) <= viewDistance && Mathf.Abs(coord.z - viewerCoord.z) <= viewDistance) {
                    ThreadStart threadStart = delegate {
                        MapDataThread (coord);
                    };
                    new Thread (threadStart).Start ();
                }
            }                
        }
    }


    ////////////////////
    /* Map generators */
    ////////////////////

    MapData Generate (Vector3Int coord) {
        Vector3 origin = OriginFromCoord (coord);
        //return new MapData(FlatGen(origin), coord);
        //return GradGen(origin);
        return new MapData(PerlinGen(origin));
        //return new MapData(ShaderGen(origin), coord);
    }

    byte[,,] FlatGen (Vector3 origin){
        byte[,,] blocks = new byte[Chunk.size.width, Chunk.size.height, Chunk.size.width];
        Vector3 tilt = new Vector3((origin.x/32 + 120)/256,0,0);
        float val;

        for (byte x = 0; x < Chunk.size.width; x++){
            for (byte z = 0; z < Chunk.size.width; z++){     
                val = tilt.x*x + tilt.z*z;           
                for (byte y = 0; y < Chunk.size.height; y++){                    
                    blocks[z,y,x] = HeightToByte(y, val);
                }
            }
        }
        return blocks;
    }

    byte[,,] GradGen (ComputeBuffer pointsBuffer, Vector3 origin) {
        byte[,,] blocks = new byte[Chunk.size.width, Chunk.size.height, Chunk.size.width];
        float xAbs;
        //float zAbs;
        float val;

        for (byte x = 0; x < Chunk.size.width; x++){
            for (byte z = 0; z < Chunk.size.width; z++){  
                xAbs = origin.x + x;
                //zAbs = origin.z + z;
                val = (noiseScale-2) * Mathf.Pow(Mathf.Sin(xAbs*noiseFrequency),2);              
                for (byte y = 0; y < Chunk.size.height; y++){                     
                    blocks[z,y,x] = HeightToByte(y, val);
                }
            }
        }
        return blocks;        
    }

    byte[,,] PerlinGen (Vector3 origin) {
        byte[,,] blocks = new byte[Chunk.size.width, Chunk.size.height, Chunk.size.width];        
        Vector2 noiseOffset = new Vector2(500, 500);
        float noise;

        for (byte x = 0; x < Chunk.size.width; x++){
            for (byte z = 0; z < Chunk.size.width; z++){
                noise = noiseScale * Mathf.PerlinNoise((x + origin.x) * noiseFrequency + noiseOffset.x, (z + origin.z) * noiseFrequency + noiseOffset.y);
                for (byte y = 0; y < Chunk.size.height; y++){
                    blocks[z,y,x] = HeightToByte(y, noise);
                }            
            }              
        }
        return blocks;
    }

    byte HeightToByte(byte y, float val) {
        if (y == Mathf.RoundToInt(val))
            if (y > val)
                return (byte)(Mathf.RoundToInt(255 *  (val - Mathf.Floor(val))) - 127);
            else
                return (byte)(Mathf.RoundToInt(255 *  (val + 1 - Mathf.Floor(val))) - 127);
        if (y > val)
            return 0;
        else
            return 255; 
    }

    byte[,,] ShaderGen (Vector3 origin){
        byte[,,] blocks = new byte[Chunk.size.width, Chunk.size.height, Chunk.size.width];
        ComputeBuffer pointsBuffer = new ComputeBuffer(Chunk.size.width * Chunk.size.height * Chunk.size.width / 4, 4);
        int kernelHandle = mapShader.FindKernel("MapGen");
        int threadsPerAxis = 8; 
        
        mapShader.SetBuffer (kernelHandle, "points", pointsBuffer);
        mapShader.SetInt ("Width", Chunk.size.width);
        mapShader.SetFloat ("Height", Chunk.size.height);  
        
        mapShader.Dispatch (kernelHandle, Chunk.size.width / (threadsPerAxis*4), Chunk.size.height / (threadsPerAxis), Chunk.size.width);

        pointsBuffer.GetData(blocks);
        pointsBuffer.Release();
        return blocks;
    }      

    Vector3 OriginFromCoord (Vector3Int coord) {
        return new Vector3 (coord.x * Chunk.size.width, coord.y * Chunk.size.height, coord.z * Chunk.size.width);
    } 
}

// container for chunk map data
public struct MapData {
	public readonly byte[,,] blocks;

	public MapData (byte[,,] blocks)
	{
		this.blocks = blocks;
	}
}