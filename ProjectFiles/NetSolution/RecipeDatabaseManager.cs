#region Using directives
using System;
using System.Collections.Generic;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.Store;
using FTOptix.RecipeX;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

public class RecipeDatabaseManager : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseManager";
    private const bool EnableLog = true;
    private const string EvtButton = "Evt_ButtonClick";

    public static RecipeDatabaseManager Instance { get; private set; }

    /// <summary>
    /// Modifiable Status：主面板写入 <c>TreeListData/SelectedTreeData/SelectedReceiptCurrentStatus</c>；
    /// 部分模板写入 <c>UIData/ReceiptStatusSet</c>。订阅变更以在配方树上标 *。
    /// </summary>
    private uint _modifiableReceiptStatusAffinityId;
    private IEventRegistration _modifiableReceiptStatusRegSelectedTree;
    private IEventRegistration _modifiableReceiptStatusRegReceiptStatusSet;
    private int _modifiableReceiptStatusSuppressDepth;

    /// <summary>供 <see cref="RecipeDatabaseTreeLoader"/> 等在 Save 时读取当前登录用户（Manager 常绑定到带会话的 UI）。</summary>
    public static string TryGetInstanceUserBrowseName()
    {
        try
        {
            var u = Instance?.Session?.User;
            if (u != null && !string.IsNullOrEmpty(u.BrowseName))
                return u.BrowseName.Trim();
        }
        catch { }
        return "";
    }

    private void Audit(string actionTpl, Dictionary<string, string> vars, string oldVal = "", string newVal = "", string status = "ok")
    {
        string user = "";
        try
        {
            var u = Session?.User;
            if (u != null && !string.IsNullOrEmpty(u.BrowseName))
                user = u.BrowseName.Trim();
        }
        catch { }
        RecipeAuditLogHelper.Append(LogicObject, user, EvtButton, actionTpl, vars, oldVal, newVal, status);
    }

    #region 生命周期
    public override void Start()
    {
        Instance = this;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已启动");
        EnsureModifiableReceiptStatusObservers();
    }

    public override void Stop()
    {
        DetachModifiableReceiptStatusObservers();
        Instance = null;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已停止");
    }
    #endregion

    #region Phase 列克隆
    /// <summary>克隆源 Phase 列并合并 Parameter1..3（与 PhaseUIBuffer 加载一致）；另存 Operation/Receipt/Phase 共用。</summary>
    private Dictionary<string, object> ClonePhaseColumnsForInsert(RecipeDatabaseTreeLoader.PhaseNode srcPh)
    {
        var colCopy = new Dictionary<string, object>(srcPh.Columns, StringComparer.OrdinalIgnoreCase);
        Loader.ApplyResolvedParameter123ToColumnCopy(srcPh, colCopy);
        return colCopy;
    }
    #endregion

    #region 通过 TreeLoader 读取
    private static RecipeDatabaseTreeLoader Loader => RecipeDatabaseTreeLoader.Instance;

    /// <summary>根据当前 TreeList 选中项，从 TreeLoader 内存树取回显示数据。</summary>
    public bool GetSelectedItemModelData(
        out string itemType, out string receiptName,
        out string receiptCreatedDate, out string receiptCreatedBy,
        out string receiptCurrentStatus, out string selectedOpName, out string selectedPhaseName)
    {
        itemType = "Receipt";
        receiptName = receiptCreatedDate = receiptCreatedBy = receiptCurrentStatus = selectedOpName = selectedPhaseName = "";
        if (GenerateTreeList.Instance == null || Loader == null) return false;

        int rId = GenerateTreeList.Instance.SelectedReceiptId;
        int oId = GenerateTreeList.Instance.SelectedOperationId;
        int pId = GenerateTreeList.Instance.SelectedPhaseId;

        if (rId > 0 && Loader.ReceiptById.TryGetValue(rId, out var rNode))
        {
            receiptName = rNode.Name ?? "";
            receiptCreatedBy = rNode.CreatedBy ?? "";
            receiptCreatedDate = rNode.CreatedDateTime ?? "";
            receiptCurrentStatus = string.IsNullOrWhiteSpace(rNode.Status)
                ? RecipeDatabaseTreeLoader.DefaultReceiptStatus
                : rNode.Status;
        }
        if (pId > 0)
        {
            itemType = "Phase";
            if (Loader.PhaseById.TryGetValue(pId, out var pNode)) selectedPhaseName = pNode.Name ?? "";
            if (oId > 0 && Loader.OperationById.TryGetValue(oId, out var opNode2)) selectedOpName = opNode2.Name ?? "";
        }
        else if (oId > 0)
        {
            itemType = "Operation";
            if (Loader.OperationById.TryGetValue(oId, out var opNode)) selectedOpName = opNode.Name ?? "";
        }
        return true;
    }
    #endregion

    #region 通过 TreeLoader 写入：配方（Receipt）
    /// <summary>新增配方：从 nameNodeId、descriptNodeId 读取名称与描述，插入内存树并刷新 UI，最后持久化。名称末尾无版本号时自动补 _000。</summary>
    [ExportMethod]
    public void AddNewReceipt(string name, string descript)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        if (string.IsNullOrWhiteSpace(name)) { if (EnableLog) Log.Warning(LogCategory, "名称为空，未执行插入"); return; }

        name = EnsureNameWithVersion(name, Loader.Tree, n => n.Name);
        if (EnableLog) Log.Info(LogCategory, $"AddNewReceipt: Name='{name}', Descript='{descript ?? ""}'");

        int newId = Loader.AddReceipt(name, descript ?? "");
        if (EnableLog) Log.Info(LogCategory, $"配方已加入内存树: ReceiptID={newId}, Name='{name}'");
        Audit("Act_Create", new Dictionary<string, string> { ["name"] = name, ["type"] = "Receipt" });
        SaveOrMarkDirtyAndRefresh(receiptId: newId);
    }

    /// <summary>另存为：将当前选中的 Receipt 另存为新配方（版本号+1），含其所有 Operation 与 Phase。</summary>
    [ExportMethod]
    public void SaveAsReceipt()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int selectedId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (selectedId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中任何配方，无法另存为"); return; }
        if (!Loader.ReceiptById.TryGetValue(selectedId, out var src)) { if (EnableLog) Log.Error(LogCategory, $"未找到 ReceiptID={selectedId}"); return; }

        ParseVersionSuffix(src.Name, out string baseName, out int _);
        string newName = baseName + "_" + GetNextVersionForBase(baseName, Loader.Tree, n => n.Name).ToString("D3");

        int newRId = Loader.AddReceipt(newName, src.Description ?? "");
        if (!Loader.ReceiptById.TryGetValue(newRId, out var newR)) return;

        foreach (var srcOp in src.Operations)
        {
            ParseVersionSuffix(srcOp.Name, out string opBase, out int _);
            string newOpName = opBase + "_" + GetNextVersionForBase(opBase, Loader.OperationById.Values, n => n.Name).ToString("D3");
            int newOpId = Loader.AddOperation(newRId, newOpName);

            foreach (var srcPh in srcOp.Phases)
            {
                ParseVersionSuffix(srcPh.Name, out string phBase, out int _);
                string newPhName = phBase + "_" + GetNextVersionForBase(phBase, Loader.PhaseById.Values, n => n.Name).ToString("D3");
                Loader.AddPhase(newOpId, newPhName, ClonePhaseColumnsForInsert(srcPh));
            }
        }
        SaveOrMarkDirtyAndRefresh(receiptId: newRId);
        if (EnableLog) Log.Info(LogCategory, $"SaveAs Receipt: '{src.Name}' -> '{newName}', ReceiptID={newRId}");
        Audit("Act_SaveAs", new Dictionary<string, string> { ["name"] = src.Name ?? "", ["newName"] = newName, ["type"] = "Receipt" });
    }

    /// <summary>删除当前选中的 Receipt（仅内存树）；落库请点保存 <see cref="DoSaveToDatabase"/> 或 Save Phase/Operation。</summary>
    [ExportMethod]
    public void DeleteSelectedReceipt()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int selectedId = GenerateTreeList.Instance?.SelectedReceiptId ?? -1;
        if (selectedId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中任何配方，无法删除"); return; }
        string deletedName = "";
        if (Loader.ReceiptById.TryGetValue(selectedId, out var rDel)) deletedName = rDel.Name ?? "";
        if (!Loader.RemoveReceipt(selectedId)) { if (EnableLog) Log.Warning(LogCategory, $"RemoveReceipt 失败: ReceiptID={selectedId}"); return; }
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 ReceiptID={selectedId}");
        Audit("Act_Delete", new Dictionary<string, string> { ["name"] = deletedName, ["type"] = "Receipt" });
    }

    /// <summary>
    /// Discarded 页删除确认：根据 <c>SelectedDiscardedItem</c> 的 <c>SelectedItemName</c>、<c>SelectedTap</c>
    /// （0=Discarded 配方，1=未用工序，2=未用阶段）删除。名称为空时从当前标签对应 <c>Query</c> 解析首行 Name。
    /// </summary>
    [ExportMethod]
    public void ConfirmDeleteDiscardedGridSelection()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }

        var holder = GetSelectedDiscardedItemHolder();
        if (holder == null) { if (EnableLog) Log.Warning(LogCategory, "未找到 SelectedDiscardedItem"); return; }

        string name = GetPlainStringFromVariableValue(holder.GetVariable("SelectedItemName"));
        int tap = ReadDiscardedSelectedTap(holder);

        if (string.IsNullOrWhiteSpace(name))
            name = TryResolveDiscardedNameFromCurrentQuery(tap);

        if (string.IsNullOrWhiteSpace(name))
        {
            if (EnableLog) Log.Warning(LogCategory, "删除中止：无名称（SelectedItemName 为空且无法从 Query 解析）。");
            return;
        }

        bool ok = false;
        if (tap == 0) ok = TryDeleteDiscardedReceiptByName(name);
        else if (tap == 1) ok = TryDeleteUnusedOperationByName(name);
        else if (tap == 2) ok = TryDeleteUnusedPhaseByName(name);
        else
        {
            if (EnableLog) Log.Warning(LogCategory, $"SelectedTap={tap} 非预期，按 Discarded 配方删除。");
            ok = TryDeleteDiscardedReceiptByName(name);
        }

        if (!ok) return;

        // 确认对话框中的永久删除必须写库并同步 DELETE；若仅依赖 autosave=false，此处不 Save 会导致库中仍存在该行。
        Loader.Save();
        AfterReceiptDatabasePersisted();
        RefreshTreeList();
        Loader.RefreshDiscardedUnusedGridQueries();
        ClearSelectedDiscardedItemName(holder);
        if (EnableLog) Log.Info(LogCategory, $"Discarded 删除完成: tap={tap}, Name='{name}'");
        string typeStr = tap == 0 ? "Receipt" : tap == 1 ? "Operation" : "Phase";
        Audit("Act_Delete", new Dictionary<string, string> { ["name"] = name, ["type"] = typeStr });
    }

    [ExportMethod]
    public void MoveUpReceipt()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        int id = GenerateTreeList.Instance.SelectedReceiptId;
        if (!Loader.MoveReceiptUp(id)) { if (EnableLog) Log.Warning(LogCategory, $"上移失败或已在顶部: ReceiptID={id}"); return; }
        SaveOrMarkDirtyAndRefresh(receiptId: id);
        string rn = Loader.ReceiptById.TryGetValue(id, out var rM) ? (rM.Name ?? "") : "";
        Audit("Act_Up", new Dictionary<string, string> { ["name"] = rn, ["type"] = "Receipt" });
    }

    [ExportMethod]
    public void MoveDownReceipt()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        int id = GenerateTreeList.Instance.SelectedReceiptId;
        if (!Loader.MoveReceiptDown(id)) { if (EnableLog) Log.Warning(LogCategory, $"下移失败或已在底部: ReceiptID={id}"); return; }
        SaveOrMarkDirtyAndRefresh(receiptId: id);
        string rn = Loader.ReceiptById.TryGetValue(id, out var rM) ? (rM.Name ?? "") : "";
        Audit("Act_Down", new Dictionary<string, string> { ["name"] = rn, ["type"] = "Receipt" });
    }
    #endregion

    #region 通过 TreeLoader 写入：工序（Operation）
    [ExportMethod]
    public void MoveUpOperation()
    {
        if (!SwapOperationOrder(up: true)) if (EnableLog) Log.Warning(LogCategory, "上移 Operation 失败或未选中/已在顶部");
    }

    [ExportMethod]
    public void MoveDownOperation()
    {
        if (!SwapOperationOrder(up: false)) if (EnableLog) Log.Warning(LogCategory, "下移 Operation 失败或未选中/已在底部");
    }

    private bool SwapOperationOrder(bool up)
    {
        if (Loader == null || GenerateTreeList.Instance == null) return false;
        int rId = GenerateTreeList.Instance.SelectedReceiptId;
        int oId = GenerateTreeList.Instance.SelectedOperationId;
        if (rId <= 0 || oId <= 0) return false;
        bool ok = up ? Loader.MoveOperationUp(rId, oId) : Loader.MoveOperationDown(rId, oId);
        if (!ok) return false;
        SaveOrMarkDirtyAndRefresh(operationId: oId);
        if (EnableLog) Log.Info(LogCategory, $"Operation {(up ? "上" : "下")}移成功: ReceiptID={rId}, OperationID={oId}");
        string on = Loader.OperationById.TryGetValue(oId, out var opM) ? (opM.Name ?? "") : "";
        Audit(up ? "Act_Up" : "Act_Down", new Dictionary<string, string> { ["name"] = on, ["type"] = "Operation" });
        return true;
    }

    /// <summary>保存当前选中 Operation 的 Name（从 nameNodeId 读取）。</summary>
    [ExportMethod]
    public void SaveOperation(NodeId nameNodeId, NodeId phasesNodeId)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation"); return; }
        string oldName = Loader.OperationById.TryGetValue(oId, out var opS) ? (opS.Name ?? "") : "";
        string name = GetStringFromNode(nameNodeId);
        if (!string.IsNullOrEmpty(name)) Loader.UpdateOperation(oId, name);
        Loader.Save();
        AfterReceiptDatabasePersisted();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"Operation 已保存: OperationID={oId}");
        string displayName = string.IsNullOrEmpty(name) ? oldName : name;
        if (!string.IsNullOrEmpty(name) && !string.Equals(oldName, name, StringComparison.Ordinal))
            Audit("Act_Rename", new Dictionary<string, string> { ["name"] = oldName, ["newName"] = name, ["type"] = "Operation" });
        else
            Audit("Act_Save", new Dictionary<string, string> { ["name"] = displayName, ["type"] = "Operation" });
    }

    /// <summary>另存为当前选中的 Operation（版本号+1），含其所有 Phase，插入到当前 Receipt 中选中项后面。</summary>
    [ExportMethod]
    public void SaveAsOperation()
    {
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (oId <= 0) return;
        SaveAsOperationFromSource(oId);
    }

    /// <summary>从指定源 Operation 另存为一个新 Operation（版本号+1），并插入到当前 Receipt 中选中项后面，同时复制其所有 Phase。</summary>
    /// <param name="fromInsertPanel">true 表示右侧列表 Insert，审计用 Act_Insert；false 表示工具栏另存为，审计用 Act_SaveAs。</param>
    public void SaveAsOperationFromSource(int sourceOperationId, bool fromInsertPanel = false)
    {
        if (EnableLog) Log.Info(LogCategory, $"[追踪] SaveAsOperationFromSource 进入 sourceOperationId={sourceOperationId}");
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int oIdInsertAfter = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (rId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt"); return; }
        if (!Loader.ReceiptById.TryGetValue(rId, out var rNode)) return;
        if (!Loader.OperationById.TryGetValue(sourceOperationId, out var srcOp)) return;

        // 1. 为源 Operation 生成新版本的名称
        ParseVersionSuffix(srcOp.Name, out string opBase, out int _);
        string newOpName = opBase + "_" + GetNextVersionForBase(opBase, Loader.OperationById.Values, n => n.Name).ToString("D3");

        // 2. 在当前 Receipt 下新增一个 Operation（仅 1 条），后续再复制 Phase
        int newOpId = Loader.AddOperation(rId, newOpName, srcOp.Description);

        // 3. 调整新 Operation 在当前 Receipt 中的位置：插在当前选中 Operation 后面
        int insertIdx = rNode.Operations.FindIndex(o => o.OperationID == newOpId);
        int srcIdx = oIdInsertAfter > 0 ? rNode.Operations.FindIndex(o => o.OperationID == oIdInsertAfter) : -1;
        if (insertIdx > 0 && srcIdx >= 0 && insertIdx != srcIdx + 1)
        {
            var newOpNode = rNode.Operations[insertIdx];
            rNode.Operations.RemoveAt(insertIdx);
            rNode.Operations.Insert(srcIdx + 1, newOpNode);
        }

        // 4. 复制源 Operation 下的所有 Phase，名称同样按版本号+1 生成
        if (Loader.OperationById.TryGetValue(newOpId, out var newOp))
        {
            foreach (var srcPh in srcOp.Phases)
            {
                ParseVersionSuffix(srcPh.Name, out string phBase, out int _);
                string newPhName = phBase + "_" + GetNextVersionForBase(phBase, Loader.PhaseById.Values, n => n.Name).ToString("D3");
                Loader.AddPhase(newOpId, newPhName, ClonePhaseColumnsForInsert(srcPh));
            }
        }

        // 5. Insert 场景：仅缓存到内存树并刷新 UI，不立即写数据库
        SaveOrMarkDirtyAndRefresh(operationId: newOpId, persistToDb: false);
        if (EnableLog) Log.Info(LogCategory, $"SaveAs Operation: '{srcOp.Name}' -> '{newOpName}', OperationID={newOpId}");
        if (fromInsertPanel)
            Audit("Act_Insert", new Dictionary<string, string> { ["name"] = newOpName, ["childrenType"] = "Operation", ["type"] = "Receipt", ["parentName"] = rNode.Name ?? "" });
        else
            Audit("Act_SaveAs", new Dictionary<string, string> { ["name"] = srcOp.Name ?? "", ["newName"] = newOpName, ["type"] = "Operation" });
    }

    /// <summary>删除当前选中的 Operation（仅内存树）；落库请点保存。</summary>
    [ExportMethod]
    public void DeleteSelectedOperation()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (rId <= 0 || oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt 或 Operation"); return; }
        string opDelName = "";
        if (Loader.OperationById.TryGetValue(oId, out var opDel)) opDelName = opDel.Name ?? "";
        if (!Loader.RemoveOperation(rId, oId)) return;
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 Operation: OperationID={oId}");
        Audit("Act_Delete", new Dictionary<string, string> { ["name"] = opDelName, ["type"] = "Operation" });
    }
    #endregion

    #region 通过 TreeLoader 写入：阶段（Phase）
    [ExportMethod]
    public void MoveUpPhase()
    {
        if (!SwapPhaseOrder(up: true)) if (EnableLog) Log.Warning(LogCategory, "上移 Phase 失败或未选中/已在顶部");
    }

    [ExportMethod]
    public void MoveDownPhase()
    {
        if (!SwapPhaseOrder(up: false)) if (EnableLog) Log.Warning(LogCategory, "下移 Phase 失败或未选中/已在底部");
    }

    private bool SwapPhaseOrder(bool up)
    {
        if (Loader == null || GenerateTreeList.Instance == null) return false;
        int oId = GenerateTreeList.Instance.SelectedOperationId;
        int pId = GenerateTreeList.Instance.SelectedPhaseId;
        if (oId <= 0 || pId <= 0) return false;
        bool ok = up ? Loader.MovePhaseUp(oId, pId) : Loader.MovePhaseDown(oId, pId);
        if (!ok) return false;
        SaveOrMarkDirtyAndRefresh(phaseId: pId);
        if (EnableLog) Log.Info(LogCategory, $"Phase {(up ? "上" : "下")}移成功: OperationID={oId}, PhaseID={pId}");
        string pn = Loader.PhaseById.TryGetValue(pId, out var phM) ? (phM.Name ?? "") : "";
        Audit(up ? "Act_Up" : "Act_Down", new Dictionary<string, string> { ["name"] = pn, ["type"] = "Phase" });
        return true;
    }

    /// <summary>保存当前选中 Phase 的 Name（从 nameNodeId 读取）。</summary>
    [ExportMethod]
    public void SavePhase(NodeId nameNodeId)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Phase"); return; }
        string oldName = Loader.PhaseById.TryGetValue(pId, out var phS) ? (phS.Name ?? "") : "";
        string name = GetStringFromNode(nameNodeId);
        if (!string.IsNullOrEmpty(name)) Loader.UpdatePhase(pId, name);
        Loader.Save();
        AfterReceiptDatabasePersisted();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"Phase 已保存: PhaseID={pId}");
        string displayName = string.IsNullOrEmpty(name) ? oldName : name;
        if (!string.IsNullOrEmpty(name) && !string.Equals(oldName, name, StringComparison.Ordinal))
            Audit("Act_Rename", new Dictionary<string, string> { ["name"] = oldName, ["newName"] = name, ["type"] = "Phase" });
        else
            Audit("Act_Save", new Dictionary<string, string> { ["name"] = displayName, ["type"] = "Phase" });
    }

    /// <summary>另存为当前选中的 Phase（版本号+1），插入到当前 Operation 选中项后面。</summary>
    [ExportMethod]
    public void SaveAsPhase()
    {
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) return;
        SaveAsPhaseFromSource(pId);
    }

    /// <summary>从指定源 Phase 另存为并插入当前 Operation，插入位置为树当前选中 Phase 后面；不改变树选中。</summary>
    /// <param name="fromInsertPanel">true 表示右侧 Insert，审计 Act_Insert；false 表示工具栏另存为，审计 Act_SaveAs。</param>
    public void SaveAsPhaseFromSource(int sourcePhaseId, bool fromInsertPanel = false)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int pIdInsertAfter = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation"); return; }
        if (!Loader.OperationById.TryGetValue(oId, out var opNode)) return;
        if (!Loader.PhaseById.TryGetValue(sourcePhaseId, out var srcPh)) return;

        ParseVersionSuffix(srcPh.Name, out string phBase, out int _);
        string newPhName = phBase + "_" + GetNextVersionForBase(phBase, Loader.PhaseById.Values, n => n.Name).ToString("D3");
        int newPId = Loader.AddPhase(oId, newPhName, ClonePhaseColumnsForInsert(srcPh));

        int insertIdx = opNode.Phases.FindIndex(p => p.PhaseID == newPId);
        int srcIdx = pIdInsertAfter > 0 ? opNode.Phases.FindIndex(p => p.PhaseID == pIdInsertAfter) : -1;
        if (insertIdx > 0 && srcIdx >= 0 && insertIdx != srcIdx + 1)
        {
            var newPNode = opNode.Phases[insertIdx];
            opNode.Phases.RemoveAt(insertIdx);
            opNode.Phases.Insert(srcIdx + 1, newPNode);
        }
        SaveOrMarkDirtyAndRefresh(phaseId: newPId, persistToDb: false); // Insert 仅缓存+刷新树，不落库
        if (EnableLog) Log.Info(LogCategory, $"SaveAs Phase: '{srcPh.Name}' -> '{newPhName}', PhaseID={newPId}");
        if (fromInsertPanel)
            Audit("Act_Insert", new Dictionary<string, string> { ["name"] = newPhName, ["childrenType"] = "Phase", ["type"] = "Operation", ["parentName"] = opNode.Name ?? "" });
        else
            Audit("Act_SaveAs", new Dictionary<string, string> { ["name"] = srcPh.Name ?? "", ["newName"] = newPhName, ["type"] = "Phase" });
    }

    /// <summary>删除当前选中的 Phase（仅内存树）；落库请点保存。</summary>
    [ExportMethod]
    public void DeleteSelectedPhase()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (oId <= 0 || pId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation 或 Phase"); return; }
        string phDelName = "";
        if (Loader.PhaseById.TryGetValue(pId, out var phDel)) phDelName = phDel.Name ?? "";
        if (!Loader.RemovePhase(oId, pId)) return;
        Loader.MarkDirtyOperation(oId);
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 Phase: PhaseID={pId}");
        Audit("Act_Delete", new Dictionary<string, string> { ["name"] = phDelName, ["type"] = "Phase" });
    }
    #endregion

    #region 统一分发
    [ExportMethod]
    public void DoUp()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) MoveUpPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) MoveUpOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) MoveUpReceipt();
    }

    [ExportMethod]
    public void DoDown()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) MoveDownPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) MoveDownOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) MoveDownReceipt();
    }

    [ExportMethod]
    public void DoSave(NodeId nameNodeId)
    {
        DoSaveItem(nameNodeId, NodeId.Empty);
    }

    [ExportMethod]
    public void DoSaveItem(NodeId nameNodeId, NodeId phasesNodeId)
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) SavePhase(nameNodeId);
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) SaveOperation(nameNodeId, phasesNodeId ?? NodeId.Empty);
    }

    [ExportMethod]
    public void DoSaveAs()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) SaveAsPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) SaveAsOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) SaveAsReceipt();
    }

    [ExportMethod]
    public void DoRemove()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) DeleteSelectedPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) DeleteSelectedOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) DeleteSelectedReceipt();
    }

    #region Rename 校验与提示（Modal 绑定 RenameDuplicateHint，非空时红色提示）
    /// <summary>清空重命名错误提示；弹窗打开时可调用。</summary>
    [ExportMethod]
    public void ClearRenameDuplicateHint()
    {
        SetRenameDuplicateHint("");
    }

    private void SetRenameDuplicateHint(string message)
    {
        try
        {
            var v = LogicObject.GetVariable("RenameDuplicateHint");
            if (v == null)
            {
                v = InformationModel.MakeVariable("RenameDuplicateHint", OpcUa.DataTypes.String);
                LogicObject.Add(v);
            }
            v.Value = message ?? "";
        }
        catch { }
    }

    private static bool PhaseFullNameTakenElsewhere(RecipeDatabaseTreeLoader loader, int exceptPhaseId, string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || loader == null) return false;
        foreach (var p in loader.PhaseById.Values)
        {
            if (p.PhaseID == exceptPhaseId) continue;
            if (string.Equals((p.Name ?? "").Trim(), fullName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool OperationFullNameTakenElsewhere(RecipeDatabaseTreeLoader loader, int exceptOperationId, string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || loader == null) return false;
        foreach (var o in loader.OperationById.Values)
        {
            if (o.OperationID == exceptOperationId) continue;
            if (string.Equals((o.Name ?? "").Trim(), fullName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool ReceiptFullNameTakenElsewhere(RecipeDatabaseTreeLoader loader, int exceptReceiptId, string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || loader == null) return false;
        foreach (var r in loader.ReceiptById.Values)
        {
            if (r.ReceiptID == exceptReceiptId) continue;
            if (string.Equals((r.Name ?? "").Trim(), fullName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>无后缀则分配 base 下下一个 _NNN；有后缀则原样使用（须全库唯一）。</summary>
    private static bool TryResolvePhaseRenameName(RecipeDatabaseTreeLoader loader, int phaseId, string input, out string finalName, out string error)
    {
        finalName = null;
        error = "";
        input = (input ?? "").Trim();
        if (string.IsNullOrEmpty(input)) { error = "名称为空"; return false; }
        ParseVersionSuffix(input, out string basePart, out int ver);
        if (ver >= 0)
        {
            if (PhaseFullNameTakenElsewhere(loader, phaseId, input))
            {
                error = $"名称「{input}」已存在（_ 前基名「{basePart}」下同全名不可重复）。请更换名称或版本号。";
                return false;
            }
            finalName = input;
            return true;
        }
        finalName = EnsureNameWithVersion(input, loader.PhaseById.Values, n => n.Name);
        if (PhaseFullNameTakenElsewhere(loader, phaseId, finalName))
        {
            error = "无法生成唯一名称，请手动指定带 _数字 后缀的名称。";
            return false;
        }
        return true;
    }

    private static bool TryResolveOperationRenameName(RecipeDatabaseTreeLoader loader, int operationId, string input, out string finalName, out string error)
    {
        finalName = null;
        error = "";
        input = (input ?? "").Trim();
        if (string.IsNullOrEmpty(input)) { error = "名称为空"; return false; }
        ParseVersionSuffix(input, out _, out int ver);
        if (ver >= 0)
        {
            if (OperationFullNameTakenElsewhere(loader, operationId, input))
            {
                error = $"名称「{input}」已存在。请更换名称或版本号。";
                return false;
            }
            finalName = input;
            return true;
        }
        finalName = EnsureNameWithVersion(input, loader.OperationById.Values, n => n.Name);
        if (OperationFullNameTakenElsewhere(loader, operationId, finalName))
        {
            error = "无法生成唯一名称，请手动指定带 _数字 后缀的名称。";
            return false;
        }
        return true;
    }

    private static bool TryResolveReceiptRenameName(RecipeDatabaseTreeLoader loader, int receiptId, string input, out string finalName, out string error)
    {
        finalName = null;
        error = "";
        input = (input ?? "").Trim();
        if (string.IsNullOrEmpty(input)) { error = "名称为空"; return false; }
        ParseVersionSuffix(input, out _, out int ver);
        if (ver >= 0)
        {
            if (ReceiptFullNameTakenElsewhere(loader, receiptId, input))
            {
                error = $"名称「{input}」已存在。请更换名称或版本号。";
                return false;
            }
            finalName = input;
            return true;
        }
        finalName = EnsureNameWithVersion(input, loader.Tree, n => n.Name);
        if (ReceiptFullNameTakenElsewhere(loader, receiptId, finalName))
        {
            error = "无法生成唯一名称，请手动指定带 _数字 后缀的名称。";
            return false;
        }
        return true;
    }
    #endregion

    /// <summary>重命名树当前选中项（重命名弹窗确认）。参数来自 UI <c>InputArguments/newName</c>。</summary>
    [ExportMethod]
    public void RenameSelectedTreeItem(string newName)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        if (GenerateTreeList.Instance == null) return;
        newName = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(newName)) { if (EnableLog) Log.Warning(LogCategory, "RenameSelectedTreeItem: 名称为空"); SetRenameDuplicateHint("名称为空"); return; }
        SetRenameDuplicateHint("");

        int rId = GenerateTreeList.Instance.SelectedReceiptId;
        int oId = GenerateTreeList.Instance.SelectedOperationId;
        int pId = GenerateTreeList.Instance.SelectedPhaseId;

        if (pId > 0)
        {
            if (!Loader.PhaseById.TryGetValue(pId, out var ph)) return;
            string oldName = ph.Name ?? "";
            if (!TryResolvePhaseRenameName(Loader, pId, newName, out string resolved, out string err))
            {
                SetRenameDuplicateHint(err);
                return;
            }
            if (string.Equals(oldName, resolved, StringComparison.Ordinal)) return;
            Loader.UpdatePhase(pId, resolved);
            if (GetAutosave())
            {
                Loader.Save();
                AfterReceiptDatabasePersisted();
            }
            else Loader.MarkDirtyPhase(pId);
            RefreshTreeList();
            GenerateOperationPhaseListPanel.Instance?.Generate();
            Audit("Act_Rename", new Dictionary<string, string> { ["name"] = oldName, ["newName"] = resolved, ["type"] = "Phase" });
            if (EnableLog) Log.Info(LogCategory, $"Rename Phase: '{oldName}' -> '{resolved}'");
            SetRenameDuplicateHint("");
            return;
        }
        if (oId > 0)
        {
            if (!Loader.OperationById.TryGetValue(oId, out var op)) return;
            string oldName = op.Name ?? "";
            if (!TryResolveOperationRenameName(Loader, oId, newName, out string resolvedOp, out string errOp))
            {
                SetRenameDuplicateHint(errOp);
                return;
            }
            if (string.Equals(oldName, resolvedOp, StringComparison.Ordinal)) return;
            Loader.UpdateOperation(oId, resolvedOp);
            if (GetAutosave())
            {
                Loader.Save();
                AfterReceiptDatabasePersisted();
            }
            else Loader.MarkDirtyOperation(oId);
            RefreshTreeList();
            GenerateOperationPhaseListPanel.Instance?.Generate();
            Audit("Act_Rename", new Dictionary<string, string> { ["name"] = oldName, ["newName"] = resolvedOp, ["type"] = "Operation" });
            if (EnableLog) Log.Info(LogCategory, $"Rename Operation: '{oldName}' -> '{resolvedOp}'");
            SetRenameDuplicateHint("");
            return;
        }
        if (rId > 0)
        {
            if (!Loader.ReceiptById.TryGetValue(rId, out var r)) return;
            string oldName = r.Name ?? "";
            if (!TryResolveReceiptRenameName(Loader, rId, newName, out string resolvedR, out string errR))
            {
                SetRenameDuplicateHint(errR);
                return;
            }
            if (string.Equals(oldName, resolvedR, StringComparison.Ordinal)) return;
            Loader.UpdateReceipt(rId, resolvedR, null);
            if (GetAutosave())
            {
                Loader.Save();
                AfterReceiptDatabasePersisted();
            }
            else Loader.MarkDirtyReceipt(rId);
            RefreshTreeList();
            GenerateOperationPhaseListPanel.Instance?.Generate();
            Audit("Act_Rename", new Dictionary<string, string> { ["name"] = oldName, ["newName"] = resolvedR, ["type"] = "Receipt" });
            if (EnableLog) Log.Info(LogCategory, $"Rename Receipt: '{oldName}' -> '{resolvedR}'");
            SetRenameDuplicateHint("");
            return;
        }
        if (EnableLog) Log.Warning(LogCategory, "RenameSelectedTreeItem: 未选中树项");
    }

    /// <summary>将当前内存中所有修改写入数据库，供“保存”按钮调用。Insert 仅缓存，需点保存才落库。</summary>
    [ExportMethod]
    public void DoSaveToDatabase()
    {
        if (Loader == null) return;
        Loader.Save();
        AfterReceiptDatabasePersisted();
        RefreshTreeList();
        GenerateOperationPhaseListPanel.Instance?.Generate();
        if (EnableLog) Log.Info(LogCategory, "已保存到数据库");
        string label = "";
        if (GenerateTreeList.Instance != null && Loader != null)
        {
            int rId = GenerateTreeList.Instance.SelectedReceiptId;
            if (rId > 0 && Loader.ReceiptById.TryGetValue(rId, out var rr)) label = rr.Name ?? "";
        }
        if (string.IsNullOrEmpty(label)) label = "RecipeEditor";
        Audit("Act_Save", new Dictionary<string, string> { ["name"] = label, ["type"] = "Receipt" });
    }
    #endregion

    #region Autosave 与保存/仅标记
    /// <summary>从 Logic 节点读取变量 autosave：true=每次修改自动保存，false=不自动保存、UI 用 * 标识未保存。</summary>
    private bool GetAutosave()
    {
        try
        {
            var v = LogicObject.GetVariable("autosave");
            if (v != null) return (bool)v.Value;
        }
        catch { }
        return true;
    }

    /// <summary><c>ReceiptDB.Receipts</c> 已持久化后，重建 Batch Editor 的 <c>RecipeList</c> 下拉数据源。</summary>
    private static void AfterReceiptDatabasePersisted()
    {
        BatchEditorLogic.RefreshRecipeListFromStore();
    }

    /// <param name="persistToDb">false 时仅标记脏+刷新树，不写入数据库（用于右侧 Insert）。</param>
    private void SaveOrMarkDirtyAndRefresh(int? receiptId = null, int? operationId = null, int? phaseId = null, bool persistToDb = true)
    {
        if (Loader == null) return;
        if (persistToDb && GetAutosave())
        {
            Loader.Save();
            AfterReceiptDatabasePersisted();
        }
        else
        {
            if (receiptId.HasValue) Loader.MarkDirtyReceipt(receiptId.Value);
            if (operationId.HasValue) Loader.MarkDirtyOperation(operationId.Value);
            if (phaseId.HasValue) Loader.MarkDirtyPhase(phaseId.Value);
        }
        RefreshTreeList();
    }

    /// <summary>Phase 面板输入变更：Buffer→内存树、<see cref="RecipeDatabaseTreeLoader.MarkDirtyPhase"/>（树名 *）；autosave 时立即 <see cref="RecipeDatabaseTreeLoader.Save"/>。</summary>
    [ExportMethod]
    public void NotifyPhaseParameterBufferEdited()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        int pId = GenerateTreeList.Instance.SelectedPhaseId;
        if (pId <= 0) return;
        try
        {
            bool wasDirty = Loader.IsDirtyPhase(pId);
            Loader.MergePhaseUiBufferIntoPhaseNode(pId);
            Loader.MarkDirtyPhase(pId);
            Loader.MarkModified();
            if (GetAutosave())
            {
                Loader.Save();
                AfterReceiptDatabasePersisted();
            }
            else if (!wasDirty)
            {
                RefreshTreeList();
                GenerateOperationPhaseListPanel.Instance?.Generate();
            }
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"NotifyPhaseParameterBufferEdited: {ex.Message}");
        }
    }
    #endregion

    #region 树移动按钮与保存前 Status 同步

    /// <summary>按当前树选中项更新 <c>SelectedTreeData</c> 的 EnableUp / EnableDown。</summary>
    public void RefreshTreeMoveButtonsEnabled()
    {
        var gt = GenerateTreeList.Instance;
        if (gt == null || Loader == null) return;
        var holder = gt.GetSelectedTreeDataNode();
        if (holder == null) return;
        var vUp = holder.GetVariable("EnableUp");
        var vDown = holder.GetVariable("EnableDown");
        if (vUp == null || vDown == null) return;
        int rId = gt.SelectedReceiptId;
        int oId = gt.SelectedOperationId;
        int pId = gt.SelectedPhaseId;
        if (!Loader.TryGetMoveAvailability(rId, oId, pId, out bool canUp, out bool canDown))
        {
            vUp.Value = new UAValue(false);
            vDown.Value = new UAValue(false);
            return;
        }
        vUp.Value = new UAValue(canUp);
        vDown.Value = new UAValue(canDown);
    }

    /// <summary>保存到数据库前将模型中 Receipt 状态下推到内存树（与下拉暂存一致）。</summary>
    public void SyncReceiptStatusFromModelBeforeSave()
    {
        var gt = GenerateTreeList.Instance;
        if (gt == null || Loader == null) return;
        int rId = gt.SelectedReceiptId;
        if (rId <= 0) return;
        string status = gt.ReadSelectedReceiptCurrentStatusFromModel();
        Loader.UpdateReceiptStatus(rId, status);
    }

    /// <summary>在工程就绪后补订阅（<c>Start</c> 时 <c>Project.Current</c> 可能尚未可用）。</summary>
    public void EnsureModifiableReceiptStatusObservers()
    {
        try
        {
            if (_modifiableReceiptStatusAffinityId == 0)
                _modifiableReceiptStatusAffinityId = LogicObject.Context.AssignAffinityId();
            TryAttachModifiableReceiptObserver(ref _modifiableReceiptStatusRegSelectedTree, TryGetSelectedReceiptCurrentStatusVariable());
            TryAttachModifiableReceiptObserver(ref _modifiableReceiptStatusRegReceiptStatusSet, TryGetUidataReceiptStatusSetVariable());
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"Modifiable status 订阅: {ex.Message}");
        }
    }

    /// <summary>切换树选中配方/工序/阶段时，将 <c>ReceiptStatusSet</c> 与内存树 Status 对齐（抑制订阅，避免误标脏）。</summary>
    public void PushReceiptStatusSetFromSelectedReceipt()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        var v = TryGetUidataReceiptStatusSetVariable();
        if (v == null) return;
        int rId = GenerateTreeList.Instance.SelectedReceiptId;
        string status;
        if (rId <= 0 || !Loader.ReceiptById.TryGetValue(rId, out var rNode))
            status = RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        else
            status = string.IsNullOrWhiteSpace(rNode.Status)
                ? RecipeDatabaseTreeLoader.DefaultReceiptStatus
                : rNode.Status.Trim();
        string cur = GetPlainStringFromVariableValue(v);
        if (string.Equals(cur, status, StringComparison.OrdinalIgnoreCase)) return;
        _modifiableReceiptStatusSuppressDepth++;
        try { v.Value = status; }
        finally { _modifiableReceiptStatusSuppressDepth--; }
    }

    private IUAVariable TryGetSelectedReceiptCurrentStatusVariable()
    {
        try
        {
            var v = Project.Current?.GetObject("Model/UIData/TreeListData/SelectedTreeData")?.GetVariable("SelectedReceiptCurrentStatus");
            if (v != null) return v;
            return GetRedeiptEditorRoot(LogicObject)?.GetObject("Model")?.GetObject("UIData")?.GetObject("TreeListData")?.GetObject("SelectedTreeData")?.GetVariable("SelectedReceiptCurrentStatus");
        }
        catch { return null; }
    }

    private IUAVariable TryGetUidataReceiptStatusSetVariable()
    {
        try
        {
            var v = Project.Current?.GetObject("Model/UIData")?.GetVariable("ReceiptStatusSet");
            if (v != null) return v;
            return GetRedeiptEditorRoot(LogicObject)?.GetObject("Model")?.GetObject("UIData")?.GetVariable("ReceiptStatusSet");
        }
        catch { return null; }
    }

    private void TryAttachModifiableReceiptObserver(ref IEventRegistration slot, IUAVariable v)
    {
        if (v == null || slot != null) return;
        var obs = new CallbackVariableChangeObserver((iv, nv, ov, accessPaths, sender) =>
            OnModifiableReceiptStatusModelChanged(iv));
        slot = v.RegisterEventObserver(obs, EventType.VariableValueChanged, _modifiableReceiptStatusAffinityId);
    }

    private void DetachModifiableReceiptStatusObservers()
    {
        try { _modifiableReceiptStatusRegSelectedTree?.Dispose(); }
        catch { }
        _modifiableReceiptStatusRegSelectedTree = null;
        try { _modifiableReceiptStatusRegReceiptStatusSet?.Dispose(); }
        catch { }
        _modifiableReceiptStatusRegReceiptStatusSet = null;
    }

    private void OnModifiableReceiptStatusModelChanged(IUAVariable iv)
    {
        if (_modifiableReceiptStatusSuppressDepth > 0) return;
        if (iv == null || Loader == null || GenerateTreeList.Instance == null) return;
        var tree = GenerateTreeList.Instance;
        int rId = tree.SelectedReceiptId;
        if (rId <= 0) return;
        string status = GetPlainStringFromVariableValue(iv);
        if (string.IsNullOrWhiteSpace(status) || status == "0")
            status = RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        else
            status = status.Trim();
        if (!Loader.ReceiptById.TryGetValue(rId, out var node)) return;
        string cur = string.IsNullOrWhiteSpace(node.Status)
            ? RecipeDatabaseTreeLoader.DefaultReceiptStatus
            : node.Status.Trim();
        if (string.Equals(cur, status, StringComparison.OrdinalIgnoreCase)) return;
        Loader.UpdateReceiptStatus(rId, status);
        if (GetAutosave())
        {
            Loader.Save();
            AfterReceiptDatabasePersisted();
            tree.RefreshAndKeepReceiptSelection(rId, status);
        }
        else
        {
            tree.RefreshAndKeepReceiptSelection(rId, status);
            GenerateOperationPhaseListPanel.Instance?.Generate();
        }
    }

    #endregion

    #region Discarded 删除辅助
    private static IUANode GetRedeiptEditorRoot(IUANode from)
    {
        IUANode n = from;
        while (n != null)
        {
            if (string.Equals(n.BrowseName, "RedeiptEditor", StringComparison.OrdinalIgnoreCase))
                return n;
            n = n.Owner;
        }
        return null;
    }

    private IUAObject GetSelectedDiscardedItemHolder()
    {
        var root = GetRedeiptEditorRoot(LogicObject);
        return root?.GetObject("Model")?.GetObject("UIData")?.GetObject("DiscardedData")?.GetObject("SelectedDiscardedItem");
    }

    private static int ReadDiscardedSelectedTap(IUAObject holder)
    {
        try
        {
            var v = holder?.GetVariable("SelectedTap");
            object inner = v?.Value?.Value;
            if (inner == null) return 0;
            if (inner is int i) return i;
            if (inner is long l) return (int)l;
            if (inner is short s) return s;
            return Convert.ToInt32(inner);
        }
        catch { return 0; }
    }

    private string TryResolveDiscardedNameFromCurrentQuery(int tap)
    {
        var root = GetRedeiptEditorRoot(LogicObject);
        var dd = root?.GetObject("Model")?.GetObject("UIData")?.GetObject("DiscardedData");
        if (dd == null) return "";
        string childName = tap == 0 ? "DiscardedRecipes" : tap == 1 ? "UnusedOperation" : tap == 2 ? "UnusedPhases" : "DiscardedRecipes";
        var qv = dd.GetObject(childName)?.GetVariable("Query");
        if (qv == null) return "";
        return TryFirstRowNameFromReceiptDbQuery(GetPlainStringFromVariableValue(qv));
    }

    private static string TryFirstRowNameFromReceiptDbQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "";
        string s = sql.Trim().TrimEnd(';');
        if (s.StartsWith("SELECT *", StringComparison.OrdinalIgnoreCase))
            s = "SELECT Name" + s.Substring("SELECT *".Length);
        if (s.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) < 0)
            s += " LIMIT 1";

        var store = Project.Current?.GetObject("DataStores")?.Get<Store>("ReceiptDB");
        if (store == null) return "";

        try
        {
            store.Query(s, out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return "";
            object cell = rows[0, 0];
            if (cell == null || cell == DBNull.Value) return "";
            return Convert.ToString(cell)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private bool TryDeleteDiscardedReceiptByName(string name)
    {
        int foundId = -1;
        foreach (var kv in Loader.ReceiptById)
        {
            if (!string.Equals(kv.Value.Name, name, StringComparison.Ordinal)) continue;
            string st = string.IsNullOrWhiteSpace(kv.Value.Status)
                ? RecipeDatabaseTreeLoader.DefaultReceiptStatus
                : kv.Value.Status.Trim();
            if (!st.Equals("Discarded", StringComparison.OrdinalIgnoreCase))
            {
                if (EnableLog) Log.Warning(LogCategory, $"配方「{name}」状态为「{st}」，非 Discarded，已拒绝删除。");
                return false;
            }
            if (foundId >= 0)
            {
                if (EnableLog) Log.Warning(LogCategory, $"多条 Discarded 同名「{name}」，已拒绝删除。");
                return false;
            }
            foundId = kv.Key;
        }
        if (foundId <= 0)
        {
            if (EnableLog) Log.Warning(LogCategory, $"未找到 Discarded 配方: 「{name}」。");
            return false;
        }
        return Loader.RemoveReceipt(foundId);
    }

    private bool TryDeleteUnusedOperationByName(string name)
    {
        var candidates = new System.Collections.Generic.List<int>();
        foreach (var kv in Loader.OperationById)
        {
            if (!string.Equals(kv.Value.Name, name, StringComparison.Ordinal)) continue;
            if (!Loader.IsOperationUnattachedToAnyReceipt(kv.Key)) continue;
            candidates.Add(kv.Key);
        }
        if (candidates.Count == 0)
        {
            if (EnableLog) Log.Warning(LogCategory, $"未找到可删独立工序: 「{name}」。");
            return false;
        }
        if (candidates.Count > 1 && EnableLog)
            Log.Warning(LogCategory, $"多条独立工序同名「{name}」，删 OperationID={candidates[0]}。");
        return Loader.RemoveStandaloneOperationForDiscardedGrid(candidates[0]);
    }

    private bool TryDeleteUnusedPhaseByName(string name)
    {
        var candidates = new System.Collections.Generic.List<int>();
        foreach (var kv in Loader.PhaseById)
        {
            if (!string.Equals(kv.Value.Name, name, StringComparison.Ordinal)) continue;
            bool referenced = false;
            foreach (var op in Loader.OperationById.Values)
            {
                if (op.Phases.Exists(p => p.PhaseID == kv.Key)) { referenced = true; break; }
            }
            if (referenced) continue;
            candidates.Add(kv.Key);
        }
        if (candidates.Count == 0)
        {
            if (EnableLog) Log.Warning(LogCategory, $"未找到可删独立阶段: 「{name}」。");
            return false;
        }
        if (candidates.Count > 1 && EnableLog)
            Log.Warning(LogCategory, $"多条独立阶段同名「{name}」，删 PhaseID={candidates[0]}。");
        return Loader.RemoveStandalonePhaseForDiscardedGrid(candidates[0]);
    }

    private static void ClearSelectedDiscardedItemName(IUAObject holder)
    {
        try
        {
            var v = holder?.GetVariable("SelectedItemName");
            if (v != null) v.Value = new UAValue("");
        }
        catch { }
    }
    #endregion

    #region 辅助
    private static void RefreshTreeList()
    {
        GenerateTreeList.Instance?.Generate();
    }

    /// <summary>从变量读取纯文本（支持 LocalizedText 与普通标量）。</summary>
    public static string GetPlainStringFromVariableValue(IUAVariable v)
    {
        if (v?.Value == null) return "";
        try
        {
            object inner = v.Value.Value;
            if (inner == null) return "";
            if (inner is LocalizedText lt)
                return lt.Text?.Trim() ?? "";
            return Convert.ToString(inner)?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string GetStringFromNode(NodeId nodeId)
    {
        if (nodeId == null || nodeId == NodeId.Empty) return "";
        var node = InformationModel.Get(nodeId);
        if (node is IUAVariable v)
        {
            var raw = v.Value;
            return CleanVariableDisplayString(raw);
        }
        return "";
    }

    private static string CleanVariableDisplayString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        int idx = s.IndexOf(" (", StringComparison.Ordinal);
        if (idx > 0) s = s.Substring(0, idx).Trim();
        return s;
    }

    private static void ParseVersionSuffix(string name, out string baseName, out int version)
    {
        baseName = name ?? "";
        version = -1;
        if (string.IsNullOrEmpty(baseName)) return;
        int lastUnderscore = baseName.LastIndexOf('_');
        if (lastUnderscore < 0) return;
        string suffix = baseName.Substring(lastUnderscore + 1);
        if (string.IsNullOrEmpty(suffix) || suffix.Length > 10) return;
        for (int i = 0; i < suffix.Length; i++)
            if (suffix[i] < '0' || suffix[i] > '9') return;
        if (int.TryParse(suffix, out int v)) { version = v; baseName = baseName.Substring(0, lastUnderscore); }
    }

    private static string EnsureNameWithVersion<T>(string name, System.Collections.Generic.IEnumerable<T> nodes, Func<T, string> getName)
    {
        ParseVersionSuffix(name, out string baseName, out int ver);
        if (ver < 0) baseName = name.Trim();
        if (string.IsNullOrEmpty(baseName)) return name;
        int nextVer = GetNextVersionForBase(baseName, nodes, getName);
        return baseName + "_" + nextVer.ToString("D3");
    }

    private static int GetNextVersionForBase<T>(string baseName, System.Collections.Generic.IEnumerable<T> nodes, Func<T, string> getName)
    {
        int maxVer = -1;
        string prefix = baseName + "_";
        foreach (var node in nodes)
        {
            string n = getName(node)?.Trim() ?? "";
            if (n.Equals(baseName, StringComparison.OrdinalIgnoreCase)) { if (maxVer < 0) maxVer = 0; continue; }
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            ParseVersionSuffix(n, out _, out int v);
            if (v >= 0 && v > maxVer) maxVer = v;
        }
        return maxVer + 1;
    }
    #endregion
}
