#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.EventLogger;
#endregion

/// <summary>
/// 在 VLContainer1 下（TitleBG、RecipeFlowTitle1 之后）动态生成 OperationListItem / PhaseListItem，
/// 并按 PLC 运行时高亮当前步。
/// 标题 Recipe/Batch 由设计器内 RecipeFlowTitle1 的 DynamicLink 绑定。
/// </summary>
public class GenerateBatchRunFlow : BaseNetLogic
{
    private const string LogCategory = "GenerateBatchRunFlow";
    private const string BatchDownloadToPlcDataPath = "Model/UIData/BatchesEditorData/BatchDownloadToPlcData";
    private const string ComponentsFolderPath = "UI/Widgets/Components";
    /// <summary>批次运行流程行：14px 字号，图标与行高与之匹配。</summary>
    private const float FlowFontSize = 14f;
    private const float FlowIconSize = 14f;
    private const float FlowRowHeight = 26f;
    private const float FlowExpandButtonSize = 16f;
    private const float ExpandButtonSlotWidth = 16f;
    private const float FlowPhaseIndent = 32f;
    private const float FlowOperationIndent = 0f;
    private const string FlowFinishedFooterName = "BatchRunFlowFinishedFooter";
    private const string FlowFinishedFooterText = "Current Batch Finished";
    private const float FlowFinishedFooterHeight = 34f;

    /// <summary>与 OperationPanels Panel1 设计器结构一致；步骤行挂在 VLContainer1 下。</summary>
    private const string FlowContentRootPath = "ScrollView1/VLContainerWithBG1/VLContainer1";

    private static readonly string[] ListContainerFallbackPaths =
    {
        FlowContentRootPath,
        "VLContainer1"
    };

    private static readonly HashSet<string> FlowHostProtectedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "TitleBG",
        "RecipeFlowTitle1",
        "GenerateBatchRunFlow"
    };

    // Optix Color 构造函数为 (Alpha, Red, Green, Blue)。#1c5a4c → A=255, R=0x1c, G=0x5a, B=0x4c。
    // 若写成 (0x1c,0x5a,0x4c,0xff) 会被当成 A=28,R=90,G=76,B=255，界面呈淡紫/紫蓝而非绿色。
    /// <summary>当前执行步文字色（亮绿，便于演示区分）。</summary>
    private static readonly Color HighlightTextColor = BatchRunFlowHighlight.RunningTextColor;
    private static readonly Color FlowTextColor = new Color(255, 0x33, 0x33, 0x33);
    private static readonly Color FlowFinishedFooterColor = new Color(255, 0x1c, 0x5a, 0x4c);
    private static readonly Color FaultTextColor = new Color(255, 0xc6, 0x28, 0x28);
    private static readonly Color FlowTransparentBg = new Color(0, 0xe4, 0xe4, 0xe4);
    private static readonly Color FlowBorderNone = new Color(0, 0, 0, 0);

    private enum FlowStepState
    {
        Pending,
        Running,
        Held,
        Idle,
        Complete,
        Fault
    }

    private bool _enableLog;
    private int _refreshPeriodMs = 500;
    private PeriodicTask _refreshTask;
    private IEventRegistration _snapshotReg;
    private uint _observerAffinityId;
    private string _lastBuiltRecipeKey = "";
    private int _lastFlowRefreshTick = -1;
    private int _emptyListRetryCount;
    private string _resolvedListContainerPath = "";

    private readonly Dictionary<int, bool> _opExpanded = new Dictionary<int, bool>();

    private IUANode _batchRoot;
    private IUANode _recipeRoot;
    private IUANode _handshakeRoot;
    private IUAObject _batchInforLogic;
    private IUAVariable _plcBatchRecipeName;
    private IUAVariable _plcCmdSeq;
    private IUAVariable _plcEvtFault;
    private IUAVariable _plcEvtBatchDone;
    private IUAVariable _plcRunning;
    private IUAVariable _plcHeld;
    private IUAVariable _plcIdle;
    private IUAVariable _plcRunningOpIndex;
    private IUAVariable _plcRunningPhaseName;
    private IUAVariable _plcOpName;
    private IUAVariable _plcBatchRunning;
    private IUAVariable _plcOpRunning;

    public static GenerateBatchRunFlow Instance { get; private set; }
    private static GenerateBatchRunFlow _instance;

    private bool _forcedStepActive;
    private int _forcedOpIndex;
    private int _forcedPhaseIndex = -1;
    private bool _forcedRunning;
    private bool _skipTickRegenerate;
    private PeriodicTask _demoStepTimer;
    private bool _demoActive;
    private int _demoStepIndex;
    private int _phaseIntervalMs = 3000;
    private readonly List<FlowDemoStep> _demoSteps = new List<FlowDemoStep>();

    private struct FlowDemoStep
    {
        public int OpIndex;
        public int PhaseIndex;
        public string OpName;
        public string PhaseName;
    }

    private NodeId _operationItemTypeId = NodeId.Empty;
    private NodeId _phaseItemTypeId = NodeId.Empty;

    /// <summary>由 BatchInforToPLC 演示步进调用，不依赖 PLC 索引。</summary>
    public static void NotifyRunStep(int opIndex, int phaseIndex, bool isRunning)
    {
        if (_instance != null)
        {
            _instance._forcedStepActive = true;
            _instance._forcedOpIndex = opIndex;
            _instance._forcedPhaseIndex = phaseIndex;
            _instance._forcedRunning = isRunning;
            _instance._skipTickRegenerate = true;
            _instance.RefreshStatuses();
            return;
        }
        BatchRunFlowHighlight.ApplyOnProject(opIndex, phaseIndex, isRunning);
    }

    /// <summary>由 Start / BatchInforToPLC 演示模式调用。</summary>
    public static void RequestStartDemo()
    {
        if (_instance != null)
            _instance.StartDemoRun();
        else
            TryExecuteStartDemoOnNode();
    }

    private static void TryExecuteStartDemoOnNode()
    {
        var nl = FindNetLogicNode(Project.Current, "GenerateBatchRunFlow");
        if (nl == null)
            return;
        try
        {
            nl.ExecuteMethod("StartDemoRun", Array.Empty<object>());
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"ExecuteMethod StartDemoRun 失败: {ex.Message}");
        }
    }

    private static IUAObject FindNetLogicNode(IUANode root, string browseName)
    {
        if (root == null) return null;
        if (root.BrowseName == browseName && root is IUAObject obj)
            return obj;
        foreach (var ch in root.Children)
        {
            var found = FindNetLogicNode(ch, browseName);
            if (found != null) return found;
        }
        return null;
    }

    public override void Start()
    {
        _instance = this;
        Instance = this;
        try
        {
            var ms = LogicObject.GetVariable("PhaseIntervalMs");
            if (ms?.Value != null)
                _phaseIntervalMs = Math.Max(500, Convert.ToInt32(ms.Value.Value, CultureInfo.InvariantCulture));
        }
        catch { }

        try
        {
            var logVar = LogicObject.GetVariable("EnableLog");
            if (logVar != null) _enableLog = (bool)logVar.Value;
        }
        catch { }

        try
        {
            var periodVar = LogicObject.GetVariable("RefreshPeriodMs");
            if (periodVar != null) _refreshPeriodMs = Math.Max(200, (int)periodVar.Value);
        }
        catch { }

        _observerAffinityId = LogicObject.Context.AssignAffinityId();
        ResolvePlcReferences();
        ResolveTypeIds();
        RegisterSnapshotObserver();
        Generate();
        _refreshTask = new PeriodicTask(RefreshStatuses, _refreshPeriodMs, LogicObject);
        _refreshTask.Start();
    }

    public override void Stop()
    {
        if (_instance == this)
        {
            _instance = null;
            Instance = null;
        }
        StopDemoInternal();
        _refreshTask?.Dispose();
        _refreshTask = null;
        _snapshotReg?.Dispose();
        _snapshotReg = null;
        _opExpanded.Clear();
    }

    [ExportMethod]
    public void Regenerate() => Generate();

    [ExportMethod]
    public void StartDemoRun()
    {
        if (_demoActive)
            return;

        if (!TryBuildDemoSteps(out string err))
        {
            SetBatchInforStatus(string.IsNullOrEmpty(err) ? "演示失败：无步骤" : err);
            Log.Warning(LogCategory, err);
            return;
        }

        ClearFlowFinishedSnapshot();
        _demoStepIndex = 0;
        _demoActive = true;
        ApplyDemoStep(_demoSteps[0]);
        NotifyRunStep(_demoSteps[0].OpIndex, _demoSteps[0].PhaseIndex, true);
        ScheduleDemoStep();
    }

    [ExportMethod]
    public void StopDemoRun() => StopDemoInternal();

    #region 生成列表

    private void Generate()
    {
        var list = GetFlowHost();
        if (list == null)
        {
            Log.Error(LogCategory, $"未找到 VLContainer1（已尝试: {string.Join(", ", GetListContainerSearchPaths())}）");
            return;
        }

        ResolveTypeIds();
        if (_operationItemTypeId == NodeId.Empty || _phaseItemTypeId == NodeId.Empty)
        {
            Log.Error(LogCategory, "未找到 OperationListItem 或 PhaseListItem");
            AddMessageRow(list, GetFlowRowWidth(), "缺少 OperationListItem/PhaseListItem");
            return;
        }

        EnsureRecipeTreeLoaded();

        string recipeName = ResolveActiveRecipeName();
        string batchName = ReadSnapshotBatchName();
        _lastBuiltRecipeKey = FormatRecipeKey(recipeName, batchName);

        float rowWidth = GetFlowRowWidth();
        ApplyFlowHostStretchLayout(list);

        try
        {
            ClearFlowRows(list);
            _opExpanded.Clear();

            if (string.IsNullOrWhiteSpace(recipeName))
            {
                _lastBuiltRecipeKey = "";
                UpdateFinishedFooter(list, false);
                ResizeFlowHost(list);
                return;
            }

            var receipt = FindReceiptForFlow(recipeName);
            if (receipt == null)
            {
                AddMessageRow(list, rowWidth, $"未找到配方: {recipeName}");
                return;
            }
            if (receipt.Operations == null || receipt.Operations.Count == 0)
            {
                AddMessageRow(list, rowWidth, $"配方 [{receipt.Name}] 无 Operation");
                return;
            }

            int rows = 0;
            for (int oi = 0; oi < receipt.Operations.Count; oi++)
            {
                var op = receipt.Operations[oi];
                string opName = op?.Name ?? $"Operation_{oi + 1}";
                int phaseCount = op?.Phases?.Count ?? 0;

                var opItem = InformationModel.MakeObject(SafeName(opName), _operationItemTypeId) as Container;
                if (opItem == null) continue;

                opItem.Width = rowWidth;
                opItem.HorizontalAlignment = HorizontalAlignment.Stretch;
                SetFlowIndices(opItem, oi, -1);
                SetItemButtonText(opItem, opName);
                ApplyCompactFlowRowStyle(opItem, isPhase: false);
                HideExpandButton(opItem);

                if (!_opExpanded.ContainsKey(oi))
                    _opExpanded[oi] = true;

                if (phaseCount > 1)
                    WireOpExpand(opItem, oi);

                list.Add(opItem);
                rows++;

                for (int pi = 0; pi < phaseCount; pi++)
                {
                    var phase = op.Phases[pi];
                    string phaseName = phase?.Name ?? $"Phase_{pi + 1}";

                    var phItem = InformationModel.MakeObject(SafeName(phaseName), _phaseItemTypeId) as Container;
                    if (phItem == null) continue;

                    phItem.Width = rowWidth;
                    phItem.HorizontalAlignment = HorizontalAlignment.Stretch;
                    SetFlowIndices(phItem, oi, pi);
                    SetItemButtonText(phItem, phaseName);
                    ApplyCompactFlowRowStyle(phItem, isPhase: true);
                    HideExpandButton(phItem);
                    phItem.Visible = _opExpanded[oi];
                    list.Add(phItem);
                    rows++;
                }
            }

            if (rows == 0)
                AddMessageRow(list, rowWidth, "未能创建步骤行");
            else
                ResizeFlowHost(list);
        }
        finally
        {
            _emptyListRetryCount = 0;
            RefreshStatuses();
        }

        if (_enableLog)
            Log.Info(LogCategory, $"已生成批次运行步骤: {recipeName}, 容器={_resolvedListContainerPath}, 行数={CountFlowRows(list)}");
    }

    private void RefreshStatuses()
    {
        var list = GetFlowHost();
        if (list == null) return;

        int tick = ReadSnapshotFlowRefreshTick();
        if (tick != _lastFlowRefreshTick)
        {
            _lastFlowRefreshTick = tick;
            if (!_skipTickRegenerate)
                Generate();
            _skipTickRegenerate = false;
        }

        string recipeName = ResolveActiveRecipeName();
        if (string.IsNullOrWhiteSpace(recipeName))
        {
            if (CountFlowRows(list) > 0 || !string.IsNullOrEmpty(_lastBuiltRecipeKey))
                Generate();
            return;
        }

        if (!string.Equals(FormatRecipeKey(recipeName, ReadSnapshotBatchName()), _lastBuiltRecipeKey, StringComparison.Ordinal))
        {
            Generate();
            return;
        }

        if (CountFlowRows(list) == 0 && _emptyListRetryCount < 20)
        {
            _emptyListRetryCount++;
            Generate();
            return;
        }

        var receipt = FindReceiptForFlow(recipeName);
        int opCount = receipt?.Operations?.Count ?? 0;

        TryResolveCurrentStep(receipt, out int runningOp, out int cmdSeq, out bool running, out bool held, out bool idle);
        bool fault = ReadBooleanTag(_plcEvtFault);

        bool finished = IsBatchFlowFinished(receipt);
        foreach (var item in list.Children.OfType<Container>())
        {
            if (!IsFlowRow(item) || !TryGetFlowIndices(item, out int opIndex, out int phaseIndex) || opIndex < 0)
                continue;

            if (finished)
            {
                ApplyItemHighlight(item, FlowStepState.Complete);
                if (phaseIndex >= 0)
                    item.Visible = _opExpanded.TryGetValue(opIndex, out bool exp) && exp;
                continue;
            }

            if (phaseIndex < 0)
            {
                var st = ResolveOperationState(opIndex, runningOp, running, held, idle, fault);
                ApplyItemHighlight(item, st);
            }
            else
            {
                item.Visible = _opExpanded.TryGetValue(opIndex, out bool exp) && exp;
                var st = ResolvePhaseState(opIndex, phaseIndex, runningOp, cmdSeq, opCount, running, held, idle, fault);
                ApplyItemHighlight(item, st);
            }
        }

        UpdateFinishedFooter(list, finished);
    }

    private void WireOpExpand(Container opItem, int opIndex)
    {
        var btn = GetExpandButton(opItem);
        if (btn == null) return;

        btn.Visible = true;
        var ic = GetItemContainer(opItem);
        if (ic != null) ic.LeftMargin = 0;

        SetExpandLook(btn, _opExpanded[opIndex]);
        btn.UAEvent -= OnExpandClick;
        btn.UAEvent += OnExpandClick;

        void OnExpandClick(object sender, UAEventArgs e)
        {
            if (e?.EventType?.BrowseName != "MouseClickEvent") return;
            _opExpanded[opIndex] = !_opExpanded.TryGetValue(opIndex, out bool v) || !v;
            SetExpandLook(btn, _opExpanded[opIndex]);
            var list = GetFlowHost();
            if (list == null) return;
            foreach (var row in list.Children.OfType<Container>())
            {
                if (!IsFlowRow(row) || !TryGetFlowIndices(row, out int oi, out int pi) || pi < 0 || oi != opIndex) continue;
                row.Visible = _opExpanded[opIndex];
            }
        }
    }

    private static void SetExpandLook(Button btn, bool expanded)
    {
        var ip = btn.GetVariable("ImagePath");
        if (ip != null) ip.Value = "ns=7;%PROJECTDIR%/Right-sm.svg";
        btn.Rotation = expanded ? 90f : 0f;
        var rot = btn.GetVariable("Rotation");
        if (rot != null) rot.Value = expanded ? 90f : 0f;
    }

    private static void HideExpandButton(Container item)
    {
        var btn = GetExpandButton(item);
        if (btn == null) return;
        btn.Visible = false;
        var ic = GetItemContainer(item);
        if (ic != null)
            ic.LeftMargin = (ic.LeftMargin > 0 ? ic.LeftMargin : 0) + ExpandButtonSlotWidth;
    }

    private static void ApplyItemHighlight(Container item, FlowStepState st)
    {
        var btn = GetItemButton(item);
        if (btn == null) return;
        switch (st)
        {
            case FlowStepState.Running:
            case FlowStepState.Held:
            case FlowStepState.Idle:
                ApplyFlowButtonLook(btn, FlowTransparentBg, HighlightTextColor);
                break;
            case FlowStepState.Fault:
                ApplyFlowButtonLook(btn, FlowTransparentBg, FaultTextColor);
                break;
            default:
                ApplyFlowButtonLook(btn, FlowTransparentBg, FlowTextColor);
                break;
        }
        ClearFlowRowBorderOnly(btn);
    }

    private static void ApplyFlowButtonLook(Button btn, Color background, Color textColor)
    {
        btn.BackgroundColor = background;
        btn.TextColor = textColor;
        SetNodeColor(btn, "BackgroundColor", background);
        SetNodeColor(btn, "Color", background);
        SetNodeColor(btn, "FillColor", background);
        SetNodeColor(btn, "TextColor", textColor);
    }

    #endregion

    #region 树项 UI（与 GenerateTreeList 相同路径）

    private static Container GetRowHost(Container item) => item?.Get<Container>("Container");
    private static Container GetItemContainer(Container item) => GetRowHost(item)?.Get<Container>("ItemContainer");
    private static Button GetItemButton(Container item) => GetItemContainer(item)?.Get<Button>("ItemButton");
    private static Button GetExpandButton(Container item) => GetRowHost(item)?.Get<Button>("ExpandButton");

    private static void SetItemButtonText(Container item, string text)
    {
        var btn = GetItemButton(item);
        if (btn != null) btn.Text = text ?? "";
    }

    /// <summary>批次运行列表专用紧凑样式，不影响配方编辑器树。</summary>
    private static void ApplyCompactFlowRowStyle(Container item, bool isPhase)
    {
        if (item == null) return;

        item.Height = FlowRowHeight;
        if (!isPhase)
            item.LeftMargin = FlowOperationIndent;

        var host = GetRowHost(item);
        if (host != null)
        {
            host.Height = FlowRowHeight;
            if (isPhase)
                host.LeftMargin = FlowPhaseIndent;
        }

        var ic = GetItemContainer(item);
        if (ic != null)
        {
            ic.Height = FlowRowHeight;
            ic.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        if (host != null)
            host.HorizontalAlignment = HorizontalAlignment.Stretch;

        item.HorizontalAlignment = HorizontalAlignment.Stretch;

        var btn = GetItemButton(item);
        if (btn != null)
        {
            btn.Height = FlowRowHeight - 2f;
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyFlowButtonLook(btn, FlowTransparentBg, FlowTextColor);
            SetNodeSize(btn, "FontSize", FlowFontSize);
            SetNodeSize(btn, "ImageWidth", FlowIconSize);
            SetNodeSize(btn, "ImageHeight", FlowIconSize);
            ClearFlowRowBorder(btn);
        }

        var exp = GetExpandButton(item);
        if (exp != null)
        {
            exp.Width = FlowExpandButtonSize;
            exp.Height = FlowExpandButtonSize;
            exp.BackgroundColor = FlowTransparentBg;
            ClearFlowRowBorder(exp);
        }

        ClearFlowRowBorder(item);
        ClearFlowRowBorder(host);
        ClearFlowRowBorder(ic);
    }

    private static void ApplyFlowHostStretchLayout(IUAObject host)
    {
        if (host == null) return;
        try
        {
            if (host is Container c)
                c.HorizontalAlignment = HorizontalAlignment.Stretch;
            var ha = host.GetVariable("HorizontalAlignment");
            if (ha != null)
                ha.Value = (int)HorizontalAlignment.Stretch;
        }
        catch { }
    }

    private static void ClearFlowRowBorderOnly(IUANode node)
    {
        if (node == null) return;
        SetNodeColor(node, "BorderColor", FlowBorderNone);
        SetNodeSize(node, "BorderThickness", 0f);
    }

    private static void ClearFlowRowBorder(IUANode node)
    {
        ClearFlowRowBorderOnly(node);
        SetNodeColor(node, "FillColor", FlowTransparentBg);
    }

    private static void SetNodeColor(IUANode node, string variableName, Color color)
    {
        if (node == null) return;
        try
        {
            var v = node.GetVariable(variableName);
            if (v != null)
                v.Value = color;
        }
        catch { }
    }

    private static void SetNodeSize(IUANode node, string variableName, float value)
    {
        if (node == null) return;
        try
        {
            var v = node.GetVariable(variableName);
            if (v != null)
                v.Value = value;
        }
        catch { }
    }

    #endregion

    #region 状态推断

    private static FlowStepState ResolveOperationState(int opIndex, int runningOp, bool running, bool held, bool idle, bool fault)
    {
        if (fault && opIndex == runningOp) return FlowStepState.Fault;
        if (opIndex < runningOp) return FlowStepState.Complete;
        if (opIndex > runningOp) return FlowStepState.Pending;
        if (held) return FlowStepState.Held;
        if (running) return FlowStepState.Running;
        if (idle) return FlowStepState.Idle;
        return FlowStepState.Running;
    }

    private static FlowStepState ResolvePhaseState(
        int opIndex, int phaseIndex, int runningOp, int cmdSeq, int opCount,
        bool running, bool held, bool idle, bool fault)
    {
        if (fault && opIndex == runningOp && phaseIndex == cmdSeq) return FlowStepState.Fault;
        if (opIndex < runningOp) return FlowStepState.Complete;
        if (opIndex > runningOp) return FlowStepState.Pending;
        if (phaseIndex < cmdSeq) return FlowStepState.Complete;
        if (phaseIndex > cmdSeq) return FlowStepState.Pending;
        if (held) return FlowStepState.Held;
        if (running) return FlowStepState.Running;
        if (idle) return FlowStepState.Idle;
        return FlowStepState.Running;
    }

    #endregion

    #region UI 容器（VLContainer1 下挂 Operation / Phase）

    /// <summary>步骤列表宿主：设计器中为 VLContainer1（NetLogic 常为其子节点）。</summary>
    private IUAObject GetFlowHost()
    {
        var owner = LogicObject.Owner as IUAObject;
        if (owner != null && string.Equals(owner.BrowseName, "VLContainer1", StringComparison.Ordinal))
        {
            _resolvedListContainerPath = "VLContainer1 (Owner)";
            return owner;
        }

        var panel = FindPanelRoot(owner);
        if (panel == null)
        {
            _resolvedListContainerPath = "";
            return null;
        }

        foreach (var path in GetListContainerSearchPaths())
        {
            var host = panel.GetObject(path) as IUAObject;
            if (host != null)
            {
                _resolvedListContainerPath = path;
                return host;
            }
        }

        _resolvedListContainerPath = "";
        return null;
    }

    private static IUAObject FindPanelRoot(IUANode start)
    {
        var cur = start;
        while (cur != null)
        {
            if (cur is Panel || string.Equals(cur.BrowseName, "Panel1", StringComparison.Ordinal))
                return cur as IUAObject;
            cur = cur.Owner;
        }
        return start as IUAObject;
    }

    private IEnumerable<string> GetListContainerSearchPaths()
    {
        var configured = LogicObject.GetVariable("ListContainerPath")?.Value?.Value as string;
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured.Trim();

        foreach (var p in ListContainerFallbackPaths)
            yield return p;
    }

    private void ClearFlowRows(IUAObject host)
    {
        foreach (var ch in host.Children.OfType<IUANode>().ToList())
        {
            if (!IsFlowRow(ch)) continue;
            ch.Delete();
        }
    }

    private bool IsFlowRow(IUANode node)
    {
        if (node == null || node == LogicObject) return false;
        if (FlowHostProtectedNames.Contains(node.BrowseName)) return false;
        if (node is BaseNetLogic) return false;
        return node is Container c && c.GetVariable("FlowOpIndex") != null;
    }

    private static int CountFlowRows(IUAObject host)
    {
        if (host == null) return 0;
        int n = 0;
        foreach (var ch in host.Children)
        {
            if (ch is BaseNetLogic) continue;
            if (FlowHostProtectedNames.Contains(ch.BrowseName)) continue;
            if (ch is Container c && c.GetVariable("FlowOpIndex") != null)
                n++;
        }
        return n;
    }

    private IUANode GetFlowContentRoot() => GetFlowHost();

    private float GetFlowRowWidth()
    {
        float w = ReadNodeWidth(GetFlowHost());
        if (w > 0)
            return Math.Max(200f, w - 16f);
        return 270f;
    }

    private static float ReadNodeWidth(IUANode node)
    {
        if (node == null) return 0;
        try
        {
            if (node is Container container && container.Width > 0)
                return container.Width;
        }
        catch { }

        try
        {
            var v = node.GetVariable("Width");
            var raw = v?.Value?.Value;
            if (raw == null) return 0;
            if (raw is float f) return f;
            if (raw is double d) return (float)d;
            return Convert.ToSingle(raw, CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    #endregion

    #region 配方 / PLC

    private void EnsureRecipeTreeLoaded()
    {
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) return;
        if (loader.Tree != null && loader.Tree.Count > 0) return;
        try { loader.LoadAllToTree(); }
        catch (Exception ex) { Log.Warning(LogCategory, $"LoadAllToTree: {ex.Message}"); }
    }

    private RecipeDatabaseTreeLoader.ReceiptNode FindReceiptForFlow(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
            return null;

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) return null;

        foreach (var r in loader.Tree ?? Enumerable.Empty<RecipeDatabaseTreeLoader.ReceiptNode>())
            if (string.Equals(r?.Name, recipeName.Trim(), StringComparison.OrdinalIgnoreCase))
                return r;

        int recipeId = ReadSnapshotRecipeId();
        if (recipeId > 0 && loader.ReceiptById != null
            && loader.ReceiptById.TryGetValue(recipeId, out var byId))
            return byId;

        return null;
    }

    /// <summary>仅使用 Download 快照；未 Download 时返回空，不读 Batch Editor 或 PLC 残留。</summary>
    private string ResolveActiveRecipeName()
    {
        string s = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "Recipe");
        return IsMeaningfulName(s) ? s.Trim() : "";
    }

    private static bool IsMeaningfulName(string name)
        => !string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), "None", StringComparison.OrdinalIgnoreCase);

    private static IUAObject GetBatchDownloadToPlcDataNode()
        => Project.Current?.GetObject(BatchDownloadToPlcDataPath) as IUAObject;

    private static string ReadSnapshotBatchName()
    {
        string n = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "Name");
        return IsMeaningfulName(n) ? n.Trim() : "";
    }

    private static int ReadSnapshotRecipeId()
    {
        var v = GetBatchDownloadToPlcDataNode()?.GetVariable("RecipeID");
        if (v?.Value == null) return 0;
        try { return Convert.ToInt32(v.Value.Value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static int ReadSnapshotFlowRefreshTick()
    {
        var v = GetBatchDownloadToPlcDataNode()?.GetVariable("FlowRefreshTick");
        if (v?.Value == null) return 0;
        try { return Convert.ToInt32(v.Value.Value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private void RegisterSnapshotObserver()
    {
        var snapshot = GetBatchDownloadToPlcDataNode();
        if (snapshot == null) return;
        var obs = new CallbackVariableChangeObserver((iv, nv, ov, access, sender) => Generate());
        foreach (string name in new[] { "Recipe", "Name", "FlowRefreshTick" })
        {
            var v = snapshot.GetVariable(name);
            if (v == null) continue;
            try { _snapshotReg = v.RegisterEventObserver(obs, EventType.VariableValueChanged, _observerAffinityId); }
            catch { }
        }
    }

    private void ResolvePlcReferences()
    {
        _batchRoot = ResolveNode("Batch");
        _recipeRoot = ResolveNode("Recipe");
        _handshakeRoot = ResolveNode("OperationHandshake");
        var opRoot = ResolveNode("OP");
        var bid = LogicObject.GetVariable("BatchInforToPLC")?.Value;
        if (bid != null) _batchInforLogic = InformationModel.Get((NodeId)bid.Value) as IUAObject;
        if (_batchInforLogic == null)
            _batchInforLogic = Project.Current?.GetObject("NetLogic/BatchInforToPLC") as IUAObject;

        _plcBatchRecipeName = _batchRoot?.GetVariable("RecipeName");
        _plcRunningPhaseName = _batchRoot?.GetVariable("RunningPhaseName");
        _plcBatchRunning = _batchRoot?.GetVariable("BatchRunning");
        _plcCmdSeq = _handshakeRoot?.GetVariable("CmdSeq");
        _plcEvtFault = _handshakeRoot?.GetVariable("EvtFault");
        _plcEvtBatchDone = _batchRoot?.GetVariable("EvtBatchDone");
        _plcOpName = opRoot?.GetVariable("Name");
        _plcOpRunning = opRoot?.GetVariable("Running");

        var runOp = _recipeRoot?.Get("RunningOperation") as IUANode;
        _plcRunning = runOp?.GetVariable("Running");
        _plcHeld = runOp?.GetVariable("Held");
        _plcIdle = runOp?.GetVariable("Idle");
        _plcRunningOpIndex = _batchInforLogic?.GetVariable("RunningOpIndex");
    }

    /// <summary>解析当前高亮步：演示步 &gt; Model 快照 &gt; 名称匹配 &gt; PLC 索引。</summary>
    private void TryResolveCurrentStep(
        RecipeDatabaseTreeLoader.ReceiptNode receipt,
        out int runningOp,
        out int cmdSeq,
        out bool running,
        out bool held,
        out bool idle)
    {
        runningOp = 0;
        cmdSeq = 0;
        running = false;
        held = ReadBooleanTag(_plcHeld);
        idle = ReadBooleanTag(_plcIdle);

        if (_forcedStepActive)
        {
            runningOp = _forcedOpIndex;
            cmdSeq = Math.Max(0, _forcedPhaseIndex);
            running = _forcedRunning;
            return;
        }

        if (_demoActive && _demoStepIndex >= 0 && _demoStepIndex < _demoSteps.Count)
        {
            var step = _demoSteps[_demoStepIndex];
            runningOp = step.OpIndex;
            cmdSeq = step.PhaseIndex;
            running = true;
            return;
        }

        int snapOp = ReadSnapshotInt("RunningOpIndex", -1);
        int snapPhase = ReadSnapshotInt("RunningPhaseIndex", -1);
        if (snapOp >= 0)
        {
            runningOp = snapOp;
            cmdSeq = Math.Max(0, snapPhase);
        }
        else
        {
            runningOp = ReadRunningOpIndex();
            cmdSeq = ReadCmdSeq();
        }

        string opName = ReadStringTag(_plcOpName);
        if (string.IsNullOrWhiteSpace(opName))
            opName = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "OperationName");
        string phaseName = ReadStringTag(_plcRunningPhaseName);
        if (string.IsNullOrWhiteSpace(phaseName))
            phaseName = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "PhaseName");

        if (TryResolveIndicesByName(receipt, opName, phaseName, out int byOp, out int byPh))
        {
            runningOp = byOp;
            cmdSeq = byPh;
        }

        running = ReadSnapshotBool("FlowIsRunning")
                  || ReadBooleanTag(_plcBatchRunning)
                  || ReadBooleanTag(_plcOpRunning)
                  || ReadBooleanTag(_plcRunning)
                  || IsBatchInforStatusRunning()
                  || !string.IsNullOrWhiteSpace(phaseName);
    }

    private bool IsBatchInforStatusRunning()
    {
        var st = _batchInforLogic?.GetVariable("StatusText");
        string text = ReadStringTag(st);
        return text.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("演示", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsBatchInforStatusFinished()
    {
        var st = _batchInforLogic?.GetVariable("StatusText");
        string text = ReadStringTag(st);
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.IndexOf("Finish", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("完成", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("Completed", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsBatchFlowFinished(RecipeDatabaseTreeLoader.ReceiptNode receipt)
    {
        if (receipt == null || CountFlowRows(GetFlowHost()) == 0)
            return false;
        if (ReadSnapshotBool("FlowBatchFinished"))
            return true;
        if (ReadBooleanTag(_plcEvtBatchDone))
            return true;
        return IsBatchInforStatusFinished();
    }

    private static void ClearFlowFinishedSnapshot()
    {
        var snapshot = GetBatchDownloadToPlcDataNode();
        TrySetBoolean(snapshot?.GetVariable("FlowBatchFinished"), false);
    }

    private void UpdateFinishedFooter(IUAObject host, bool visible)
    {
        if (host == null) return;
        var existing = host.Get<Label>(FlowFinishedFooterName);
        existing?.Delete();
        if (!visible)
        {
            ResizeFlowHost(host);
            return;
        }

        float rowWidth = GetFlowRowWidth();
        var lbl = InformationModel.MakeObject<Label>(FlowFinishedFooterName);
        lbl.Text = FlowFinishedFooterText;
        lbl.Width = rowWidth;
        lbl.Height = FlowFinishedFooterHeight;
        lbl.HorizontalAlignment = HorizontalAlignment.Stretch;
        lbl.TextColor = FlowFinishedFooterColor;
        lbl.FontSize = FlowFontSize;
        lbl.TopMargin = 12f;
        host.Add(lbl);
        ResizeFlowHost(host);
    }

    private static bool TryResolveIndicesByName(
        RecipeDatabaseTreeLoader.ReceiptNode receipt,
        string opName,
        string phaseName,
        out int opIndex,
        out int phaseIndex)
    {
        opIndex = -1;
        phaseIndex = -1;
        if (receipt?.Operations == null || receipt.Operations.Count == 0)
            return false;

        bool hasOp = !string.IsNullOrWhiteSpace(opName);
        bool hasPh = !string.IsNullOrWhiteSpace(phaseName);

        for (int oi = 0; oi < receipt.Operations.Count; oi++)
        {
            var op = receipt.Operations[oi];
            if (hasOp && !NameEquals(op?.Name, opName))
                continue;

            int phaseCount = op?.Phases?.Count ?? 0;
            if (hasPh && phaseCount > 0)
            {
                for (int pi = 0; pi < phaseCount; pi++)
                {
                    if (NameEquals(op.Phases[pi]?.Name, phaseName))
                    {
                        opIndex = oi;
                        phaseIndex = pi;
                        return true;
                    }
                }
            }

            if (hasOp || (!hasOp && !hasPh))
            {
                opIndex = oi;
                phaseIndex = 0;
                return true;
            }
        }

        if (hasPh)
        {
            for (int oi = 0; oi < receipt.Operations.Count; oi++)
            {
                var op = receipt.Operations[oi];
                int phaseCount = op?.Phases?.Count ?? 0;
                for (int pi = 0; pi < phaseCount; pi++)
                {
                    if (NameEquals(op.Phases[pi]?.Name, phaseName))
                    {
                        opIndex = oi;
                        phaseIndex = pi;
                        return true;
                    }
                }
            }
        }

        return opIndex >= 0;
    }

    private static bool NameEquals(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private int ReadSnapshotInt(string name, int fallback)
    {
        var v = GetBatchDownloadToPlcDataNode()?.GetVariable(name);
        if (v?.Value == null) return fallback;
        try
        {
            return Convert.ToInt32(v.Value.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private bool ReadSnapshotBool(string name)
    {
        var v = GetBatchDownloadToPlcDataNode()?.GetVariable(name);
        if (v?.Value == null) return false;
        try
        {
            object raw = v.Value.Value;
            if (raw is bool b) return b;
            if (raw is int i) return i != 0;
            return string.Equals(raw?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #region 演示步进（每 Phase 3 秒，直接驱动高亮）

    private void ScheduleDemoStep()
    {
        _demoStepTimer?.Dispose();
        _demoStepTimer = new PeriodicTask(OnDemoStepTimer, _phaseIntervalMs, LogicObject);
        _demoStepTimer.Start();
    }

    private void OnDemoStepTimer()
    {
        if (!_demoActive)
            return;

        _demoStepIndex++;
        if (_demoStepIndex >= _demoSteps.Count)
        {
            StopDemoInternal();
            PublishFlowSnapshot(-1, -1, "", "", false);
            SetBatchInforStatus("Finish");
            RefreshStatuses();
            return;
        }

        ApplyDemoStep(_demoSteps[_demoStepIndex]);
        NotifyRunStep(_demoSteps[_demoStepIndex].OpIndex, _demoSteps[_demoStepIndex].PhaseIndex, true);
    }

    private void ApplyDemoStep(FlowDemoStep step)
    {
        PublishFlowSnapshot(step.OpIndex, step.PhaseIndex, step.OpName, step.PhaseName, true);
        TrySetInt32(_batchInforLogic?.GetVariable("RunningOpIndex"), step.OpIndex);
        TrySetInt32(_plcCmdSeq, step.PhaseIndex);
        TrySetString(_plcOpName, step.OpName ?? "");
        TrySetString(_plcRunningPhaseName, step.PhaseName ?? "");
        TrySetBoolean(_plcBatchRunning, true);
        TrySetBoolean(_plcOpRunning, true);
        TrySetBoolean(_plcRunning, true);
        TrySetBoolean(_plcHeld, false);
        TrySetBoolean(_plcIdle, false);
        SetBatchInforStatus($"Start [{step.OpIndex + 1}] {step.OpName} / {step.PhaseName}");
    }

    private void StopDemoInternal()
    {
        _demoStepTimer?.Dispose();
        _demoStepTimer = null;
        _demoActive = false;
        _demoSteps.Clear();
        _demoStepIndex = 0;
    }

    private bool TryBuildDemoSteps(out string error)
    {
        error = "";
        _demoSteps.Clear();
        string recipeName = ResolveActiveRecipeName();
        var receipt = FindReceiptForFlow(recipeName);
        if (receipt?.Operations == null || receipt.Operations.Count == 0)
        {
            error = "失败：无配方步骤";
            Log.Warning(LogCategory, error);
            return false;
        }

        for (int oi = 0; oi < receipt.Operations.Count; oi++)
        {
            var op = receipt.Operations[oi];
            string opName = op?.Name ?? $"Operation_{oi + 1}";
            int phaseCount = op?.Phases?.Count ?? 0;
            if (phaseCount == 0)
            {
                _demoSteps.Add(new FlowDemoStep { OpIndex = oi, PhaseIndex = 0, OpName = opName, PhaseName = "" });
                continue;
            }
            for (int pi = 0; pi < phaseCount; pi++)
            {
                string phaseName = op.Phases[pi]?.Name ?? $"Phase_{pi + 1}";
                _demoSteps.Add(new FlowDemoStep { OpIndex = oi, PhaseIndex = pi, OpName = opName, PhaseName = phaseName });
            }
        }

        return _demoSteps.Count > 0;
    }

    private static void PublishFlowSnapshot(int opIndex, int phaseIndex, string opName, string phaseName, bool isRunning)
    {
        var snapshot = GetBatchDownloadToPlcDataNode();
        if (snapshot == null) return;
        TrySetString(snapshot.GetVariable("OperationName"), opName ?? "");
        TrySetString(snapshot.GetVariable("PhaseName"), phaseName ?? "");
        if (opIndex >= 0)
            TrySetInt32(snapshot.GetVariable("RunningOpIndex"), opIndex);
        if (phaseIndex >= 0)
            TrySetInt32(snapshot.GetVariable("RunningPhaseIndex"), phaseIndex);
        TrySetBoolean(snapshot.GetVariable("FlowIsRunning"), isRunning);
        if (!isRunning && opIndex < 0)
        {
            TrySetInt32(snapshot.GetVariable("RunningOpIndex"), -1);
            TrySetInt32(snapshot.GetVariable("RunningPhaseIndex"), -1);
            TrySetBoolean(snapshot.GetVariable("FlowBatchFinished"), true);
        }
        else if (isRunning)
            TrySetBoolean(snapshot.GetVariable("FlowBatchFinished"), false);
        BumpFlowRefreshTick(snapshot);
    }

    private static void BumpFlowRefreshTick(IUAObject snapshot)
    {
        var tickVar = snapshot?.GetVariable("FlowRefreshTick");
        if (tickVar == null) return;
        int tick = 0;
        try
        {
            if (tickVar.Value?.Value != null)
                tick = Convert.ToInt32(tickVar.Value.Value, CultureInfo.InvariantCulture);
        }
        catch { }
        TrySetInt32(tickVar, tick + 1);
    }

    private static void TrySetInt32(IUAVariable v, int value)
    {
        if (v == null) return;
        try { v.Value = value; } catch { }
    }

    private static void TrySetBoolean(IUAVariable v, bool value)
    {
        if (v == null) return;
        try { v.Value = value; } catch { }
    }

    private static void TrySetString(IUAVariable v, string value)
    {
        if (v == null) return;
        try { v.Value = value ?? ""; } catch { }
    }

    private void SetBatchInforStatus(string text)
    {
        var v = _batchInforLogic?.GetVariable("StatusText");
        if (v == null) return;
        try { v.Value = text ?? ""; } catch { }
    }

    #endregion

    private IUANode ResolveNode(string varName)
    {
        var ptr = LogicObject.GetVariable(varName);
        if (ptr?.Value == null) return null;
        try { return InformationModel.Get((NodeId)ptr.Value); }
        catch { return null; }
    }

    private int ReadRunningOpIndex()
    {
        if (_plcRunningOpIndex?.Value == null) return 0;
        try { return Math.Max(0, Convert.ToInt32(_plcRunningOpIndex.Value.Value, CultureInfo.InvariantCulture)); }
        catch { return 0; }
    }

    private int ReadCmdSeq()
    {
        if (_plcCmdSeq?.Value == null) return 0;
        try { return Math.Max(0, Convert.ToInt32(_plcCmdSeq.Value.Value, CultureInfo.InvariantCulture)); }
        catch { return 0; }
    }

    private static bool ReadBooleanTag(IUAVariable v)
    {
        if (v?.Value == null) return false;
        try
        {
            object raw = v.Value.Value;
            if (raw is bool b) return b;
            if (raw is int i) return i != 0;
            return string.Equals(raw?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw?.ToString(), "1", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadStringTag(IUAVariable v)
    {
        if (v?.Value == null) return "";
        object val = v.Value.Value;
        if (val is LocalizedText lt) return lt.Text ?? "";
        return val?.ToString() ?? "";
    }

    private void ResolveTypeIds()
    {
        _operationItemTypeId = FindType(Project.Current, "OperationListItem");
        _phaseItemTypeId = FindType(Project.Current, "PhaseListItem");
        if (_operationItemTypeId == NodeId.Empty)
            _operationItemTypeId = FindType(Project.Current?.GetObject(ComponentsFolderPath), "OperationListItem");
        if (_phaseItemTypeId == NodeId.Empty)
            _phaseItemTypeId = FindType(Project.Current?.GetObject(ComponentsFolderPath), "PhaseListItem");
    }

    private static NodeId FindType(IUANode root, string typeName)
    {
        if (root == null) return NodeId.Empty;
        if (root.BrowseName == typeName && root.NodeClass == NodeClass.ObjectType)
            return root.NodeId;
        foreach (var c in root.Children)
        {
            var id = FindType(c, typeName);
            if (id != NodeId.Empty) return id;
        }
        return NodeId.Empty;
    }

    private static void SetFlowIndices(Container item, int opIndex, int phaseIndex)
    {
        EnsureIntVar(item, "FlowOpIndex", opIndex);
        EnsureIntVar(item, "FlowPhaseIndex", phaseIndex);
    }

    private static bool TryGetFlowIndices(Container item, out int opIndex, out int phaseIndex)
    {
        opIndex = 0;
        phaseIndex = -1;
        try
        {
            var o = item.GetVariable("FlowOpIndex");
            if (o?.Value == null) return false;
            opIndex = Convert.ToInt32(o.Value.Value, CultureInfo.InvariantCulture);
            var p = item.GetVariable("FlowPhaseIndex");
            phaseIndex = p?.Value != null ? Convert.ToInt32(p.Value.Value, CultureInfo.InvariantCulture) : -1;
            return true;
        }
        catch { return false; }
    }

    private static void EnsureIntVar(IUANode owner, string name, int value)
    {
        var v = owner.GetVariable(name);
        if (v == null)
        {
            v = InformationModel.MakeVariable(name, OpcUa.DataTypes.Int32);
            owner.Add(v);
        }
        v.Value = value;
    }

    private void AddMessageRow(IUAObject list, float rowWidth, string msg)
    {
        if (_phaseItemTypeId == NodeId.Empty) return;
        var row = InformationModel.MakeObject("BatchRunFlowMsg", _phaseItemTypeId) as Container;
        if (row == null) return;
        row.Width = rowWidth;
        SetFlowIndices(row, -1, -1);
        SetItemButtonText(row, msg);
        ApplyCompactFlowRowStyle(row, isPhase: true);
        HideExpandButton(row);
        list.Add(row);
    }

    private static void ResizeFlowHost(IUAObject host)
    {
        if (host == null) return;
        float total = 0f;
        foreach (var ch in host.Children)
        {
            if (ch is Container c && c.Visible)
                total += c.Height > 0 ? c.Height : FlowRowHeight;
            else if (ch is Label lb && lb.Visible)
                total += lb.Height > 0 ? lb.Height : FlowFinishedFooterHeight;
        }
        float height = Math.Max(total + 8f, 120f);
        try
        {
            if (host is Container hc)
                hc.Height = height;
            else
            {
                var hv = host.GetVariable("Height");
                if (hv != null) hv.Value = height;
            }
        }
        catch { }
    }

    private static string FormatRecipeKey(string recipe, string batch)
        => (recipe ?? "").Trim() + "|" + (batch ?? "").Trim();

    private static string ReadStringVariable(IUAObject owner, string name)
    {
        var v = owner?.GetVariable(name);
        if (v?.Value == null) return "";
        object val = v.Value.Value;
        if (val is LocalizedText lt) return lt.Text ?? "";
        return val?.ToString() ?? "";
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Item";
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }

    #endregion
}
