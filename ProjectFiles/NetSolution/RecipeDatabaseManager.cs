#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.SQLiteStore;
using FTOptix.Store;
#endregion

public class RecipeDatabaseManager : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseManager";
    private const bool EnableLog = true;  // 设为 false 关闭本类所有日志

    public static RecipeDatabaseManager Instance { get; private set; }

    private Store _store;
    private Table _receiptTable;
    private string _receiptTableName;
    private Table _opTable;
    private string _opTableName;
    private Table _phaseTable;
    private string _phaseTableName;

    #region 生命周期
    public override void Start()
    {
        Instance = this;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已启动");
        OpenReceiptTable();
    }

    public override void Stop()
    {
        Instance = null;
        _store = null;
        _receiptTable = null;
        _receiptTableName = null;
        _opTable = null;
        _opTableName = null;
        _phaseTable = null;
        _phaseTableName = null;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseManager 已停止");
    }

    /// <summary>解析 ReceiptDB/OperationDB/PhaseDB，打开 Store 与表并缓存。</summary>
    private void OpenReceiptTable()
    {
        _store = null;
        _receiptTable = null;
        _receiptTableName = null;
        _opTable = null;
        _opTableName = null;
        _phaseTable = null;
        _phaseTableName = null;

        if (!ResolveTable("ReceiptDB", out _store, out _receiptTable, out _receiptTableName)) return;
        if (!ResolveTable("OperationDB", out _, out _opTable, out _opTableName)) return;
        if (!ResolveTable("PhaseDB", out _, out _phaseTable, out _phaseTableName)) return;
    }

    private bool ResolveTable(string varName, out Store store, out Table table, out string tableName)
    {
        store = null;
        table = null;
        tableName = null;
        var v = LogicObject.GetVariable(varName);
        if (v == null) { if (EnableLog) Log.Error(LogCategory, $"未配置 {varName} 变量"); return false; }
        var node = InformationModel.Get(v.Value);
        if (node == null) { if (EnableLog) Log.Error(LogCategory, $"{varName} 指向的节点无效"); return false; }
        store = GetStoreFromNode(node);
        if (store == null) return false;
        tableName = node.BrowseName;
        table = node as Table ?? GetTableFromStoreByNodeId(store, node.NodeId);
        if (table == null && EnableLog) Log.Error(LogCategory, $"无法获取表: {tableName}");
        return true;
    }
    #endregion

    #region 暴露方法
    /// <summary>新增配方：从 nameNodeId、descriptNodeId 读取名称与描述，插入 Receipt 表并刷新树列表。名称末尾无版本号时自动补 _000。</summary>
    [ExportMethod]
    public void AddNewReceipt(NodeId nameNodeId, NodeId descriptNodeId)
    {
        string name = GetStringFromNode(nameNodeId);
        string descript = GetStringFromNode(descriptNodeId);
        if (string.IsNullOrWhiteSpace(name))
        {
            if (EnableLog) Log.Warning(LogCategory, "名称为空，未执行插入");
            return;
        }

        if (_receiptTable == null || _store == null)
        {
            if (EnableLog) Log.Error(LogCategory, "Receipt 表未就绪，请检查 ReceiptDB 配置");
            return;
        }

        name = EnsureNameWithVersion(name);
        if (EnableLog) Log.Info(LogCategory, $"AddNewReceipt: Name='{name}', Descript='{descript ?? ""}'");

        int nextReceiptId = GetNextReceiptID(_store, _receiptTableName);
        int nextSeq = GetNextSequence(_store, _receiptTableName);
        if (EnableLog) Log.Info(LogCategory, $"表={_receiptTableName}, ReceiptID={nextReceiptId}, Sequence={nextSeq}");

        if (!TryInsertReceipt(nextReceiptId, name, nextSeq, descript))
            return;

        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, "树列表已刷新");
    }

    /// <summary>另存为：将当前选中的 Receipt 另存为新配方（版本号+1）；其下 Operation、Phase 同样按版本号新建并关联。</summary>
    [ExportMethod]
    public void SaveAsReceipt()
    {
        if (_receiptTable == null || _store == null)
        {
            if (EnableLog) Log.Error(LogCategory, "Receipt 表未就绪");
            return;
        }
        int selectedId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        if (selectedId <= 0)
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中任何配方，无法另存为");
            return;
        }

        string name, operationsCsv, descript;
        int seq;
        if (!GetReceiptRow(selectedId, out name, out seq, out operationsCsv, out descript))
        {
            if (EnableLog) Log.Error(LogCategory, $"未找到 ReceiptID={selectedId}");
            return;
        }

        ParseVersionSuffix(name, out string baseName, out int _);
        string newName = baseName + "_" + GetNextVersionForBase(baseName).ToString("D3");

        string newOperationsCsv = operationsCsv;
        if (_opTable != null && _phaseTable != null)
            newOperationsCsv = SaveAsCopyOperationsAndPhases(ParseIdList(operationsCsv));

        int nextReceiptId = GetNextReceiptID(_store, _receiptTableName);
        int nextSeq = GetNextSequence(_store, _receiptTableName);
        if (!TryInsertReceipt(nextReceiptId, newName, nextSeq, descript, newOperationsCsv))
            return;

        RefreshTreeList();
        if (EnableLog) Log.Info(LogCategory, $"SaveAs 完成: '{name}' -> '{newName}', ReceiptID={nextReceiptId}");
    }

    /// <summary>遍历 Operation 列表，每个 Operation 及其 Phases 均按版本号新建，返回新 OperationID 逗号串。</summary>
    private string SaveAsCopyOperationsAndPhases(System.Collections.Generic.List<int> opIdList)
    {
        if (opIdList == null || opIdList.Count == 0) return "";
        var newOpIds = new System.Collections.Generic.List<int>();
        foreach (int opId in opIdList)
        {
            string opName, phasesCsv;
            if (!GetOperationRow(opId, out opName, out phasesCsv)) continue;
            ParseVersionSuffix(opName, out string opBase, out int _);
            string newOpName = opBase + "_" + GetNextVersionForBase(_store, _opTableName, opBase).ToString("D3");

            var phaseIdList = ParseIdList(phasesCsv);
            var newPhaseIds = new System.Collections.Generic.List<int>();
            foreach (int pId in phaseIdList)
            {
                string pName;
                var colNames = new System.Collections.Generic.List<string>();
                var colValues = new System.Collections.Generic.List<object>();
                if (!GetPhaseRow(pId, out pName, colNames, colValues)) continue;
                ParseVersionSuffix(pName, out string pBase, out int __);
                string newPName = pBase + "_" + GetNextVersionForBase(_store, _phaseTableName, pBase).ToString("D3");
                int newPId = GetNextId(_store, _phaseTableName, "PhaseID");
                if (!InsertPhase(newPId, newPName, colNames, colValues)) continue;
                newPhaseIds.Add(newPId);
            }
            string newPhasesCsv = string.Join(",", newPhaseIds);
            int newOpId = GetNextId(_store, _opTableName, "OperationID");
            if (!InsertOperation(newOpId, newOpName, newPhasesCsv)) continue;
            newOpIds.Add(newOpId);
        }
        return string.Join(",", newOpIds);
    }

    /// <summary>删除当前选中的 Receipt。</summary>
    [ExportMethod]
    public void DeleteSelectedReceipt()
    {
        if (_store == null || string.IsNullOrEmpty(_receiptTableName))
        {
            if (EnableLog) Log.Error(LogCategory, "Receipt 表未就绪");
            return;
        }
        int selectedId = GenerateTreeList.Instance?.SelectedReceiptId ?? -1;
        if (selectedId < 0)
        {
            if (EnableLog) Log.Warning(LogCategory, "未选中任何配方，无法删除");
            return;
        }
        try
        {
            _store.Query($"DELETE FROM {_receiptTableName} WHERE ReceiptID = {selectedId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"已删除 ReceiptID={selectedId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"删除失败: {ex.Message}");
        }
    }

    private bool TryInsertReceipt(int receiptId, string name, int nextSeq, string descript, string operations = null)
    {
        string ops = operations ?? "";
        try
        {
            _receiptTable.Insert(
                new[] { "ReceiptID", "Name", "Sequence", "Operations", "Description" },
                new object[,] { { receiptId, name, nextSeq, ops, descript ?? "" } });
            if (EnableLog) Log.Info(LogCategory, $"配方已插入: ReceiptID={receiptId}, Name='{name}'");
            return true;
        }
        catch (Exception)
        {
            try
            {
                _receiptTable.Insert(
                    new[] { "ReceiptID", "Name", "Sequence", "Operations", "Description" },
                    new object[,] { { receiptId, name, nextSeq, ops, descript ?? "" } });
                if (EnableLog) Log.Info(LogCategory, $"配方已插入(无 Description): ReceiptID={receiptId}, Name='{name}'");
                return true;
            }
            catch (Exception)
            {
                try
                {
                    _receiptTable.Insert(
                        new[] { "Name", "Sequence", "Operations", "Description" },
                        new object[,] { { name, nextSeq, ops, descript ?? "" } });
                    if (EnableLog) Log.Info(LogCategory, $"配方已插入(无 ReceiptID 列): Name='{name}'");
                    return true;
                }
                catch (Exception ex2)
                {
                    if (EnableLog) Log.Error(LogCategory, $"插入失败: {ex2.Message}");
                    return false;
                }
            }
        }
    }

    /// <summary>上移配方：与 Sequence 更小的那条交换 Sequence。</summary>
    /// <param name="receiptId">要上移的配方 ReceiptID</param>
    [ExportMethod]
    public void MoveUpReceipt()
    {
        if (!SwapSequenceWithNeighbor(GenerateTreeList.Instance.SelectedReceiptId, up: true))
            if (EnableLog) Log.Warning(LogCategory, $"上移失败或已在顶部: ReceiptID={GenerateTreeList.Instance.SelectedReceiptId}");
    }

    /// <summary>下移配方：与 Sequence 更大的那条交换 Sequence。</summary>
    /// <param name="receiptId">要下移的配方 ReceiptID</param>
    [ExportMethod]
    public void MoveDownReceipt()
    {
        if (!SwapSequenceWithNeighbor(GenerateTreeList.Instance.SelectedReceiptId, up: false))
            if (EnableLog) Log.Warning(LogCategory, $"下移失败或已在底部: ReceiptID={GenerateTreeList.Instance.SelectedReceiptId}");
    }

    private bool SwapSequenceWithNeighbor(int receiptId, bool up)
    {
        if (_store == null || string.IsNullOrEmpty(_receiptTableName)) return false;
        try
        {
            _store.Query($"SELECT ReceiptID, Sequence FROM {_receiptTableName} ORDER BY Sequence", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) < 2) return false;

            int rowCount = rows.GetLength(0);
            int colReceiptId = 0, colSeq = 1;
            int idx = -1;
            for (int i = 0; i < rowCount; i++)
            {
                var cell = rows[i, colReceiptId];
                if (cell != null && cell != DBNull.Value && int.TryParse(cell.ToString(), out int id) && id == receiptId)
                { idx = i; break; }
            }
            if (idx < 0) return false;

            int swapIdx = up ? idx - 1 : idx + 1;
            if (swapIdx < 0 || swapIdx >= rowCount) return false;

            int seqA = GetInt(rows[idx, colSeq]);
            int seqB = GetInt(rows[swapIdx, colSeq]);
            int otherId = (int)Convert.ChangeType(rows[swapIdx, colReceiptId], typeof(int));

            _store.Query($"UPDATE {_receiptTableName} SET Sequence = {seqB} WHERE ReceiptID = {receiptId}", out _, out _);
            _store.Query($"UPDATE {_receiptTableName} SET Sequence = {seqA} WHERE ReceiptID = {otherId}", out _, out _);

            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"{(up ? "上" : "下")}移成功: ReceiptID={receiptId}");
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"交换 Sequence 失败: {ex.Message}");
            return false;
        }
    }

    private static int GetInt(object cell)
    {
        if (cell == null || cell == DBNull.Value) return 0;
        return int.TryParse(cell.ToString(), out int v) ? v : 0;
    }
    #endregion

    #region Operation：Up / Down / Save / SaveAs / Remove
    /// <summary>上移当前选中的 Operation（在同一 Receipt 的 Operations 列表中与前一序交换）。</summary>
    [ExportMethod]
    public void MoveUpOperation()
    {
        if (!SwapOperationOrderInReceipt(up: true))
            if (EnableLog) Log.Warning(LogCategory, "上移 Operation 失败或未选中/已在顶部");
    }

    /// <summary>下移当前选中的 Operation。</summary>
    [ExportMethod]
    public void MoveDownOperation()
    {
        if (!SwapOperationOrderInReceipt(up: false))
            if (EnableLog) Log.Warning(LogCategory, "下移 Operation 失败或未选中/已在底部");
    }

    private bool SwapOperationOrderInReceipt(bool up)
    {
        int receiptId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (receiptId <= 0 || opId <= 0 || _store == null || string.IsNullOrEmpty(_receiptTableName)) return false;
        if (!GetReceiptRow(receiptId, out _, out _, out string operationsCsv, out _)) return false;
        var list = ParseIdList(operationsCsv);
        int idx = list.IndexOf(opId);
        if (idx < 0) return false;
        int swapIdx = up ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= list.Count) return false;
        list[idx] = list[swapIdx];
        list[swapIdx] = opId;
        string newCsv = string.Join(",", list);
        try
        {
            _store.Query($"UPDATE {_receiptTableName} SET Operations = '{EscapeSql(newCsv)}' WHERE ReceiptID = {receiptId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"Operation {(up ? "上" : "下")}移成功: ReceiptID={receiptId}, OperationID={opId}");
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SwapOperationOrder 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>保存当前选中的 Operation：用 nameNodeId、phasesNodeId 读取名称与 Phases，更新 Operation 表。</summary>
    [ExportMethod]
    public void SaveOperation(NodeId nameNodeId, NodeId phasesNodeId)
    {
        if (_store == null || string.IsNullOrEmpty(_opTableName)) { if (EnableLog) Log.Error(LogCategory, "表未就绪"); return; }
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (opId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation"); return; }
        string name = GetStringFromNode(nameNodeId);
        string phases = GetStringFromNode(phasesNodeId);
        try
        {
            if (!string.IsNullOrEmpty(name))
                _store.Query($"UPDATE {_opTableName} SET Name = '{EscapeSql(name)}' WHERE OperationID = {opId}", out _, out _);
            if (phases != null)
                _store.Query($"UPDATE {_opTableName} SET Phases = '{EscapeSql(phases)}' WHERE OperationID = {opId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"Operation 已保存: OperationID={opId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SaveOperation 失败: {ex.Message}");
        }
    }

    /// <summary>另存为当前选中的 Operation：新建版本并加入当前 Receipt 的 Operations 列表。</summary>
    [ExportMethod]
    public void SaveAsOperation()
    {
        if (_store == null || _opTable == null || string.IsNullOrEmpty(_opTableName)) return;
        int receiptId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (receiptId <= 0 || opId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt 或 Operation"); return; }
        if (!GetReceiptRow(receiptId, out _, out _, out string operationsCsv, out _)) return;
        if (!GetOperationRow(opId, out string opName, out string phasesCsv)) return;
        ParseVersionSuffix(opName, out string opBase, out int _);
        string newOpName = opBase + "_" + GetNextVersionForBase(_store, _opTableName, opBase).ToString("D3");
        var phaseIdList = ParseIdList(phasesCsv);
        var newPhaseIds = new System.Collections.Generic.List<int>();
        foreach (int pId in phaseIdList)
        {
            string pName;
            var colNames = new System.Collections.Generic.List<string>();
            var colValues = new System.Collections.Generic.List<object>();
            if (!GetPhaseRow(pId, out pName, colNames, colValues)) continue;
            ParseVersionSuffix(pName, out string pBase, out int __);
            string newPName = pBase + "_" + GetNextVersionForBase(_store, _phaseTableName, pBase).ToString("D3");
            int newPId = GetNextId(_store, _phaseTableName, "PhaseID");
            if (!InsertPhase(newPId, newPName, colNames, colValues)) continue;
            newPhaseIds.Add(newPId);
        }
        string newPhasesCsv = string.Join(",", newPhaseIds);
        int newOpId = GetNextId(_store, _opTableName, "OperationID");
        if (!InsertOperation(newOpId, newOpName, newPhasesCsv)) return;
        var opList = ParseIdList(operationsCsv);
        int insertIdx = opList.IndexOf(opId) + 1;
        if (insertIdx <= 0) insertIdx = opList.Count;
        opList.Insert(insertIdx, newOpId);
        string newOpsCsv = string.Join(",", opList);
        try
        {
            _store.Query($"UPDATE {_receiptTableName} SET Operations = '{EscapeSql(newOpsCsv)}' WHERE ReceiptID = {receiptId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"SaveAs Operation: '{opName}' -> '{newOpName}', OperationID={newOpId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SaveAsOperation 更新配方列表失败: {ex.Message}");
        }
    }

    /// <summary>删除当前选中的 Operation：从所属 Receipt 的 Operations 中移除，并删除 Operation 表行。</summary>
    [ExportMethod]
    public void DeleteSelectedOperation()
    {
        if (_store == null || string.IsNullOrEmpty(_receiptTableName)) { if (EnableLog) Log.Error(LogCategory, "表未就绪"); return; }
        int receiptId = GenerateTreeList.Instance?.SelectedReceiptId ?? 0;
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        if (receiptId <= 0 || opId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Receipt 或 Operation"); return; }
        if (!GetReceiptRow(receiptId, out _, out _, out string operationsCsv, out _)) return;
        var list = ParseIdList(operationsCsv);
        if (!list.Remove(opId)) return;
        string newCsv = string.Join(",", list);
        try
        {
            _store.Query($"UPDATE {_receiptTableName} SET Operations = '{EscapeSql(newCsv)}' WHERE ReceiptID = {receiptId}", out _, out _);
            _store.Query($"DELETE FROM {_opTableName} WHERE OperationID = {opId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"已删除 Operation: OperationID={opId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"DeleteSelectedOperation 失败: {ex.Message}");
        }
    }
    #endregion

    #region Phase：Up / Down / Save / SaveAs / Remove
    /// <summary>上移当前选中的 Phase（在同一 Operation 的 Phases 列表中与前一序交换）。</summary>
    [ExportMethod]
    public void MoveUpPhase()
    {
        if (!SwapPhaseOrderInOperation(up: true))
            if (EnableLog) Log.Warning(LogCategory, "上移 Phase 失败或未选中/已在顶部");
    }

    /// <summary>下移当前选中的 Phase。</summary>
    [ExportMethod]
    public void MoveDownPhase()
    {
        if (!SwapPhaseOrderInOperation(up: false))
            if (EnableLog) Log.Warning(LogCategory, "下移 Phase 失败或未选中/已在底部");
    }

    private bool SwapPhaseOrderInOperation(bool up)
    {
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int phaseId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (opId <= 0 || phaseId <= 0 || _store == null || string.IsNullOrEmpty(_opTableName)) return false;
        if (!GetOperationRow(opId, out _, out string phasesCsv)) return false;
        var list = ParseIdList(phasesCsv);
        int idx = list.IndexOf(phaseId);
        if (idx < 0) return false;
        int swapIdx = up ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= list.Count) return false;
        list[idx] = list[swapIdx];
        list[swapIdx] = phaseId;
        string newCsv = string.Join(",", list);
        try
        {
            _store.Query($"UPDATE {_opTableName} SET Phases = '{EscapeSql(newCsv)}' WHERE OperationID = {opId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"Phase {(up ? "上" : "下")}移成功: OperationID={opId}, PhaseID={phaseId}");
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SwapPhaseOrder 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>保存当前选中的 Phase：用 nameNodeId 更新 Phase 表 Name 列。</summary>
    [ExportMethod]
    public void SavePhase(NodeId nameNodeId)
    {
        if (_store == null || string.IsNullOrEmpty(_phaseTableName)) { if (EnableLog) Log.Error(LogCategory, "表未就绪"); return; }
        int phaseId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (phaseId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Phase"); return; }
        string name = GetStringFromNode(nameNodeId);
        try
        {
            if (!string.IsNullOrEmpty(name))
                _store.Query($"UPDATE {_phaseTableName} SET Name = '{EscapeSql(name)}' WHERE PhaseID = {phaseId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"Phase 已保存: PhaseID={phaseId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SavePhase 失败: {ex.Message}");
        }
    }

    /// <summary>另存为当前选中的 Phase：新建版本并加入当前 Operation 的 Phases 列表。</summary>
    [ExportMethod]
    public void SaveAsPhase()
    {
        if (_store == null || _phaseTable == null || string.IsNullOrEmpty(_opTableName)) return;
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int phaseId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (opId <= 0 || phaseId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation 或 Phase"); return; }
        if (!GetOperationRow(opId, out _, out string phasesCsv)) return;
        string pName;
        var colNames = new System.Collections.Generic.List<string>();
        var colValues = new System.Collections.Generic.List<object>();
        if (!GetPhaseRow(phaseId, out pName, colNames, colValues)) return;
        ParseVersionSuffix(pName, out string pBase, out int _);
        string newPName = pBase + "_" + GetNextVersionForBase(_store, _phaseTableName, pBase).ToString("D3");
        int newPId = GetNextId(_store, _phaseTableName, "PhaseID");
        if (!InsertPhase(newPId, newPName, colNames, colValues)) return;
        var list = ParseIdList(phasesCsv);
        int insertIdx = list.IndexOf(phaseId) + 1;
        if (insertIdx <= 0) insertIdx = list.Count;
        list.Insert(insertIdx, newPId);
        string newCsv = string.Join(",", list);
        try
        {
            _store.Query($"UPDATE {_opTableName} SET Phases = '{EscapeSql(newCsv)}' WHERE OperationID = {opId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"SaveAs Phase: '{pName}' -> '{newPName}', PhaseID={newPId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"SaveAsPhase 更新工序列表失败: {ex.Message}");
        }
    }

    /// <summary>删除当前选中的 Phase：从所属 Operation 的 Phases 中移除，并删除 Phase 表行。</summary>
    [ExportMethod]
    public void DeleteSelectedPhase()
    {
        if (_store == null || string.IsNullOrEmpty(_opTableName)) { if (EnableLog) Log.Error(LogCategory, "表未就绪"); return; }
        int opId = GenerateTreeList.Instance?.SelectedOperationId ?? 0;
        int phaseId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (opId <= 0 || phaseId <= 0) { if (EnableLog) Log.Warning(LogCategory, "未选中 Operation 或 Phase"); return; }
        if (!GetOperationRow(opId, out _, out string phasesCsv)) return;
        var list = ParseIdList(phasesCsv);
        if (!list.Remove(phaseId)) return;
        string newCsv = string.Join(",", list);
        try
        {
            _store.Query($"UPDATE {_opTableName} SET Phases = '{EscapeSql(newCsv)}' WHERE OperationID = {opId}", out _, out _);
            _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID = {phaseId}", out _, out _);
            RefreshTreeList();
            if (EnableLog) Log.Info(LogCategory, $"已删除 Phase: PhaseID={phaseId}");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"DeleteSelectedPhase 失败: {ex.Message}");
        }
    }
    #endregion

    #region 统一操作：一组按钮绑定此 5 个方法，根据当前选中 Receipt/Operation/Phase 自动分发
    /// <summary>统一上移。选中 Phase→上移 Phase；选中 Operation→上移 Operation；选中 Receipt→上移 Receipt。</summary>
    [ExportMethod]
    public void DoUp()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) MoveUpPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) MoveUpOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) MoveUpReceipt();
    }

    /// <summary>统一下移。</summary>
    [ExportMethod]
    public void DoDown()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) MoveDownPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) MoveDownOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) MoveDownReceipt();
    }

    /// <summary>统一保存。Phase 只传 nameNodeId；Operation 可传 nameNodeId + phasesNodeId。Receipt 无 Save。</summary>
    [ExportMethod]
    public void DoSave(NodeId nameNodeId)
    {
        DoSave(nameNodeId, NodeId.Empty);
    }

    [ExportMethod]
    public void DoSave(NodeId nameNodeId, NodeId phasesNodeId)
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) SavePhase(nameNodeId);
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) SaveOperation(nameNodeId, phasesNodeId ?? NodeId.Empty);
    }

    /// <summary>统一另存为。</summary>
    [ExportMethod]
    public void DoSaveAs()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) SaveAsPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) SaveAsOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) SaveAsReceipt();
    }

    /// <summary>统一删除。</summary>
    [ExportMethod]
    public void DoRemove()
    {
        if (GenerateTreeList.Instance == null) return;
        if (GenerateTreeList.Instance.SelectedPhaseId != 0) DeleteSelectedPhase();
        else if (GenerateTreeList.Instance.SelectedOperationId != 0) DeleteSelectedOperation();
        else if (GenerateTreeList.Instance.SelectedReceiptId != 0) DeleteSelectedReceipt();
    }
    #endregion

    #region 为 GenerateTreeList 提供选中项显示数据
    /// <summary>根据当前 Treelist 选中项，从数据库取回要写入 Model 的显示数据。</summary>
    public bool GetSelectedItemModelData(out string itemType, out string receiptName, out string receiptCreatedDate, out string receiptCreatedBy, out string receiptCurrentStatus, out string selectedOpName, out string selectedPhaseName)
    {
        itemType = "Receipt";
        receiptName = "";
        receiptCreatedDate = "";
        receiptCreatedBy = "";
        receiptCurrentStatus = "";
        selectedOpName = "";
        selectedPhaseName = "";
        if (GenerateTreeList.Instance == null) return false;
        int rId = GenerateTreeList.Instance.SelectedReceiptId;
        int oId = GenerateTreeList.Instance.SelectedOperationId;
        int pId = GenerateTreeList.Instance.SelectedPhaseId;
        if (rId > 0 && GetReceiptRow(rId, out receiptName, out _, out _, out string description))
            receiptCurrentStatus = description ?? "";
        if (pId > 0)
        {
            itemType = "Phase";
            GetPhaseRow(pId, out selectedPhaseName, null, null);
            if (oId > 0) GetOperationRow(oId, out selectedOpName, out _);
        }
        else if (oId > 0)
        {
            itemType = "Operation";
            GetOperationRow(oId, out selectedOpName, out _);
        }
        return true;
    }
    #endregion

    #region 辅助
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

    /// <summary>去掉变量 ToString 可能带上的类型后缀，如 " (String)"</summary>
    private static string CleanVariableDisplayString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        int idx = s.IndexOf(" (", StringComparison.Ordinal);
        if (idx > 0) s = s.Substring(0, idx).Trim();
        return s;
    }

    private static string EscapeSql(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("'", "''");
    }

    private static int GetNextSequence(Store store, string tableName)
    {
        try
        {
            store.Query($"SELECT Sequence FROM {tableName}", out _, out object[,] r);
            if (r == null || r.GetLength(0) == 0) return 1;
            int max = 0;
            for (int i = 0; i < r.GetLength(0); i++)
            {
                var cell = r[i, 0];
                if (cell != null && cell != DBNull.Value && int.TryParse(cell.ToString(), out int val) && val > max)
                    max = val;
            }
            return max + 1;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"获取下一 Sequence 失败: {tableName}, {ex.Message}");
        }
        return 1;
    }

    private static int GetNextReceiptID(Store store, string tableName)
    {
        try
        {
            store.Query($"SELECT ReceiptID FROM {tableName}", out _, out object[,] r);
            if (r == null || r.GetLength(0) == 0) return 1;
            int max = 0;
            for (int i = 0; i < r.GetLength(0); i++)
            {
                var cell = r[i, 0];
                if (cell != null && cell != DBNull.Value && int.TryParse(cell.ToString(), out int val) && val > max)
                    max = val;
            }
            return max + 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static Store GetStoreFromNode(IUANode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Store store) return store;
            current = current.Owner;
        }
        if (EnableLog) Log.Error(LogCategory, "无法从 Receipt 表节点向上找到 Store");
        return null;
    }

    /// <summary>从 Store 子节点中按 NodeId 查找 Table（InformationModel.Get 可能返回包装节点无法直接 as Table）</summary>
    private static Table GetTableFromStoreByNodeId(Store store, NodeId tableNodeId)
    {
        foreach (var child in store.Children)
        {
            if (child.NodeId == tableNodeId && child is Table t) return t;
        }
        return null;
    }

    private void RefreshTreeList()
    {
        if (GenerateTreeList.Instance == null)
        {
            if (EnableLog) Log.Warning(LogCategory, "GenerateTreeList 单例未就绪，跳过刷新");
            return;
        }
        GenerateTreeList.Instance.Generate();
    }
    #endregion

    #region 版本号与另存为
    /// <summary>解析名称末尾 _数字：如 Receipt_000 -> baseName=Receipt, version=0；无则 baseName=name, version=-1</summary>
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
        if (int.TryParse(suffix, out int v))
        {
            version = v;
            baseName = baseName.Substring(0, lastUnderscore);
        }
    }

    /// <summary>名称末尾无 _数字 时补全为 _000，并确保与表中已有名称不重复（取下一个可用版本号）</summary>
    private string EnsureNameWithVersion(string name)
    {
        ParseVersionSuffix(name, out string baseName, out int ver);
        if (ver < 0) baseName = name.Trim();
        if (string.IsNullOrEmpty(baseName)) return name;
        int nextVer = GetNextVersionForBase(baseName);
        return baseName + "_" + nextVer.ToString("D3");
    }

    /// <summary>表中已有该 base 下的最大版本号 +1，保证不重复</summary>
    private int GetNextVersionForBase(string baseName)
    {
        return GetNextVersionForBase(_store, _receiptTableName, baseName);
    }

    private static int GetNextVersionForBase(Store store, string tableName, string baseName)
    {
        if (store == null || string.IsNullOrEmpty(tableName)) return 0;
        try
        {
            store.Query($"SELECT Name FROM {tableName}", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return 0;
            int maxVer = -1;
            string prefix = baseName + "_";
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                string n = rows[i, 0]?.ToString()?.Trim() ?? "";
                if (n.Equals(baseName, StringComparison.OrdinalIgnoreCase)) { if (maxVer < 0) maxVer = 0; continue; }
                if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                ParseVersionSuffix(n, out _, out int v);
                if (v >= 0 && v > maxVer) maxVer = v;
            }
            return maxVer + 1;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"GetNextVersionForBase 失败: {tableName}, {ex.Message}");
            return 0;
        }
    }

    private bool GetReceiptRow(int receiptId, out string name, out int sequence, out string operations, out string description)
    {
        name = ""; sequence = 0; operations = ""; description = "";
        if (_store == null || string.IsNullOrEmpty(_receiptTableName)) return false;
        try
        {
            _store.Query($"SELECT Name, Sequence, Operations, Description FROM {_receiptTableName} WHERE ReceiptID = {receiptId}", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return false;
            name = rows[0, 0]?.ToString()?.Trim() ?? "";
            sequence = GetInt(rows[0, 1]);
            operations = rows[0, 2]?.ToString() ?? "";
            description = rows[0, 3]?.ToString() ?? "";
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"GetReceiptRow 失败: {ex.Message}");
            return false;
        }
    }
    private static System.Collections.Generic.List<int> ParseIdList(string csv)
    {
        var list = new System.Collections.Generic.List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string s in csv.Split(','))
        {
            if (int.TryParse(s?.Trim(), out int id)) list.Add(id);
        }
        return list;
    }

    private static int GetNextId(Store store, string tableName, string idColumnName)
    {
        if (store == null || string.IsNullOrEmpty(tableName)) return 1;
        try
        {
            store.Query($"SELECT {idColumnName} FROM {tableName}", out _, out object[,] r);
            if (r == null || r.GetLength(0) == 0) return 1;
            int max = 0;
            for (int i = 0; i < r.GetLength(0); i++)
            {
                var cell = r[i, 0];
                if (cell != null && cell != DBNull.Value && int.TryParse(cell.ToString(), out int val) && val > max)
                    max = val;
            }
            return max + 1;
        }
        catch (Exception) { return 1; }
    }

    private bool GetOperationRow(int operationId, out string name, out string phases)
    {
        name = ""; phases = "";
        if (_store == null || string.IsNullOrEmpty(_opTableName)) return false;
        try
        {
            _store.Query($"SELECT Name, Phases FROM {_opTableName} WHERE OperationID = {operationId}", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return false;
            name = rows[0, 0]?.ToString()?.Trim() ?? "";
            phases = rows[0, 1]?.ToString() ?? "";
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"GetOperationRow 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>从 DB 节点下 Phase 表节点（Table）的列定义获取列名（Table.Columns 或 Children）。</summary>
    private System.Collections.Generic.List<string> GetPhaseColumnNames()
    {
        var list = new System.Collections.Generic.List<string>();
        if (_phaseTable == null) return list;
        try
        {
            if (_phaseTable.Columns != null)
            {
                foreach (var col in _phaseTable.Columns)
                {
                    string name = col?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
            }
            if (list.Count == 0)
            {
                foreach (var child in _phaseTable.Children)
                {
                    string name = child?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"从 Phase 表节点获取列名失败: {ex.Message}，将仅拷贝 Name");
        }
        if (list.Count == 0) list.Add("Name");
        return list;
    }

    private bool GetPhaseRow(int phaseId, out string name, System.Collections.Generic.List<string> colNames, System.Collections.Generic.List<object> colValues)
    {
        name = "";
        colNames?.Clear();
        colValues?.Clear();
        if (_store == null || string.IsNullOrEmpty(_phaseTableName)) return false;
        var columns = GetPhaseColumnNames();
        if (columns.Count == 0) return false;
        try
        {
            string selectList = string.Join(", ", columns);
            _store.Query($"SELECT {selectList} FROM {_phaseTableName} WHERE PhaseID = {phaseId}", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return false;
            int nameIdx = columns.IndexOf("Name");
            name = nameIdx >= 0 ? rows[0, nameIdx]?.ToString()?.Trim() ?? "" : "";
            if (colNames != null) colNames.AddRange(columns);
            if (colValues != null)
                for (int c = 0; c < rows.GetLength(1); c++)
                    colValues.Add(rows[0, c] == null || rows[0, c] == DBNull.Value ? "" : rows[0, c]);
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"GetPhaseRow 失败: {ex.Message}");
            return false;
        }
    }

    private bool InsertOperation(int operationId, string name, string phases)
    {
        if (_store == null || _opTable == null) return false;
        try
        {
            _opTable.Insert(new[] { "OperationID", "Name", "Phases" }, new object[,] { { operationId, name, phases ?? "" } });
            if (EnableLog) Log.Info(LogCategory, $"Operation 已插入: OperationID={operationId}, Name='{name}'");
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"InsertOperation 失败: {ex.Message}");
            return false;
        }
    }

    private bool InsertPhase(int phaseId, string newName, System.Collections.Generic.List<string> colNames, System.Collections.Generic.List<object> colValues)
    {
        if (_store == null || _phaseTable == null) return false;
        bool noValidColumns = colNames == null || colValues == null || colNames.Count != colValues.Count;
        bool noPhaseId = colNames != null && colNames.Count > 0 && !colNames.Exists(c => c.Equals("PhaseID", StringComparison.OrdinalIgnoreCase));
        if (noValidColumns || noPhaseId)
        {
            try { _phaseTable.Insert(new[] { "PhaseID", "Name" }, new object[,] { { phaseId, newName ?? "" } }); return true; }
            catch (Exception ex) { if (EnableLog) Log.Error(LogCategory, $"InsertPhase 失败: {ex.Message}"); return false; }
        }
        var names = colNames;
        var values = new object[colValues.Count];
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Equals("PhaseID", StringComparison.OrdinalIgnoreCase)) values[i] = phaseId;
            else if (names[i].Equals("Name", StringComparison.OrdinalIgnoreCase)) values[i] = newName ?? "";
            else values[i] = colValues[i] == null || colValues[i] == DBNull.Value ? "" : colValues[i];
        }
        try
        {
            var row = new object[1, values.Length];
            for (int i = 0; i < values.Length; i++) row[0, i] = values[i];
            _phaseTable.Insert(names.ToArray(), row);
            if (EnableLog) Log.Info(LogCategory, $"Phase 已插入: PhaseID={phaseId}, Name='{newName}'");
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"InsertPhase 失败: {ex.Message}");
            return false;
        }
    }
    #endregion
}
