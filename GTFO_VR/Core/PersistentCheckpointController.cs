using CellMenu;
using GTFO.PersistentCheckpoint.Persistence;
using TMPro;
using UnityEngine;

namespace GTFO.PersistentCheckpoint.Core;

internal sealed class PersistentCheckpointController : MonoBehaviour
{
    private static PersistentCheckpointController? _instance;
    private CM_PageLoadout? _queuedPage;
    private int _uiDelayFrames;
    private CM_TimedButton? _resumeButton;
    private CM_TimedButton? _createButton;

    public PersistentCheckpointController(IntPtr pointer) : base(pointer)
    {
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        UpdateQueuedUi();
        ResumeCoordinator.ObserveCurrentSlots();
        UpdateCheckpointButtons();
        ResumeCoordinator.TryRecallPending();
        ResumeCoordinator.UpdatePlayerResync();
        ResumeCoordinator.UpdateBridgeRecall();
    }

    internal static void QueueLoadoutUi(CM_PageLoadout page)
    {
        if (_instance == null)
            return;

        _instance._queuedPage = page;
        _instance._uiDelayFrames = 2;
    }

    private void UpdateQueuedUi()
    {
        if (_queuedPage == null || --_uiDelayFrames > 0)
            return;

        var page = _queuedPage;
        _queuedPage = null;

        try
        {
            _resumeButton = GetOrCreateButton(page, "PersistentCheckpointResumeButton",
                eTimedButtonClickType.Instant, _ => ResumeCoordinator.ToggleResume());
            _createButton = GetOrCreateButton(page, "PersistentCheckpointCreateButton",
                eTimedButtonClickType.LongHold, _ => ResumeCoordinator.CreateManualCheckpoint());
            _createButton.SetText("CREATE CHECKPOINT");
            UpdateCheckpointButtons();
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create checkpoint UI: {exception}");
        }
    }

    private static CM_TimedButton GetOrCreateButton(CM_PageLoadout page, string name,
        eTimedButtonClickType clickType, Action<int> onPress)
    {
        var existing = page.GetComponentsInChildren<CM_TimedButton>(includeInactive: true)
            .FirstOrDefault(item => item.gameObject.name == name);
        if (existing != null)
        {
            existing.OnBtnPressCallback = onPress;
            return existing;
        }

        var buttonObject = Instantiate(page.m_readyButtonPrefab, page.m_readyButtonAlign);
        buttonObject.name = name;
        buttonObject.transform.localPosition = new Vector3(430f, 100f, 0f);
        buttonObject.transform.localScale = new Vector3(0.75f, 0.75f, 1f);

        var button = buttonObject.GetComponent<CM_TimedButton>();
        button.TextMeshRoot = page.transform;
        button.m_clickType = clickType;
        button.SetSize(new Vector2(520f, 80f));
        button.Setup();
        // Instantiate can run Awake/Setup before m_clickType is assigned. Rebuild
        // the native hold duration explicitly; Setup alone skips initialized items.
        button.SetupCMItem();
        ShiftButtonTextRight(button, 40f);
        button.OnBtnPressCallback = onPress;
        button.SetVisible(false);
        return button;
    }

    private static void ShiftButtonTextRight(CM_TimedButton button, float offset)
    {
        foreach (var text in button.GetComponentsInChildren<TextMeshPro>(includeInactive: true))
            text.transform.localPosition += new Vector3(offset, 0f, 0f);
    }

    private void UpdateCheckpointButtons()
    {
        if (_resumeButton != null)
        {
            var visible = SNetwork.SNet.IsMaster
                && GameStateManager.CurrentStateName == eGameStateName.Lobby
                && ResumeCoordinator.ShouldShowControl;
            if (_resumeButton.IsVisible != visible)
                _resumeButton.SetVisible(visible);
            if (visible)
            {
                _resumeButton.SetButtonEnabled(ResumeCoordinator.HasResumeChoice);
                var text = ResumeCoordinator.GetButtonText();
                if (_resumeButton.m_btnText.text != text)
                    _resumeButton.SetText(text);
            }
        }

        if (_createButton != null)
        {
            var visible = ResumeCoordinator.ShouldShowCreateControl;
            // SetVisible clears the hold-progress text, so only call it when the
            // visibility actually changes, not every frame during a long press.
            if (_createButton.IsVisible != visible)
                _createButton.SetVisible(visible);
            if (visible)
                _createButton.SetButtonEnabled(ResumeCoordinator.CanCreateManualCheckpoint);
        }
    }

    private void OnApplicationQuit() => CheckpointStore.Flush(TimeSpan.FromSeconds(3));
}
