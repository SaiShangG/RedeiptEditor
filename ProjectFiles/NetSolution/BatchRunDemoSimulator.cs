#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
#endregion

/// <summary>
/// 批次运行演示：不依赖 PLC 正确逻辑，按配方树顺序每 <see cref="PhaseIntervalMs"/> 推进 Operation/Phase，
/// 写入与 <see cref="GenerateBatchRunFlow"/>、顶部状态栏相同的标签，使当前步文字变绿。
/// </summary>
public class BatchRunDemoSimulator : BaseNetLogic
{
    private const string LogCategory = "BatchRunDemoSimulator";
    private const string BatchDownloadToPlcDataPath = "Model/UIData/BatchesEditorData/BatchDownloadToPlcData";
    private const string BatchInforToPlcPath = "NetLogic/BatchInforToPLC";

    private int _phaseIntervalMs = 3000;
    private PeriodicTask _stepTimer;
    private readonly List<DemoStep> _steps = new List<DemoStep>();
    private int _stepIndex;
    private bool _demoRunning;

    private static BatchRunDemoSimulator _instance;

    private IUANode _batchRoot;
    private IUANode _recipeRoot;
    private IUANode _opRoot;
    private IUANode _handshakeRoot;
    private IUAObject _batchInforLogic;

    private IUAVariable _plcBatchRecipeName;
    private IUAVariable _plcRunningPhaseName;
    private IUAVariable _plcBatchRunning;
    private IUAVariable _plcCmdSeq;
    private IUAVariable _plcOpName;
    private IUAVariable _plcRecipeName;
    private IUAVariable _plcRunningOpRunning;
    private IUAVariable _plcRunningOpHeld;
    private IUAVariable _plcRunningOpIdle;

    private struct DemoStep
    {
        public int OpIndex;
        public int PhaseIndex;
        public string OpName;
        public string PhaseName;
    }

    /// <summary>由 <see cref="BatchInforToPLC"/> 在演示模式下调用。</summary>
    public static void RequestStart() => GenerateBatchRunFlow.RequestStartDemo();

    public override void Start()
    {
        _instance = this;
        try
        {
            var ms = LogicObject.GetVariable("PhaseIntervalMs");
            if (ms?.Value != null)
                _phaseIntervalMs = Math.Max(500, Convert.ToInt32(ms.Value.Value, CultureInfo.InvariantCulture));
        }
        catch { }

        ResolveTagReferences();
    }

    public override void Stop()
    {
        if (_instance == this)
            _instance = null;
        StopDemoInternal("Stopped");
    }

    /// <summary>Start 按钮演示入口：先 Download to PLC，再点此开始逐步演示。</summary>
    [ExportMethod]
    public void StartDemoRun()
    {
        if (_demoRunning)
        {
            SetBatchInforStatus("演示已在运行中");
            return;
        }

        ResolveTagReferences();
        if (!TryBuildStepsFromRecipe(out string err))
        {
            SetBatchInforStatus(err);
            Log.Warning(LogCategory, err);
            return;
        }

        _stepIndex = 0;
        _demoRunning = true;
        SetBatchInforStatus("演示运行中…");
        ApplyCurrentStep();
        ScheduleNextStep();
    }

    [ExportMethod]
    public void StopDemoRun() => StopDemoInternal("演示已停止");

    private void ScheduleNextStep()
    {
        _stepTimer?.Dispose();
        _stepTimer = new PeriodicTask(OnStepTimer, _phaseIntervalMs, LogicObject);
        _stepTimer.Start();
    }

    private void OnStepTimer()
    {
        if (!_demoRunning)
            return;

        _stepIndex++;
        if (_stepIndex >= _steps.Count)
        {
            FinishDemo();
            return;
        }

        ApplyCurrentStep();
    }

    private void ApplyCurrentStep()
    {
        if (_stepIndex < 0 || _stepIndex >= _steps.Count)
            return;

        var step = _steps[_stepIndex];
        TrySetInt32(_batchInforLogic?.GetVariable("RunningOpIndex"), step.OpIndex);
        TrySetInt32(_plcCmdSeq, step.PhaseIndex);
        TrySetString(_plcOpName, step.OpName ?? "");
        TrySetString(_plcRunningPhaseName, step.PhaseName ?? "");
        TrySetBoolean(_plcRunningOpRunning, true);
        TrySetBoolean(_plcRunningOpHeld, false);
        TrySetBoolean(_plcRunningOpIdle, false);
        TrySetBoolean(_plcBatchRunning, true);
        BumpFlowRefreshTick();

        SetBatchInforStatus($"演示: {step.OpName} / {step.PhaseName} ({_stepIndex + 1}/{_steps.Count})");
        if (LogicObject.GetVariable("EnableLog") is { } logVar && (bool)logVar.Value)
            Log.Info(LogCategory, $"Step {_stepIndex + 1}/{_steps.Count}: Op[{step.OpIndex}] {step.OpName}, Phase[{step.PhaseIndex}] {step.PhaseName}");
    }

    private void FinishDemo()
    {
        StopDemoInternal("演示完成");
        TrySetBoolean(_plcRunningOpRunning, false);
        TrySetBoolean(_plcRunningOpHeld, false);
        TrySetBoolean(_plcRunningOpIdle, true);
        TrySetBoolean(_plcBatchRunning, false);
        BumpFlowRefreshTick();
    }

    private void StopDemoInternal(string status)
    {
        _stepTimer?.Dispose();
        _stepTimer = null;
        _demoRunning = false;
        _steps.Clear();
        _stepIndex = 0;
        if (!string.IsNullOrEmpty(status))
            SetBatchInforStatus(status);
    }

    private bool TryBuildStepsFromRecipe(out string error)
    {
        error = "";
        _steps.Clear();

        string recipeName = ReadStringTag(_plcBatchRecipeName);
        if (string.IsNullOrWhiteSpace(recipeName))
            recipeName = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "Recipe");

        if (string.IsNullOrWhiteSpace(recipeName))
        {
            error = "演示启动失败：请先 Download to PLC（配方名为空）";
            return false;
        }

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null)
        {
            error = "演示启动失败：RecipeDatabaseTreeLoader 未就绪";
            return false;
        }

        if (loader.Tree == null || loader.Tree.Count == 0)
        {
            try { loader.LoadAllToTree(); }
            catch (Exception ex)
            {
                error = $"演示启动失败：无法加载配方树 ({ex.Message})";
                return false;
            }
        }

        RecipeDatabaseTreeLoader.ReceiptNode receipt = null;
        foreach (var r in loader.Tree ?? new List<RecipeDatabaseTreeLoader.ReceiptNode>())
        {
            if (string.Equals(r?.Name, recipeName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                receipt = r;
                break;
            }
        }

        if (receipt == null)
        {
            error = $"演示启动失败：未找到配方 {recipeName}";
            return false;
        }

        if (receipt.Operations == null || receipt.Operations.Count == 0)
        {
            error = $"演示启动失败：配方 [{receipt.Name}] 无 Operation";
            return false;
        }

        TrySetString(_plcRecipeName, receipt.Name ?? recipeName);

        for (int oi = 0; oi < receipt.Operations.Count; oi++)
        {
            var op = receipt.Operations[oi];
            string opName = op?.Name ?? $"Operation_{oi + 1}";
            int phaseCount = op?.Phases?.Count ?? 0;
            if (phaseCount == 0)
            {
                _steps.Add(new DemoStep { OpIndex = oi, PhaseIndex = 0, OpName = opName, PhaseName = "" });
                continue;
            }

            for (int pi = 0; pi < phaseCount; pi++)
            {
                string phaseName = op.Phases[pi]?.Name ?? $"Phase_{pi + 1}";
                _steps.Add(new DemoStep { OpIndex = oi, PhaseIndex = pi, OpName = opName, PhaseName = phaseName });
            }
        }

        if (_steps.Count == 0)
        {
            error = "演示启动失败：配方中无可演示的步骤";
            return false;
        }

        return true;
    }

    private void ResolveTagReferences()
    {
        _batchRoot = ResolveNode("Batch");
        _recipeRoot = ResolveNode("Recipe");
        _opRoot = ResolveNode("OP");
        _handshakeRoot = ResolveNode("OperationHandshake");

        var bid = LogicObject.GetVariable("BatchInforToPLC")?.Value;
        if (bid != null)
        {
            try { _batchInforLogic = InformationModel.Get((NodeId)bid.Value) as IUAObject; }
            catch { _batchInforLogic = null; }
        }

        if (_batchInforLogic == null)
            _batchInforLogic = Project.Current?.GetObject(BatchInforToPlcPath) as IUAObject;

        _plcBatchRecipeName = _batchRoot?.GetVariable("RecipeName");
        _plcRunningPhaseName = _batchRoot?.GetVariable("RunningPhaseName");
        _plcBatchRunning = _batchRoot?.GetVariable("BatchRunning");
        _plcCmdSeq = _handshakeRoot?.GetVariable("CmdSeq");
        _plcOpName = _opRoot?.GetVariable("Name");
        _plcRecipeName = _recipeRoot?.GetVariable("Name");

        var runOp = _recipeRoot?.Get("RunningOperation") as IUANode;
        _plcRunningOpRunning = runOp?.GetVariable("Running");
        _plcRunningOpHeld = runOp?.GetVariable("Held");
        _plcRunningOpIdle = runOp?.GetVariable("Idle");
    }

    private IUANode ResolveNode(string varName)
    {
        var ptr = LogicObject.GetVariable(varName);
        if (ptr?.Value == null) return null;
        try { return InformationModel.Get((NodeId)ptr.Value); }
        catch { return null; }
    }

    private static void BumpFlowRefreshTick()
    {
        var snapshot = Project.Current?.GetObject(BatchDownloadToPlcDataPath) as IUAObject;
        var tickVar = snapshot?.GetVariable("FlowRefreshTick");
        if (tickVar?.Value == null) return;
        try
        {
            int tick = Convert.ToInt32(tickVar.Value.Value, CultureInfo.InvariantCulture);
            tickVar.Value = tick + 1;
        }
        catch { }
    }

    private void SetBatchInforStatus(string text)
    {
        var v = _batchInforLogic?.GetVariable("StatusText");
        if (v == null) return;
        try { v.Value = text ?? ""; }
        catch { }
    }

    private static IUAObject GetBatchDownloadToPlcDataNode()
        => Project.Current?.GetObject(BatchDownloadToPlcDataPath) as IUAObject;

    private static string ReadStringVariable(IUAObject owner, string name)
    {
        var v = owner?.GetVariable(name);
        if (v?.Value == null) return "";
        object val = v.Value.Value;
        if (val is LocalizedText lt) return lt.Text ?? "";
        return val?.ToString() ?? "";
    }

    private static string ReadStringTag(IUAVariable v)
    {
        if (v?.Value == null) return "";
        object val = v.Value.Value;
        if (val is LocalizedText lt) return lt.Text ?? "";
        return val?.ToString() ?? "";
    }

    private static void TrySetInt32(IUAVariable v, int value)
    {
        if (v == null) return;
        try { v.Value = value; }
        catch (Exception ex) { Log.Warning(LogCategory, $"写入 {v.BrowseName} 失败: {ex.Message}"); }
    }

    private static void TrySetBoolean(IUAVariable v, bool value)
    {
        if (v == null) return;
        try { v.Value = value; }
        catch (Exception ex) { Log.Warning(LogCategory, $"写入 {v.BrowseName} 失败: {ex.Message}"); }
    }

    private static void TrySetString(IUAVariable v, string value)
    {
        if (v == null) return;
        try { v.Value = value ?? ""; }
        catch (Exception ex) { Log.Warning(LogCategory, $"写入 {v.BrowseName} 失败: {ex.Message}"); }
    }
}
