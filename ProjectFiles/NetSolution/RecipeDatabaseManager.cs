#region Using directives
using System;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.EventLogger;
#endregion

public class RecipeDatabaseManager : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseManager";
    private const bool EnableLog = true;

    public static RecipeDatabaseManager Instance { get; private set; }

    #region 生命周期
    public override void Start()
    {
        Instance = this;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已启动");
    }

    public override void Stop()
    {
        Instance = null;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已停止");
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
            receiptCurrentStatus = rNode.Status ?? "";
            receiptCreatedBy = rNode.CreatedBy ?? "";
            receiptCreatedDate = rNode.CreatedDateTime ?? "";
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

        string user = GetCurrentSessionUserBrowseName();
        string createdAt = FormatCreatedDateTimeLocal();
        int newId = Loader.AddReceipt(name, descript ?? "", user, createdAt);
        if (EnableLog) Log.Info(LogCategory, $"配方已加入内存树: ReceiptID={newId}, Name='{name}'");
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

        string user = GetCurrentSessionUserBrowseName();
        string createdAt = FormatCreatedDateTimeLocal();
        int newRId = Loader.AddReceipt(newName, src.Description ?? "", user, createdAt);
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
                Loader.AddPhase(newOpId, newPhName, new System.Collections.Generic.Dictionary<string, object>(srcPh.Columns));
            }
        }
        SaveOrMarkDirtyAndRefresh(receiptId: newRId);
        if (EnableLog) Log.Info(LogCategory, $"SaveAs Receipt: '{src.Name}' -> '{newName}', ReceiptID={newRId}");
    }

    /// <summary>删除当前选中的 Receipt（内存 + 持久化）。</summary>
    [ExportMethod]
    public void DeleteSelectedReceipt()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int selectedId = GenerateTreeList.Instance?.SelectedReceiptId ?? -1;
        if (selectedId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中任何配方，无法删除"); return; }
        if (!Loader.RemoveReceipt(selectedId)) { if (EnableLog) Log.Warning(LogCategory, $"RemoveReceipt 失败: ReceiptID={selectedId}"); return; }
        if (GetAutosave()) PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 ReceiptID={selectedId}");
    }

    [ExportMethod]
    public void MoveUpReceipt()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        int id = GenerateTreeList.Instance.SelectedReceiptId;
        if (!Loader.MoveReceiptUp(id)) { if (EnableLog) Log.Warning(LogCategory, $"上移失败或已在顶部: ReceiptID={id}"); return; }
        SaveOrMarkDirtyAndRefresh(receiptId: id);
    }

    [ExportMethod]
    public void MoveDownReceipt()
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        int id = GenerateTreeList.Instance.SelectedReceiptId;
        if (!Loader.MoveReceiptDown(id)) { if (EnableLog) Log.Warning(LogCategory, $"下移失败或已在底部: ReceiptID={id}"); return; }
        SaveOrMarkDirtyAndRefresh(receiptId: id);
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
        return true;
    }

    /// <summary>保存当前选中 Operation 的 Name（从 nameNodeId 读取）。</summary>
    [ExportMethod]
    public void SaveOperation(NodeId nameNodeId, NodeId phasesNodeId)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation"); return; }
        string name = GetStringFromNode(nameNodeId);
        if (!string.IsNullOrEmpty(name)) Loader.UpdateOperation(oId, name);
        PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"Operation 已保存: OperationID={oId}");
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
    public void SaveAsOperationFromSource(int sourceOperationId)
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
                Loader.AddPhase(newOpId, newPhName, new System.Collections.Generic.Dictionary<string, object>(srcPh.Columns));
            }
        }

        // 5. Insert 场景：仅缓存到内存树并刷新 UI，不立即写数据库
        SaveOrMarkDirtyAndRefresh(operationId: newOpId, persistToDb: false);
        if (EnableLog) Log.Info(LogCategory, $"SaveAs Operation: '{srcOp.Name}' -> '{newOpName}', OperationID={newOpId}");
    }

    /// <summary>删除当前选中的 Operation（内存 + 持久化）。</summary>
    [ExportMethod]
    public void DeleteSelectedOperation()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (rId <= 0 || oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt 或 Operation"); return; }
        if (!Loader.RemoveOperation(rId, oId)) return;
        if (GetAutosave()) PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 Operation: OperationID={oId}");
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
        return true;
    }

    /// <summary>保存当前选中 Phase 的 Name（从 nameNodeId 读取）。</summary>
    [ExportMethod]
    public void SavePhase(NodeId nameNodeId)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Phase"); return; }
        string name = GetStringFromNode(nameNodeId);
        if (!string.IsNullOrEmpty(name)) Loader.UpdatePhase(pId, name);
        PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"Phase 已保存: PhaseID={pId}");
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
    public void SaveAsPhaseFromSource(int sourcePhaseId)
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int pIdInsertAfter = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (oId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation"); return; }
        if (!Loader.OperationById.TryGetValue(oId, out var opNode)) return;
        if (!Loader.PhaseById.TryGetValue(sourcePhaseId, out var srcPh)) return;

        ParseVersionSuffix(srcPh.Name, out string phBase, out int _);
        string newPhName = phBase + "_" + GetNextVersionForBase(phBase, Loader.PhaseById.Values, n => n.Name).ToString("D3");
        int newPId = Loader.AddPhase(oId, newPhName, new System.Collections.Generic.Dictionary<string, object>(srcPh.Columns));

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
    }

    /// <summary>删除当前选中的 Phase（内存 + 持久化）。</summary>
    [ExportMethod]
    public void DeleteSelectedPhase()
    {
        if (Loader == null) { if (EnableLog) Log.Error(LogCategory, "TreeLoader 未就绪"); return; }
        int oId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (oId <= 0 || pId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation 或 Phase"); return; }
        if (!Loader.RemovePhase(oId, pId)) return;
        if (GetAutosave()) PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"已删除 Phase: PhaseID={pId}");
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

    #region 树节点重命名
    /// <summary>
    /// 按当前树选中项重命名：Phase → 改 Phase 名；Operation → 改 Operation 名；仅 Receipt → 改配方名（Description 不变）。
    /// 新名称若无末尾 <c>_数字</c> 版本后缀，则自动补 <c>_000</c> / 下一个可用三位版本号（排除当前节点，与新增命名规则一致）。
    /// 仅更新内存并标记脏；需调用 <see cref="DoSaveToDatabase"/> 才写入数据库。全名与其它节点重复（忽略大小写）时拒绝并写日志。
    /// </summary>
    [ExportMethod]
    public void RenameSelectedTreeItem(string newName)
    {
        if (Loader == null || GenerateTreeList.Instance == null) return;
        newName = newName?.Trim() ?? "";
        if (string.IsNullOrEmpty(newName))
        {
            if (EnableLog) Log.Warning(LogCategory, "重命名：新名称为空");
            return;
        }

        var tree = GenerateTreeList.Instance;
        if (tree.SelectedPhaseId > 0)
        {
            int pId = tree.SelectedPhaseId;
            newName = EnsureRenameNameWithVersion(newName, Loader.PhaseById.Values, ph => ph.Name, ph => ph.PhaseID == pId);
            if (string.IsNullOrEmpty(newName)) return;
            if (IsPhaseNameTakenByOther(Loader, newName, pId))
            {
                if (EnableLog) Log.Warning(LogCategory, $"重命名 Phase 失败：名称「{newName}」已存在");
                return;
            }
            Loader.UpdatePhase(pId, newName);
            SaveOrMarkDirtyAndRefresh(phaseId: pId, persistToDb: false);
            GenerateOperationPhaseListPanel.Instance?.Generate();
            if (EnableLog) Log.Info(LogCategory, $"Phase 已重命名: PhaseID={pId} -> '{newName}'");
            return;
        }
        if (tree.SelectedOperationId > 0)
        {
            int oId = tree.SelectedOperationId;
            newName = EnsureRenameNameWithVersion(newName, Loader.OperationById.Values, op => op.Name, op => op.OperationID == oId);
            if (string.IsNullOrEmpty(newName)) return;
            if (IsOperationNameTakenByOther(Loader, newName, oId))
            {
                if (EnableLog) Log.Warning(LogCategory, $"重命名 Operation 失败：名称「{newName}」已存在");
                return;
            }
            Loader.UpdateOperation(oId, newName);
            SaveOrMarkDirtyAndRefresh(operationId: oId, persistToDb: false);
            GenerateOperationPhaseListPanel.Instance?.Generate();
            if (EnableLog) Log.Info(LogCategory, $"Operation 已重命名: OperationID={oId} -> '{newName}'");
            return;
        }
        if (tree.SelectedReceiptId > 0)
        {
            int rId = tree.SelectedReceiptId;
            newName = EnsureRenameNameWithVersion(newName, Loader.Tree, r => r.Name, r => r.ReceiptID == rId);
            if (string.IsNullOrEmpty(newName)) return;
            if (IsReceiptNameTakenByOther(Loader, newName, rId))
            {
                if (EnableLog) Log.Warning(LogCategory, $"重命名 Receipt 失败：名称「{newName}」已存在");
                return;
            }
            Loader.UpdateReceipt(rId, newName, description: null);
            SaveOrMarkDirtyAndRefresh(receiptId: rId, persistToDb: false);
            if (EnableLog) Log.Info(LogCategory, $"Receipt 已重命名: ReceiptID={rId} -> '{newName}'");
            return;
        }
        if (EnableLog) Log.Warning(LogCategory, "重命名：未选中 Receipt / Operation / Phase");
    }
    #endregion

    /// <summary>将当前内存中所有修改写入数据库，供“保存”按钮调用。Insert 仅缓存，需点保存才落库。</summary>
    [ExportMethod]
    public void DoSaveToDatabase()
    {
        if (Loader == null) return;
        PersistLoaderWithStagedReceiptStatus();
        RefreshTreeList();
        GenerateOperationPhaseListPanel.Instance?.Generate();
        if (EnableLog) Log.Info(LogCategory, "已保存到数据库");
    }
    #endregion

    #region Status 暂存（SelectedTreeData/SelectedReceiptCurrentStatus）与落库
    private void ApplyStagingReceiptStatusToMemory()
    {
        int rId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (rId <= 0 || Loader == null) return;
        string staged = GenerateTreeList.Instance?.ReadSelectedReceiptCurrentStatusFromModel() ?? "";
        if (string.IsNullOrWhiteSpace(staged)) return;
        Loader.UpdateReceiptStatus(rId, staged);
    }

    private void PersistLoaderWithStagedReceiptStatus()
    {
        if (Loader == null) return;
        ApplyStagingReceiptStatusToMemory();
        Loader.Save();
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

    /// <param name="persistToDb">false 时仅标记脏+刷新树，不写入数据库（用于右侧 Insert）。</param>
    private void SaveOrMarkDirtyAndRefresh(int? receiptId = null, int? operationId = null, int? phaseId = null, bool persistToDb = true)
    {
        if (Loader == null) return;
        if (persistToDb && GetAutosave())
            PersistLoaderWithStagedReceiptStatus();
        else
        {
            if (receiptId.HasValue) Loader.MarkDirtyReceipt(receiptId.Value);
            if (operationId.HasValue) Loader.MarkDirtyOperation(operationId.Value);
            if (phaseId.HasValue) Loader.MarkDirtyPhase(phaseId.Value);
        }
        RefreshTreeList();
    }
    #endregion

    #region 会话与创建时间
    /// <summary>当前登录用户在 Session 下的 BrowseName（与联机帮助中 {Session}/User@BrowseName 一致）。</summary>
    private string GetCurrentSessionUserBrowseName()
    {
        try
        {
            var u = Session.User;
            if (u != null && !string.IsNullOrEmpty(u.BrowseName))
                return u.BrowseName.Trim();
        }
        catch { }
        return "";
    }

    /// <summary>与 <see cref="RecipeDatabaseTreeLoader.CreatedDateTimeStorageFormat"/> 一致，由 TreeLoader 统一格式化。</summary>
    private string FormatCreatedDateTimeLocal()
    {
        return RecipeDatabaseTreeLoader.FormatStoredCreatedDateTimeNow();
    }
    #endregion

    #region 辅助
    private static void RefreshTreeList()
    {
        GenerateTreeList.Instance?.Generate();
    }

    private static string GetStringFromNode(NodeId nodeId)
    {
        if (nodeId == null || nodeId == NodeId.Empty) return "";
        var node = InformationModel.Get(nodeId);
        if (node is IUAVariable v)
            return GetPlainStringFromVariableValue(v);
        return "";
    }

    /// <summary>从变量取出界面可用的纯文本：<see cref="LocalizedText"/> 取 Text；其它取内部值并去掉 ToString 产生的「 … (String)」等后缀。</summary>
    public static string GetPlainStringFromVariableValue(IUAVariable v)
    {
        if (v?.Value == null) return "";
        object inner;
        try { inner = v.Value.Value; }
        catch { inner = null; }
        if (inner == null)
            return CleanVariableDisplayString(v.Value.ToString() ?? "");
        if (inner is LocalizedText lt)
            return CleanVariableDisplayString(lt.Text?.Trim() ?? "");
        return CleanVariableDisplayString(inner.ToString() ?? "");
    }

    private static bool IsReceiptNameTakenByOther(RecipeDatabaseTreeLoader loader, string name, int exceptReceiptId)
    {
        if (loader == null || string.IsNullOrEmpty(name)) return false;
        foreach (var r in loader.Tree)
        {
            if (r.ReceiptID == exceptReceiptId) continue;
            if (string.Equals(r.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsOperationNameTakenByOther(RecipeDatabaseTreeLoader loader, string name, int exceptOperationId)
    {
        if (loader == null || string.IsNullOrEmpty(name)) return false;
        foreach (var op in loader.OperationById.Values)
        {
            if (op.OperationID == exceptOperationId) continue;
            if (string.Equals(op.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsPhaseNameTakenByOther(RecipeDatabaseTreeLoader loader, string name, int exceptPhaseId)
    {
        if (loader == null || string.IsNullOrEmpty(name)) return false;
        foreach (var ph in loader.PhaseById.Values)
        {
            if (ph.PhaseID == exceptPhaseId) continue;
            if (string.Equals(ph.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string CleanVariableDisplayString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        int idx = s.IndexOf(" (", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>重命名专用：无合法 <c>_NNN</c> 后缀时补三位版本号；统计已占用版本时排除当前节点自身。</summary>
    private static string EnsureRenameNameWithVersion<T>(string name, System.Collections.Generic.IEnumerable<T> nodes, Func<T, string> getName, Func<T, bool> excludeNode)
    {
        string trimmed = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed)) return trimmed;
        ParseVersionSuffix(trimmed, out string baseName, out int ver);
        if (ver >= 0) return trimmed;
        baseName = trimmed;
        if (string.IsNullOrEmpty(baseName)) return trimmed;
        int nextVer = GetNextVersionForBaseExcluding(baseName, nodes, getName, excludeNode);
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

    private static int GetNextVersionForBaseExcluding<T>(string baseName, System.Collections.Generic.IEnumerable<T> nodes, Func<T, string> getName, Func<T, bool> excludeNode)
    {
        int maxVer = -1;
        string prefix = baseName + "_";
        foreach (var node in nodes)
        {
            if (excludeNode != null && excludeNode(node)) continue;
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
