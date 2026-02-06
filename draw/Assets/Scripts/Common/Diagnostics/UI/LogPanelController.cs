using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Common.Diagnostics.UI
{
#if DEVELOPMENT_BUILD || UNITY_EDITOR
    public class LogPanelController : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int _maxVisibleLogs = 300;
        [SerializeField] private KeyCode _toggleKey = KeyCode.BackQuote;

        private ConcurrentQueue<LogData> _incomingLogs = new ConcurrentQueue<LogData>();
        private List<LogData> _allLogs = new List<LogData>(2000);
        private List<LogData> _filteredLogs = new List<LogData>(500);
        private List<LogEntryItem> _spawnedItems = new List<LogEntryItem>();
        private Queue<LogEntryItem> _itemPool = new Queue<LogEntryItem>();

        private bool _showInfo = true;
        private bool _showWarn = true;
        private bool _showError = true;
        private string _searchFilter = "";

        private bool _isExpanded = false;
        private LogData? _selectedLog;

        // UI References
        private GameObject _canvasObj;
        private GameObject _panelRoot;
        private GameObject _openButtonObj;
        private RectTransform _contentRect;
        private TMP_InputField _detailsInputField;
        private GameObject _detailsPanel;
        private TMP_Text _statsText;

        // Resources
        private Font _defaultFont; // Fallback
        private TMP_FontAsset _tmpFont;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitialize()
        {
            if (Application.platform == RuntimePlatform.IPhonePlayer && !Debug.isDebugBuild)
            {
                return;
            }
            if (FindObjectOfType<LogPanelController>() == null)
            {
                var go = new GameObject("LogPanelSystem");
                go.AddComponent<LogPanelController>();
            }
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _tmpFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            
            // If TMP font missing, try to find any
            if (_tmpFont == null)
            {
                _tmpFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            }

            BuildUI();
        }

        private void OnEnable()
        {
            Application.logMessageReceivedThreaded += HandleLog;
        }

        private void OnDisable()
        {
            Application.logMessageReceivedThreaded -= HandleLog;
        }

        private void HandleLog(string logString, string stackTrace, LogType type)
        {
            LogLevel level = LogLevel.Info;
            switch (type)
            {
                case LogType.Warning: level = LogLevel.Warn; break;
                case LogType.Error: 
                case LogType.Exception: 
                case LogType.Assert: 
                    level = LogLevel.Error; break;
            }

            _incomingLogs.Enqueue(new LogData
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = logString,
                StackTrace = stackTrace
            });
        }

        private void Update()
        {
            // Process Queue
            bool needsRefresh = false;
            while (_incomingLogs.TryDequeue(out var log))
            {
                _allLogs.Add(log);
                if (IsLogVisible(log))
                {
                    _filteredLogs.Add(log);
                    needsRefresh = true;
                }
            }

            if (needsRefresh)
            {
                RefreshList();
            }

            if (Input.GetKeyDown(_toggleKey))
            {
                TogglePanel();
            }
        }

        private bool IsLogVisible(LogData log)
        {
            if (!string.IsNullOrEmpty(_searchFilter) && !log.Message.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            switch (log.Level)
            {
                case LogLevel.Info: return _showInfo;
                case LogLevel.Warn: return _showWarn;
                case LogLevel.Error: return _showError;
                default: return true;
            }
        }

        private void RefreshList()
        {
            // Return existing items to pool
            foreach (var item in _spawnedItems)
            {
                item.gameObject.SetActive(false);
                _itemPool.Enqueue(item);
            }
            _spawnedItems.Clear();

            // Determine start index to show last N logs
            int count = _filteredLogs.Count;
            int startIndex = Mathf.Max(0, count - _maxVisibleLogs);

            for (int i = startIndex; i < count; i++)
            {
                var log = _filteredLogs[i];
                var item = GetItem();
                item.Setup(log, _selectedLog.HasValue && log.Equals(_selectedLog.Value), OnLogItemClicked);
                item.transform.SetAsLastSibling();
                _spawnedItems.Add(item);
            }

            // Auto scroll to bottom if at bottom?
            // For now, simple set as last sibling works with layout group.
            
            UpdateStats();
        }

        private void UpdateStats()
        {
            if (_statsText != null)
                _statsText.text = $"{_filteredLogs.Count} / {_allLogs.Count}";
        }

        private LogEntryItem GetItem()
        {
            if (_itemPool.Count > 0)
            {
                var item = _itemPool.Dequeue();
                item.gameObject.SetActive(true);
                return item;
            }

            // Create new item
            GameObject itemObj = new GameObject("LogItem");
            itemObj.transform.SetParent(_contentRect, false);
            
            var le = itemObj.AddComponent<LayoutElement>();
            le.minHeight = 44; // Touch friendly
            le.flexibleHeight = 0;

            var bg = itemObj.AddComponent<Image>();
            bg.color = new Color(0,0,0,0); // Transparent by default

            // Text
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(itemObj.transform, false);
            var rect = textObj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10, 0);
            rect.offsetMax = new Vector2(-10, 0);

            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            if (_tmpFont) tmp.font = _tmpFont;
            tmp.fontSize = 24;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;

            var entry = itemObj.AddComponent<LogEntryItem>();
            entry.Initialize(tmp, bg);

            return entry;
        }

        private void OnLogItemClicked(LogData log, bool isDoubleClick)
        {
            _selectedLog = log;
            // Refresh visuals to show selection
            foreach(var item in _spawnedItems)
            {
                // Re-setup to update selection color
                // Ideally LogEntryItem would have SetSelected(bool) but this is quick
                // Note: Setup takes onClick, need to pass it again
                // Optimization: Just check data equality inside item? No, item doesn't know about other items.
                // We'll just RefreshList() or iterate and update color.
                // Iterating is better.
            }
            RefreshList(); // Laziest way, maybe slow. But fine for now.

            if (isDoubleClick)
            {
                ShowDetails(log);
            }
        }

        private void ShowDetails(LogData log)
        {
            if (_detailsPanel != null)
            {
                _detailsPanel.SetActive(true);
                if (_detailsInputField != null)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"[{log.Timestamp:O}] {log.Level}");
                    sb.AppendLine(log.Message);
                    if (!string.IsNullOrEmpty(log.StackTrace))
                    {
                        sb.AppendLine();
                        sb.AppendLine("Stack Trace:");
                        sb.AppendLine(log.StackTrace);
                    }
                    _detailsInputField.text = sb.ToString();
                }
            }
        }

        private void TogglePanel()
        {
            _isExpanded = !_isExpanded;
            _panelRoot.SetActive(_isExpanded);
            _openButtonObj.SetActive(!_isExpanded);
            
            if (_isExpanded)
            {
                RefreshList();
            }
        }

        // --- UI Construction ---

        private void BuildUI()
        {
            // Ensure EventSystem
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystem.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                DontDestroyOnLoad(eventSystem);
            }

            // Canvas
            _canvasObj = new GameObject("DiagnosticsCanvas");
            DontDestroyOnLoad(_canvasObj);
            var canvas = _canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            var scaler = _canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _canvasObj.AddComponent<GraphicRaycaster>();
            canvas.pixelPerfect = false;

            foreach (var c in FindObjectsOfType<Canvas>())
            {
                if (c.pixelPerfect)
                {
                    c.pixelPerfect = false;
                }
            }

            // Open Button
            // Try to find TopBar and Undo button to unify UI
            var topBar = GameObject.Find("TopBar");
            var btnUndo = GameObject.Find("Btn_Undo");
            
            _openButtonObj = CreateButton("OpenLogsBtn", _canvasObj.transform, "LOG", () => TogglePanel());
            
            // Apply Undo Button Style if available
            if (btnUndo != null)
            {
                var undoImg = btnUndo.GetComponent<Image>();
                var undoTxt = btnUndo.GetComponentInChildren<TextMeshProUGUI>();
                var undoRect = btnUndo.GetComponent<RectTransform>();

                var myImg = _openButtonObj.GetComponent<Image>();
                var myTxt = _openButtonObj.GetComponentInChildren<TextMeshProUGUI>();
                var myRect = _openButtonObj.GetComponent<RectTransform>();

                if (undoImg != null)
                {
                    myImg.sprite = undoImg.sprite;
                    myImg.type = undoImg.type;
                    myImg.color = undoImg.color;
                }
                
                if (undoTxt != null)
                {
                    myTxt.font = undoTxt.font;
                    myTxt.fontSize = undoTxt.fontSize;
                    myTxt.fontStyle = undoTxt.fontStyle;
                    myTxt.color = undoTxt.color;
                    myTxt.alignment = undoTxt.alignment;
                }

                if (undoRect != null)
                {
                    myRect.sizeDelta = undoRect.sizeDelta;
                }
            }
            else
            {
                var img = _openButtonObj.GetComponent<Image>();
                img.color = new Color(0.9f, 0.9f, 0.95f);
                
                var txt = _openButtonObj.GetComponentInChildren<TextMeshProUGUI>();
                txt.color = new Color(0.3f, 0.3f, 0.4f);
                txt.fontSize = 28;
                txt.fontStyle = FontStyles.Bold;
                
                var rt = _openButtonObj.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(140, 70);
            }

            if (topBar != null)
            {
                _openButtonObj.transform.SetParent(topBar.transform, false);
                _openButtonObj.transform.SetAsFirstSibling();
            }
            else
            {
                var rt = _openButtonObj.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(0, 1);
                // Move down to avoid iOS Status Bar / Dynamic Island (approx 50-60px)
                rt.anchoredPosition = new Vector2(10, -60);
            }

            // Panel Root
            _panelRoot = new GameObject("LogPanel");
            _panelRoot.transform.SetParent(_canvasObj.transform, false);
            var panelRect = _panelRoot.AddComponent<RectTransform>();
            // Occupy Top 60% of screen to allow drawing in the bottom area
            panelRect.anchorMin = new Vector2(0, 0.4f); 
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
            
            var panelImg = _panelRoot.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f); // Slightly more transparent
            
            // Add a dummy Button component to mark this area as "Interactive/Selectable"
            // This ensures MouseInputProvider blocks drawing when touching the log panel
            var blocker = _panelRoot.AddComponent<Button>();
            blocker.transition = Selectable.Transition.None;
            var nav = new Navigation();
            nav.mode = Navigation.Mode.None;
            blocker.navigation = nav;
            
            _panelRoot.SetActive(false);

            // Header Container (Vertical Layout for 2 rows)
            var header = new GameObject("Header");
            header.transform.SetParent(_panelRoot.transform, false);
            var headerRect = header.AddComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0, 1);
            headerRect.anchorMax = new Vector2(1, 1);
            headerRect.pivot = new Vector2(0.5f, 1);
            headerRect.anchoredPosition = Vector2.zero;
            headerRect.sizeDelta = new Vector2(0, 160); // Increased height for safe area

            var headerVLG = header.AddComponent<VerticalLayoutGroup>();
            // Top padding 55 to clear safe area
            headerVLG.padding = new RectOffset(10, 10, 55, 5);
            headerVLG.spacing = 5;
            headerVLG.childControlHeight = true;
            headerVLG.childForceExpandHeight = false;

            // Row 1: Title + Stats + Spacer + Close
            var row1 = new GameObject("Row1");
            row1.transform.SetParent(header.transform, false);
            var row1Layout = row1.AddComponent<HorizontalLayoutGroup>();
            row1Layout.spacing = 10;
            row1Layout.childControlWidth = true; // Enable width control for Spacer to work
            row1Layout.childForceExpandWidth = false;
            
            CreateText("Title", row1.transform, "System Logs", 32, Color.white);
            _statsText = CreateText("Stats", row1.transform, "0/0", 24, new Color(0.7f, 0.7f, 0.7f)).GetComponent<TextMeshProUGUI>();
            
            var spacer1 = new GameObject("Spacer");
            spacer1.transform.SetParent(row1.transform, false);
            var le1 = spacer1.AddComponent<LayoutElement>();
            le1.flexibleWidth = 1;

            CreateButton("Close", row1.transform, "X", () => TogglePanel(), 50, 44);

            // Row 2: Filters + Spacer + Actions
            var row2 = new GameObject("Row2");
            row2.transform.SetParent(header.transform, false);
            var row2Layout = row2.AddComponent<HorizontalLayoutGroup>();
            row2Layout.spacing = 15; // More spacing for touch
            row2Layout.childControlWidth = true; // Enable width control for Spacer to work
            row2Layout.childForceExpandWidth = false;

            // Filters
            CreateToggle("I", row2.transform, true, (v) => { _showInfo = v; RefreshList(); });
            CreateToggle("W", row2.transform, true, (v) => { _showWarn = v; RefreshList(); });
            CreateToggle("E", row2.transform, true, (v) => { _showError = v; RefreshList(); });

            var spacer2 = new GameObject("Spacer");
            spacer2.transform.SetParent(row2.transform, false);
            var le2 = spacer2.AddComponent<LayoutElement>();
            le2.flexibleWidth = 1;

            // Actions (Icons would be better, but text for now)
            CreateButton("Clear", row2.transform, "Clear", () => { 
                _allLogs.Clear(); 
                _filteredLogs.Clear(); 
                RefreshList(); 
            });

            CreateButton("Copy", row2.transform, "Copy", () => {
                StringBuilder sb = new StringBuilder();
                foreach(var l in _filteredLogs) sb.AppendLine($"[{l.Timestamp:O}] {l.Level}: {l.Message}");
                GUIUtility.systemCopyBuffer = sb.ToString();
            });

            CreateButton("Exp", row2.transform, "Export", ExportLogs);

            // Scroll View
            var scrollObj = new GameObject("Scroll View");
            scrollObj.transform.SetParent(_panelRoot.transform, false);
            var scrollRect = scrollObj.AddComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0, 0);
            scrollRect.anchorMax = new Vector2(1, 1);
            scrollRect.offsetMin = new Vector2(10, 10);
            scrollRect.offsetMax = new Vector2(-10, -170); // Below header (160 + padding)

            var scroll = scrollObj.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 25;
            scroll.inertia = true;

            // Viewport
            var viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollObj.transform, false);
            var vpRect = viewport.AddComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.pivot = Vector2.up;
            vpRect.sizeDelta = Vector2.zero;
            
            var mask = viewport.AddComponent<RectMask2D>();
            var vpImg = viewport.AddComponent<Image>();
            vpImg.color = new Color(0,0,0,0);

            scroll.viewport = vpRect;

            // Content
            var content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            _contentRect = content.AddComponent<RectTransform>();
            _contentRect.anchorMin = new Vector2(0, 1);
            _contentRect.anchorMax = new Vector2(1, 1);
            _contentRect.pivot = new Vector2(0.5f, 1);
            _contentRect.sizeDelta = new Vector2(0, 0);

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(5, 5, 5, 5);
            vlg.spacing = 2;
            vlg.childControlHeight = false;
            vlg.childForceExpandHeight = false;
            
            var csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = _contentRect;

            // Details Panel (Overlay)
            _detailsPanel = new GameObject("DetailsPanel");
            _detailsPanel.transform.SetParent(_panelRoot.transform, false);
            var dpRect = _detailsPanel.AddComponent<RectTransform>();
            dpRect.anchorMin = new Vector2(0.1f, 0.1f);
            dpRect.anchorMax = new Vector2(0.9f, 0.9f);
            
            var dpImg = _detailsPanel.AddComponent<Image>();
            dpImg.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            
            _detailsPanel.AddComponent<Shadow>().effectDistance = new Vector2(5, -5);

            // Details InputField
            var inputObj = new GameObject("DetailsInput");
            inputObj.transform.SetParent(_detailsPanel.transform, false);
            var inputRect = inputObj.AddComponent<RectTransform>();
            inputRect.anchorMin = Vector2.zero;
            inputRect.anchorMax = Vector2.one;
            inputRect.offsetMin = new Vector2(10, 50); // Space for buttons
            inputRect.offsetMax = new Vector2(-10, -10);

            _detailsInputField = inputObj.AddComponent<TMP_InputField>();
            _detailsInputField.readOnly = true;
            _detailsInputField.lineType = TMP_InputField.LineType.MultiLineNewline;
            
            var textArea = new GameObject("TextArea");
            textArea.transform.SetParent(inputObj.transform, false);
            // Setup TextArea boilerplate... (Skipping full details for brevity, assuming minimal setup works)
            // Actually TMP_InputField needs a TextViewport and Text Component.
            // Simplified: Just use a child TextMeshProUGUI and assign it.
            var textViewport = new GameObject("Text Area");
            textViewport.transform.SetParent(inputObj.transform, false);
            var tvRect = textViewport.AddComponent<RectTransform>();
            tvRect.anchorMin = Vector2.zero;
            tvRect.anchorMax = Vector2.one;
            var tvMask = textViewport.AddComponent<RectMask2D>();

            var textCompObj = new GameObject("Text");
            textCompObj.transform.SetParent(textViewport.transform, false);
            var textCompRect = textCompObj.AddComponent<RectTransform>();
            textCompRect.anchorMin = Vector2.zero;
            textCompRect.anchorMax = Vector2.one;
            
            var textComp = textCompObj.AddComponent<TextMeshProUGUI>();
            if (_tmpFont) textComp.font = _tmpFont;
            textComp.fontSize = 24;
            
            _detailsInputField.textViewport = tvRect;
            _detailsInputField.textComponent = textComp;

            // Details Close Button
            var closeDetails = CreateButton("CloseDetails", _detailsPanel.transform, "Close", () => _detailsPanel.SetActive(false));
            var cdRect = closeDetails.GetComponent<RectTransform>();
            cdRect.anchorMin = new Vector2(0.5f, 0);
            cdRect.anchorMax = new Vector2(0.5f, 0);
            cdRect.anchoredPosition = new Vector2(0, 25);
            cdRect.sizeDelta = new Vector2(100, 40);

            _detailsPanel.SetActive(false);
        }

        private GameObject CreateButton(string name, Transform parent, string label, UnityEngine.Events.UnityAction onClick, float width = 100, float height = 44)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.25f, 0.25f, 0.3f); // Dark Blue-Gray
            img.raycastTarget = true;
            
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(onClick);
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.35f, 0.35f, 0.4f);
            colors.pressedColor = new Color(0.45f, 0.45f, 0.5f);
            btn.colors = colors;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(width, height);

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(go.transform, false);
            var rect = textObj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            if (_tmpFont) tmp.font = _tmpFont;
            tmp.text = label;
            tmp.fontSize = 24; // Readable size
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;

            var le = go.AddComponent<LayoutElement>();
            le.minWidth = width;
            le.minHeight = height; // Touch target
            le.preferredWidth = width;
            le.preferredHeight = height;

            return go;
        }

        private GameObject CreateText(string name, Transform parent, string content, float fontSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_tmpFont) tmp.font = _tmpFont;
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            return go;
        }

        private void CreateToggle(string label, Transform parent, bool isOn, UnityEngine.Events.UnityAction<bool> onValueChanged)
        {
            var go = new GameObject("Toggle_" + label);
            go.transform.SetParent(parent, false);
            
            var toggle = go.AddComponent<Toggle>();
            toggle.isOn = isOn;
            toggle.onValueChanged.AddListener(onValueChanged);

            var toggleLayout = go.AddComponent<LayoutElement>();
            toggleLayout.minWidth = 80;
            toggleLayout.minHeight = 44;
            toggleLayout.preferredWidth = 80;
            toggleLayout.preferredHeight = 44;

            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.childControlWidth = true;
            hlg.childForceExpandWidth = false;

            // Checkbox Container
            var bg = new GameObject("Background");
            bg.transform.SetParent(go.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.2f, 0.2f, 0.2f); // Dark background
            bgImg.raycastTarget = true;
            
            var bgLe = bg.AddComponent<LayoutElement>();
            bgLe.minWidth = 44; // Touch Target
            bgLe.minHeight = 44;

            var check = new GameObject("Checkmark");
            check.transform.SetParent(bg.transform, false);
            if (check.GetComponent<RectTransform>() == null) check.AddComponent<RectTransform>();
            var checkImg = check.AddComponent<Image>();
            checkImg.color = new Color(0.2f, 0.8f, 0.2f); // Bright Green
            checkImg.raycastTarget = false;
            
            var checkRect = check.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.2f, 0.2f);
            checkRect.anchorMax = new Vector2(0.8f, 0.8f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;
            
            toggle.targetGraphic = bgImg;
            toggle.graphic = checkImg;

            // Label
            var lbl = CreateText("Label", go.transform, label, 24, Color.white);
        }

        private void ExportLogs()
        {
            string path = Path.Combine(Application.persistentDataPath, $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            StringBuilder sb = new StringBuilder();
            foreach(var l in _allLogs) sb.AppendLine($"[{l.Timestamp:O}] {l.Level}: {l.Message}\n{l.StackTrace}\n");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"Logs exported to: {path}");
        }
    }
#endif
}
