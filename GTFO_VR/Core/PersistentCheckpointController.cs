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
        UpdateResumeButton();
        ResumeCoordinator.TryRecallPending();
        ResumeCoordinator.UpdatePlayerResync();
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
            var existing = page.GetComponentsInChildren<CM_TimedButton>(includeInactive: true)
                .FirstOrDefault(item => item.gameObject.name == "PersistentCheckpointResumeButton");
            if (existing != null)
            {
                _resumeButton = existing;
                return;
            }

            var buttonObject = Instantiate(page.m_readyButtonPrefab, page.m_readyButtonAlign);
            buttonObject.name = "PersistentCheckpointResumeButton";
            buttonObject.transform.localPosition = new Vector3(430f, 100f, 0f);
            buttonObject.transform.localScale = new Vector3(0.75f, 0.75f, 1f);
            buttonObject.SetActive(true);

            var button = buttonObject.GetComponent<CM_TimedButton>();
            button.TextMeshRoot = page.transform;
            button.m_clickType = eTimedButtonClickType.Instant;
            button.SetSize(new Vector2(520f, 80f));
            button.Setup();
            ShiftButtonTextRight(button, 40f);
            button.OnBtnPressCallback = new Action<int>(_ => ResumeCoordinator.ToggleResume());
            _resumeButton = button;
            UpdateResumeButton();
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create resume checkpoint UI: {exception}");
        }
    }

    private static void ShiftButtonTextRight(CM_TimedButton button, float offset)
    {
        foreach (var text in button.GetComponentsInChildren<TextMeshPro>(includeInactive: true))
            text.transform.localPosition += new Vector3(offset, 0f, 0f);
    }

    private void UpdateResumeButton()
    {
        if (_resumeButton == null)
            return;

        var visible = SNetwork.SNet.IsMaster && ResumeCoordinator.ShouldShowControl;
        _resumeButton.SetVisible(visible);
        if (visible)
        {
            _resumeButton.SetButtonEnabled(ResumeCoordinator.HasResumeChoice);
            _resumeButton.SetText(ResumeCoordinator.GetButtonText());
        }
    }

    private void OnApplicationQuit() => CheckpointStore.Flush(TimeSpan.FromSeconds(3));
}
