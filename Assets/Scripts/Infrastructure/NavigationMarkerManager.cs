using UnityEngine;

public class NavigationMarkerManager : MonoBehaviour
{
    [Header("3D Models (FBX Prefabs)")]
    [SerializeField] private GameObject targetPinPrefab;
    [SerializeField] private GameObject stairPrefab;

    // Internal references to the spawned instances
    private GameObject activeTargetPin;
    private GameObject activeStairPin;

    /// <summary>
    /// Places the 3D markers in the AR world. 
    /// Pass null if the marker shouldn't be visible on the CURRENT floor.
    /// </summary>
    public void UpdateMarkers(Vector3? targetWorldPosition, Vector3? stairWorldPosition)
    {
        // 1. Handle Final Destination Target Pin
        if (targetWorldPosition.HasValue)
        {
            if (activeTargetPin == null) activeTargetPin = Instantiate(targetPinPrefab);
            
            activeTargetPin.transform.position = targetWorldPosition.Value;
            activeTargetPin.SetActive(true);
            
            // Optional: Make the pin pulse or rotate here
        }
        else if (activeTargetPin != null)
        {
            activeTargetPin.SetActive(false);
        }

        // 2. Handle Stair Transition Pin
        if (stairWorldPosition.HasValue)
        {
            if (activeStairPin == null) activeStairPin = Instantiate(stairPrefab);
            
            activeStairPin.transform.position = stairWorldPosition.Value;
            activeStairPin.SetActive(true);
        }
        else if (activeStairPin != null)
        {
            activeStairPin.SetActive(false);
        }
    }

    /// <summary>
    /// Hides all 3D markers (e.g., when navigation is cancelled or stopped)
    /// </summary>
    public void ClearMarkers()
    {
        if (activeTargetPin != null) activeTargetPin.SetActive(false);
        if (activeStairPin != null) activeStairPin.SetActive(false);
    }
}