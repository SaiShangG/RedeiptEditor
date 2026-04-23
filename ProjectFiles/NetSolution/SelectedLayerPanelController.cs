#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.WebUI;
#endregion

public class SelectedLayerPanelController : BaseNetLogic
{
    #region 按面板查找实例
    private static readonly Dictionary<NodeId, SelectedLayerPanelController> ByPanel = new();

    public static bool TryGetByPanel(NodeId panelNodeId, out SelectedLayerPanelController controller)
    {
        return ByPanel.TryGetValue(panelNodeId, out controller);
    }
    #endregion

    /// <summary>
    /// 面板显示类型：由 mode 与 isLeftPanel 共同决定。
    /// PhasesInOperation    — Operation 已选中，左侧面板，显示该 Operation 下的 Phase 列表。
    /// ReceiptsWithOperation — Operation 已选中，右侧面板，显示包含该 Operation 的 Receipt 列表。
    /// OperationsWithPhase  — Phase 已选中，右侧面板，显示包含该 Phase 的 Operation 列表。
    /// </summary>
    public enum PanelType
    {
        PhasesInOperation,
        ReceiptsWithOperation,
        OperationsWithPhase,
    }

    #region 数据结构
    public class DataItem
    {
        public PanelType Type { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Subtitle2 { get; set; }
        public List<SubDataItem> SubDataItems { get; set; } = new List<SubDataItem>();
    }

    public class SubDataItem
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
    }
    #endregion

    #region 挂的变量引用
    private IUAVariable _iconTypeVar;
    private IUAVariable _titleVar;
    private IUAVariable _typeVar;
    private IUAVariable _iconTextVar;
    private IUAVariable _iconText2Var;
    private IUAVariable _verticalLayoutNodeIdVar;       // UI 上垂直布局的节点 ID
    private IUAVariable _titleWithSubtitleComponentVar; // 组件模板，用于显示每条数据

    private NodeId _verticalLayoutNodeId = NodeId.Empty;
    private NodeId _titleWithSubtitleComponentNodeId = NodeId.Empty;
    #endregion

    #region 生命周期
    public override void Start()
    {
        var panel = LogicObject.Owner;
        if (panel != null) ByPanel[panel.NodeId] = this;

        _iconTypeVar = LogicObject.GetVariable("IconType");
        _titleVar = LogicObject.GetVariable("Title");
        _typeVar = LogicObject.GetVariable("Type");
        _iconTextVar = LogicObject.GetVariable("IconText");
        _iconText2Var = LogicObject.GetVariable("IconText2");

        _verticalLayoutNodeIdVar = LogicObject.GetVariable("VerticalLayout");
        if (_verticalLayoutNodeIdVar != null)
            _verticalLayoutNodeId = (_verticalLayoutNodeIdVar.Value.Value as NodeId) ?? NodeId.Empty;

        // yaml 中变量名为 TitleWithSubtitleComp
        _titleWithSubtitleComponentVar = LogicObject.GetVariable("TitleWithSubtitleComp");
        if (_titleWithSubtitleComponentVar != null)
            _titleWithSubtitleComponentNodeId = (_titleWithSubtitleComponentVar.Value.Value as NodeId) ?? NodeId.Empty;
    }

    public override void Stop()
    {
        var panel = LogicObject.Owner;
        if (panel != null) ByPanel.Remove(panel.NodeId);
    }
    #endregion

    #region 对外方法
    /**
     * @brief 根据 mode、item 和是否左侧面板更新面板显示内容。
     * @note 执行顺序：
     *   1. 根据 mode + isLeftPanel 确定 PanelType；
     *   2. 创建 DataItem 并从 TreeLoader 填充 SubDataItems；
     *   3. 设置 UI 变量（_titleVar, _typeVar, _iconTextVar, _iconText2Var）；
     *   4. 清空 VerticalLayout 并按 SubDataItems 创建 TitleWithSubtitle 子组件；
     *   5. 为每个子组件设置 Title 与 Description 变量。
     */
    public void UpdatePanelContent(string mode, string item, bool isLeftPanel)
    {
        // Step 1: 根据 mode + isLeftPanel 确定 PanelType
        PanelType panelType = DeterminePanelType(mode, isLeftPanel);

        // Step 2: 创建 DataItem 并获取 SubDataItems
        DataItem dataItem = BuildDataItem(panelType, item);

        // Step 3: 设置 UI 变量
        if (_typeVar != null) _typeVar.Value = mode ?? string.Empty;
        if (_titleVar != null) _titleVar.Value = dataItem.Title ?? string.Empty;
        if (_iconTextVar != null) _iconTextVar.Value = dataItem.Subtitle ?? "None";
        if (_iconText2Var != null) _iconText2Var.Value = dataItem.Subtitle2 ?? "None";

        // Step 4 & 5: 清空 VerticalLayout 并按 SubDataItems 重建子组件
        RefreshSubItemList(dataItem.SubDataItems);
    }
    #endregion

    #region 私有方法
    /**
     * @brief 根据 mode 和 isLeftPanel 映射到对应的 PanelType。
     * @note Operation+左侧 → PhasesInOperation；Operation+右侧 → ReceiptsWithOperation；
     *       其余（Phase 等）→ OperationsWithPhase。
     */
    private static PanelType DeterminePanelType(string mode, bool isLeftPanel)
    {
        if (string.Equals(mode, "Operation", StringComparison.OrdinalIgnoreCase))
            return isLeftPanel ? PanelType.PhasesInOperation : PanelType.ReceiptsWithOperation;
        return PanelType.OperationsWithPhase;
    }

    private static readonly Dictionary<PanelType, string> PanelTypeTitleMap = new()
    {
        { PanelType.PhasesInOperation, "Phases in selected operation:" },
        { PanelType.ReceiptsWithOperation, "Receipts which include this operation:" },
        { PanelType.OperationsWithPhase, "Operations which include this phase:" },
    };

    /**
     * @brief 构建 DataItem：按 panelType 设置标题文案，并从 RecipeDatabaseTreeLoader 缓存
     *        中依据 item 名称查找对应数据填充 SubDataItems。
     * @note 完全依赖 item 名称查缓存，与左侧 TreeList 的选中状态无关。
     *       若 TreeLoader 不可用或 item 名称无匹配，SubDataItems 保持空列表。
     */
    private static DataItem BuildDataItem(PanelType panelType, string item)
    {
        var loader = RecipeDatabaseTreeLoader.Instance;

        // 按 item 名称从缓存中查找对应节点的 Description 作为副标题
        string des = item ?? "None";
        if (loader != null && !string.IsNullOrEmpty(item))
        {
            if (panelType == PanelType.PhasesInOperation || panelType == PanelType.ReceiptsWithOperation)
            {
                var opNode = loader.FindOperationByName(item);
                if (opNode != null && !string.IsNullOrEmpty(opNode.Description))
                    des = opNode.Description;
            }
            else
            {
                var phNode = loader.FindPhaseByName(item);
                if (phNode != null && !string.IsNullOrEmpty(phNode.Description))
                    des = phNode.Description;
            }
        }

        var dataItem = new DataItem
        {
            Type = panelType,
            Title = PanelTypeTitleMap.TryGetValue(panelType, out var title) ? title : panelType.ToString(),
            Subtitle = item,
            Subtitle2 = des,
        };

        if (loader == null) return dataItem;

        switch (panelType)
        {
            case PanelType.PhasesInOperation:
                // 左侧面板：按 item 名称找到 Operation，列出其所有 Phase 
                foreach (var op in loader.OperationById.Values)
                {
                    if (!string.Equals(op.Name, item, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var ph in op.Phases)
                        dataItem.SubDataItems.Add(new SubDataItem { Title = ph.Name, Subtitle = ph.Description ?? "" });
                    break;
                }
                break;

            case PanelType.ReceiptsWithOperation:
                // 右侧面板（Operation 已选中）：按 item 名称找到包含该 Operation 的所有 Receipt
                foreach (var receipt in loader.Tree)
                {
                    if (receipt.Operations.Any(op => string.Equals(op.Name, item, StringComparison.OrdinalIgnoreCase)))
                        dataItem.SubDataItems.Add(new SubDataItem
                        {
                            Title = receipt.Name,
                            Subtitle = receipt.Description ?? ""
                        });
                }
                break;

            case PanelType.OperationsWithPhase:
                // 右侧面板（Phase 已选中）：按 item 名称找到包含该 Phase 的所有 Operation
                foreach (var op in loader.OperationById.Values)
                {
                    if (op.Phases.Any(ph => string.Equals(ph.Name, item, StringComparison.OrdinalIgnoreCase)))
                        dataItem.SubDataItems.Add(new SubDataItem { Title = op.Name, Subtitle = op.Description ?? "" });
                }
                break;
        }

        return dataItem;
    }

    /**
     * @brief 清空 VerticalLayout 下的所有子节点，再按 subItems 列表逐项创建
     *        TitleWithSubtitle 组件实例并设置 Title / Description 变量后挂载。
     * @note 若 _verticalLayoutNodeId 无效或类型 NodeId 查找失败，则静默返回。
     */
    private void RefreshSubItemList(List<SubDataItem> subItems)
    {
        if (_verticalLayoutNodeId == null || _verticalLayoutNodeId.IsEmpty) return;

        var layoutNode = InformationModel.Get(_verticalLayoutNodeId);
        if (layoutNode == null) return;

        // 清空已有子节点，避免重复累积
        foreach (var child in layoutNode.Children.ToList())
            child.Delete();

        if (subItems == null || subItems.Count == 0) return;

        // 通过类型名获取 TitleWithSubtitle 的类型 NodeId
        NodeId typeNodeId = FindCustomTypeNodeId(Project.Current, "TitleWithSubtitle");
        if (typeNodeId == NodeId.Empty) return;

        // 逐项创建组件实例并设置 Title / Description
        for (int i = 0; i < subItems.Count; i++)
        {
            var sub = subItems[i];
            var instance = InformationModel.MakeObject("TitleWithSubtitle_" + i, typeNodeId);
            if (instance == null) continue;

            var titleVar = instance.GetVariable("Title");
            if (titleVar != null) titleVar.Value = sub.Title ?? string.Empty;

            var descVar = instance.GetVariable("Description");
            if (descVar != null) descVar.Value = sub.Subtitle ?? string.Empty;

            layoutNode.Add(instance);
        }
    }

    /**
     * @brief 递归在 root 下查找 BrowseName 为 typeName 的自定义类型节点 ID。
     * @note 排除命名空间索引为 0 的内置节点，只返回项目自定义类型。
     */
    private static NodeId FindCustomTypeNodeId(IUANode root, string typeName)
    {
        if (root == null) return NodeId.Empty;
        if (string.Equals(root.BrowseName, typeName, StringComparison.OrdinalIgnoreCase)
            && root.NodeId.NamespaceIndex != 0)
            return root.NodeId;

        foreach (var child in root.Children)
        {
            var result = FindCustomTypeNodeId(child, typeName);
            if (!result.IsEmpty) return result;
        }
        return NodeId.Empty;
    }
    #endregion
}
