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

/// <summary>
/// 右侧面板列表生成器，复用于 Operation List 与 Operation Phase 两种模式。
/// 挂在 GenerateOperationPhaseListPanel 下的 Logic，通过 Logic 变量 ListMode("Operation"/"Phase") 控制模式。
///
/// Optix UI 层级（与图一致）：
///   RightContainer
///   └── Background
///       └── OperationPhaseTemplatePanel1
///           ├── Columns
///           │   ├── Label1               ← 标题 "Operation List" / "Operation Phase"
///           │   └── ButtonWithIcon1       ← "+ Create New"，点击 OnCreateNew
///           │   └── SearchBox (TextBox)  可选，搜索过滤
///           ├── ColumnsCards （vertical layout）
///                  ← 动态生成 OperationPhaseCard 实例
///           └── GenerateOperationPhaseListPanel  ← 本 Logic 的 Owner
///               
///               
///
/// 自定义类型 OperationPhaseCard 结构（与 Optix Studio 中一致）：
///   OperationPhaseCard (Container)
///   ├── Variables: ItemId(Int32), ReceiptID(Int32), OperationID(Int32)
///   ├── Rectangle1
///       └─── VerticalLayout1
///           └─── ButtonWithIcon1     ← 标题（如 "Operation"）
///           └─── Row
///                 ├─── ComboBox1       ← 版本选择
///                 └─── ButtonWithIcon1 ← "+Insert"，点击 OnInsert
/// </summary>
public class GenerateOperationPhaseListPanel : BaseNetLogic
{
    #region 日志
    private const string LogCategory = "GenerateRightPannelList";
    private static bool _enableLog = true;
    private static bool EnableLog => _enableLog;
    #endregion

    public static GenerateOperationPhaseListPanel Instance { get; private set; }

    #region 生命周期
    public override void Start()
    {
        try { var v = LogicObject.GetVariable("EnableLog"); if (v != null) _enableLog = (bool)v.Value; } catch { }
        if (Instance == null) Instance = this;
        if (EnableLog) Log.Info(LogCategory, $"Start (Mode={ListMode})");
        UpdateTitle();
        Generate();
    }

    public override void Stop()
    {
        Instance = null;
        if (EnableLog) Log.Info(LogCategory, "Stop");
    }
    #endregion

    #region 模式
    private string ListMode
    {
        get
        {
            try { var v = LogicObject.GetVariable("ListMode"); if (v != null && v.Value != null) { var s = (string)v.Value; if (!string.IsNullOrEmpty(s)) return s; } }
            catch { }
            return "Operation";
        }
    }
    #endregion

    #region 生成入口
    [ExportMethod]
    public void Generate()
    {
        var listContainer = GetListContainer();
        if (listContainer == null)
        {
            if (EnableLog) Log.Error(LogCategory, "未找到 ListContainer！");
            return;
        }
        bool wasVisible = listContainer.Visible;
        try
        {
            listContainer.Visible = false;
            GenerateCore(listContainer);
        }
        finally
        {
            listContainer.Visible = wasVisible;
        }
    }
    #endregion

    #region 核心生成
    private void GenerateCore(Container listContainer)
    {
        foreach (var child in listContainer.Children.OfType<Container>().ToList())
            child.Delete();

        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) { if (EnableLog) Log.Error(LogCategory, "RecipeDatabaseTreeLoader 未就绪"); return; }

        NodeId itemTypeId = FindCustomTypeNodeId(Project.Current, "OperationPhaseCard");
        if (itemTypeId == NodeId.Empty) { if (EnableLog) Log.Error(LogCategory, "未找到 OperationPhaseCard 类型！"); return; }

        string searchText = GetSearchText();

        if (ListMode == "Phase")
            BuildPhaseItems(listContainer, loader, itemTypeId, searchText);
        else
            BuildOperationItems(listContainer, loader, itemTypeId, searchText);
    }

    private void BuildOperationItems(Container listContainer, RecipeDatabaseTreeLoader loader, NodeId itemTypeId, string searchText)
    {
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (rId <= 0 || !loader.ReceiptById.TryGetValue(rId, out var rNode))
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中有效 Receipt，跳过 Operation 列表生成");
            return;
        }

        var versionMap = BuildVersionMap(rNode.Operations.Select(o => (o.OperationID, o.Name)));
        int count = 0;

        foreach (var op in rNode.Operations)
        {
            ParseBaseName(op.Name, out string baseName, out string version);
            if (!string.IsNullOrEmpty(searchText) && !baseName.Contains(searchText, StringComparison.OrdinalIgnoreCase)) continue;

            var item = InformationModel.MakeObject(SafeName(op.Name) + "_Item", itemTypeId) as Container;
            if (item == null) continue;

            SetTitle(item, baseName);
            SetVariables(item, op.OperationID, rId, 0);
            SetVersionComboBox(item, versionMap, baseName, op.OperationID);
            SetInsertButton(item, op.OperationID);
            listContainer.Add(item);
            count++;
        }
        if (EnableLog) Log.Info(LogCategory, $"Operation 列表生成完毕，共 {count} 项");
    }

    private void BuildPhaseItems(Container listContainer, RecipeDatabaseTreeLoader loader, NodeId itemTypeId, string searchText)
    {
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (oId <= 0 || !loader.OperationById.TryGetValue(oId, out var opNode))
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中有效 Operation，跳过 Phase 列表生成");
            return;
        }
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;

        var versionMap = BuildVersionMap(opNode.Phases.Select(p => (p.PhaseID, p.Name)));
        int count = 0;

        foreach (var ph in opNode.Phases)
        {
            ParseBaseName(ph.Name, out string baseName, out string version);
            if (!string.IsNullOrEmpty(searchText) && !baseName.Contains(searchText, StringComparison.OrdinalIgnoreCase)) continue;

            var item = InformationModel.MakeObject(SafeName(ph.Name) + "_Item", itemTypeId) as Container;
            if (item == null) continue;

            SetTitle(item, baseName);
            SetVariables(item, ph.PhaseID, rId, oId);
            SetVersionComboBox(item, versionMap, baseName, ph.PhaseID);
            SetInsertButton(item, ph.PhaseID);
            listContainer.Add(item);
            count++;
        }
        if (EnableLog) Log.Info(LogCategory, $"Phase 列表生成完毕，共 {count} 项");
    }
    #endregion

    #region ExportMethod：插入与新建
    /// <summary>Insert 按钮点击：将选中版本对应的 Operation/Phase 另存为复制插入当前树。</summary>
    [ExportMethod]
    public void OnInsert(int itemId)
    {
        if (itemId <= 0) return;
        string mode = ListMode;
        if (mode == "Phase")
        {
            int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
            int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
            if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation，无法插入 Phase"); return; }
            GenerateTreeList.Instance?.SetSelectedPhase(rId, oId, itemId);
            RecipeDatabaseManager.Instance?.SaveAsPhase();
        }
        else
        {
            int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
            if (rId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt，无法插入 Operation"); return; }
            GenerateTreeList.Instance?.SetSelectedOperation(rId, itemId);
            RecipeDatabaseManager.Instance?.SaveAsOperation();
        }
        GenerateTreeList.Instance?.Generate();
        Generate();
    }

    /// <summary>+ Create New 按钮点击：在当前 Receipt/Operation 下新建 Operation/Phase。</summary>
    [ExportMethod]
    public void OnCreateNew()
    {
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }

        if (ListMode == "Phase")
        {
            int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
            if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation，无法新建 Phase"); return; }
            loader.AddPhase(oId, GenerateUniqueName("NewPhase", loader.PhaseById.Values, n => n.Name), new Dictionary<string, object>());
            if (EnableLog) Log.Info(LogCategory, "已新建 Phase");
        }
        else
        {
            int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
            if (rId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt，无法新建 Operation"); return; }
            loader.AddOperation(rId, GenerateUniqueName("NewOperation", loader.OperationById.Values, n => n.Name));
            if (EnableLog) Log.Info(LogCategory, "已新建 Operation");
        }
        GenerateTreeList.Instance?.Generate();
        Generate();
    }
    #endregion

    #region 辅助：UI 控件设置
    /// <summary>列表容器在父级 OperationPhaseTemplatePanel1 -> ColumnsCards（vertical layout）。</summary>
    private Container GetListContainer()
    {
        return LogicObject.Owner.Parent?.Get<Container>("ColumnsCards");
    }

    /// <summary>标题在父级 OperationPhaseTemplatePanel1 -> Columns -> Label1。</summary>
    private void UpdateTitle()
    {
        var columns = LogicObject.Owner.Parent?.Get("Columns");
        var lbl = columns?.Get<Label>("Label1");
        if (lbl == null) return;
        lbl.Text = ListMode == "Phase" ? "Operation Phase" : "Operation List";
    }

    /// <summary>搜索框在 OperationPhaseTemplatePanel1 -> Columns -> SearchBox。</summary>
    private string GetSearchText()
    {
        try
        {
            var tb = LogicObject.Owner.Parent?.Get("Columns")?.Get<TextBox>("SearchBox");
            return tb?.Text ?? "";
        }
        catch { return ""; }
    }

    /// <summary>标题在 Rectangle1 -> VerticalLayout1 -> ButtonWithIcon1。</summary>
    private static void SetTitle(Container item, string baseName)
    {
        var titleBtn = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get<Button>("ButtonWithIcon1");
        if (titleBtn != null) titleBtn.Text = baseName;
    }

    private static void SetVariables(Container item, int itemId, int receiptId, int operationId)
    {
        var v = item.GetVariable("ItemId"); if (v != null) v.Value = itemId;
        v = item.GetVariable("ReceiptID"); if (v != null) v.Value = receiptId;
        v = item.GetVariable("OperationID"); if (v != null) v.Value = operationId;
    }

    /// <summary>版本下拉在 Rectangle1 -> VerticalLayout1 -> Row -> ComboBox1。</summary>
    private static void SetVersionComboBox(Container item, Dictionary<string, List<(int Id, string Version)>> versionMap, string baseName, int currentId)
    {
        var comboBox = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<ComboBox>("ComboBox1");
        if (comboBox == null) return;

        if (!versionMap.TryGetValue(baseName, out var versions)) return;

        string label = baseName;
        for (int i = 0; i < versions.Count; i++)
        {
            if (versions[i].Id == currentId)
            {
                label = string.IsNullOrEmpty(versions[i].Version) ? baseName : "Rev_" + versions[i].Version;
                break;
            }
        }
        comboBox.Text = label;
    }

    /// <summary>+Insert 按钮在 Rectangle1 -> VerticalLayout1 -> Row -> ButtonWithIcon1。</summary>
    private void SetInsertButton(Container item, int itemId)
    {
        var btn = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<Button>("ButtonWithIcon1");
        if (btn == null) return;
        btn.UAEvent -= (s, a) => OnInsert(itemId);
        btn.UAEvent += (s, a) => OnInsert(itemId);
    }
    #endregion

    #region 辅助：数据处理
    /// <summary>按名称基础部分分组，返回 baseName → [(Id, VersionSuffix)] 的映射。</summary>
    private static Dictionary<string, List<(int Id, string Version)>> BuildVersionMap(IEnumerable<(int Id, string Name)> items)
    {
        var map = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in items)
        {
            ParseBaseName(name, out string baseName, out string version);
            if (!map.TryGetValue(baseName, out var list)) { list = new List<(int, string)>(); map[baseName] = list; }
            list.Add((id, version));
        }
        return map;
    }

    /// <summary>拆分名称末尾的纯数字版本号，如 "MixingCycle_001" → baseName="MixingCycle", version="001"。</summary>
    private static void ParseBaseName(string name, out string baseName, out string version)
    {
        baseName = name ?? "";
        version = "";
        if (string.IsNullOrEmpty(baseName)) return;
        int lastUs = baseName.LastIndexOf('_');
        if (lastUs < 0) return;
        string suffix = baseName.Substring(lastUs + 1);
        if (string.IsNullOrEmpty(suffix) || !suffix.All(c => c >= '0' && c <= '9')) return;
        version = suffix;
        baseName = baseName.Substring(0, lastUs);
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Item";
        return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
    }

    private static string GenerateUniqueName<T>(string baseName, IEnumerable<T> existing, Func<T, string> getName)
    {
        int max = -1;
        string prefix = baseName + "_";
        foreach (var item in existing)
        {
            string n = getName(item) ?? "";
            if (n.Equals(baseName, StringComparison.OrdinalIgnoreCase)) { if (max < 0) max = 0; continue; }
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            ParseBaseName(n, out _, out string ver);
            if (!string.IsNullOrEmpty(ver) && int.TryParse(ver, out int v) && v > max) max = v;
        }
        return baseName + "_" + (max + 1).ToString("D3");
    }

    private static NodeId FindCustomTypeNodeId(IUANode root, string typeName)
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
