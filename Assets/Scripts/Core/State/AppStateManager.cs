using System;

namespace NavAR.Core.State
{
    public enum AppState
    {
        Splash,
        Home,
        Explore,
        Settings,
        QrScanning,
        Permission,
        Navigating,
        PositionLost,
        Feedback,
        ComingSoon,
        DestinationReached = 11,
        // Backward-compatible alias while Presentation is migrated to Explore.
        DestinationSelection = Explore,
        FloorTransition = 10 // Placed here for your future multi-floor epic!
    }

    public class AppStateManager
    {
        public AppState CurrentState { get; private set; }
        public NavigationContext Context { get; private set; }

        // UI Managers will subscribe to this event to know when to change screens
        public event Action<AppState> OnStateChanged;

        public AppStateManager()
        {
            Context = new NavigationContext();
            CurrentState = AppState.Splash;
        }

        public void SetState(AppState newState)
        {
            if (CurrentState == newState) return;

            var previous = CurrentState;
            CurrentState = newState;

            // Log state transitions for debugging navigation flow
            try
            {
                UnityEngine.Debug.Log($"[AppStateManager] State change: {previous} -> {newState}");
            }
            catch
            {
                // Ignore if logging can't be performed (shouldn't happen in Unity runtime)
            }

            OnStateChanged?.Invoke(CurrentState);
        }

        // Kept for compatibility with existing call sites.
        public void ChangeState(AppState newState)
        {
            SetState(newState);
        }
    }
}
