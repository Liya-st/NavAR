using UnityEngine;
using UnityEngine.UIElements;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif
using NavAR.Core.State; // This is the state folder from Stage 3
using NavAR.Core.Interfaces; // For IQrScannerService
using NavAR.Presentation.Controllers;
using NavAR.Infrastructure; // For AlignmentService
using NavAR.Core.Entities; // For QRAnchor
using NavAR.Data; // For MockMapRepository
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NavAR.Infrastructure.Navigation;
using UnityEngine.SceneManagement;
using NavAR.Core.Navigation; // For graph routing
using NavAR.Bootstrapper;
using NavAR.Presentation.Navigation;
using NavAR.Presentation.Presenters;
using NavAR.Presentation.State;
using NavAR.Infrastructure.Backend;
#if !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace NavAR.Presentation
{
    [RequireComponent(typeof(UIDocument))]
    public class UIManager : MonoBehaviour, INavigationInstructionPresenter
    {
        private bool _initialized = false;
        private class DestinationGroup
        {
            public DestinationGroup(string groupId, string displayName, string category, int floorId)
            {
                GroupId = groupId;
                DisplayName = displayName;
                Category = category;
                FloorId = floorId;
                Entrances = new List<Destination>();
            }

            public string GroupId { get; }
            public string DisplayName { get; }
            public string Category { get; }
            public int FloorId { get; }
            public List<Destination> Entrances { get; }
        }

        private sealed class NavigationCompletionSnapshot
        {
            public string SessionId;
            public string DestinationId;
            public string DestinationName;
            public int FloorId;
        }

        private enum ContextualPanelKind
        {
            None,
            Help,
            FloorMap,
            About
        }

        private readonly struct UiHistoryEntry
        {
            public UiHistoryEntry(AppState state, ContextualPanelKind contextualPanel)
            {
                State = state;
                ContextualPanel = contextualPanel;
            }

            public AppState State { get; }
            public ContextualPanelKind ContextualPanel { get; }
        }

        [Header("Screen Assets")]
        [SerializeField] private VisualTreeAsset splashScreenAsset;
        [SerializeField] private VisualTreeAsset homeScreenAsset;
        [SerializeField] private VisualTreeAsset destinationScreenAsset;
        [SerializeField] private VisualTreeAsset settingsScreenAsset;
        [SerializeField] private VisualTreeAsset permissionScreenAsset;
        [SerializeField] private VisualTreeAsset qrScannerAsset;
        [SerializeField] private VisualTreeAsset arNavigationAsset;
        [SerializeField] private VisualTreeAsset positionLostAsset;
        [SerializeField] private VisualTreeAsset feedbackScreenAsset;
        [SerializeField] private VisualTreeAsset helpScreenAsset;
        [SerializeField] private VisualTreeAsset floorMapScreenAsset;
        [SerializeField] private VisualTreeAsset destinationReachedScreenAsset;
        [SerializeField] private VisualTreeAsset exitPromptAsset;
        [SerializeField] private VisualTreeAsset floorTransitionScreenAsset;
        [SerializeField] private VisualTreeAsset destinationItemAsset;

        [Header("Home Actions")]
        [SerializeField] private UnityEngine.UI.Button btnOutdoorNavigation;
        [Header("Navigation Scene Context")]
        [SerializeField] private NavigationSceneContext navigationSceneContext;

        [Header("Navigation Markers")]
        [SerializeField] private NavigationMarkerManager markerManager;

        [Header("Diagnostics")]
        [SerializeField] private bool enableUiDiagnostics = true;

        [Header("Transition Detection")]
        [SerializeField] private float transitionArrivalRadiusMeters = 1.5f;
        [Header("Editor QR Fallback")]
        [SerializeField] private bool useEditorManualStartAnchor = true;
        [SerializeField] private string editorManualStartAnchorId = "EDITOR-MANUAL-START";
        [SerializeField] private int editorManualStartFloorId = 1;
        [SerializeField] private Vector3 editorManualStartPosition = Vector3.zero;

        private VisualElement _contentContainer;
        private VisualElement _root;
        private const string HighContrastClass = "high-contrast";
        private const string SettingsPrefPrefix = "NavAR.Settings.";
        private bool _settingsLoaded;
        private Button _navHome;
        private Button _navExplore;
        private Button _navSettings;
        private VisualElement _bottomNavBar;
        private AppState _lastNonOverlayState = AppState.Home;
        private AppStateManager _stateManager;
        private IQrScannerService _qrScannerService;
        private IMapRepository _mapRepository; // Changed to interface type
        private AlignmentService _alignmentService;
        private IPathCalculator _pathCalculator;
        private HybridGraphPathCalculator _hybridCalculator;
        private IArRenderer _pathRenderer;
        private IFloorSceneTransitionService _floorTransitionService;
        private IEntranceSelector _entranceSelector;
        private Vector3 _pendingTransitionLandingPosition;
        private bool _hasPendingTransitionLanding;
        private INavigationCoordinator _navigationCoordinator;
        private QrNavigationCoordinator _qrNavigationCoordinator;
        private FloorTransitionCoordinator _floorTransitionCoordinator;
        private NavigationTransitionSequencer _navigationTransitionSequencer;
        private NavigationSequencer _navigationSequencer;
        private ScreenStateMachine _screenStateMachine;
        private Dictionary<AppState, IScreenPresenter> _screenPresenters;
        private AppState _activeRenderedState;
        private Coroutine _navigationDependencyResolveRoutine;
        private ServiceContainer _serviceContainer;
        private INavigationContextProvider _navigationContextProvider;
        private INavigationProgressTracker _navigationProgressTracker;
        private IGuidanceCueService _guidanceCueService;
        private Coroutine _dynamicNavigationRoutine;
        private string _latestInstructionText = "Continue.";
        private NavigationSessionService _navigationSessionService;
        private BackendApiClient _backendApiClient;
        private NavigationCompletionSnapshot _lastNavigationSnapshot;
        private readonly Stack<UiHistoryEntry> _uiHistory = new Stack<UiHistoryEntry>();
        private ContextualPanelKind _activeContextualPanel = ContextualPanelKind.None;
        private ContextualPanelKind _nextContextualPanel = ContextualPanelKind.None;
        private bool _isRestoringHistory;
        private VisualElement _exitPromptOverlay;
        private bool _hasSmoothedCameraPosition;
        private Vector3 _smoothedCameraPosition;
        private bool _hasLastDynamicUpdatePosition;
        private Vector3 _lastDynamicUpdatePosition;
        private bool _forceDynamicPathRedraw;
        [Header("Guidance")]
        [SerializeField] private bool hapticGuidanceEnabled = true;

        [Header("Dynamic Path Smoothing")]
        [SerializeField] private float dynamicPathSmoothingTimeSeconds = 0.3f;
        [SerializeField] private float dynamicPathMinUpdateDistanceMeters = 0.5f;

        [Header("Navigation Context Retry")]
        [SerializeField] private float navigationContextResolveTimeoutSeconds = 12f;
        [SerializeField] private float navigationContextResolvePollIntervalSeconds = 0.25f;

        private void Awake()
        {
            if (btnOutdoorNavigation != null)
            {
                btnOutdoorNavigation.onClick.AddListener(OnOutdoorNavigationClicked);
            }
        }

        public void Initialize(AppStateManager stateManager, IQrScannerService qrScannerService, IMapRepository mapRepository, IFloorSceneTransitionService floorTransitionService = null, ServiceContainer services = null)
        {
            // Allow safe re-binding without resetting UI state or resubscribing.
            var firstInit = !_initialized;

            if (firstInit)
            {
                _stateManager = stateManager;
                _qrScannerService = qrScannerService; // Save the reference
                _mapRepository = mapRepository; // Save the repository we were handed
            }

            // Always update references passed by the bootstrapper
            _stateManager = stateManager ?? _stateManager;
            _qrScannerService = qrScannerService ?? _qrScannerService;
            _mapRepository = mapRepository ?? _mapRepository;
            _floorTransitionService = floorTransitionService ?? _floorTransitionService;
            _serviceContainer = services ?? _serviceContainer;
            _navigationContextProvider = _serviceContainer?.Resolve<INavigationContextProvider>() ?? _navigationContextProvider;
            _navigationSessionService = _serviceContainer?.Resolve<NavigationSessionService>() ?? _navigationSessionService;
            _backendApiClient = _serviceContainer?.Resolve<BackendApiClient>() ?? _backendApiClient;

            // Resolve navigation services from the current active scene(s).
            // Additive loads may not have finished yet, so start a bounded retry in background.
            ResolveNavigationDependencies(logErrorIfMissing: false);
            ConfigureCoordinationServices(_serviceContainer);

            // Wire UI document and controls only once
            if (firstInit)
            {
                _root = GetComponent<UIDocument>().rootVisualElement;
                _contentContainer = _root.Q<VisualElement>("ContentContainer");

                if (_contentContainer == null)
                {
                    Debug.LogError("UIManager: ContentContainer was not found.");
                    return;
                }

                ValidateScreenAssetAssignments();

                ConfigureNavigationBar();

                _stateManager.OnStateChanged += HandleStateChange;
                ConfigureScreenPresenters();
                ConfigureScreenStateMachine();

                // Set initial screen
                _stateManager.SetState(AppState.Home);

                _initialized = true;
            }
        }

        private void ConfigureCoordinationServices(ServiceContainer services)
        {
            if (_entranceSelector == null && _pathCalculator != null)
            {
                _entranceSelector = new NavMeshEntranceSelector(_pathCalculator);
            }

            _navigationCoordinator = new NavigationCoordinator(_entranceSelector);
            _navigationSequencer ??= new NavigationSequencer(this);
            _qrNavigationCoordinator = new QrNavigationCoordinator(
                _stateManager,
                ResolveQrAnchor,
                () => _alignmentService,
                _floorTransitionService,
                ResetNavigationServiceReferences,
                EnsureNavigationServices,
                CalculatePathForCurrentFloor,
                (path, setNavigating) => StartCoroutine(WaitForAndDrawPath(path, setNavigating)),
                _navigationSequencer,
                msg => Debug.Log(msg),
                msg => Debug.LogError(msg)
            );
            _floorTransitionCoordinator = new FloorTransitionCoordinator(
                _stateManager,
                _floorTransitionService,
                EnsureNavigationServices,
                CalculatePathForCurrentFloor,
                (path, setNavigating) => StartCoroutine(WaitForAndDrawPath(path, setNavigating)),
                _navigationSequencer,
                ResetNavigationServiceReferences,
                SnapXrOriginToPendingTransitionLanding,
                () => navigationSceneContext,
                msg => Debug.Log(msg),
                msg => Debug.LogError(msg),
                msg => Debug.LogWarning(msg)
            );
            _navigationTransitionSequencer = new NavigationTransitionSequencer(
                this,
                _stateManager,
                () => _hybridCalculator,
                transitionArrivalRadiusMeters,
                BeginFloorTransition,
                landing =>
                {
                    _pendingTransitionLandingPosition = landing;
                    _hasPendingTransitionLanding = true;
                },
                msg =>
                {
                    if (enableUiDiagnostics)
                    {
                        Debug.Log(msg);
                    }
                });
            _navigationProgressTracker ??= new NavigationProgressTracker();
            _navigationProgressTracker.OnGuidanceEvent -= HandleGuidanceEvent;
            _navigationProgressTracker.OnGuidanceEvent += HandleGuidanceEvent;
            _guidanceCueService ??= new GuidanceCueService(this);
            services?.Register(_navigationCoordinator);
            services?.Register(_floorTransitionCoordinator);
        }

        private QRAnchor ResolveQrAnchor(string qrPayload)
        {
            var anchor = _mapRepository?.GetQRAnchor(qrPayload);
            if (anchor != null)
            {
                return anchor;
            }

#if UNITY_EDITOR
            if (useEditorManualStartAnchor)
            {
                Debug.LogWarning($"[UIManager] Using manual editor start anchor fallback for payload '{qrPayload}'.");
                return new QRAnchor
                {
                    qr_id = editorManualStartAnchorId,
                    floor_id = editorManualStartFloorId,
                    x = editorManualStartPosition.x,
                    y = editorManualStartPosition.y,
                    z = editorManualStartPosition.z
                };
            }
#endif
            return null;
        }

        private void ResetNavigationServiceReferences()
        {
            _pathRenderer = null;
            _pathCalculator = null;
            _alignmentService = null;
            _entranceSelector = null;
            _hybridCalculator = null;
            navigationSceneContext = null;
        }

        private void ConfigureScreenPresenters()
        {
            _screenPresenters = new Dictionary<AppState, IScreenPresenter>
            {
                [AppState.Splash] = new SplashScreenPresenter(splashScreenAsset),
                [AppState.Home] = new HomeScreenPresenter(homeScreenAsset, SetState, OnOpenHelpPanel, null),
                [AppState.Explore] = new ExploreScreenPresenter(destinationScreenAsset, SetState, PopulateDestinationList),
                [AppState.QrScanning] = new QrScanScreenPresenter(qrScannerAsset, _qrScannerService, OnQrCodeFound, SetState, HasCameraPermission),
                [AppState.Permission] = new PermissionScreenPresenter(permissionScreenAsset, SetState, RequestCameraPermission),
                [AppState.Navigating] = new NavigatingScreenPresenter(arNavigationAsset, SetState, () => _lastNonOverlayState, OnToggleVoiceGuidance, OnOpenFloorMap, EndNavigationEarly),
                [AppState.PositionLost] = new PositionLostScreenPresenter(positionLostAsset, SetState),
                [AppState.Settings] = new SettingsScreenPresenter(settingsScreenAsset, SetState, null, OnOpenAboutPanel, OnOpenHelpPanel, ApplySettings),
                [AppState.Feedback] = new FeedbackScreenPresenter(feedbackScreenAsset, SetState, () => _lastNonOverlayState, OnSubmitFeedback)
            };
        }

        private void EndNavigationEarly()
        {
            if (_stateManager == null)
            {
                return;
            }

            if (_stateManager.CurrentState == AppState.Navigating)
            {
                CompleteNavigationSession(SessionStatus.Cancelled);
                return;
            }

            SetState(AppState.Feedback);
        }

        private void ShowPresenter(AppState state)
        {
            if (_activeRenderedState != state
                && _screenPresenters != null
                && _screenPresenters.TryGetValue(_activeRenderedState, out var activePresenter)
                && activePresenter is IHideablePresenter hideablePresenter)
            {
                hideablePresenter.Hide();
            }

            if (_screenPresenters != null && _screenPresenters.TryGetValue(state, out var presenter))
            {
                presenter.Show(_contentContainer);
                _activeRenderedState = state;
                return;
            }

            Debug.LogError($"UIManager: No presenter registered for state {state}.");
        }

        private IEnumerator ResolveNavigationDependenciesWithRetry(bool logErrorOnTimeout)
        {
            var startedAt = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - startedAt < navigationContextResolveTimeoutSeconds)
            {
                if (ResolveNavigationDependencies(logErrorIfMissing: false))
                {
                    ConfigureCoordinationServices(_serviceContainer);
                    _navigationDependencyResolveRoutine = null;
                    yield break;
                }

                yield return new WaitForSeconds(navigationContextResolvePollIntervalSeconds);
            }

            if (logErrorOnTimeout)
            {
                ResolveNavigationDependencies(logErrorIfMissing: true);
            }
            _navigationDependencyResolveRoutine = null;
        }

        private bool ResolveNavigationDependencies(bool logErrorIfMissing = true)
        {
            var currentFloorId = _stateManager?.Context?.CurrentFloorId ?? 0;

            if (_navigationContextProvider != null)
            {
                _navigationContextProvider.Refresh();
                if (_navigationContextProvider.TryGetCurrent(out var resolvedContext))
                {
                    navigationSceneContext = resolvedContext;
                }
            }
            else if (navigationSceneContext == null)
            {
                foreach (var context in FindObjectsOfType<NavigationSceneContext>())
                {
                    if (context != null && context.gameObject.scene.IsValid() && context.gameObject.scene.isLoaded)
                    {
                        navigationSceneContext = context;
                        break;
                    }
                }
            }

            // When multiple additive scenes are loaded, provider may return a context from
            // a non-target scene. Prefer a context that can resolve services and (if known)
            // matches the current floor naming convention.
            if (!TrySelectBestNavigationContext(currentFloorId, out var bestContext))
            {
                bestContext = navigationSceneContext;
            }
            navigationSceneContext = bestContext;

            if (enableUiDiagnostics)
            {
                var contextName = navigationSceneContext != null ? navigationSceneContext.gameObject.name : "<none>";
                var contextScene = navigationSceneContext != null ? navigationSceneContext.gameObject.scene.name : "<none>";
                var contextActive = navigationSceneContext != null && navigationSceneContext.gameObject.activeInHierarchy;
                Debug.Log($"UIManager: NavigationSceneContext resolved -> {contextName} (scene={contextScene}, active={contextActive})");
            }

            if (navigationSceneContext == null)
            {
                if (logErrorIfMissing)
                {
                    Debug.LogError("UIManager: NavigationSceneContext is missing. Navigation services must be provided through NavigationSceneContext.");
                }
                return false;
            }

            if (!navigationSceneContext.TryResolve(out var sceneAlignmentService, out var scenePathCalculator, out var scenePathRenderer)
                || sceneAlignmentService == null
                || scenePathCalculator == null
                || scenePathRenderer == null)
            {
                var hasAlign = navigationSceneContext.AlignmentService != null;
                var hasCalc = navigationSceneContext.PathCalculator != null;
                var hasRenderer = navigationSceneContext.PathRenderer != null;
                if (logErrorIfMissing)
                {
                    Debug.LogError($"UIManager: NavigationSceneContext is incomplete. Alignment={hasAlign}, PathCalculator={hasCalc}, PathRenderer={hasRenderer}.");
                }
                return false;
            }

            _alignmentService = sceneAlignmentService;
            _pathCalculator = scenePathCalculator;
            _pathRenderer = scenePathRenderer;

            if (_entranceSelector == null)
            {
                _entranceSelector = new NavMeshEntranceSelector(_pathCalculator);
            }

            CreateHybridCalculator();

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Services -> Alignment={(_alignmentService != null)}, PathCalc={(_pathCalculator != null)}, Renderer={(_pathRenderer != null)}, EntranceSelector={(_entranceSelector != null)}, HybridCalc={(_hybridCalculator != null)}");
            }

            return true;
        }

        private bool TrySelectBestNavigationContext(int currentFloorId, out NavigationSceneContext bestContext)
        {
            bestContext = null;
            var allContexts = FindObjectsOfType<NavigationSceneContext>();
            if (allContexts == null || allContexts.Length == 0)
            {
                return false;
            }

            var activeScene = SceneManager.GetActiveScene();
            NavigationSceneContext activeSceneContext = null;

            NavigationSceneContext firstResolvable = null;
            foreach (var ctx in allContexts)
            {
                if (ctx == null || ctx.gameObject == null)
                {
                    continue;
                }

                var scene = ctx.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                if (!ctx.TryResolve(out var a, out var p, out var r) || a == null || p == null || r == null)
                {
                    continue;
                }

                firstResolvable ??= ctx;

                if (activeSceneContext == null && ctx.gameObject.scene == activeScene)
                {
                    activeSceneContext = ctx;
                }

                if (currentFloorId > 0 && scene.name.IndexOf($"Floor_{currentFloorId}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bestContext = ctx;
                    return true;
                }
            }

            if (activeSceneContext != null)
            {
                bestContext = activeSceneContext;
                return true;
            }

            bestContext = firstResolvable;
            return bestContext != null;
        }

        private void CreateHybridCalculator()
        {
            if (_hybridCalculator != null)
            {
                return; // Already created
            }

            if (_pathCalculator == null || _mapRepository == null)
            {
                if (enableUiDiagnostics)
                {
                    Debug.Log("UIManager: Cannot create hybrid calculator without pathCalculator and mapRepository.");
                }
                return;
            }

            try
            {
                var graphRouter = new DijkstraGraphRouter(_mapRepository, enableUiDiagnostics);
                _hybridCalculator = new HybridGraphPathCalculator(
                    _pathCalculator,
                    graphRouter,
                    (targetFloor, label, nodeId) => BeginFloorTransition(targetFloor, label, nodeId),
                    enableUiDiagnostics
                );

                if (enableUiDiagnostics)
                {
                    Debug.Log("UIManager: Hybrid calculator created successfully.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"UIManager: Failed to create hybrid calculator: {ex}");
            }
        }

        private bool EnsureNavigationServices()
        {
            if (_pathCalculator == null || _pathRenderer == null || _entranceSelector == null)
            {
                var resolved = ResolveNavigationDependencies(logErrorIfMissing: false);
                if (!resolved)
                {
                    if (_navigationDependencyResolveRoutine == null)
                    {
                        _navigationDependencyResolveRoutine = StartCoroutine(ResolveNavigationDependenciesWithRetry(logErrorOnTimeout: true));
                    }
                    return false;
                }

                ConfigureCoordinationServices(_serviceContainer);
            }

            var ready = _pathCalculator != null && _pathRenderer != null && _entranceSelector != null;
            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: EnsureNavigationServices -> {ready}. PathCalculator={_pathCalculator != null}, PathRenderer={_pathRenderer != null}, EntranceSelector={_entranceSelector != null}");
            }
            return ready;
        }

        private void OnDestroy()
        {
            if (_screenPresenters != null
                && _screenPresenters.TryGetValue(_activeRenderedState, out var activePresenter)
                && activePresenter is IHideablePresenter hideablePresenter)
            {
                hideablePresenter.Hide();
            }

            _navigationTransitionSequencer?.Stop();
            _navigationSequencer?.CancelAll();
            if (_navigationDependencyResolveRoutine != null)
            {
                StopCoroutine(_navigationDependencyResolveRoutine);
                _navigationDependencyResolveRoutine = null;
            }

            if (_stateManager != null)
            {
                _stateManager.OnStateChanged -= HandleStateChange;
            }

            if (btnOutdoorNavigation != null)
            {
                btnOutdoorNavigation.onClick.RemoveListener(OnOutdoorNavigationClicked);
            }
        }

        private void EnsureSettingsLoaded()
        {
            if (_settingsLoaded)
            {
                return;
            }

            LoadSettingsFromPrefs();
            ApplySettings(ScreenBinders.Settings);
            _settingsLoaded = true;
        }

        private void LoadSettingsFromPrefs()
        {
            var settings = ScreenBinders.Settings;
            settings.VoiceGuidanceEnabled = PlayerPrefs.GetInt(SettingsPrefPrefix + "VoiceGuidance", settings.VoiceGuidanceEnabled ? 1 : 0) == 1;
            settings.HighContrastEnabled = PlayerPrefs.GetInt(SettingsPrefPrefix + "HighContrast", settings.HighContrastEnabled ? 1 : 0) == 1;
            settings.ElevatorRoutingEnabled = PlayerPrefs.GetInt(SettingsPrefPrefix + "ElevatorRouting", settings.ElevatorRoutingEnabled ? 1 : 0) == 1;
            settings.TextScalePercent = Mathf.Clamp(PlayerPrefs.GetInt(SettingsPrefPrefix + "TextScalePercent", settings.TextScalePercent), 80, 140);
        }

        private void ApplySettings(ScreenBinders.SettingsState settings)
        {
            if (settings == null)
            {
                return;
            }

            if (_root != null)
            {
                if (settings.HighContrastEnabled)
                {
                    _root.AddToClassList(HighContrastClass);
                }
                else
                {
                    _root.RemoveFromClassList(HighContrastClass);
                }
            }

            var document = GetComponent<UIDocument>();
            if (document != null && document.panelSettings != null)
            {
                var scale = Mathf.Clamp(settings.TextScalePercent / 100f, 0.8f, 1.4f);
                document.panelSettings.scale = scale;
            }

            PersistSettings(settings);
        }

        private void PersistSettings(ScreenBinders.SettingsState settings)
        {
            PlayerPrefs.SetInt(SettingsPrefPrefix + "VoiceGuidance", settings.VoiceGuidanceEnabled ? 1 : 0);
            PlayerPrefs.SetInt(SettingsPrefPrefix + "HighContrast", settings.HighContrastEnabled ? 1 : 0);
            PlayerPrefs.SetInt(SettingsPrefPrefix + "ElevatorRouting", settings.ElevatorRoutingEnabled ? 1 : 0);
            PlayerPrefs.SetInt(SettingsPrefPrefix + "TextScalePercent", settings.TextScalePercent);
            PlayerPrefs.Save();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                HandleAndroidBackButton();
            }
#else
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                HandleAndroidBackButton();
            }
#endif
        }

        private void HandleStateChange(AppState newState)
        {
            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Entering state '{newState}'.");
            }

            DismissExitPrompt();

            if (newState == AppState.ComingSoon)
            {
                _activeContextualPanel = ResolveContextualPanelForNextRoute();
            }
            else
            {
                _activeContextualPanel = ContextualPanelKind.None;
                _nextContextualPanel = ContextualPanelKind.None;
            }

            if (newState == AppState.Home)
            {
                _uiHistory.Clear();
            }

            _contentContainer.Clear();
            UpdateNavigationBarActive(newState);
            EnsureSettingsLoaded();

            if (!IsOverlayState(newState))
            {
                _lastNonOverlayState = newState;
            }

            if (newState != AppState.Navigating)
            {
                _navigationTransitionSequencer?.Stop();
                StopDynamicNavigationLoop(clearInstruction: false);
            }

            if (newState != AppState.QrScanning)
            {
                _navigationSequencer?.CancelQrFlow();
            }

            if (newState != AppState.FloorTransition && newState != AppState.Navigating)
            {
                _navigationSequencer?.CancelFloorContinuation();
            }

            _screenStateMachine?.Execute(newState);
            if (newState == AppState.Home)
            {
                ConfigureHomeScreenActions();
            }

            if (newState == AppState.Navigating)
            {
                SyncNavigationUi();
                StartDynamicNavigationLoop();
            }
        }

        private void ConfigureNavigationBar()
        {
            _navHome = _root.Q<Button>("NavHome");
            _navExplore = _root.Q<Button>("NavExplore");
            _navSettings = _root.Q<Button>("NavSettings");
            _bottomNavBar = _root.Q<VisualElement>("BottomNavBar");

            var removedNavItem = _root.Q<Button>("NavSaved");
            if (removedNavItem != null)
            {
                removedNavItem.style.display = DisplayStyle.None;
                removedNavItem.pickingMode = PickingMode.Ignore;
            }

            if (_navHome != null)
            {
                _navHome.clicked += () => SetState(AppState.Home);
            }

            if (_navExplore != null)
            {
                _navExplore.clicked += () => SetState(AppState.Explore);
            }

            if (_navSettings != null)
            {
                _navSettings.clicked += () => SetState(AppState.Settings);
            }
        }

        private void UpdateNavigationBarActive(AppState state)
        {
            var isMainScreen = state == AppState.Home
                || state == AppState.Explore
                || state == AppState.DestinationSelection
                || state == AppState.Settings
                || state == AppState.ComingSoon; // Keep bottom nav visible for contextual panels (Help, FloorMap, About)

            if (_bottomNavBar != null)
            {
                _bottomNavBar.style.display = isMainScreen ? DisplayStyle.Flex : DisplayStyle.None;
            }

            SetNavItemActive(_navHome, isMainScreen && state == AppState.Home);
            SetNavItemActive(_navExplore, isMainScreen && (state == AppState.Explore || state == AppState.DestinationSelection));
            SetNavItemActive(_navSettings, isMainScreen && state == AppState.Settings);
        }

        private static void SetNavItemActive(Button navButton, bool isActive)
        {
            if (navButton == null)
            {
                return;
            }

            const string activeClass = "nav-item-active";
            if (isActive)
            {
                navButton.AddToClassList(activeClass);
            }
            else
            {
                navButton.RemoveFromClassList(activeClass);
            }
        }

        private void ConfigureHomeScreenActions()
        {
            var outdoorButton = _contentContainer.Q<Button>("BtnOutdoorNavigation");
            if (outdoorButton == null)
            {
                return;
            }

            outdoorButton.clicked += OnOutdoorNavigationClicked;
        }

        private void ConfigureScreenStateMachine()
        {
            _screenStateMachine = new ScreenStateMachine();
            _screenStateMachine.SetTransitionGuard((from, to) => true);

            _screenStateMachine.RegisterAllowedTransition(AppState.Splash, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.Home, AppState.Explore, AppState.Settings, AppState.ComingSoon, AppState.QrScanning);
            _screenStateMachine.RegisterAllowedTransition(AppState.Explore, AppState.Home, AppState.QrScanning, AppState.Settings, AppState.ComingSoon);
            _screenStateMachine.RegisterAllowedTransition(AppState.QrScanning, AppState.Permission, AppState.Explore, AppState.Navigating, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.Permission, AppState.QrScanning, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.Navigating, AppState.Explore, AppState.Home, AppState.DestinationReached, AppState.Feedback, AppState.PositionLost, AppState.FloorTransition, AppState.QrScanning, AppState.ComingSoon, AppState.Settings);
            _screenStateMachine.RegisterAllowedTransition(AppState.FloorTransition, AppState.Navigating, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.PositionLost, AppState.QrScanning, AppState.Navigating, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.Settings, AppState.Home, AppState.Explore, AppState.Navigating, AppState.ComingSoon);
            _screenStateMachine.RegisterAllowedTransition(AppState.DestinationReached, AppState.Home);
            _screenStateMachine.RegisterAllowedTransition(AppState.Feedback, AppState.Home, AppState.Explore, AppState.Navigating);
            _screenStateMachine.RegisterAllowedTransition(AppState.ComingSoon, AppState.Home, AppState.Explore, AppState.Navigating, AppState.Settings, AppState.Feedback);

            _screenStateMachine.Register(AppState.Splash, () => ShowPresenter(AppState.Splash));
            _screenStateMachine.Register(AppState.Home, () => ShowPresenter(AppState.Home));
            _screenStateMachine.Register(AppState.Explore, () => ShowPresenter(AppState.Explore));
            _screenStateMachine.Register(AppState.QrScanning, () => ShowPresenter(AppState.QrScanning));
            _screenStateMachine.Register(AppState.Permission, () => ShowPresenter(AppState.Permission));
            _screenStateMachine.Register(AppState.Navigating, () => ShowPresenter(AppState.Navigating));
            _screenStateMachine.Register(AppState.FloorTransition, ShowFloorTransitionScreen);
            _screenStateMachine.Register(AppState.PositionLost, () => ShowPresenter(AppState.PositionLost));
            _screenStateMachine.Register(AppState.Settings, () => ShowPresenter(AppState.Settings));
            _screenStateMachine.Register(AppState.DestinationReached, ShowDestinationReachedScreen);
            _screenStateMachine.Register(AppState.Feedback, () => ShowPresenter(AppState.Feedback));
            _screenStateMachine.Register(AppState.ComingSoon, ShowContextualPanel);
            _screenStateMachine.RegisterFallback(() => ShowPresenter(AppState.Home));
        }

        private void ShowScreen(VisualTreeAsset asset)
        {
            if (asset == null)
            {
                Debug.LogError("UIManager: Attempted to show a screen with a null VisualTreeAsset. Check inspector assignments.");
                return;
            }

            var instance = asset.Instantiate();
            if (instance == null)
            {
                Debug.LogError($"UIManager: Failed to instantiate VisualTreeAsset '{asset.name}'.");
                return;
            }

            instance.style.flexGrow = 1; // Make it fill the screen
            _contentContainer.Add(instance);

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Rendered screen asset '{asset.name}'.");
            }
        }

        private void ShowFloorTransitionScreen()
        {
            var promptFloorId = _stateManager?.Context?.PendingFloorId ?? 0;
            var promptLabel = _stateManager?.Context?.PendingFloorLabel;
            var transitionNode = _stateManager?.Context?.PendingTransitionNodeId;

            if (floorTransitionScreenAsset == null)
            {
                Debug.LogError("UIManager: floorTransitionScreenAsset is not assigned.");
                return;
            }

            var instance = floorTransitionScreenAsset.Instantiate();
            if (instance == null)
            {
                Debug.LogError("UIManager: Failed to instantiate floorTransitionScreenAsset.");
                return;
            }

            instance.style.flexGrow = 1;

            var messageText = string.IsNullOrWhiteSpace(promptLabel)
                ? $"Click the button when you are at floor {promptFloorId}."
                : $"Click the button when you are at {promptLabel}.";

            if (!string.IsNullOrWhiteSpace(transitionNode))
            {
                messageText += $"\nTransition node: {transitionNode}";
            }

            var messageLabel = instance.Q<Label>("FloorTransitionMessage");
            if (messageLabel != null)
            {
                messageLabel.text = messageText;
            }

            var confirmButton = instance.Q<Button>("BtnConfirmFloorTransition");
            if (confirmButton != null)
            {
                confirmButton.text = $"I am at floor {promptFloorId}";
                confirmButton.clicked += ConfirmFloorTransition;
            }

            _contentContainer.Add(instance);

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Showing floor transition prompt for floor {promptFloorId}.");
            }
        }

        private void ShowDestinationReachedScreen()
        {
            var destinationName = _lastNavigationSnapshot?.DestinationName;
            if (string.IsNullOrWhiteSpace(destinationName))
            {
                destinationName = _stateManager?.Context?.CurrentDestination?.name;
            }

            if (string.IsNullOrWhiteSpace(destinationName))
            {
                destinationName = "your destination";
            }

            var asset = destinationReachedScreenAsset != null
                ? destinationReachedScreenAsset
                : Resources.Load<VisualTreeAsset>("UI/DestinationReachedScreen");
            if (asset == null)
            {
                Debug.LogError("UIManager: Missing destination reached screen asset.");
                return;
            }

            var instance = asset.Instantiate();
            instance.style.flexGrow = 1;

            var destinationLabel = instance.Q<Label>("DestinationReachedName");
            if (destinationLabel != null)
            {
                destinationLabel.text = $"You have arrived at {destinationName}.";
            }

            var returnButton = instance.Q<Button>("BtnReturnHomeFromDestinationReached");
            if (returnButton != null)
            {
                returnButton.clicked += ReturnHomeFromDestinationReached;
            }

            _contentContainer.Add(instance);

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Showing destination reached screen for '{destinationName}'.");
            }
        }

        private void ShowContextualPanel()
        {
            var panel = _activeContextualPanel == ContextualPanelKind.None
                ? ContextualPanelKind.Help
                : _activeContextualPanel;

            var asset = ResolveContextualScreenAsset(panel);
            if (asset == null)
            {
                Debug.LogError($"UIManager: Missing contextual screen asset for {panel}.");
                return;
            }

            var instance = asset.Instantiate();
            instance.style.flexGrow = 1;
            _contentContainer.Add(instance);
            BindContextualScreen(panel, instance);
        }

        private VisualTreeAsset ResolveContextualScreenAsset(ContextualPanelKind panel)
        {
            switch (panel)
            {
                case ContextualPanelKind.Help:
                    return helpScreenAsset != null ? helpScreenAsset : Resources.Load<VisualTreeAsset>("UI/HelpScreen");
                case ContextualPanelKind.FloorMap:
                    return floorMapScreenAsset != null ? floorMapScreenAsset : Resources.Load<VisualTreeAsset>("UI/FloorMapScreen");
                case ContextualPanelKind.About:
                    return Resources.Load<VisualTreeAsset>("UI/AboutScreen");
                default:
                    return helpScreenAsset != null ? helpScreenAsset : Resources.Load<VisualTreeAsset>("UI/HelpScreen");
            }
        }

        private void BindContextualScreen(ContextualPanelKind panel, VisualElement root)
        {
            var backButton = root.Q<Button>("BtnScreenBack");
            if (backButton != null)
            {
                backButton.clicked += HandleAndroidBackButton;
            }

            var startNavigation = root.Q<Button>("BtnContextStartNavigation");
            if (startNavigation != null)
            {
                startNavigation.clicked += () => SetState(AppState.Explore);
            }

            var openSettings = root.Q<Button>("BtnContextOpenSettings");
            if (openSettings != null)
            {
                openSettings.clicked += () => SetState(AppState.Settings);
            }

            var rescan = root.Q<Button>("BtnContextRescan");
            if (rescan != null)
            {
                rescan.clicked += () => SetState(AppState.QrScanning);
            }

            var resumeNavigation = root.Q<Button>("BtnContextResumeNavigation");
            if (resumeNavigation != null)
            {
                resumeNavigation.clicked += HandleAndroidBackButton;
            }

            var visitWebsite = root.Q<Button>("BtnVisitWebsite");
            if (visitWebsite != null)
            {
                visitWebsite.clicked += () => Application.OpenURL("https://navar.example");
            }

            if (panel == ContextualPanelKind.FloorMap)
            {
                PopulateFloorMapScreen(root);
            }
        }

        private void PopulateFloorMapScreen(VisualElement root)
        {
            var destination = _stateManager?.Context?.CurrentDestination;
            var destinationValue = root.Q<Label>("FloorMapDestinationValue");
            if (destinationValue != null)
            {
                destinationValue.text = destination?.name ?? "No active destination";
            }

            var floorValue = root.Q<Label>("FloorMapFloorValue");
            if (floorValue != null)
            {
                floorValue.text = $"Floor {_stateManager?.Context?.CurrentFloorId ?? 0}";
            }

            var routeValue = root.Q<Label>("FloorMapRouteValue");
            if (routeValue != null)
            {
                routeValue.text = _navigationProgressTracker != null && _navigationProgressTracker.HasActiveRoute
                    ? "AR route active"
                    : "Route not active";
            }
        }

        private void PopulateDestinationList()
        {
            var listContainer = _contentContainer.Q<ScrollView>("DestinationListContainer");
            if (listContainer == null) return;

            listContainer.Clear();
            var destinations = _mapRepository.GetAllDestinations();
            var groups = BuildDestinationGroups(destinations);

            foreach (var group in groups)
            {
                // CRITICAL: We must store the data in a local variable inside the loop, 
                // otherwise every button will think it is the LAST item in the list!
                DestinationGroup localGroup = group;

                var itemInstance = destinationItemAsset.Instantiate();

                var nameLabel = itemInstance.Q<Label>("DestinationNameLabel");
                var descLabel = itemInstance.Q<Label>("DestinationDescLabel");

                if (nameLabel != null) nameLabel.text = localGroup.DisplayName;
                if (descLabel != null) descLabel.text = $"{localGroup.Category} - Floor {localGroup.FloorId}";

                // Force the item to catch mouse/touch interactions
                itemInstance.pickingMode = PickingMode.Position;

                // In destination selection, allow user to select ANY destination
                // Floor will be loaded after QR scan. Don't check floor load state here.
                itemInstance.RegisterCallback<PointerUpEvent>(evt =>
                {
                    Debug.Log($"[UI] Clicked on: {localGroup.DisplayName}");
                    OnDestinationGroupSelected(localGroup);
                });

                listContainer.Add(itemInstance);
            }
        }

        private List<DestinationGroup> BuildDestinationGroups(List<Destination> destinations)
        {
            var groups = new Dictionary<string, DestinationGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var destination in destinations)
            {
                var baseKey = NormalizeDestinationBase(destination.destination_id);
                if (string.IsNullOrWhiteSpace(baseKey))
                {
                    baseKey = NormalizeDestinationBase(destination.name);
                }

                if (string.IsNullOrWhiteSpace(baseKey))
                {
                    baseKey = destination.destination_id ?? destination.name ?? "Unknown";
                }

                if (!groups.TryGetValue(baseKey, out var group))
                {
                    var displayName = baseKey.Trim();
                    group = new DestinationGroup(baseKey, displayName, destination.category, destination.floor_id);
                    groups.Add(baseKey, group);
                }

                group.Entrances.Add(destination);
            }

            return groups.Values
                .OrderBy(g => g.DisplayName)
                .ToList();
        }

        private bool IsFloorLoaded(int floorId)
        {
            if (floorId < 0) return false;

            // Convention: floor scenes are named 'Floor_<id>' (e.g. Floor_1)
            var sceneName = $"Floor_{floorId}";
            var scene = SceneManager.GetSceneByName(sceneName);
            if (scene.IsValid() && scene.isLoaded)
            {
                return true;
            }

            return false;
        }

        private static string NormalizeDestinationBase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var trimmed = input.Trim();
            return Regex.Replace(trimmed, "-door-\\d+$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private bool HasCameraPermission()
        {
            // If we are running in the Unity Editor, we just pretend we have permission 
            // so we don't get stuck during testing.
            #if UNITY_EDITOR
            return true;
            #else
            // This is the actual check for Android/iOS devices
            return Permission.HasUserAuthorizedPermission(Permission.Camera);
            #endif
        }

        private void ValidateScreenAssetAssignments()
        {
            ValidateAsset(splashScreenAsset, nameof(splashScreenAsset));
            ValidateAsset(homeScreenAsset, nameof(homeScreenAsset));
            ValidateAsset(destinationScreenAsset, nameof(destinationScreenAsset));
            ValidateAsset(settingsScreenAsset, nameof(settingsScreenAsset));
            ValidateAsset(permissionScreenAsset, nameof(permissionScreenAsset));
            ValidateAsset(qrScannerAsset, nameof(qrScannerAsset));
            ValidateAsset(arNavigationAsset, nameof(arNavigationAsset));
            ValidateAsset(positionLostAsset, nameof(positionLostAsset));
            ValidateAsset(feedbackScreenAsset, nameof(feedbackScreenAsset));
            ValidateAsset(floorTransitionScreenAsset, nameof(floorTransitionScreenAsset));
            ValidateAsset(destinationItemAsset, nameof(destinationItemAsset));
        }

        private void ValidateAsset(VisualTreeAsset asset, string fieldName)
        {
            if (asset == null)
            {
                Debug.LogWarning($"UIManager: '{fieldName}' is not assigned in the inspector.");
                return;
            }

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: '{fieldName}' assigned to '{asset.name}'.");
            }
        }

        private static bool IsOverlayState(AppState state)
        {
            return state == AppState.Permission
                || state == AppState.QrScanning
                || state == AppState.FloorTransition
                || state == AppState.PositionLost
                || state == AppState.DestinationReached
                || state == AppState.Feedback
                || state == AppState.ComingSoon;
        }

        private void SetState(AppState state)
        {
            var currentState = _stateManager.CurrentState;
            var currentContextPanel = _activeContextualPanel;

            if (state == AppState.ComingSoon && _nextContextualPanel == ContextualPanelKind.None)
            {
                _nextContextualPanel = ContextualPanelKind.Help;
            }

            if (_screenStateMachine != null)
            {
                if (!_screenStateMachine.TryTransition(currentState, state, out var resolvedState))
                {
                    Debug.LogWarning($"UIManager: Blocked invalid transition '{currentState}' -> '{state}'.");
                    return;
                }

                state = resolvedState;
            }

            if (!_isRestoringHistory && ShouldPushHistory(currentState, state))
            {
                _uiHistory.Push(new UiHistoryEntry(currentState, currentContextPanel));
            }

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: SetState requested -> '{currentState}' to '{state}'.");
            }
            _stateManager.SetState(state);
        }

        private bool ShouldPushHistory(AppState current, AppState next)
        {
            if (current == AppState.Splash || current == next)
            {
                return false;
            }

            if (current == AppState.Home && next == AppState.Home)
            {
                return false;
            }

            return true;
        }

        private ContextualPanelKind ResolveContextualPanelForNextRoute()
        {
            var panel = _nextContextualPanel;
            _nextContextualPanel = ContextualPanelKind.None;
            return panel == ContextualPanelKind.None ? ContextualPanelKind.Help : panel;
        }

        private void NavigateToContextualPanel(ContextualPanelKind panel)
        {
            _nextContextualPanel = panel;
            SetState(AppState.ComingSoon);
        }

        private void HandleAndroidBackButton()
        {
            if (_exitPromptOverlay != null)
            {
                DismissExitPrompt();
                return;
            }

            if (_stateManager == null)
            {
                return;
            }

            if (_stateManager.CurrentState == AppState.Home)
            {
                ShowExitPrompt();
                return;
            }

            if (_uiHistory.Count > 0)
            {
                RestoreHistoryEntry(_uiHistory.Pop());
                return;
            }

            _isRestoringHistory = true;
            try
            {
                _stateManager.SetState(AppState.Home);
            }
            finally
            {
                _isRestoringHistory = false;
            }
        }

        private void RestoreHistoryEntry(UiHistoryEntry entry)
        {
            _isRestoringHistory = true;
            _nextContextualPanel = entry.ContextualPanel;
            try
            {
                if (_stateManager.CurrentState == entry.State)
                {
                    HandleStateChange(entry.State);
                }
                else
                {
                    _stateManager.SetState(entry.State);
                }
            }
            finally
            {
                _isRestoringHistory = false;
            }
        }

        private void ShowExitPrompt()
        {
            if (_root == null || _exitPromptOverlay != null)
            {
                return;
            }

            var asset = exitPromptAsset != null
                ? exitPromptAsset
                : Resources.Load<VisualTreeAsset>("UI/ExitPrompt");
            if (asset == null)
            {
                Debug.LogError("UIManager: Missing exit prompt asset.");
                return;
            }

            var overlay = asset.Instantiate();
            overlay.style.flexGrow = 1;

            var stayButton = overlay.Q<Button>("BtnExitPromptStay");
            if (stayButton != null)
            {
                stayButton.clicked += DismissExitPrompt;
            }

            var exitButton = overlay.Q<Button>("BtnExitPromptExit");
            if (exitButton != null)
            {
                exitButton.clicked += ExitApplication;
            }

            _root.Add(overlay);
            _exitPromptOverlay = overlay;
        }

        private void DismissExitPrompt()
        {
            if (_exitPromptOverlay == null)
            {
                return;
            }

            _exitPromptOverlay.RemoveFromHierarchy();
            _exitPromptOverlay = null;
        }

        private void ExitApplication()
        {
            DismissExitPrompt();
            Application.Quit();
        }

        private void RequestCameraPermission()
        {
            Debug.Log("User clicked Allow Camera.");

            // Request the physical permission from the OS
            #if !UNITY_EDITOR
            Permission.RequestUserPermission(Permission.Camera);
            #endif
        }

        private void OnToggleVoiceGuidance()
        {
            ScreenBinders.Settings.VoiceGuidanceEnabled = !ScreenBinders.Settings.VoiceGuidanceEnabled;
            Debug.Log($"Voice guidance {(ScreenBinders.Settings.VoiceGuidanceEnabled ? "enabled" : "disabled")}.");
        }

        private void OnOpenFloorMap()
        {
            NavigateToContextualPanel(ContextualPanelKind.FloorMap);
        }

        public void BeginFloorTransition(int targetFloorId, string targetFloorLabel = null, string transitionNodeId = null)
        {
            _floorTransitionCoordinator?.BeginFloorTransition(targetFloorId, targetFloorLabel, transitionNodeId);
        }

        private void ConfirmFloorTransition()
        {
            _floorTransitionCoordinator?.ConfirmFloorTransition();
        }

        private void SnapXrOriginToPendingTransitionLanding()
        {
            if (!_hasPendingTransitionLanding)
            {
                return;
            }

            if (_alignmentService != null)
            {
                var didSnapWithAlignment = _alignmentService.RecenterToWorldPosition(_pendingTransitionLandingPosition, resetSession: true);
                if (didSnapWithAlignment)
                {
                    _hasPendingTransitionLanding = false;
                    if (enableUiDiagnostics)
                    {
                        Debug.Log($"UIManager: Snapped XR Origin via AlignmentService to transition landing {_pendingTransitionLandingPosition}.");
                    }
                    return;
                }
            }

            // Try common XR origin names first and log each step for diagnostics
            Transform xrOrigin = null;

            var foundByName = GameObject.Find("XROrigin");
            if (foundByName != null)
            {
                xrOrigin = foundByName.transform;
                if (enableUiDiagnostics)
                {
                    Debug.Log($"[UIManager] Found XR Origin by name: XROrigin (scene={foundByName.scene.name}, id={foundByName.GetInstanceID()})");
                }
            }

            if (xrOrigin == null)
            {
                var arSessionOrigin = FindObjectOfType<UnityEngine.XR.ARFoundation.ARSessionOrigin>();
                if (arSessionOrigin != null)
                {
                    xrOrigin = arSessionOrigin.transform;
                    if (enableUiDiagnostics)
                    {
                        Debug.Log($"[UIManager] Found ARSessionOrigin instance (scene={arSessionOrigin.gameObject.scene.name}, id={arSessionOrigin.gameObject.GetInstanceID()})");
                    }
                }
            }

            if (xrOrigin == null)
            {
                // Last resort: search for any object named similarly
                var possible = GameObject.FindObjectsOfType<Transform>().FirstOrDefault(t => t.name.IndexOf("xr", StringComparison.OrdinalIgnoreCase) >= 0);
                if (possible != null)
                {
                    xrOrigin = possible;
                    if (enableUiDiagnostics)
                    {
                        Debug.Log($"[UIManager] Found XR-like transform by heuristic: {possible.name} (scene={possible.gameObject.scene.name}, id={possible.gameObject.GetInstanceID()})");
                    }
                }
            }

            if (xrOrigin == null)
            {
                Debug.LogWarning("[UIManager] Could not locate XR Origin for transition landing snap. Checked 'XROrigin', 'ARSessionOrigin' and heuristics.");
                return;
            }

            xrOrigin.position = _pendingTransitionLandingPosition;
            _hasPendingTransitionLanding = false;

            if (enableUiDiagnostics)
            {
                Debug.Log($"UIManager: Snapped XR Origin to transition landing {_pendingTransitionLandingPosition}.");
            }
        }

        private List<Vector3> CalculatePathForCurrentFloor(
            Vector3 startPos,
            Vector3 targetPos,
            int floorId,
            int? destinationFloorId = null,
            IReadOnlyList<string> destinationNodeIds = null)
        {
            if (_hybridCalculator != null)
            {
                return _hybridCalculator.CalculatePathWithContext(startPos, targetPos, floorId, destinationFloorId, destinationNodeIds);
            }

            if (_pathCalculator != null)
            {
                return _pathCalculator.CalculatePath(startPos, targetPos);
            }

            return null;
        }

        /// <summary>
        /// Safely draws a path, handling cases where the renderer may have been destroyed.
        /// </summary>
        private void SafeDrawPath(List<Vector3> pathCorners)
        {
            if (pathCorners == null || pathCorners.Count == 0)
            {
                return;
            }

            if (_pathRenderer == null && !ResolveNavigationDependencies(logErrorIfMissing: false))
            {
                Debug.LogError("[UIManager] Path renderer unavailable because NavigationSceneContext could not be resolved.");
                return;
            }

            try
            {
                _pathRenderer.DrawPath(pathCorners);
            }
            catch (System.MissingMemberException mme)
            {
                Debug.LogError($"[UIManager] MissingMemberException drawing path: {mme.Message}. Clearing reference and aborting draw.");
                _pathRenderer = null;
            }
            catch (UnityEngine.MissingReferenceException mre)
            {
                Debug.LogError($"[UIManager] MissingReferenceException drawing path: {mre.Message}. Renderer destroyed. Clearing reference.");
                _pathRenderer = null;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[UIManager] Error drawing path: {ex.Message}. Renderer may have been destroyed. Clearing reference.");
                _pathRenderer = null;
            }
        }

        /// <summary>
        /// Waits for an `ArPathRenderer` to be available (up to a timeout) and draws the path.
        /// Retries across frames instead of throwing immediately so transient scene unloads
        /// won't crash or cause state fallback.
        /// </summary>
        private IEnumerator WaitForAndDrawPath(List<Vector3> pathCorners, bool setNavigatingOnSuccess = false, int maxAttempts = 60)
        {
            if (pathCorners == null || pathCorners.Count == 0)
            {
                yield break;
            }

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (_pathRenderer == null && !ResolveNavigationDependencies(logErrorIfMissing: false))
                {
                    yield return null;
                    continue;
                }

                    if (_pathRenderer != null)
                {
                    try
                    {
                        var canCompleteNavigation = true;
                        if (_hybridCalculator != null
                            && _hybridCalculator.TryGetPendingTransition(
                                out _,
                                out _,
                                out _,
                                out _,
                                out _))
                        {
                            canCompleteNavigation = false;
                        }

                        _navigationProgressTracker?.InitializeRoute(pathCorners, canCompleteNavigation);
                        UpdateRouteNodeIdsForSession();
                        UpdateNavigationMarkersForActiveRoute();
                        if (setNavigatingOnSuccess || (_navigationSessionService != null && !_navigationSessionService.HasActiveSession))
                        {
                            EnsureNavigationSessionStarted();
                        }
                        _navigationProgressTracker?.Tick(Camera.main != null ? Camera.main.transform.position : pathCorners[0], Camera.main != null ? Camera.main.transform.forward : Vector3.forward, 0f);
                        _pathRenderer.DrawPath(pathCorners);
                            if (setNavigatingOnSuccess && _stateManager != null)
                            {
                                ResetDynamicNavigationSmoothing();
                                _forceDynamicPathRedraw = true;
                                SetState(AppState.Navigating);
                                StartTransitionArrivalWatchIfNeeded();
                                StartDynamicNavigationLoop();
                            }
                        yield break; // success
                    }
                    catch (UnityEngine.MissingReferenceException mre)
                    {
                        if (enableUiDiagnostics)
                        {
                            Debug.LogWarning($"[UIManager] WaitForAndDrawPath attempt {attempt + 1}: renderer destroyed while drawing: {mre.Message}");
                        }
                        _pathRenderer = null; // clear stale ref and retry
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[UIManager] WaitForAndDrawPath unexpected error: {ex.Message}");
                        _pathRenderer = null;
                        yield break; // don't loop on unknown errors
                    }
                }

                // Wait a frame and retry
                yield return null;
            }

            Debug.LogWarning("[UIManager] WaitForAndDrawPath: failed to draw path after retries.");
        }

        private void EnsureNavigationSessionStarted()
        {
            if (_navigationSessionService == null || _navigationSessionService.HasActiveSession)
            {
                return;
            }

            var anchorId = _stateManager?.Context?.LastScannedAnchor?.qr_id ?? string.Empty;
            var destination = _stateManager?.Context?.CurrentDestination;
            var destinationId = destination?.destination_id ?? string.Empty;
            var floorId = _stateManager?.Context?.CurrentFloorId ?? 0;

            _navigationSessionService.StartSession(anchorId, destinationId, floorId);

            var payload = new SessionStartPayload
            {
                eventType = "navigation_session_start",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                sessionId = _navigationSessionService.ActiveSessionId,
                startQrId = anchorId,
                destinationId = destinationId,
                floorId = floorId
            };

            Debug.Log($"[Navigation][SessionStart] {JsonUtility.ToJson(payload, true)}");
            _backendApiClient?.SendSessionStart(payload);
        }

        private void ResetFeedbackState()
        {
            var feedback = ScreenBinders.Feedback;
            if (feedback == null)
            {
                return;
            }

            feedback.Rating = 0;
            feedback.SelectedChips.Clear();
            feedback.Comment = string.Empty;
        }

        private void ResetDynamicNavigationSmoothing()
        {
            _hasSmoothedCameraPosition = false;
            _hasLastDynamicUpdatePosition = false;
            _forceDynamicPathRedraw = false;
        }

        private void UpdateRouteNodeIdsForSession()
        {
            if (_navigationSessionService == null)
            {
                return;
            }

            if (_hybridCalculator == null)
            {
                _navigationSessionService.SetRouteNodeIds(null);
                return;
            }

            var nodeIds = _hybridCalculator.GetLastRouteNodeIds();
            _navigationSessionService.SetRouteNodeIds(nodeIds);
        }

        private void UpdateNavigationMarkersForActiveRoute()
        {
            if (markerManager == null)
            {
                return;
            }

            if (_hybridCalculator == null || _stateManager?.Context == null)
            {
                markerManager.UpdateMarkers(null, null);
                return;
            }

            var routeNodes = _hybridCalculator.GetLastNodePath();
            if (routeNodes == null || routeNodes.Count == 0)
            {
                markerManager.UpdateMarkers(null, null);
                return;
            }

            var currentFloorId = _stateManager.Context.CurrentFloorId;
            Vector3? targetWorldPosition = null;
            Vector3? stairWorldPosition = null;

            var targetNode = routeNodes[routeNodes.Count - 1];
            if (targetNode != null && targetNode.floor_id == currentFloorId)
            {
                targetWorldPosition = new Vector3(targetNode.x, targetNode.y, targetNode.z);
            }

            for (var i = 0; i < routeNodes.Count - 1; i++)
            {
                var currentNode = routeNodes[i];
                var nextNode = routeNodes[i + 1];
                if (currentNode == null || nextNode == null)
                {
                    continue;
                }

                if (currentNode.floor_id == currentFloorId && nextNode.floor_id != currentFloorId)
                {
                    stairWorldPosition = new Vector3(currentNode.x, currentNode.y, currentNode.z);
                    break;
                }
            }

            markerManager.UpdateMarkers(targetWorldPosition, stairWorldPosition);
        }

        private void RequestFullSceneReset()
        {
            _floorTransitionService?.ResetToMainScene();
        }

        private void OnSubmitFeedback()
        {
            var feedback = ScreenBinders.Feedback;
            var destination = _stateManager?.Context?.CurrentDestination;
            var snapshot = _lastNavigationSnapshot;
            var sessionId = _navigationSessionService?.ActiveSessionId ?? snapshot?.SessionId ?? string.Empty;
            var destinationId = destination?.destination_id ?? snapshot?.DestinationId ?? string.Empty;
            var destinationName = destination?.name ?? snapshot?.DestinationName ?? string.Empty;
            var floorId = _stateManager?.Context?.CurrentFloorId ?? snapshot?.FloorId ?? 0;

            var payload = new FeedbackPayload
            {
                eventType = "feedback_submit",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                sessionId = sessionId,
                rating = feedback?.Rating ?? 0,
                chips = feedback != null ? feedback.SelectedChips.ToArray() : Array.Empty<string>(),
                comment = feedback?.Comment ?? string.Empty,
                destinationName = destinationName,
                destinationId = destinationId,
                currentFloorId = floorId
            };

            var json = JsonUtility.ToJson(payload, true);
            Debug.Log($"[Feedback] {json}");
            _backendApiClient?.SendFeedback(payload);
            _navigationSessionService?.ClearSession();
            _lastNavigationSnapshot = null;
            ResetFeedbackState();
        }

        private void OnOpenHelpPanel()
        {
            NavigateToContextualPanel(ContextualPanelKind.Help);
        }

        private void OnOpenAboutPanel()
        {
            NavigateToContextualPanel(ContextualPanelKind.About);
        }

        private void OnOutdoorNavigationClicked()
        {
            Debug.Log("Opening Outdoor Map...");
        }

        private void OnQrCodeFound(string qrPayload)
        {
            _qrNavigationCoordinator?.OnQrCodeFound(qrPayload);
        }

        public void OnDestinationSelected(Destination dest)
        {
            CleanupNavigationForNewDestination();
            _qrNavigationCoordinator?.OnDestinationSelected(dest);
        }

        private void StartTransitionArrivalWatchIfNeeded()
        {
            _navigationTransitionSequencer?.StartIfNeeded();
        }

        private void StopTransitionArrivalWatch()
        {
            _navigationTransitionSequencer?.Stop();
        }

        private void StartDynamicNavigationLoop()
        {
            if (_dynamicNavigationRoutine != null)
            {
                return;
            }

            if (_navigationProgressTracker == null || !_navigationProgressTracker.HasActiveRoute)
            {
                return;
            }

            ResetDynamicNavigationSmoothing();
            _forceDynamicPathRedraw = true;
            _dynamicNavigationRoutine = StartCoroutine(DynamicNavigationLoop());
        }

        private void StopDynamicNavigationLoop(bool clearInstruction)
        {
            if (_dynamicNavigationRoutine != null)
            {
                StopCoroutine(_dynamicNavigationRoutine);
                _dynamicNavigationRoutine = null;
            }

            ResetDynamicNavigationSmoothing();

            if (clearInstruction)
            {
                _guidanceCueService?.Reset();
            }
        }

        private IEnumerator DynamicNavigationLoop()
        {
            while (_stateManager != null && _stateManager.CurrentState == AppState.Navigating)
            {
                if (_pathRenderer == null && !ResolveNavigationDependencies(logErrorIfMissing: false))
                {
                    yield return null;
                    continue;
                }

                var cameraTransform = Camera.main != null ? Camera.main.transform : null;
                if (cameraTransform == null)
                {
                    yield return null;
                    continue;
                }

                var rawPosition = cameraTransform.position;
                var rawForward = cameraTransform.forward;

                _navigationProgressTracker.Tick(rawPosition, rawForward, Time.deltaTime);

                if (!_hasSmoothedCameraPosition)
                {
                    _smoothedCameraPosition = rawPosition;
                    _hasSmoothedCameraPosition = true;
                }

                if (!_hasLastDynamicUpdatePosition)
                {
                    _lastDynamicUpdatePosition = rawPosition;
                    _hasLastDynamicUpdatePosition = true;
                }

                var smoothingTime = Mathf.Max(0.01f, dynamicPathSmoothingTimeSeconds);
                var smoothingAlpha = 1f - Mathf.Exp(-Time.deltaTime / smoothingTime);
                _smoothedCameraPosition = Vector3.Lerp(_smoothedCameraPosition, rawPosition, smoothingAlpha);

                var shouldUpdate = _forceDynamicPathRedraw
                                   || Vector3.Distance(_lastDynamicUpdatePosition, rawPosition) >= dynamicPathMinUpdateDistanceMeters;
                if (shouldUpdate)
                {
                    var dynamicPath = _navigationProgressTracker.GetDynamicRenderPath(_smoothedCameraPosition);
                    if (dynamicPath != null && dynamicPath.Count >= 2)
                    {
                        _pathRenderer.DrawPath(dynamicPath);
                        _lastDynamicUpdatePosition = rawPosition;
                        _hasLastDynamicUpdatePosition = true;
                        _forceDynamicPathRedraw = false;
                    }
                }

                yield return null;
            }

            _dynamicNavigationRoutine = null;
        }

        private void HandleGuidanceEvent(GuidanceEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            _guidanceCueService?.HandleGuidanceEvent(evt, ScreenBinders.Settings.VoiceGuidanceEnabled, hapticGuidanceEnabled);
            if (evt.EventType == GuidanceEventType.ReachedNode && evt.NodeIndex >= 0)
            {
                _navigationSessionService?.RecordVisitedNodeByIndex(evt.NodeIndex);
            }

            if (evt.EventType == GuidanceEventType.OffRouteDetected)
            {
                RecalculateRouteFromLivePose();
            }
            else if (evt.EventType == GuidanceEventType.DestinationReached)
            {
                CompleteNavigationSession(SessionStatus.Completed, AppState.DestinationReached);
            }
        }

        private void RecalculateRouteFromLivePose()
        {
            var destination = _stateManager?.Context?.CurrentDestination;
            var cam = Camera.main != null ? Camera.main.transform : null;
            if (destination == null || cam == null)
            {
                return;
            }

            Debug.Log("[Navigation][Recalc] Triggered by off-route detection.");
            Debug.Log($"[Navigation][Recalc] Current pose=({cam.position.x:F2},{cam.position.y:F2},{cam.position.z:F2}) floor={_stateManager.Context.CurrentFloorId}, destination={destination.destination_id}.");
            var targetPos = cam.position;
            var path = CalculatePathForCurrentFloor(cam.position, targetPos, _stateManager.Context.CurrentFloorId, destination.floor_id, destination.entrance_node_ids);
            if (path == null || path.Count == 0)
            {
                Debug.LogWarning("[Navigation][Recalc] Recalculation failed: no path returned.");
                return;
            }

            _navigationProgressTracker.InitializeRoute(path, canCompleteNavigation: true);
            _navigationSessionService?.ResetVisitedNodes();
            UpdateRouteNodeIdsForSession();
            UpdateNavigationMarkersForActiveRoute();
            if (_navigationProgressTracker is NavigationProgressTracker trackerImpl)
            {
                trackerImpl.NotifyRouteRecalculated();
            }
            _forceDynamicPathRedraw = true;
            Debug.Log($"[Navigation][Recalc] Recalculated path with {path.Count} corners and reset route progress.");
        }

        private void SyncNavigationUi()
        {
            var destination = _stateManager?.Context?.CurrentDestination;
            ScreenBinders.SetArTargetName(_contentContainer, destination?.name);
            ScreenBinders.SetArInstruction(_contentContainer, _latestInstructionText);
        }

        public void SetInstruction(string text, GuidanceSeverity severity, float? distanceMeters)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _latestInstructionText = text;
            }

            ScreenBinders.SetArInstruction(_contentContainer, _latestInstructionText);
        }

        public void ClearInstruction()
        {
            _latestInstructionText = "Continue.";
            ScreenBinders.SetArInstruction(_contentContainer, _latestInstructionText);
        }

        private void CleanupNavigationForNewDestination()
        {
            StopDynamicNavigationLoop(clearInstruction: true);
            StopTransitionArrivalWatch();
            _pathRenderer?.ClearPath();
            markerManager?.ClearMarkers();
            _navigationProgressTracker?.Reset();
            _navigationSessionService?.ClearSession();
            _lastNavigationSnapshot = null;
            ResetFeedbackState();
            _pendingTransitionLandingPosition = Vector3.zero;
            _hasPendingTransitionLanding = false;
            _stateManager?.Context?.ClearSession();
            ResetNavigationServiceReferences();
            RequestFullSceneReset();
        }

        private void CompleteNavigationSession(SessionStatus status, AppState nextState = AppState.Feedback)
        {
            if (_stateManager == null || _stateManager.CurrentState != AppState.Navigating)
            {
                return;
            }

            var destination = _stateManager.Context?.CurrentDestination;
            var floorId = _stateManager.Context?.CurrentFloorId ?? 0;
            var sessionId = _navigationSessionService?.ActiveSessionId ?? string.Empty;
            var destinationId = destination?.destination_id ?? _navigationSessionService?.DestinationId ?? string.Empty;
            var destinationName = destination?.name ?? string.Empty;

            _lastNavigationSnapshot = new NavigationCompletionSnapshot
            {
                SessionId = sessionId,
                DestinationId = destinationId,
                DestinationName = destinationName,
                FloorId = floorId
            };

            _navigationSessionService?.MarkCompleted(status);

            var routePayload = new RouteTakenPayload
            {
                eventType = "navigation_route",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                sessionId = sessionId,
                destinationId = destinationId,
                destinationName = destinationName,
                floorId = floorId,
                visitedNodeIds = _navigationSessionService?.GetVisitedNodeIds() ?? Array.Empty<string>(),
                completionStatus = status.ToString()
            };
            Debug.Log($"[Navigation][Metrics] {JsonUtility.ToJson(routePayload, true)}");
            _backendApiClient?.SendRouteTaken(routePayload);

            StopDynamicNavigationLoop(clearInstruction: true);
            StopTransitionArrivalWatch();
            _pathRenderer?.ClearPath();
            markerManager?.ClearMarkers();
            _navigationProgressTracker?.Reset();
            _stateManager.Context?.ClearSession();
            ResetNavigationServiceReferences();
            RequestFullSceneReset();
            _stateManager.ChangeState(nextState);
        }

        private void ReturnHomeFromDestinationReached()
        {
            StopDynamicNavigationLoop(clearInstruction: true);
            StopTransitionArrivalWatch();
            _pathRenderer?.ClearPath();
            markerManager?.ClearMarkers();
            _navigationProgressTracker?.Reset();
            _navigationSessionService?.ClearSession();
            _lastNavigationSnapshot = null;
            ResetFeedbackState();
            _stateManager?.Context?.ClearSession();
            SetState(AppState.Home);
        }

        private void OnDestinationGroupSelected(DestinationGroup group)
        {
            if (group == null || group.Entrances.Count == 0)
            {
                Debug.LogError("[UIManager] Destination group is empty.");
                return;
            }

            if (_navigationCoordinator == null
                || !_navigationCoordinator.TrySelectBestEntrance(
                    _stateManager?.Context?.LastScannedAnchor,
                    group.Entrances,
                    out var bestEntrance))
            {
                Debug.LogError("[UIManager] Could not resolve a valid entrance for the destination group.");
                return;
            }

            OnDestinationSelected(bestEntrance);
        }
    }
}
