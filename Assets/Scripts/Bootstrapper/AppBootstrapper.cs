using UnityEngine;
using NavAR.Core.State;
using NavAR.Infrastructure;
using NavAR.Presentation;
using NavAR.Data; // Add this!
using NavAR.Core.Interfaces; // Add this!
using UnityEngine.SceneManagement;
using NavAR.Data.SQLite;
using NavAR.Infrastructure.Navigation;
using NavAR.Infrastructure.Backend;
using System;

namespace NavAR.Bootstrapper
{
    public class AppBootstrapper : MonoBehaviour
    {
        [Header("Presentation Layer")]
        [SerializeField] private UIManager uiManager;

        [Header("Infrastructure Layer")]
        [SerializeField] private ZxingQrScanner arQrScanner;

        [Header("Data Layer")]
        [SerializeField] private bool useSQLiteRepository = true;

        [Header("Backend Sync")]
        [SerializeField] private bool enableBackendSync = true;
        [SerializeField] private string backendBaseUrl = "https://example.com/api";

        private AppStateManager appStateManager;
        private IMapRepository mapRepository;
        private IFloorSceneTransitionService floorTransitionService;
        private IQrScannerService qrScannerService;
        private ServiceContainer services;
        private INavigationContextProvider navigationContextProvider;
        private NavigationSessionService navigationSessionService;
        private BackendApiClient backendApiClient;
        private IBackendEventQueue backendEventQueue;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void Start()
        {
            Debug.Log("[Bootstrapper] Booting up NavAR System...");

            // 1. Create the Brain
            appStateManager = new AppStateManager();
            
            // 2. Create the Data Layer (Safe to do here because Start() runs after Unity is fully awake)
            mapRepository = BuildRepository();
            services = new ServiceContainer();
            services.Register(appStateManager);
            services.Register(mapRepository);
            navigationSessionService = new NavigationSessionService();
            services.Register(navigationSessionService);
            if (enableBackendSync)
            {
                backendEventQueue = BuildBackendEventQueue();
                if (backendEventQueue != null)
                {
                    services.Register(backendEventQueue);
                }

                backendApiClient = new BackendApiClient(this, backendBaseUrl, backendEventQueue);
                backendApiClient.StartAutoFlush();
                services.Register(backendApiClient);
            }
            navigationContextProvider = new SceneNavigationContextProvider();
            services.Register(navigationContextProvider);

            // 3. Try to bind the current scene immediately, then again whenever a new scene loads
            TryBindPresentationLayer();

            Debug.Log("[Bootstrapper] Initialization Complete.");
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            navigationContextProvider?.Refresh();
            TryBindPresentationLayer();
        }

        private IMapRepository BuildRepository()
        {
            if (!useSQLiteRepository)
            {
                Debug.Log("[Bootstrapper] Using MockMapRepository (SQLite disabled).");
                return new MockMapRepository();
            }

            try
            {
                Debug.Log("[Bootstrapper] Using SQLiteMapRepository.");
                return new SQLiteMapRepository();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Bootstrapper] SQLite initialization failed. Falling back to MockMapRepository. Error: {ex.Message}");
                return new MockMapRepository();
            }
        }

        private IBackendEventQueue BuildBackendEventQueue()
        {
            try
            {
                Debug.Log("[Bootstrapper] Using SQLiteBackendEventQueue.");
                return new SQLiteBackendEventQueue();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bootstrapper] Backend event queue unavailable. Offline backend retry is disabled for this run. Error: {ex.Message}");
                return null;
            }
        }

        private void TryBindPresentationLayer()
        {
            if (appStateManager == null || mapRepository == null)
            {
                return;
            }

            // Create FloorSceneTransitionService if not already created
            if (floorTransitionService == null)
            {
                var service = FindObjectOfType<FloorSceneTransitionService>();
                if (service == null)
                {
                    var serviceGO = new GameObject("FloorSceneTransitionService");
                    service = serviceGO.AddComponent<FloorSceneTransitionService>();
                    DontDestroyOnLoad(serviceGO);
                }
                floorTransitionService = service;
                services?.Register(floorTransitionService);
            }

            if (uiManager == null)
            {
                uiManager = FindObjectOfType<UIManager>();
            }

            if (arQrScanner == null)
            {
                arQrScanner = FindObjectOfType<ZxingQrScanner>();
            }

            if (arQrScanner == null)
            {
                Debug.Log("[Bootstrapper] Waiting for ZxingQrScanner to become available in the active scene.");
                return;
            }

            // Always use AR-based scanner as the single scanner implementation.
            qrScannerService = arQrScanner;
            services?.Register(qrScannerService);

            if (uiManager == null || qrScannerService == null)
            {
                Debug.Log("[Bootstrapper] Waiting for UIManager and/or scanner to become available in the active scene.");
                return;
            }

            navigationContextProvider?.Refresh();
            uiManager.Initialize(appStateManager, qrScannerService, mapRepository, floorTransitionService, services);

            // Attempt to auto-wire AlignmentService runtime references (ARSession + XR Origin)
            var arSession = FindObjectOfType<UnityEngine.XR.ARFoundation.ARSession>();
            UnityEngine.Transform xrOriginTransform = null;
            var originComp = FindObjectOfType<UnityEngine.XR.ARFoundation.ARSessionOrigin>();
            if (originComp != null) xrOriginTransform = originComp.transform;
            else
            {
                var go = GameObject.Find("XR Origin (Mobile AR)");
                if (go != null) xrOriginTransform = go.transform;
            }

            var alignServices = FindObjectsOfType<AlignmentService>();
            foreach (var a in alignServices)
            {
                if (arSession != null) a.SetSession(arSession);
                if (xrOriginTransform != null) a.SetXROrigin(xrOriginTransform);
            }
        }
    }
}
