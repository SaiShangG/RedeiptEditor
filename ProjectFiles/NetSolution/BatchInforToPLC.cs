#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
#endregion

/// <summary>
/// 通过设计器配置的 <c>Batch</c>（NodeId）解析到 Controller Tags 下的 <c>Batch</c> UDT 根，缓存子标签引用并支持下载到 PLC。
/// </summary>
public class BatchInforToPLC : BaseNetLogic
{
    private const string LogCategory = nameof(BatchInforToPLC);

    /// <summary>下载后批次状态：与 PLC 程序约定一致时可改为其它枚举值。</summary>
    private const int PlcBatchStatusReady = 1;

    /// <summary>PLC <c>BatchID</c> 为 DINT；超出 Int32 时只保留十进制绝对值的末尾这么多位。</summary>
    private const int PlcBatchIdSuffixDigits = 9;

    private IUAVariable _statusText;

    private RedeiptEditor.NetSolution.SimpleStateMachine _sm;
    private RedeiptEditor.NetSolution.SimpleStateMachine.State _stIdle;
    private RedeiptEditor.NetSolution.SimpleStateMachine.State _stDownload;
    private RedeiptEditor.NetSolution.SimpleStateMachine.State _stWriteRun;
    private RedeiptEditor.NetSolution.SimpleStateMachine.State _stWait;
    private RedeiptEditor.NetSolution.SimpleStateMachine.State _stFinish;

    private IUANode _batchRoot;
    private IUAVariable _plcBatchId;
    private IUAVariable _plcBatchStatus;
    private IUAVariable _plcEvtBatchDone;
    private IUAVariable _plcBatchName;
    private IUAVariable _plcRunningPhaseName;
    private IUAVariable _plcBatchStart;
    private IUAVariable _plcBatchRecipeName;

    private IUANode _recipeRoot;
    private IUAVariable _plcRecipeName;
    private IUAVariable _plcRecipeId;
    private IUAVariable _plcRecipeNoOfOperations;

    private IUANode _op1Root;
    private IUAVariable _plcOp1Id;
    private IUAVariable _plcOp1Name;
    private IUAVariable _plcOp1NoOfPhases;

    private IUANode _phasesRoot;

    private IUANode _operationHandshakeRoot;
    private IUAVariable _plcEvtDone;
    private IUAVariable _plcCmdStart;
    private IUAVariable _plcCmdSeq;
    private PeriodicTask _evtDoneTimer;
    private object _lastEvtDoneValue;
    private bool _hasLastEvtDoneValue;

    private RecipeDatabaseTreeLoader.ReceiptNode _flowReceipt;
    private string _flowRecipeName;
    private int _flowOpCount;
    private int _flowCurrentOpIndex;
    private bool _flowActive;

    public override void Start()
    {
        _statusText = LogicObject.GetVariable("StatusText");
        SetStatus("初始化中…");

        InitStateMachine();
        SetStatus("状态机未启动");

        if (!TryEnsureBatchTagReferences())
        {
            Log.Warning(LogCategory, "启动时未解析到 Batch 引用：请检查 NetLogic 上 Batch（NodeId）是否已绑定。");
            SetStatus("未绑定 Batch（NodeId）");
        }
        else
        {
            SetStatus("就绪（状态机未启动）");
        }

        SetupEvtDoneTimer();
    }

    public override void Stop()
    {
        SetStatus("已停止");
        _statusText = null;

        _sm = null;
        _stIdle = null;
        _stDownload = null;
        _stWriteRun = null;
        _stWait = null;
        _stFinish = null;

        _batchRoot = null;
        _plcBatchId = null;
        _plcBatchStatus = null;
        _plcEvtBatchDone = null;
        _plcBatchName = null;
        _plcRunningPhaseName = null;
        _plcBatchStart = null;
        _plcBatchRecipeName = null;

        _recipeRoot = null;
        _plcRecipeName = null;
        _plcRecipeId = null;
        _plcRecipeNoOfOperations = null;

        _op1Root = null;
        _plcOp1Id = null;
        _plcOp1Name = null;
        _plcOp1NoOfPhases = null;

        _phasesRoot = null;

        _evtDoneTimer?.Dispose();
        _evtDoneTimer = null;
        _lastEvtDoneValue = null;
        _hasLastEvtDoneValue = false;
        _operationHandshakeRoot = null;
        _plcEvtDone = null;
        _plcCmdStart = null;
        _plcCmdSeq = null;

        _flowReceipt = null;
        _flowRecipeName = null;
        _flowOpCount = 0;
        _flowCurrentOpIndex = 0;
        _flowActive = false;
    }
  

    /// <summary>
    /// 启动 Operation 全流程状态机：
    /// Download -> WriteRun -> Wait -> (EvtDone=1) -> Next/Finish
    /// </summary>
    [ExportMethod]
    public void StartRunFlow()
    {
        if (_sm == null)
            InitStateMachine();
        if (_sm?.Current == null)
            _sm?.Start(_stIdle);

        if (_sm?.Current != _stIdle)
        {
            SetStatus($"流程已在运行中：{_sm?.Current?.Name ?? "Unknown"}");
            return;
        }

        if (!TryInitializeRunFlowContext(out string err))
        {
            SetStatus(err);
            _flowActive = false;
            TryTransitionTo(_stIdle);
            return;
        }

        TryTransitionTo(_stDownload);
    }

    /// <summary>
    /// 将当前批次编辑器中的元数据写入 PLC <c>Batch</c> 结构（不写 OP1 / Phases / 不置启动沿）。
    /// </summary>
    [ExportMethod]
    public void DownloadBatchToPlc()
    {
        SetStatus("下载中…");
        TryTransitionTo(_stDownload);
        if (!TryEnsureBatchTagReferences())
        {
            Log.Error(LogCategory, "DownloadBatchToPlc：无法解析 Batch NodeId 或子标签。");
            SetStatus("下载失败：Batch 引用无效");
            TryTransitionTo(_stIdle);
            return;
        }
        if (!TryEnsureRecipeTagReferences())
        {
            Log.Error(LogCategory, "DownloadBatchToPlc：无法解析 Recipe NodeId 或子标签。");
            SetStatus("下载失败：Recipe 引用无效");
            TryTransitionTo(_stIdle);
            return;
        }

        var editor = GetBatchEditorDataNode();
        if (editor == null)
        {
            Log.Error(LogCategory, "DownloadBatchToPlc：未找到 Model/UIData/BatchesEditorData/BatchEditorData。");
            SetStatus("下载失败：未找到 BatchEditorData");
            TryTransitionTo(_stIdle);
            return;
        }

        string batchName = ReadStringVariable(editor, "Name");
        string recipeName = ReadStringVariable(editor, "Recipe");

        if (string.IsNullOrWhiteSpace(batchName))
        {
            Log.Warning(LogCategory, "DownloadBatchToPlc：批次名称为空，已中止。");
            SetStatus("下载失败：批次名称为空");
            TryTransitionTo(_stIdle);
            return;
        }

        long rawId = ReadRawBatchId(batchName);
        int batchId = ToPlcDintId(rawId);

        var recipeNode = FindReceiptByName(recipeName);
        int recipeId = recipeNode?.ReceiptID ?? batchId;
        int noOfOperations = recipeNode?.Operations?.Count ?? 0;

        TrySetInt32(_plcBatchId, batchId);
        TrySetInt32(_plcBatchStatus, PlcBatchStatusReady);
        TrySetBoolean(_plcEvtBatchDone, false);
        TrySetString(_plcBatchName, batchName);
        TrySetString(_plcRunningPhaseName, "");
        TrySetBoolean(_plcBatchStart, false);
        TrySetString(_plcBatchRecipeName, recipeName ?? "");

        // 仅写入 Recipe 结构的目标字段：Name/ID/NoOfOperations（不写 RunningOperation / RecipeComplete）。
        TrySetString(_plcRecipeName, recipeName ?? "");
        TrySetInt32(_plcRecipeId, recipeId);
        TrySetInt32(_plcRecipeNoOfOperations, noOfOperations);

        Log.Info(LogCategory, $"DownloadBatchToPlc：已写入 BatchName='{batchName}', RecipeName='{recipeName}', BatchID={batchId}, Recipe.ID={recipeId}, Recipe.NoOfOperations={noOfOperations}.");
        SetStatus("下载成功");
        TryTransitionTo(_stIdle);
    }

 
    private bool TryInitializeRunFlowContext(out string errorStatus)
    {
        errorStatus = "";
        if (!TryEnsureBatchTagReferences())
        {
            errorStatus = "启动失败：Batch 引用无效";
            return false;
        }
        if (!TryEnsureOp1TagReferences())
        {
            errorStatus = "启动失败：Operation 引用无效";
            return false;
        }
        if (!TryEnsurePhasesTagReferences())
        {
            errorStatus = "启动失败：Phases 引用无效";
            return false;
        }
        if (!TryEnsureOperationHandshakeReferences())
        {
            errorStatus = "启动失败：OperationHandshake 引用无效";
            return false;
        }

        string recipeName = ReadStringVariableValue(_plcBatchRecipeName);
        if (string.IsNullOrWhiteSpace(recipeName))
        {
            errorStatus = "启动失败：PLC RecipeName 为空";
            return false;
        }
        if (!TryReadRunningOpIndex(out int runningIndex))
        {
            errorStatus = "启动失败：RunningOpIndex 无效";
            return false;
        }

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null)
        {
            errorStatus = "启动失败：TreeLoader 未启动";
            return false;
        }

        RecipeDatabaseTreeLoader.ReceiptNode receipt = null;
        foreach (var r in loader.Tree)
        {
            if (string.Equals(r?.Name, recipeName, StringComparison.OrdinalIgnoreCase))
            {
                receipt = r;
                break;
            }
        }
        if (receipt == null)
        {
            errorStatus = $"启动失败：未找到配方 {recipeName}";
            return false;
        }

        int opCount = receipt.Operations?.Count ?? 0;
        if (opCount == 0)
        {
            errorStatus = $"启动失败：{recipeName} 无 Operation";
            return false;
        }
        if (runningIndex >= opCount)
        {
            errorStatus = $"启动失败：索引 {runningIndex} 越界";
            return false;
        }

        _flowReceipt = receipt;
        _flowRecipeName = recipeName;
        _flowOpCount = opCount;
        _flowCurrentOpIndex = runningIndex;
        _flowActive = true;
        TrySetInt32(LogicObject.GetVariable("RunningOpIndex"), _flowCurrentOpIndex);
        return true;
    }

    private bool ExecuteDownloadCurrentOperation()
    {
        if (_flowReceipt == null || _flowCurrentOpIndex < 0 || _flowCurrentOpIndex >= _flowOpCount)
        {
            SetStatus("下载失败：流程上下文无效");
            _flowActive = false;
            TryTransitionTo(_stIdle);
            return false;
        }

        var loader = RecipeDatabaseTreeLoader.Instance;
        var op = _flowReceipt.Operations[_flowCurrentOpIndex];
        int opId = ToPlcDintId(op?.OperationID ?? 0);
        string opName = op?.Name ?? "";
        int noOfPhases = op?.Phases?.Count ?? 0;

        // 先写 Operation 头信息
        TrySetInt32(_plcOp1Id, opId);
        TrySetString(_plcOp1Name, opName);
        TrySetInt32(_plcOp1NoOfPhases, noOfPhases);

        int totalWritten = 0;
        int loadedPhases = 0;
        for (int i = 0; i < noOfPhases; i++)
        {
            var phase = op.Phases[i];
            if (phase == null) continue;
            var targetPhaseNode = GetPlcPhaseNodeByIndex(i);
            if (targetPhaseNode == null) break;

            loader?.LoadPhaseParametersToUdtTemplateBuffer(phase.PhaseID);
            var resolvedCols = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (phase.Columns != null)
            {
                foreach (var kv in phase.Columns)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    resolvedCols[kv.Key] = kv.Value;
                }
            }
            loader?.ApplyResolvedParameter123ToColumnCopy(phase, resolvedCols);
            totalWritten += WritePhaseColumnsToPlcPhaseNode(targetPhaseNode, resolvedCols);
            loadedPhases++;
        }

        Log.Info(LogCategory, $"FlowDownload：Recipe='{_flowRecipeName}', OpIndex={_flowCurrentOpIndex}, OP1.ID={opId}, OP1.Name='{opName}', OP1.NoOfPhases={noOfPhases}, 已下载Phase={loadedPhases}/{noOfPhases}, 写入字段总数={totalWritten}.");
        if (_flowActive)
            SetFlowStatus(opName, "下载");
        else
            SetStatus("下载成功");
        TryTransitionTo(_stWriteRun);
        return true;
    }

    private void InitStateMachine()
    {
        if (_sm != null)
            return;

        _sm = new RedeiptEditor.NetSolution.SimpleStateMachine("BatchInforToPLC");

        _stIdle = new RedeiptEditor.NetSolution.SimpleStateMachine.State(
            "IDLE",
            onEnter: _ => SetStatus("空闲"),
            onExit: _ => { },
            onRun: _ => { });

        _stDownload = new RedeiptEditor.NetSolution.SimpleStateMachine.State(
            "Download",
            onEnter: _ =>
            {
                if (_flowActive)
                    ExecuteDownloadCurrentOperation();
                else
                    SetStatus("下载状态");
            },
            onExit: _ => { },
            onRun: _ => { });

        _stWriteRun = new RedeiptEditor.NetSolution.SimpleStateMachine.State(
            "WriteRun",
            onEnter: _ =>
            {
                if (_flowActive)
                    ExecuteWriteRunStep();
                else
                    SetStatus("写入并运行状态");
            },
            onExit: _ => { },
            onRun: _ => { });

        _stWait = new RedeiptEditor.NetSolution.SimpleStateMachine.State(
            "Wait",
            onEnter: _ =>
            {
                if (_flowActive)
                    SetFlowStatus(GetCurrentFlowOpName(), "执行中等待完成");
                else
                    SetStatus("等待状态");
            },
            onExit: _ => { },
            onRun: _ => { });

        _stFinish = new RedeiptEditor.NetSolution.SimpleStateMachine.State(
            "Finish",
            onEnter: _ =>
            {
                _flowActive = false;
                SetStatus("运行结束");
            },
            onExit: _ => { },
            onRun: _ => { });

        _sm.AddState(_stIdle);
        _sm.AddState(_stDownload);
        _sm.AddState(_stWriteRun);
        _sm.AddState(_stWait);
        _sm.AddState(_stFinish);
    }

    private void TryTransitionTo(RedeiptEditor.NetSolution.SimpleStateMachine.State next)
    {
        if (_sm == null || next == null)
            return;
        try
        {
            _sm.TransitionTo(next);
        }
        catch
        {
            // 状态机不应影响主流程
        }
    }

    /// <summary>从脚本对象读取 <c>Batch</c> 指针并缓存 UDT 下业务子标签（跳过 SymbolName、BlockRead 等元数据）。</summary>
    private bool TryEnsureBatchTagReferences()
    {
        if (_batchRoot != null && _plcBatchName != null)
            return true;

        var batchPtr = LogicObject.GetVariable("Batch");
        if (batchPtr == null)
        {
            Log.Error(LogCategory, "未找到脚本变量 Batch。");
            return false;
        }

        var node = InformationModel.Get(batchPtr.Value);
        if (node == null)
        {
            Log.Error(LogCategory, "Batch 指针解析失败（InformationModel.Get 返回 null）。");
            return false;
        }

        _batchRoot = node;
        _plcBatchId = node.GetVariable("BatchID");
        _plcBatchStatus = node.GetVariable("BatchStatus");
        _plcEvtBatchDone = node.GetVariable("EvtBatchDone");
        _plcBatchName = node.GetVariable("BatchName");
        _plcRunningPhaseName = node.GetVariable("RunningPhaseName");
        _plcBatchStart = node.GetVariable("BatchStart");
        _plcBatchRecipeName = node.GetVariable("RecipeName");

        if (_plcBatchName == null || _plcBatchId == null)
        {
            Log.Error(LogCategory, "Batch 根节点下缺少 BatchName 或 BatchID 子变量，请核对 Controller Tags 结构。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从脚本对象读取 <c>Operation</c> 指针并缓存 OP1 下业务子标签。
    /// </summary>
    private bool TryEnsureOp1TagReferences()
    {
        if (_op1Root != null && _plcOp1Id != null && _plcOp1Name != null && _plcOp1NoOfPhases != null)
            return true;

        var opPtr = LogicObject.GetVariable("Operation");
        if (opPtr == null)
        {
            Log.Error(LogCategory, "未找到脚本变量 Operation。");
            return false;
        }

        var node = InformationModel.Get(opPtr.Value);
        if (node == null)
        {
            Log.Error(LogCategory, "Operation 指针解析失败（InformationModel.Get 返回 null）。");
            return false;
        }

        _op1Root = node;
        _plcOp1Id = node.GetVariable("ID");
        _plcOp1Name = node.GetVariable("Name");
        _plcOp1NoOfPhases = node.GetVariable("NoOfPhases");

        if (_plcOp1Id == null || _plcOp1Name == null || _plcOp1NoOfPhases == null)
        {
            Log.Error(LogCategory, "OP1 根节点下缺少 ID/Name/NoOfPhases 子变量，请核对 Controller Tags 结构。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从脚本对象读取 <c>Phases</c> 指针并缓存 PLC 根节点。
    /// </summary>
    private bool TryEnsurePhasesTagReferences()
    {
        if (_phasesRoot != null)
            return true;

        var phasesPtr = LogicObject.GetVariable("Phases");
        if (phasesPtr == null)
        {
            Log.Error(LogCategory, "未找到脚本变量 Phases。");
            return false;
        }

        var node = InformationModel.Get(phasesPtr.Value);
        if (node == null)
        {
            Log.Error(LogCategory, "Phases 指针解析失败（InformationModel.Get 返回 null）。");
            return false;
        }

        _phasesRoot = node;
        return true;
    }

    /// <summary>
    /// 从脚本对象读取 <c>Recipe</c> 指针并缓存下载时需要写入的子标签。
    /// </summary>
    private bool TryEnsureRecipeTagReferences()
    {
        if (_recipeRoot != null
            && _plcRecipeName != null
            && _plcRecipeId != null
            && _plcRecipeNoOfOperations != null)
            return true;

        var recipePtr = LogicObject.GetVariable("Recipe");
        if (recipePtr == null)
        {
            Log.Error(LogCategory, "未找到脚本变量 Recipe。");
            return false;
        }

        var node = InformationModel.Get(recipePtr.Value);
        if (node == null)
        {
            Log.Error(LogCategory, "Recipe 指针解析失败（InformationModel.Get 返回 null）。");
            return false;
        }

        _recipeRoot = node;
        _plcRecipeName = node.GetVariable("Name");
        _plcRecipeId = node.GetVariable("ID");
        _plcRecipeNoOfOperations = node.GetVariable("NoOfOperations");

        if (_plcRecipeName == null || _plcRecipeId == null || _plcRecipeNoOfOperations == null)
        {
            Log.Error(LogCategory, "Recipe 根节点下缺少 Name/ID/NoOfOperations 子变量，请核对 Controller Tags 结构。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从脚本对象读取 <c>OperationHandshake</c> 指针并缓存 <c>EvtDone</c> 子变量。
    /// </summary>
    private bool TryEnsureOperationHandshakeReferences()
    {
        if (_operationHandshakeRoot != null && _plcEvtDone != null && _plcCmdStart != null)
            return true;

        var handshakePtr = LogicObject.GetVariable("OperationHandshake");
        if (handshakePtr == null)
        {
            Log.Error(LogCategory, "未找到脚本变量 OperationHandshake。");
            return false;
        }

        var node = InformationModel.Get(handshakePtr.Value);
        if (node == null)
        {
            Log.Error(LogCategory, "OperationHandshake 指针解析失败（InformationModel.Get 返回 null）。");
            return false;
        }

        _operationHandshakeRoot = node;
        _plcEvtDone = node.GetVariable("EvtDone");
        _plcCmdStart = node.GetVariable("CmdStart");
        _plcCmdSeq = node.GetVariable("CmdSeq");
        if (_plcEvtDone == null)
        {
            Log.Error(LogCategory, "OperationHandshake 根节点下缺少 EvtDone 子变量。");
            return false;
        }
        if (_plcCmdStart == null)
        {
            Log.Error(LogCategory, "OperationHandshake 根节点下缺少 CmdStart 子变量。");
            return false;
        }

        return true;
    }

    private void SetupEvtDoneTimer()
    {
        _evtDoneTimer?.Dispose();
        _evtDoneTimer = null;
        _lastEvtDoneValue = null;
        _hasLastEvtDoneValue = false;

        if (!TryEnsureOperationHandshakeReferences())
            return;

        _evtDoneTimer = new PeriodicTask(PollEvtDoneValue, 200, LogicObject);
        _evtDoneTimer.Start();
    }

    private void PollEvtDoneValue()
    {
        if (_plcEvtDone == null) return;
        object current = null;
        try { current = _plcEvtDone.Value?.Value; } catch { return; }

        if (!_hasLastEvtDoneValue)
        {
            _lastEvtDoneValue = current;
            _hasLastEvtDoneValue = true;
            return;
        }

        bool same = (_lastEvtDoneValue == null && current == null)
                    || (_lastEvtDoneValue != null && _lastEvtDoneValue.Equals(current));
        if (!same)
        {
            string oldText = _lastEvtDoneValue?.ToString() ?? "<null>";
            string newText = current?.ToString() ?? "<null>";
            Log.Info(LogCategory, $"EvtDone 值变化：{oldText} -> {newText}");
            _lastEvtDoneValue = current;
        }

        UpdateRunningPhaseNameFromCmdSeqCurrentZeroBased();

        // 仅在 Wait 状态消费 EvtDone=1 事件推进流程
        if (_sm?.Current != _stWait)
            return;

        bool isDone = false;
        try
        {
            if (current is bool b) isDone = b;
            else if (current is sbyte sb) isDone = sb != 0;
            else if (current is byte by) isDone = by != 0;
            else if (current is short s) isDone = s != 0;
            else if (current is ushort us) isDone = us != 0;
            else if (current is int i) isDone = i != 0;
            else if (current is uint ui) isDone = ui != 0;
            else if (current is long l) isDone = l != 0;
            else if (current is ulong ul) isDone = ul != 0;
            else if (current != null) isDone = string.Equals(current.ToString(), "1", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(current.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { isDone = false; }

        if (!isDone)
            return;

        _flowCurrentOpIndex++;
        TrySetInt32(LogicObject.GetVariable("RunningOpIndex"), _flowCurrentOpIndex);

        if (_flowCurrentOpIndex < _flowOpCount)
        {
            TryTransitionTo(_stDownload);
            return;
        }

        TryTransitionTo(_stFinish);
    }

    /// <summary>
    /// 将 PLC 握手中的 CmdSeq（0 基，表示当前执行 phase）映射为 Phase 名称并写入 RunningPhaseName。
    /// </summary>
    private void UpdateRunningPhaseNameFromCmdSeqCurrentZeroBased()
    {
        if (_plcRunningPhaseName == null || _plcBatchRecipeName == null || _plcCmdSeq == null)
            return;

        string recipeName = ReadStringVariableValue(_plcBatchRecipeName);
        if (string.IsNullOrWhiteSpace(recipeName))
        {
            WriteRunningPhaseNameIfChanged("");
            return;
        }

        if (!TryReadRunningOpIndex(out int opIndex))
            return;

        int phaseIndex;
        try
        {
            object raw = _plcCmdSeq.Value?.Value;
            if (raw == null)
                return;
            phaseIndex = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return;
        }

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader?.Tree == null)
            return;

        RecipeDatabaseTreeLoader.ReceiptNode receipt = null;
        foreach (var r in loader.Tree)
        {
            if (string.Equals(r?.Name, recipeName, StringComparison.OrdinalIgnoreCase))
            {
                receipt = r;
                break;
            }
        }

        if (receipt?.Operations == null || opIndex < 0 || opIndex >= receipt.Operations.Count)
            return;

        var op = receipt.Operations[opIndex];
        if (op?.Phases == null)
            return;

        if (phaseIndex < 0 || phaseIndex >= op.Phases.Count)
        {
            WriteRunningPhaseNameIfChanged("");
            return;
        }

        string phaseName = op.Phases[phaseIndex]?.Name ?? "";
        WriteRunningPhaseNameIfChanged(phaseName);
    }

    private void WriteRunningPhaseNameIfChanged(string newName)
    {
        string target = newName ?? "";
        string current = ReadStringVariableValue(_plcRunningPhaseName);
        if (string.Equals(current, target, StringComparison.Ordinal))
            return;
        TrySetString(_plcRunningPhaseName, target);
    }

    private void ExecuteWriteRunStep()
    {
        if (!TryEnsureOperationHandshakeReferences())
        {
            SetStatus("WriteRun 失败：OperationHandshake 引用无效");
            _flowActive = false;
            TryTransitionTo(_stIdle);
            return;
        }

        TrySetBoolean(_plcCmdStart, true);
        TrySetBoolean(_plcEvtDone, false);
        if (_flowActive)
            SetFlowStatus(GetCurrentFlowOpName(), "执行中等待完成");
        else
            SetStatus("下载成功");
        TryTransitionTo(_stWait);
    }

    private string GetCurrentFlowOpName()
    {
        try
        {
            if (_flowReceipt?.Operations == null) return "";
            if (_flowCurrentOpIndex < 0 || _flowCurrentOpIndex >= _flowReceipt.Operations.Count) return "";
            return _flowReceipt.Operations[_flowCurrentOpIndex]?.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private void SetFlowStatus(string opName, string actionText)
    {
        int n = _flowCurrentOpIndex + 1;
        int m = _flowOpCount;
        string safeName = string.IsNullOrWhiteSpace(opName) ? "-" : opName;
        SetStatus($"[OP {n}/{m}] [{safeName}] {actionText}");
    }

    private IUANode GetPlcPhaseNodeByIndex(int index)
    {
        if (_phasesRoot == null || index < 0)
            return null;
        try
        {
            return _phasesRoot.Get(index.ToString(CultureInfo.InvariantCulture)) as IUANode;
        }
        catch
        {
            return null;
        }
    }

    private static IUAObject GetBatchEditorDataNode()
        => Project.Current?.GetObject("Model/UIData/BatchesEditorData/BatchEditorData") as IUAObject;

    private static string ReadStringVariable(IUAObject owner, string browseName)
    {
        var v = owner?.GetVariable(browseName);
        if (v?.Value == null)
            return "";
        object val = v.Value.Value;
        if (val == null)
            return "";
        if (val is LocalizedText lt)
            return lt.Text ?? "";
        return val.ToString() ?? "";
    }

    private bool TryReadRunningOpIndex(out int index)
    {
        index = 0;
        var v = LogicObject.GetVariable("RunningOpIndex");
        if (v == null)
        {
            // 未配置时使用默认第一个（0 基）。
            return true;
        }

        try
        {
            object raw = v.Value?.Value;
            if (raw == null)
                return true;

            index = Convert.ToInt32(raw);
            if (index < 0)
            {
                Log.Error(LogCategory, $"RunningOpIndex 不能小于 0，当前值={index}。");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"RunningOpIndex 解析失败：{ex.Message}");
            return false;
        }
    }

    private static string ReadStringVariableValue(IUAVariable v)
    {
        if (v?.Value == null)
            return "";
        object val = v.Value.Value;
        if (val == null)
            return "";
        if (val is LocalizedText lt)
            return lt.Text ?? "";
        return val.ToString() ?? "";
    }

    private static RecipeDatabaseTreeLoader.ReceiptNode FindReceiptByName(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
            return null;
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader?.Tree == null)
            return null;

        foreach (var r in loader.Tree)
        {
            if (string.Equals(r?.Name, recipeName, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    private int WritePhaseColumnsToPlcPhaseNode(IUANode phaseNode, Dictionary<string, object> cols)
    {
        if (phaseNode == null || cols == null || cols.Count == 0)
            return 0;

        int written = 0;
        WritePhaseColumnsRecursive(phaseNode, cols, ref written);
        return written;
    }

    private void WritePhaseColumnsRecursive(IUANode node, Dictionary<string, object> cols, ref int written)
    {
        if (node == null)
            return;

        if (node is IUAVariable v)
        {
            string key = v.BrowseName;
            if (!string.IsNullOrEmpty(key)
                && !IsPhaseMetaFieldName(key)
                && cols.TryGetValue(key, out var raw)
                && TryConvertPhaseColumnValueForVariable(v, raw, out object converted))
            {
                TrySetVariable(v, converted);
                written++;
            }
        }

        if (node.Children == null)
            return;
        foreach (var child in node.Children)
        {
            if (child == null || IsPhaseMetaFieldName(child.BrowseName))
                continue;
            WritePhaseColumnsRecursive(child, cols, ref written);
        }
    }

    private static bool IsPhaseMetaFieldName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        return string.Equals(name, "Description", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SymbolName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "BlockRead", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertPhaseColumnValueForVariable(IUAVariable v, object raw, out object converted)
    {
        converted = null;
        if (v == null)
            return false;
        if (raw == null || raw == DBNull.Value)
            return false;

        try
        {
            if (TryGetRankOneTemplateArray(v, out var template))
            {
                converted = CoerceRawToRankOneArray(raw, template);
                return converted != null;
            }

            if (raw is LocalizedText lt)
            {
                converted = lt.Text ?? "";
                return true;
            }
            if (raw is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or string)
            {
                converted = ConvertScalarByDataType(v, raw);
                return true;
            }

            converted = raw.ToString() ?? "";
            return true;
        }
        catch
        {
            return TryParseJsonArray(raw, out converted);
        }
    }

    private static bool TryGetRankOneTemplateArray(IUAVariable v, out Array template)
    {
        template = null;
        if (v == null)
            return false;
        try
        {
            object current = v.Value?.Value;
            if (current is Array arr && arr.Rank == 1 && arr.Length > 0)
            {
                template = arr;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static Array CoerceRawToRankOneArray(object raw, Array template)
    {
        if (raw == null || raw == DBNull.Value || template == null || template.Length <= 0)
            return null;

        int len = template.Length;
        Type elementType = template.GetValue(0)?.GetType() ?? typeof(string);

        // 若源值本身就是一维数组，按目标长度与元素类型拷贝/补齐。
        if (raw is Array srcArr && srcArr.Rank == 1)
        {
            Array result = Array.CreateInstance(elementType, len);
            int copy = Math.Min(len, srcArr.Length);
            for (int i = 0; i < copy; i++)
                result.SetValue(ConvertArrayElement(srcArr.GetValue(i), elementType), i);
            for (int i = copy; i < len; i++)
                result.SetValue(ConvertArrayElement("0", elementType), i);
            return result;
        }

        string s = raw.ToString()?.Trim() ?? "";
        if (s.Length == 0)
            s = "0";

        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                if (elementType == typeof(bool))
                {
                    var parsed = JsonSerializer.Deserialize<bool[]>(s);
                    return parsed == null ? null : PadOrTrimBoolArray(parsed, len);
                }
                if (elementType == typeof(float))
                {
                    var parsed = JsonSerializer.Deserialize<double[]>(s);
                    return parsed == null ? null : PadOrTrimFloatArray(parsed, len);
                }
                if (elementType == typeof(double))
                {
                    var parsed = JsonSerializer.Deserialize<double[]>(s);
                    return parsed == null ? null : PadOrTrimDoubleArray(parsed, len);
                }
                if (elementType == typeof(int))
                {
                    var parsed = JsonSerializer.Deserialize<int[]>(s);
                    return parsed == null ? null : PadOrTrimIntArray(parsed, len);
                }
                if (elementType == typeof(long))
                {
                    var parsed = JsonSerializer.Deserialize<long[]>(s);
                    return parsed == null ? null : PadOrTrimLongArray(parsed, len);
                }
                if (elementType == typeof(string))
                {
                    var parsed = JsonSerializer.Deserialize<string[]>(s);
                    return parsed == null ? null : PadOrTrimStringArray(parsed, len);
                }
            }
            catch
            {
            }
        }

        // 兼容历史格式：以 ';' 分隔，未提供位数则按 0 补齐。
        var parts = s.Split(';');
        Array fallback = Array.CreateInstance(elementType, len);
        for (int i = 0; i < len; i++)
        {
            string p = i < parts.Length ? parts[i].Trim() : "0";
            fallback.SetValue(ConvertArrayElement(p, elementType), i);
        }
        return fallback;
    }

    private static object ConvertArrayElement(object raw, Type elementType)
    {
        string s = raw?.ToString()?.Trim() ?? "0";
        if (elementType == typeof(bool))
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        if (elementType == typeof(float))
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
        if (elementType == typeof(double))
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        if (elementType == typeof(int))
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        if (elementType == typeof(long))
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;
        if (elementType == typeof(uint))
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0u;
        if (elementType == typeof(string))
            return raw?.ToString() ?? "";
        return Convert.ChangeType(0, elementType, CultureInfo.InvariantCulture);
    }

    private static Array PadOrTrimBoolArray(bool[] src, int len)
    {
        var result = new bool[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length && src[i];
        return result;
    }

    private static Array PadOrTrimFloatArray(double[] src, int len)
    {
        var result = new float[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length ? (float)src[i] : 0f;
        return result;
    }

    private static Array PadOrTrimDoubleArray(double[] src, int len)
    {
        var result = new double[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length ? src[i] : 0.0;
        return result;
    }

    private static Array PadOrTrimIntArray(int[] src, int len)
    {
        var result = new int[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length ? src[i] : 0;
        return result;
    }

    private static Array PadOrTrimLongArray(long[] src, int len)
    {
        var result = new long[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length ? src[i] : 0L;
        return result;
    }

    private static Array PadOrTrimStringArray(string[] src, int len)
    {
        var result = new string[len];
        for (int i = 0; i < len; i++)
            result[i] = i < src.Length ? (src[i] ?? "") : "";
        return result;
    }

    private static object ConvertScalarByDataType(IUAVariable v, object raw)
    {
        NodeId t = v.DataType;
        if (t == OpcUa.DataTypes.Boolean) return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.Int16) return Convert.ToInt16(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.Int32) return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.Int64) return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.UInt16) return Convert.ToUInt16(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.UInt32) return Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.UInt64) return Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.Float) return Convert.ToSingle(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.Double) return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (t == OpcUa.DataTypes.String) return raw.ToString() ?? "";
        return raw;
    }

    private static bool TryParseJsonArray(object raw, out object arr)
    {
        arr = null;
        string s = raw?.ToString()?.Trim();
        if (string.IsNullOrEmpty(s) || s[0] != '[')
            return false;

        try { arr = JsonSerializer.Deserialize<bool[]>(s); if (arr != null) return true; } catch { }
        try { arr = JsonSerializer.Deserialize<int[]>(s); if (arr != null) return true; } catch { }
        try { arr = JsonSerializer.Deserialize<double[]>(s); if (arr != null) return true; } catch { }
        try { arr = JsonSerializer.Deserialize<string[]>(s); if (arr != null) return true; } catch { }
        return false;
    }

    private static void TrySetVariable(IUAVariable v, object value)
    {
        if (v == null) return;
        try
        {
            v.Value = new UAValue(value);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"写入变量（{v.BrowseName}）失败：{ex.Message}");
        }
    }

    /// <summary>从库读 BatchID，失败则用批次名哈希（long，不做 DINT 裁剪）。</summary>
    private static long ReadRawBatchId(string batchName)
    {
        if (string.IsNullOrEmpty(batchName))
            return 0;

        try
        {
            var store = Project.Current?.GetObject("DataStores")?.Get<Store>("ReceiptDB");
            if (store == null)
                return batchName.GetHashCode();

            store.Query($"SELECT BatchID FROM Batches WHERE Name='{EscapeSql(batchName)}'", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0)
                return batchName.GetHashCode();

            object cell = rows[0, 0];
            if (cell == null || cell == DBNull.Value)
                return batchName.GetHashCode();

            return Convert.ToInt64(cell);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"读取 BatchID 失败，使用哈希：{ex.Message}");
            return batchName.GetHashCode();
        }
    }

    /// <summary>仅用于写入 PLC 的 BatchID：落在 Int32 内则原值，否则取绝对值十进制末尾 <see cref="PlcBatchIdSuffixDigits"/> 位。</summary>
    private static int ToPlcDintId(long raw)
    {
        if (raw >= int.MinValue && raw <= int.MaxValue)
            return (int)raw;

        ulong magnitude = raw == long.MinValue
            ? (ulong)long.MaxValue + 1UL
            : (ulong)(raw < 0 ? -raw : raw);

        string s = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Length > PlcBatchIdSuffixDigits)
            s = s.Substring(s.Length - PlcBatchIdSuffixDigits);

        if (!long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long tail))
            return 0;
        return tail > int.MaxValue ? int.MaxValue : (int)tail;
    }

    private static string EscapeSql(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Replace("'", "''");
    }

    private static void TrySetInt32(IUAVariable v, int value)
    {
        if (v == null) return;
        try
        {
            v.Value = value;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"写入 Int32（{v.BrowseName}）失败：{ex.Message}");
        }
    }

    private static void TrySetBoolean(IUAVariable v, bool value)
    {
        if (v == null) return;
        try
        {
            v.Value = value;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"写入 Boolean（{v.BrowseName}）失败：{ex.Message}");
        }
    }

    private static void TrySetString(IUAVariable v, string value)
    {
        if (v == null) return;
        try
        {
            v.Value = value ?? "";
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"写入 String（{v.BrowseName}）失败：{ex.Message}");
        }
    }

    private void SetStatus(string text)
    {
        if (_statusText == null)
            return;
        try
        {
            _statusText.Value = text ?? "";
        }
        catch
        {
            // 状态文本不应影响主流程
        }
    }
}
