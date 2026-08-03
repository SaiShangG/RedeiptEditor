#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.DataLogger;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.EventLogger;
#endregion

public class GenerateRunningTreeList : BaseNetLogic
{
    private const string LogCategory = nameof(GenerateRunningTreeList);
    private const string BatchDownloadToPlcDataPath = "Model/UIData/BatchesEditorData/BatchDownloadToPlcData";
    private const string CurrentBatchDataBatchPath = "Model/UIData/BatchesEditorData/CurrentBatchData/Batch";
    private const string CurrentBatchDataOperationPath = "Model/UIData/BatchesEditorData/CurrentBatchData/Operation";
    private const string ComponentsFolderPath = "UI/Widgets/Components";
    private const string OperationOptionsObjectName = "JumpOperationOptions";
    private const string PhaseOptionsObjectName = "JumpPhaseOptions";
    private const int DisabledOptionIndex = 0;

    private const float RowHeight = 26f;
    private const float FontSize = 14f;
    private const float IconSize = 14f;
    private const float ExpandButtonSlotWidth = 16f;
    private const float ReceiptIndent = 0f;
    private const float OperationIndent = 18f;
    private const float PhaseIndent = 38f;

    private static readonly Color RunningTextColor = new Color(255, 0, 0x9a, 0x3c);
    private static readonly Color JumpTargetTextColor = new Color(255, 0xff, 0x69, 0xb4);
    private static readonly Color NormalTextColor = new Color(255, 0x33, 0x33, 0x33);
    private static readonly Color MessageTextColor = new Color(255, 0x66, 0x66, 0x66);
    private static readonly Color RunningBackgroundColor = new Color(255, 0xd9, 0xf4, 0xe3);
    private static readonly Color JumpTargetBackgroundColor = new Color(255, 0xff, 0xe4, 0xf1);
    private static readonly Color TransparentBg = new Color(0, 0xe4, 0xe4, 0xe4);
    private static readonly Color BorderNone = new Color(0, 0, 0, 0);

    private bool _enableLog = true;
    private int _refreshPeriodMs = 500;
    private PeriodicTask _refreshTask;
    private uint _observerAffinityId;
    private readonly List<IEventRegistration> _snapshotRegs = new List<IEventRegistration>();
    private string _lastBuiltRecipeKey = "";
    private int _lastFlowRefreshTick = -1;

    private NodeId _receiptItemTypeId = NodeId.Empty;
    private NodeId _operationItemTypeId = NodeId.Empty;
    private NodeId _phaseItemTypeId = NodeId.Empty;
    private RecipeDatabaseTreeLoader.ReceiptNode _currentJumpReceipt;
    private bool _suppressOperationSelectionRefresh;
    private int _jumpTargetOpIndex = -1;
    private int _jumpTargetPhaseIndex = -1;

    public override void Start()
    {
        ReadSettings();
        ResolveTypeIds();
        try { _observerAffinityId = LogicObject.Context.AssignAffinityId(); }
        catch { _observerAffinityId = 0; }
        RegisterSnapshotObservers();
        Generate();

        _refreshTask?.Dispose();
        _refreshTask = new PeriodicTask(RefreshRunningStatus, _refreshPeriodMs, LogicObject);
        _refreshTask.Start();
    }

    public override void Stop()
    {
        var operationComboBox = GetJumpOperationComboBox();
        if (operationComboBox != null)
            operationComboBox.UAEvent -= OnOperationComboUserSelectionChanged;
        var phaseComboBox = GetJumpPhaseComboBox();
        if (phaseComboBox != null)
            phaseComboBox.UAEvent -= OnPhaseComboUserSelectionChanged;
        var operationButton = GetJumpOperationButton();
        if (operationButton != null)
            operationButton.UAEvent -= OnJumpOperationClicked;
        var phaseButton = GetJumpPhaseButton();
        if (phaseButton != null)
            phaseButton.UAEvent -= OnJumpPhaseClicked;
        UnregisterSnapshotObservers();
        _refreshTask?.Dispose();
        _refreshTask = null;
    }

    private void RegisterSnapshotObservers()
    {
        UnregisterSnapshotObservers();
        var snapshot = GetBatchDownloadToPlcDataNode();
        if (snapshot == null) return;

        var treeObserver = new CallbackVariableChangeObserver((variable, newValue, oldValue, eventType, senderId) => Generate());
        foreach (string name in new[] { "Recipe", "Name", "RecipeID", "DownloadDateTime" })
        {
            var variable = snapshot.GetVariable(name);
            if (variable == null) continue;
            try { _snapshotRegs.Add(variable.RegisterEventObserver(treeObserver, EventType.VariableValueChanged, _observerAffinityId)); }
            catch { }
        }

        var statusObserver = new CallbackVariableChangeObserver((variable, newValue, oldValue, eventType, senderId) => RefreshRunningStatus());
        foreach (string name in new[] { "OperationName", "PhaseName", "RecipeIsRunning", "RecipeFinished", "RecipeRefreshTick", "FlowIsRunning", "FlowBatchFinished", "FlowRefreshTick" })
        {
            var variable = snapshot.GetVariable(name);
            if (variable == null) continue;
            try { _snapshotRegs.Add(variable.RegisterEventObserver(statusObserver, EventType.VariableValueChanged, _observerAffinityId)); }
            catch { }
        }

        RegisterStatusObserver(GetCurrentBatchDataOperationNode()?.GetVariable("OperationName"), statusObserver);
        RegisterStatusObserver(GetCurrentBatchDataBatchNode()?.GetVariable("RunningPhaseName"), statusObserver);
    }

    private void RegisterStatusObserver(IUAVariable variable, CallbackVariableChangeObserver observer)
    {
        if (variable == null || observer == null) return;
        try { _snapshotRegs.Add(variable.RegisterEventObserver(observer, EventType.VariableValueChanged, _observerAffinityId)); }
        catch { }
    }

    private void UnregisterSnapshotObservers()
    {
        foreach (var registration in _snapshotRegs)
        {
            try { registration?.Dispose(); }
            catch { }
        }
        _snapshotRegs.Clear();
    }

    [ExportMethod]
    public void Regenerate() => Generate();

    private void ReadSettings()
    {
        try
        {
            var logVar = LogicObject.GetVariable("EnableLog");
            if (logVar != null)
                _enableLog = (bool)logVar.Value;
        }
        catch { }

        try
        {
            var periodVar = LogicObject.GetVariable("RefreshPeriodMs");
            if (periodVar?.Value != null)
                _refreshPeriodMs = Math.Max(200, Convert.ToInt32(periodVar.Value.Value, CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private void Generate()
    {
        Container treeContainer = null;
        try
        {
            treeContainer = GetTreeContainer();
            if (treeContainer == null)
            {
                if (_enableLog) Log.Error(LogCategory, "未找到 TreeContainer。请确认 GenerateRunningTreeList 与 TreeContainer 同在 ScrollView1 下。");
                return;
            }

            ResolveTypeIds();
            ClearGeneratedRows(treeContainer);

            if (_receiptItemTypeId == NodeId.Empty || _operationItemTypeId == NodeId.Empty || _phaseItemTypeId == NodeId.Empty)
            {
                if (_enableLog) Log.Error(LogCategory, "未找到 ReceiptListItem / OperationListItem / PhaseListItem 类型。");
                return;
            }

            EnsureRecipeTreeLoaded();

            string recipeName = ResolveActiveRecipeName();
            string batchName = ReadSnapshotBatchName();
            string downloadStamp = ReadSnapshotDownloadStamp();
            _lastBuiltRecipeKey = FormatRecipeKey(recipeName, batchName, downloadStamp);
            _lastFlowRefreshTick = ReadSnapshotFlowRefreshTick();

            ApplyHostLayout(treeContainer);
            float rowWidth = GetRowWidth(treeContainer);

            if (string.IsNullOrWhiteSpace(recipeName))
            {
                ClearJumpComboBoxes();
                AddMessageRow(treeContainer, rowWidth, "No downloaded running recipe");
                ResizeTreeContainer(treeContainer);
                return;
            }

            var receipt = FindReceiptForTree(recipeName);
            if (receipt == null)
            {
                ClearJumpComboBoxes();
                AddMessageRow(treeContainer, rowWidth, $"Recipe not found: {recipeName}");
                ResizeTreeContainer(treeContainer);
                return;
            }

            PopulateJumpComboBoxes(receipt);

            int rows = 0;
            var receiptItem = InformationModel.MakeObject(SafeName(receipt.Name) + "_Receipt", _receiptItemTypeId) as Container;
            if (receiptItem != null)
            {
                ConfigureRow(receiptItem, rowWidth, receipt.Name ?? recipeName, "", "", -1, -1, receipt.ReceiptID, 0, 0, TreeRowKind.Receipt);
                treeContainer.Add(receiptItem);
                rows++;
            }

            for (int opIndex = 0; opIndex < (receipt.Operations?.Count ?? 0); opIndex++)
            {
                var op = receipt.Operations[opIndex];
                string opName = op?.Name ?? $"Operation_{opIndex + 1}";
                var opItem = InformationModel.MakeObject(SafeName(opName) + "_Operation", _operationItemTypeId) as Container;
                if (opItem != null)
                {
                    ConfigureRow(opItem, rowWidth, opName, opName, "", opIndex, -1, receipt.ReceiptID, op?.OperationID ?? 0, 0, TreeRowKind.Operation);
                    treeContainer.Add(opItem);
                    rows++;
                }

                for (int phaseIndex = 0; phaseIndex < (op?.Phases?.Count ?? 0); phaseIndex++)
                {
                    var phase = op.Phases[phaseIndex];
                    string phaseName = phase?.Name ?? $"Phase_{phaseIndex + 1}";
                    var phaseItem = InformationModel.MakeObject(SafeName(phaseName) + "_Phase", _phaseItemTypeId) as Container;
                    if (phaseItem == null) continue;
                    ConfigureRow(phaseItem, rowWidth, phaseName, opName, phaseName, opIndex, phaseIndex, receipt.ReceiptID, op?.OperationID ?? 0, phase?.PhaseID ?? 0, TreeRowKind.Phase);
                    treeContainer.Add(phaseItem);
                    rows++;
                }
            }

            if (rows == 0)
                AddMessageRow(treeContainer, rowWidth, $"Recipe [{receipt.Name}] has no running tree rows");

            ResizeTreeContainer(treeContainer);
            RefreshRunningStatus();

            if (_enableLog)
                Log.Info(LogCategory, $"Running tree generated: Recipe='{recipeName}', Rows={CountGeneratedRows(treeContainer)}, TreeWidth={treeContainer.Width}, TreeHeight={treeContainer.Height}");
        }
        catch (Exception ex)
        {
            if (_enableLog) Log.Error(LogCategory, $"Generate failed: {ex.Message}");
            if (treeContainer != null)
            {
                try
                {
                    ClearGeneratedRows(treeContainer);
                    AddMessageRow(treeContainer, GetRowWidth(treeContainer), "Running tree generate failed");
                    ResizeTreeContainer(treeContainer);
                }
                catch { }
            }
        }
    }

    private void RefreshRunningStatus()
    {
        var treeContainer = GetTreeContainer();
        if (treeContainer == null) return;

        string recipeName = ResolveActiveRecipeName();
        string batchName = ReadSnapshotBatchName();
        string downloadStamp = ReadSnapshotDownloadStamp();
        string key = FormatRecipeKey(recipeName, batchName, downloadStamp);
        int tick = ReadSnapshotFlowRefreshTick();
        if (!string.Equals(key, _lastBuiltRecipeKey, StringComparison.Ordinal)
            || CountGeneratedRows(treeContainer) == 0)
        {
            Generate();
            return;
        }
        _lastFlowRefreshTick = tick;

        ResolveCurrentStep(out string runningOperationName, out string runningPhaseName, out bool isRunning);
        foreach (var row in treeContainer.Children.OfType<Container>())
        {
            if (!IsGeneratedRow(row)) continue;
            if (!TryGetFlowIndices(row, out int opIndex, out int phaseIndex)) continue;
            if (!TryGetFlowNames(row, out string rowOperationName, out string rowPhaseName)) continue;

            bool highlight = isRunning && IsRunningRowByName(rowOperationName, rowPhaseName, phaseIndex, runningOperationName, runningPhaseName);
            bool jumpTarget = IsJumpTargetRow(opIndex, phaseIndex);
            ApplyRowColors(
                row,
                jumpTarget ? JumpTargetTextColor : highlight ? RunningTextColor : NormalTextColor,
                jumpTarget ? JumpTargetBackgroundColor : highlight ? RunningBackgroundColor : TransparentBg);
        }
    }

    private void ResolveCurrentStep(out string operationName, out string phaseName, out bool isRunning)
    {
        operationName = ReadCurrentOperationName();
        phaseName = ReadCurrentPhaseName();
        isRunning = IsCurrentNameMeaningful(operationName);
    }

    private void PopulateJumpComboBoxes(RecipeDatabaseTreeLoader.ReceiptNode receipt)
    {
        _currentJumpReceipt = receipt;
        ClearJumpTarget();
        ApplyJumpPanelText();
        WireJumpButtons();

        var operationComboBox = GetJumpOperationComboBox();
        if (operationComboBox != null)
        {
            operationComboBox.UAEvent -= OnOperationComboUserSelectionChanged;
            operationComboBox.UAEvent += OnOperationComboUserSelectionChanged;

            var operationOptions = BuildOperationOptions(receipt);
            _suppressOperationSelectionRefresh = true;
            try
            {
                SetComboOptions(operationComboBox, OperationOptionsObjectName, operationOptions, DisabledOptionIndex);
            }
            finally
            {
                _suppressOperationSelectionRefresh = false;
            }
        }

        var phaseComboBox = GetJumpPhaseComboBox();
        if (phaseComboBox != null)
        {
            phaseComboBox.UAEvent -= OnPhaseComboUserSelectionChanged;
            phaseComboBox.UAEvent += OnPhaseComboUserSelectionChanged;
        }

        int selectedOperationIndex = ResolveSelectedOperationIndex(operationComboBox);
        PopulatePhaseComboBox(selectedOperationIndex);
    }

    private void ClearJumpComboBoxes()
    {
        _currentJumpReceipt = null;
        ClearJumpTarget();
        SetComboOptions(GetJumpOperationComboBox(), OperationOptionsObjectName, new List<JumpComboOption>(), DisabledOptionIndex);
        SetComboOptions(GetJumpPhaseComboBox(), PhaseOptionsObjectName, new List<JumpComboOption>(), DisabledOptionIndex);
        RefreshJumpButtonEnabled();
    }

    private void WireJumpButtons()
    {
        var operationButton = GetJumpOperationButton();
        if (operationButton != null)
        {
            operationButton.UAEvent -= OnJumpOperationClicked;
            operationButton.UAEvent += OnJumpOperationClicked;
        }

        var phaseButton = GetJumpPhaseButton();
        if (phaseButton != null)
        {
            phaseButton.UAEvent -= OnJumpPhaseClicked;
            phaseButton.UAEvent += OnJumpPhaseClicked;
        }
    }

    private void OnJumpOperationClicked(object sender, UAEventArgs args)
    {
        var selectedOption = GetSelectedComboOption(GetJumpOperationComboBox());
        int opIndex = ReadIntVariable(selectedOption?.GetVariable("OpIndex"), -1);
        if (opIndex < 0)
        {
            ClearJumpTarget();
            RefreshRunningStatus();
            return;
        }

        _jumpTargetOpIndex = opIndex;
        _jumpTargetPhaseIndex = -1;
        if (_enableLog)
            Log.Info(LogCategory, $"Jump target operation selected: OpIndex={_jumpTargetOpIndex}");
        RefreshRunningStatus();
    }

    private void OnJumpPhaseClicked(object sender, UAEventArgs args)
    {
        var selectedOption = GetSelectedComboOption(GetJumpPhaseComboBox());
        int opIndex = ReadIntVariable(selectedOption?.GetVariable("OpIndex"), -1);
        int phaseIndex = ReadIntVariable(selectedOption?.GetVariable("PhaseIndex"), -1);
        if (opIndex < 0 || phaseIndex < 0)
        {
            ClearJumpTarget();
            RefreshRunningStatus();
            return;
        }

        _jumpTargetOpIndex = opIndex;
        _jumpTargetPhaseIndex = phaseIndex;
        if (_enableLog)
            Log.Info(LogCategory, $"Jump target phase selected: OpIndex={_jumpTargetOpIndex}, PhaseIndex={_jumpTargetPhaseIndex}");
        RefreshRunningStatus();
    }

    private void ClearJumpTarget()
    {
        _jumpTargetOpIndex = -1;
        _jumpTargetPhaseIndex = -1;
    }

    private bool IsJumpTargetRow(int opIndex, int phaseIndex)
    {
        if (_jumpTargetOpIndex < 0 || opIndex != _jumpTargetOpIndex)
            return false;

        if (_jumpTargetPhaseIndex < 0)
            return phaseIndex < 0;

        return phaseIndex == _jumpTargetPhaseIndex;
    }

    private void PopulatePhaseComboBox(int operationIndex)
    {
        var phaseComboBox = GetJumpPhaseComboBox();
        var phaseOptions = BuildPhaseOptions(_currentJumpReceipt, operationIndex);
        SetComboOptions(phaseComboBox, PhaseOptionsObjectName, phaseOptions, DisabledOptionIndex);
        RefreshJumpButtonEnabled();
    }

    private void OnOperationComboUserSelectionChanged(object sender, UAEventArgs args)
    {
        if (_suppressOperationSelectionRefresh)
            return;

        var operationComboBox = sender as ComboBox ?? GetJumpOperationComboBox();
        int operationIndex = ResolveSelectedOperationIndex(operationComboBox);
        PopulatePhaseComboBox(operationIndex);
        RefreshJumpButtonEnabled();
    }

    private void OnPhaseComboUserSelectionChanged(object sender, UAEventArgs args)
    {
        if (args?.EventType?.BrowseName != "UserSelectionChanged")
            return;

        RefreshJumpButtonEnabled();
    }

    private void RefreshJumpButtonEnabled()
    {
        var operationButton = GetJumpOperationButton();
        if (operationButton != null)
            operationButton.Enabled = IsSelectedOperationJumpEnabled();

        var phaseButton = GetJumpPhaseButton();
        if (phaseButton != null)
            phaseButton.Enabled = IsSelectedPhaseJumpEnabled();
    }

    private bool IsSelectedOperationJumpEnabled()
    {
        var selectedOption = GetSelectedComboOption(GetJumpOperationComboBox());
        return ReadIntVariable(selectedOption?.GetVariable("OpIndex"), -1) >= 0;
    }

    private bool IsSelectedPhaseJumpEnabled()
    {
        var selectedOption = GetSelectedComboOption(GetJumpPhaseComboBox());
        return ReadIntVariable(selectedOption?.GetVariable("OpIndex"), -1) >= 0
            && ReadIntVariable(selectedOption?.GetVariable("PhaseIndex"), -1) >= 0;
    }

    private List<JumpComboOption> BuildOperationOptions(RecipeDatabaseTreeLoader.ReceiptNode receipt)
    {
        var options = new List<JumpComboOption>();
        AddDisabledOption(options);
        if (receipt?.Operations == null)
        {
            LogJumpOptions("Operation source", options);
            return options;
        }

        for (int opIndex = 0; opIndex < receipt.Operations.Count; opIndex++)
        {
            var operation = receipt.Operations[opIndex];
            options.Add(new JumpComboOption
            {
                Label = operation?.Name ?? $"Operation_{opIndex + 1}",
                OpIndex = opIndex,
                PhaseIndex = -1,
                OperationID = operation?.OperationID ?? 0,
                PhaseID = 0
            });
        }
        LogJumpOptions("Operation source", options);
        return options;
    }

    private List<JumpComboOption> BuildPhaseOptions(RecipeDatabaseTreeLoader.ReceiptNode receipt, int operationIndex)
    {
        var options = new List<JumpComboOption>();
        AddDisabledOption(options);
        if (receipt?.Operations == null || operationIndex < 0 || operationIndex >= receipt.Operations.Count)
        {
            LogJumpOptions($"Phase source OpIndex={operationIndex}", options);
            return options;
        }

        var operation = receipt.Operations[operationIndex];
        if (operation?.Phases == null)
        {
            LogJumpOptions($"Phase source OpIndex={operationIndex}", options);
            return options;
        }

        for (int phaseIndex = 0; phaseIndex < operation.Phases.Count; phaseIndex++)
        {
            var phase = operation.Phases[phaseIndex];
            options.Add(new JumpComboOption
            {
                Label = phase?.Name ?? $"Phase_{phaseIndex + 1}",
                OpIndex = operationIndex,
                PhaseIndex = phaseIndex,
                OperationID = operation?.OperationID ?? 0,
                PhaseID = phase?.PhaseID ?? 0
            });
        }
        LogJumpOptions($"Phase source OpIndex={operationIndex}", options);
        return options;
    }

    private static void AddDisabledOption(List<JumpComboOption> options)
    {
        options.Add(new JumpComboOption
        {
            Label = "Disabled",
            OpIndex = -1,
            PhaseIndex = -1,
            OperationID = 0,
            PhaseID = 0
        });
    }

    private void LogJumpOptions(string listName, List<JumpComboOption> options)
    {
        if (!_enableLog)
            return;

        int count = options?.Count ?? 0;
        Log.Info(LogCategory, $"{listName}: Count={count}");
        if (options == null)
            return;

        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            Log.Info(LogCategory, $"{listName}[{i}]: Label='{option.Label}', OpIndex={option.OpIndex}, PhaseIndex={option.PhaseIndex}, OperationID={option.OperationID}, PhaseID={option.PhaseID}");
        }
    }

    private int ResolveSelectedOperationIndex(ComboBox operationComboBox)
    {
        if (operationComboBox == null)
            return -1;

        var selectedOption = GetSelectedComboOption(operationComboBox);
        int operationIndex = ReadIntVariable(selectedOption?.GetVariable("OpIndex"), -1);
        if (operationIndex >= 0)
            return operationIndex;

        return -1;
    }

    private IUANode GetSelectedComboOption(ComboBox comboBox)
    {
        var selectedItemVariable = comboBox?.GetVariable("SelectedItem");
        if (selectedItemVariable?.Value?.Value is NodeId selectedNodeId && !selectedNodeId.IsEmpty)
            return InformationModel.Get(selectedNodeId);
        return null;
    }

    private void SetComboOptions(ComboBox comboBox, string optionsObjectName, List<JumpComboOption> options, int selectedOptionIndex)
    {
        if (comboBox == null)
            return;

        var optionsObject = LogicObject.GetObject(optionsObjectName);
        if (optionsObject == null)
        {
            optionsObject = InformationModel.MakeObject(optionsObjectName);
            LogicObject.Add(optionsObject);
        }

        foreach (var child in optionsObject.Children.ToList())
            child.Delete();

        if (_enableLog)
            Log.Info(LogCategory, $"SetComboOptions: ComboBox='{comboBox.BrowseName}', OptionsObject='{optionsObjectName}', Count={options?.Count ?? 0}, SelectedIndex={selectedOptionIndex}");

        NodeId selectedNodeId = NodeId.Empty;
        var usedBrowseNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < (options?.Count ?? 0); i++)
        {
            var option = options[i];
            var optionVariable = InformationModel.MakeVariable(CreateUniqueOptionBrowseName(option.Label, usedBrowseNames), OpcUa.DataTypes.LocalizedText);
            optionVariable.Value = new LocalizedText("", option.Label ?? "");
            EnsureIntVar(optionVariable, "OpIndex", option.OpIndex);
            EnsureIntVar(optionVariable, "PhaseIndex", option.PhaseIndex);
            EnsureIntVar(optionVariable, "OperationID", option.OperationID);
            EnsureIntVar(optionVariable, "PhaseID", option.PhaseID);
            optionsObject.Add(optionVariable);

            if (_enableLog)
                Log.Info(LogCategory, $"SetComboOptions: Added '{optionVariable.BrowseName}', Value='{option.Label}', OpIndex={option.OpIndex}, PhaseIndex={option.PhaseIndex}, OperationID={option.OperationID}, PhaseID={option.PhaseID}");

            if (i == selectedOptionIndex)
                selectedNodeId = optionVariable.NodeId;
        }

        if (selectedNodeId.IsEmpty && optionsObject.Children.Any())
            selectedNodeId = optionsObject.Children.First().NodeId;

        var modelVariable = comboBox.GetVariable("Model");
        if (modelVariable != null)
        {
            modelVariable.Value = optionsObject.NodeId;
            if (_enableLog)
                Log.Info(LogCategory, $"SetComboOptions: Model='{optionsObject.BrowseName}' NodeId='{optionsObject.NodeId}' assigned to ComboBox='{comboBox.BrowseName}'");
        }

        var selectedItemVariable = comboBox.GetVariable("SelectedItem");
        if (selectedItemVariable != null)
        {
            selectedItemVariable.Value = selectedNodeId;
            if (_enableLog)
                Log.Info(LogCategory, $"SetComboOptions: SelectedItem='{selectedNodeId}' assigned to ComboBox='{comboBox.BrowseName}'");
        }
        RefreshJumpButtonEnabled();
    }

    private static string CreateUniqueOptionBrowseName(string label, HashSet<string> usedBrowseNames)
    {
        string baseName = SafeName(label);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Item";

        string candidate = baseName;
        int suffix = 2;
        while (!usedBrowseNames.Add(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }
        return candidate;
    }

    private IUANode GetDialogRoot()
    {
        var current = LogicObject.Owner as IUANode;
        while (current != null)
        {
            if (string.Equals(current.BrowseName, "OperationPhaseJump", StringComparison.Ordinal))
                return current;
            current = current.Owner;
        }
        return FindPanelRoot(LogicObject.Owner as IUANode);
    }

    private ComboBox GetJumpOperationComboBox() => GetJumpPanelControl<ComboBox>("Panel1", "ComboBox1");
    private ComboBox GetJumpPhaseComboBox() => GetJumpPanelControl<ComboBox>("Panel2", "ComboBox1");
    private Button GetJumpOperationButton() => GetJumpPanelControl<Button>("Panel1", "Button1");
    private Button GetJumpPhaseButton() => GetJumpPanelControl<Button>("Panel2", "Button1");

    private T GetJumpPanelControl<T>(string panelName, string controlName) where T : class, IUANode
    {
        var panel = FindDescendantByBrowseName(GetDialogRoot(), panelName);
        return FindDescendantByBrowseName(panel, controlName) as T;
    }

    private void ApplyJumpPanelText()
    {
        SetDescendantLabelText(GetJumpPanelControl<Label>("Panel1", "Label1"), "Select Jump Operation");
        SetDescendantLabelText(GetJumpPanelControl<Label>("Panel2", "Label1"), "Select Jump Phase");

        var operationButton = GetJumpOperationButton();
        if (operationButton != null)
            operationButton.Text = "Jump Operation";
        var phaseButton = GetJumpPhaseButton();
        if (phaseButton != null)
            phaseButton.Text = "Jump Phase";
    }

    private static void SetDescendantLabelText(Label label, string text)
    {
        if (label != null)
            label.Text = text ?? "";
    }

    private Container GetTreeContainer()
    {
        var owner = LogicObject.Owner as IUAObject;
        var direct = owner?.Get<Container>("TreeContainer");
        if (direct != null) return direct;
        var panel = FindPanelRoot(owner);
        return FindDescendantByBrowseName(panel, "TreeContainer") as Container;
    }

    private static IUANode FindPanelRoot(IUANode start)
    {
        var current = start;
        while (current != null)
        {
            if (current is Panel || string.Equals(current.BrowseName, "OperationPhaseJump", StringComparison.Ordinal))
                return current;
            current = current.Owner;
        }
        return start;
    }

    private static IUANode FindDescendantByBrowseName(IUANode node, string browseName)
    {
        if (node == null || string.IsNullOrEmpty(browseName)) return null;
        foreach (var child in node.Children)
        {
            if (string.Equals(child.BrowseName, browseName, StringComparison.Ordinal))
                return child;
            var found = FindDescendantByBrowseName(child, browseName);
            if (found != null) return found;
        }
        return null;
    }

    private void ResolveTypeIds()
    {
        _receiptItemTypeId = FindType(Project.Current, "ReceiptListItem");
        _operationItemTypeId = FindType(Project.Current, "OperationListItem");
        _phaseItemTypeId = FindType(Project.Current, "PhaseListItem");
        var components = Project.Current?.GetObject(ComponentsFolderPath);
        if (_receiptItemTypeId == NodeId.Empty)
            _receiptItemTypeId = FindType(components, "ReceiptListItem");
        if (_operationItemTypeId == NodeId.Empty)
            _operationItemTypeId = FindType(components, "OperationListItem");
        if (_phaseItemTypeId == NodeId.Empty)
            _phaseItemTypeId = FindType(components, "PhaseListItem");
    }

    private static NodeId FindType(IUANode root, string typeName)
    {
        if (root == null) return NodeId.Empty;
        if (root.BrowseName == typeName && root.NodeClass == NodeClass.ObjectType)
            return root.NodeId;
        foreach (var child in root.Children)
        {
            var result = FindType(child, typeName);
            if (result != NodeId.Empty) return result;
        }
        return NodeId.Empty;
    }

    private void EnsureRecipeTreeLoaded()
    {
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) return;
        if (loader.Tree != null && loader.Tree.Count > 0) return;
        try { loader.LoadAllToTree(); }
        catch (Exception ex) { if (_enableLog) Log.Warning(LogCategory, $"LoadAllToTree: {ex.Message}"); }
    }

    private RecipeDatabaseTreeLoader.ReceiptNode FindReceiptForTree(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName)) return null;
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) return null;

        string target = recipeName.Trim();
        foreach (var receipt in loader.Tree ?? Enumerable.Empty<RecipeDatabaseTreeLoader.ReceiptNode>())
            if (string.Equals(receipt?.Name, target, StringComparison.OrdinalIgnoreCase))
                return receipt;

        int recipeId = ReadSnapshotInt("RecipeID", 0);
        if (recipeId > 0 && loader.ReceiptById != null && loader.ReceiptById.TryGetValue(recipeId, out var byId))
            return byId;

        return null;
    }

    private string ResolveActiveRecipeName()
    {
        string recipe = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "Recipe");
        return IsMeaningfulName(recipe) ? recipe.Trim() : "";
    }

    private static bool IsMeaningfulName(string name)
        => !string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), "None", StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentNameMeaningful(string name)
    {
        if (!IsMeaningfulName(name))
            return false;
        return !string.Equals(name.Trim(), "0", StringComparison.OrdinalIgnoreCase);
    }

    private static IUAObject GetBatchDownloadToPlcDataNode()
        => Project.Current?.GetObject(BatchDownloadToPlcDataPath) as IUAObject;

    private static IUAObject GetCurrentBatchDataBatchNode()
        => Project.Current?.GetObject(CurrentBatchDataBatchPath) as IUAObject;

    private static IUAObject GetCurrentBatchDataOperationNode()
        => Project.Current?.GetObject(CurrentBatchDataOperationPath) as IUAObject;

    private static string ReadCurrentOperationName()
    {
        string value = ReadStringVariable(GetCurrentBatchDataOperationNode(), "OperationName");
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static string ReadCurrentPhaseName()
    {
        string value = ReadStringVariable(GetCurrentBatchDataBatchNode(), "RunningPhaseName");
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static string ReadSnapshotBatchName()
    {
        string value = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "Name");
        return IsMeaningfulName(value) ? value.Trim() : "";
    }

    private static string ReadSnapshotDownloadStamp()
    {
        string value = ReadStringVariable(GetBatchDownloadToPlcDataNode(), "DownloadDateTime");
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static int ReadSnapshotFlowRefreshTick()
    {
        var snapshot = GetBatchDownloadToPlcDataNode();
        if (snapshot == null)
            return 0;

        var variable = snapshot.GetVariable("FlowRefreshTick") ?? snapshot.GetVariable("RecipeRefreshTick");
        return ReadIntVariable(variable, 0);
    }

    private static int ReadSnapshotInt(string variableName, int defaultValue)
        => ReadIntVariable(GetBatchDownloadToPlcDataNode()?.GetVariable(variableName), defaultValue);

    private static string ReadSnapshotText(string variableName)
    {
        string value = ReadStringVariable(GetBatchDownloadToPlcDataNode(), variableName);
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static bool ReadSnapshotBool(string variableName)
        => ReadBooleanVariable(GetBatchDownloadToPlcDataNode()?.GetVariable(variableName));

    private static string ReadStringVariable(IUAVariable variable)
    {
        if (variable?.Value == null) return "";
        object value = variable.Value.Value;
        if (value is LocalizedText lt) return lt.Text ?? "";
        return value?.ToString() ?? "";
    }

    private static string ReadStringVariable(IUAObject owner, string variableName)
        => ReadStringVariable(owner?.GetVariable(variableName));

    private static int ReadIntVariable(IUAVariable variable, int defaultValue)
    {
        if (variable?.Value == null) return defaultValue;
        try
        {
            object value = variable.Value.Value;
            if (value == null) return defaultValue;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch { return defaultValue; }
    }

    private static bool ReadBooleanVariable(IUAVariable variable)
    {
        if (variable?.Value == null) return false;
        try
        {
            object value = variable.Value.Value;
            if (value is bool b) return b;
            if (value == null) return false;
            if (value is string s)
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
        }
        catch { return false; }
    }

    private void ClearGeneratedRows(IUAObject host)
    {
        foreach (var child in host.Children.OfType<IUANode>().ToList())
            child.Delete();
    }

    private static bool IsGeneratedRow(IUANode node)
        => node is Container container && container.GetVariable("RunningTreeRow") != null;

    private static int CountGeneratedRows(IUAObject host)
    {
        if (host == null) return 0;
        int count = 0;
        foreach (var child in host.Children)
            if (IsGeneratedRow(child)) count++;
        return count;
    }

    private void ConfigureRow(
        Container item,
        float rowWidth,
        string text,
        string operationName,
        string phaseName,
        int opIndex,
        int phaseIndex,
        int receiptId,
        int operationId,
        int phaseId,
        TreeRowKind kind)
    {
        item.Visible = true;
        item.Width = rowWidth;
        item.Height = RowHeight;
        item.HorizontalAlignment = HorizontalAlignment.Stretch;
        SetItemButtonText(item, text);
        SetFlowIndices(item, opIndex, phaseIndex);
        SetFlowNames(item, operationName, phaseName);
        EnsureIntVar(item, "ReceiptID", receiptId);
        EnsureIntVar(item, "OperationID", operationId);
        EnsureIntVar(item, "PhaseID", phaseId);
        EnsureBoolVar(item, "RunningTreeRow", true);
        ApplyTreeRowStyle(item, kind, textColor: NormalTextColor);
        HideExpandButton(item);
    }

    private void AddMessageRow(IUAObject host, float rowWidth, string message)
    {
        var row = InformationModel.MakeObject("RunningTreeMessage", _phaseItemTypeId) as Container;
        if (row == null) return;
        ConfigureRow(row, rowWidth, message, "", "", -1, -1, 0, 0, 0, TreeRowKind.Message);
        ApplyRowColors(row, MessageTextColor, TransparentBg);
        host.Add(row);
    }

    private static bool IsRunningRowByName(string rowOperationName, string rowPhaseName, int phaseIndex, string runningOperationName, string runningPhaseName)
    {
        if (!NameEquals(rowOperationName, runningOperationName))
            return false;

        if (phaseIndex < 0)
            return true;

        return !string.IsNullOrWhiteSpace(runningPhaseName) && NameEquals(rowPhaseName, runningPhaseName);
    }

    private static bool NameEquals(string left, string right)
        => string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static Container GetRowHost(Container item) => item?.Get<Container>("Container");
    private static Container GetItemContainer(Container item) => GetRowHost(item)?.Get<Container>("ItemContainer");
    private static Button GetItemButton(Container item) => GetItemContainer(item)?.Get<Button>("ItemButton");
    private static Button GetExpandButton(Container item) => GetRowHost(item)?.Get<Button>("ExpandButton");

    private static void SetItemButtonText(Container item, string text)
    {
        var button = GetItemButton(item);
        if (button != null) button.Text = text ?? "";
    }

    private static void ApplyTreeRowStyle(Container item, TreeRowKind kind, Color textColor)
    {
        if (item == null) return;
        item.Height = RowHeight;
        item.HorizontalAlignment = HorizontalAlignment.Stretch;

        var host = GetRowHost(item);
        if (host != null)
        {
            host.Visible = true;
            host.Height = RowHeight;
            host.Width = Math.Max(180f, item.Width - (kind == TreeRowKind.Phase ? PhaseIndent : kind == TreeRowKind.Operation || kind == TreeRowKind.Message ? OperationIndent : ReceiptIndent));
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.LeftMargin = kind switch
            {
                TreeRowKind.Operation => OperationIndent,
                TreeRowKind.Phase => PhaseIndent,
                TreeRowKind.Message => OperationIndent,
                _ => ReceiptIndent
            };
        }

        var itemContainer = GetItemContainer(item);
        if (itemContainer != null)
        {
            itemContainer.Visible = true;
            itemContainer.Height = RowHeight;
            itemContainer.Width = Math.Max(120f, item.Width - host.LeftMargin - ExpandButtonSlotWidth);
            itemContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        var button = GetItemButton(item);
        if (button != null)
        {
            button.Visible = true;
            button.Height = RowHeight - 2f;
            button.Width = Math.Max(100f, item.Width - ExpandButtonSlotWidth - (host?.LeftMargin ?? 0f));
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyButtonLook(button, TransparentBg, textColor);
            SetNodeSize(button, "FontSize", FontSize);
            SetNodeSize(button, "ImageWidth", IconSize);
            SetNodeSize(button, "ImageHeight", IconSize);
            ClearBorder(button);
        }

        ClearBorder(item);
        ClearBorder(host);
        ClearBorder(itemContainer);
    }

    private static void ApplyRowColors(Container row, Color textColor, Color backgroundColor)
    {
        var button = GetItemButton(row);
        var host = GetRowHost(row);
        var itemContainer = GetItemContainer(row);

        ApplyNodeBackground(row, backgroundColor);
        ApplyNodeBackground(host, backgroundColor);
        ApplyNodeBackground(itemContainer, backgroundColor);

        if (button == null) return;
        ApplyButtonLook(button, backgroundColor, textColor);
        ClearBorderOnly(button);
    }

    private static void ApplyButtonLook(Button button, Color background, Color textColor)
    {
        button.BackgroundColor = background;
        button.TextColor = textColor;
        SetNodeColor(button, "BackgroundColor", background);
        SetNodeColor(button, "Color", background);
        SetNodeColor(button, "FillColor", background);
        SetNodeColor(button, "TextColor", textColor);
    }

    private static void ApplyNodeBackground(IUANode node, Color background)
    {
        if (node == null) return;
        SetNodeColor(node, "BackgroundColor", background);
        SetNodeColor(node, "Color", background);
        SetNodeColor(node, "FillColor", background);
        ClearBorderOnly(node);
    }

    private static void HideExpandButton(Container item)
    {
        var expand = GetExpandButton(item);
        if (expand == null) return;
        expand.Visible = false;
        var itemContainer = GetItemContainer(item);
        if (itemContainer != null)
            itemContainer.LeftMargin = (itemContainer.LeftMargin > 0 ? itemContainer.LeftMargin : 0) + ExpandButtonSlotWidth;
    }

    private static void SetFlowIndices(Container item, int opIndex, int phaseIndex)
    {
        EnsureIntVar(item, "FlowOpIndex", opIndex);
        EnsureIntVar(item, "FlowPhaseIndex", phaseIndex);
    }

    private static void SetFlowNames(Container item, string operationName, string phaseName)
    {
        EnsureStringVar(item, "FlowOperationName", operationName ?? "");
        EnsureStringVar(item, "FlowPhaseName", phaseName ?? "");
    }

    private static bool TryGetFlowIndices(Container item, out int opIndex, out int phaseIndex)
    {
        opIndex = -1;
        phaseIndex = -1;
        try
        {
            opIndex = ReadIntVariable(item?.GetVariable("FlowOpIndex"), -1);
            phaseIndex = ReadIntVariable(item?.GetVariable("FlowPhaseIndex"), -1);
            return true;
        }
        catch { return false; }
    }

    private static bool TryGetFlowNames(Container item, out string operationName, out string phaseName)
    {
        operationName = "";
        phaseName = "";
        try
        {
            operationName = ReadStringVariable(item?.GetVariable("FlowOperationName"));
            phaseName = ReadStringVariable(item?.GetVariable("FlowPhaseName"));
            return true;
        }
        catch { return false; }
    }

    private static void EnsureIntVar(IUANode owner, string name, int value)
    {
        if (owner == null) return;
        var variable = owner.GetVariable(name);
        if (variable == null)
        {
            variable = InformationModel.MakeVariable(name, OpcUa.DataTypes.Int32);
            owner.Add(variable);
        }
        variable.Value = value;
    }

    private static void EnsureBoolVar(IUANode owner, string name, bool value)
    {
        if (owner == null) return;
        var variable = owner.GetVariable(name);
        if (variable == null)
        {
            variable = InformationModel.MakeVariable(name, OpcUa.DataTypes.Boolean);
            owner.Add(variable);
        }
        variable.Value = value;
    }

    private static void EnsureStringVar(IUANode owner, string name, string value)
    {
        if (owner == null) return;
        var variable = owner.GetVariable(name);
        if (variable == null)
        {
            variable = InformationModel.MakeVariable(name, OpcUa.DataTypes.String);
            owner.Add(variable);
        }
        variable.Value = value ?? "";
    }

    private static void SetNodeColor(IUANode node, string variableName, Color color)
    {
        if (node == null) return;
        try
        {
            var variable = node.GetVariable(variableName);
            if (variable != null) variable.Value = color;
        }
        catch { }
    }

    private static void SetNodeSize(IUANode node, string variableName, float value)
    {
        if (node == null) return;
        try
        {
            var variable = node.GetVariable(variableName);
            if (variable != null) variable.Value = value;
        }
        catch { }
    }

    private static void ClearBorder(IUANode node)
    {
        ClearBorderOnly(node);
        SetNodeColor(node, "FillColor", TransparentBg);
    }

    private static void ClearBorderOnly(IUANode node)
    {
        SetNodeColor(node, "BorderColor", BorderNone);
        SetNodeSize(node, "BorderThickness", 0f);
    }

    private static void ApplyHostLayout(IUANode host)
    {
        if (host == null) return;
        try
        {
            if (host is Container container)
            {
                container.Visible = true;
                if (container.Width <= 0)
                    container.Width = Math.Max(280f, ReadNodeWidth(host.Owner) - 4f);
                if (container.Height <= 0)
                    container.Height = 120f;
                container.HorizontalAlignment = HorizontalAlignment.Stretch;
                container.VerticalAlignment = VerticalAlignment.Top;
            }
            var horizontalAlignment = host.GetVariable("HorizontalAlignment");
            if (horizontalAlignment != null)
                horizontalAlignment.Value = (int)HorizontalAlignment.Stretch;
            var verticalAlignment = host.GetVariable("VerticalAlignment");
            if (verticalAlignment != null)
                verticalAlignment.Value = (int)VerticalAlignment.Top;
        }
        catch { }
    }

    private static float GetRowWidth(Container host)
    {
        float width = ReadNodeWidth(host);
        if (width > 0) return Math.Max(180f, width - 16f);
        return 280f;
    }

    private static float ReadNodeWidth(IUANode node)
    {
        if (node == null) return 0f;
        try
        {
            if (node is Container container && container.Width > 0)
                return container.Width;
        }
        catch { }

        try
        {
            var variable = node.GetVariable("Width");
            object raw = variable?.Value?.Value;
            if (raw == null) return 0f;
            return Convert.ToSingle(raw, CultureInfo.InvariantCulture);
        }
        catch { return 0f; }
    }

    private static void ResizeTreeContainer(Container host)
    {
        if (host == null) return;
        float total = 0f;
        foreach (var child in host.Children)
        {
            if (child is Container container && container.Visible)
                total += container.Height > 0 ? container.Height : RowHeight;
        }
        host.Height = Math.Max(total + 8f, 120f);
    }

    private static string FormatRecipeKey(string recipe, string batch, string downloadStamp)
        => (recipe ?? "").Trim() + "|" + (batch ?? "").Trim() + "|" + (downloadStamp ?? "").Trim();

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Item";
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }

    private enum TreeRowKind
    {
        Receipt,
        Operation,
        Phase,
        Message
    }

    private class JumpComboOption
    {
        public string Label { get; set; }
        public int OpIndex { get; set; }
        public int PhaseIndex { get; set; }
        public int OperationID { get; set; }
        public int PhaseID { get; set; }
    }
}
