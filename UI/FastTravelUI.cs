using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FastTravel.UI
{
    public static class FastTravelUI
    {
        private static FastTravelPanelController _controller;
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<FastTravelPanelController>();
                _initialized = true;
                ModMain.LogMessage("FastTravel: UI controller type registered.");
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: UI initialization failed: " + ex);
            }
        }

        public static bool IsAnyMenuOpen()
        {
            return _controller != null && _controller.IsOpen;
        }

        public static void ShowForBedObject(GameObject bedObject)
        {
            try
            {
                EnsureController();
                if (_controller == null)
                {
                    ModMain.LogMessage("FastTravel: UI controller was not created.");
                    return;
                }

                _controller.Open(bedObject);
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: ShowForBedObject failed: " + ex);
            }
        }

        private static void EnsureController()
        {
            Initialize();

            if (_controller != null)
                return;

            try
            {
                var host = new GameObject("FastTravelUIRoot");
                UnityEngine.Object.DontDestroyOnLoad(host);
                _controller = host.AddComponent<FastTravelPanelController>();
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: EnsureController failed: " + ex);
            }
        }
    }

    internal sealed class FastTravelPanelController : MonoBehaviour
    {
        public FastTravelPanelController(IntPtr ptr) : base(ptr)
        {
        }

        private enum FastTravelTransitionPhase
        {
            None,
            WaitingForClose,
            FadeOut,
            HoldBeforeFadeIn,
            FadeIn
        }

        private sealed class BedRowView
        {
            public int BedObjectId;
            public Image Background;
            public TextMeshProUGUI NameLabel;
            public TextMeshProUGUI DistanceLabel;
            public Color BaseBackgroundColor;
            public Color SelectedBackgroundColor;
        }

        private const float RefreshInterval = 0.35f;
        private const int NoSelection = int.MinValue;

        private GameObject _overlay;
        private GameObject _travelFadeOverlay;
        private RectTransform _window;
        private RectTransform _listContent;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _emptyListLabel;
        private TextMeshProUGUI _statusLabel;
        private Image _travelFadeImage;

        private Button _personalTabButton;
        private Button _publicTabButton;
        private Button _fastTravelButton;
        private Button _renameButton;
        private Button _visibilityButton;
        private TextMeshProUGUI _personalTabLabel;
        private TextMeshProUGUI _publicTabLabel;
        private TextMeshProUGUI _visibilityButtonLabel;

        private GameObject _renameModal;
        private TMP_InputField _renameInput;

        private readonly List<BedRowView> _rows = new List<BedRowView>();

        private bool _isOpen;
        private bool _showPublicTab;
        private float _nextRefreshAt;
        private int _currentBedObjectId = NoSelection;
        private int _selectedBedObjectId = NoSelection;
        private bool _previousCursorVisible;
        private CursorLockMode _previousCursorLockMode;
        private float _previousTimeScale = 1f;
        private bool _inputStateApplied;
        private bool _pauseGameplayWhileUiOpen;
        private bool _pendingEscReleaseRestore;
        private float _pendingEscRestoreStartedAt;
        private bool _pendingPointerReleaseRestore;
        private float _pendingPointerRestoreStartedAt;
        private bool _pendingPointerFinalizeRestore;
        private int _pointerFinalizeFramesRemaining;
        private bool _pendingOverlayHide;
        private bool _pendingPointerCloseRequest;
        private int _postRestoreInputFlushFrames;

        private static bool _pauseBlockResolverInitialized;
        private static MethodInfo _setBlockPauseMenuMethod;
        private static bool _pauseBlockStateKnown;
        private static bool _pauseBlockState;
        private static bool _menuModeFailureLogged;

        private const float EscRestoreFailSafeSeconds = 0.75f;
        private const float PointerRestoreMinDelaySeconds = 0.05f;
        private const float PointerRestoreFailSafeSeconds = 0.2f;
        private const float RefreshRetryWhilePointerHeldSeconds = 0.05f;
        private const float ForceRefreshWhilePointerHeldAfterSeconds = 1f;
        private const int PointerFinalizeStabilizeFrames = 2;
        private const int PostRestoreInputFlushFrames = 3;

        private float _pointerHeldRefreshDeferredStartedAt = -1f;
        private int _lastKnownLocationStateVersion = -1;
        private int _pendingVisibilityBedObjectId = NoSelection;
        private bool _pendingVisibilityTargetPublic;
        private bool _suppressAutoSelectionOnNextRefresh;
        private bool _isFastTravelTransitionActive;
        private FastTravelTransitionPhase _fastTravelTransitionPhase;
        private FastTravelBedLocation _pendingTravelLocation;
        private float _fastTravelTransitionPhaseStartedAt;
        private bool _transitionInputLockActive;
        private bool _transitionInputPreviousCursorVisible;
        private CursorLockMode _transitionInputPreviousCursorLockMode;

        private const float FastTravelFadeOutSeconds = 2f;
        private const float FastTravelFadeInSeconds = 2f;
        private const float FastTravelFadeHoldSeconds = 6f;
        private const float FastTravelCloseWaitTimeoutSeconds = 2f;

        public bool IsOpen
        {
            get { return _isOpen; }
        }

        private void Awake()
        {
            EnsureEventSystem();
            BuildUi();
            Close();
        }

        private void Update()
        {
            if (_postRestoreInputFlushFrames > 0)
            {
                Input.ResetInputAxes();
                _postRestoreInputFlushFrames--;
            }

            TickFastTravelTransition();

            if (_pendingPointerCloseRequest)
            {
                _pendingPointerCloseRequest = false;

                if (_isOpen)
                {
                    CloseInternal(restoreImmediately: false, waitForEscRelease: false, waitForPointerRelease: true);
                    return;
                }
            }

            if (_pendingPointerFinalizeRestore)
            {
                // Keep gameplay paused briefly while mouse lock/axes settle after pointer close.
                SetPauseMenuBlocked(true);
                EnforceUiInputState();

                Input.ResetInputAxes();

                _pointerFinalizeFramesRemaining--;
                if (_pointerFinalizeFramesRemaining <= 0)
                {
                    _pendingPointerFinalizeRestore = false;
                    RestoreUiInputState();
                }

                return;
            }

            if (_pendingEscReleaseRestore || _pendingPointerReleaseRestore)
            {
                // Hold input lock until release gates finish to avoid click/ESC leaking into gameplay.
                SetPauseMenuBlocked(true);
                EnforceUiInputState();
                Input.ResetInputAxes();

                bool escReleased = !_pendingEscReleaseRestore || !Input.GetKey(KeyCode.Escape);
                bool escTimedOut = !_pendingEscReleaseRestore || (Time.unscaledTime - _pendingEscRestoreStartedAt) >= EscRestoreFailSafeSeconds;

                bool pointerReleased = !_pendingPointerReleaseRestore || !IsAnyMouseButtonHeld();
                bool pointerMinDelayReached = !_pendingPointerReleaseRestore || (Time.unscaledTime - _pendingPointerRestoreStartedAt) >= PointerRestoreMinDelaySeconds;
                bool pointerTimedOut = !_pendingPointerReleaseRestore || (Time.unscaledTime - _pendingPointerRestoreStartedAt) >= PointerRestoreFailSafeSeconds;

                bool escDone = escReleased || escTimedOut;
                bool pointerDone = (pointerReleased && pointerMinDelayReached) || pointerTimedOut;

                if (escDone && pointerDone)
                {
                    bool pointerWasPending = _pendingPointerReleaseRestore;
                    _pendingEscReleaseRestore = false;
                    _pendingPointerReleaseRestore = false;

                    if (pointerWasPending)
                    {
                        BeginPointerFinalizeRestore();
                    }
                    else
                    {
                        RestoreUiInputState();
                    }
                }

                return;
            }

            if (!_isOpen)
                return;

            // Keep menu input state enforced while UI is open.
            SetPauseMenuBlocked(true);
            EnforceUiInputState();

            TryRefreshForRegistryStateChange();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CloseFromEscape();
                return;
            }

            if (Time.unscaledTime >= _nextRefreshAt)
            {
                bool pointerHeld = IsAnyMouseButtonHeld();
                if (pointerHeld)
                {
                    if (_pointerHeldRefreshDeferredStartedAt < 0f)
                        _pointerHeldRefreshDeferredStartedAt = Time.unscaledTime;

                    // Avoid rebuilding rows while pointer is held; button click is committed on mouse-up.
                    // Force a periodic refresh if held state appears to get stuck, so authoritative updates remain visible.
                    bool forceRefresh = (Time.unscaledTime - _pointerHeldRefreshDeferredStartedAt) >= ForceRefreshWhilePointerHeldAfterSeconds;
                    if (!forceRefresh)
                    {
                        _nextRefreshAt = Time.unscaledTime + RefreshRetryWhilePointerHeldSeconds;
                    }
                    else
                    {
                        RefreshList(keepSelection: true);
                        _nextRefreshAt = Time.unscaledTime + RefreshInterval;
                        _pointerHeldRefreshDeferredStartedAt = -1f;
                    }
                }
                else
                {
                    _pointerHeldRefreshDeferredStartedAt = -1f;
                    RefreshList(keepSelection: true);
                    _nextRefreshAt = Time.unscaledTime + RefreshInterval;
                }
            }
        }

        public void Open(GameObject bedObject)
        {
            if (bedObject != null)
            {
                _currentBedObjectId = bedObject.GetInstanceID();
            }
            else
            {
                _currentBedObjectId = NoSelection;
            }

            _showPublicTab = false;
            _isOpen = true;
            _pendingEscReleaseRestore = false;
            _pendingPointerReleaseRestore = false;
            _pendingPointerFinalizeRestore = false;
            _pointerFinalizeFramesRemaining = 0;
            _pendingOverlayHide = false;
            _pendingPointerCloseRequest = false;
            _postRestoreInputFlushFrames = 0;
            _pointerHeldRefreshDeferredStartedAt = -1f;
            _pendingVisibilityBedObjectId = NoSelection;
            _lastKnownLocationStateVersion = FastTravelLocationRegistry.GetStateVersion();
            _suppressAutoSelectionOnNextRefresh = true;
            _overlay.SetActive(true);
            if (_window != null)
                _window.gameObject.SetActive(true);
            _renameModal.SetActive(false);
            ApplyUiInputState();
            SetStatus("Select a bed destination.");
            RefreshTabVisuals();
            RefreshList(keepSelection: false);
            _nextRefreshAt = Time.unscaledTime + RefreshInterval;
        }

        private void Close()
        {
            CloseInternal(restoreImmediately: true, waitForEscRelease: false, waitForPointerRelease: false);
        }

        private void CloseFromPointerUiAction()
        {
            if (!_isOpen)
                return;

            _pendingPointerCloseRequest = true;
            Input.ResetInputAxes();
        }

        private void CloseFromEscape()
        {
            CloseInternal(restoreImmediately: false, waitForEscRelease: true, waitForPointerRelease: false);
        }

        private void CloseInternal(bool restoreImmediately, bool waitForEscRelease, bool waitForPointerRelease)
        {
            _isOpen = false;
            _pendingVisibilityBedObjectId = NoSelection;
            if (_renameModal != null)
                _renameModal.SetActive(false);
            if (_window != null)
                _window.gameObject.SetActive(false);

            if (restoreImmediately)
            {
                _pendingEscReleaseRestore = false;
                _pendingPointerReleaseRestore = false;
                _pendingPointerFinalizeRestore = false;
                _pointerFinalizeFramesRemaining = 0;
                _pendingPointerCloseRequest = false;
                _pointerHeldRefreshDeferredStartedAt = -1f;
                _lastKnownLocationStateVersion = FastTravelLocationRegistry.GetStateVersion();

                if (_overlay != null)
                    _overlay.SetActive(false);

                RestoreUiInputState();
                return;
            }

            _pendingOverlayHide = true;

            _pendingEscReleaseRestore = waitForEscRelease;
            _pendingPointerReleaseRestore = waitForPointerRelease;

            if (waitForEscRelease)
                _pendingEscRestoreStartedAt = Time.unscaledTime;

            if (waitForPointerRelease)
                _pendingPointerRestoreStartedAt = Time.unscaledTime;

            Input.ResetInputAxes();
        }

        private void OnDisable()
        {
            CancelFastTravelTransition();
            _pendingEscReleaseRestore = false;
            _pendingPointerReleaseRestore = false;
            _pendingPointerFinalizeRestore = false;
            _pointerFinalizeFramesRemaining = 0;
            _pendingOverlayHide = false;
            _pendingPointerCloseRequest = false;
            _postRestoreInputFlushFrames = 0;
            _pointerHeldRefreshDeferredStartedAt = -1f;
            _pendingVisibilityBedObjectId = NoSelection;
            _lastKnownLocationStateVersion = FastTravelLocationRegistry.GetStateVersion();
            if (_overlay != null)
                _overlay.SetActive(false);
            RestoreUiInputState();
        }

        private void OnDestroy()
        {
            CancelFastTravelTransition();
            _pendingEscReleaseRestore = false;
            _pendingPointerReleaseRestore = false;
            _pendingPointerFinalizeRestore = false;
            _pointerFinalizeFramesRemaining = 0;
            _pendingOverlayHide = false;
            _pendingPointerCloseRequest = false;
            _postRestoreInputFlushFrames = 0;
            _pointerHeldRefreshDeferredStartedAt = -1f;
            _pendingVisibilityBedObjectId = NoSelection;
            _lastKnownLocationStateVersion = FastTravelLocationRegistry.GetStateVersion();
            RestoreUiInputState();
        }

        private void BuildUi()
        {
            _overlay = new GameObject("FastTravelOverlay");
            _overlay.transform.SetParent(transform, false);
            _overlay.AddComponent<RectTransform>();
            _overlay.AddComponent<Canvas>();
            _overlay.AddComponent<CanvasScaler>();
            _overlay.AddComponent<GraphicRaycaster>();
            _overlay.AddComponent<Image>();

            var canvas = _overlay.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;

            var scaler = _overlay.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var overlayRect = _overlay.GetComponent<RectTransform>();
            StretchToParent(overlayRect);

            var overlayImage = _overlay.GetComponent<Image>();
            overlayImage.color = new Color(0f, 0f, 0f, 0.6f);

            BuildTravelFadeOverlay();

            _window = CreateRect("Window", overlayRect);
            _window.anchorMin = new Vector2(0.5f, 0.5f);
            _window.anchorMax = new Vector2(0.5f, 0.5f);
            _window.pivot = new Vector2(0.5f, 0.5f);
            _window.sizeDelta = new Vector2(980f, 620f);

            var windowImage = _window.gameObject.AddComponent<Image>();
            windowImage.color = new Color(0.1f, 0.12f, 0.16f, 0.96f);

            var title = CreateText("Title", _window, "Fast travel:", 44, TextAlignmentOptions.Left);
            title.rectTransform.anchorMin = new Vector2(0.04f, 0.9f);
            title.rectTransform.anchorMax = new Vector2(0.55f, 0.98f);
            title.color = new Color(0.93f, 0.95f, 0.99f, 1f);

            var closeButton = CreateButton(_window, "CloseButton", "X", out _);
            SetRect(closeButton.GetComponent<RectTransform>(), new Vector2(0.93f, 0.92f), new Vector2(0.98f, 0.98f));
            closeButton.onClick.AddListener((Action)CloseFromPointerUiAction);

            _personalTabButton = CreateButton(_window, "PersonalTab", "Personal", out _personalTabLabel);
            SetRect(_personalTabButton.GetComponent<RectTransform>(), new Vector2(0.04f, 0.82f), new Vector2(0.2f, 0.89f));
            _personalTabButton.onClick.AddListener((Action)OnPersonalTabClicked);

            _publicTabButton = CreateButton(_window, "PublicTab", "Public", out _publicTabLabel);
            SetRect(_publicTabButton.GetComponent<RectTransform>(), new Vector2(0.22f, 0.82f), new Vector2(0.38f, 0.89f));
            _publicTabButton.onClick.AddListener((Action)OnPublicTabClicked);

            var listFrame = CreateRect("ListFrame", _window);
            SetRect(listFrame, new Vector2(0.04f, 0.24f), new Vector2(0.96f, 0.78f));
            var listFrameImage = listFrame.gameObject.AddComponent<Image>();
            listFrameImage.color = new Color(0.13f, 0.16f, 0.22f, 0.92f);

            var viewport = CreateRect("Viewport", listFrame);
            StretchToParent(viewport, 8f);
            var viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _listContent = CreateRect("Content", viewport);
            _listContent.anchorMin = new Vector2(0f, 1f);
            _listContent.anchorMax = new Vector2(1f, 1f);
            _listContent.pivot = new Vector2(0.5f, 1f);
            _listContent.anchoredPosition = Vector2.zero;
            _listContent.sizeDelta = new Vector2(0f, 0f);

            _scrollRect = listFrame.gameObject.AddComponent<ScrollRect>();
            _scrollRect.viewport = viewport;
            _scrollRect.content = _listContent;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 24f;

            _emptyListLabel = CreateText("EmptyListLabel", viewport, "No beds discovered yet.", 28, TextAlignmentOptions.Center);
            StretchToParent(_emptyListLabel.rectTransform, 20f);
            _emptyListLabel.color = new Color(0.75f, 0.78f, 0.85f, 1f);

            _fastTravelButton = CreateButton(_window, "FastTravelButton", "Fast travel", out _);
            SetRect(_fastTravelButton.GetComponent<RectTransform>(), new Vector2(0.04f, 0.12f), new Vector2(0.31f, 0.2f));
            _fastTravelButton.onClick.AddListener((Action)OnFastTravelClicked);

            _renameButton = CreateButton(_window, "RenameButton", "Rename", out _);
            SetRect(_renameButton.GetComponent<RectTransform>(), new Vector2(0.35f, 0.12f), new Vector2(0.62f, 0.2f));
            _renameButton.onClick.AddListener((Action)OnRenameClicked);

            _visibilityButton = CreateButton(_window, "VisibilityButton", "Make Public", out _visibilityButtonLabel);
            SetRect(_visibilityButton.GetComponent<RectTransform>(), new Vector2(0.66f, 0.12f), new Vector2(0.96f, 0.2f));
            _visibilityButton.onClick.AddListener((Action)OnVisibilityClicked);

            _statusLabel = CreateText("Status", _window, string.Empty, 24, TextAlignmentOptions.Left);
            _statusLabel.rectTransform.anchorMin = new Vector2(0.04f, 0.03f);
            _statusLabel.rectTransform.anchorMax = new Vector2(0.96f, 0.1f);
            _statusLabel.color = new Color(0.77f, 0.8f, 0.9f, 1f);

            BuildRenameModal();
            RefreshActionButtons();
            RefreshTabVisuals();
        }

        private void BuildTravelFadeOverlay()
        {
            _travelFadeOverlay = new GameObject("FastTravelFadeOverlay");
            _travelFadeOverlay.transform.SetParent(transform, false);
            _travelFadeOverlay.AddComponent<RectTransform>();
            _travelFadeOverlay.AddComponent<Canvas>();
            _travelFadeOverlay.AddComponent<CanvasScaler>();
            _travelFadeOverlay.AddComponent<GraphicRaycaster>();
            _travelFadeOverlay.AddComponent<Image>();

            var canvas = _travelFadeOverlay.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5100;

            var scaler = _travelFadeOverlay.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var rect = _travelFadeOverlay.GetComponent<RectTransform>();
            StretchToParent(rect);

            _travelFadeImage = _travelFadeOverlay.GetComponent<Image>();
            _travelFadeImage.color = new Color(0f, 0f, 0f, 0f);
            _travelFadeImage.raycastTarget = true;

            _travelFadeOverlay.SetActive(false);
        }

        private void BeginFastTravelTransition(FastTravelBedLocation location)
        {
            if (location == null)
                return;

            CancelFastTravelTransition();
            _pendingTravelLocation = location;
            _isFastTravelTransitionActive = true;
            StartFastTravelTransitionPhase(FastTravelTransitionPhase.WaitingForClose);
            CloseFromPointerUiAction();
        }

        private void CancelFastTravelTransition()
        {
            _isFastTravelTransitionActive = false;
            _fastTravelTransitionPhase = FastTravelTransitionPhase.None;
            _pendingTravelLocation = null;
            ReleaseTransitionInputLock();
            SetTravelFadeOverlayVisible(false);

            if (_travelFadeImage != null)
                SetTravelFadeAlpha(0f);
        }

        private void StartFastTravelTransitionPhase(FastTravelTransitionPhase phase)
        {
            _fastTravelTransitionPhase = phase;
            _fastTravelTransitionPhaseStartedAt = Time.unscaledTime;
        }

        private void TickFastTravelTransition()
        {
            if (!_isFastTravelTransitionActive)
                return;

            if (_transitionInputLockActive)
                EnforceTransitionInputLockState();

            switch (_fastTravelTransitionPhase)
            {
                case FastTravelTransitionPhase.WaitingForClose:
                {
                    bool closeComplete = !_isOpen
                        && !_pendingPointerCloseRequest
                        && !_pendingEscReleaseRestore
                        && !_pendingPointerReleaseRestore
                        && !_pendingPointerFinalizeRestore
                        && !_inputStateApplied;

                    bool waitTimedOut = (Time.unscaledTime - _fastTravelTransitionPhaseStartedAt) >= FastTravelCloseWaitTimeoutSeconds;
                    if (!closeComplete && !waitTimedOut)
                        return;

                    AcquireTransitionInputLock();
                    SetTravelFadeOverlayVisible(true);
                    SetTravelFadeAlpha(0f);
                    StartFastTravelTransitionPhase(FastTravelTransitionPhase.FadeOut);
                    return;
                }

                case FastTravelTransitionPhase.FadeOut:
                {
                    float progress = FastTravelFadeOutSeconds <= 0f
                        ? 1f
                        : Mathf.Clamp01((Time.unscaledTime - _fastTravelTransitionPhaseStartedAt) / FastTravelFadeOutSeconds);

                    SetTravelFadeAlpha(progress);
                    if (progress < 1f)
                        return;

                    bool teleported = FastTravelTeleportService.TryTeleportToBed(_pendingTravelLocation, out string status);
                    ModMain.LogMessage("FastTravel: Transition teleport result -> success=" + teleported + " status='" + status + "'.");

                    StartFastTravelTransitionPhase(FastTravelTransitionPhase.HoldBeforeFadeIn);

                    return;
                }

                case FastTravelTransitionPhase.HoldBeforeFadeIn:
                {
                    if ((Time.unscaledTime - _fastTravelTransitionPhaseStartedAt) < FastTravelFadeHoldSeconds)
                        return;

                    ReleaseTransitionInputLock();
                    StartFastTravelTransitionPhase(FastTravelTransitionPhase.FadeIn);
                    return;
                }

                case FastTravelTransitionPhase.FadeIn:
                {
                    float progress = FastTravelFadeInSeconds <= 0f
                        ? 1f
                        : Mathf.Clamp01((Time.unscaledTime - _fastTravelTransitionPhaseStartedAt) / FastTravelFadeInSeconds);

                    SetTravelFadeAlpha(1f - progress);
                    if (progress < 1f)
                        return;

                    CancelFastTravelTransition();
                    return;
                }
            }
        }

        private void SetTravelFadeOverlayVisible(bool visible)
        {
            if (_travelFadeOverlay == null)
                return;

            _travelFadeOverlay.SetActive(visible);
            if (visible)
                _travelFadeOverlay.transform.SetAsLastSibling();
        }

        private void SetTravelFadeAlpha(float alpha)
        {
            if (_travelFadeImage == null)
                return;

            Color color = _travelFadeImage.color;
            color.a = Mathf.Clamp01(alpha);
            _travelFadeImage.color = color;
        }

        private void AcquireTransitionInputLock()
        {
            if (_transitionInputLockActive)
                return;

            _transitionInputPreviousCursorVisible = Cursor.visible;
            _transitionInputPreviousCursorLockMode = Cursor.lockState;

            SetPauseMenuBlocked(true);
            TrySetMenuMode(true);
            EnforceTransitionInputLockState();
            Input.ResetInputAxes();

            _transitionInputLockActive = true;
        }

        private static void EnforceTransitionInputLockState()
        {
            if (Cursor.visible)
                Cursor.visible = false;

            if (Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;
        }

        private void ReleaseTransitionInputLock()
        {
            if (!_transitionInputLockActive)
                return;

            TrySetMenuMode(false);
            SetPauseMenuBlocked(false);

            Cursor.visible = _transitionInputPreviousCursorVisible;
            Cursor.lockState = _transitionInputPreviousCursorLockMode;

            Input.ResetInputAxes();
            _transitionInputLockActive = false;
        }

        private void BuildRenameModal()
        {
            _renameModal = new GameObject("RenameModal");
            _renameModal.transform.SetParent(_overlay.transform, false);
            _renameModal.AddComponent<RectTransform>();
            _renameModal.AddComponent<Image>();

            var modalRect = _renameModal.GetComponent<RectTransform>();
            StretchToParent(modalRect);

            var modalBack = _renameModal.GetComponent<Image>();
            modalBack.color = new Color(0f, 0f, 0f, 0.72f);

            var panel = CreateRect("RenamePanel", modalRect);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(620f, 280f);
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(0.12f, 0.14f, 0.2f, 0.98f);

            var title = CreateText("RenameTitle", panel, "Rename bed", 34, TextAlignmentOptions.Left);
            title.rectTransform.anchorMin = new Vector2(0.07f, 0.73f);
            title.rectTransform.anchorMax = new Vector2(0.93f, 0.9f);

            _renameInput = CreateInputField(panel, "RenameInput", "Enter bed name...");
            SetRect(_renameInput.GetComponent<RectTransform>(), new Vector2(0.07f, 0.42f), new Vector2(0.93f, 0.64f));

            var applyButton = CreateButton(panel, "RenameApply", "Apply", out _);
            SetRect(applyButton.GetComponent<RectTransform>(), new Vector2(0.07f, 0.13f), new Vector2(0.45f, 0.3f));
            applyButton.onClick.AddListener((Action)OnRenameApplyClicked);

            var cancelButton = CreateButton(panel, "RenameCancel", "Cancel", out _);
            SetRect(cancelButton.GetComponent<RectTransform>(), new Vector2(0.55f, 0.13f), new Vector2(0.93f, 0.3f));
            cancelButton.onClick.AddListener((Action)(() => _renameModal.SetActive(false)));

            _renameModal.SetActive(false);
        }

        private void OnPersonalTabClicked()
        {
            _showPublicTab = false;
            RefreshTabVisuals();
            SetStatus("Private beds list.");
            RefreshList(keepSelection: true);
        }

        private void OnPublicTabClicked()
        {
            _showPublicTab = true;
            RefreshTabVisuals();
            SetStatus("Public beds list (server authoritative).");
            RefreshList(keepSelection: true);
        }

        private void OnFastTravelClicked()
        {
            if (_isFastTravelTransitionActive)
                return;

            if (_selectedBedObjectId == NoSelection)
                return;

            if (_selectedBedObjectId == _currentBedObjectId)
            {
                SetStatus("You are already here.");
                return;
            }

            if (FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var location))
            {
                if (FastTravelTeleportService.TryGetCooldownRemaining(out float remainingSeconds))
                {
                    SetStatus("Fast travel is cooling down (" + Mathf.CeilToInt(remainingSeconds) + "s).");
                    return;
                }

                SetStatus("Fast traveling...");
                BeginFastTravelTransition(location);
            }
            else
            {
                SetStatus("Selected bed is no longer available.");
                RefreshList(keepSelection: false);
            }
        }

        private void OnRenameClicked()
        {
            if (_selectedBedObjectId == NoSelection)
                return;

            if (!FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var location))
            {
                SetStatus("Selected bed is no longer available.");
                RefreshList(keepSelection: false);
                return;
            }

            if (!CanRenameLocation(location))
            {
                SetStatus("This bed cannot be renamed.");
                return;
            }

            _renameInput.text = location.DisplayName;
            _renameModal.SetActive(true);
            _renameInput.ActivateInputField();
            _renameInput.Select();
        }

        private void OnVisibilityClicked()
        {
            if (_selectedBedObjectId == NoSelection)
                return;

            if (!FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var selectedLocation))
            {
                SetStatus("Selected bed is no longer available.");
                RefreshList(keepSelection: false);
                return;
            }

            bool makePublic = !selectedLocation.IsPublic;
            if (!FastTravelLocationRegistry.TrySetBedPublicStateByObjectId(_selectedBedObjectId, makePublic, out string failureReason))
            {
                SetStatus(string.IsNullOrEmpty(failureReason)
                    ? "Could not change bed visibility."
                    : failureReason);
                RefreshActionButtons();
                return;
            }

            bool changedLocally = false;
            if (FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var refreshedLocation))
            {
                changedLocally = refreshedLocation.IsPublic == makePublic;
            }

            if (changedLocally)
            {
                _pendingVisibilityBedObjectId = NoSelection;
                SetStatus(makePublic
                    ? "Bed is now public."
                    : "Bed is now private.");
            }
            else
            {
                _pendingVisibilityBedObjectId = _selectedBedObjectId;
                _pendingVisibilityTargetPublic = makePublic;
                SetStatus("Visibility request sent. Waiting for server confirmation.");
            }

            RefreshList(keepSelection: false);
        }

        private void OnRenameApplyClicked()
        {
            if (_selectedBedObjectId == NoSelection)
            {
                _renameModal.SetActive(false);
                return;
            }

            bool isPersonalPublicAlias = false;
            if (FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var selectedLocation))
                isPersonalPublicAlias = _showPublicTab && selectedLocation.IsPublic && !selectedLocation.IsOwnedByLocalPlayer;

            bool renamed = FastTravelLocationRegistry.RenameBedByObjectId(_selectedBedObjectId, _renameInput.text, isPersonalPublicAlias);
            _renameModal.SetActive(false);

            if (!renamed)
            {
                SetStatus("Rename failed: selected bed is no longer available.");
                RefreshList(keepSelection: false);
                return;
            }

            SetStatus(isPersonalPublicAlias
                ? "Public bed alias saved (local only)."
                : "Bed renamed.");
            RefreshList(keepSelection: true);
        }

        private void RefreshList(bool keepSelection)
        {
            _lastKnownLocationStateVersion = FastTravelLocationRegistry.GetStateVersion();

            var locations = _showPublicTab
                ? FastTravelLocationRegistry.GetPublicSnapshot(includeInactive: true)
                : FastTravelLocationRegistry.GetPrivateSnapshot(includeInactive: true);

            ClearRows();

            if (locations.Count == 0)
            {
                _emptyListLabel.gameObject.SetActive(true);
                _selectedBedObjectId = NoSelection;
                RefreshActionButtons();
                TryResolvePendingVisibilityConfirmationStatus();
                return;
            }

            _emptyListLabel.gameObject.SetActive(false);

            if (!keepSelection)
            {
                if (_suppressAutoSelectionOnNextRefresh)
                {
                    _selectedBedObjectId = NoSelection;
                    _suppressAutoSelectionOnNextRefresh = false;
                }
                else
                {
                    _selectedBedObjectId = PickDefaultSelection(locations);
                }
            }
            else if (_selectedBedObjectId != NoSelection && !ContainsBed(locations, _selectedBedObjectId))
            {
                _selectedBedObjectId = PickDefaultSelection(locations);
            }

            FastTravelBedLocation currentLocation = null;
            FastTravelLocationRegistry.TryGetByBedObjectId(_currentBedObjectId, out currentLocation);

            float y = -4f;
            for (int i = 0; i < locations.Count; i++)
            {
                var location = locations[i];
                var rowRect = CreateRect("Row_" + location.BedObjectId, _listContent);
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(1f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.anchoredPosition = new Vector2(0f, y);
                rowRect.sizeDelta = new Vector2(0f, 42f);
                y -= 46f;

                var rowImage = rowRect.gameObject.AddComponent<Image>();
                bool isPublicBed = location.IsPublic;
                Color baseColor = isPublicBed
                    ? new Color(0.12f, 0.45f, 0.5f, 0.96f)
                    : new Color(0.28f, 0.28f, 0.3f, 0.96f);
                Color selectedColor = isPublicBed
                    ? new Color(0.2f, 0.68f, 0.74f, 0.98f)
                    : new Color(0.43f, 0.43f, 0.46f, 0.98f);
                rowImage.color = baseColor;

                var rowButton = rowRect.gameObject.AddComponent<Button>();
                var rowColors = rowButton.colors;
                rowColors.normalColor = new Color(1f, 1f, 1f, 1f);
                rowColors.highlightedColor = new Color(1f, 1f, 1f, 1f);
                rowColors.pressedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
                rowColors.selectedColor = new Color(1f, 1f, 1f, 1f);
                rowButton.colors = rowColors;

                int selectedId = location.BedObjectId;
                rowButton.onClick.AddListener((Action)(() => OnRowSelected(selectedId)));

                string displayName = BuildRowDisplayName(location);
                var nameLabel = CreateText("Name", rowRect, displayName, 25, TextAlignmentOptions.Left);
                nameLabel.rectTransform.anchorMin = new Vector2(0.03f, 0.1f);
                nameLabel.rectTransform.anchorMax = new Vector2(0.68f, 0.9f);
                nameLabel.color = new Color(0.95f, 0.96f, 0.99f, 1f);

                var distanceLabel = CreateText("Distance", rowRect, BuildDistanceLabel(currentLocation, location), 22, TextAlignmentOptions.Right);
                distanceLabel.rectTransform.anchorMin = new Vector2(0.7f, 0.1f);
                distanceLabel.rectTransform.anchorMax = new Vector2(0.97f, 0.9f);
                distanceLabel.color = new Color(0.78f, 0.82f, 0.9f, 1f);

                _rows.Add(new BedRowView
                {
                    BedObjectId = location.BedObjectId,
                    Background = rowImage,
                    NameLabel = nameLabel,
                    DistanceLabel = distanceLabel,
                    BaseBackgroundColor = baseColor,
                    SelectedBackgroundColor = selectedColor
                });
            }

            _listContent.sizeDelta = new Vector2(0f, Mathf.Max(0f, -y + 8f));
            _scrollRect.verticalNormalizedPosition = 1f;

            RefreshRowSelectionVisuals();
            RefreshActionButtons();
            TryResolvePendingVisibilityConfirmationStatus();
        }

        private void OnRowSelected(int bedObjectId)
        {
            _selectedBedObjectId = bedObjectId;
            RefreshRowSelectionVisuals();
            RefreshActionButtons();

            if (FastTravelLocationRegistry.TryGetByBedObjectId(bedObjectId, out var location))
            {
                SetStatus("Selected: " + location.DisplayName);
            }
        }

        private void RefreshRowSelectionVisuals()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool selected = row.BedObjectId == _selectedBedObjectId;

                row.Background.color = selected
                    ? row.SelectedBackgroundColor
                    : row.BaseBackgroundColor;

                row.NameLabel.color = selected
                    ? new Color(1f, 1f, 1f, 1f)
                    : new Color(0.95f, 0.96f, 0.99f, 1f);

                row.DistanceLabel.color = selected
                    ? new Color(0.88f, 0.92f, 1f, 1f)
                    : new Color(0.78f, 0.82f, 0.9f, 1f);
            }
        }

        private void RefreshActionButtons()
        {
            bool hasSelection = _selectedBedObjectId != NoSelection;
            _fastTravelButton.interactable = hasSelection;

            bool canRename = false;
            bool canToggleVisibility = false;
            string visibilityButtonText = "Make Public";

            if (hasSelection && FastTravelLocationRegistry.TryGetByBedObjectId(_selectedBedObjectId, out var selectedLocation))
            {
                canRename = CanRenameLocation(selectedLocation);
                canToggleVisibility = selectedLocation.IsOwnedByLocalPlayer;
                visibilityButtonText = selectedLocation.IsPublic ? "Make Private" : "Make Public";
            }

            _renameButton.interactable = canRename;
            _visibilityButton.interactable = canToggleVisibility;

            if (_visibilityButtonLabel != null)
                _visibilityButtonLabel.text = visibilityButtonText;
        }

        private bool CanRenameLocation(FastTravelBedLocation location)
        {
            if (location == null)
                return false;

            if (location.IsOwnedByLocalPlayer)
                return true;

            return _showPublicTab && location.IsPublic;
        }

        private void RefreshTabVisuals()
        {
            ApplyTabStyle(_personalTabButton, _personalTabLabel, !_showPublicTab);
            ApplyTabStyle(_publicTabButton, _publicTabLabel, _showPublicTab);
        }

        private static void ApplyTabStyle(Button button, TextMeshProUGUI label, bool active)
        {
            var img = button.GetComponent<Image>();
            if (img != null)
            {
                img.color = active
                    ? new Color(0.26f, 0.34f, 0.53f, 1f)
                    : new Color(0.19f, 0.22f, 0.3f, 1f);
            }

            if (label != null)
                label.color = active ? Color.white : new Color(0.82f, 0.86f, 0.94f, 1f);
        }

        private static string BuildDistanceLabel(FastTravelBedLocation current, FastTravelBedLocation candidate)
        {
            if (candidate == null)
                return "(distance away)";

            if (current != null && current.BedObjectId == candidate.BedObjectId)
                return "(You are Here)";

            if (current == null)
                return "(distance away)";

            float distance = Vector3.Distance(current.Position, candidate.Position);
            int rounded = Mathf.RoundToInt(distance);
            return "(" + rounded + "m away)";
        }

        private string BuildRowDisplayName(FastTravelBedLocation location)
        {
            if (location == null)
                return string.Empty;

            if (!_showPublicTab)
                return location.DisplayName;

            string owner = !string.IsNullOrEmpty(location.OwnerDisplayName)
                ? location.OwnerDisplayName
                : location.OwnerId;

            if (string.IsNullOrEmpty(owner))
                owner = "Unknown";

            return location.DisplayName + " (" + owner + ")";
        }

        private static bool ContainsBed(List<FastTravelBedLocation> locations, int bedObjectId)
        {
            if (bedObjectId == NoSelection)
                return false;

            for (int i = 0; i < locations.Count; i++)
            {
                if (locations[i].BedObjectId == bedObjectId)
                    return true;
            }

            return false;
        }

        private int PickDefaultSelection(List<FastTravelBedLocation> locations)
        {
            if (locations == null || locations.Count == 0)
                return NoSelection;

            for (int i = 0; i < locations.Count; i++)
            {
                if (locations[i].BedObjectId != _currentBedObjectId)
                    return locations[i].BedObjectId;
            }

            return locations[0].BedObjectId;
        }

        private void ClearRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row != null && row.Background != null)
                    UnityEngine.Object.Destroy(row.Background.gameObject);
            }

            _rows.Clear();
        }

        private void TryRefreshForRegistryStateChange()
        {
            int currentStateVersion = FastTravelLocationRegistry.GetStateVersion();
            if (currentStateVersion == _lastKnownLocationStateVersion)
                return;

            _lastKnownLocationStateVersion = currentStateVersion;

            if (IsAnyMouseButtonHeld())
            {
                if (_pointerHeldRefreshDeferredStartedAt < 0f)
                    _pointerHeldRefreshDeferredStartedAt = Time.unscaledTime;

                _nextRefreshAt = Time.unscaledTime + RefreshRetryWhilePointerHeldSeconds;
                return;
            }

            _pointerHeldRefreshDeferredStartedAt = -1f;
            RefreshList(keepSelection: true);
            _nextRefreshAt = Time.unscaledTime + RefreshInterval;
        }

        private void TryResolvePendingVisibilityConfirmationStatus()
        {
            if (_pendingVisibilityBedObjectId == NoSelection)
                return;

            if (!FastTravelLocationRegistry.TryGetByBedObjectId(_pendingVisibilityBedObjectId, out var location))
                return;

            if (location.IsPublic != _pendingVisibilityTargetPublic)
                return;

            SetStatus(_pendingVisibilityTargetPublic
                ? "Server confirmed: bed is now public."
                : "Server confirmed: bed is now private.");

            _pendingVisibilityBedObjectId = NoSelection;
        }

        private void SetStatus(string message)
        {
            if (_statusLabel != null)
                _statusLabel.text = message ?? string.Empty;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            var eventSystem = new GameObject("FastTravelEventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        private static TextMeshProUGUI CreateText(string name, RectTransform parent, string content, float fontSize, TextAlignmentOptions align)
        {
            var rect = CreateRect(name, parent);
            StretchToParent(rect);

            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = align;
            text.enableWordWrapping = false;
            text.richText = false;

            if (text.font == null && TMP_Settings.defaultFontAsset != null)
                text.font = TMP_Settings.defaultFontAsset;

            return text;
        }

        private static Button CreateButton(RectTransform parent, string name, string label, out TextMeshProUGUI labelText)
        {
            var rect = CreateRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.22f, 0.27f, 0.39f, 1f);

            var button = rect.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;

            labelText = CreateText("Label", rect, label, 24, TextAlignmentOptions.Center);
            labelText.color = Color.white;
            return button;
        }

        private static TMP_InputField CreateInputField(RectTransform parent, string name, string placeholder)
        {
            var rect = CreateRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(0.08f, 0.1f, 0.14f, 1f);

            var input = rect.gameObject.AddComponent<TMP_InputField>();
            input.characterLimit = 40;
            input.lineType = TMP_InputField.LineType.SingleLine;

            var textRect = CreateRect("Text", rect);
            StretchToParent(textRect, 10f);
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.Left;
            text.enableWordWrapping = false;
            text.color = new Color(0.95f, 0.97f, 1f, 1f);
            if (text.font == null && TMP_Settings.defaultFontAsset != null)
                text.font = TMP_Settings.defaultFontAsset;

            var placeholderRect = CreateRect("Placeholder", rect);
            StretchToParent(placeholderRect, 10f);
            var placeholderText = placeholderRect.gameObject.AddComponent<TextMeshProUGUI>();
            placeholderText.text = placeholder;
            placeholderText.fontSize = 24f;
            placeholderText.alignment = TextAlignmentOptions.Left;
            placeholderText.enableWordWrapping = false;
            placeholderText.fontStyle = FontStyles.Italic;
            placeholderText.color = new Color(0.6f, 0.65f, 0.76f, 1f);
            if (placeholderText.font == null && TMP_Settings.defaultFontAsset != null)
                placeholderText.font = TMP_Settings.defaultFontAsset;

            input.textViewport = rect;
            input.textComponent = text;
            input.placeholder = placeholderText;
            return input;
        }

        private static void StretchToParent(RectTransform rect, float margin = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(margin, margin);
            rect.offsetMax = new Vector2(-margin, -margin);
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void ApplyUiInputState()
        {
            if (_inputStateApplied)
                return;

            SetPauseMenuBlocked(true);
            _previousCursorVisible = Cursor.visible;
            _previousCursorLockMode = Cursor.lockState;
            _previousTimeScale = Time.timeScale;
            _pauseGameplayWhileUiOpen = ShouldPauseGameplayForUi();

            // Always enter menu mode while panel is open so look/move gameplay input is captured by UI.
            TrySetMenuMode(true);

            ForceCursorForMenu();

            if (_pauseGameplayWhileUiOpen)
                Time.timeScale = 0f;
            else if (Time.timeScale != _previousTimeScale)
                Time.timeScale = _previousTimeScale;

            _inputStateApplied = true;
        }

        private void EnforceUiInputState()
        {
            TrySetMenuMode(true);

            ForceCursorForMenu();

            if (_pauseGameplayWhileUiOpen && Time.timeScale != 0f)
                Time.timeScale = 0f;
            else if (!_pauseGameplayWhileUiOpen && Time.timeScale != _previousTimeScale)
                Time.timeScale = _previousTimeScale;
        }

        private void RestoreUiInputState()
        {
            SetPauseMenuBlocked(false);

            if (!_inputStateApplied)
            {
                _pauseGameplayWhileUiOpen = false;
                HideOverlayIfPending();
                return;
            }

            TrySetMenuMode(false);

            if (_pauseGameplayWhileUiOpen)
            {
                Time.timeScale = _previousTimeScale;
            }

            Cursor.visible = _previousCursorVisible;
            Cursor.lockState = _previousCursorLockMode;

            Input.ResetInputAxes();
            _pauseGameplayWhileUiOpen = false;
            _inputStateApplied = false;
            _postRestoreInputFlushFrames = PostRestoreInputFlushFrames;
            HideOverlayIfPending();
        }

        private void BeginPointerFinalizeRestore()
        {
            if (!_inputStateApplied)
            {
                RestoreUiInputState();
                return;
            }

            _pendingPointerFinalizeRestore = true;
            _pointerFinalizeFramesRemaining = PointerFinalizeStabilizeFrames;
            Input.ResetInputAxes();
        }

        private void HideOverlayIfPending()
        {
            if (!_pendingOverlayHide)
                return;

            _pendingOverlayHide = false;

            if (_overlay != null)
                _overlay.SetActive(false);
        }

        private void SetPauseMenuBlocked(bool blocked)
        {
            if (!TryResolvePauseBlockMethod())
                return;

            if (_pauseBlockStateKnown && _pauseBlockState == blocked)
                return;

            try
            {
                object instance = null;
                if (!_setBlockPauseMenuMethod.IsStatic)
                {
                    var declaringType = _setBlockPauseMenuMethod.DeclaringType;
                    if (declaringType == null)
                        return;

                    instance = FindBehaviourInstance(declaringType);
                    if (instance == null)
                        return;
                }

                var parameters = _setBlockPauseMenuMethod.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
                    return;

                _setBlockPauseMenuMethod.Invoke(instance, new object[] { blocked });

                _pauseBlockStateKnown = true;
                _pauseBlockState = blocked;
            }
            catch (Exception ex)
            {
                _pauseBlockStateKnown = false;
                ModMain.LogMessage("FastTravel: SetBlockPauseMenu call failed: " + ex.Message);
            }
        }

        private static bool TryResolvePauseBlockMethod()
        {
            if (_pauseBlockResolverInitialized)
                return _setBlockPauseMenuMethod != null;

            _pauseBlockResolverInitialized = true;

            try
            {
                var pauseMenuType = FindTypeByName("Sons.Gui.PauseMenu");
                if (pauseMenuType == null)
                {
                    ModMain.LogMessage("FastTravel: Could not resolve Sons.Gui.PauseMenu type for SetBlockPauseMenu.");
                    return false;
                }

                var methods = pauseMenuType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, "SetBlockPauseMenu", StringComparison.Ordinal))
                        continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                    {
                        _setBlockPauseMenuMethod = method;
                        break;
                    }
                }

                if (_setBlockPauseMenuMethod == null)
                {
                    ModMain.LogMessage("FastTravel: SetBlockPauseMenu method not found on Sons.Gui.PauseMenu.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ModMain.LogMessage("FastTravel: Pause block resolver failed: " + ex.Message);
                return false;
            }

            return true;
        }

        private static Type FindTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            var direct = Type.GetType(fullName + ", Sons.Gui");
            if (direct != null)
                return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                if (assembly == null)
                    continue;

                Type resolved;
                try
                {
                    resolved = assembly.GetType(fullName, false);
                }
                catch
                {
                    continue;
                }

                if (resolved != null)
                    return resolved;
            }

            return null;
        }

        private static object FindBehaviourInstance(Type declaringType)
        {
            if (declaringType == null)
                return null;

            MonoBehaviour[] behaviours;
            try
            {
                behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            }
            catch
            {
                return null;
            }

            if (behaviours == null)
                return null;

            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;

                Type behaviourType;
                try
                {
                    behaviourType = behaviour.GetType();
                }
                catch
                {
                    continue;
                }

                if (behaviourType == null)
                    continue;

                if (declaringType.IsAssignableFrom(behaviourType))
                    return behaviour;
            }

            return null;
        }

        private static bool IsAnyMouseButtonHeld()
        {
            return Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        }

        private static void ForceCursorForMenu()
        {
            if (!Cursor.visible)
                Cursor.visible = true;

            if (Cursor.lockState != CursorLockMode.None)
                Cursor.lockState = CursorLockMode.None;
        }

        private static void TrySetMenuMode(bool enabled)
        {
            try
            {
                SonsTools.MenuMode(enabled);
            }
            catch (Exception ex)
            {
                if (!_menuModeFailureLogged)
                {
                    _menuModeFailureLogged = true;
                    ModMain.LogMessage("FastTravel: SonsTools.MenuMode failed: " + ex.Message);
                }
            }
        }

        private static bool ShouldPauseGameplayForUi()
        {
            if (FastTravelNetworkingRuntime.TryGetRuntimeNetworkState(
                out bool isMultiplayer,
                out bool isServer,
                out bool isClient,
                out bool isDedicatedServer))
            {
                if (isDedicatedServer)
                    return false;

                if (isMultiplayer || isServer || isClient)
                    return false;
            }

            return true;
        }

    }
}
