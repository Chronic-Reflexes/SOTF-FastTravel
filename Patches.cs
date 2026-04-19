using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Sons.Gameplay;
using Sons.Gui.Input;

namespace FastTravel
{
    internal static class LinkUiReflectionCache
    {
        internal const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static readonly PropertyInfo UiElementIdProperty = typeof(LinkUiElement).GetProperty("_uiElementId", InstanceFlags);
        internal static readonly FieldInfo UiElementIdField = typeof(LinkUiElement).GetField("_uiElementId", InstanceFlags);

        internal static readonly PropertyInfo UpdateMethodTypeProperty = typeof(LinkUiElement).GetProperty("_updateMethodType", InstanceFlags);
        internal static readonly FieldInfo UpdateMethodTypeField = typeof(LinkUiElement).GetField("_updateMethodType", InstanceFlags);

        internal static readonly PropertyInfo WorldSpaceOffsetProperty = typeof(LinkUiElement).GetProperty("_worldSpaceOffset", InstanceFlags);
        internal static readonly FieldInfo WorldSpaceOffsetField = typeof(LinkUiElement).GetField("_worldSpaceOffset", InstanceFlags);

        internal static readonly PropertyInfo TransformProperty = typeof(LinkUiElement).GetProperty("_transform", InstanceFlags);
        internal static readonly FieldInfo TransformField = typeof(LinkUiElement).GetField("_transform", InstanceFlags);

        internal static readonly PropertyInfo UiGameObjectProperty = typeof(LinkUiElement).GetProperty("_uiGameObject", InstanceFlags);
        internal static readonly FieldInfo UiGameObjectField = typeof(LinkUiElement).GetField("_uiGameObject", InstanceFlags);

        internal static readonly PropertyInfo IsActiveProperty = typeof(LinkUiElement).GetProperty("IsActive", InstanceFlags);
        internal static readonly PropertyInfo IsActiveBackingProperty = typeof(LinkUiElement).GetProperty("_IsActive_k__BackingField", InstanceFlags);
    }

    internal static class GrabNodeReflectionCache
    {
        internal const BindingFlags PublicInstanceFlags = BindingFlags.Public | BindingFlags.Instance;

        internal static readonly MethodInfo GetEnterMethod = typeof(GrabNode).GetMethod("get__grabEnter", PublicInstanceFlags);
        internal static readonly MethodInfo GetExitMethod = typeof(GrabNode).GetMethod("get__grabExit", PublicInstanceFlags);
        internal static readonly MethodInfo GetStayMethod = typeof(GrabNode).GetMethod("get__grabStay", PublicInstanceFlags);
        internal static readonly MethodInfo RegisterCallbacksMethod = typeof(GrabNode).GetMethod("RegisterCallbacks", PublicInstanceFlags);
    }

    [HarmonyPatch(typeof(SleepInteract), "Awake")]
    public static class SleepInteract_Awake_Patch
    {
        private const string FastTravelElementName = "FastTravelInteractionElement";
        private const string FastTravelNestedElementName = "FastTravelNestedInteractionElement";
        private const string ItemTemplateName = "ItemTemplatePickupGui";
        private const string CustomUiElementId = "custom.fasttravel";
        private static Texture2D _fastTravelArrowTexture;

        [HarmonyPostfix]
        public static void Postfix(SleepInteract __instance)
        {
            try
            {
                LogVerbose($"FastTravel: SleepInteract.Awake postfix hit on '{__instance.name}'.");

                FastTravelLocationRegistry.TrackBed(__instance);

                Transform sleepAndSave = __instance.transform;
                if (sleepAndSave == null) return;

                Transform itemTemplate = sleepAndSave.Find(ItemTemplateName);
                if (itemTemplate == null) return;

                // Don't add if already present
                if (itemTemplate.Find(FastTravelElementName) != null) return;

                // Find a suitable template (Save or Sleep)
                Transform template = FindInteractionTemplate(itemTemplate);
                if (template == null) return;

                // Clone the template
                GameObject fastTravelObj = UnityEngine.Object.Instantiate(template.gameObject, itemTemplate);
                fastTravelObj.name = FastTravelElementName;

                var primaryLinkUi = FindPrimaryLinkUiElement(fastTravelObj);

                // Prevent cloned gameplay scripts from hijacking Sleep/Save registration.
                DisableConflictingBehaviours(fastTravelObj);
                SanitizeFastTravelHierarchy(fastTravelObj, primaryLinkUi);

                // Keep transform aligned with source template immediately.
                fastTravelObj.transform.localPosition = template.localPosition;
                fastTravelObj.transform.localRotation = template.localRotation;
                fastTravelObj.transform.localScale = template.localScale;

                // Ensure our own anchor exists and is offset from Sleep icon.
                GetFastTravelAnchorTransform(fastTravelObj.transform);

                // 1. Configure only one primary LinkUiElement.
                ConfigurePrimaryLinkUiElement(fastTravelObj);
                ForceRequestAllLinkUiElements(fastTravelObj);

                var linkUi = FindPrimaryLinkUiElement(fastTravelObj);
                if (linkUi != null)
                {
                    LogVerbose($"FastTravel: Primary LinkUiElement id is '{GetUiElementId(linkUi) ?? "<null>"}'.");
                }
                else
                {
                    LogVerbose("FastTravel: LinkUiElement not found on clone.");
                }

                // 2. Change key prompt to "F"
                SetKeyPromptToF(linkUi != null ? linkUi.gameObject : fastTravelObj);

                // 3. Hook GrabNode without replacing existing callbacks
                var grabNode = sleepAndSave.GetComponent<GrabNode>();
                if (grabNode != null)
                {
                    AddFastTravelCallback(grabNode, __instance);
                    LogVerbose("FastTravel: Grab callback added.");
                }
                else
                {
                    LogVerbose("FastTravel: GrabNode not found on SleepAndSaveInteract.");
                }

                LogVerbose("FastTravel: Fast Travel button added successfully.");
            }
            catch (Exception ex)
            {
                ModMain.LogMessage($"FastTravel: Error - {ex}");
            }
        }

        private static Transform FindInteractionTemplate(Transform itemTemplate)
        {
            // Prefer Sleep to avoid Save element conflicts in some game versions.
            for (int i = 0; i < itemTemplate.childCount; i++)
            {
                var child = itemTemplate.GetChild(i);
                if (child.name == "SleepInteractionElement")
                    return child;
            }

            // Fallback to Save
            for (int i = 0; i < itemTemplate.childCount; i++)
            {
                var child = itemTemplate.GetChild(i);
                if (child.name == "SaveInteractionElement")
                    return child;
            }
            return null;
        }

        private static void DisableConflictingBehaviours(GameObject root)
        {
            var behaviours = root.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;

                if (behaviour is LinkUiElement) continue;
                if (behaviour is DynamicInputIcon) continue;

                string typeName = behaviour.GetType().Name;
                bool isPotentialConflict =
                    typeName.IndexOf("Sleep", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isPotentialConflict)
                {
                    behaviour.enabled = false;
                    LogVerbose($"FastTravel: Disabled cloned behaviour {typeName} to avoid conflicts.");
                }
            }
        }

        private static void SanitizeFastTravelHierarchy(GameObject root, LinkUiElement primaryLink)
        {
            // Keep only one link active on Fast Travel branch to avoid collisions with Save/Sleep.
            var allLinks = root.GetComponentsInChildren<LinkUiElement>(true);
            for (int i = 0; i < allLinks.Length; i++)
            {
                var link = allLinks[i];
                if (link == null)
                    continue;

                bool keep = link == primaryLink;
                link.enabled = keep;
            }

            // Remove Sleep/Save naming from descendants so game lookups do not collide.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                root.name
            };

            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                var current = allTransforms[i];
                if (current == null || current == root.transform) continue;

                string originalName = current.name;
                string renamed = originalName
                    .Replace("Sleep", "FastTravel")
                    .Replace("Save", "FastTravel")
                    .Replace("sleep", "fastTravel")
                    .Replace("save", "fastTravel");

                if (!renamed.StartsWith("FastTravel", StringComparison.OrdinalIgnoreCase))
                    renamed = "FastTravel_" + renamed;

                if (string.Equals(renamed, FastTravelElementName, StringComparison.OrdinalIgnoreCase))
                    renamed = "FastTravelNestedInteractionElement";

                string uniqueName = renamed;
                int suffix = 1;
                while (usedNames.Contains(uniqueName))
                {
                    uniqueName = renamed + "_" + suffix;
                    suffix++;
                }

                usedNames.Add(uniqueName);

                if (!string.Equals(originalName, uniqueName, StringComparison.Ordinal))
                {
                    current.name = uniqueName;
                    LogVerbose($"FastTravel: Renamed cloned child '{originalName}' -> '{uniqueName}'.");
                }

                // Do not reuse the disabled sleep-like branch for Fast Travel visuals.
                if (uniqueName.IndexOf("DisabledInteractionElement", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    current.gameObject.SetActive(false);
                }
            }
        }

        private static void ConfigurePrimaryLinkUiElement(GameObject root)
        {
            var primary = FindPrimaryLinkUiElement(root);
            if (primary == null)
            {
                ModMain.LogMessage("FastTravel: No LinkUiElement components found to configure.");
                return;
            }

            // Keep a unique id so Fast Travel does not replace Sleep/Save entries.
            primary.SetId(CustomUiElementId, true);
            primary.SetText("Fast Travel");
            primary.enabled = true;
            ApplyArrowIcon(primary);
            primary.gameObject.SetActive(false);
            primary.gameObject.SetActive(true);

            LogVerbose($"FastTravel: Configured primary LinkUiElement component with id '{CustomUiElementId}'.");
        }

        private static void ForceRequestAllLinkUiElements(GameObject root)
        {
            var links = root.GetComponentsInChildren<LinkUiElement>(true);
            int requested = 0;
            int enabledCount = 0;
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                if (link == null || !link.enabled)
                    continue;

                enabledCount++;

                if (link.RequestUiElement())
                    requested++;
            }

            LogVerbose($"FastTravel: Requested UI for {requested}/{enabledCount} enabled LinkUiElement component(s).");
        }

        public static string GetUiElementId(LinkUiElement link)
        {
            if (link == null)
                return null;

            try
            {
                if (LinkUiReflectionCache.UiElementIdProperty != null)
                {
                    var value = LinkUiReflectionCache.UiElementIdProperty.GetValue(link, null) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }

                if (LinkUiReflectionCache.UiElementIdField != null)
                {
                    var value = LinkUiReflectionCache.UiElementIdField.GetValue(link) as string;
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
            }
            catch
            {
            }

            return null;
        }

        public static void ApplyArrowIcon(LinkUiElement link)
        {
            if (link == null)
                return;

            try
            {
                Texture2D arrow = GetOrCreateArrowTexture();
                if (arrow == null)
                    return;

                link.SetApplyTexture(true);
                link.SetTexture(arrow);
                link.SetOutlineTexture(arrow);
                link.ValidateTexture();
                link.ValidateMaterial();
            }
            catch
            {
            }
        }

        private static Texture2D GetOrCreateArrowTexture()
        {
            if (_fastTravelArrowTexture != null)
                return _fastTravelArrowTexture;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "FastTravelArrowIcon";

            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 arrow = new Color32(230, 240, 255, 255);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    tex.SetPixel(x, y, clear);
                }
            }

            // Shaft
            for (int y = 28; y <= 35; y++)
            {
                for (int x = 10; x <= 38; x++)
                {
                    tex.SetPixel(x, y, arrow);
                }
            }

            // Arrow head
            for (int x = 39; x <= 56; x++)
            {
                int t = 56 - x;
                int minY = 31 - t;
                int maxY = 32 + t;
                if (minY < 10) minY = 10;
                if (maxY > 53) maxY = 53;

                for (int y = minY; y <= maxY; y++)
                {
                    tex.SetPixel(x, y, arrow);
                }
            }

            tex.Apply(false, false);
            _fastTravelArrowTexture = tex;
            return _fastTravelArrowTexture;
        }

        public static LinkUiElement FindPrimaryLinkUiElement(GameObject root)
        {
            var direct = root.GetComponent<LinkUiElement>();
            if (direct != null)
                return direct;

            var all = root.GetComponentsInChildren<LinkUiElement>(true);
            if (all == null || all.Length == 0)
                return null;

            LinkUiElement best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < all.Length; i++)
            {
                var current = all[i];
                if (current == null) continue;

                int depth = 0;
                Transform t = current.transform;
                while (t != null && t != root.transform)
                {
                    depth++;
                    t = t.parent;
                }

                if (t != root.transform)
                    continue;

                int score = depth * 10;
                if (current.name.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                    score -= 1000;
                if (current.name.IndexOf("InteractionElement", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 20;

                if (score > bestScore)
                {
                    best = current;
                    bestScore = score;
                }
            }

            return best ?? all[0];
        }

        public static Transform GetFastTravelAnchorTransform(Transform fastTravelRoot)
        {
            if (fastTravelRoot == null)
                return null;

            var nested = fastTravelRoot.Find(FastTravelNestedElementName);
            if (nested != null)
            {
                nested.localPosition = new Vector3(-0.2f, 0f, 0f);
                return nested;
            }

            return fastTravelRoot;
        }

        private static void SetKeyPromptToF(GameObject root)
        {
            Transform buttonPanel = root.transform.Find("PrimaryPanel/ButtonPanel");
            if (buttonPanel == null) return;

            // Try direct TMP text path first
            Transform keyBase = buttonPanel.Find("KeyboardKeyBase");
            if (keyBase != null)
            {
                var tmpText = keyBase.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmpText != null)
                {
                    tmpText.text = "F";
                    LogVerbose("FastTravel: Set key prompt to 'F' (TMP).");
                    return;
                }
            }

            // Fallback: DynamicInputIcon
            var dynamicIcon = buttonPanel.GetComponentInChildren<DynamicInputIcon>(true);
            if (dynamicIcon != null)
            {
                dynamicIcon.SetActionId("Lighter");
                LogVerbose("FastTravel: Set DynamicInputIcon action to 'Lighter'.");
                return;
            }

            LogVerbose("FastTravel: Could not set key prompt.");
        }

        private static void AddFastTravelCallback(GrabNode grabNode, SleepInteract sleepInteract)
        {
            var getEnter = GrabNodeReflectionCache.GetEnterMethod;
            var getExit = GrabNodeReflectionCache.GetExitMethod;
            var getStay = GrabNodeReflectionCache.GetStayMethod;
            var register = GrabNodeReflectionCache.RegisterCallbacksMethod;

            if (getEnter == null || register == null)
            {
                ModMain.LogMessage("FastTravel: GrabNode callback accessors not found.");
                return;
            }

            object existingEnter = getEnter.Invoke(grabNode, null);
            object existingExit = getExit != null ? getExit.Invoke(grabNode, null) : null;
            object existingStay = getStay != null ? getStay.Invoke(grabNode, null) : null;

            var enterType = getEnter.ReturnType;
            var exitType = getExit != null ? getExit.ReturnType : enterType;

            System.Action fastTravelExitAction = () =>
            {
                HideFastTravelUiForSleepInteract(sleepInteract);
            };

            object newExit = ConvertToExpectedAction(exitType, fastTravelExitAction);

            object combinedExit = CombineCallbacks(existingExit, newExit, exitType);
            register.Invoke(grabNode, new object[] { existingEnter, combinedExit ?? existingExit, existingStay });
        }

        public static void HideFastTravelUiForSleepInteract(SleepInteract sleepInteract)
        {
            if (sleepInteract == null)
                return;

            try
            {
                Transform sleepAndSave = sleepInteract.transform;
                if (sleepAndSave == null)
                    return;

                Transform itemTemplate = sleepAndSave.Find(ItemTemplateName);
                if (itemTemplate == null)
                    return;

                Transform fastTravel = itemTemplate.Find(FastTravelElementName);
                if (fastTravel == null)
                    return;

                SleepInteract_Update_Patch.HideDedicatedUiForFastTravel(fastTravel);

                var links = fastTravel.GetComponentsInChildren<LinkUiElement>(true);
                for (int i = 0; i < links.Length; i++)
                {
                    var link = links[i];
                    if (link == null)
                        continue;

                    try
                    {
                        var ui = link.GetUiGameObject();
                        if (ui != null && ui.activeSelf)
                            ui.SetActive(false);
                    }
                    catch
                    {
                    }

                    try
                    {
                        link.RemoveElement();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static object ConvertToExpectedAction(Type expectedType, System.Action callback)
        {
            if (expectedType == typeof(System.Action))
                return callback;

            if (expectedType == typeof(Il2CppSystem.Action))
                return (Il2CppSystem.Action)callback;

            // Try an explicit/implicit conversion operator if present.
            var explicitCast = expectedType.GetMethod("op_Explicit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(System.Action) }, null);
            if (explicitCast != null)
                return explicitCast.Invoke(null, new object[] { callback });

            var implicitCast = expectedType.GetMethod("op_Implicit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(System.Action) }, null);
            if (implicitCast != null)
                return implicitCast.Invoke(null, new object[] { callback });

            return null;
        }

        private static object CombineCallbacks(object left, object right, Type callbackType)
        {
            if (left == null) return right;
            if (right == null) return left;

            var opAdd = callbackType.GetMethod(
                "op_Addition",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null,
                new[] { callbackType, callbackType },
                null);

            if (opAdd != null)
                return opAdd.Invoke(null, new[] { left, right });

            if (left is Delegate leftDelegate && right is Delegate rightDelegate)
            {
                return Delegate.Combine(leftDelegate, rightDelegate);
            }

            ModMain.LogMessage("FastTravel: Could not combine callbacks, using only Fast Travel callback.");
            return right;
        }

        [Conditional("FASTTRAVEL_VERBOSE")]
        private static void LogVerbose(string message)
        {
            ModMain.LogMessage(message);
        }
    }

    [HarmonyPatch(typeof(SleepInteract), "OnGrabExit")]
    public static class SleepInteract_OnGrabExit_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(SleepInteract __instance)
        {
            SleepInteract_Awake_Patch.HideFastTravelUiForSleepInteract(__instance);
        }
    }

    [HarmonyPatch(typeof(SleepInteract), "OnDisable")]
    public static class SleepInteract_OnDisable_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(SleepInteract __instance)
        {
            FastTravelLocationRegistry.MarkBedInactive(__instance);
            SleepInteract_Awake_Patch.HideFastTravelUiForSleepInteract(__instance);
        }
    }

    [HarmonyPatch(typeof(SleepInteract), "OnDestroy")]
    public static class SleepInteract_OnDestroy_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(SleepInteract __instance)
        {
            FastTravelLocationRegistry.RemoveBed(__instance);
        }
    }

    [HarmonyPatch(typeof(SleepInteract), "Update")]
    public static class SleepInteract_Update_Patch
    {
        private const string FastTravelElementName = "FastTravelInteractionElement";
        private const string ItemTemplateName = "ItemTemplatePickupGui";
        private const float FastTravelHoldDuration = 0.65f;
        private static readonly Dictionary<int, GameObject> _dedicatedUiByLinkId = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, LinkUiElement> _primaryLinkByRootId = new Dictionary<int, LinkUiElement>();
        private static readonly Dictionary<int, float> _holdStartBySleepInteractId = new Dictionary<int, float>();
        private static readonly HashSet<int> _holdTriggeredBySleepInteractId = new HashSet<int>();
        private static readonly Dictionary<int, Image[]> _progressImagesByUiId = new Dictionary<int, Image[]>();
        private static readonly HashSet<int> _configuredPromptUiIds = new HashSet<int>();

        [HarmonyPostfix]
        public static void Postfix(SleepInteract __instance)
        {
            try
            {
                FastTravelLocationRegistry.TrackBed(__instance);

                Transform sleepAndSave = __instance.transform;
                if (sleepAndSave == null) return;

                Transform itemTemplate = sleepAndSave.Find(ItemTemplateName);
                if (itemTemplate == null) return;

                Transform fastTravel = itemTemplate.Find(FastTravelElementName);
                if (fastTravel == null) return;

                if (!fastTravel.gameObject.activeInHierarchy || !fastTravel.gameObject.activeSelf)
                {
                    HideDedicatedUiForFastTravel(fastTravel);
                    return;
                }

                Transform source = FindSyncTemplate(itemTemplate);
                if (source == null)
                {
                    HideDedicatedUiForFastTravel(fastTravel);
                    return;
                }

                fastTravel.localPosition = source.localPosition;
                fastTravel.localRotation = source.localRotation;
                fastTravel.localScale = source.localScale;

                MirrorLinkUiDrawState(__instance, source, fastTravel);
            }
            catch
            {
                // Keep this silent; this runs every frame.
            }
        }

        private static void MirrorLinkUiDrawState(SleepInteract sleepInteract, Transform source, Transform fastTravel)
        {
            if (sleepInteract == null || source == null || fastTravel == null) return;

            var sourceLink = GetCachedPrimaryLinkUi(source.gameObject);
            if (sourceLink == null)
            {
                HideDedicatedUiForFastTravel(fastTravel);
                return;
            }

            var fastLink = GetCachedPrimaryLinkUi(fastTravel.gameObject);
            if (fastLink == null || !fastLink.enabled || !fastLink.gameObject.activeInHierarchy)
            {
                HideDedicatedUiForFastTravel(fastTravel);
                return;
            }

            int fastLinkId = fastLink.GetInstanceID();

            // Anchor to FastTravelNestedInteractionElement when available.
            Transform anchor = SleepInteract_Awake_Patch.GetFastTravelAnchorTransform(fastTravel) ?? fastTravel;
            Vector3 anchorPos = anchor.position;

            var sourceUi = sourceLink.GetUiGameObject();
            bool sourceVisible = IsUiVisible(sourceUi);

            var fastUi = fastLink.GetUiGameObject();
            if (fastUi == null && _dedicatedUiByLinkId.TryGetValue(fastLinkId, out var trackedUi) && trackedUi != null)
            {
                SetUiGameObject(fastLink, trackedUi);
                fastUi = trackedUi;
            }

            if (!sourceLink.IsActive || !sourceVisible)
            {
                HideDedicatedUiForFastTravel(fastTravel);
                DeactivateTrackedUi(fastLinkId, fastUi);
                fastLink.RemoveElement();
                return;
            }

            if (!fastLink.IsActive)
                fastLink.RequestUiElement();

            // If custom id has no UI template, create a dedicated UI instance from source visuals.
            if (fastLink.GetUiGameObject() == null)
            {
                if (sourceUi != null)
                {
                    var clone = UnityEngine.Object.Instantiate(sourceUi, sourceUi.transform.parent);
                    clone.name = "FastTravelUiElement";
                    SetUiGameObject(fastLink, clone);
                    _dedicatedUiByLinkId[fastLinkId] = clone;
                    fastLink.SetText("Fast Travel");
                    SetIsActive(fastLink, true);
                    ConfigureCloneForFPrompt(clone);
                    LogVerbose("FastTravel: Created dedicated UI object instance.");
                }
            }

            fastUi = fastLink.GetUiGameObject();
            if (fastUi == null)
                return;

            SleepInteract_Awake_Patch.ApplyArrowIcon(fastLink);

            if (!fastUi.activeSelf)
            {
                fastUi.SetActive(true);
            }

            ConfigureCloneForFPrompt(fastUi);

            HandleFastTravelFActivation(sleepInteract, sourceVisible, fastUi);

            // If this is our dedicated cloned ui object, keep it attached to source UI screen position.
            if (string.Equals(fastUi.name, "FastTravelUiElement", StringComparison.Ordinal))
            {
                SyncClonedUiWithSource(sourceUi, fastUi, source.position, anchorPos);
                return;
            }

            // Keep internal tracking fields aligned with this interaction anchor.
            SetTrackedTransform(fastLink, anchor);
            fastLink.SetWorldSpaceOffset(ReadWorldSpaceOffset(sourceLink));
            SetUpdateMethodTypeFromSource(fastLink, sourceLink);

            fastLink.ShowElement(anchorPos, anchor);
            fastLink.ManagedUpdate(anchor, anchorPos);
        }

        private static void HandleFastTravelFActivation(SleepInteract sleepInteract, bool sourceVisible, GameObject fastUi)
        {
            if (sleepInteract == null)
                return;

            int id = sleepInteract.GetInstanceID();

            if (!sourceVisible || fastUi == null)
            {
                ResetHoldState(id, fastUi);
                return;
            }

            if (!Input.GetKey(KeyCode.F))
            {
                ResetHoldState(id, fastUi);
                return;
            }

            if (!_holdStartBySleepInteractId.TryGetValue(id, out float holdStart))
            {
                holdStart = Time.unscaledTime;
                _holdStartBySleepInteractId[id] = holdStart;
                _holdTriggeredBySleepInteractId.Remove(id);
            }

            float progress = Mathf.Clamp01((Time.unscaledTime - holdStart) / FastTravelHoldDuration);
            SetHoldProgressVisual(fastUi, progress);

            if (progress < 1f)
                return;

            if (_holdTriggeredBySleepInteractId.Contains(id))
                return;

            _holdTriggeredBySleepInteractId.Add(id);

            if (FastTravel.UI.FastTravelUI.IsAnyMenuOpen())
                return;

            LogVerbose("FastTravel: F key activation triggered.");
            FastTravelLocationRegistry.MarkBedInteracted(sleepInteract);
            try
            {
                FastTravel.UI.FastTravelUI.ShowForBedObject(sleepInteract.gameObject);
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Failed to open Fast Travel UI: " + ex);
                _holdTriggeredBySleepInteractId.Remove(id);
            }
        }

        private static void ResetHoldState(int sleepInteractId, GameObject fastUi)
        {
            _holdStartBySleepInteractId.Remove(sleepInteractId);
            _holdTriggeredBySleepInteractId.Remove(sleepInteractId);
            SetHoldProgressVisual(fastUi, 0f);
        }

        private static void SetHoldProgressVisual(GameObject fastUi, float progress)
        {
            if (fastUi == null)
                return;

            int uiId = fastUi.GetInstanceID();
            if (!_progressImagesByUiId.TryGetValue(uiId, out var images) || images == null)
            {
                images = ResolveProgressImages(fastUi);
                _progressImagesByUiId[uiId] = images;
            }

            for (int i = 0; i < images.Length; i++)
            {
                var image = images[i];
                if (image == null)
                    continue;

                if (image.type == Image.Type.Filled)
                {
                    image.fillAmount = progress;
                }
            }
        }

        private static Image[] ResolveProgressImages(GameObject fastUi)
        {
            var allImages = fastUi.GetComponentsInChildren<Image>(true);
            var selected = new List<Image>();

            for (int i = 0; i < allImages.Length; i++)
            {
                var image = allImages[i];
                if (image == null || image.type != Image.Type.Filled)
                    continue;

                string n = image.name ?? string.Empty;
                bool looksBackground =
                    n.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("bg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("base", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0;

                if (looksBackground)
                    continue;

                bool looksProgress =
                    n.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("fill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0;

                if (looksProgress || image.fillAmount <= 0.05f)
                    selected.Add(image);
            }

            if (selected.Count == 0)
            {
                Image best = null;
                float bestFill = float.MaxValue;

                for (int i = 0; i < allImages.Length; i++)
                {
                    var image = allImages[i];
                    if (image == null || image.type != Image.Type.Filled)
                        continue;

                    if (image.fillAmount < bestFill)
                    {
                        bestFill = image.fillAmount;
                        best = image;
                    }
                }

                if (best != null)
                    selected.Add(best);
            }

            return selected.ToArray();
        }

        private static void ConfigureCloneForFPrompt(GameObject fastUi)
        {
            if (fastUi == null)
                return;

            int uiId = fastUi.GetInstanceID();
            if (_configuredPromptUiIds.Contains(uiId))
                return;

            try
            {
                var icons = fastUi.GetComponentsInChildren<DynamicInputIcon>(true);
                for (int i = 0; i < icons.Length; i++)
                {
                    var icon = icons[i];
                    if (icon == null)
                        continue;
                    icon.SetActionId("Lighter");
                }

                var tmps = fastUi.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++)
                {
                    var tmp = tmps[i];
                    if (tmp == null)
                        continue;

                    string t = tmp.text != null ? tmp.text.Trim() : string.Empty;
                    if (string.Equals(t, "E", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Z", StringComparison.OrdinalIgnoreCase))
                        tmp.text = "F";
                }

                _configuredPromptUiIds.Add(uiId);
            }
            catch
            {
            }
        }

        private static LinkUiElement GetCachedPrimaryLinkUi(GameObject root)
        {
            if (root == null)
                return null;

            int rootId = root.GetInstanceID();
            if (_primaryLinkByRootId.TryGetValue(rootId, out var cached) && cached != null)
                return cached;

            var resolved = SleepInteract_Awake_Patch.FindPrimaryLinkUiElement(root);
            _primaryLinkByRootId[rootId] = resolved;
            return resolved;
        }

        private static bool IsUiVisible(GameObject ui)
        {
            if (ui == null)
                return false;

            if (!ui.activeInHierarchy || !ui.activeSelf)
                return false;

            return true;
        }

        private static void DeactivateTrackedUi(int linkId, GameObject fastUi)
        {
            GameObject target = fastUi;
            if (target == null)
                _dedicatedUiByLinkId.TryGetValue(linkId, out target);

            if (target != null && target.activeSelf)
                target.SetActive(false);

            if (target != null)
            {
                int uiId = target.GetInstanceID();
                _progressImagesByUiId.Remove(uiId);
                _configuredPromptUiIds.Remove(uiId);
            }
        }

        public static void HideDedicatedUiForFastTravel(Transform fastTravel)
        {
            if (fastTravel == null)
                return;

            var links = fastTravel.GetComponentsInChildren<LinkUiElement>(true);
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                if (link == null)
                    continue;

                int id = link.GetInstanceID();
                if (_dedicatedUiByLinkId.TryGetValue(id, out var ui) && ui != null && ui.activeSelf)
                {
                    ui.SetActive(false);
                    int uiId = ui.GetInstanceID();
                    _progressImagesByUiId.Remove(uiId);
                    _configuredPromptUiIds.Remove(uiId);
                }
            }

            // Fallback sweep: disable any orphaned FastTravel UI clones under the same template branch.
            var parent = fastTravel.parent;
            if (parent != null)
            {
                var transforms = parent.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    var t = transforms[i];
                    if (t == null) continue;
                    if (!string.Equals(t.name, "FastTravelUiElement", StringComparison.Ordinal)) continue;

                    if (t.gameObject.activeSelf)
                        t.gameObject.SetActive(false);

                    int uiId = t.gameObject.GetInstanceID();
                    _progressImagesByUiId.Remove(uiId);
                    _configuredPromptUiIds.Remove(uiId);
                }
            }
        }

        private static void SyncClonedUiWithSource(GameObject sourceUi, GameObject fastUi, Vector3 sourceAnchorWorld, Vector3 fastAnchorWorld)
        {
            if (sourceUi == null || fastUi == null)
                return;

            if (!sourceUi.activeInHierarchy)
            {
                if (fastUi.activeSelf)
                    fastUi.SetActive(false);
                return;
            }

            if (!fastUi.activeSelf)
                fastUi.SetActive(true);

            Vector3 offset = Vector3.zero;
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 srcScreen = cam.WorldToScreenPoint(sourceAnchorWorld);
                Vector3 fastScreen = cam.WorldToScreenPoint(fastAnchorWorld);
                if (srcScreen.z > 0f && fastScreen.z > 0f)
                {
                    offset = new Vector3(fastScreen.x - srcScreen.x, fastScreen.y - srcScreen.y, 0f);
                }
            }

            // Fallback nudge to keep icons separated if projection is unavailable.
            if (offset.sqrMagnitude < 0.01f)
                offset = new Vector3(-32f, 0f, 0f);

            fastUi.transform.position = sourceUi.transform.position + offset;
            fastUi.transform.rotation = sourceUi.transform.rotation;
            fastUi.transform.localScale = sourceUi.transform.localScale;
        }

        private static void SetUpdateMethodTypeFromSource(LinkUiElement target, LinkUiElement source)
        {
            if (target == null || source == null)
                return;

            try
            {
                object sourceValue = null;
                if (LinkUiReflectionCache.UpdateMethodTypeProperty != null)
                {
                    sourceValue = LinkUiReflectionCache.UpdateMethodTypeProperty.GetValue(source, null);
                }

                if (sourceValue == null)
                    return;

                if (LinkUiReflectionCache.UpdateMethodTypeProperty != null && LinkUiReflectionCache.UpdateMethodTypeProperty.CanWrite)
                {
                    LinkUiReflectionCache.UpdateMethodTypeProperty.SetValue(target, sourceValue, null);
                    return;
                }

                if (LinkUiReflectionCache.UpdateMethodTypeField != null)
                {
                    LinkUiReflectionCache.UpdateMethodTypeField.SetValue(target, sourceValue);
                }
            }
            catch
            {
            }
        }

        private static Vector3 ReadWorldSpaceOffset(LinkUiElement link)
        {
            if (link == null)
                return Vector3.zero;

            try
            {
                if (LinkUiReflectionCache.WorldSpaceOffsetProperty != null)
                {
                    object value = LinkUiReflectionCache.WorldSpaceOffsetProperty.GetValue(link, null);
                    if (value is Vector3 vec)
                        return vec;
                }

                if (LinkUiReflectionCache.WorldSpaceOffsetField != null)
                {
                    object value = LinkUiReflectionCache.WorldSpaceOffsetField.GetValue(link);
                    if (value is Vector3 vec)
                        return vec;
                }
            }
            catch
            {
            }

            return Vector3.zero;
        }

        private static void SetTrackedTransform(LinkUiElement link, Transform transform)
        {
            if (link == null || transform == null)
                return;

            try
            {
                if (LinkUiReflectionCache.TransformProperty != null && LinkUiReflectionCache.TransformProperty.CanWrite)
                {
                    LinkUiReflectionCache.TransformProperty.SetValue(link, transform, null);
                    return;
                }

                if (LinkUiReflectionCache.TransformField != null)
                {
                    LinkUiReflectionCache.TransformField.SetValue(link, transform);
                }
            }
            catch
            {
            }
        }

        private static void SetUiGameObject(LinkUiElement link, GameObject gameObject)
        {
            try
            {
                if (LinkUiReflectionCache.UiGameObjectProperty != null && LinkUiReflectionCache.UiGameObjectProperty.CanWrite)
                {
                    LinkUiReflectionCache.UiGameObjectProperty.SetValue(link, gameObject, null);
                    return;
                }

                if (LinkUiReflectionCache.UiGameObjectField != null)
                {
                    LinkUiReflectionCache.UiGameObjectField.SetValue(link, gameObject);
                }
            }
            catch
            {
            }
        }

        private static void SetIsActive(LinkUiElement link, bool value)
        {
            try
            {
                if (LinkUiReflectionCache.IsActiveProperty != null && LinkUiReflectionCache.IsActiveProperty.CanWrite)
                {
                    LinkUiReflectionCache.IsActiveProperty.SetValue(link, value, null);
                    return;
                }

                if (LinkUiReflectionCache.IsActiveBackingProperty != null && LinkUiReflectionCache.IsActiveBackingProperty.CanWrite)
                {
                    LinkUiReflectionCache.IsActiveBackingProperty.SetValue(link, value, null);
                }
            }
            catch
            {
            }
        }

        [Conditional("FASTTRAVEL_VERBOSE")]
        private static void LogVerbose(string message)
        {
            ModMain.LogMessage(message);
        }

        private static Transform FindSyncTemplate(Transform itemTemplate)
        {
            var sleep = itemTemplate.Find("SleepInteractionElement");
            if (sleep != null)
                return sleep;

            var save = itemTemplate.Find("SaveInteractionElement");
            if (save != null)
                return save;

            return null;
        }
    }
}
