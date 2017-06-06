﻿using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using Leap.Unity;
using Leap.Unity.Attributes;
using Leap.Unity.RuntimeGizmos;
using System.Linq;

public abstract partial class ParticleEngine : MonoBehaviour, IRuntimeGizmoComponent {
  private const int CHUNK_SIDE = 32;
  private const int CHUNK_SIDE_SQRD = CHUNK_SIDE * CHUNK_SIDE;
  private const int HALF_CHUNK_SIDE = CHUNK_SIDE / 2;
  private const int NUM_CHUNKS = CHUNK_SIDE * CHUNK_SIDE * CHUNK_SIDE;
  private const float CHUNK_SIZE = PARTICLE_DIAMETER;// PARTICLE_RADIUS * 4;

  private const float PARTICLE_DIAMETER = PARTICLE_RADIUS * 2;

  [Header("Simulation")]
  [MinValue(0)]
  [EditTimeOnly]
  [SerializeField]
  private int _maxParticles = 1024;

  [SerializeField]
  private bool _useMultithreading = false;

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
  private int _aliveParticles = 0;
  private Queue<Particle> _toEmit = new Queue<Particle>();

  private SpeciesData[] _speciesData;
  private SocialData[] _socialData;

  //Threading
  private ParallelForeach _integrationForeach;
  private ParallelForeach _sortingForeach;
  private ParallelForeach _resolveCollisionsForeach;

  //Collision acceleration structures
  private int[] _chunkCount;
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

  //Drawing
  protected RuntimeGizmoDrawer drawer;

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
      System.Array.Copy(_particlesBack, _particlesFront, _particlesFront.Length);
      _resolveCollisionsForeach.Dispatch(_aliveParticles);
    };

    ResetSimulation();
  }

  protected virtual void Update() {
    RuntimeGizmoManager.TryGetGizmoDrawer(out drawer);

    BeforeParticleUpdate();

    considerCount = 0;
    collisionCount = 0;
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

      System.Array.Copy(_particlesBack, _particlesFront, _particlesFront.Length);

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
            matrix = Matrix4x4.TRS(_particlesFront[i].position, Quaternion.identity, Vector3.one * PARTICLE_DIAMETER);
            Graphics.DrawMesh(_particleMesh, matrix, _displayMaterial, 0);
          }
          break;
        case DisplayMethod.DrawMeshInstanced:
          int remaining = _aliveParticles;
          int index = 0;

          matrix = Matrix4x4.identity;
          matrix[0, 0] = PARTICLE_DIAMETER;
          matrix[1, 1] = PARTICLE_DIAMETER;
          matrix[2, 2] = PARTICLE_DIAMETER;

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
    GUI.matrix = ogScale * Matrix4x4.Scale(Vector3.one * 1f);

    GUILayout.Label("Cores: " + SystemInfo.processorCount);
    GUILayout.Label("Particles: " + _aliveParticles);

    GUILayout.Label("Collision Tests: " + considerCount / (float)_aliveParticles);
    GUILayout.Label("Collisions: " + collisionCount / (float)_aliveParticles);

    GUILayout.Label("Max Count: " + _chunkCount.Max());

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

    //string threadLabel = "";
    for (int i = 0; i < data.Length; i++) {
      long ticks = data[i];
      totalTicks += ticks;

      if (ticks != 0) {
        float ms = ticks * 1000.0f / System.Diagnostics.Stopwatch.Frequency;
        ms = Mathf.Round(ms * 10) / 10.0f;

        //threadLabel = threadLabel + "".PadLeft(Mathf.RoundToInt(ms), "0123456789ABCDEFGH"[i]);
        GUILayout.Label("  Thread " + "0123456789ABCDEFGH"[i] + ": " + "".PadLeft(Mathf.RoundToInt(ms), '#'));
      }
    }

    float totalMs = totalTicks * 1000.0f / System.Diagnostics.Stopwatch.Frequency;
    totalMs = Mathf.Round(totalMs * 10) / 10.0f;
    //GUILayout.Label("  Threads: " + threadLabel);
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

  public void ResetSimulation() {
    _instanceMatrices = new Matrix4x4[_maxParticles];
    _particlesBack = new Particle[_maxParticles];
    _particlesFront = new Particle[_maxParticles];
    _speciesData = new SpeciesData[1];

    _chunkCount = new int[NUM_CHUNKS];
    _chunkStart = new int[NUM_CHUNKS];
    _chunkEnd = new int[NUM_CHUNKS];

    _randomColors = new Color[NUM_CHUNKS];
    for (int i = 0; i < NUM_CHUNKS; i++) {
      _randomColors[i] = Color.HSVToRGB(Random.value, Random.Range(0.5f, 1), Random.Range(0.5f, 1f));
    }

    OnInitializeSimulation();
  }

  public bool TryEmit(Particle particle) {
    if (_toEmit.Count + _aliveParticles >= _maxParticles) {
      return false;
    } else {
      _toEmit.Enqueue(particle);
      return true;
    }
  }

  protected abstract void OnInitializeSimulation();
  protected abstract void BeforeParticleUpdate();

  protected abstract void DoParticleSocialInteraction(ref Particle particle,
                                                      ref SpeciesData speciesData,
                                                      ref Particle other,
                                                      ref SpeciesData otherSpeciesData,
                                                      ref Vector3 totalSocialforce,
                                                      ref int totalSocialInteractions);

  protected abstract void DoParticleGlobalForces(ref Particle particle,
                                                 ref SpeciesData speciesData);

  protected abstract void DoParticleConstraints(ref Particle particle,
                                                ref SpeciesData speciesData);

  protected abstract bool ShouldKillParticle(ref Particle particle);

  public virtual void OnDrawRuntimeGizmos(RuntimeGizmoDrawer drawer) {

  }
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
    for (int i = 0; i < NUM_CHUNKS; ++i) {
      sum += _chunkCount[i];
      _chunkStart[i] = _chunkEnd[i] = sum;
    }
  }

  private void sortParticlesIntoChunks(int workerIndex, int startIndex, int endIndex) {
    long startTick = _stopwatch.ElapsedTicks;
    for (int i = startIndex; i < endIndex; ++i) {
      int chunk = getChunk(ref _particlesFront[i]);

      int newIndex = Interlocked.Add(ref _chunkStart[chunk], -1);
      _particlesBack[newIndex] = _particlesFront[i];
    }
    sortingTimes[workerIndex] = _stopwatch.ElapsedTicks - startTick;
  }

  private void resolveCollisions(int workerIndex, int startIndex, int endIndex) {
    long startTick = _stopwatch.ElapsedTicks;
    for (int i = startIndex; i < endIndex; ++i) {
      resolveCollisions(i, ref _particlesFront[i], ref _speciesData[_particlesFront[i].species]);
    }
    collisionTimes[workerIndex] = _stopwatch.ElapsedTicks - startTick;
  }

  private void resolveCollisions(int index, ref Particle particle, ref SpeciesData speciesData) {
    //resolveParticleCollisions2x2(index, ref particle, ref speciesData);
    resolveParticleCollisionsNaive(index, ref particle, ref speciesData);

    Vector3 unclamped = particle.position;

    particle.position.x = Mathf.Clamp(particle.position.x, -(HALF_CHUNK_SIDE - 2) * CHUNK_SIZE, (HALF_CHUNK_SIDE - 2) * CHUNK_SIZE);
    particle.position.y = Mathf.Clamp(particle.position.y, -(HALF_CHUNK_SIDE - 2) * CHUNK_SIZE, (HALF_CHUNK_SIDE - 2) * CHUNK_SIZE);
    particle.position.z = Mathf.Clamp(particle.position.z, -(HALF_CHUNK_SIDE - 2) * CHUNK_SIZE, (HALF_CHUNK_SIDE - 2) * CHUNK_SIZE);

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

    float chunkFloatX = particle.position.x / CHUNK_SIZE + HALF_CHUNK_SIDE;
    float chunkFloatY = particle.position.y / CHUNK_SIZE + HALF_CHUNK_SIDE;
    float chunkFloatZ = particle.position.z / CHUNK_SIZE + HALF_CHUNK_SIDE;

    int chunkX = (int)chunkFloatX;
    int chunkY = (int)chunkFloatY;
    int chunkZ = (int)chunkFloatZ;

    chunkX += (chunkFloatX - chunkX < 0.5) ? -1 : 0;

    int offsetY = ((chunkFloatY - chunkY > 0.5f) ? CHUNK_SIDE : -CHUNK_SIDE);
    int offsetZ = ((chunkFloatZ - chunkZ > 0.5f) ? CHUNK_SIDE_SQRD : -CHUNK_SIDE_SQRD);

    int chunk = chunkX + chunkY * CHUNK_SIDE + chunkZ * CHUNK_SIDE_SQRD;
    int chunkA_Start = _chunkStart[chunk];
    int chunkA_End = index;

    resolveParticleCollisions(chunkA_Start, chunkA_End, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    int chunkB_Start = index + 1;
    int chunkB_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkB_Start, chunkB_End, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk += offsetY;
    int chunkC_Start = _chunkStart[chunk];
    int chunkC_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkC_Start, chunkC_End, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk += offsetZ;
    int chunkD_Start = _chunkStart[chunk];
    int chunkD_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkD_Start, chunkD_End, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    chunk -= offsetY;
    int chunkE_Start = _chunkStart[chunk];
    int chunkE_End = _chunkEnd[chunk + 1];

    resolveParticleCollisions(chunkE_Start, chunkE_End, ref particle, ref speciesData, ref totalDepenetration, ref numCollisions);

    if (numCollisions > 0) {
      particle.position.x += totalDepenetration.x / numCollisions;
      particle.position.y += totalDepenetration.y / numCollisions;
      particle.position.z += totalDepenetration.z / numCollisions;
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

  int considerCount = 0;
  int collisionCount = 0;
  private void resolveParticleCollisions(int start,
                                         int end,
                                     ref Particle particle,
                                     ref SpeciesData speciesData,
                                     ref Vector3 totalDepenetration,
                                     ref int numCollisions) {
    for (int i = start; i < end; ++i) {
      considerCount++;
      float dx = particle.position.x - _particlesBack[i].position.x;
      float dy = particle.position.y - _particlesBack[i].position.y;
      float dz = particle.position.z - _particlesBack[i].position.z;
      float sqrDist = dx * dx + dy * dy + dz * dz;

      if (sqrDist < PARTICLE_DIAMETER * PARTICLE_DIAMETER && sqrDist > 0.000000001f) {
        collisionCount++;
        float dist = Mathf.Sqrt(sqrDist);
        float constant = -0.5f * (dist - PARTICLE_DIAMETER) / dist;

        totalDepenetration.x += dx * constant;
        totalDepenetration.y += dy * constant;
        totalDepenetration.z += dz * constant;
        numCollisions++;
      }
    }
  }

  private int getChunk(ref Particle particle) {
    int x = (int)(particle.position.x / CHUNK_SIZE + HALF_CHUNK_SIDE);
    int y = (int)(particle.position.y / CHUNK_SIZE + HALF_CHUNK_SIDE);
    int z = (int)(particle.position.z / CHUNK_SIZE + HALF_CHUNK_SIDE);
    return x + y * CHUNK_SIDE + z * CHUNK_SIDE_SQRD;
  }

  private float frac(float value) {
    return value - (int)value;
  }
  #endregion
}