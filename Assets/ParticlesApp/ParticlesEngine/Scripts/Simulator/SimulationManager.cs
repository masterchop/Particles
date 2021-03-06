﻿using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Leap.Unity;
using Leap.Unity.Query;
using Leap.Unity.Attributes;

public enum ColorMode {
  BySpecies,
  BySpeciesWithMagnitude,
  ByVelocity,
  ByInverseVelocity,
  SmartVelocity
}

public enum TrailMode {
  Fish,
  Squash
}

public enum ResetBehavior {
  None,
  SmoothTransition,
  ResetPositions,
  FadeInOut,
}

public enum SimulationMethod {
  Texture,
  InteractionEngine
}

public class SimulationManager : MonoBehaviour {
  public const int TEXTURE_SIZE = 64;
  public const int MAX_PARTICLES = TEXTURE_SIZE * TEXTURE_SIZE;
  public const int MAX_SPECIES = 31;
  public const int MAX_FORCE_STEPS = 64;

  public const float PARTICLE_RADIUS = 0.01f;
  public const float PARTICLE_DIAMETER = PARTICLE_RADIUS * 2;

  #region INSPECTOR

  [SerializeField]
  private GeneratorManager _generator;

  [SerializeField]
  private TextureSimulator _textureSimulator;

  [SerializeField]
  private IESimulator _ieSimulator;

  #pragma warning disable 0414 // screen GUI code was commented out.
  [SerializeField]
  private bool _showScreenGUI = true;
  #pragma warning restore 0414

  //##################//
  ///      Field      //
  //##################//
  [Header("Field")]
  [Tooltip("Must be a child of this component.")]
  [SerializeField]
  private Vector3 _fieldCenter;
  public Vector3 fieldCenter {
    get { return _fieldCenter; }
  }

  [Range(0, 2)]
  [SerializeField]
  private float _fieldRadius = 1;
  public float fieldRadius {
    get { return _fieldRadius; }
    set { _fieldRadius = value; }
  }

  [Range(0, 0.1f)]
  [SerializeField]
  private float _fieldForce = 0.0005f;
  public float fieldForce {
    get { return _fieldForce; }
    set { _fieldForce = value; }
  }

  [MinMax(0, 1)]
  [SerializeField]
  private Vector2 _headRadiusRange = new Vector2(0.15f, 0.15f);
  public float headRadius {
    get {
      if (_zoomController == null) {
        return 0;
      } else {
        float ratio = _zoomController._targetZoomedInScale / _zoomController._targetZoomedOutScale;
        float currZoomRatio = _displayAnchor.lossyScale.x / _zoomController._targetZoomedOutScale;
        float percent = Mathf.InverseLerp(1, ratio, currZoomRatio);
        return Mathf.Lerp(_headRadiusRange.x, _headRadiusRange.y, percent);
      }
    }
  }

  //#######################//
  ///      Simulation      //
  //#######################//
  [Header("Simulation")]
  [SerializeField]
  private bool _simulationEnabled = true;
  public bool simulationEnabled {
    get { return _simulationEnabled; }
    set { _simulationEnabled = value; }
  }

  [Range(0, 2)]
  [SerializeField]
  private float _simulationTimescale = 1;
  public float simulationTimescale {
    get { return _simulationTimescale; }
    set { _simulationTimescale = value; }
  }

  [MinValue(1)]
  [SerializeField]
  private int _stepsPerTick = 1;
  public int stepsPerTick {
    get { return _stepsPerTick; }
    set { _stepsPerTick = value; }
  }

  [SerializeField]
  private KeyCode _resetParticlePositionsKey = KeyCode.P;

  //###########################//
  ///      Reset Behavior      //
  //###########################//
  [Header("Reset Behavior")]
  [MinValue(0)]
  [SerializeField]
  private float _resetTime = 0.5f;
  public float resetTime {
    get { return _resetTime; }
  }

  [MinValue(0)]
  [SerializeField]
  private float _resetForce = 1;
  public float resetForce {
    get { return _resetForce; }
  }

  [MinValue(0)]
  [SerializeField]
  private float _resetRange = 1;
  public float resetRange {
    get { return _resetRange; }
  }

  [MinValue(0)]
  [SerializeField]
  private float _resetHeadRange = 0.6f;
  public float resetHeadRange {
    get { return _resetHeadRange; }
  }

  [SerializeField]
  private AnimationCurve _resetSocialCurve;
  public AnimationCurve resetSocialCurve {
    get { return _resetSocialCurve; }
  }

  //####################//
  ///      Display      //
  //####################//
  [Header("Display")]
  [SerializeField]
  private bool _displayParticles = true;
  public bool displayParticles {
    get { return _displayParticles; }
    set { _displayParticles = value; }
  }

  [SerializeField]
  private Transform _displayAnchor;
  public Transform displayAnchor {
    get { return _displayAnchor; }
  }

  [SerializeField]
  private SimulationZoomController _zoomController;

  [SerializeField]
  private Mesh _particleMesh;
  public Mesh particleMesh {
    get { return _particleMesh; }
    set {
      _particleMesh = value;

      if (_textureSimulator != null) {
        _textureSimulator.BuildDisplayMeshes();
      }

      if (_ieSimulator != null) {
        _ieSimulator.ApplyDisplayMesh();
      }
    }
  }

  [SerializeField]
  private Mesh[] _meshLods;

  [Range(0, 0.05f)]
  [SerializeField]
  private float _particleRadius = 0.01f;
  public float particleRadius {
    get { return _particleRadius; }
    set {
      _particleRadius = value;
    }
  }

  [OnEditorChange("colorMode")]
  [SerializeField]
  private ColorMode _colorMode = ColorMode.BySpecies;
  public ColorMode colorMode {
    get { return _colorMode; }
    set {
      _colorMode = value;

      if (_textureSimulator != null) {
        _textureSimulator.UpdateShaderKeywords();
      }
    }
  }

  [MinValue(0)]
  [SerializeField]
  private float _particleBrightness = 165.24f;
  public float particleBrightness {
    get { return _particleBrightness; }
    set { _particleBrightness = value; }
  }

  [MinValue(0)]
  [OnEditorChange("trailSize")]
  [SerializeField]
  private float _trailSize = 0.02f;
  public float trailSize {
    get { return _trailSize; }
    set {
      _trailSize = value;

      if (_textureSimulator != null) {
        _textureSimulator.RebuildTrailTexture();
      }
    }
  }

  [OnEditorChange("trailMode")]
  [SerializeField]
  private TrailMode _trailMode = TrailMode.Fish;
  public TrailMode trailMode {
    get { return _trailMode; }
    set {
      _trailMode = value;

      if (_textureSimulator != null) {
        _textureSimulator.UpdateShaderKeywords();
      }
    }
  }

  [OnEditorChange("speedToTrailLength")]
  [SerializeField]
  private AnimationCurve _speedToTrailLength;
  public AnimationCurve speedToTrailLength {
    get { return _speedToTrailLength; }
    set {
      _speedToTrailLength = value;

      if (_textureSimulator != null) {
        _textureSimulator.RebuildTrailTexture();
      }
    }
  }

  [SerializeField]
  private KeyCode _ranzomizeColorsKey = KeyCode.C;

  [SerializeField]
  private KeyCode _zoomInKey = KeyCode.UpArrow;

  [SerializeField]
  private KeyCode _zoomOutKey = KeyCode.DownArrow;

  [MinValue(0)]
  [SerializeField]
  private float _zoomFactor = 0.1f;

  //#######################//
  ///      Ecosystems      //
  //#######################//
  [Header("Ecosystems")]
  [SerializeField]
  private ToLoadOnStart _toLoadOnStart = ToLoadOnStart.Random;

  [DisableIf("_toLoadOnStart", isNotEqualTo: ToLoadOnStart.Preset)]
  [SerializeField]
  private EcosystemPreset _presetToLoad = EcosystemPreset.BlackHole;

  [SerializeField]
  private SimulationMethod _simulationMethod = SimulationMethod.Texture;
  public SimulationMethod simulationMethod {
    get { return _simulationMethod; }
    set { _simulationMethod = value; }
  }

  [SerializeField]
  private KeyCode _saveEcosystemKey = KeyCode.F5;

  [SerializeField]
  private KeyCode _loadPrevEcosystem = KeyCode.LeftArrow;

  [SerializeField]
  private KeyCode _loadNextEcosystem = KeyCode.RightArrow;

  [SerializeField]
  private KeyCode _randomizeEcosystemKey = KeyCode.Space;

  [SerializeField]
  private KeyCode _loadPresetEcosystemKey = KeyCode.R;

  [SerializeField]
  private StreamingFolder _loadingFolder;

  #endregion

  private EcosystemDescription _currentDescription;
  private SimulationMethod _currentSimulationMethod = SimulationMethod.Texture;
  private bool _inGui = false;
  private string _currLoadedEcosystemName = "";

  #region PUBLIC API
  public event System.Action OnPresetLoaded;
  public event System.Action OnEcosystemBeginTransition;
  public event System.Action OnEcosystemMidTransition;
  public event System.Action OnEcosystemEndedTransition;

  public bool isPerformingTransition { get; private set; }

  public EcosystemDescription currentDescription {
    get { return _currentDescription; }
  }

  public SimulationMethod currentSimulationMethod {
    get { return _currentSimulationMethod; }
  }

  public float simulationAge {
    get {
      //TODO
      return 0;
    }
  }

  public Color GetSpeciesColor(int speciesIdx) {
    return _currentDescription.speciesData[speciesIdx].color;
  }

  public void ApplySliderValues() {
    ResetBehavior resetBehavior;
    _generator.ApplySliderValues(ref _currentDescription, out resetBehavior);

    RestartSimulation(_currentDescription, resetBehavior);
  }

  /// <summary>
  /// Restarts the simulation to whatever state it was in when it was
  /// most recently restarted.
  /// </summary>
  public void RestartSimulation(ResetBehavior resetBehavior = ResetBehavior.SmoothTransition) {
    //If we had generated a random simulation, re-generate it so that new settings
    //can take effect.  We assume the name of the description is it's seed!
    if (_currentDescription.isRandomDescription) {
      RandomizeSimulation(_currentDescription.name, resetBehavior);
    } else {
      RestartSimulation(_currentDescription, resetBehavior);
    }
  }

  /// <summary>
  /// Restarts the simulation using a specific preset to choose the
  /// initial conditions.
  /// </summary>
  public void RestartSimulation(EcosystemPreset preset,
                                ResetBehavior resetBehavior = ResetBehavior.FadeInOut) {
    var presetDesc = _generator.GetPresetEcosystem(preset);

    RestartSimulation(presetDesc, resetBehavior);

    if (OnPresetLoaded != null) {
      OnPresetLoaded();
    }
  }

  /// <summary>
  /// Restarts the simulation using a random description.
  /// </summary>
  public void RandomizeSimulation(ResetBehavior resetBehavior) {
    RestartSimulation(_generator.GetRandomEcosystem(), resetBehavior);
  }

  /// <summary>
  /// Loads a random simulation based on the given string seed.
  /// </summary>
  public void LoadSimulationWithSeed(string seed, ResetBehavior resetBehavior) {
    RestartSimulation(_generator.GetRandomEcosystem(seed), resetBehavior);
  }

  /// <summary>
  /// Restarts the simulation using a random description calculated using
  /// the seed value.  The same seed value should always result in the same
  /// simulation description.
  /// </summary>
  public void RandomizeSimulation(string seed,
                                  ResetBehavior resetBehavior) {
    RestartSimulation(_generator.GetRandomEcosystem(seed), resetBehavior);
  }

  /// <summary>
  /// Randomizes the simulation colors of the current simulation.
  /// </summary>
  public void RandomizeSimulationColors() {
    var newRandomColors = _generator.GetRandomColors();
    for (int i = 0; i < _currentDescription.speciesData.Length; i++) {
      _currentDescription.speciesData[i].color = newRandomColors[i];
    }

    RestartSimulation(_currentDescription, ResetBehavior.None);
  }

  public void RestartSimulation(EcosystemDescription ecosystemDescription,
                                ResetBehavior resetBehavior) {
    var oldMethod = _currentSimulationMethod;
    var newMethod = _simulationMethod;

    _currentSimulationMethod = newMethod;

    isPerformingTransition = true;
    if (OnEcosystemBeginTransition != null) {
      OnEcosystemBeginTransition();
    }

    if (oldMethod != newMethod) {
      restartSimulator(oldMethod, EcosystemDescription.empty, resetBehavior);
      restartSimulator(newMethod, ecosystemDescription, resetBehavior);
    } else {
      restartSimulator(_currentSimulationMethod, ecosystemDescription, resetBehavior);
    }
  }

  public void NotifyMidTransition(SimulationMethod method) {
    if (method == currentSimulationMethod && OnEcosystemMidTransition != null) {
      OnEcosystemMidTransition();
    }
  }

  public void NotifyEndedTransition(SimulationMethod method) {
    isPerformingTransition = false;

    if (method == _currentSimulationMethod) {
      switch (method) {
        case SimulationMethod.Texture:
          _currentDescription = _textureSimulator.currentDescription;
          break;
        case SimulationMethod.InteractionEngine:
          _currentDescription = _ieSimulator.currentDescription;
          break;
      }

      if (OnEcosystemEndedTransition != null) {
        OnEcosystemEndedTransition();
      }
    }
  }

  public int GetRecommendedMaxParticles() {
    return GetRecommendedMaxParticles(simulationMethod);
  }

  public int GetRecommendedMaxParticles(SimulationMethod simMethod) {
    switch (simMethod) {
      case SimulationMethod.Texture:
        return 4096;
      case SimulationMethod.InteractionEngine:
      default:
        return 24;
    }
  }

  /// <summary>
  /// Returns true if successful, otherwise returns false.
  /// </summary>
  public bool SaveEcosystem() {
    try {
      return SaveEcosystem("");
    } catch (System.Exception) {
      return false;
    }
  }

  /// <summary>
  /// Returns true if successful, otherwise returns false.
  /// </summary>
  public bool SaveEcosystem(string filePath) {
    try {
      File.WriteAllText(filePath,
        JsonUtility.ToJson(currentDescription, prettyPrint: true));
      return true;
    } catch (System.Exception e) {
      Debug.LogError(e.ToString());
      return false;
    }
  }

  /// <summary>
  /// Returns true if successful, otherwise returns false.
  /// </summary>
  public bool LoadEcosystem() {
    try {
      var file = Directory.GetFiles(_loadingFolder.Path).Query()
                          .FirstOrDefault(t => t.EndsWith(".json"));
      return LoadEcosystem(file);
    } catch (System.Exception) {
      return false;
    }
  }

  /// <summary>
  /// Returns true if successful, otherwise returns false.
  /// </summary>
  public bool LoadEcosystem(string filePath) {
    try {
      var description = JsonUtility.FromJson<EcosystemDescription>(File.ReadAllText(filePath));
      if (description.isMalformed) {
        return false;
      }

      //Always treat descriptions loaded from file as a preset, whether or not
      //they were originally derived from random settings.
      description.isRandomDescription = false;
      RestartSimulation(description, ResetBehavior.ResetPositions);

      return true;
    } catch (System.Exception) {
      return false;
    }
  }

  #endregion

  #region UNITY MESSAGES
  private IEnumerator Start() {
    switch (_toLoadOnStart) {
      case ToLoadOnStart.Preset:
        RestartSimulation(_presetToLoad, ResetBehavior.ResetPositions);
        break;
      case ToLoadOnStart.Random:
        RandomizeSimulation(ResetBehavior.ResetPositions);
        break;
    }

    yield return null;
    yield return null;
    yield return null;

    switch (_toLoadOnStart) {
      case ToLoadOnStart.Preset:
        RestartSimulation(_presetToLoad, ResetBehavior.ResetPositions);
        break;
      case ToLoadOnStart.Random:
        RandomizeSimulation(ResetBehavior.ResetPositions);
        break;
    }
  }

  private void Update() {
    // Commented out to prevent typing into simulation seed name field from overlapping
    // and activating keyboard commands.
    //handleUserInput();
  }

  private void OnGUI() {
    //_showScreenGUI = GUILayout.Toggle(_showScreenGUI, "Show GUI");
    //if (_showScreenGUI) {
    //  _inGui = true;
    //  handleUserInput();
    //  _inGui = false;

    //  if (_currentDescription != null) {
    //    GUILayout.Label(_currentDescription.name);
    //  }
    //}
  }
  #endregion

  #region PRIVATE IMPLEMENTATION

  private void handleUserInput() {
    if (buttonOrKey("Restart", _resetParticlePositionsKey)) {
      RestartSimulation(ResetBehavior.ResetPositions);
    }

    if (buttonOrKey("Load Preset", _loadPresetEcosystemKey)) {
      RestartSimulation(_presetToLoad, ResetBehavior.ResetPositions);
    }

    if (buttonOrKey("Randomize Ecosystem", _randomizeEcosystemKey)) {
      RandomizeSimulation(ResetBehavior.ResetPositions);
    }

    if (buttonOrKey("Randomize Colors", _ranzomizeColorsKey)) {
      RandomizeSimulationColors();
    }

    beginHorizontal();

    if (buttonOrKey("Zoom In", _zoomInKey)) {
      _displayAnchor.localScale *= (1.0f + _zoomFactor);
    }

    if (buttonOrKey("Zoom Out", _zoomOutKey)) {
      _displayAnchor.localScale /= (1.0f + _zoomFactor);
    }

    endHorizontal();

    beginHorizontal();

    if (buttonOrKey("GPU", KeyCode.Alpha1) && _simulationMethod != SimulationMethod.Texture) {
      _simulationMethod = SimulationMethod.Texture;
      RestartSimulation(ResetBehavior.ResetPositions);
    }

    if (buttonOrKey("IE", KeyCode.Alpha2) && _simulationMethod != SimulationMethod.InteractionEngine) {
      _simulationMethod = SimulationMethod.InteractionEngine;
      RestartSimulation(ResetBehavior.ResetPositions);
    }

    endHorizontal();

    if (_meshLods != null && _meshLods.Length > 0) {
      beginHorizontal();

      for (int i = 0; i < _meshLods.Length; i++) {
        if (buttonOrKey("LOD" + i, KeyCode.F1 + i)) {
          particleMesh = _meshLods[i];
        }
      }

      endHorizontal();
    }

    beginHorizontal();

    if (buttonOrKey("Save Ecosystem", _saveEcosystemKey)) {
      SaveEcosystem();
    }

    if (buttonOrKey("Load Prev", _loadPrevEcosystem)) {
      loadAtOffset(-1);
    }

    if (buttonOrKey("Load Next", _loadNextEcosystem)) {
      loadAtOffset(1);
    }

    endHorizontal();
  }

  private void loadAtOffset(int offset) {
    List<string> allPaths = Directory.GetFiles(_loadingFolder.Path).Query().Where(t => t.EndsWith(".json")).ToList();
    if (allPaths.Count == 0) {
      return;
    }

    if (!allPaths.Contains(_currLoadedEcosystemName)) {
      allPaths.Add(_currLoadedEcosystemName);
    }

    allPaths.Sort((a, b) => Path.GetFileNameWithoutExtension(a).ToLower().Replace(" ", "").CompareTo(Path.GetFileNameWithoutExtension(b).ToLower().Replace(" ", "")));

    int currIndex = allPaths.IndexOf(_currLoadedEcosystemName);
    currIndex = (currIndex + allPaths.Count + offset) % allPaths.Count;

    string toLoad = allPaths[currIndex];
    var description = JsonUtility.FromJson<EcosystemDescription>(File.ReadAllText(toLoad));
    description.name = Path.GetFileNameWithoutExtension(toLoad);
    RestartSimulation(description, ResetBehavior.ResetPositions);
    _currLoadedEcosystemName = toLoad;
  }

  private bool buttonOrKey(string name, KeyCode key) {
    if (_inGui) {
      if (key == KeyCode.None) {
        return GUILayout.Button(name + " (" + key.ToString() + ")");
      } else {
        return GUILayout.Button(name);
      }
    } else {
      return Input.GetKeyDown(key);
    }
  }

  private void beginHorizontal() {
    if (_inGui) {
      GUILayout.BeginHorizontal();
    }
  }

  private void endHorizontal() {
    if (_inGui) {
      GUILayout.EndHorizontal();
    }
  }

  private void restartSimulator(SimulationMethod method,
                              EcosystemDescription ecosystemDescription,
                              ResetBehavior resetBehavior) {
    switch (method) {
      case SimulationMethod.InteractionEngine:
        _ieSimulator.RestartSimulation(ecosystemDescription, resetBehavior);
        break;
      case SimulationMethod.Texture:
        _textureSimulator.RestartSimulation(ecosystemDescription, resetBehavior);
        break;
      default:
        throw new System.InvalidOperationException();
    }
  }

  private enum ToLoadOnStart {
    None,
    Random,
    Preset
  }

  #endregion
}
