#region Using directives

using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using System;
using System.Linq;

#endregion

public class GenerateTreeList : BaseNetLogic
{
    [ExportMethod]
    public void Generate()
    {
        // 1. 定位父级 UI 容器 TreeContainer
        var treeContainer = LogicObject.Owner.Get<Container>("TreeContainer");
        if (treeContainer == null)
        {
            Log.Error("GenerateTreeList", "未找到 TreeContainer 节点！");
            return;
        }

        // 2. 清理旧数据，防止每次点击重复生成
        foreach (var child in treeContainer.Children.OfType<ColumnLayout>().ToList())
        {
            child.Delete();
        }

        // 3. 读取所有数据库表绑定的 NodeId 并获取表名
        var receiptNode = InformationModel.Get(LogicObject.GetVariable("ReceiptDB").Value);
        var opNode = InformationModel.Get(LogicObject.GetVariable("OperationDB").Value);
        var phaseNode = InformationModel.Get(LogicObject.GetVariable("PhaseDB").Value);
        var roMapNode = InformationModel.Get(LogicObject.GetVariable("R_O_MapDB").Value);
        var opMapNode = InformationModel.Get(LogicObject.GetVariable("O_P_MapDB").Value);

        if (receiptNode == null || opNode == null || phaseNode == null || roMapNode == null || opMapNode == null)
        {
            Log.Error("GenerateTreeList", "有数据库表 NodeId 未绑定，请检查属性面板！");
            return;
        }

        string rTable = receiptNode.BrowseName;
        string oTable = opNode.BrowseName;
        string pTable = phaseNode.BrowseName;
        string roMapTable = roMapNode.BrowseName;
        string opMapTable = opMapNode.BrowseName;

        // 通过 Receipt 表节点向上回溯获取 Store 实例
        var store = GetStoreFromNode(receiptNode);
        if (store == null) return;

        // 4. 查找自定义 UI 组件的类型 NodeId (ObjectType)
        NodeId receiptTypeId = FindCustomTypeNodeId(Project.Current, "ReceiptListItem");
        NodeId opTypeId = FindCustomTypeNodeId(Project.Current, "OperationListItem");
        NodeId phaseTypeId = FindCustomTypeNodeId(Project.Current, "PhaseListItem");

        if (receiptTypeId == NodeId.Empty || opTypeId == NodeId.Empty || phaseTypeId == NodeId.Empty)
        {
            Log.Error("GenerateTreeList", "未在项目中找到 ReceiptListItem, OperationListItem 或 PhaseListItem 类型，请检查名称是否完全一致！");
            return;
        }

        // 5. 第一层：查询 Receipts 表
        string rSql = $"SELECT ReceiptID, Name FROM {rTable} ORDER BY Sequence";
        store.Query(rSql, out _, out object[,] rResult);

        if (rResult == null) return;

        int rowCount = rResult.GetLength(0);

        // 6. 开始循环生成三层级 UI
        for (int i = 0; i < rowCount; i++)
        {
            int rId = Convert.ToInt32(rResult[i, 0]);
            string rName = rResult[i, 1]?.ToString() ?? "Unknown";

            // 统计当前配方下所有的 item 个数（Receipt 本身算 1 个）
            int itemCount = 1;

            string containerName = (i == 0) ? "ListContainer" : $"ListContainer{i}";
            var listContainer = InformationModel.MakeObject<ColumnLayout>(containerName);
            listContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            listContainer.LeftMargin = 0;
            listContainer.TopMargin = 0;

            // --- 实例化 ReceiptListItem ---
            var rItem = InformationModel.MakeObject(rName + "_Item", receiptTypeId) as Container;
            // 赋值文本给里面的 Button1
            SetItemButtonText(rItem, rName);
            listContainer.Add(rItem);

            // --- 第二层：联合查询当前 Receipt 下的 Operations ---
            string oSql = $"SELECT {oTable}.OperationID, {oTable}.Name FROM {oTable} INNER JOIN {roMapTable} ON {oTable}.OperationID = {roMapTable}.OperationID WHERE {roMapTable}.ReceiptID = {rId} ORDER BY {roMapTable}.Sequence";

            store.Query(oSql, out _, out object[,] oResult);

            if (oResult != null)
            {
                for (int j = 0; j < oResult.GetLength(0); j++)
                {
                    int oId = Convert.ToInt32(oResult[j, 0]);
                    string oName = oResult[j, 1]?.ToString() ?? "Unknown";

                    // 实例化 OperationListItem
                    var oItem = InformationModel.MakeObject(oName + "_Item", opTypeId) as Container;
                    // 赋值文本给里面的 Button1
                    SetItemButtonText(oItem, oName);
                    listContainer.Add(oItem);
                    itemCount++; // 增加总数

                    // --- 第三层：联合查询当前 Operation 下的 Phases ---
                    string pSql = $"SELECT {pTable}.PhaseID, {pTable}.Name FROM {pTable} INNER JOIN {opMapTable} ON {pTable}.PhaseID = {opMapTable}.PhaseID WHERE {opMapTable}.OperationID = {oId} ORDER BY {opMapTable}.Sequence";

                    store.Query(pSql, out _, out object[,] pResult);

                    if (pResult != null)
                    {
                        for (int k = 0; k < pResult.GetLength(0); k++)
                        {
                            string pName = pResult[k, 1]?.ToString() ?? "Unknown";

                            // 实例化 PhaseListItem
                            var pItem = InformationModel.MakeObject(pName + "_Item", phaseTypeId) as Container;
                            // 赋值文本给里面的 Button1
                            SetItemButtonText(pItem, pName);
                            listContainer.Add(pItem);
                            itemCount++; // 增加总数
                        }
                    }
                }
            }
            // 7. 动态设置 ListContainer 的高度 (Receipt 本身 + 所有 Op + 所有 Phase 的总数 * 40)
            listContainer.Height = itemCount * 40;

            // 将组合好的配方块添加到全局树容器
            treeContainer.Add(listContainer);
        }

        Log.Info("GenerateTreeList", $"成功读取 {rowCount} 条配方，树形列表生成完毕。");
    }

    // ================= 辅助方法 ================= //

    // 辅助方法 1：设置自定义组件内部 Button1 的文本
    private void SetItemButtonText(Container listItem, string textValue)
    {
        if (listItem == null) return;

        // 根据截图结构，先找到里层的 ItemContainer
        var itemContainer = listItem.Get<Container>("ItemContainer");
        if (itemContainer != null)
        {
            // 再从 ItemContainer 里面找到 Button1
            var button = itemContainer.Get<Button>("Button1");
            if (button != null)
            {
                button.Text = textValue;
            }
        }
    }

    // 辅助方法 2：通过 Table 节点递归向上寻找 Store 根节点
    private Store GetStoreFromNode(IUANode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Store store) return store;
            current = current.Owner;
        }
        Log.Error("GenerateTreeList", "无法从绑定的表节点向上找到 Store 数据库！");
        return null;
    }

    // 辅助方法 3：全局递归搜索，找到自定义 UI 组件的基础类型 (ObjectType) NodeId
    private NodeId FindCustomTypeNodeId(IUANode root, string typeName)
    {
        if (root.BrowseName == typeName && root.NodeClass == NodeClass.ObjectType)
        {
            return root.NodeId;
        }

        foreach (var child in root.Children)
        {
            var result = FindCustomTypeNodeId(child, typeName);
            if (result != NodeId.Empty) return result;
        }
        return NodeId.Empty;
    }
}