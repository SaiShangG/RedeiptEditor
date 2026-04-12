#region Using directives

using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.CoreBase;
using System;
using System.Collections.Generic;
using System.Linq;
using FTOptix.EventLogger;
using FTOptix.RecipeX;

#endregion

public class GenerateTreeList : BaseNetLogic
{
    #region LOG
    private const string LogCategory = "GenerateTreeList";
    private static bool EnableLog => _enableLog;
    private static bool _enableLog = true;  // 可在 Logic 下添加变量 EnableLog(bool) 覆盖
    #endregion

    public static GenerateTreeList Instance { get; private set; }

    /// <summary>当前选中的配方 ReceiptID（Receipt 按钮点击时通过 SetSelectedReceiptId 记录）</summary>
    public int SelectedReceiptId => _selectedReceiptId;
    private int _selectedReceiptId;

    /// <summary>当前选中的工序 OperationID（Operation 按钮点击时通过 SetSelectedOperation 记录，同时会设置 SelectedReceiptId 为其所属配方）</summary>
    public int SelectedOperationId => _selectedOperationId;
    private int _selectedOperationId;

    /// <summary>当前选中的阶段 PhaseID（Phase 按钮点击时通过 SetSelectedPhase 记录）</summary>
    public int SelectedPhaseId => _selectedPhaseId;
    private int _selectedPhaseId;

    #region 树展开折叠（与 Components 中 ExpandButton 一致）
    private readonly Dictionary<int, bool> _treeReceiptExpanded = new Dictionary<int, bool>();
    private readonly Dictionary<int, bool> _treeOperationExpanded = new Dictionary<int, bool>();
    /// <summary>右箭头资源；展开时 Rotation=90°，折叠时 Rotation=0。</summary>
    private const string ExpandButtonImagePath = "ns=7;%PROJECTDIR%/Right-sm.svg";
    /// <summary>与模板 ExpandButton Width 一致；无展开时按钮隐藏不占位，此项加在 ItemContainer.LeftMargin 上以对齐右侧内容。</summary>
    private const float ExpandButtonSlotWidth = 25f;
    #endregion

    public override void Start()
    {
        try { var v = LogicObject.GetVariable("EnableLog"); if (v != null) _enableLog = (bool)v.Value; } catch { }
        if (Instance == null) Instance = this;
        if (EnableLog) Log.Info(LogCategory, "Start");
        TryWireDevReleasedFilterButtons();
        ApplyDevReleasedFilterBarVisuals();
        Generate();
    }
    public override void Stop()
    {
        UnwireDevReleasedFilterButtons();
        Instance = null;
        if (EnableLog) Log.Info(LogCategory, "Stop");
    }

    /// <summary>Receipt 按钮点击时调用，传入自己的 ReceiptID，记录为 SelectedID，并刷新高亮。</summary>
    public void SetSelectedReceiptId(int receiptId)
    {
        _selectedReceiptId = receiptId;
        _selectedOperationId = 0;
        _selectedPhaseId = 0;
        var v = LogicObject.GetVariable("SelectedID");
        if (v != null) v.Value = receiptId;
        ApplyReceiptHighlight();
        ApplyOperationHighlight();
        ApplyPhaseHighlight();
        SyncSelectedItemToModel();
        GenerateOperationPhaseListPanel.Instance?.RefreshIfModeChanged();
        RecipeDatabaseManager.Instance?.RefreshTreeMoveButtonsEnabled();
        RecipeDatabaseTreeLoader.Instance?.LoadPhaseParametersToPhaseUIBuffer(0);
        if (EnableLog) Log.Info(LogCategory, $"Receipt  点击，SelectedID = {receiptId}");
    }

    /// <summary>Operation 按钮点击时调用，记录所属 Receipt 与当前 Operation，并刷新高亮。</summary>
    public void SetSelectedOperation(int receiptId, int operationId)
    {
        _selectedReceiptId = receiptId;
        _selectedOperationId = operationId;
        _selectedPhaseId = 0;
        var v = LogicObject.GetVariable("SelectedID");
        if (v != null) v.Value = receiptId;
        ApplyReceiptHighlight();
        ApplyOperationHighlight();
        ApplyPhaseHighlight();
        SyncSelectedItemToModel();
        GenerateOperationPhaseListPanel.Instance?.RefreshIfModeChanged();
        RecipeDatabaseManager.Instance?.RefreshTreeMoveButtonsEnabled();
        RecipeDatabaseTreeLoader.Instance?.LoadPhaseParametersToPhaseUIBuffer(0);
        if (EnableLog) Log.Info(LogCategory, $"Operation 按钮点击，Selected Receipt={receiptId}, Operation={operationId}");
    }

    /// <summary>Phase 按钮点击时调用，记录所属 Receipt/Operation 与当前 Phase，并刷新高亮。</summary>
    public void SetSelectedPhase(int receiptId, int operationId, int phaseId)
    {
        _selectedReceiptId = receiptId;
        _selectedOperationId = operationId;
        _selectedPhaseId = phaseId;
        var v = LogicObject.GetVariable("SelectedID");
        if (v != null) v.Value = receiptId;
        ApplyReceiptHighlight();
        ApplyOperationHighlight();
        ApplyPhaseHighlight();
        SyncSelectedItemToModel();
        GenerateOperationPhaseListPanel.Instance?.RefreshIfModeChanged();
        RecipeDatabaseManager.Instance?.RefreshTreeMoveButtonsEnabled();
        RecipeDatabaseTreeLoader.Instance?.LoadPhaseParametersToPhaseUIBuffer(phaseId);
        if (EnableLog) Log.Info(LogCategory, $"Phase 按钮点击，Selected Receipt={receiptId}, Operation={operationId}, Phase={phaseId}");
    }

    private void SyncSelectedItemToModel()
    {
        var selectedTreeData = ResolveSelectedTreeData();
        if (selectedTreeData == null) return;
        string itemType = "", receiptName = "", receiptCreatedDate = "", receiptCreatedBy = "", receiptCurrentStatus = "", selectedOpName = "", selectedPhaseName = "";
        if (RecipeDatabaseManager.Instance == null || !RecipeDatabaseManager.Instance.GetSelectedItemModelData(
            out itemType, out receiptName, out receiptCreatedDate, out receiptCreatedBy,
            out receiptCurrentStatus, out selectedOpName, out selectedPhaseName))
            return;
        SetModelVar(selectedTreeData, "CurrentSelectedItemType", itemType);
        SetModelVar(selectedTreeData, "SelectedReceiptName", receiptName ?? "");
        SetModelVar(selectedTreeData, "SelectedReceiptCreatedDate", receiptCreatedDate ?? "");
        SetModelVar(selectedTreeData, "SelectedReceiptCreatedBy", receiptCreatedBy ?? "");
        SetModelVar(selectedTreeData, "SelectedReceiptCurrentStatus", receiptCurrentStatus ?? "");
        SetModelVar(selectedTreeData, "SelectedOperationName", selectedOpName ?? "");
        SetModelVar(selectedTreeData, "SelectedPhaseName", selectedPhaseName ?? "");
    }

    /// <summary>Model 是 NodeId 变量，直接指向 SelectedTreeData 对象，解析后即可使用。</summary>
    private IUANode ResolveSelectedTreeData()
    {
        var modelVar = LogicObject.Owner?.GetVariable("Model");
        if (modelVar == null) return null;
        return InformationModel.Get(modelVar.Value);
    }

    /// <summary>供 <see cref="RecipeDatabaseManager.RefreshTreeMoveButtonsEnabled"/> 写入 EnableUp/EnableDown。</summary>
    public IUANode GetSelectedTreeDataNode()
    {
        return ResolveSelectedTreeData();
    }

    /// <summary>下拉暂存与模型绑定一致：<c>SelectedReceiptCurrentStatus</c>；保存前由 <see cref="RecipeDatabaseManager"/> 读取写入 DB Status。</summary>
    public string ReadSelectedReceiptCurrentStatusFromModel()
    {
        var n = ResolveSelectedTreeData();
        if (n == null) return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        var v = n.GetVariable("SelectedReceiptCurrentStatus");
        string s = RecipeDatabaseManager.GetPlainStringFromVariableValue(v);
        if (string.IsNullOrWhiteSpace(s) || s == "0")
            return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        return s;
    }

    private static void SetModelVar(IUANode node, string name, string value)
    {
        if (node == null || string.IsNullOrEmpty(name)) return;
        var v = node.GetVariable(name);
        if (v != null) v.Value = value;
    }

    #region 搜索过滤（关键字由 UI 事件参数传入，不解析控件树）
    private string _receiptNameSearchFilter = "";

    /// <summary>与库中 Status 一致：Development / Released（不区分大小写匹配行数据）。</summary>
    private string _receiptStatusFilter = RecipeDatabaseTreeLoader.DefaultReceiptStatus;

    private bool _filterDialogRecipeMultiActive;
    private bool _fdIncludeDevelopment;
    private bool _fdIncludeReleased;
    private bool _fdIncludeDiscarded;

    private const string ReleasedStatusFilter = "Released";

    private Button _devFilterButton;
    private Button _releasedFilterButton;

    private static readonly Color FilterBarSelectedBg = new Color(255, 0x05, 0x52, 0x88);
    private static readonly Color FilterBarUnselectedBg = new Color(255, 0xf1, 0xf5, 0xf9);
    private static readonly Color FilterBarSelectedText = new Color(255, 255, 255, 255);
    private static readonly Color FilterBarUnselectedText = new Color(255, 0, 0, 0);

    private static List<RecipeDatabaseTreeLoader.ReceiptNode> FilterReceiptsByName(
        List<RecipeDatabaseTreeLoader.ReceiptNode> tree, string filter)
    {
        if (tree == null || tree.Count == 0) return new List<RecipeDatabaseTreeLoader.ReceiptNode>();
        if (string.IsNullOrWhiteSpace(filter)) return tree.ToList();
        string f = filter.Trim();
        return tree.Where(r => r.Name != null && r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private static string NormalizeReceiptStatusForFilter(RecipeDatabaseTreeLoader.ReceiptNode r)
    {
        if (r == null) return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        string s = r.Status?.Trim();
        if (string.IsNullOrEmpty(s) || s == "0")
            return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        return s;
    }

    private static List<RecipeDatabaseTreeLoader.ReceiptNode> FilterReceiptsByStatus(
        List<RecipeDatabaseTreeLoader.ReceiptNode> receipts, string requiredStatus)
    {
        if (receipts == null || receipts.Count == 0) return receipts ?? new List<RecipeDatabaseTreeLoader.ReceiptNode>();
        string need = requiredStatus?.Trim() ?? "";
        if (string.IsNullOrEmpty(need)) return receipts;
        return receipts.Where(r =>
            string.Equals(NormalizeReceiptStatusForFilter(r), need, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private bool ReceiptMatchesFilterDialogMulti(RecipeDatabaseTreeLoader.ReceiptNode r)
    {
        string s = NormalizeReceiptStatusForFilter(r);
        bool any = _fdIncludeDevelopment || _fdIncludeReleased || _fdIncludeDiscarded;
        if (!any) return true;
        if (_fdIncludeDevelopment && string.Equals(s, RecipeDatabaseTreeLoader.DefaultReceiptStatus, StringComparison.OrdinalIgnoreCase))
            return true;
        if (_fdIncludeReleased && string.Equals(s, ReleasedStatusFilter, StringComparison.OrdinalIgnoreCase))
            return true;
        if (_fdIncludeDiscarded && string.Equals(s, "Discarded", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private bool StatusFilterIsDevelopment() =>
        string.Equals(_receiptStatusFilter, RecipeDatabaseTreeLoader.DefaultReceiptStatus, StringComparison.OrdinalIgnoreCase);

    private void ApplyDevReleasedFilterBarVisuals()
    {
        if (_devFilterButton == null || _releasedFilterButton == null) return;
        bool dev = StatusFilterIsDevelopment();
        _devFilterButton.BackgroundColor = dev ? FilterBarSelectedBg : FilterBarUnselectedBg;
        _devFilterButton.TextColor = dev ? FilterBarSelectedText : FilterBarUnselectedText;
        _releasedFilterButton.BackgroundColor = !dev ? FilterBarSelectedBg : FilterBarUnselectedBg;
        _releasedFilterButton.TextColor = !dev ? FilterBarSelectedText : FilterBarUnselectedText;
    }

    private static string BrowseNameTail(IUANode node)
    {
        string bn = node?.BrowseName?.ToString() ?? "";
        int c = bn.LastIndexOf(':');
        return c >= 0 && c < bn.Length - 1 ? bn.Substring(c + 1) : bn;
    }

    private static IUAObject FindChildObjectByBrowseTail(IUANode parent, string tail)
    {
        if (parent == null || string.IsNullOrEmpty(tail)) return null;
        foreach (var ch in parent.Children)
        {
            if (string.Equals(BrowseNameTail(ch), tail, StringComparison.OrdinalIgnoreCase))
                return ch as IUAObject;
        }
        return null;
    }

    private static Button TryGetButtonWithIconInnerButton(IUAObject wrap)
    {
        if (wrap == null) return null;
        return wrap.Get<Button>("Button1") ?? wrap.GetObject("Button1") as Button;
    }

    /// <summary>在左侧面板 <c>VerticalLayout1</c> 下定位 Dev/Released，订阅点击（无需改 yaml）。</summary>
    private void TryWireDevReleasedFilterButtons()
    {
        UnwireDevReleasedFilterButtons();
        try
        {
            IUANode vertical = LogicObject.Owner?.Owner?.Owner;
            var row = FindChildObjectByBrowseTail(vertical, "DevReleasedContainer");
            var devWrap = FindChildObjectByBrowseTail(row, "Dev");
            var relWrap = FindChildObjectByBrowseTail(row, "Released");
            _devFilterButton = TryGetButtonWithIconInnerButton(devWrap);
            _releasedFilterButton = TryGetButtonWithIconInnerButton(relWrap);
            if (_devFilterButton != null)
                _devFilterButton.UAEvent += OnDevFilterButtonClicked;
            if (_releasedFilterButton != null)
                _releasedFilterButton.UAEvent += OnReleasedFilterButtonClicked;
            if ((_devFilterButton == null || _releasedFilterButton == null) && EnableLog)
                Log.Warning(LogCategory, "Dev/Released 筛选按钮未找到（期望路径: TreeList1 父级 ColumnLayout / DevReleasedContainer）。");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TryWireDevReleasedFilterButtons: {ex.Message}");
        }
    }

    private void UnwireDevReleasedFilterButtons()
    {
        if (_devFilterButton != null)
        {
            _devFilterButton.UAEvent -= OnDevFilterButtonClicked;
            _devFilterButton = null;
        }
        if (_releasedFilterButton != null)
        {
            _releasedFilterButton.UAEvent -= OnReleasedFilterButtonClicked;
            _releasedFilterButton = null;
        }
    }

    private void OnDevFilterButtonClicked(object sender, UAEventArgs a)
    {
        if (a?.EventType?.BrowseName != "MouseClickEvent") return;
        _receiptStatusFilter = RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        ApplyDevReleasedFilterBarVisuals();
        RunGenerateTreeContainer();
    }

    private void OnReleasedFilterButtonClicked(object sender, UAEventArgs a)
    {
        if (a?.EventType?.BrowseName != "MouseClickEvent") return;
        _receiptStatusFilter = ReleasedStatusFilter;
        ApplyDevReleasedFilterBarVisuals();
        RunGenerateTreeContainer();
    }

    /// <summary>供面板事件绑定（可选）；逻辑与点击 Dev 相同。</summary>
    [ExportMethod]
    public void FilterTreeListByDevelopmentStatus()
    {
        _receiptStatusFilter = RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        ApplyDevReleasedFilterBarVisuals();
        RunGenerateTreeContainer();
    }

    /// <summary>供面板事件绑定（可选）；逻辑与点击 Released 相同。</summary>
    [ExportMethod]
    public void FilterTreeListByReleasedStatus()
    {
        _receiptStatusFilter = ReleasedStatusFilter;
        ApplyDevReleasedFilterBarVisuals();
        RunGenerateTreeContainer();
    }
    #endregion

    /// <summary>全量重建树（清除名称过滤）。保存、刷新数据等内部调用。</summary>
    [ExportMethod]
    public void Generate()
    {
        _receiptNameSearchFilter = "";
        RunGenerateTreeContainer();
    }

    /// <summary>按配方名包含关系过滤并重建树。将 TextBox「Modified text」绑定到此方法，并把当前文本作为参数传入。</summary>
    [ExportMethod]
    public void RefreshTreeListBySearchText(string filterText)
    {
        _receiptNameSearchFilter = filterText?.Trim() ?? "";
        RunGenerateTreeContainer();
    }

    /// <summary>若事件只能绑定 LocalizedText（与 Text 变量类型一致），可选此重载。</summary>
    [ExportMethod]
    public void RefreshTreeListBySearchLocalizedText(LocalizedText filterText)
    {
        _receiptNameSearchFilter = filterText?.Text?.Trim() ?? "";
        RunGenerateTreeContainer();
    }

    /// <summary>FilterDialog：按配方名 + 多选状态（Development / Released / Discarded）重建树。</summary>
    [ExportMethod]
    public void ApplyFilterDialogRecipe(string nameFilter, bool includeDevelopment, bool includeReleased, bool includeDiscarded)
    {
        _receiptNameSearchFilter = nameFilter?.Trim() ?? "";
        _filterDialogRecipeMultiActive = true;
        _fdIncludeDevelopment = includeDevelopment;
        _fdIncludeReleased = includeReleased;
        _fdIncludeDiscarded = includeDiscarded;
        RunGenerateTreeContainer();
    }

    private void RunGenerateTreeContainer()
    {
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer");
        if (treeContainer == null)
        {
            if (EnableLog) Log.Error(LogCategory, "未找到 TreeContainer 节点！");
            _filterDialogRecipeMultiActive = false;
            return;
        }

        bool wasVisible = treeContainer.Visible;
        try
        {
            treeContainer.Visible = false;
            GenerateCore(treeContainer);
        }
        finally
        {
            treeContainer.Visible = wasVisible;
            RecipeDatabaseManager.Instance?.RefreshTreeMoveButtonsEnabled();
            _filterDialogRecipeMultiActive = false;
        }
    }

    /// <summary>内部：在 TreeContainer 已隐藏时执行清空与重建，避免闪烁。</summary>
    private void GenerateCore(Container treeContainer)
    {
        foreach (var child in treeContainer.Children.OfType<ColumnLayout>().ToList())
            child.Delete();

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null)
        {
            if (EnableLog) Log.Error(LogCategory, "RecipeDatabaseTreeLoader 未就绪，跳过生成");
            return;
        }

        NodeId receiptTypeId = FindCustomTypeNodeId(Project.Current, "ReceiptListItem");
        NodeId opTypeId = FindCustomTypeNodeId(Project.Current, "OperationListItem");
        NodeId phaseTypeId = FindCustomTypeNodeId(Project.Current, "PhaseListItem");
        if (receiptTypeId == NodeId.Empty || opTypeId == NodeId.Empty || phaseTypeId == NodeId.Empty)
        {
            if (EnableLog) Log.Error(LogCategory, "未在项目中找到 ReceiptListItem, OperationListItem 或 PhaseListItem 类型，请检查名称是否完全一致！");
            return;
        }

        var tree = loader.Tree;
        string filterText = _receiptNameSearchFilter ?? "";
        var visibleReceipts = FilterReceiptsByName(tree, filterText);
        if (_filterDialogRecipeMultiActive)
            visibleReceipts = visibleReceipts.Where(ReceiptMatchesFilterDialogMulti).ToList();
        else
            visibleReceipts = FilterReceiptsByStatus(visibleReceipts, _receiptStatusFilter);

        if (_selectedReceiptId != 0 && !visibleReceipts.Exists(r => r.ReceiptID == _selectedReceiptId))
        {
            _selectedReceiptId = 0;
            _selectedOperationId = 0;
            _selectedPhaseId = 0;
            var selV = LogicObject.GetVariable("SelectedID");
            if (selV != null) selV.Value = 0;
        }

        #region 主流程：三层级 UI 生成
        for (int i = 0; i < visibleReceipts.Count; i++)
        {
            var rNode = visibleReceipts[i];
            string containerName = (i == 0) ? "ListContainer" : $"ListContainer{i}";
            var listContainer = InformationModel.MakeObject<ColumnLayout>(containerName);
            listContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            listContainer.LeftMargin = 0;
            listContainer.TopMargin = 0;

            var rItem = InformationModel.MakeObject(rNode.Name + "_Item", receiptTypeId) as Container;
            string rDisplayName = (loader.IsDirtyReceipt(rNode.ReceiptID) ? "*" : "") + rNode.Name;
            SetItemButtonText(rItem, rDisplayName);
            SetReceiptItemIdAndClick(rItem, rNode.ReceiptID);
            SetReceiptButtonHighlight(rItem, rNode.ReceiptID);
            listContainer.Add(rItem);
            WireReceiptTreeExpandUi(rItem, rNode.ReceiptID, CountReceiptDescendantRows(rNode) > 1);

            foreach (var opNode in rNode.Operations)
            {
                var oItem = InformationModel.MakeObject(opNode.Name + "_Item", opTypeId) as Container;
                string oDisplayName = (loader.IsDirtyOperation(opNode.OperationID) ? "*" : "") + opNode.Name;
                SetItemButtonText(oItem, oDisplayName);
                SetOperationItemIdAndClick(oItem, rNode.ReceiptID, opNode.OperationID);
                SetOperationButtonHighlight(oItem, opNode.OperationID);
                listContainer.Add(oItem);
                WireOperationTreeExpandUi(oItem, opNode.OperationID, opNode.Phases.Count > 1);

                foreach (var phNode in opNode.Phases)
                {
                    var pItem = InformationModel.MakeObject(phNode.Name + "_Item", phaseTypeId) as Container;
                    string pDisplayName = (loader.IsDirtyPhase(phNode.PhaseID) ? "*" : "") + phNode.Name;
                    SetItemButtonText(pItem, pDisplayName);
                    SetPhaseItemIdAndClick(pItem, rNode.ReceiptID, opNode.OperationID, phNode.PhaseID);
                    SetPhaseButtonHighlight(pItem, phNode.PhaseID);
                    listContainer.Add(pItem);
                    HideTreeExpandButton(pItem);
                }
            }
            treeContainer.Add(listContainer);
        }
        #endregion

        ApplyTreeExpandCollapseVisuals(treeContainer);

        if (EnableLog)
        {
            string st = _receiptStatusFilter ?? "";
            if (string.IsNullOrWhiteSpace(filterText))
                Log.Info(LogCategory, $"成功读取 {visibleReceipts.Count} 条配方（状态筛选={st}），树形列表生成完毕。");
            else
                Log.Info(LogCategory, $"按名称「{filterText}」+ 状态「{st}」：显示 {visibleReceipts.Count}/{tree.Count} 条配方。");
        }

        if (visibleReceipts.Count == 0)
        {
            SyncSelectedItemToModel();
            return;
        }

        if (_selectedReceiptId == 0 || !visibleReceipts.Exists(r => r.ReceiptID == _selectedReceiptId))
            SetSelectedReceiptId(visibleReceipts[0].ReceiptID);
    }

    #region 辅助方法

    #region 树列表项 UI 路径（Receipt/Operation/PhaseListItem：Container → ItemContainer → ItemButton，ExpandButton 与 ItemContainer 同级）
    private static Container GetTreeListRowHost(Container listItem) =>
        listItem?.Get<Container>("Container");

    private static Container GetTreeListItemContainer(Container listItem) =>
        GetTreeListRowHost(listItem)?.Get<Container>("ItemContainer");

    private static Button GetTreeListItemButton(Container listItem) =>
        GetTreeListItemContainer(listItem)?.Get<Button>("ItemButton");

    private static Button GetTreeListExpandButton(Container listItem) =>
        GetTreeListRowHost(listItem)?.Get<Button>("ExpandButton");
    #endregion

    private void SetItemButtonText(Container listItem, string textValue)
    {
        var button = GetTreeListItemButton(listItem);
        if (button != null) button.Text = textValue;
    }

    /// <summary>设置 Receipt 项的 ReceiptID 变量，并订阅按钮点击以调用 SetSelectedReceiptId。</summary>
    private void SetReceiptItemIdAndClick(Container listItem, int receiptId)
    {
        if (listItem == null) return;
        var v = listItem.GetVariable("ReceiptID");
        if (v != null) v.Value = receiptId;
        var button = GetTreeListItemButton(listItem);
        if (button != null)
        {
            button.UAEvent -= ReceiptButtonClicked;
            button.UAEvent += ReceiptButtonClicked;
        }
        void ReceiptButtonClicked(object s, UAEventArgs a)
        {
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            SetSelectedReceiptId(receiptId);
            MiddlePanelManager.Instance?.NotifyTitleClicked("Empty", "");
        }
    }

    /// <summary>Generate 时：仅当选中的是 Receipt（未选 Operation/Phase）且为当前配方时才高亮。</summary>
    private void SetReceiptButtonHighlight(Container listItem, int receiptId)
    {
        if (listItem == null) return;
        bool highlightReceipt = (_selectedOperationId == 0 && _selectedPhaseId == 0 && receiptId == _selectedReceiptId);
        var button = GetTreeListItemButton(listItem);
        if (button != null)
            button.BackgroundColor = highlightReceipt ? HighlightColor : NormalColor;
    }

    /// <summary>遍历 TreeContainer 内所有配方项。选中 Operation/Phase 时不高亮 Receipt；仅选中 Receipt 时高亮该 Receipt。</summary>
    private void ApplyReceiptHighlight()
    {
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer")
            ?? LogicObject.Owner.Get("ScrollView1")?.Get<Container>("TreeContainer");
        if (treeContainer == null) return;
        bool highlightReceipt = (_selectedOperationId == 0 && _selectedPhaseId == 0);
        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            if (col.Children.Count == 0) continue;
            var rItem = col.Children[0] as Container;
            var receiptVar = rItem?.GetVariable("ReceiptID");
            if (receiptVar == null) continue;
            if (!int.TryParse(receiptVar.Value, out int rid)) continue;
            var button = GetTreeListItemButton(rItem);
            if (button != null)
                button.BackgroundColor = (highlightReceipt && rid == _selectedReceiptId) ? HighlightColor : NormalColor;
        }
    }

    /// <summary>设置 Phase 项的 ReceiptID/OperationID/PhaseID 变量，并订阅点击以调用 SetSelectedPhase。</summary>
    private void SetPhaseItemIdAndClick(Container listItem, int receiptId, int operationId, int phaseId)
    {
        if (listItem == null) return;
        EnsureSetInt32Variable(listItem, "ReceiptID", receiptId);
        EnsureSetInt32Variable(listItem, "OperationID", operationId);
        EnsureSetInt32Variable(listItem, "PhaseID", phaseId);
        var button = GetTreeListItemButton(listItem);
        if (button != null)
        {
            button.UAEvent -= PhaseButtonClicked;
            button.UAEvent += PhaseButtonClicked;
        }
        void PhaseButtonClicked(object s, UAEventArgs a)
        {
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            SetSelectedPhase(receiptId, operationId, phaseId);

            MiddlePanelManager.Instance?.NotifyTitleClicked("Empty", "");
        }
    }

    /// <summary>Generate 时设置 Phase 项按钮颜色：选中项为 HighlightColor，否则为 TransparentColor。</summary>
    private void SetPhaseButtonHighlight(Container listItem, int phaseId)
    {
        if (listItem == null) return;
        var button = GetTreeListItemButton(listItem);
        if (button != null)
            button.BackgroundColor = (phaseId == _selectedPhaseId) ? HighlightColor : TransparentColor;
    }

    /// <summary>设置 Operation 项的 ReceiptID/OperationID 变量，并订阅点击以调用 SetSelectedOperation。</summary>
    private void SetOperationItemIdAndClick(Container listItem, int receiptId, int operationId)
    {
        if (listItem == null) return;
        EnsureSetInt32Variable(listItem, "ReceiptID", receiptId);
        EnsureSetInt32Variable(listItem, "OperationID", operationId);
        var button = GetTreeListItemButton(listItem);
        if (button != null)
        {
            button.UAEvent -= OperationButtonClicked;
            button.UAEvent += OperationButtonClicked;
            if (EnableLog) Log.Info(LogCategory, $"OperationButton event wired for item: {listItem?.BrowseName ?? "<null>"}");
        }

        void OperationButtonClicked(object sender, UAEventArgs a)
        {
            // 仅响应鼠标点击，忽略键盘等其它触发
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            SetSelectedOperation(receiptId, operationId);

            MiddlePanelManager.Instance?.NotifyTitleClicked("Empty", "");
        }
    }

    /// <summary>Generate 时设置 Operation 项按钮颜色：仅当未选 Phase 且为选中 Operation 时高亮。</summary>
    private void SetOperationButtonHighlight(Container listItem, int operationId)
    {
        if (listItem == null) return;
        var button = GetTreeListItemButton(listItem);
        if (button != null)
            button.BackgroundColor = (_selectedPhaseId == 0 && operationId == _selectedOperationId) ? HighlightColor : TransparentColor;
    }

    /// <summary>遍历树中所有 Operation 项：先全部恢复默认，再仅当未选 Phase 时将当前选中的 Operation 高亮。</summary>
    private void ApplyOperationHighlight()
    {
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer")
            ?? LogicObject.Owner.Get("ScrollView1")?.Get<Container>("TreeContainer");
        if (treeContainer == null) return;
        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            for (int i = 1; i < col.Children.Count; i++)
            {
                var opNode = col.Children[i] as Container;
                var opVar = opNode?.GetVariable("OperationID");
                if (opVar == null) continue;
                var pVar = opNode?.GetVariable("PhaseID");
                if (pVar != null) continue;
                if (!int.TryParse(opVar.Value, out int oid)) continue;
                var button = GetTreeListItemButton(opNode);
                if (button != null) button.BackgroundColor = TransparentColor;
            }
        }
        if (_selectedPhaseId != 0 || _selectedOperationId == 0) return;
        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            for (int i = 1; i < col.Children.Count; i++)
            {
                var opNode = col.Children[i] as Container;
                var opVar = opNode?.GetVariable("OperationID");
                if (opVar == null) continue;
                var pVar = opNode?.GetVariable("PhaseID");
                if (pVar != null) continue;
                if (!int.TryParse(opVar.Value, out int oid)) continue;
                if (oid != _selectedOperationId) continue;
                var button = GetTreeListItemButton(opNode);
                if (button != null) button.BackgroundColor = HighlightColor;
                return;
            }
        }
    }

    /// <summary>遍历树中所有 Phase 项：先全部恢复默认，再仅将当前选中的 Phase 高亮。</summary>
    private void ApplyPhaseHighlight()
    {
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer")
            ?? LogicObject.Owner.Get("ScrollView1")?.Get<Container>("TreeContainer");
        if (treeContainer == null) return;
        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            for (int i = 1; i < col.Children.Count; i++)
            {
                var node = col.Children[i] as Container;
                var pVar = node?.GetVariable("PhaseID");
                if (pVar == null) continue;
                if (!int.TryParse(pVar.Value, out int pid)) continue;
                var button = GetTreeListItemButton(node);
                if (button != null) button.BackgroundColor = TransparentColor;
            }
        }
        if (_selectedPhaseId == 0) return;
        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            for (int i = 1; i < col.Children.Count; i++)
            {
                var node = col.Children[i] as Container;
                var pVar = node?.GetVariable("PhaseID");
                if (pVar == null) continue;
                if (!int.TryParse(pVar.Value, out int pid)) continue;
                if (pid != _selectedPhaseId) continue;
                var button = GetTreeListItemButton(node);
                if (button != null) button.BackgroundColor = HighlightColor;
                return;
            }
        }
    }

    #region 树展开折叠逻辑（ExpandButton）
    private static int CountReceiptDescendantRows(RecipeDatabaseTreeLoader.ReceiptNode r)
    {
        if (r?.Operations == null) return 0;
        int n = 0;
        foreach (var op in r.Operations)
            n += 1 + (op.Phases?.Count ?? 0);
        return n;
    }

    private void WireReceiptTreeExpandUi(Container listItem, int receiptId, bool showButton)
    {
        var btn = GetTreeListExpandButton(listItem);
        if (btn == null) return;
        ConfigureExpandButtonAndItemMargin(listItem, btn, showButton);
        if (!showButton) return;
        btn.UAEvent -= ReceiptExpandClicked;
        btn.UAEvent += ReceiptExpandClicked;
        SetExpandButtonExpandedLook(btn, TreeReceiptExpanded(receiptId));

        void ReceiptExpandClicked(object sender, UAEventArgs a)
        {
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            _treeReceiptExpanded[receiptId] = !TreeReceiptExpanded(receiptId);
            var tc = ResolveTreeContainer();
            if (tc != null) ApplyTreeExpandCollapseVisuals(tc);
        }
    }

    private void WireOperationTreeExpandUi(Container listItem, int operationId, bool showButton)
    {
        var btn = GetTreeListExpandButton(listItem);
        if (btn == null) return;
        ConfigureExpandButtonAndItemMargin(listItem, btn, showButton);
        if (!showButton) return;
        btn.UAEvent -= OperationExpandClicked;
        btn.UAEvent += OperationExpandClicked;
        SetExpandButtonExpandedLook(btn, TreeOperationExpanded(operationId));

        void OperationExpandClicked(object sender, UAEventArgs a)
        {
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            _treeOperationExpanded[operationId] = !TreeOperationExpanded(operationId);
            var tc = ResolveTreeContainer();
            if (tc != null) ApplyTreeExpandCollapseVisuals(tc);
        }
    }

    private static void HideTreeExpandButton(Container listItem)
    {
        var btn = GetTreeListExpandButton(listItem);
        if (btn == null) return;
        ConfigureExpandButtonAndItemMargin(listItem, btn, false);
    }

    /// <summary>有子项：显示 ExpandButton；无子项：隐藏不占位，并把 <c>ItemContainer.LeftMargin</c> 增加槽宽，与有按钮行右侧对齐。</summary>
    private static void ConfigureExpandButtonAndItemMargin(Container listItem, Button btn, bool interactive)
    {
        if (btn == null) return;
        var ic = GetTreeListItemContainer(listItem);
        float baseLeft = ic?.LeftMargin ?? 0f;
        EnsureSetBoolVariable(btn, "IsHasSubItem", interactive);
        if (interactive)
        {
            btn.Visible = true;
            btn.Width = ExpandButtonSlotWidth;
            if (ic != null) ic.LeftMargin = baseLeft;
        }
        else
        {
            btn.Visible = false;
            if (ic != null) ic.LeftMargin = baseLeft + ExpandButtonSlotWidth;
        }
    }

    private static void EnsureSetBoolVariable(IUANode item, string name, bool value)
    {
        if (item == null || string.IsNullOrEmpty(name)) return;
        var v = item.GetVariable(name);
        if (v == null)
        {
            v = InformationModel.MakeVariable(name, OpcUa.DataTypes.Boolean);
            item.Add(v);
        }
        v.Value = value;
    }

    private static void SetExpandButtonExpandedLook(Button btn, bool expanded)
    {
        if (btn == null) return;
        var ip = btn.GetVariable("ImagePath");
        if (ip != null)
            ip.Value = ExpandButtonImagePath;
        SetExpandButtonRotationDegrees(btn, expanded ? 90f : 0f);
    }

    private static void SetExpandButtonRotationDegrees(Button btn, float degrees)
    {
        if (btn == null) return;
        btn.Rotation = degrees;
        var rot = btn.GetVariable("Rotation");
        if (rot != null)
            rot.Value = degrees;
        else
        {
            rot = InformationModel.MakeVariable("Rotation", OpcUa.DataTypes.Float);
            btn.Add(rot);
            rot.Value = degrees;
        }
    }

    private bool TreeReceiptExpanded(int receiptId) =>
        !_treeReceiptExpanded.TryGetValue(receiptId, out bool v) || v;

    private bool TreeOperationExpanded(int operationId) =>
        !_treeOperationExpanded.TryGetValue(operationId, out bool v) || v;

    private Container ResolveTreeContainer()
    {
        return LogicObject.Owner?.Get<Container>("TreeContainer")
            ?? LogicObject.Owner?.Get("ScrollView1")?.Get<Container>("TreeContainer");
    }

    private static bool TryReadVariableInt(IUAVariable v, out int id)
    {
        id = 0;
        if (v?.Value == null) return false;
        try
        {
            object raw = v.Value.Value;
            if (raw is int i) { id = i; return true; }
            return int.TryParse(raw?.ToString(), out id);
        }
        catch { return false; }
    }

    private static bool TryReadRowIds(Container row, out int receiptId, out int operationId, out int phaseId)
    {
        receiptId = operationId = phaseId = 0;
        if (row == null) return false;
        var rv = row.GetVariable("ReceiptID");
        var ov = row.GetVariable("OperationID");
        var pv = row.GetVariable("PhaseID");
        if (rv != null) TryReadVariableInt(rv, out receiptId);
        if (ov != null) TryReadVariableInt(ov, out operationId);
        if (pv != null) TryReadVariableInt(pv, out phaseId);
        return true;
    }

    private void ApplyTreeExpandCollapseVisuals(Container treeContainer)
    {
        if (treeContainer == null) return;

        foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
        {
            if (col.Children.Count == 0) continue;
            var rItem = col.Children[0] as Container;
            var rVar = rItem?.GetVariable("ReceiptID");
            if (rVar == null || !TryReadVariableInt(rVar, out int receiptId) || receiptId <= 0) continue;

            bool rExp = TreeReceiptExpanded(receiptId);
            UpdateTreeExpandButtonGlyph(rItem, rExp);

            for (int i = 1; i < col.Children.Count; i++)
            {
                if (!(col.Children[i] is Container row)) continue;
                TryReadRowIds(row, out _, out int opId, out int phId);

                if (phId > 0)
                    row.Visible = rExp && TreeOperationExpanded(opId);
                else if (opId > 0)
                {
                    row.Visible = rExp;
                    UpdateTreeExpandButtonGlyph(row, TreeOperationExpanded(opId));
                }
                else
                    row.Visible = rExp;
            }

            int visibleRows = 0;
            foreach (var ch in col.Children)
            {
                if (ch is Panel p && p.Visible) visibleRows++;
            }
            col.Height = Math.Max(visibleRows, 1) * 40f;
        }

        if (treeContainer is ColumnLayout columnLayout)
        {
            float totalHeight = 0;
            foreach (var c in treeContainer.Children.OfType<ColumnLayout>())
                totalHeight += c.Height + 5;
            columnLayout.Height = totalHeight + 50;
        }
    }

    private static void UpdateTreeExpandButtonGlyph(Container listItem, bool expanded)
    {
        var btn = GetTreeListExpandButton(listItem);
        if (btn == null || !btn.Visible) return;
        var hasSub = btn.GetVariable("IsHasSubItem");
        if (hasSub?.Value == null) return;
        object raw = hasSub.Value.Value;
        if (raw is not bool sub || !sub) return;
        SetExpandButtonExpandedLook(btn, expanded);
    }
    #endregion

    private static readonly Color TransparentColor = new Color(0, 0xe4, 0xe4, 0xe4);
    private static readonly Color HighlightColor = new Color(255, 255, 220, 150);
    private static readonly Color NormalColor = new Color(0x99, 0xde, 0xee, 0xff);

    private NodeId FindCustomTypeNodeId(IUANode root, string typeName)
    {
        if (root.BrowseName == typeName && root.NodeClass == NodeClass.ObjectType) return root.NodeId;
        foreach (var child in root.Children)
        {
            var result = FindCustomTypeNodeId(child, typeName);
            if (result != NodeId.Empty) return result;
        }
        return NodeId.Empty;
    }

    private static void EnsureSetInt32Variable(Container item, string name, int value)
    {
        var v = item.GetVariable(name);
        if (v == null)
        {
            v = InformationModel.MakeVariable(name, OpcUa.DataTypes.Int32);
            item.Add(v);
        }
        v.Value = value;
    }

    #endregion
}
