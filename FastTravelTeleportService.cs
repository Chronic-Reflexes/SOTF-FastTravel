using System;
using System.Collections.Generic;
using System.Linq;
using Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;

namespace FastTravel
{
    public static class FastTravelTeleportService
    {
        private const float TeleportCooldownSeconds = 60f;
        private const float FoodDrainPercentPer100Meters = 8f;
        private const float WaterDrainPercentPer100Meters = 5f;
        private const float SleepDrainPercentPer100Meters = 3f;
        private static float _nextTeleportAllowedAt;

        public static bool TryGetCooldownRemaining(out float remainingSeconds)
        {
            remainingSeconds = Mathf.Max(0f, _nextTeleportAllowedAt - Time.unscaledTime);
            return remainingSeconds > 0f;
        }

        public static bool TryTeleportToBed(FastTravelBedLocation location, out string status)
        {
            status = "Teleport failed.";
            if (location == null)
            {
                status = "No bed selected.";
                return false;
            }

            if (TryGetCooldownRemaining(out _))
            {
                status = "Fast travel is cooling down.";
                return false;
            }

            var bedTransform = ResolveBedTransform(location);
            Vector3 origin = location.Position;
            Quaternion rotation = location.Rotation;
            if (rotation == default)
                rotation = Quaternion.identity;

            Vector3 safeSpot = FindSafeTeleportSpot(origin, rotation, bedTransform);

            if (!TryGetLocalPlayerTransform(out var playerTransform))
            {
                status = "Could not resolve player transform.";
                ModMain.LogMessage("FastTravel: Could not resolve player transform for teleport.");
                return false;
            }

            Vector3 startPosition = playerTransform.position;

            if (!TryMovePlayer(playerTransform, safeSpot, rotation))
            {
                status = "Could not move player.";
                return false;
            }

            _nextTeleportAllowedAt = Time.unscaledTime + TeleportCooldownSeconds;
            ApplyDistanceBasedVitalsDrain(startPosition, safeSpot);

            status = "Teleported to " + location.DisplayName + ".";
            ModMain.LogMessage("FastTravel: Teleport success to " + safeSpot + ".");
            return true;
        }

        private static void ApplyDistanceBasedVitalsDrain(Vector3 fromPosition, Vector3 toPosition)
        {
            float travelDistanceMeters = Vector3.Distance(fromPosition, toPosition);
            if (travelDistanceMeters <= 0.01f)
                return;

            if (!TryGetLocalVitals(out Vitals vitals))
                return;

            // Drain is fully proportional to exact travel distance, not quantized to 100m chunks.
            float foodDrainFraction = CalculateDrainFraction(travelDistanceMeters, FoodDrainPercentPer100Meters);
            float waterDrainFraction = CalculateDrainFraction(travelDistanceMeters, WaterDrainPercentPer100Meters);
            float sleepDrainFraction = CalculateDrainFraction(travelDistanceMeters, SleepDrainPercentPer100Meters);

            bool fullnessApplied = TryApplyFactorDrain(
                vitals.GetFullnessFactor,
                vitals.GetFullness,
                vitals.SetFullness,
                foodDrainFraction,
                out float fullnessBefore,
                out float fullnessAfter);

            bool hydrationApplied = TryApplyFactorDrain(
                vitals.GetHydrationFactor,
                vitals.GetHydration,
                vitals.SetHydration,
                waterDrainFraction,
                out float hydrationBefore,
                out float hydrationAfter);

            bool restApplied = TryApplyFactorDrain(
                vitals.GetRestFactor,
                vitals.GetRest,
                vitals.SetRest,
                sleepDrainFraction,
                out float restBefore,
                out float restAfter);

            if (!fullnessApplied && !hydrationApplied && !restApplied)
                return;

            ModMain.LogMessage(
                "FastTravel: Distance drain " + travelDistanceMeters.ToString("F1") + "m"
                + " food=" + (foodDrainFraction * 100f).ToString("F2") + "%"
                + " water=" + (waterDrainFraction * 100f).ToString("F2") + "%"
                + " sleep=" + (sleepDrainFraction * 100f).ToString("F2") + "%"
                + " factors F:" + fullnessBefore.ToString("F3") + "->" + fullnessAfter.ToString("F3")
                + " W:" + hydrationBefore.ToString("F3") + "->" + hydrationAfter.ToString("F3")
                + " S:" + restBefore.ToString("F3") + "->" + restAfter.ToString("F3") + ".");
        }

        private static float CalculateDrainFraction(float distanceMeters, float percentPer100Meters)
        {
            if (distanceMeters <= 0f || percentPer100Meters <= 0f)
                return 0f;

            return Mathf.Max(0f, distanceMeters * percentPer100Meters / 10000f);
        }

        private static bool TryGetLocalVitals(out Vitals vitals)
        {
            vitals = null;

            try
            {
                vitals = LocalPlayer.Vitals;
                return vitals != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyFactorDrain(
            Func<float> getFactor,
            Func<float> getValue,
            Action<float> setValue,
            float drainFraction,
            out float beforeFactor,
            out float afterFactor)
        {
            beforeFactor = 0f;
            afterFactor = 0f;

            if (getFactor == null || getValue == null || setValue == null || drainFraction <= 0f)
                return false;

            try
            {
                beforeFactor = Sanitize01(getFactor());
                afterFactor = Mathf.Clamp01(beforeFactor - drainFraction);

                if (afterFactor >= beforeFactor)
                    return true;

                float currentValue = Mathf.Max(0f, getValue());
                float estimatedMax = currentValue;

                if (beforeFactor > 0.0001f)
                    estimatedMax = currentValue / beforeFactor;

                if (!IsFinite(estimatedMax) || estimatedMax <= 0f)
                    estimatedMax = Mathf.Max(currentValue, 1f);

                float targetValue = Mathf.Clamp(afterFactor * estimatedMax, 0f, estimatedMax);
                setValue(targetValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float Sanitize01(float value)
        {
            if (!IsFinite(value))
                return 0f;

            return Mathf.Clamp01(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Transform ResolveBedTransform(FastTravelBedLocation location)
        {
            if (location == null)
                return null;

            var allBeds = UnityEngine.Object.FindObjectsOfType<SleepInteract>(true);
            for (int i = 0; i < allBeds.Length; i++)
            {
                var bed = allBeds[i];
                if (bed == null)
                    continue;

                if (bed.gameObject != null && bed.gameObject.GetInstanceID() == location.BedObjectId)
                    return bed.transform;

                if (bed.GetInstanceID() == location.SleepInteractId)
                    return bed.transform;
            }

            return null;
        }

        private static bool TryGetLocalPlayerTransform(out Transform playerTransform)
        {
            playerTransform = null;

            var cam = Camera.main;
            if (cam != null)
            {
                playerTransform = cam.transform.root != null ? cam.transform.root : cam.transform;
                return playerTransform != null;
            }

            // Fallback if camera is unavailable.
            var controller = UnityEngine.Object.FindObjectOfType<CharacterController>();
            if (controller != null)
            {
                playerTransform = controller.transform;
                return playerTransform != null;
            }

            return false;
        }

        private static bool TryMovePlayer(Transform playerTransform, Vector3 safeSpot, Quaternion rotation)
        {
            if (playerTransform == null)
                return false;

            var controller = playerTransform.GetComponent<CharacterController>();
            bool hadController = controller != null;
            bool oldEnabled = false;

            try
            {
                if (hadController)
                {
                    oldEnabled = controller.enabled;
                    controller.enabled = false;
                }

                playerTransform.position = safeSpot;

                Vector3 euler = playerTransform.eulerAngles;
                playerTransform.rotation = Quaternion.Euler(euler.x, rotation.eulerAngles.y, euler.z);

                Physics.SyncTransforms();
                return true;
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: TryMovePlayer failed: " + ex);
                return false;
            }
            finally
            {
                if (hadController)
                {
                    try
                    {
                        controller.enabled = oldEnabled;
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static Vector3 FindSafeTeleportSpot(Vector3 origin, Quaternion rotation, Transform bedTransform)
        {
            try
            {
                float playerHeight = 1.8f;
                float playerRadius = 0.3f;
                float minClearDistance = 1.5f;
                float maxRayDistance = 5.0f;
                int layerMask = ~0;

                float ceilingHeight = GetCeilingHeight(origin, 8f, layerMask);
                bool isEnclosed = ceilingHeight < 3.0f;

                Vector3 floorOrigin = origin;
                if (TryGetFloorBelow(origin, bedTransform, out Vector3 floorPos, 4f, layerMask))
                {
                    floorOrigin = floorPos + Vector3.up * 0.1f;
                }

                float heightTolerance = isEnclosed ? 3.0f : 1.5f;

                Vector3 forward = rotation * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                forward.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

                Vector3[] directions =
                {
                    forward,
                    (forward + right).normalized,
                    right,
                    (right - forward).normalized,
                    -forward,
                    (-forward - right).normalized,
                    -right,
                    (forward - right).normalized
                };

                Vector3 rayOrigin = floorOrigin + Vector3.up * (playerHeight * 0.5f);
                var candidates = new List<(Vector3 direction, float clearDistance)>();

                foreach (Vector3 dir in directions)
                {
                    float distance = maxRayDistance;
                    if (Physics.Raycast(rayOrigin, dir, out RaycastHit hit, maxRayDistance, layerMask))
                    {
                        if (!IsPartOfBed(hit.collider, bedTransform))
                        {
                            distance = hit.distance;
                        }
                    }

                    if (distance >= minClearDistance)
                        candidates.Add((dir, distance));
                }

                foreach (var candidate in candidates.OrderByDescending(c => c.clearDistance))
                {
                    Vector3 dir = candidate.direction;
                    float placementDist = Mathf.Min(candidate.clearDistance - 0.3f, 2.5f);
                    placementDist = Mathf.Max(placementDist, 1.0f);
                    Vector3 targetPos = floorOrigin + dir * placementDist;
                    targetPos.y = floorOrigin.y;

                    Vector3 rayStart = targetPos + Vector3.up * 2.0f;
                    if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit groundHit, 5f, layerMask))
                        continue;

                    Vector3 actualFloor = groundHit.point;
                    if (Mathf.Abs(actualFloor.y - floorOrigin.y) > heightTolerance)
                        continue;

                    if (!HasEnoughHeadroom(actualFloor, playerHeight, layerMask))
                        continue;

                    Vector3 capsuleBottom = actualFloor + Vector3.up * playerRadius;
                    Vector3 capsuleTop = actualFloor + Vector3.up * (playerHeight - playerRadius);
                    if (Physics.CheckCapsule(capsuleBottom, capsuleTop, playerRadius, layerMask, QueryTriggerInteraction.Ignore))
                        continue;

                    return actualFloor + Vector3.up * 0.1f;
                }

                return FindTerrainDropPosition(origin, bedTransform, playerHeight, playerRadius, layerMask);
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Exception in FindSafeTeleportSpot: " + ex);
                return origin + Vector3.up * 1.0f;
            }
        }

        private static float GetCeilingHeight(Vector3 origin, float maxCheck = 10f, int layerMask = ~0)
        {
            Vector3 rayStart = origin + Vector3.up * 1.0f;
            if (Physics.Raycast(rayStart, Vector3.up, out RaycastHit hit, maxCheck, layerMask))
                return hit.distance + 1.0f;
            return maxCheck;
        }

        private static bool TryGetFloorBelow(Vector3 origin, Transform bedTransform, out Vector3 floorPos, float maxDist = 5f, int layerMask = ~0)
        {
            floorPos = origin;
            float remaining = maxDist;
            Vector3 rayStart = origin + Vector3.up * 0.5f;

            while (remaining > 0.1f)
            {
                if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, remaining, layerMask))
                    break;

                if (IsPartOfBed(hit.collider, bedTransform))
                {
                    rayStart = hit.point - Vector3.up * 0.1f;
                    remaining -= hit.distance + 0.1f;
                    continue;
                }

                float surfaceY = hit.point.y;
                if (surfaceY >= origin.y - 0.3f)
                {
                    floorPos = hit.point;
                    return true;
                }

                floorPos = hit.point;
                return true;
            }

            return false;
        }

        private static bool IsPartOfBed(Collider col, Transform bedTransform)
        {
            if (col == null) return false;
            if (bedTransform != null && (col.transform.IsChildOf(bedTransform) || col.transform == bedTransform))
                return true;

            string rootName = col.transform.root.name.ToLowerInvariant();
            if (rootName.Contains("bed") || rootName.Contains("sleep") || rootName.Contains("tarp") || rootName.Contains("shelter"))
                return true;

            return false;
        }

        private static bool HasEnoughHeadroom(Vector3 floorPos, float playerHeight, int layerMask)
        {
            float checkHeight = playerHeight + 0.5f;
            return !Physics.Raycast(floorPos + Vector3.up * 0.1f, Vector3.up, out RaycastHit _, checkHeight, layerMask);
        }

        private static Vector3 FindTerrainDropPosition(Vector3 origin, Transform bedTransform, float playerHeight, float playerRadius, int layerMask)
        {
            float maxDrop = 10f;
            Vector3 rayStart = origin + Vector3.up * 2.0f;
            float remaining = maxDrop;
            Vector3 currentStart = rayStart;

            while (remaining > 0.1f)
            {
                if (!Physics.Raycast(currentStart, Vector3.down, out RaycastHit hit, remaining, layerMask))
                    break;

                string name = hit.collider.gameObject.name.ToLowerInvariant();
                bool isStructure = name.Contains("logplank") || name.Contains("roof") || name.Contains("beam") ||
                                   name.Contains("floor") || name.Contains("wall") || name.Contains("pillar");

                if (!IsPartOfBed(hit.collider, bedTransform) && !isStructure)
                {
                    Vector3 floorPos = hit.point;
                    if (HasEnoughHeadroom(floorPos, playerHeight, layerMask))
                    {
                        Vector3 capsuleBottom = floorPos + Vector3.up * playerRadius;
                        Vector3 capsuleTop = floorPos + Vector3.up * (playerHeight - playerRadius);
                        if (!Physics.CheckCapsule(capsuleBottom, capsuleTop, playerRadius, layerMask, QueryTriggerInteraction.Ignore))
                        {
                            return floorPos + Vector3.up * 0.1f;
                        }
                    }
                }

                currentStart = hit.point - Vector3.up * 0.1f;
                remaining -= hit.distance + 0.1f;
            }

            return origin + Vector3.up * 1.0f;
        }
    }
}
