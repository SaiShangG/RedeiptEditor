#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.EventLogger;
#endregion

public class GenerateOperationPhaseListPanel : BaseNetLogic
{
    #region 日志
    private const string LogCategory = "GenerateRightPannelList";
    private static bool _enableLog = true;
    private static bool EnableLog => _enableLog;
    #endregion

    public static GenerateOperationPhaseListPanel Instance { get; private set; }
    private string _lastDerivedMode = "";
    private bool _insertInProgress;
    /// <summary>重建右侧卡片时抑制版本下拉事件，避免初始化误刷新中间面板。</summary>
    private bool _suppressVersionComboNotify;
    /// <summary>当前选中的模板卡片（标题行），与左侧树 GenerateTreeList 高亮色一致。</summary>
    private string _selectedTemplateTitleBaseName = "";
    private string _selectedTemplateDerivedMode = "";
    private static readonly Color TemplateTitleHighlightColor = new Color(255, 255, 220, 150);
    /// <summary>未选中标题背景：与 <see cref="GenerateTreeList"/> 中 Receipt 行 NormalColor 一致。</summary>
    private static readonly Color TemplateTitleNormalColor = new Color(0x99, 0xde, 0xee, 0xff);
    private static int _insertCallCount;
    private static readonly object _insertLock = new object(); // 防止多线程/异步下多次执行

    #region 生命周期
    public override void Start()
    {
        try { var v = LogicObject.GetVariable("EnableLog"); if (v != null) _enableLog = (bool)v.Value; } catch { }
        if (Instance == null) Instance = this;
        SyncTypeVariable();
        UpdateCreateNewButtonVisibility();
        if (EnableLog) Log.Info(LogCategory, "Start");
        Generate();
    }

    public override void Stop()
    {
        Instance = null;
        if (EnableLog) Log.Info(LogCategory, "Stop");
    }
    #endregion

    #region 模式（由左侧树选择推导）
    /// <summary>
    /// 根据左侧树当前选择推导有效模式：
    ///   选 Phase       → "Empty"（右侧 Phase 模板列表在 GenerateCore 中保留不清空）
    ///   选 Operation   → "Phase"
    ///   选 Receipt     → "Operation"
    ///   无选择         → "Empty"
    /// 同步到 LogicObject 的 Type 变量，供属性面板显示。
    /// </summary>
    private string DerivedMode
    {
        get
        {
            var tree = GenerateTreeList.Instance;
            if (tree == null) return "Empty";
            if (tree.SelectedPhaseId != 0) return "Empty";
            if (tree.SelectedOperationId != 0) return "Phase";
            if (tree.SelectedReceiptId != 0) return "Operation";
            return "Empty";
        }
    }

    private void SyncTypeVariable()
    {
        var v = LogicObject.GetVariable("Type");
        if (v != null) v.Value = DerivedMode;
    }

    private void UpdateCreateNewButtonVisibility()
    {
        var btnVar = LogicObject.GetVariable("CreateNewButton");
        if (btnVar == null || btnVar.Value == null) return;
        var nodeId = (NodeId)btnVar.Value;
        if (nodeId.IsEmpty) return;
        var btn = InformationModel.Get(nodeId) as Button;
        if (btn != null) btn.Visible = (DerivedMode == "Phase" || DerivedMode == "Operation");
    }
    #endregion

    #region 模板列表名称过滤（Search 绑定 ExportMethod）
    private string _templateListSearchFilter = "";

    private bool TemplateBaseNameMatchesSearch(string baseName)
    {
        if (string.IsNullOrWhiteSpace(_templateListSearchFilter)) return true;
        if (string.IsNullOrEmpty(baseName)) return false;
        return baseName.IndexOf(_templateListSearchFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>按模板标题基名包含关系过滤右侧 Operation/Phase 卡片。在 Optix 中将 Search 的「文本变更」绑定到此方法并传入当前文本（或 Text 变量）。</summary>
    [ExportMethod]
    public void RefreshTemplateListBySearchText(string filterText)
    {
        _templateListSearchFilter = filterText?.Trim() ?? "";
        Generate();
    }

    /// <summary>事件仅能提供 LocalizedText 时使用此重载。</summary>
    [ExportMethod]
    public void RefreshTemplateListBySearchLocalizedText(LocalizedText filterText)
    {
        _templateListSearchFilter = filterText?.Text?.Trim() ?? "";
        Generate();
    }
    #endregion

    #region 生成入口
    /// <summary>仅当左侧选中类型（Receipt/Operation/Phase）变化时刷新右侧列表，同类型下切换节点不刷新。</summary>
    public void RefreshIfModeChanged()
    {
        string mode = DerivedMode;
        if (mode == _lastDerivedMode) return;
        _lastDerivedMode = mode;
        Generate();
    }

    [ExportMethod]
    public void Generate()
    {
        _lastDerivedMode = DerivedMode;
        SyncTypeVariable();
        UpdateCreateNewButtonVisibility();
        var listContainer = GetListContainer();
        if (listContainer == null) { if (EnableLog) Log.Error(LogCategory, "未找到 ListContainer！"); return; }
        bool wasVisible = listContainer.Visible;
        try
        {
            listContainer.Visible = false;
            GenerateCore(listContainer);
        }
        finally { listContainer.Visible = wasVisible; }
    }
    #endregion

    #region 核心生成
    private void GenerateCore(Container listContainer)
    {
        // 左侧树选中 Phase 时 DerivedMode 为 Empty；不删除、不重建最右侧 Phase 模板列表，保持上次内容
        if (DerivedMode == "Empty" && (GenerateTreeList.Instance?.SelectedPhaseId ?? 0) != 0)
        {
            if (EnableLog) Log.Info(LogCategory, "左侧树选中 Phase：保留右侧 Phase 列表");
            return;
        }

        _suppressVersionComboNotify = true;
        try
        {
            foreach (var child in listContainer.Children.OfType<Container>().ToList())
                child.Delete();

            UpdateTitle();
            string mode = DerivedMode;
            if (mode == "Empty")
            {
                _selectedTemplateTitleBaseName = "";
                _selectedTemplateDerivedMode = "";
                if (EnableLog) Log.Info(LogCategory, "Empty 模式，列表为空");
                return;
            }

            var loader = RecipeDatabaseTreeLoader.Instance;
            if (loader == null) { if (EnableLog) Log.Error(LogCategory, "RecipeDatabaseTreeLoader 未就绪"); return; }

            NodeId itemTypeId = FindCustomTypeNodeId(Project.Current, "OperationPhaseCard");
            if (itemTypeId == NodeId.Empty) { if (EnableLog) Log.Error(LogCategory, "未找到 OperationPhaseCard 类型！"); return; }

            if (mode == "Phase")
                BuildPhaseItems(listContainer, loader, itemTypeId);
            else
                BuildOperationItems(listContainer, loader, itemTypeId);
            ApplyTemplateTitleSelectionHighlights(listContainer);
        }
        finally { _suppressVersionComboNotify = false; }
    }

    private void BuildOperationItems(Container listContainer, RecipeDatabaseTreeLoader loader, NodeId itemTypeId)
    {
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (rId <= 0)
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt，跳过 Operation 列表生成");
            return;
        }

        // 全库所有 Operation 按 baseName 分组，不过滤已添加的版本
        var versionMap = BuildVersionMap(loader.OperationById.Values.Select(o => (o.OperationID, o.Name)), null);

        int count = 0;
        foreach (var kvp in versionMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TemplateBaseNameMatchesSearch(kvp.Key)) continue;
            var item = InformationModel.MakeObject(SafeName(kvp.Key) + "_Item", itemTypeId) as Container;
            if (item == null) continue;

            SetTitle(item, kvp.Key);
            SetTemplateBaseNameTag(item, kvp.Key);
            SetVariables(item, kvp.Value[0].Id, rId, 0);
            SetVersionComboBoxAndIds(item, kvp.Key, kvp.Value);
            SetTitleButtonClick(item, kvp.Key);
            SetInsertButton(item);
            listContainer.Add(item);
            count++;
        }
        if (EnableLog) Log.Info(LogCategory, $"Operation 列表生成完毕，共 {count} 项");
    }

    private void BuildPhaseItems(Container listContainer, RecipeDatabaseTreeLoader loader, NodeId itemTypeId)
    {
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (oId <= 0)
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中 Operation，跳过 Phase 列表生成");
            return;
        }

        // 全库所有 Phase 按 baseName 分组，不过滤已添加的版本
        var versionMap = BuildVersionMap(loader.PhaseById.Values.Select(p => (p.PhaseID, p.Name)), null);

        int count = 0;
        foreach (var kvp in versionMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TemplateBaseNameMatchesSearch(kvp.Key)) continue;
            var item = InformationModel.MakeObject(SafeName(kvp.Key) + "_Item", itemTypeId) as Container;
            if (item == null) continue;

            SetTitle(item, kvp.Key);
            SetTemplateBaseNameTag(item, kvp.Key);
            SetVariables(item, kvp.Value[0].Id, rId, oId);
            SetVersionComboBoxAndIds(item, kvp.Key, kvp.Value);
            SetTitleButtonClick(item, kvp.Key);
            SetInsertButton(item);
            listContainer.Add(item);
            count++;
        }
        if (EnableLog) Log.Info(LogCategory, $"Phase 列表生成完毕，共 {count} 项");
    }
    #endregion

    #region ExportMethod：插入与新建
    [ExportMethod]
    public void OnInsert(int itemId)
    {
        _insertCallCount++;
        if (EnableLog) Log.Info(LogCategory, $"[追踪] OnInsert 第 {_insertCallCount} 次进入 itemId={itemId}");
        if (EnableLog) Log.Info(LogCategory, $"调用 OnInsert, itemId={itemId}\n{Environment.StackTrace}");


        if (itemId <= 0) return;
        lock (_insertLock)
        {
            if (_insertInProgress)
            {
                if (EnableLog) Log.Warning(LogCategory, "[追踪] OnInsert 跳过(防重入)");
                return;
            }
            _insertInProgress = true;
        }
        try
        {
            string mode = DerivedMode;
            if (mode == "Phase")
            {
                int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
                if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation，无法插入 Phase"); return; }
                if (EnableLog) Log.Info(LogCategory, "[追踪] 执行 SaveAsPhaseFromSource");
                RecipeDatabaseManager.Instance?.SaveAsPhaseFromSource(itemId);
            }
            else if (mode == "Operation")
            {
                int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
                if (rId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt，无法插入 Operation"); return; }
                if (EnableLog) Log.Info(LogCategory, "[追踪] 执行 SaveAsOperationFromSource");
                RecipeDatabaseManager.Instance?.SaveAsOperationFromSource(itemId);
            }
        }
        finally { lock (_insertLock) _insertInProgress = false; }
    }

    /// <summary>创建新的 Operation 或 Phase：仅写入对应数据库并刷新右侧 List，不加入左侧树。</summary>
    public void OnCreate(string name, string description = "")
    {
        if (string.IsNullOrWhiteSpace(name)) { if (EnableLog) Log.Warning(LogCategory, "名称为空"); return; }
        var loader = RecipeDatabaseTreeLoader.Instance;
        if (loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        string mode = DerivedMode;
        if (mode == "Phase")
        {
            string safeName = GenerateUniqueName(name.Trim(), loader.PhaseById.Values, n => n.Name);
            int newPId = loader.AddPhaseStandalone(safeName, description);
            if (newPId > 0 && EnableLog) Log.Info(LogCategory, "已新建 Phase(仅库): " + safeName);
        }
        else if (mode == "Operation")
        {
            string safeName = GenerateUniqueName(name.Trim(), loader.OperationById.Values, n => n.Name);
            int newOpId = loader.AddOperationStandalone(safeName, description);
            if (newOpId > 0 && EnableLog) Log.Info(LogCategory, "已新建 Operation(仅库): " + safeName);
        }
        else
            return;
        Generate(); // 仅刷新右侧 List，不刷新左侧树
    }
    #endregion

    #region 辅助：UI 控件设置
    private Container GetListContainer() => LogicObject.Owner?.Get<Container>("ColumnsCards");

    private void UpdateTitle()
    {
        string mode = DerivedMode;
        string titleText = mode == "Phase" ? "Phase List" : mode == "Operation" ? "Operation List" : "";

        var titleVar = LogicObject.GetVariable("Title");
        if (titleVar != null) titleVar.Value = titleText;

        var iconVar = LogicObject.GetVariable("Icon");
        if (iconVar != null) iconVar.Value = mode;
    }

    private static void SetTitle(Container item, string baseName)
    {
        var btn = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("ButtonWithIcon1")?.Get<Button>("Button1");
        if (btn != null) btn.Text = baseName;
    }

    private static void SetTemplateBaseNameTag(Container item, string baseName)
    {
        var v = item.GetVariable("TemplateBaseName");
        if (v == null)
        {
            v = InformationModel.MakeVariable("TemplateBaseName", OpcUa.DataTypes.String);
            item.Add(v);
        }
        v.Value = baseName ?? "";
    }

    private static Button GetTemplateTitleButton(Container item)
    {
        return item?.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("ButtonWithIcon1")?.Get<Button>("Button1");
    }

    /// <summary>根据记录的选中模板刷新各卡片标题按钮背景色（与左侧树选中行一致）。</summary>
    private void ApplyTemplateTitleSelectionHighlights(Container listContainer)
    {
        if (listContainer == null) return;
        string modeNow = DerivedMode;
        foreach (var child in listContainer.Children.OfType<Container>())
        {
            var btn = GetTemplateTitleButton(child);
            if (btn == null) continue;
            var tagVar = child.GetVariable("TemplateBaseName");
            string tag = "";
            if (tagVar?.Value != null)
            {
                var raw = tagVar.Value.Value;
                tag = raw as string ?? raw?.ToString() ?? "";
            }
            bool sel = !string.IsNullOrEmpty(_selectedTemplateTitleBaseName)
                && string.Equals(modeNow, _selectedTemplateDerivedMode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tag, _selectedTemplateTitleBaseName, StringComparison.OrdinalIgnoreCase);
            btn.BackgroundColor = sel ? TemplateTitleHighlightColor : TemplateTitleNormalColor;
        }
    }

    /// <summary>标题按钮（与 Insert 同级的 ButtonWithIcon1）：创建时增加 Click 回调，Log 当前是 Operation/Phase 及下拉框选中版本项（如 Operation_000、Phase_003）。</summary>
    private void SetTitleButtonClick(Container item, string baseName)
    {
        var btn = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("ButtonWithIcon1")?.Get<Button>("Button1");
        if (btn == null) return;
        btn.UAEvent -= TitleButtonClicked;
        btn.UAEvent += TitleButtonClicked;
        if (EnableLog) Log.Info(LogCategory, $"TitleButton event wired for item: {baseName}");
        void TitleButtonClicked(object s, UAEventArgs a)
        {
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            string mode = DerivedMode;
            string versionItem = GetSelectedVersionItemName(item, baseName);
            Log.Info(LogCategory, $"标题按钮点击: 类型={mode}, 当前选中项={versionItem}");
            _selectedTemplateTitleBaseName = baseName;
            _selectedTemplateDerivedMode = mode;
            ApplyTemplateTitleSelectionHighlights(GetListContainer());
            MiddlePanelManager.Instance?.NotifyTitleClicked(mode, versionItem);
        }
    }

    private static void SetVariables(Container item, int itemId, int receiptId, int operationId)
    {
        var v = item.GetVariable("ItemId"); if (v != null) v.Value = itemId;
        v = item.GetVariable("ReceiptID"); if (v != null) v.Value = receiptId;
        v = item.GetVariable("OperationID"); if (v != null) v.Value = operationId;
    }

    /// <summary>
    /// 将版本数据写入 Information Model（卡片下 VersionOptions 容器），并设置 ComboBox.Model 指向该容器；
    /// 同时将各版本 ID（按选项顺序）存入卡片 VersionIds 变量。选项格式：无版本号 → baseName；有版本号 → "v{N}"。
    /// </summary>
    private void SetVersionComboBoxAndIds(Container item, string baseName, List<(int Id, string Version)> versions)
    {
        var comboBox = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<ComboBox>("ComboBox1");

        var vIdsVar = item.GetVariable("VersionIds");
        if (vIdsVar == null)
        {
            vIdsVar = InformationModel.MakeVariable("VersionIds", OpcUa.DataTypes.String);
            item.Add(vIdsVar);
        }
        vIdsVar.Value = string.Join(",", versions.Select(v => v.Id));

        if (comboBox == null) return;

        // 获取或创建 Information Model 容器（存放版本选项）
        var versionOptions = item.GetObject("VersionOptions");
        if (versionOptions == null)
        {
            versionOptions = InformationModel.MakeObject("VersionOptions");
            item.Add(versionOptions);
        }
        foreach (var ch in versionOptions.Children.ToList())
            ch.Delete();

        NodeId firstOptNodeId = NodeId.Empty;
        for (int i = 0; i < versions.Count; i++)
        {
            string ver = versions[i].Version;
            string label = string.IsNullOrEmpty(ver)
                ? baseName
                : "v" + ver.TrimStart('0').PadLeft(1, '0');
            string optName = string.IsNullOrEmpty(ver) ? "0" : ver;
            var optVar = InformationModel.MakeVariable(optName, OpcUa.DataTypes.LocalizedText);
            optVar.Value = new LocalizedText("", label);
            versionOptions.Add(optVar);
            if (i == 0) firstOptNodeId = optVar.NodeId;
        }

        var modelVar = comboBox.GetVariable("Model");
        if (modelVar != null) modelVar.Value = versionOptions.NodeId;

        var selVar = comboBox.GetVariable("SelectedItem");
        if (selVar != null && !firstOptNodeId.IsEmpty) selVar.Value = firstOptNodeId;

        WireVersionComboBoxForMiddlePanelSync(item, baseName);
    }

    #region 版本下拉与中间模板面板同步
    /// <summary>与标题按钮一致：切换版本后仍刷新 Operation/Phase 模板面板，避免仅操作下拉导致中间区“掉选中”。</summary>
    private void WireVersionComboBoxForMiddlePanelSync(Container item, string baseName)
    {
        var comboBox = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<ComboBox>("ComboBox1");
        if (comboBox == null) return;

        comboBox.UAEvent -= OnVersionComboUserSelectionChanged;
        comboBox.UAEvent += OnVersionComboUserSelectionChanged;
        if (EnableLog) Log.Info(LogCategory, $"Version ComboBox UserSelectionChanged 已绑定: {baseName}");

        void OnVersionComboUserSelectionChanged(object sender, UAEventArgs a)
        {
            if (_suppressVersionComboNotify) return;
            if (a?.EventType?.BrowseName != "UserSelectionChanged") return;
            string mode = DerivedMode;
            if (mode != "Operation" && mode != "Phase") return;
            string versionItem = GetSelectedVersionItemName(item, baseName);
            if (EnableLog) Log.Info(LogCategory, $"版本下拉变更，同步中间面板: {versionItem}");
            _selectedTemplateTitleBaseName = baseName;
            _selectedTemplateDerivedMode = mode;
            ApplyTemplateTitleSelectionHighlights(GetListContainer());
            MiddlePanelManager.Instance?.NotifyTitleClicked(mode, versionItem);
        }
    }
    #endregion

    /// <summary>
    /// Insert 按钮：点击时读取该卡片 ComboBox 当前选中索引，从 VersionIds 查出 ID 后调用 OnInsert。
    /// </summary>
    private void SetInsertButton(Container item)
    {
        var btn = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get("ButtonWithIcon1")?.Get<Button>("Button1");
        if (btn == null) return;

        btn.UAEvent -= InsertButtonClicked; // 先移除
        btn.UAEvent += InsertButtonClicked; // 后添加

        if (EnableLog) Log.Info(LogCategory, $"InsertButton event wired for item: {item?.BrowseName ?? "<null>"}");

        void InsertButtonClicked(object s, UAEventArgs a)
        {
            // 仅响应鼠标点击，忽略键盘等其它触发
            if (a?.EventType?.BrowseName != "MouseClickEvent") return;
            int id = GetSelectedVersionId(item);
            if (id > 0) OnInsert(id);
        }
    }

    /// <summary>从 ComboBox 当前选中项得到版本项名称，如 baseName + "_000" / "_003"。</summary>
    private static string GetSelectedVersionItemName(Container item, string baseName)
    {
        var comboBox = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<ComboBox>("ComboBox1");
        if (comboBox == null) return baseName + "_???";
        var modelVar = comboBox.GetVariable("Model");
        NodeId modelNodeId = modelVar?.Value != null && modelVar.Value.Value is NodeId mid ? (NodeId)mid : NodeId.Empty;
        IUANode modelNode = !modelNodeId.IsEmpty ? InformationModel.Get(modelNodeId) : null;
        var optChildren = modelNode != null ? modelNode.Children.OrderBy(c => c.BrowseName).ToList() : new List<IUANode>();
        var selVar = comboBox.GetVariable("SelectedItem");
        string verPart = "000";
        if (selVar?.Value?.Value is NodeId nodeId && !nodeId.IsEmpty)
        {
            for (int i = 0; i < optChildren.Count; i++)
            {
                if (optChildren[i].NodeId != nodeId) continue;
                string bn = optChildren[i].BrowseName;
                verPart = string.IsNullOrEmpty(bn) || bn == "0" ? "000" : bn.PadLeft(3, '0');
                break;
            }
        }
        return baseName + "_" + verPart;
    }

    /// <summary>从 ComboBox 的 Model（Information Model）与 SelectedItem 解析选中索引，再结合 VersionIds 得到当前选中的 ID。</summary>
    private static int GetSelectedVersionId(Container item)
    {
        int selectedIndex = 0;
        var comboBox = item.Get("Rectangle1")?.Get("VerticalLayout1")?.Get("Row")?.Get<ComboBox>("ComboBox1");
        if (comboBox != null)
        {
            var modelVar = comboBox.GetVariable("Model");
            NodeId modelNodeId = modelVar?.Value != null && modelVar.Value.Value is NodeId mid ? (NodeId)mid : NodeId.Empty;
            IUANode modelNode = !modelNodeId.IsEmpty ? InformationModel.Get(modelNodeId) : null;
            var optChildren = modelNode != null
                ? modelNode.Children.OrderBy(c => c.BrowseName).ToList()
                : new List<IUANode>();

            var selVar = comboBox.GetVariable("SelectedItem");
            if (selVar?.Value != null)
            {
                var inner = selVar.Value.Value;
                if (inner is NodeId nodeId && !nodeId.IsEmpty)
                {
                    for (int i = 0; i < optChildren.Count; i++)
                    {
                        if (optChildren[i].NodeId == nodeId) { selectedIndex = i; break; }
                    }
                }
                else
                {
                    try { selectedIndex = Convert.ToInt32(inner); } catch { }
                }
            }
        }

        var vIdsVar = item.GetVariable("VersionIds");
        string vIdsStr = (vIdsVar?.Value?.Value as string) ?? "";
        if (!string.IsNullOrEmpty(vIdsStr))
        {
            var parts = vIdsStr.Split(',');
            if (selectedIndex >= 0 && selectedIndex < parts.Length &&
                int.TryParse(parts[selectedIndex], out int id))
                return id;
        }

        var itemIdVar = item.GetVariable("ItemId");
        if (itemIdVar?.Value != null)
        {
            try { return (int)itemIdVar.Value; } catch { }
        }
        return 0;
    }
    #endregion

    #region 辅助：数据处理
    /// <summary>
    /// 按 baseName 分组，返回 baseName → [(Id, Version)] 映射。
    /// 可选传入 excludedFullNames（HashSet）以跳过已存在的全名（base+版本）。
    /// </summary>
    private static Dictionary<string, List<(int Id, string Version)>> BuildVersionMap(
        IEnumerable<(int Id, string Name)> items,
        HashSet<string> excludedFullNames = null)
    {
        var map = new Dictionary<string, List<(int, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in items)
        {
            ParseBaseName(name, out string baseName, out string version);
            if (excludedFullNames != null && excludedFullNames.Contains(name)) continue;
            if (!map.TryGetValue(baseName, out var list)) { list = new List<(int, string)>(); map[baseName] = list; }
            list.Add((id, version));
        }
        return map;
    }

    /// <summary>拆分末尾纯数字版本号，如 "MixingCycle_001" → baseName="MixingCycle", version="001"。</summary>
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

    /// <summary>生成不重复的 <c>base_NNN</c>：仅按已有 <c>base_NNN</c> 取 max；无后缀的 base 不占位，首个为 <c>_000</c>。</summary>
    private static string GenerateUniqueName<T>(string baseName, IEnumerable<T> existing, Func<T, string> getName)
    {
        int max = -1;
        string prefix = baseName + "_";
        foreach (var item in existing)
        {
            string n = getName(item) ?? "";
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
