using System;
using System.Collections.Generic;
using UnityEngine;
using NavAR.Core.Interfaces;

namespace NavAR.Core.Navigation
{
    public sealed class NavigationProgressTracker : INavigationProgressTracker
    {
        private readonly float _approachDistanceMeters;
        private readonly float _reachedDistanceMeters;
        private readonly float _offRouteDistanceMeters;
        private readonly float _offRouteCooldownSeconds;
        private readonly float _segmentSwitchBiasMeters;

        private readonly List<Vector3> _route = new List<Vector3>();
        private int _activeSegmentIndex;
        private bool _isForward = true;
        private int _lastDirectionSign = 1;
        private int _lastApproachNodeIndex = -1;
        private int _lastReachedNodeIndex = -1;
        private float _offRouteAccumulated;
        private bool _offRouteRaised;
        private bool _canCompleteNavigation = true;
        private bool _destinationReachedRaised;

        public event Action<GuidanceEvent> OnGuidanceEvent;
        public bool HasActiveRoute => _route.Count >= 2;

        public NavigationProgressTracker(
            float approachDistanceMeters = 3.5f,
            float reachedDistanceMeters = 1.2f,
            float offRouteDistanceMeters = 3f,
            float offRouteCooldownSeconds = 1.5f,
            float segmentSwitchBiasMeters = 0.35f)
        {
            _approachDistanceMeters = Mathf.Max(0.5f, approachDistanceMeters);
            _reachedDistanceMeters = Mathf.Max(0.35f, reachedDistanceMeters);
            _offRouteDistanceMeters = Mathf.Max(1f, offRouteDistanceMeters);
            _offRouteCooldownSeconds = Mathf.Max(0.25f, offRouteCooldownSeconds);
            _segmentSwitchBiasMeters = Mathf.Max(0f, segmentSwitchBiasMeters);
        }

        public void InitializeRoute(List<Vector3> routeCorners, bool canCompleteNavigation = true)
        {
            _route.Clear();
            if (routeCorners != null)
            {
                for (var i = 0; i < routeCorners.Count; i++)
                {
                    if (_route.Count == 0 || Vector3.Distance(_route[_route.Count - 1], routeCorners[i]) > 0.05f)
                    {
                        _route.Add(routeCorners[i]);
                    }
                }
            }

            _activeSegmentIndex = 0;
            _isForward = true;
            _lastDirectionSign = 1;
            _lastApproachNodeIndex = -1;
            _lastReachedNodeIndex = -1;
            _offRouteAccumulated = 0f;
            _offRouteRaised = false;
            _canCompleteNavigation = canCompleteNavigation;
            _destinationReachedRaised = false;
        }

        public void Reset()
        {
            _route.Clear();
            _activeSegmentIndex = 0;
            _isForward = true;
            _lastDirectionSign = 1;
            _lastApproachNodeIndex = -1;
            _lastReachedNodeIndex = -1;
            _offRouteAccumulated = 0f;
            _offRouteRaised = false;
            _canCompleteNavigation = true;
            _destinationReachedRaised = false;
        }

        public void Tick(Vector3 userWorldPosition, Vector3 userForward, float deltaTime)
        {
            if (!HasActiveRoute)
            {
                return;
            }

            var flattenedForward = new Vector3(userForward.x, 0f, userForward.z);
            if (flattenedForward.sqrMagnitude < 0.01f)
            {
                flattenedForward = Vector3.forward;
            }
            else
            {
                flattenedForward.Normalize();
            }

            var best = FindBestSegment(userWorldPosition, flattenedForward);
            var shouldSwitch = best.segmentIndex != _activeSegmentIndex
                               && best.distance + _segmentSwitchBiasMeters < best.currentDistance;

            if (shouldSwitch)
            {
                _activeSegmentIndex = best.segmentIndex;
            }

            var directionSign = best.headingDot >= 0f ? 1 : -1;
            _isForward = directionSign >= 0;
            if (directionSign != _lastDirectionSign)
            {
                _lastDirectionSign = directionSign;
                Emit(GuidanceEventType.SegmentReversed, _isForward ? "Continuing forward on route." : "You are moving back along the route.", GuidanceSeverity.Caution, null, ResolveTargetNodeIndex(best.segmentIndex, _isForward), userWorldPosition);
            }

            var targetNodeIndex = ResolveTargetNodeIndex(_activeSegmentIndex, _isForward);
            if (targetNodeIndex >= 0 && targetNodeIndex < _route.Count)
            {
                var targetNode = _route[targetNodeIndex];
                var distToTarget = HorizontalDistance(userWorldPosition, targetNode);

                if (distToTarget <= _approachDistanceMeters && _lastApproachNodeIndex != targetNodeIndex)
                {
                    _lastApproachNodeIndex = targetNodeIndex;
                    var turnAhead = BuildTurnMessage(targetNodeIndex, _isForward, useNowWording: false);
                    if (!string.IsNullOrEmpty(turnAhead))
                    {
                        Emit(GuidanceEventType.ApproachingNode, $"{turnAhead} in {Mathf.RoundToInt(Mathf.Max(1f, distToTarget))} meters.", GuidanceSeverity.Info, distToTarget, targetNodeIndex, userWorldPosition);
                    }
                }

                if (distToTarget <= _reachedDistanceMeters && _lastReachedNodeIndex != targetNodeIndex)
                {
                    _lastReachedNodeIndex = targetNodeIndex;
                    Emit(GuidanceEventType.ReachedNode, "Instruction point reached.", GuidanceSeverity.Info, distToTarget, targetNodeIndex, userWorldPosition);

                    var turn = BuildTurnMessage(targetNodeIndex, _isForward, useNowWording: true);
                    if (!string.IsNullOrEmpty(turn))
                    {
                        Emit(GuidanceEventType.TurnInstructionReady, turn, GuidanceSeverity.Info, null, targetNodeIndex, userWorldPosition);
                    }

                    if (_canCompleteNavigation && !_destinationReachedRaised && _isForward && targetNodeIndex == _route.Count - 1)
                    {
                        _destinationReachedRaised = true;
                        Emit(GuidanceEventType.DestinationReached, "Destination reached.", GuidanceSeverity.Info, 0f, targetNodeIndex, userWorldPosition);
                    }
                }
            }

            if (best.distance > _offRouteDistanceMeters)
            {
                _offRouteAccumulated += Mathf.Max(0f, deltaTime);
                if (_offRouteAccumulated >= _offRouteCooldownSeconds && !_offRouteRaised)
                {
                    _offRouteRaised = true;
                    Emit(GuidanceEventType.OffRouteDetected, "You are off the suggested path. Recalculating.", GuidanceSeverity.Warning, best.distance, targetNodeIndex, userWorldPosition);
                }
            }
            else
            {
                _offRouteAccumulated = 0f;
                _offRouteRaised = false;
            }
        }

        public List<Vector3> GetDynamicRenderPath(Vector3 userWorldPosition)
        {
            var result = new List<Vector3>();
            if (!HasActiveRoute)
            {
                return result;
            }

            var targetNodeIndex = ResolveTargetNodeIndex(_activeSegmentIndex, _isForward);
            if (targetNodeIndex < 0 || targetNodeIndex >= _route.Count)
            {
                return result;
            }

            result.Add(userWorldPosition);
            result.Add(_route[targetNodeIndex]);

            if (_isForward)
            {
                for (var i = targetNodeIndex + 1; i < _route.Count; i++)
                {
                    result.Add(_route[i]);
                }
            }
            else
            {
                for (var i = targetNodeIndex - 1; i >= 0; i--)
                {
                    result.Add(_route[i]);
                }
            }

            return result;
        }

        public void NotifyRouteRecalculated()
        {
            Emit(GuidanceEventType.RouteRecalculated, "Route updated.", GuidanceSeverity.Caution, null, -1, Vector3.zero);
        }

        private (int segmentIndex, float distance, float currentDistance, float headingDot) FindBestSegment(Vector3 userPosition, Vector3 userForward)
        {
            var bestIndex = Mathf.Clamp(_activeSegmentIndex, 0, _route.Count - 2);
            var bestDistance = float.MaxValue;
            var bestHeading = 1f;

            var current = SegmentDistanceAndHeading(userPosition, userForward, bestIndex);
            var currentDistance = current.distance;

            for (var i = 0; i < _route.Count - 1; i++)
            {
                var eval = SegmentDistanceAndHeading(userPosition, userForward, i);
                var score = eval.distance + HeadingPenalty(eval.headingDot);
                var bestScore = bestDistance + HeadingPenalty(bestHeading);
                if (score < bestScore)
                {
                    bestDistance = eval.distance;
                    bestIndex = i;
                    bestHeading = eval.headingDot;
                }
            }

            return (bestIndex, bestDistance, currentDistance, bestHeading);
        }

        private (float distance, float headingDot) SegmentDistanceAndHeading(Vector3 userPosition, Vector3 userForward, int segmentIndex)
        {
            var a = _route[segmentIndex];
            var b = _route[segmentIndex + 1];
            var flatA = new Vector3(a.x, 0f, a.z);
            var flatB = new Vector3(b.x, 0f, b.z);
            var flatUser = new Vector3(userPosition.x, 0f, userPosition.z);
            var ab = flatB - flatA;
            var abLenSq = Mathf.Max(0.0001f, ab.sqrMagnitude);
            var t = Mathf.Clamp01(Vector3.Dot(flatUser - flatA, ab) / abLenSq);
            var projection = flatA + ab * t;
            var dist = Vector3.Distance(flatUser, projection);

            var dir = ab.normalized;
            var headingDot = Vector3.Dot(userForward, dir);
            return (dist, headingDot);
        }

        private static float HeadingPenalty(float headingDot)
        {
            if (headingDot >= 0f)
            {
                return 0f;
            }

            return Mathf.Abs(headingDot) * 0.5f;
        }

        private int ResolveTargetNodeIndex(int segmentIndex, bool isForward)
        {
            if (isForward)
            {
                return Mathf.Clamp(segmentIndex + 1, 0, _route.Count - 1);
            }

            return Mathf.Clamp(segmentIndex, 0, _route.Count - 1);
        }

        private string BuildTurnMessage(int nodeIndex, bool isForward, bool useNowWording)
        {
            var prevIndex = isForward ? nodeIndex - 1 : nodeIndex + 1;
            var currentIndex = nodeIndex;
            var nextIndex = isForward ? nodeIndex + 1 : nodeIndex - 1;
            if (prevIndex < 0 || nextIndex < 0 || prevIndex >= _route.Count || currentIndex < 0 || currentIndex >= _route.Count || nextIndex >= _route.Count)
            {
                return useNowWording ? "Continue straight now." : "Continue straight";
            }

            var a = Flatten(_route[prevIndex]);
            var b = Flatten(_route[currentIndex]);
            var c = Flatten(_route[nextIndex]);
            var inDir = (b - a).normalized;
            var outDir = (c - b).normalized;
            var cross = inDir.x * outDir.z - inDir.z * outDir.x;
            var dot = Vector3.Dot(inDir, outDir);

            if (dot > 0.92f)
            {
                return useNowWording ? "Continue straight now." : "Continue straight";
            }

            if (cross > 0f)
            {
                return useNowWording ? "Turn left now." : "Turn left";
            }

            return useNowWording ? "Turn right now." : "Turn right";
        }

        private static Vector3 Flatten(Vector3 value)
        {
            return new Vector3(value.x, 0f, value.z);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private void Emit(GuidanceEventType type, string message, GuidanceSeverity severity, float? distanceMeters, int nodeIndex, Vector3 userPosition)
        {
            OnGuidanceEvent?.Invoke(new GuidanceEvent
            {
                EventType = type,
                Message = message,
                Severity = severity,
                DistanceMeters = distanceMeters,
                NodeIndex = nodeIndex,
                UserPosition = userPosition
            });
        }
    }
}
