﻿using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Leap.Unity;
using Leap.Unity.Attributes;
using Leap.Unity.RuntimeGizmos;

public abstract class ParticleEngineBase : MonoBehaviour {

  [Header("Simulation")]
  [MinValue(0)]
  [EditTimeOnly]
  [SerializeField]
  private int _maxParticles = 1024;

  [SerializeField]
  private bool _useMultithreading = false;

  [Header("Collision Chunks")]
  [EditTimeOnly]
  [SerializeField]
  private float _chunkSize = 0.1f;

  [EditTimeOnly]
  [SerializeField]
  private ChunkResolution _chunkResolution = ChunkResolution.Sixteen;

  [Header("Rendering")]
  [SerializeField]
  private DisplayMethod _renderMethod = DisplayMethod.DrawMesh;

  [SerializeField]
  private Mesh _particleMesh;

  [SerializeField]
  private Material _displayMaterial;

  //Particle simulation data
  private Particle[] _particlesBack;
  private Particle[] _particlesFront;
  private int[] _chunkCount;
  private int _aliveParticles = 0;
  private Queue<Particle> _toEmit = new Queue<Particle>();

  private SpeciesData[] _speciesData;
  private SocialData[] _socialData;

  //Threading
  private ParallelForeach _integrationForeach;
  private ParallelForeach _sortingForeach;
  private ParallelForeach _resolveCollisionsForeach;

  //Collision acceleration structures
  private int _chunkSide;
  private int _numChunks;
  private Vector3 _collisionOffset;
  private Vector3 _collisionSize;

  private int[] _chunkStart;
  private int[] _chunkEnd;

  //Rendering
  private Matrix4x4[] _instanceMatrices;
  private Color[] _randomColors;

  //Timing
  private long[] integrationTimes = new long[32];
  private long[] collisionTimes = new long[32];
  private long[] sortingTimes = new long[32];
  private System.Diagnostics.Stopwatch _stopwatch = new System.Diagnostics.Stopwatch();

  public struct Particle {
    public Vector3 position;
    public Vector3 prevPosition;
    public int species;
  }

  public struct SpeciesData {

  }

  public struct SocialData {

  }

  public enum ChunkResolution {
    Four = 4,
    Eight = 8,
    Sixteen = 16,
    ThirtyTwo = 32,
    SixyFour = 64
  }

  public enum DisplayMethod {
    DrawMesh,
    DrawMeshInstanced
  }

  #region UNITY MESSAGES

  protected virtual void Awake() {
    _stopwatch.Start();

    int cores = SystemInfo.processorCount;
    _integrationForeach = new ParallelForeach(integrateParticles, cores);
    _sortingForeach = new ParallelForeach(sortParticlesIntoChunks, cores);
    _resolveCollisionsForeach = new ParallelForeach(resolveCollisions, cores);

    _integrationForeach.OnComplete += () => {
      emitParticles();
      accumulateCollisionChunksNaive();
      _sortingForeach.Dispatch(_aliveParticles);
    };

    _sortingForeach.OnComplete += () => {
      _resolveCollisionsForeach.Dispatch(_aliveParticles);
    };

    _chunkSide = (int)_chunkResolution;
    _numChunks = _chunkSide * _chunkSide * _chunkSide;

    _instanceMatrices = new Matrix4x4[_maxParticles];
    _particlesBack = new Particle[_maxParticles];
    _particlesFront = new Particle[_maxParticles];
    _speciesData = new SpeciesData[1];

    _chunkCount = new int[_numChunks];
    _chunkStart = new int[_numChunks];
    _chunkEnd = new int[_numChunks];

    _randomColors = new Color[_numChunks];
    for (int i = 0; i < _numChunks; i++) {
      _randomColors[i] = Color.HSVToRGB(Random.value, Random.Range(0.5f, 1), Random.Range(0.5f, 1f));
    }
  }

  protected virtual void Update() {
    System.Array.Clear(integrationTimes, 0, integrationTimes.Length);
    System.Array.Clear(collisionTimes, 0, collisionTimes.Length);
    System.Array.Clear(sortingTimes, 0, sortingTimes.Length);

    System.Array.Clear(_chunkCount, 0, _chunkCount.Length);

    if (_useMultithreading) {
      using (new ProfilerSample("Dispatch Simulation Jobs")) {
        _integrationForeach.Dispatch(_aliveParticles);
      }
    } else {
      using (new ProfilerSample("Integrate Particles")) {
        integrateParticles(0, 0, _aliveParticles);
      }

      using (new ProfilerSample("Emit Particles")) {
        emitParticles();
      }

      using (new ProfilerSample("Accumulate Collision Chunks")) {
        accumulateCollisionChunksNaive();
      }

      using (new ProfilerSample("Sort Particles Into Chunks")) {
        sortParticlesIntoChunks(0, 0, _aliveParticles);
      }

      using (new ProfilerSample("Resolve Collisions")) {
        resolveCollisions(0, 0, _aliveParticles);
      }
    }
  }

  protected virtual void LateUpdate() {
    if (_useMultithreading) {
      using (new ProfilerSample("Wait For Simulation Jobs")) {
        _integrationForeach.Wait();
        _sortingForeach.Wait();
        _resolveCollisionsForeach.Wait();
      }
    }

    MaterialPropertyBlock block = new MaterialPropertyBlock();
    Matrix4x4 matrix;

    using (new ProfilerSample("Draw Particles")) {
      switch (_renderMethod) {
        case DisplayMethod.DrawMesh:
          for (int i = 0; i < _aliveParticles; i++) {
            matrix = Matrix4x4.TRS(_particlesFront[i].position, Quaternion.identity, Vector3.one * 0.05f);

            int chunk = getChunk(ref _particlesFront[i]);
            block.SetColor("_Color", _randomColors[chunk]);

            //int chunkStart = _chunkStart[chunk];
            //int chunkEnd = _chunkEnd[chunk];
            //block.SetColor("_Color", (i >= chunkStart && i < chunkEnd) ? Color.green : Color.red);

            //float p = i / (float)_aliveParticles;
            //block.SetColor("_Color", new Color(p, p, p));

            Graphics.DrawMesh(_particleMesh, matrix, _displayMaterial, 0, null, 0, block);
            //Graphics.DrawMesh(_particleMesh, matrix, _displayMaterial, 0);
          }
          break;
        case DisplayMethod.DrawMeshInstanced:
          int remaining = _aliveParticles;
          int index = 0;

          matrix = Matrix4x4.identity;
          matrix[0, 0] = 0.05f;
          matrix[1, 1] = 0.05f;
          matrix[2, 2] = 0.05f;

          while (remaining > 0) {
            block.SetColor("_Color", _randomColors[index]);

            int toDraw = Mathf.Min(1023, remaining);
            using (new ProfilerSample("Copy Particle Positions")) {
              for (int i = 0; i < toDraw; i++) {
                matrix[0, 3] = _particlesFront[index].position.x;
                matrix[1, 3] = _particlesFront[index].position.y;
                matrix[2, 3] = _particlesFront[index].position.z;
                _instanceMatrices[i] = matrix;
                index++;
                //_instanceMatrices[i] = Matrix4x4.TRS(_particlesFront[index++].position, Quaternion.identity, Vector3.one * 0.05f);
              }
              remaining -= toDraw;
            }

            using (new ProfilerSample("Draw Mesh Instanced")) {
              Graphics.DrawMeshInstanced(_particleMesh, 0, _displayMaterial, _instanceMatrices, toDraw, block);
              //Graphics.DrawMeshInstanced(_particleMesh, 0, _displayMaterial, _instanceMatrices, toDraw);
            }
          }
          break;
      }
    }
  }

  private void OnGUI() {
    Matrix4x4 ogScale = GUI.matrix;
    GUI.matrix = ogScale * Matrix4x4.Scale(Vector3.one * 5);

    GUILayout.Label("Cores: " + SystemInfo.processorCount);
    GUILayout.Label("Particles: " + _aliveParticles);
    displayTimingData("Integration:", integrationTimes);
    displayTimingData("Collision:", collisionTimes);
    displayTimingData("Sorting:", sortingTimes);
    GUILayout.Space(50);
    _useMultithreading = GUILayout.Toggle(_useMultithreading, "Multithreading " + (_useMultithreading ? "enabled" : "disabled"));

    GUI.matrix = ogScale;
  }

  private void displayTimingData(string label, long[] data) {
    GUILayout.Label(label);
    long totalTicks = 0;
    for (int i = 0; i < data.Length; i++) {
      long ticks = data[i];
      totalTicks += ticks;

      if (ticks != 0) {
        float ms = ticks * 1000.0f / System.Diagnostics.Stopwatch.Frequency;
        ms = Mathf.Round(ms * 10) / 10.0f;
        //GUILayout.Label("  Thread " + i + ": " + ms + "ms");
      }
    }

    float totalMs = totalTicks * 1000.0f / System.Diagnostics.Stopwatch.Frequency;
    totalMs = Mathf.Round(totalMs * 10) / 10.0f;
    GUILayout.Label("  Total: " + totalMs + "ms");
  }

  #endregion

  #region PUBLIC API

  public int aliveParticles {
    get {
      return _aliveParticles;
    }
  }

  public int maxParticles {
    get {
      return _maxParticles;
    }
  }

  public bool TryEmit(Particle particle) {
    if (_toEmit.Count + _aliveParticles >= _maxParticles) {
      return false;
    } else {
      _toEmit.Enqueue(particle);
      return true;
    }
  }

  /*
  protected abstract void DoParticleCollision(ref Particle particle,
                                              ref SpeciesData speciesData,
                                              ref Particle other,
                                              ref SpeciesData otherSpeciesData,
                                              ref Vector3 totalDisplacement,
                                              ref int totalCollisions);
                                              */

  protected abstract bool DoParticleInteraction(ref Particle particle,
                                                ref SpeciesData speciesData,
                                                ref Particle other,
                                                ref SpeciesData otherSpeciesData,
                                                ref Vector3 particleDisplacement);

  protected abstract void DoParticleGlobalForces(ref Particle particle,
                                                 ref SpeciesData speciesData);

  protected abstract void DoParticleConstraints(ref Particle particle,
                                                ref SpeciesData speciesData);

  protected abstract bool ShouldKillParticle(ref Particle particle);
  #endregion

  #region PRIVATE IMPLEMENTATION
  private void emitParticles() {
    while (_toEmit.Count > 0) {
      Particle toEmit = _toEmit.Dequeue();

      //Make sure to increment the count of the chunk that we are emitting into
      int emitChunk = getChunk(ref toEmit);
      _chunkCount[emitChunk]++;

      //Plop the particle onto the end of the front array, will be sorted into
      //the right chunk by the next accumulate/sort cycle
      _particlesFront[_aliveParticles++] = toEmit;
    }
  }

  private void integrateParticles(int workerIndex, int startIndex, int endIndex) {
    long startTick = _stopwatch.ElapsedTicks;
    for (int i = startIndex; i < endIndex; i++) {
      integrateParticle(i, ref _particlesFront[i], ref _speciesData[_particlesFront[i].species]);
    }
    integrationTimes[workerIndex] = _stopwatch.ElapsedTicks - startTick;
  }

  private void integrateParticle(int index, ref Particle particle, ref SpeciesData speciesData) {
    Vector3 originalPos = particle.position;

    particle.position.x += 0.99f * (particle.position.x - particle.prevPosition.x);
    particle.position.y += 0.99f * (particle.position.y - particle.prevPosition.y);
    particle.position.z += 0.99f * (particle.position.z - particle.prevPosition.z);

    DoParticleGlobalForces(ref particle, ref speciesData);

    //TODO: particle-particle forces

    particle.prevPosition = originalPos;

    int newChunk = getChunk(ref particle);
    if (newChunk < 0 || newChunk >= _chunkCount.Length) {
      Debug.Log(newChunk);
      Debug.Log(particle.position);
    }
    Interlocked.Add(ref _chunkCount[newChunk], 1);
  }

  private void accumulateCollisionChunksNaive() {
    int sum = 0;
    for (int i = 0; i < _numChunks; i++) {
      sum += _chunkCount[i];
      _chunkStart[i] = _chunkEnd[i] = sum;
    }
  }

  private void sortParticlesIntoChunks(int workerIndex, int startIndex, int endIndex) {
    long startTick = _stopwatch.ElapsedTicks;
    for (int i = startIndex; i < endIndex; i++) {
      sortParticleIntoChunk(i, ref _particlesFront[i]);
    }
    sortingTimes[workerIndex] = _stopwatch.ElapsedTicks - startTick;
  }

  private void sortParticleIntoChunk(int index, ref Particle particle) {
    int chunk = getChunk(ref particle);

    int newIndex = Interlocked.Add(ref _chunkStart[chunk], -1);
    _particlesBack[newIndex] = particle;
  }

  private void resolveCollisions(int workerIndex, int startIndex, int endIndex) {
    long startTick = _stopwatch.ElapsedTicks;
    for (int i = startIndex; i < endIndex; i++) {
      _particlesFront[i] = _particlesBack[i];
      resolveCollisions(i, ref _particlesFront[i], ref _speciesData[_particlesFront[i].species]);
    }
    collisionTimes[workerIndex] = _stopwatch.ElapsedTicks - startTick;
  }

  public bool use2x2 = true;
  private void resolveCollisions(int index, ref Particle particle, ref SpeciesData speciesData) {
    resolveParticleCollisions2x2(index, ref particle, ref speciesData);

    Vector3 unclamped = particle.position;
    DoParticleConstraints(ref particle, ref speciesData);
    particle.position = unclamped + 2 * (particle.position - unclamped);
  }

  private void resolveParticleCollisionsNaive(int index, ref Particle particle, ref SpeciesData speciesData) {
    Vector3 totalDepenetration = Vector3.zero;
    int numCollisions = 0;

    resolveParticleCollisions(0, aliveParticles, index, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    if (numCollisions > 0) {
      particle.position += totalDepenetration / numCollisions;
    }
  }

  private void resolveParticleCollisions2x2(int index, ref Particle particle, ref SpeciesData speciesData) {
    int numCollisions = 0;
    Vector3 totalDepenetration = Vector3.zero;

    Vector3 chunkFloatPos = particle.position / _chunkSize + Vector3.one * _chunkSide * 0.5f;
    int chunkX = (int)chunkFloatPos.x;
    int chunkY = (int)chunkFloatPos.y;
    int chunkZ = (int)chunkFloatPos.z;

    chunkX += (chunkFloatPos.x - (int)chunkFloatPos.x < 0.5) ? -1 : 0;
    chunkY += (chunkFloatPos.y - (int)chunkFloatPos.y < 0.5) ? -1 : 0;
    chunkZ += (chunkFloatPos.z - (int)chunkFloatPos.z < 0.5) ? -1 : 0;

    int chunk = chunkX + chunkY * _chunkSide + chunkZ * _chunkSide * _chunkSide;
    int chunkA_Start = _chunkStart[chunk];
    int chunkA_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkA_Start, chunkA_End, index, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk += _chunkSide;
    int chunkB_Start = _chunkStart[chunk];
    int chunkB_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkB_Start, chunkB_End, index, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk += (_chunkSide * _chunkSide);
    int chunkC_Start = _chunkStart[chunk];
    int chunkC_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkC_Start, chunkC_End, index, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk -= _chunkSide;
    int chunkD_Start = _chunkStart[chunk];
    int chunkD_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkD_Start, chunkD_End, index, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    if (numCollisions > 0) {
      particle.position += totalDepenetration / numCollisions;
    }
  }

  private void resolveParticleCollisions(int start,
                                         int end,
                                         int toSkip,
                                     ref Particle particle,
                                     ref SpeciesData speciesData,
                                     ref Vector3 totalDepenetration,
                                     ref int numCollisions) {

    if (toSkip >= start && toSkip <= end) {
      resolveParticleCollisions(start, toSkip, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);
      resolveParticleCollisions(toSkip + 1, end, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);
    } else {
      resolveParticleCollisions(start, end, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);
    }
  }

  private void resolveParticleCollisions(int start,
                                         int end,
                                     ref Particle particle,
                                     ref SpeciesData speciesData,
                                     ref Vector3 totalDepenetration,
                                     ref int numCollisions) {
    for (int i = start; i < end; i++) {
      float dx = particle.position.x - _particlesBack[i].position.x;
      float dy = particle.position.y - _particlesBack[i].position.y;
      float dz = particle.position.z - _particlesBack[i].position.z;
      float sqrDist = dx * dx + dy * dy + dz * dz;

      if (sqrDist < 0.05f * 0.05f && sqrDist > 0.000000001f) {
        float dist = Mathf.Sqrt(sqrDist);
        float constant = -0.5f * (dist - 0.05f) / dist;
        totalDepenetration.x += dx * constant;
        totalDepenetration.y += dy * constant;
        totalDepenetration.z += dz * constant;
        numCollisions++;
      }
    }
  }

  private int getChunk(ref Particle particle) {
    Vector3 floatPos = particle.position / _chunkSize + Vector3.one * _chunkSide * 0.5f;
    return getChunk(new ChunkIndex(floatPos));
  }

  private int getChunk(ChunkIndex index) {
    return index.x + index.y * _chunkSide + index.z * _chunkSide * _chunkSide;
  }

  private int getChunkAtOffset(ChunkIndex index, int offsetX, int offsetY, int offsetZ) {
    return index.x + offsetX + (index.y + offsetY) * _chunkSide + (index.z + offsetZ) * _chunkSide * _chunkSide;
  }


  private float frac(float value) {
    return value - (int)value;
  }

  private struct ChunkIndex {
    public int x, y, z;

    public ChunkIndex(Vector3 position) {
      x = (int)position.x;
      y = (int)position.y;
      z = (int)position.z;
    }

    public static ChunkIndex operator +(ChunkIndex a, ChunkIndex b) {
      return new ChunkIndex() { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
    }
  }
  #endregion
}