using System;
using System.Collections.Generic;
using UnityEngine;
using NavAR.Core.Interfaces;
using NavAR.Core.Entities;

namespace NavAR.Core.Navigation
{
    /// <summary>
    /// Hybrid path calculator that tries graph routing first, then falls back to NavMesh.
    /// Also handles floor transition detection and UI prompts.
    /// </summary>
    public class HybridGraphPathCalculator : IPathCalculator
    {
        private readonly IPathCalculator _navMeshCalculator;
        private readonly IGraphPathRouter _graphRouter;
        private readonly Action<int, string, string> _onFloorTransitionDetected;
        private readonly bool _enableDiagnostics;
        private GraphRoutingResult _lastGraphResult;
        private readonly List<string> _lastReturnedNodeIds = new List<string>();

        public HybridGraphPathCalculator(
            IPathCalculator navMeshCalculator,
            IGraphPathRouter graphRouter,
            Action<int, string, string> onFloorTransitionDetected,
            bool enableDiagnostics = false
        )
        {
            _navMeshCalculator = navMeshCalculator ?? throw new ArgumentNullException(nameof(navMeshCalculator));
            _graphRouter = graphRouter ?? throw new ArgumentNullException(nameof(graphRouter));
            _onFloorTransitionDetected = onFloorTransitionDetected;
            _enableDiagnostics = enableDiagnostics;
        }

        public List<Vector3> CalculatePath(Vector3 startPosition, Vector3 endPosition)
        {
            // This is a compatibility override. Use CalculatePathWithContext instead.
            return _navMeshCalculator.CalculatePath(startPosition, endPosition);
        }

        /// <summary>
        /// Calculate path with floor context, using graph routing and floor transition detection.
        /// </summary>
        public List<Vector3> CalculatePathWithContext(
            Vector3 startPosition,
            Vector3 endPosition,
            int currentFloorId,
            int? destinationFloorId = null,
            IReadOnlyList<string> destinationNodeIds = null
        )
        {
            try
            {
                _lastReturnedNodeIds.Clear();

                if (_enableDiagnostics)
                {
                    Debug.Log(
                        $"[HybridGraphPathCalculator] Starting path calculation: " +
                        $"start={startPosition}, end={endPosition}, floor={currentFloorId}, destFloor={destinationFloorId}"
                    );
                }

                // Try graph routing first, passing destination floor hint
                var graphResult = _graphRouter.CalculateGraphPath(startPosition, endPosition, currentFloorId, destinationFloorId, destinationNodeIds);

                if (_enableDiagnostics)
                {
                    Debug.Log(
                        $"[HybridGraphPathCalculator] Graph router result: " +
                        $"valid={graphResult.IsValid}, corners={graphResult.PathCorners.Count}, " +
                        $"nodes={graphResult.NodePath.Count}, floorTransition={graphResult.HasFloorTransition}"
                    );
                    if (graphResult.NodePath != null && graphResult.NodePath.Count > 0)
                    {
                        Debug.Log($"[HybridGraphPathCalculator] Dijkstra route: {FormatNodePath(graphResult.NodePath)}");
                    }
                }

                _lastGraphResult = graphResult;

                if (graphResult.IsValid)
                {
                    // NOTE: do not trigger transition prompt here.
                    // UI should prompt only when the user reaches the transition node.

                    // Return graph corners directly (Dijkstra node path snapped to renderer).
                    // For multi-floor: return primary stage corners if floor transition exists,
                    // otherwise return full graph path.
                    if (graphResult.HasFloorTransition && graphResult.PrimaryStageCorners.Count > 0)
                    {
                        if (_enableDiagnostics)
                        {
                            Debug.Log(
                                $"[HybridGraphPathCalculator] Graph routing valid with floor transition. " +
                                $"Returning primary stage path with {graphResult.PrimaryStageCorners.Count} corners."
                            );
                            Debug.Log($"[HybridGraphPathCalculator] Path corners: {FormatCorners(graphResult.PrimaryStageCorners)}");
                        }
                        CaptureNodeIdsForPrimaryStage(graphResult);
                        return graphResult.PrimaryStageCorners;
                    }

                    if (_enableDiagnostics)
                    {
                        Debug.Log(
                            $"[HybridGraphPathCalculator] Graph routing valid (no transition). " +
                            $"Returning full graph path with {graphResult.PathCorners.Count} corners."
                        );
                        Debug.Log($"[HybridGraphPathCalculator] Path corners: {FormatCorners(graphResult.PathCorners)}");
                    }
                    CaptureNodeIdsForFullPath(graphResult);
                    return graphResult.PathCorners;
                }

                if (_enableDiagnostics)
                {
                    Debug.LogWarning(
                        $"[HybridGraphPathCalculator] Graph routing failed: {graphResult.ErrorMessage}. " +
                        $"Falling back to NavMesh."
                    );
                }

                _lastGraphResult = graphResult;
                _lastReturnedNodeIds.Clear();

                // Fallback to NavMesh
                var navMeshPath = _navMeshCalculator.CalculatePath(startPosition, endPosition);
                
                if (_enableDiagnostics)
                {
                    Debug.Log(
                        $"[HybridGraphPathCalculator] NavMesh fallback returned {navMeshPath?.Count ?? 0} corners."
                    );
                    if (navMeshPath != null && navMeshPath.Count > 0)
                    {
                        Debug.Log($"[HybridGraphPathCalculator] Fallback path corners: {FormatCorners(navMeshPath)}");
                    }
                }

                return navMeshPath;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HybridGraphPathCalculator] Exception during path calculation: {ex}");
                _lastReturnedNodeIds.Clear();
                return _navMeshCalculator.CalculatePath(startPosition, endPosition);
            }
        }

        public List<string> GetLastRouteNodeIds()
        {
            return _lastReturnedNodeIds.Count == 0 ? new List<string>() : new List<string>(_lastReturnedNodeIds);
        }

        public IReadOnlyList<GraphNode> GetLastNodePath()
        {
            return _lastGraphResult?.NodePath;
        }

        private void CaptureNodeIdsForPrimaryStage(GraphRoutingResult graphResult)
        {
            _lastReturnedNodeIds.Clear();
            if (graphResult?.NodePath == null || graphResult.NodePath.Count == 0)
            {
                return;
            }

            var lastIndex = graphResult.TransitionNodeIndex >= 0
                ? graphResult.TransitionNodeIndex
                : graphResult.NodePath.Count - 1;

            for (var i = 0; i <= lastIndex && i < graphResult.NodePath.Count; i++)
            {
                var nodeId = graphResult.NodePath[i]?.node_id;
                if (!string.IsNullOrWhiteSpace(nodeId))
                {
                    _lastReturnedNodeIds.Add(nodeId);
                }
            }
        }

        private void CaptureNodeIdsForFullPath(GraphRoutingResult graphResult)
        {
            _lastReturnedNodeIds.Clear();
            if (graphResult?.NodePath == null || graphResult.NodePath.Count == 0)
            {
                return;
            }

            foreach (var node in graphResult.NodePath)
            {
                if (!string.IsNullOrWhiteSpace(node?.node_id))
                {
                    _lastReturnedNodeIds.Add(node.node_id);
                }
            }
        }

        private static string FormatNodePath(List<GraphNode> nodePath)
        {
            if (nodePath == null || nodePath.Count == 0)
            {
                return "<empty>";
            }

            return string.Join(" -> ", nodePath.ConvertAll(node => $"{node.node_id}[F{node.floor_id}]"));
        }

        private static string FormatCorners(List<Vector3> corners)
        {
            if (corners == null || corners.Count == 0)
            {
                return "<empty>";
            }

            return string.Join(" -> ", corners.ConvertAll(corner => $"({corner.x:F2},{corner.y:F2},{corner.z:F2})"));
        }

        public bool TryGetPendingTransition(
            out int targetFloorId,
            out string targetFloorLabel,
            out string transitionNodeId,
            out Vector3 transitionNodePosition,
            out Vector3 transitionLandingPosition)
        {
            targetFloorId = 0;
            targetFloorLabel = null;
            transitionNodeId = null;
            transitionNodePosition = Vector3.zero;
            transitionLandingPosition = Vector3.zero;

            if (_lastGraphResult == null || !_lastGraphResult.IsValid || !_lastGraphResult.HasFloorTransition)
            {
                return false;
            }

            if (_lastGraphResult.PrimaryStageCorners == null || _lastGraphResult.PrimaryStageCorners.Count == 0)
            {
                return false;
            }

            targetFloorId = _lastGraphResult.TransitionTargetFloorId;
            targetFloorLabel = _lastGraphResult.TransitionTargetLabel;
            transitionNodeId = _lastGraphResult.TransitionNodeId;
            transitionNodePosition = _lastGraphResult.PrimaryStageCorners[_lastGraphResult.PrimaryStageCorners.Count - 1];

            var landingNodeIndex = _lastGraphResult.TransitionNodeIndex + 1;
            if (_lastGraphResult.NodePath != null && landingNodeIndex >= 0 && landingNodeIndex < _lastGraphResult.NodePath.Count)
            {
                var landingNode = _lastGraphResult.NodePath[landingNodeIndex];
                transitionLandingPosition = new Vector3(landingNode.x, landingNode.y, landingNode.z);
            }
            else
            {
                transitionLandingPosition = transitionNodePosition;
            }

            return true;
        }
    }
}
