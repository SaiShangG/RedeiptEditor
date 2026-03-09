#region Using directives

using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
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
    [ExportMethod]
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
        if (EnableLog) Log.Info(LogCategory, $"SelectedID = {receiptId}");
    }

    /// <summary>Operation 按钮点击时调用，记录所属 Receipt 与当前 Operation，并刷新高亮。</summary>
    [ExportMethod]
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
        if (EnableLog) Log.Info(LogCategory, $"Selected Receipt={receiptId}, Operation={operationId}");
    }

    /// <summary>Phase 按钮点击时调用，记录所属 Receipt/Operation 与当前 Phase，并刷新高亮。</summary>
    [ExportMethod]
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
        if (EnableLog) Log.Info(LogCategory, $"Selected Receipt={receiptId}, Operation={operationId}, Phase={phaseId}");
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
        SetModelVar(selectedTreeData, "CurrentSelecteditemType", itemType);
        SetModelVar(selectedTreeData, "ReceiptName", receiptName ?? "");
        SetModelVar(selectedTreeData, "ReceiptCreatedDate", receiptCreatedDate ?? "");
        SetModelVar(selectedTreeData, "ReceiptCreatedBy", receiptCreatedBy ?? "");
        SetModelVar(selectedTreeData, "ReceiptCurrentStatus", receiptCurrentStatus ?? "");
        SetModelVar(selectedTreeData, "SelectedOperationName", selectedOpName ?? "");
        SetModelVar(selectedTreeData, "SelectedPhaseName", selectedPhaseName ?? "");
    }

    private IUANode ResolveSelectedTreeData()
    {
        IUANode model = LogicObject.Owner?.Get("Model");
        if (model == null) return null;
        var treeListData = model.Get("TreeListData");
        if (treeListData == null) return null;
        return treeListData.Get("SelectedTreeData");
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
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer")
            ?? LogicObject.Owner.Get("ScrollView1")?.Get<Container>("TreeContainer");
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
        // 1. 清理旧数据，防止每次点击重复生成
        foreach (var child in treeContainer.Children.OfType<ColumnLayout>().ToList())
        {
            child.Delete();
        }

        // 3. 读取三张表绑定的 NodeId 并获取表名（无 map 表）
        var receiptVar = LogicObject.GetVariable("ReceiptDB");
        var opVar = LogicObject.GetVariable("OperationDB");
        var phaseVar = LogicObject.GetVariable("PhaseDB");
        if (receiptVar == null || opVar == null || phaseVar == null)
        {
            if (EnableLog) Log.Error(LogCategory, "ReceiptDB/OperationDB/PhaseDB 变量未配置，请检查属性面板！");
            return;
        }
        var receiptNode = InformationModel.Get(receiptVar.Value);
        var opNode = InformationModel.Get(opVar.Value);
        var phaseNode = InformationModel.Get(phaseVar.Value);
        if (receiptNode == null || opNode == null || phaseNode == null)
        {
            if (EnableLog) Log.Error(LogCategory, "有数据库表 NodeId 未绑定或指向无效节点，请检查属性面板！");
            return;
        }

        string rTable = receiptNode.BrowseName;
        string oTable = opNode.BrowseName;
        string pTable = phaseNode.BrowseName;

        // 通过 Receipt 表节点向上回溯获取 Store 实例
        var store = GetStoreFromNode(receiptNode);
        if (store == null) return;

        // 4. 查找自定义 UI 组件的类型 NodeId (ObjectType)
        NodeId receiptTypeId = FindCustomTypeNodeId(Project.Current, "ReceiptListItem");
        NodeId opTypeId = FindCustomTypeNodeId(Project.Current, "OperationListItem");
        NodeId phaseTypeId = FindCustomTypeNodeId(Project.Current, "PhaseListItem");

        if (receiptTypeId == NodeId.Empty || opTypeId == NodeId.Empty || phaseTypeId == NodeId.Empty)
        {
            if (EnableLog) Log.Error(LogCategory, "未在项目中找到 ReceiptListItem, OperationListItem 或 PhaseListItem 类型，请检查名称是否完全一致！");
            return;
        }

        // 5. 第一层：查询 Receipts 表（含 Operations 列）
        string rSql = $"SELECT ReceiptID, Name, Sequence, Operations FROM {rTable} ORDER BY Sequence";
        store.Query(rSql, out _, out object[,] rResult);

        if (rResult == null) return;

        int rowCount = rResult.GetLength(0);

        #region 主流程：三层级 UI 生成
        for (int i = 0; i < rowCount; i++)
        {
            string rName = rResult[i, 1]?.ToString() ?? "Unknown";
            string operationsCsv = rResult[i, 3]?.ToString();
            var opIdList = ParseIdList(operationsCsv);

            int itemCount = 1;
            string containerName = (i == 0) ? "ListContainer" : $"ListContainer{i}";
            var listContainer = InformationModel.MakeObject<ColumnLayout>(containerName);
            listContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            listContainer.LeftMargin = 0;
            listContainer.TopMargin = 0;

            int receiptId = Convert.ToInt32(rResult[i, 0]);
            var rItem = InformationModel.MakeObject(rName + "_Item", receiptTypeId) as Container;
            SetItemButtonText(rItem, rName);
            SetReceiptItemIdAndClick(rItem, receiptId);
            SetReceiptButtonHighlight(rItem, receiptId);
            listContainer.Add(rItem);

            // 第二层：按 Receipts.Operations 逗号分隔 ID 查询 Operations 表
            if (opIdList.Count > 0)
            {
                string inClause = string.Join(",", opIdList);
                string oSql = $"SELECT OperationID, Name, Phases FROM {oTable} WHERE OperationID IN ({inClause})";
                store.Query(oSql, out _, out object[,] oResult);
                var opById = BuildOpDict(oResult);

                foreach (int oId in opIdList)
                {
                    if (!opById.TryGetValue(oId, out var opRow)) continue;
                    string oName = opRow.Name;
                    var oItem = InformationModel.MakeObject(oName + "_Item", opTypeId) as Container;
                    SetItemButtonText(oItem, oName);
                    SetOperationItemIdAndClick(oItem, receiptId, oId);
                    SetOperationButtonHighlight(oItem, oId);
                    listContainer.Add(oItem);
                    itemCount++;

                    // 第三层：按 Operations.Phases 逗号分隔 ID 查询 Phases 表
                    var phaseIdList = ParseIdList(opRow.Phases);
                    if (phaseIdList.Count > 0)
                    {
                        string pInClause = string.Join(",", phaseIdList);
                        string pSql = $"SELECT PhaseID, Name FROM {pTable} WHERE PhaseID IN ({pInClause})";
                        store.Query(pSql, out _, out object[,] pResult);
                        var phaseById = BuildPhaseDict(pResult);
                        foreach (int pId in phaseIdList)
                        {
                            if (!phaseById.TryGetValue(pId, out string pName)) pName = "Unknown";
                            var pItem = InformationModel.MakeObject(pName + "_Item", phaseTypeId) as Container;
                            SetItemButtonText(pItem, pName);
                            SetPhaseItemIdAndClick(pItem, receiptId, oId, pId);
                            SetPhaseButtonHighlight(pItem, pId);
                            listContainer.Add(pItem);
                            itemCount++;
                        }
                    }
                }
            }
            listContainer.Height = itemCount * 40;
            treeContainer.Add(listContainer);
        }
        #endregion

        if (EnableLog) Log.Info(LogCategory, $"成功读取 {rowCount} 条配方，树形列表生成完毕。");
    }

    #region 解析与查询
    private static List<int> ParseIdList(string csv)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string s in csv.Split(','))
        {
            if (int.TryParse(s?.Trim(), out int id)) list.Add(id);
        }
        return list;
    }

    private static Dictionary<int, (string Name, string Phases)> BuildOpDict(object[,] oResult)
    {
        var d = new Dictionary<int, (string, string)>();
        if (oResult == null) return d;
        for (int r = 0; r < oResult.GetLength(0); r++)
        {
            int id = Convert.ToInt32(oResult[r, 0]);
            string name = oResult[r, 1]?.ToString() ?? "";
            string phases = oResult[r, 2]?.ToString() ?? "";
            d[id] = (name, phases);
        }
        return d;
    }

    private static Dictionary<int, string> BuildPhaseDict(object[,] pResult)
    {
        var d = new Dictionary<int, string>();
        if (pResult == null) return d;
        for (int r = 0; r < pResult.GetLength(0); r++)
        {
            int id = Convert.ToInt32(pResult[r, 0]);
            string name = pResult[r, 1]?.ToString() ?? "";
            d[id] = name;
        }
        return d;
    }
    #endregion

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
            button.UAEvent -= (sender, args) => SetSelectedReceiptId(receiptId);
            button.UAEvent += (sender, args) => SetSelectedReceiptId(receiptId);
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
            button.UAEvent -= (s, a) => SetSelectedPhase(receiptId, operationId, phaseId);
            button.UAEvent += (s, a) => SetSelectedPhase(receiptId, operationId, phaseId);
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
            button.UAEvent -= (sender, args) => SetSelectedOperation(receiptId, operationId);
            button.UAEvent += (sender, args) => SetSelectedOperation(receiptId, operationId);
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
    private static readonly Color TransparentColor = new Color(0, 0xe4, 0xe4, 0xe4); // #e4e4e400 (A=0透明, R=G=B=228 灰)
    private static readonly Color HighlightColor = new Color(255, 255, 220, 150);
    private static readonly Color NormalColor = new Color(0x99, 0xde, 0xee, 0xff); // #deeeff99 (A,R,G,B)

    private Store GetStoreFromNode(IUANode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Store store) return store;
            current = current.Owner;
        }
        if (EnableLog) Log.Error(LogCategory, "无法从绑定的表节点向上找到 Store 数据库！");
        return null;
    }

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