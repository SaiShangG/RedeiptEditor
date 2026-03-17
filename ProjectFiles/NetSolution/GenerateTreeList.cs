#region Using directives

using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using System;
using System.Collections.Generic;
using System.Linq;

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

    public override void Start()
    {
        try { var v = LogicObject.GetVariable("EnableLog"); if (v != null) _enableLog = (bool)v.Value; } catch { }
        if (Instance == null) Instance = this;
        if (EnableLog) Log.Info(LogCategory, "Start");
        Generate();
    }
    public override void Stop()
    {
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

    private static void SetModelVar(IUANode node, string name, string value)
    {
        if (node == null || string.IsNullOrEmpty(name)) return;
        var v = node.GetVariable(name);
        if (v != null) v.Value = value;
    }

    [ExportMethod]
    public void Generate()
    {
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer");
        if (treeContainer == null)
        {
            if (EnableLog) Log.Error(LogCategory, "未找到 TreeContainer 节点！");
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

        #region 主流程：三层级 UI 生成
        for (int i = 0; i < tree.Count; i++)
        {
            var rNode = tree[i];
            int itemCount = 1;
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

            foreach (var opNode in rNode.Operations)
            {
                var oItem = InformationModel.MakeObject(opNode.Name + "_Item", opTypeId) as Container;
                string oDisplayName = (loader.IsDirtyOperation(opNode.OperationID) ? "*" : "") + opNode.Name;
                SetItemButtonText(oItem, oDisplayName);
                SetOperationItemIdAndClick(oItem, rNode.ReceiptID, opNode.OperationID);
                SetOperationButtonHighlight(oItem, opNode.OperationID);
                listContainer.Add(oItem);
                itemCount++;

                foreach (var phNode in opNode.Phases)
                {
                    var pItem = InformationModel.MakeObject(phNode.Name + "_Item", phaseTypeId) as Container;
                    string pDisplayName = (loader.IsDirtyPhase(phNode.PhaseID) ? "*" : "") + phNode.Name;
                    SetItemButtonText(pItem, pDisplayName);
                    SetPhaseItemIdAndClick(pItem, rNode.ReceiptID, opNode.OperationID, phNode.PhaseID);
                    SetPhaseButtonHighlight(pItem, phNode.PhaseID);
                    listContainer.Add(pItem);
                    itemCount++;
                }
            }
            listContainer.Height = itemCount * 40;
            treeContainer.Add(listContainer);
        }
        #endregion

        #region 根据子布局高度之和设置 TreeContainer 高度
        var columnLayout = treeContainer as ColumnLayout;
        if (columnLayout != null)
        {
            float totalHeight = 0;
            foreach (var col in treeContainer.Children.OfType<ColumnLayout>())
                totalHeight += col.Height + 5;
            columnLayout.Height = totalHeight + 50;
        }
        #endregion

        if (EnableLog) Log.Info(LogCategory, $"成功读取 {tree.Count} 条配方，树形列表生成完毕。");

        // 若当前无选中项，自动选中第一个 Receipt
        if (_selectedReceiptId == 0 && tree.Count > 0)
            SetSelectedReceiptId(tree[0].ReceiptID);
    }

    #region 辅助方法
    private void SetItemButtonText(Container listItem, string textValue)
    {
        if (listItem == null) return;
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
        if (button != null) button.Text = textValue;
    }

    /// <summary>设置 Receipt 项的 ReceiptID 变量，并订阅按钮点击以调用 SetSelectedReceiptId。</summary>
    private void SetReceiptItemIdAndClick(Container listItem, int receiptId)
    {
        if (listItem == null) return;
        var v = listItem.GetVariable("ReceiptID");
        if (v != null) v.Value = receiptId;
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
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
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
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
            var itemContainer = rItem.Get<Container>("ItemContainer");
            var button = itemContainer?.Get<Button>("ItemButton");
            if (button != null)
                button.BackgroundColor = (highlightReceipt && rid == _selectedReceiptId) ? HighlightColor : NormalColor;
        }
    }

    /// <summary>设置 Phase 项的 ReceiptID/OperationID/PhaseID 变量，并订阅点击以调用 SetSelectedPhase。</summary>
    private void SetPhaseItemIdAndClick(Container listItem, int receiptId, int operationId, int phaseId)
    {
        if (listItem == null) return;
        var rv = listItem.GetVariable("ReceiptID");
        if (rv != null) rv.Value = receiptId;
        var ov = listItem.GetVariable("OperationID");
        if (ov != null) ov.Value = operationId;
        var pv = listItem.GetVariable("PhaseID");
        if (pv != null) pv.Value = phaseId;
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
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
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
        if (button != null)
            button.BackgroundColor = (phaseId == _selectedPhaseId) ? HighlightColor : TransparentColor;
    }

    /// <summary>设置 Operation 项的 ReceiptID/OperationID 变量，并订阅点击以调用 SetSelectedOperation。</summary>
    private void SetOperationItemIdAndClick(Container listItem, int receiptId, int operationId)
    {
        if (listItem == null) return;
        var rv = listItem.GetVariable("ReceiptID");
        if (rv != null) rv.Value = receiptId;
        var ov = listItem.GetVariable("OperationID");
        if (ov != null) ov.Value = operationId;
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
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
        var itemContainer = listItem.Get<Container>("ItemContainer");
        var button = itemContainer?.Get<Button>("ItemButton");
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
                var itemContainer = opNode?.Get<Container>("ItemContainer");
                var button = itemContainer?.Get<Button>("ItemButton");
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
                var itemContainer = opNode?.Get<Container>("ItemContainer");
                var button = itemContainer?.Get<Button>("ItemButton");
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
                var itemContainer = node?.Get<Container>("ItemContainer");
                var button = itemContainer?.Get<Button>("ItemButton");
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
                var itemContainer = node?.Get<Container>("ItemContainer");
                var button = itemContainer?.Get<Button>("ItemButton");
                if (button != null) button.BackgroundColor = HighlightColor;
                return;
            }
        }
    }
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
    #endregion
}