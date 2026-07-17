using System.Globalization;
using System.Numerics;
using System.Text;
using Content.Client._Scp.Blinking;
using Content.Client.Interactable.Components;
using Content.Client.Light;
using Content.Shared._Scp.Blinking;
using Content.Shared._Scp.Graphics.Shaders.Grain;
using Content.Shared._Scp.Graphics.Shaders.SinCity;
using Content.Shared._Scp.Graphics.Shaders.Vignette;
using Content.Shared._Scp.ScpCCVars;
using Content.Shared._Scp.Vision.FOV;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Light.Components;
using Robust.Client.Console;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Profiling;
using Robust.Shared.Timing;

namespace Content.Client._Scp.Graphics.Shadows.Benchmark;

/// <summary>
/// Runs a fixed graphical lighting benchmark and reads Clyde's public profiler values.
/// </summary>
public sealed partial class ScpLightingBenchmarkSystem : EntitySystem
{
    private const string MarkerPrototype = "ScpLightingBenchmarkMarker";
    private const string LightPrototype = "ScpLightingBenchmarkLight";
    private const string OccluderPrototype = "ScpLightingBenchmarkOccluder";
    private const string CasterPrototype = "ScpLightingBenchmarkCaster";
    private const string ResultMarker = "SCP_LIGHT_BENCH_RESULT ";
    private const int ExpectedLights = 128;
    private const float CameraZoom = 1.5f;
    private static readonly TimeSpan SceneWaitTimeout = TimeSpan.FromMinutes(2);

    private static readonly BenchmarkPhase[] Phases =
    [
        new("baseline", false, false, false, false, 2),
        new("engineLight", false, true, true, false, 2),
        new("engineShadows", false, true, true, true, 2),
        new("contentLight", true, true, true, false, 2),
        new("contentOccluders", true, true, true, true, 0),
        new("contentShadows", true, true, true, true, 2),
    ];

    private static readonly string[] ProfileGroups =
    [
        "ScpContentLighting.Snapshot",
        "ScpContentLighting.Restore",
        "ScpContentLighting",
        "ScpContentLighting.SpriteCache",
        "ScpContentLighting.OccluderCache",
        "ScpContentLighting.Geometry",
        "ScpContentLighting.AtlasGeometry",
        "ScpContentLighting.ShadowAtlas",
        "ScpContentLighting.ShadowContributions",
        "ScpContentLighting.ProtectionMask",
        "ScpContentLighting.StandardLights",
        "UpdateOcclusionGeometry",
        "Draw Lights",
    ];

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IClientConsoleHost _console = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private ILightManager _light = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IOverlayManager _overlay = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private ProfManager _prof = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly List<EntityUid> _outlines = new(256);
    private readonly List<FrameMetrics> _samples = new(600);
    private readonly List<PhaseResult> _phaseResults = new(Phases.Length);
    private readonly Dictionary<int, int> _profileGroupSlots = new(ProfileGroups.Length);
    private readonly bool[] _registeredProfileGroups = new bool[ProfileGroups.Length];
    private readonly ProfileFrameMetrics[] _currentProfileGroups = new ProfileFrameMetrics[ProfileGroups.Length];
    private readonly List<ProfileFrameMetrics>[] _profileSamples = CreateProfileSampleLists();

    private SharedTransformSystem _transform = default!;
    private ScpShadowCasterSystem _shadowCaster = default!;
    private ISawmill _logger = default!;
    private readonly FixedEye _fixedEye = new();
    private IEye? _previousEye;

    private BenchmarkState _state;
    private MapCoordinates _cameraCoordinates;
    private BlinkingOverlay? _blinkingOverlay;
    private SunShadowOverlay? _sunShadowOverlay;
    private TimeSpan _sceneDeadline;
    private int _warmupFrames;
    private int _sampleFrames;
    private int _remainingFrames;
    private int _phaseIndex;
    private int _lightCount;
    private int _occluderCount;
    private int _casterCount;
    private long _lastProfilerIndex;
    private bool _quitWhenFinished;

    private bool _savedEnvironment;
    private bool _oldAmbientOcclusion;
    private bool _oldContentLighting;
    private bool _oldFieldOfViewBlur;
    private bool _oldGrain;
    private bool _oldLightBlur;
    private bool _oldSoftShadows;
    private bool _oldLocalPlayerShadowOutsideFov;
    private bool _oldOutline;
    private bool _oldSinCity;
    private bool _oldLightEnabled;
    private bool _oldDrawHardFov;
    private bool _oldDrawLighting;
    private bool _oldDrawShadows;
    private float _oldLightResolutionScale;
    private float _oldMaxLightRadius;
    private int _oldMaxLightCount;
    private int _oldMaxOccluderCount;
    private int _oldMaxShadowcastingLights;
    private int _oldMobShadowQuality;
    private int _oldObjectShadowQuality;

    public bool Running => _state != BenchmarkState.Idle;

    public override void Initialize()
    {
        base.Initialize();

        _transform = EntityManager.System<SharedTransformSystem>();
        _shadowCaster = EntityManager.System<ScpShadowCasterSystem>();
        _logger = _log.GetSawmill("scp.light.benchmark");

        if (!ScpLightingBenchmarkCommand.TryTakeStartupRequest(out var request))
            return;

        if (!Start(request.WarmupFrames, request.SampleFrames, request.QuitWhenFinished, out var error))
        {
            _logger.Error("SCP_LIGHT_BENCH_ERROR " +
                (error ?? "Unable to start the queued lighting benchmark."));
            if (request.QuitWhenFinished)
                _console.ExecuteCommand("quit");
            return;
        }

        _logger.Info("Queued lighting benchmark started; waiting for the fixture scene.");
    }

    public override void Shutdown()
    {
        RestoreEnvironment();
        base.Shutdown();
    }

    public bool Start(int warmupFrames, int sampleFrames, bool quitWhenFinished, out string? error)
    {
        error = null;
        if (Running)
        {
            error = "The lighting benchmark is already running.";
            return false;
        }

        if (!_prof.IsEnabled)
        {
            error = "prof.enabled must be true before starting the benchmark.";
            return false;
        }

        if (warmupFrames < 1 || sampleFrames < 1)
        {
            error = "Warmup and sample frame counts must be positive.";
            return false;
        }

        _warmupFrames = warmupFrames;
        _sampleFrames = sampleFrames;
        _quitWhenFinished = quitWhenFinished;
        _sceneDeadline = _timing.RealTime + SceneWaitTimeout;
        _phaseIndex = 0;
        _phaseResults.Clear();
        _samples.Clear();
        ClearProfileSamples();
        _lastProfilerIndex = _prof.Buffer.IndexWriteOffset - 1;

        SaveAndApplyEnvironment();
        TryRemoveBlinkingOverlay();
        TryRemoveSunShadowOverlay();
        _state = BenchmarkState.WaitingForScene;
        return true;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_state == BenchmarkState.Idle)
            return;

        TryRemoveBlinkingOverlay();
        TryRemoveSunShadowOverlay();

        if (_state == BenchmarkState.WaitingForScene)
        {
            if (!TryPrepareScene())
            {
                if (_state == BenchmarkState.Idle)
                    return;

                if (_timing.RealTime >= _sceneDeadline)
                    Abort("Timed out waiting for ScpLightingBenchmark with exactly 128 lights.");
                return;
            }

            ApplyPhase(0);
            return;
        }

        KeepCameraFixed();
        if (!TryReadLatestFrame(out var metrics))
            return;

        if (_state == BenchmarkState.WarmingUp)
        {
            if (--_remainingFrames > 0)
                return;

            _samples.Clear();
            ClearProfileSamples();
            _remainingFrames = _sampleFrames;
            _state = BenchmarkState.Sampling;
            return;
        }

        _samples.Add(metrics);
        for (var i = 0; i < _profileSamples.Length; i++)
            _profileSamples[i].Add(_currentProfileGroups[i]);

        if (--_remainingFrames > 0)
            return;

        _phaseResults.Add(BuildPhaseResult(
            Phases[_phaseIndex].Name,
            _samples,
            _profileSamples));
        _phaseIndex++;
        if (_phaseIndex < Phases.Length)
        {
            ApplyPhase(_phaseIndex);
            return;
        }

        Finish();
    }

    private void SaveAndApplyEnvironment()
    {
        _oldAmbientOcclusion = _configuration.GetCVar(CCVars.AmbientOcclusion);
        _oldContentLighting = _configuration.GetCVar(ScpCCVars.ContentLighting);
        _oldFieldOfViewBlur = _configuration.GetCVar(ScpCCVars.FieldOfViewBlurEnabled);
        _oldGrain = _configuration.GetCVar(ScpCCVars.GrainToggleOverlay);
        _oldLightBlur = _configuration.GetCVar(Robust.Shared.CVars.LightBlur);
        _oldSoftShadows = _configuration.GetCVar(Robust.Shared.CVars.LightSoftShadows);
        _oldLocalPlayerShadowOutsideFov = _configuration.GetCVar(ScpCCVars.LocalPlayerShadowOutsideFov);
        _oldLightResolutionScale = _configuration.GetCVar(Robust.Shared.CVars.LightResolutionScale);
        _oldMaxLightCount = _configuration.GetCVar(Robust.Shared.CVars.MaxLightCount);
        _oldMaxLightRadius = _configuration.GetCVar(Robust.Shared.CVars.MaxLightRadius);
        _oldMaxOccluderCount = _configuration.GetCVar(Robust.Shared.CVars.MaxOccluderCount);
        _oldMaxShadowcastingLights = _configuration.GetCVar(Robust.Shared.CVars.MaxShadowcastingLights);
        _oldMobShadowQuality = _configuration.GetCVar(ScpCCVars.MobShadowQuality);
        _oldObjectShadowQuality = _configuration.GetCVar(ScpCCVars.ObjectShadowQuality);
        _oldOutline = _configuration.GetCVar(CCVars.OutlineEnabled);
        _oldSinCity = _configuration.GetCVar(ScpCCVars.SinCityToggleOverlay);
        _oldLightEnabled = _light.Enabled;
        _oldDrawHardFov = _light.DrawHardFov;
        _oldDrawLighting = _light.DrawLighting;
        _oldDrawShadows = _light.DrawShadows;
        _savedEnvironment = true;

        _configuration.SetCVar(CCVars.AmbientOcclusion, false);
        _configuration.SetCVar(CCVars.OutlineEnabled, false);
        _configuration.SetCVar(ScpCCVars.FieldOfViewBlurEnabled, false);
        _configuration.SetCVar(ScpCCVars.GrainToggleOverlay, false);
        _configuration.SetCVar(ScpCCVars.SinCityToggleOverlay, false);
        _configuration.SetCVar(Robust.Shared.CVars.LightBlur, false);
        _configuration.SetCVar(Robust.Shared.CVars.LightSoftShadows, true);
        _configuration.SetCVar(Robust.Shared.CVars.LightResolutionScale, 0.5f);
        _configuration.SetCVar(Robust.Shared.CVars.MaxLightCount, 2_048);
        _configuration.SetCVar(Robust.Shared.CVars.MaxLightRadius, 32.1f);
        _configuration.SetCVar(Robust.Shared.CVars.MaxOccluderCount, 2_048);
        _configuration.SetCVar(Robust.Shared.CVars.MaxShadowcastingLights, ExpectedLights);
        _configuration.SetCVar(ScpCCVars.LocalPlayerShadowOutsideFov, false);
        _configuration.SetCVar(ScpCCVars.MobShadowQuality, 2);
        _configuration.SetCVar(ScpCCVars.ObjectShadowQuality, 2);
        _light.Enabled = true;
        _light.DrawHardFov = false;
    }

    private void RestoreEnvironment()
    {
        if (!_savedEnvironment)
            return;

        _configuration.SetCVar(CCVars.AmbientOcclusion, _oldAmbientOcclusion);
        _configuration.SetCVar(CCVars.OutlineEnabled, _oldOutline);
        _configuration.SetCVar(ScpCCVars.ContentLighting, _oldContentLighting);
        _configuration.SetCVar(ScpCCVars.FieldOfViewBlurEnabled, _oldFieldOfViewBlur);
        _configuration.SetCVar(ScpCCVars.GrainToggleOverlay, _oldGrain);
        _configuration.SetCVar(ScpCCVars.SinCityToggleOverlay, _oldSinCity);
        _configuration.SetCVar(Robust.Shared.CVars.LightBlur, _oldLightBlur);
        _configuration.SetCVar(Robust.Shared.CVars.LightSoftShadows, _oldSoftShadows);
        _configuration.SetCVar(Robust.Shared.CVars.LightResolutionScale, _oldLightResolutionScale);
        _configuration.SetCVar(Robust.Shared.CVars.MaxLightCount, _oldMaxLightCount);
        _configuration.SetCVar(Robust.Shared.CVars.MaxLightRadius, _oldMaxLightRadius);
        _configuration.SetCVar(Robust.Shared.CVars.MaxOccluderCount, _oldMaxOccluderCount);
        _configuration.SetCVar(Robust.Shared.CVars.MaxShadowcastingLights, _oldMaxShadowcastingLights);
        _configuration.SetCVar(ScpCCVars.LocalPlayerShadowOutsideFov, _oldLocalPlayerShadowOutsideFov);
        _configuration.SetCVar(ScpCCVars.MobShadowQuality, _oldMobShadowQuality);
        _configuration.SetCVar(ScpCCVars.ObjectShadowQuality, _oldObjectShadowQuality);
        _light.Enabled = _oldLightEnabled;
        _light.DrawHardFov = _oldDrawHardFov;
        _light.DrawLighting = _oldDrawLighting;
        _light.DrawShadows = _oldDrawShadows;

        if (_sunShadowOverlay != null)
        {
            _overlay.AddOverlay(_sunShadowOverlay);
            _sunShadowOverlay = null;
        }

        if (_blinkingOverlay != null)
        {
            _overlay.AddOverlay(_blinkingOverlay);
            _blinkingOverlay = null;
        }

        if (_previousEye != null)
        {
            _eye.CurrentEye = _previousEye;
            _previousEye = null;
        }

        _savedEnvironment = false;
    }

    private void TryRemoveSunShadowOverlay()
    {
        if (_sunShadowOverlay != null || !_overlay.TryGetOverlay<SunShadowOverlay>(out var overlay))
            return;

        _sunShadowOverlay = overlay;
        _overlay.RemoveOverlay(overlay);
    }

    private void TryRemoveBlinkingOverlay()
    {
        if (_blinkingOverlay != null || !_overlay.TryGetOverlay<BlinkingOverlay>(out var overlay))
            return;

        _blinkingOverlay = overlay;
        _overlay.RemoveOverlay(overlay);
    }

    private bool TryPrepareScene()
    {
        if (_player.LocalEntity is not { } player || Deleted(player))
            return false;

        var hasMarker = false;
        var markerQuery = EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (markerQuery.MoveNext(out var uid, out var metadata, out var transform))
        {
            if (metadata.EntityPrototype?.ID != MarkerPrototype)
                continue;

            _cameraCoordinates = _transform.GetMapCoordinates(uid, transform);
            hasMarker = true;
            break;
        }

        if (!hasMarker)
            return false;

        _lightCount = 0;
        var lightQuery = EntityQueryEnumerator<MetaDataComponent, PointLightComponent>();
        while (lightQuery.MoveNext(out _, out var metadata, out _))
        {
            if (metadata.EntityPrototype?.ID == LightPrototype)
                _lightCount++;
        }

        if (_lightCount < ExpectedLights)
            return false;

        var sunQuery = EntityQueryEnumerator<SunShadowComponent>();
        if (sunQuery.MoveNext(out _, out _))
        {
            Abort("ScpLightingBenchmark must not contain SunShadowComponent.");
            return false;
        }

        if (_lightCount != ExpectedLights)
        {
            Abort($"Expected {ExpectedLights} point lights, found {_lightCount}.");
            return false;
        }

        RemoveBenchmarkEffects(player);

        _occluderCount = 0;
        var occluderQuery = EntityQueryEnumerator<MetaDataComponent, OccluderComponent>();
        while (occluderQuery.MoveNext(out _, out var metadata, out _))
        {
            if (metadata.EntityPrototype?.ID == OccluderPrototype)
                _occluderCount++;
        }

        if (_occluderCount < ExpectedLights)
            return false;
        if (_occluderCount != ExpectedLights)
        {
            Abort($"Expected {ExpectedLights} occluders, found {_occluderCount}.");
            return false;
        }

        _casterCount = 0;
        var casterQuery = EntityQueryEnumerator<MetaDataComponent, ScpShadowCasterVisualsComponent>();
        while (casterQuery.MoveNext(out _, out var metadata, out _))
        {
            if (metadata.EntityPrototype?.ID == CasterPrototype)
                _casterCount++;
        }

        if (_casterCount < ExpectedLights)
            return false;
        if (_casterCount != ExpectedLights)
        {
            Abort($"Expected {ExpectedLights} sprite casters, found {_casterCount}.");
            return false;
        }

        _previousEye = _eye.CurrentEye;
        _fixedEye.Position = _cameraCoordinates;
        _eye.CurrentEye = _fixedEye;
        KeepCameraFixed();
        return true;
    }

    private void RemoveBenchmarkEffects(EntityUid player)
    {
        RemComp<BlinkableComponent>(player);
        RemComp<FieldOfViewComponent>(player);
        RemComp<GrainOverlayComponent>(player);
        RemComp<SinCityOverlayComponent>(player);
        RemComp<VignetteOverlayComponent>(player);
        RemComp<ScpShadowCasterVisualsComponent>(player);

        if (TryComp(player, out SpriteComponent? sprite))
            sprite.Visible = false;

        _outlines.Clear();
        var outlineQuery = EntityQueryEnumerator<InteractionOutlineComponent>();
        while (outlineQuery.MoveNext(out var uid, out _))
            _outlines.Add(uid);

        for (var i = 0; i < _outlines.Count; i++)
            RemComp<InteractionOutlineComponent>(_outlines[i]);
    }

    private void KeepCameraFixed()
    {
        if (_player.LocalEntity is { } player && !Deleted(player))
            RemoveBenchmarkEffects(player);

        _fixedEye.Position = _cameraCoordinates;
        _fixedEye.Offset = Vector2.Zero;
        _fixedEye.Rotation = Angle.Zero;
        _fixedEye.Zoom = new Vector2(CameraZoom);
        _fixedEye.DrawFov = false;
        _fixedEye.DrawLight = true;

        if (!ReferenceEquals(_eye.CurrentEye, _fixedEye))
            _eye.CurrentEye = _fixedEye;
    }

    private void ApplyPhase(int index)
    {
        var phase = Phases[index];
        _configuration.SetCVar(ScpCCVars.ContentLighting, phase.ContentLighting);
        _configuration.SetCVar(ScpCCVars.MobShadowQuality, phase.ShadowQuality);
        _configuration.SetCVar(ScpCCVars.ObjectShadowQuality, phase.ShadowQuality);
        _light.Enabled = phase.LightEnabled;
        _light.DrawLighting = phase.DrawLighting;
        _light.DrawShadows = phase.DrawShadows;
        _lastProfilerIndex = _prof.Buffer.IndexWriteOffset - 1;
        _remainingFrames = _warmupFrames;
        _state = BenchmarkState.WarmingUp;
        _logger.Info($"Lighting benchmark phase {index + 1}/{Phases.Length}: {phase.Name}.");
    }

    private bool TryReadLatestFrame(out FrameMetrics metrics)
    {
        metrics = default;
        Array.Clear(_currentProfileGroups);
        RegisterProfileGroups();
        var buffer = _prof.Buffer;
        var profilerIndex = buffer.IndexWriteOffset - 1;
        if (profilerIndex <= _lastProfilerIndex || profilerIndex < 0)
            return false;

        _lastProfilerIndex = profilerIndex;
        ref var index = ref buffer.Index(profilerIndex);
        if (index.Type != ProfIndexType.Frame ||
            index.StartPos < buffer.LogWriteOffset - buffer.LogBuffer.LongLength)
        {
            return false;
        }

        var glId = _prof.GetStringIdx("GL Draw Calls");
        var clydeId = _prof.GetStringIdx("Clyde Draw Calls");
        var batchesId = _prof.GetStringIdx("Batches");
        if (glId == null || clydeId == null || batchesId == null)
            return false;

        var found = 0;
        for (var i = index.StartPos; i < index.EndPos; i++)
        {
            ref var entry = ref buffer.Log(i);
            if (entry.Type == ProfLogType.GroupEnd &&
                entry.GroupEnd.Value.Type == ProfValueType.TimeAllocSample &&
                _profileGroupSlots.TryGetValue(entry.GroupEnd.StringId, out var slot))
            {
                ref var profile = ref _currentProfileGroups[slot];
                profile.Seconds += entry.GroupEnd.Value.TimeAllocSample.Time;
                profile.AllocatedBytes += entry.GroupEnd.Value.TimeAllocSample.Alloc;
                profile.Occurrences++;
                continue;
            }

            if (entry.Type != ProfLogType.Value || entry.Value.Value.Type != ProfValueType.Int32)
                continue;

            if (entry.Value.StringId == glId.Value)
            {
                metrics.GlDrawCalls = entry.Value.Value.Int32;
                found |= 1;
            }
            else if (entry.Value.StringId == clydeId.Value)
            {
                metrics.ClydeDrawCalls = entry.Value.Value.Int32;
                found |= 2;
            }
            else if (entry.Value.StringId == batchesId.Value)
            {
                metrics.Batches = entry.Value.Value.Int32;
                found |= 4;
            }
        }

        if (index.EndPos > index.StartPos)
        {
            ref var frameGroup = ref buffer.Log(index.EndPos - 1);
            if (frameGroup.Type == ProfLogType.GroupEnd &&
                frameGroup.GroupEnd.Value.Type == ProfValueType.TimeAllocSample)
            {
                metrics.FrameSeconds = frameGroup.GroupEnd.Value.TimeAllocSample.Time;
            }
        }

        metrics.ViewportLights = _shadowCaster.ViewportLights.Count;

        return found == 7;
    }

    private void RegisterProfileGroups()
    {
        for (var slot = 0; slot < ProfileGroups.Length; slot++)
        {
            if (_registeredProfileGroups[slot] ||
                _prof.GetStringIdx(ProfileGroups[slot]) is not { } stringId)
            {
                continue;
            }

            _registeredProfileGroups[slot] = true;
            _profileGroupSlots[stringId] = slot;
        }
    }

    private void ClearProfileSamples()
    {
        for (var i = 0; i < _profileSamples.Length; i++)
            _profileSamples[i].Clear();
    }

    private static List<ProfileFrameMetrics>[] CreateProfileSampleLists()
    {
        var result = new List<ProfileFrameMetrics>[ProfileGroups.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = new List<ProfileFrameMetrics>(600);
        return result;
    }

    private static PhaseResult BuildPhaseResult(
        string name,
        List<FrameMetrics> samples,
        List<ProfileFrameMetrics>[] profileSamples)
    {
        var gl = new int[samples.Count];
        var clyde = new int[samples.Count];
        var batches = new int[samples.Count];
        var viewportLights = new int[samples.Count];
        var frameMilliseconds = new double[samples.Count];
        var framesPerSecond = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            gl[i] = samples[i].GlDrawCalls;
            clyde[i] = samples[i].ClydeDrawCalls;
            batches[i] = samples[i].Batches;
            viewportLights[i] = samples[i].ViewportLights;
            frameMilliseconds[i] = samples[i].FrameSeconds * 1_000d;
            framesPerSecond[i] = samples[i].FrameSeconds > 0f
                ? 1d / samples[i].FrameSeconds
                : 0d;
        }

        var profileResults = new ProfileGroupResult[ProfileGroups.Length];
        for (var group = 0; group < ProfileGroups.Length; group++)
        {
            var groupSamples = profileSamples[group];
            var timeMilliseconds = new double[groupSamples.Count];
            var allocatedBytes = new double[groupSamples.Count];
            var occurrences = new int[groupSamples.Count];
            for (var i = 0; i < groupSamples.Count; i++)
            {
                timeMilliseconds[i] = groupSamples[i].Seconds * 1_000d;
                allocatedBytes[i] = groupSamples[i].AllocatedBytes;
                occurrences[i] = groupSamples[i].Occurrences;
            }

            profileResults[group] = new ProfileGroupResult(
                ProfileGroups[group],
                CalculateReferenceStats(timeMilliseconds),
                CalculateReferenceStats(allocatedBytes),
                CalculateStats(occurrences));
        }

        var glStats = CalculateStats(gl);
        return new PhaseResult(
            name,
            glStats.Stability >= 0.95,
            glStats,
            CalculateStats(clyde),
            CalculateStats(batches),
            CalculateStats(viewportLights),
            CalculateReferenceStats(frameMilliseconds),
            CalculateReferenceStats(framesPerSecond),
            profileResults);
    }

    private static MetricSummary CalculateStats(int[] values)
    {
        Array.Sort(values);
        var mode = values[0];
        var modeCount = 1;
        var current = values[0];
        var currentCount = 1;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] == current)
            {
                currentCount++;
                continue;
            }

            if (currentCount > modeCount)
            {
                mode = current;
                modeCount = currentCount;
            }

            current = values[i];
            currentCount = 1;
        }

        if (currentCount > modeCount)
        {
            mode = current;
            modeCount = currentCount;
        }

        var middle = values.Length / 2;
        var median = values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2d
            : values[middle];
        return new MetricSummary(
            mode,
            median,
            values[0],
            values[^1],
            modeCount / (double) values.Length);
    }

    private static ReferenceMetricSummary CalculateReferenceStats(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        var median = values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2d
            : values[middle];
        var percentile95 = values[(int) Math.Ceiling(values.Length * 0.95) - 1];
        return new ReferenceMetricSummary(median, percentile95, values[0], values[^1]);
    }

    private void Finish()
    {
        var quit = _quitWhenFinished;

        try
        {
            var size = _clyde.ScreenSize;
            var result = new BenchmarkResult(
                "ScpLightingBenchmark",
                size.X,
                size.Y,
                CameraZoom,
                _configuration.GetCVar(Robust.Shared.CVars.LightResolutionScale),
                new BenchmarkConfiguration(
                    _configuration.GetCVar(CCVars.AmbientOcclusion),
                    _configuration.GetCVar(CCVars.OutlineEnabled),
                    _configuration.GetCVar(Robust.Shared.CVars.LightBlur),
                    _configuration.GetCVar(Robust.Shared.CVars.LightSoftShadows),
                    _configuration.GetCVar(ScpCCVars.FieldOfViewBlurEnabled),
                    _configuration.GetCVar(ScpCCVars.GrainToggleOverlay),
                    _configuration.GetCVar(ScpCCVars.SinCityToggleOverlay),
                    _configuration.GetCVar(ScpCCVars.MobShadowQuality),
                    _configuration.GetCVar(ScpCCVars.ObjectShadowQuality),
                    _configuration.GetCVar(ScpCCVars.LocalPlayerShadowOutsideFov),
                    _configuration.GetCVar(Robust.Shared.CVars.MaxLightCount),
                    _configuration.GetCVar(Robust.Shared.CVars.MaxLightRadius),
                    _configuration.GetCVar(Robust.Shared.CVars.MaxShadowcastingLights),
                    _configuration.GetCVar(Robust.Shared.CVars.MaxOccluderCount)),
                _lightCount,
                _occluderCount,
                _casterCount,
                _warmupFrames,
                _sampleFrames,
                _phaseResults.ToArray());
            var json = SerializeResult(result);
            _logger.Info(ResultMarker + json);
        }
        finally
        {
            _state = BenchmarkState.Idle;
            RestoreEnvironment();
        }

        if (quit)
            _console.ExecuteCommand("quit");
    }

    private void Abort(string reason)
    {
        var quit = _quitWhenFinished;
        try
        {
            _logger.Error("SCP_LIGHT_BENCH_ERROR " + reason);
        }
        finally
        {
            _state = BenchmarkState.Idle;
            RestoreEnvironment();
        }

        if (quit)
            _console.ExecuteCommand("quit");
    }

    private static string SerializeResult(BenchmarkResult result)
    {
        var builder = new StringBuilder(2_048);
        builder.Append('{');
        AppendStringProperty(builder, "scene", result.Scene, false);
        AppendIntProperty(builder, "width", result.Width);
        AppendIntProperty(builder, "height", result.Height);
        AppendFloatProperty(builder, "cameraZoom", result.CameraZoom);
        AppendFloatProperty(builder, "lightResolutionScale", result.LightResolutionScale);

        builder.Append(",\"configuration\":{");
        var configuration = result.Configuration;
        AppendBoolProperty(builder, "ambientOcclusion", configuration.AmbientOcclusion, false);
        AppendBoolProperty(builder, "outlineEnabled", configuration.OutlineEnabled);
        AppendBoolProperty(builder, "lightBlur", configuration.LightBlur);
        AppendBoolProperty(builder, "softShadows", configuration.SoftShadows);
        AppendBoolProperty(builder, "fieldOfViewBlur", configuration.FieldOfViewBlur);
        AppendBoolProperty(builder, "grain", configuration.Grain);
        AppendBoolProperty(builder, "sinCity", configuration.SinCity);
        AppendIntProperty(builder, "mobShadowQuality", configuration.MobShadowQuality);
        AppendIntProperty(builder, "objectShadowQuality", configuration.ObjectShadowQuality);
        AppendBoolProperty(builder, "localPlayerShadowOutsideFov", configuration.LocalPlayerShadowOutsideFov);
        AppendIntProperty(builder, "maxLightCount", configuration.MaxLightCount);
        AppendFloatProperty(builder, "maxLightRadius", configuration.MaxLightRadius);
        AppendIntProperty(builder, "maxShadowcastingLights", configuration.MaxShadowcastingLights);
        AppendIntProperty(builder, "maxOccluderCount", configuration.MaxOccluderCount);
        builder.Append('}');

        AppendIntProperty(builder, "lightCount", result.LightCount);
        AppendIntProperty(builder, "occluderCount", result.OccluderCount);
        AppendIntProperty(builder, "casterCount", result.CasterCount);
        AppendIntProperty(builder, "warmupFrames", result.WarmupFrames);
        AppendIntProperty(builder, "sampleFrames", result.SampleFrames);
        builder.Append(",\"phases\":[");

        for (var i = 0; i < result.Phases.Length; i++)
        {
            if (i != 0)
                builder.Append(',');

            var phase = result.Phases[i];
            builder.Append('{');
            AppendStringProperty(builder, "name", phase.Name, false);
            AppendBoolProperty(builder, "stable", phase.Stable);
            AppendMetric(builder, "glDrawCalls", phase.GlDrawCalls);
            AppendMetric(builder, "clydeDrawCalls", phase.ClydeDrawCalls);
            AppendMetric(builder, "batches", phase.Batches);
            AppendMetric(builder, "viewportLights", phase.ViewportLights);
            AppendReferenceMetric(builder, "frameMilliseconds", phase.FrameMilliseconds);
            AppendReferenceMetric(builder, "framesPerSecond", phase.FramesPerSecond);
            builder.Append(",\"cpuGroups\":[");
            for (var groupIndex = 0; groupIndex < phase.ProfileGroups.Length; groupIndex++)
            {
                if (groupIndex != 0)
                    builder.Append(',');

                var group = phase.ProfileGroups[groupIndex];
                builder.Append('{');
                AppendStringProperty(builder, "name", group.Name, false);
                AppendReferenceMetric(builder, "timeMilliseconds", group.TimeMilliseconds);
                AppendReferenceMetric(builder, "allocatedBytes", group.AllocatedBytes);
                AppendMetric(builder, "occurrences", group.Occurrences);
                builder.Append('}');
            }
            builder.Append(']');
            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string name, MetricSummary metric)
    {
        builder.Append(",\"").Append(name).Append("\":{");
        AppendIntProperty(builder, "mode", metric.Mode, false);
        AppendDoubleProperty(builder, "median", metric.Median);
        AppendIntProperty(builder, "minimum", metric.Minimum);
        AppendIntProperty(builder, "maximum", metric.Maximum);
        AppendDoubleProperty(builder, "stability", metric.Stability);
        builder.Append('}');
    }

    private static void AppendReferenceMetric(
        StringBuilder builder,
        string name,
        ReferenceMetricSummary metric)
    {
        builder.Append(",\"").Append(name).Append("\":{");
        AppendDoubleProperty(builder, "median", metric.Median, false);
        AppendDoubleProperty(builder, "percentile95", metric.Percentile95);
        AppendDoubleProperty(builder, "minimum", metric.Minimum);
        AppendDoubleProperty(builder, "maximum", metric.Maximum);
        builder.Append('}');
    }

    private static void AppendStringProperty(
        StringBuilder builder,
        string name,
        string value,
        bool comma = true)
    {
        if (comma)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":\"").Append(value).Append('"');
    }

    private static void AppendBoolProperty(
        StringBuilder builder,
        string name,
        bool value,
        bool comma = true)
    {
        if (comma)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
    }

    private static void AppendIntProperty(
        StringBuilder builder,
        string name,
        int value,
        bool comma = true)
    {
        if (comma)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":").Append(value);
    }

    private static void AppendFloatProperty(
        StringBuilder builder,
        string name,
        float value,
        bool comma = true)
    {
        if (comma)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":")
            .Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void AppendDoubleProperty(
        StringBuilder builder,
        string name,
        double value,
        bool comma = true)
    {
        if (comma)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":")
            .Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private readonly record struct BenchmarkPhase(
        string Name,
        bool ContentLighting,
        bool LightEnabled,
        bool DrawLighting,
        bool DrawShadows,
        int ShadowQuality);

    private struct FrameMetrics
    {
        public int GlDrawCalls;
        public int ClydeDrawCalls;
        public int Batches;
        public int ViewportLights;
        public float FrameSeconds;
    }

    private struct ProfileFrameMetrics
    {
        public float Seconds;
        public long AllocatedBytes;
        public int Occurrences;
    }

    private sealed record MetricSummary(
        int Mode,
        double Median,
        int Minimum,
        int Maximum,
        double Stability);

    private sealed record PhaseResult(
        string Name,
        bool Stable,
        MetricSummary GlDrawCalls,
        MetricSummary ClydeDrawCalls,
        MetricSummary Batches,
        MetricSummary ViewportLights,
        ReferenceMetricSummary FrameMilliseconds,
        ReferenceMetricSummary FramesPerSecond,
        ProfileGroupResult[] ProfileGroups);

    private sealed record ProfileGroupResult(
        string Name,
        ReferenceMetricSummary TimeMilliseconds,
        ReferenceMetricSummary AllocatedBytes,
        MetricSummary Occurrences);

    private sealed record ReferenceMetricSummary(
        double Median,
        double Percentile95,
        double Minimum,
        double Maximum);

    private sealed record BenchmarkResult(
        string Scene,
        int Width,
        int Height,
        float CameraZoom,
        float LightResolutionScale,
        BenchmarkConfiguration Configuration,
        int LightCount,
        int OccluderCount,
        int CasterCount,
        int WarmupFrames,
        int SampleFrames,
        PhaseResult[] Phases);

    private sealed record BenchmarkConfiguration(
        bool AmbientOcclusion,
        bool OutlineEnabled,
        bool LightBlur,
        bool SoftShadows,
        bool FieldOfViewBlur,
        bool Grain,
        bool SinCity,
        int MobShadowQuality,
        int ObjectShadowQuality,
        bool LocalPlayerShadowOutsideFov,
        int MaxLightCount,
        float MaxLightRadius,
        int MaxShadowcastingLights,
        int MaxOccluderCount);

    private enum BenchmarkState : byte
    {
        Idle,
        WaitingForScene,
        WarmingUp,
        Sampling,
    }
}

[AnyCommand]
public sealed partial class ScpLightingBenchmarkCommand : IConsoleCommand
{
    [Dependency] private IEntitySystemManager _systems = default!;

    private static readonly object StartupRequestLock = new();
    private static BenchmarkLaunchRequest? _startupRequest;

    public string Command => "scplightbench";
    public string Description => "Runs the deterministic SCP lighting draw-call benchmark.";
    public string Help => "scplightbench [run] [all] [--warmup frames] [--frames frames] [--quit]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var warmup = 120;
        var frames = 600;
        var quit = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "run":
                case "all":
                    break;
                case "--quit":
                    quit = true;
                    break;
                case "--warmup" when i + 1 < args.Length && int.TryParse(args[++i], out warmup):
                    break;
                case "--frames" when i + 1 < args.Length && int.TryParse(args[++i], out frames):
                    break;
                default:
                    shell.WriteError(Help);
                    return;
            }
        }

        if (!_systems.TryGetEntitySystem<ScpLightingBenchmarkSystem>(out var benchmark))
        {
            lock (StartupRequestLock)
            {
                if (_startupRequest != null)
                {
                    shell.WriteError("The lighting benchmark is already queued for startup.");
                    return;
                }

                _startupRequest = new BenchmarkLaunchRequest(warmup, frames, quit);
            }

            shell.WriteLine("Lighting benchmark queued until client systems initialize.");
            return;
        }

        if (!benchmark.Start(warmup, frames, quit, out var error))
        {
            shell.WriteError(error ?? "Unable to start the lighting benchmark.");
            return;
        }

        shell.WriteLine("Lighting benchmark armed; waiting for ScpLightingBenchmark.");
    }

    internal static bool TryTakeStartupRequest(out BenchmarkLaunchRequest request)
    {
        lock (StartupRequestLock)
        {
            if (_startupRequest is not { } pending)
            {
                request = default;
                return false;
            }

            request = pending;
            _startupRequest = null;
            return true;
        }
    }
}

internal readonly record struct BenchmarkLaunchRequest(
    int WarmupFrames,
    int SampleFrames,
    bool QuitWhenFinished);
