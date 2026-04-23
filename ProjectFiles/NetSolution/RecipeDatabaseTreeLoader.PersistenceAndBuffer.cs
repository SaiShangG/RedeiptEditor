#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
using FTOptix.WebUI;
#endregion

public partial class RecipeDatabaseTreeLoader
{
    #region Save 与同步删除
    /// <summary>将本地树整体写回数据库（新增/更新），删除 DB 中本地已不存在的条目，最后重新加载树并刷新 UI。</summary>
    [ExportMethod]
    public void Save()
    {
        if (_store == null || string.IsNullOrEmpty(_receiptTableName))
        {
            if (EnableLog) Log.Warning(LogCategory, "数据库未就绪，跳过 Save");
            return;
        }
        RecipeDatabaseManager.Instance?.SyncReceiptStatusFromModelBeforeSave();
        FlushSelectedPhaseUiBufferToTreeForPersist();
        try
        {
            // 1. 读取 DB 中现有 ID 集合
            var dbReceiptIds = QueryIdSet(_receiptTableName, "ReceiptID");
            var dbOpIds = string.IsNullOrEmpty(_opTableName) ? new HashSet<int>() : QueryIdSet(_opTableName, "OperationID");
            var dbPhaseIds = string.IsNullOrEmpty(_phaseTableName) ? new HashSet<int>() : QueryIdSet(_phaseTableName, "PhaseID");

            // 2. 以本地树为准写回 DB（增/改）
            foreach (var receipt in Tree)
            {
                string opsCsv = BuildIdCsv(receipt.Operations, op => op.OperationID);
                receipt.OperationsCsv = opsCsv;
                if (HasReceiptColumn("CreatedDateTime") && !string.IsNullOrWhiteSpace(receipt.CreatedDateTime))
                    receipt.CreatedDateTime = NormalizeStoredCreatedDateTime(receipt.CreatedDateTime);

                if (dbReceiptIds.Contains(receipt.ReceiptID))
                {
                    var setParts = new List<string>
                    {
                        $"Name='{EscapeSql(receipt.Name)}'",
                        $"Sequence={receipt.Sequence}",
                        $"Operations='{EscapeSql(opsCsv)}'",
                        $"Description='{EscapeSql(receipt.Description)}'"
                    };
                    if (HasReceiptColumn("Status"))
                        setParts.Add($"Status='{EscapeSql(string.IsNullOrEmpty(receipt.Status) ? DefaultReceiptStatus : receipt.Status)}'");
                    if (HasReceiptColumn("CreatedBy"))
                        setParts.Add($"CreatedBy='{EscapeSql(receipt.CreatedBy ?? "")}'");
                    if (HasReceiptColumn("CreatedDateTime"))
                        setParts.Add($"CreatedDateTime='{EscapeSql(receipt.CreatedDateTime ?? "")}'");
                    if (ReceiptNeedsLastModifiedStamp(receipt.ReceiptID))
                    {
                        if (HasReceiptColumn("LastModifiedBY"))
                            setParts.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForReceipt(receipt))}'");
                        if (HasReceiptColumn("LastModifiedDateTime"))
                            setParts.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
                    }
                    _store.Query(
                        $"UPDATE {_receiptTableName} SET {string.Join(", ", setParts)} WHERE ReceiptID={receipt.ReceiptID}",
                        out _, out _);
                }
                else
                {
                    var insCols = new List<string> { "ReceiptID", "Name", "Sequence", "Operations", "Description" };
                    var insVals = new List<object> { receipt.ReceiptID, receipt.Name, receipt.Sequence, opsCsv, receipt.Description };
                    if (HasReceiptColumn("Status")) { insCols.Add("Status"); insVals.Add(string.IsNullOrEmpty(receipt.Status) ? DefaultReceiptStatus : receipt.Status); }
                    if (HasReceiptColumn("CreatedBy")) { insCols.Add("CreatedBy"); insVals.Add(receipt.CreatedBy ?? ""); }
                    if (HasReceiptColumn("CreatedDateTime")) { insCols.Add("CreatedDateTime"); insVals.Add(receipt.CreatedDateTime ?? ""); }
                    if (HasReceiptColumn("LastModifiedBY")) { insCols.Add("LastModifiedBY"); insVals.Add(GetLastModifiedByForReceipt(receipt)); }
                    if (HasReceiptColumn("LastModifiedDateTime")) { insCols.Add("LastModifiedDateTime"); insVals.Add(receipt.CreatedDateTime ?? ""); }
                    var row = new object[1, insVals.Count];
                    for (int c = 0; c < insVals.Count; c++) row[0, c] = insVals[c];
                    _receiptTable.Insert(insCols.ToArray(), row);
                }

                foreach (var op in receipt.Operations)
                {
                    string phsCsv = BuildIdCsv(op.Phases, ph => ph.PhaseID);
                    op.PhasesCsv = phsCsv;
                    if (dbOpIds.Contains(op.OperationID))
                    {
                        var opSet = new List<string>
                        {
                            $"Name='{EscapeSql(op.Name)}'",
                            $"Description='{EscapeSql(op.Description ?? "")}'",
                            $"Phases='{EscapeSql(phsCsv)}'"
                        };
                        if (OperationNeedsLastModifiedStamp(op.OperationID))
                        {
                            if (HasOperationColumn("LastModifiedBY"))
                                opSet.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForOperation(op))}'");
                            if (HasOperationColumn("LastModifiedDateTime"))
                                opSet.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
                        }
                        _store.Query(
                            $"UPDATE {_opTableName} SET {string.Join(", ", opSet)} WHERE OperationID={op.OperationID}",
                            out _, out _);
                    }
                    else
                    {
                        var insCols = new List<string> { "OperationID", "Name", "Description", "Phases" };
                        var insVals = new List<object> { op.OperationID, op.Name, op.Description ?? "", phsCsv };
                        if (HasOperationColumn("CreatedBy")) { insCols.Add("CreatedBy"); insVals.Add(op.CreatedBy ?? ""); }
                        if (HasOperationColumn("CreatedDateTime")) { insCols.Add("CreatedDateTime"); insVals.Add(op.CreatedDateTime ?? ""); }
                        if (HasOperationColumn("LastModifiedBY")) { insCols.Add("LastModifiedBY"); insVals.Add(op.CreatedBy ?? ""); }
                        if (HasOperationColumn("LastModifiedDateTime")) { insCols.Add("LastModifiedDateTime"); insVals.Add(op.CreatedDateTime ?? FormatStoredCreatedDateTimeNow()); }
                        var row = new object[1, insVals.Count];
                        for (int c = 0; c < insVals.Count; c++) row[0, c] = insVals[c];
                        _opTable.Insert(insCols.ToArray(), row);
                    }

                    foreach (var ph in op.Phases)
                    {
                        ph.Columns["PhaseID"] = ph.PhaseID;
                        ph.Columns["Name"] = ph.Name;
                        if (dbPhaseIds.Contains(ph.PhaseID))
                        {
                            SavePhaseUpdate(ph);
                            if (IsDirtyPhase(ph.PhaseID))
                                TryPersistPhaseType1FromPhaseNode(ph);
                        }
                        else
                        {
                            SavePhaseInsert(ph);
                            if (IsDirtyPhase(ph.PhaseID))
                                TryPersistPhaseType1FromPhaseNode(ph);
                        }
                    }
                }
            }

            // 3. 删除 DB 中本地已不存在的条目
            foreach (int id in dbReceiptIds)
                if (!ReceiptById.ContainsKey(id))
                    _store.Query($"DELETE FROM {_receiptTableName} WHERE ReceiptID={id}", out _, out _);

            if (!string.IsNullOrEmpty(_opTableName))
                foreach (int id in dbOpIds)
                    if (!OperationById.ContainsKey(id))
                        _store.Query($"DELETE FROM {_opTableName} WHERE OperationID={id}", out _, out _);

            if (!string.IsNullOrEmpty(_phaseTableName))
                foreach (int id in dbPhaseIds)
                    if (!PhaseById.ContainsKey(id))
                    {
                        int paramInfoId = QueryPhaseParameterInfoIdFromDb(id);
                        DeletePhaseType1RowIfAny(paramInfoId);
                        _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID={id}", out _, out _);
                    }

            if (EnableLog) Log.Info(LogCategory, "Save 完成，正在重新加载树...");
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"Save 失败: {ex.Message}");
            return;
        }

        // 4. 重载（内部已清零 IsModified）、清除未保存标记并刷新 UI
        LoadAllToTree();
        ClearDirty();
        GenerateTreeList.Instance?.Generate();
    }

    /// <summary>
    /// 写库前将当前选中 Phase 的模板 UDT 并入 <see cref="PhaseNode.Columns"/> 并标脏。
    /// 否则 <see cref="TryPersistPhaseType1FromPhaseNode"/> 因未标脏被跳过，或缓冲未合并导致新列不落库。
    /// </summary>
    public void FlushSelectedPhaseUiBufferToTreeForPersist()
    {
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) return;
        MergeUdtPhaseTemplateBufferIntoPhaseNode(pId);
        MarkDirtyPhase(pId);
    }

    private void ClearPhaseType1TableCache()
    {
        _phaseType1Table = null;
    }

    private HashSet<int> QueryIdSet(string tableName, string idColumn)
    {
        var set = new HashSet<int>();
        try
        {
            _store.Query($"SELECT {idColumn} FROM {tableName}", out _, out object[,] rows);
            if (rows == null) return set;
            for (int r = 0; r < rows.GetLength(0); r++) set.Add(CellToInt(rows[r, 0]));
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"QueryIdSet({tableName}) 失败: {ex.Message}");
        }
        return set;
    }

    private void AppendPhaseLastModifiedSetClauses(PhaseNode ph, List<string> setClauses)
    {
        if (!PhaseNeedsLastModifiedStamp(ph.PhaseID)) return;
        if (HasPhaseColumn("LastModifiedBY"))
            setClauses.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForPhase(ph))}'");
        if (HasPhaseColumn("LastModifiedDateTime"))
            setClauses.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
    }

    #region PhaseParametersDB 与 PhaseType1
    private bool TryGetPhaseParametersStore(out Store store)
    {
        store = _phaseParamsStore;
        if (store != null) return true;
        try
        {
            var ds = Project.Current?.GetObject("DataStores");
            if (ds == null) return false;
            _phaseParamsStore = ds.Get<Store>(PhaseParametersDbName);
            store = _phaseParamsStore;
            return store != null;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TryGetPhaseParametersStore: {ex.Message}");
            return false;
        }
    }

    private bool TryGetPhaseType1Table(out Table table)
    {
        if (_phaseType1Table != null)
        {
            table = _phaseType1Table;
            return true;
        }
        if (!TryGetPhaseParametersStore(out var ps))
        {
            table = null;
            return false;
        }
        _phaseType1Table = ps.Tables.Get<Table>(PhaseType1TableName);
        table = _phaseType1Table;
        return table != null;
    }

    /// <summary>表节点列（Optix Store.Query 不支持 PRAGMA，列以 Studio 表定义为准）。</summary>
    private static List<string> GetPhaseType1ModelColumnNames(Table table)
    {
        var list = new List<string>();
        if (table == null) return list;
        try
        {
            if (table.Columns != null)
                foreach (var col in table.Columns)
                {
                    string n = col?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(n)) list.Add(n);
                }
            if (list.Count == 0)
                foreach (var child in table.Children)
                {
                    string n = child?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(n)) list.Add(n);
                }
        }
        catch { }
        return list;
    }

    /// <summary>PhaseType1 业务数据列（表模型顺序）；重命名 Parameter1 或新增列后自动包含。</summary>
    private static List<string> GetPhaseType1DataColumnNames(Table t1)
    {
        var list = new List<string>();
        if (t1 == null) return list;
        foreach (var col in GetPhaseType1ModelColumnNames(t1))
        {
            if (string.IsNullOrEmpty(col) || PhaseType1ReservedColumns.Contains(col)) continue;
            list.Add(col);
        }
        return list;
    }

    private static bool IsSafeSqlIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i == 0) { if (!(char.IsLetter(c) || c == '_')) return false; }
            else if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    /// <summary>从布局 JSON 收集扁平缓冲变量名：<c>bind.uiProperty==Checked</c> → Boolean，否则 String；键为 <c>bufferField</c> 将 «.» 换为 «_»。</summary>
    private static void CollectPhaseLayoutKeysFromJson(out HashSet<string> booleanBufferKeys, out List<string> allBufferKeyNames)
    {
        booleanBufferKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        allBufferKeyNames = new List<string>();
        try
        {
            string jp = PhaseUILayoutJson.ResolveLayoutPath(PhaseLayoutJsonFileName);
            var layout = PhaseUILayoutJson.TryLoadFromFile(jp);
            if (layout?.Sections == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sec in layout.Sections)
            {
                if (sec?.Items == null) continue;
                foreach (var it in sec.Items)
                {
                    if (it?.Bind == null || string.IsNullOrWhiteSpace(it.Bind.BufferField)) continue;
                    // 扁平缓冲键：PP.Valve → PP_Valve（与 UDT 路径对应，供 Type1 列名扩展时对齐）
                    string k = it.Bind.BufferField.Trim().Replace('.', '_');
                    bool asBool = string.Equals(it.Bind.UiProperty, "Checked", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(k) || !IsSafeSqlIdentifier(k)) continue;
                    if (seen.Add(k))
                        allBufferKeyNames.Add(k);
                    if (asBool)
                        booleanBufferKeys.Add(k);
                }
            }
        }
        catch { }
    }

    private static List<string> TryGetPhaseLayoutBindKeys()
    {
        CollectPhaseLayoutKeysFromJson(out _, out var all);
        return all;
    }

    private static object GetInitialType1InsertCell(PhaseNode ph, string col)
    {
        if (ph?.Columns != null && ph.Columns.TryGetValue(col, out var v) && v != null && v != DBNull.Value)
            return v;
        return 0;
    }

    private static object NormalizeType1ValueForColumnCopy(object v)
    {
        if (v == null || v == DBNull.Value) return 0;
        if (v is int || v is long || v is short || v is byte || v is bool || v is double || v is float || v is decimal)
            return v;
        string s = ParamToBufferString(v);
        return int.TryParse(s.Trim(), out int i) ? (object)i : s;
    }

    private static string CellToSqlLiteralForPhaseType1(object v)
    {
        if (v == null || v == DBNull.Value) return "NULL";
        if (v is bool b) return b ? "1" : "0";
        if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
        if (v is long l) return l.ToString(CultureInfo.InvariantCulture);
        if (v is short s) return s.ToString(CultureInfo.InvariantCulture);
        if (v is byte by) return by.ToString(CultureInfo.InvariantCulture);
        if (v is float f) return f.ToString(CultureInfo.InvariantCulture);
        if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
        if (v is decimal m) return m.ToString(CultureInfo.InvariantCulture);
        string str = v.ToString();
        if (int.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ip))
            return ip.ToString(CultureInfo.InvariantCulture);
        if (double.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dp))
            return dp.ToString(CultureInfo.InvariantCulture);
        return $"'{EscapeSql(str)}'";
    }

    private int AllocateNextPhaseParameterInfoId()
    {
        if (!TryGetPhaseParametersStore(out var ps)) return 1;
        try
        {
            ps.Query($"SELECT MAX(PhaseParameterInfoID) FROM {PhaseType1TableName}", out _, out object[,] rows);
            if (rows != null && rows.GetLength(0) > 0)
                return CellToInt(rows[0, 0]) + 1;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"AllocateNextPhaseParameterInfoId MAX: {ex.Message}");
        }
        try
        {
            ps.Query($"SELECT PhaseParameterInfoID FROM {PhaseType1TableName}", out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return 1;
            int max = 0;
            for (int r = 0; r < rows.GetLength(0); r++)
            {
                int v = CellToInt(rows[r, 0]);
                if (v > max) max = v;
            }
            return max + 1;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"AllocateNextPhaseParameterInfoId: {ex.Message}");
            return 1;
        }
    }

    private static int GetPhaseColumnIntOrDefault(PhaseNode ph, string col, int def)
    {
        if (ph?.Columns == null || !ph.Columns.TryGetValue(col, out object v) || v == null || v == DBNull.Value)
            return def;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is short s) return s;
        return int.TryParse(v.ToString(), out int p) ? p : def;
    }

    /// <summary>Phases 行插入后：插入 PhaseType1 并回写 PhaseParameterInfoID。</summary>
    private void AfterPhaseInsertLinkParameterRow(PhaseNode ph)
    {
        if (ph == null || ph.PhaseID <= 0 || string.IsNullOrEmpty(_phaseTableName) || _store == null) return;
        int existing = GetPhaseColumnIntOrDefault(ph, "PhaseParameterInfoID", 0);
        if (existing > 0) return;
        if (!TryGetPhaseType1Table(out var t1))
        {
            if (EnableLog) Log.Error(LogCategory, "PhaseParametersDB.PhaseType1 不可用，回滚 Phases 插入");
            try { _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID={ph.PhaseID}", out _, out _); } catch { }
            throw new InvalidOperationException("PhaseType1 表不可用");
        }
        int nextId = AllocateNextPhaseParameterInfoId();
        int templateTypeId = GetPhaseColumnIntOrDefault(ph, "PhaseTemplateTypeID", 1);
        if (templateTypeId <= 0) templateTypeId = 1;
        short typeId = templateTypeId > short.MaxValue ? short.MaxValue : (short)templateTypeId;
        var type1DataCols = new HashSet<string>(GetPhaseType1DataColumnNames(t1), StringComparer.OrdinalIgnoreCase);
        try
        {
            var insertCols = new List<string>();
            var insertVals = new List<object>();
            foreach (string col in GetPhaseType1ModelColumnNames(t1))
            {
                if (string.Equals(col, "TypeID", StringComparison.OrdinalIgnoreCase))
                {
                    insertCols.Add(col);
                    insertVals.Add(typeId);
                }
                else if (string.Equals(col, "PhaseTemplateTypeID", StringComparison.OrdinalIgnoreCase))
                {
                    insertCols.Add(col);
                    insertVals.Add(templateTypeId);
                }
                else if (string.Equals(col, "PhaseParameterInfoID", StringComparison.OrdinalIgnoreCase))
                {
                    insertCols.Add(col);
                    insertVals.Add(nextId);
                }
                else if (type1DataCols.Contains(col))
                {
                    insertCols.Add(col);
                    insertVals.Add(GetInitialType1InsertCell(ph, col));
                }
            }
            if (insertCols.Count == 0 || !insertCols.Exists(c => string.Equals(c, "PhaseParameterInfoID", StringComparison.OrdinalIgnoreCase)))
            {
                if (EnableLog) Log.Error(LogCategory, "PhaseType1 无 PhaseParameterInfoID 列或列为空，无法插入参数行");
                try { _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID={ph.PhaseID}", out _, out _); } catch { }
                throw new InvalidOperationException("PhaseType1 缺少 PhaseParameterInfoID 列");
            }
            var row = new object[1, insertVals.Count];
            for (int i = 0; i < insertVals.Count; i++) row[0, i] = insertVals[i];
            t1.Insert(insertCols.ToArray(), row);
            _store.Query(
                $"UPDATE {_phaseTableName} SET PhaseParameterInfoID={nextId} WHERE PhaseID={ph.PhaseID}",
                out _, out _);
            ph.Columns["PhaseParameterInfoID"] = nextId;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"AfterPhaseInsertLinkParameterRow: {ex.Message}");
            try { _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID={ph.PhaseID}", out _, out _); } catch { }
            throw;
        }
    }

    private int QueryPhaseParameterInfoIdFromDb(int phaseId)
    {
        if (_store == null || string.IsNullOrEmpty(_phaseTableName) || phaseId <= 0) return 0;
        try
        {
            _store.Query(
                $"SELECT PhaseParameterInfoID FROM {_phaseTableName} WHERE PhaseID={phaseId}",
                out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return 0;
            return CellToInt(rows[0, 0]);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"QueryPhaseParameterInfoIdFromDb: {ex.Message}");
            return 0;
        }
    }

    private void DeletePhaseType1RowIfAny(int phaseParameterInfoId)
    {
        if (phaseParameterInfoId <= 0) return;
        if (!TryGetPhaseParametersStore(out var ps)) return;
        try
        {
            ps.Query($"DELETE FROM {PhaseType1TableName} WHERE PhaseParameterInfoID={phaseParameterInfoId}", out _, out _);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"DeletePhaseType1RowIfAny: {ex.Message}");
        }
    }

    #region PhaseTemplateUdt 与 PhaseType1
    /// <summary>按表模型列读取 PhaseType1 一行（仅业务数据列）；列名变更后仍与 Studio 表定义一致。</summary>
    private bool TrySelectPhaseType1DataRow(int phaseParameterInfoId, out Dictionary<string, object> row)
    {
        row = null;
        if (phaseParameterInfoId <= 0 || !TryGetPhaseType1Table(out var t1)) return false;
        var cols = GetPhaseType1DataColumnNames(t1);
        if (cols.Count == 0) return false;
        if (!TryGetPhaseParametersStore(out var ps)) return false;
        var safe = cols.Where(IsSafeSqlIdentifier).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (safe.Count == 0) return false;
        string list = string.Join(",", safe);
        try
        {
            ps.Query(
                $"SELECT {list} FROM {PhaseType1TableName} WHERE PhaseParameterInfoID={phaseParameterInfoId}",
                out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) == 0) return false;
            row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < safe.Count; c++)
                row[safe[c]] = rows[0, c];
            return true;
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TrySelectPhaseType1DataRow: {ex.Message}");
            return false;
        }
    }

    private static string ParamToBufferString(object cell)
    {
        string s = CellToString(cell);
        return string.IsNullOrEmpty(s) ? "0" : s;
    }

    private static int ParsePhaseParamIntString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return int.TryParse(s.Trim(), out int v) ? v : 0;
    }

    /// <summary>按 PhaseType1 表业务列合并 Phases 与 Type1；无 Type1 表时仅处理 Parameter1..3。</summary>
    public void ApplyResolvedParameter123ToColumnCopy(PhaseNode src, Dictionary<string, object> target)
    {
        if (src == null || target == null) return;
        int infoId = GetPhaseColumnIntOrDefault(src, "PhaseParameterInfoID", 0);
        if (infoId <= 0 && src.PhaseID > 0) infoId = QueryPhaseParameterInfoIdFromDb(src.PhaseID);
        var cols = src.Columns;
        if (!TryGetPhaseType1Table(out var t1))
        {
            foreach (var legacy in new[] { "Parameter1", "Parameter2", "Parameter3" })
            {
                string s = "0";
                if (cols != null && cols.TryGetValue(legacy, out var a) && a != null && a != DBNull.Value) s = ParamToBufferString(a);
                target[legacy] = ParsePhaseParamIntString(s);
            }
            return;
        }
        var dataCols = GetPhaseType1DataColumnNames(t1);
        var dataColSet = new HashSet<string>(dataCols, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> row = null;
        if (infoId > 0) TrySelectPhaseType1DataRow(infoId, out row);
        // Type1 为权威源：同名列 Phases 里占位 0 时仍以 Type1 为准，避免 UI/库不一致
        foreach (string col in dataCols)
        {
            object t = null, a = null;
            bool hasT1 = row != null && row.TryGetValue(col, out t) && t != null && t != DBNull.Value;
            bool hasPh = cols != null && cols.TryGetValue(col, out a) && a != null && a != DBNull.Value;
            if (hasT1)
                target[col] = NormalizeType1ValueForColumnCopy(t);
            else if (hasPh)
                target[col] = NormalizeType1ValueForColumnCopy(a);
        }
        foreach (string col in TryGetPhaseLayoutBindKeys())
        {
            if (dataColSet.Contains(col)) continue;
            if (cols != null && cols.TryGetValue(col, out var a2) && a2 != null && a2 != DBNull.Value)
                target[col] = NormalizeType1ValueForColumnCopy(a2);
        }
    }

    private void AttachUdtTemplateBufferDirtyObservers()
    {
        DetachUdtTemplateBufferDirtyObservers();
        if (_phaseBufferDirtyAffinityId == 0) return;
        IUAObject root = null;
        try { root = Project.Current?.GetObject(DefaultUdtPhaseTemplateBufferObjectPath); }
        catch { root = null; }
        if (root == null) return;
        AttachUdtTemplateBufferDirtyObserversRecursive(root);
    }

    private void DetachUdtTemplateBufferDirtyObservers()
    {
        foreach (var r in _udtTemplateBufferDirtyRegs)
            r?.Dispose();
        _udtTemplateBufferDirtyRegs.Clear();
    }

    private void AttachUdtTemplateBufferDirtyObserversRecursive(IUANode node)
    {
        if (node == null) return;
        if (node is IUAVariable v && IsUdtTemplateTagLevelVariable(v))
        {
            try
            {
                var obs = new CallbackVariableChangeObserver(OnUdtTemplateBufferVariableChanged);
                _udtTemplateBufferDirtyRegs.Add(v.RegisterEventObserver(obs, EventType.VariableValueChanged, _phaseBufferDirtyAffinityId));
            }
            catch { }
            return;
        }
        if (node.Children == null) return;
        foreach (var child in node.Children)
        {
            if (child == null) continue;
            if (ShouldSkipUdtTemplateTreeChildBrowseName(child.BrowseName)) continue;
            AttachUdtTemplateBufferDirtyObserversRecursive(child);
        }
    }

    private static bool ShouldSkipUdtTemplateTreeChildBrowseName(string browseName)
    {
        if (string.IsNullOrEmpty(browseName)) return false;
        return string.Equals(browseName, "SymbolName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browseName, "EnableBlockRead", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browseName, "ArrayUpdateRate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browseName, "SamplingMode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>与 <see cref="GeneratePhaseColumns"/> 中标签叶节点判定一致：RAEtherNetIPTag 等仅含 SymbolName 子项时为叶。</summary>
    private static bool IsUdtTemplateTagLevelVariable(IUAVariable variable)
    {
        if (variable == null) return false;
        if (string.Equals(variable.BrowseName, "SymbolName", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(variable.BrowseName, "EnableBlockRead", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(variable.BrowseName, "ArrayUpdateRate", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(variable.BrowseName, "SamplingMode", StringComparison.OrdinalIgnoreCase)) return false;
        if (variable.Children == null) return true;
        foreach (var child in variable.Children)
        {
            if (child == null) continue;
            if (string.Equals(child.BrowseName, "SymbolName", StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    private void OnUdtTemplateBufferVariableChanged(
        IUAVariable variable,
        UAValue newValue,
        UAValue oldValue,
        ElementAccess elementAccess,
        ulong senderId)
    {
        if (_phaseBufferLoadDepth > 0) return;
        if (_phaseBufferProgSenderId != 0 && senderId == _phaseBufferProgSenderId) return;
        if (PhaseUaValuesEqual(newValue, oldValue)) return;
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) return;
        RecipeDatabaseManager.Instance?.NotifyPhaseParameterBufferEdited();
    }

    private static bool PhaseUaValuesEqual(UAValue a, UAValue b)
    {
        object av = a?.Value;
        object bv = b?.Value;
        if (av == null && bv == null) return true;
        if (av is LocalizedText alt && bv is LocalizedText blt)
            return string.Equals(alt.Text ?? "", blt.Text ?? "", StringComparison.Ordinal);
        if (av == null || bv == null) return false;
        if (av.Equals(bv)) return true;
        return string.Equals(CellToString(av).Trim(), CellToString(bv).Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 将模板 UDT（如 <see cref="DefaultUdtPhaseTemplateBufferObjectPath"/>）中各标签变量写入内存 <see cref="PhaseNode.Columns"/>；
    /// 一维数组序列化为 JSON 字符串，便于 SQLite TEXT 存储。
    /// 按变量树读取：仅使用 GetVariable(path) 解析根节点。
    /// </summary>
    public void MergeUdtPhaseTemplateBufferIntoPhaseNode(int phaseId, string udtRootPath = null)
    {
        if (phaseId <= 0 || !PhaseById.TryGetValue(phaseId, out var ph) || ph?.Columns == null) return;
        string path = string.IsNullOrEmpty(udtRootPath) ? DefaultUdtPhaseTemplateBufferObjectPath : udtRootPath;
        IUAVariable root = null;
        try { root = Project.Current?.GetVariable(path); } catch { root = null; }
        if (root == null)
        {
            if (EnableLog) Log.Warning(LogCategory, $"MergeUdtPhaseTemplateBufferIntoPhaseNode: 无法解析 UDT 根节点，path={path}");
            return;
        }
        MergeUdtTemplateTagVariablesIntoPhaseColumns(root, ph);
    }

    private static void MergeUdtTemplateTagVariablesIntoPhaseColumns(IUANode node, PhaseNode ph)
    {
        if (node == null || ph?.Columns == null) return;
        if (node is IUAVariable v && IsUdtTemplateTagLevelVariable(v))
        {
            string col = v.BrowseName;
            if (string.IsNullOrEmpty(col) || PhaseType1ReservedColumns.Contains(col)) return;
            ph.Columns[col] = ConvertUdtTagVariableToPhaseColumnCell(v);
            return;
        }
        if (node.Children == null) return;
        foreach (var child in node.Children)
        {
            if (child == null) continue;
            if (ShouldSkipUdtTemplateTreeChildBrowseName(child.BrowseName)) continue;
            MergeUdtTemplateTagVariablesIntoPhaseColumns(child, ph);
        }
    }

    private static object ConvertUdtTagVariableToPhaseColumnCell(IUAVariable v)
    {
        if (v == null) return "";
        try
        {
            object val = v.Value?.Value;
            if (val == null) return "";
            if (val is Array arr && arr.Rank == 1)
                return SerializeRankOneArrayAsJson(arr);
            if (val is LocalizedText lt)
                return lt.Text ?? "";
            if (val is bool b) return b;
            if (val is int i) return i;
            if (val is long l) return l;
            if (val is short s) return (int)s;
            if (val is byte by) return (int)by;
            if (val is uint ui) return ui;
            if (val is float f) return f;
            if (val is double d) return d;
            if (val is decimal m) return m;
            return CellToString(val);
        }
        catch
        {
            return "";
        }
    }

    private static string SerializeRankOneArrayAsJson(Array arr)
    {
        int n = arr.Length;
        if (n == 0) return "[]";
        object o0 = arr.GetValue(0);
        if (o0 is bool)
        {
            var list = new bool[n];
            for (int i = 0; i < n; i++) list[i] = (bool)arr.GetValue(i);
            return JsonSerializer.Serialize(list);
        }
        if (o0 is float || o0 is double)
        {
            var list = new double[n];
            for (int i = 0; i < n; i++) list[i] = Convert.ToDouble(arr.GetValue(i), CultureInfo.InvariantCulture);
            return JsonSerializer.Serialize(list);
        }
        if (o0 is int || o0 is long || o0 is short || o0 is byte || o0 is uint)
        {
            var list = new long[n];
            for (int i = 0; i < n; i++) list[i] = Convert.ToInt64(arr.GetValue(i), CultureInfo.InvariantCulture);
            return JsonSerializer.Serialize(list);
        }
        var fallback = new object[n];
        for (int i = 0; i < n; i++) fallback[i] = arr.GetValue(i);
        return JsonSerializer.Serialize(fallback);
    }

    private void TryPersistPhaseType1FromPhaseNode(PhaseNode ph)
    {
        if (ph == null || ph.PhaseID <= 0) return;
        int infoId = GetPhaseColumnIntOrDefault(ph, "PhaseParameterInfoID", 0);
        if (infoId <= 0 || !TryGetPhaseParametersStore(out var ps) || !TryGetPhaseType1Table(out var t1)) return;
        var dataCols = GetPhaseType1DataColumnNames(t1);
        var parts = new List<string>();
        foreach (string col in dataCols)
        {
            if (!IsSafeSqlIdentifier(col) || !ph.Columns.TryGetValue(col, out var v)) continue;
            parts.Add($"{col}={CellToSqlLiteralForPhaseType1(v)}");
        }
        if (parts.Count == 0) return;
        try
        {
            ps.Query(
                $"UPDATE {PhaseType1TableName} SET {string.Join(",", parts)} WHERE PhaseParameterInfoID={infoId}",
                out _, out _);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TryPersistPhaseType1FromPhaseNode({ph.PhaseID}): {ex.Message}");
        }
    }

    /// <summary>
    /// 将 PhaseType1 行与内存 <see cref="PhaseNode.Columns"/> 按 BrowseName 写入模板 UDT；
    /// 无匹配列时将该标签复位为默认。若当前 Phase 为 dirty，则优先使用内存列（未保存编辑）回填。
    /// </summary>
    public void LoadPhaseParametersToUdtTemplateBuffer(int phaseId)
    {
        IUAVariable root = null;
        try { root = Project.Current?.GetVariable(DefaultUdtPhaseTemplateBufferObjectPath); }
        catch { root = null; }
        if (root == null) return;

        _phaseBufferLoadDepth++;
        try
        {
            Dictionary<string, object> row = null;
            Dictionary<string, object> phCols = null;
            bool phaseDirty = false;
            if (phaseId > 0 && PhaseById.TryGetValue(phaseId, out var ph))
            {
                phCols = ph.Columns;
                phaseDirty = IsDirtyPhase(phaseId);
                int infoId = GetPhaseColumnIntOrDefault(ph, "PhaseParameterInfoID", 0);
                if (infoId <= 0) infoId = QueryPhaseParameterInfoIdFromDb(phaseId);
                if (infoId > 0) TrySelectPhaseType1DataRow(infoId, out row);
            }
            if (phaseDirty)
            {
                // dirty 优先：有未保存改动时，用内存列覆盖同名 DB 列，避免切换 phase 后显示被旧 DB 值回滚。
                if (row == null)
                    row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (phCols != null)
                {
                    foreach (var kv in phCols)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        if (kv.Value == null || kv.Value == DBNull.Value) continue;
                        row[kv.Key] = kv.Value;
                    }
                }
            }
            ApplyPhaseDataToUdtTemplateTree(root, row, phCols);
        }
        finally
        {
            if (_phaseBufferLoadDepth > 0) _phaseBufferLoadDepth--;
        }
    }

    private void ApplyPhaseDataToUdtTemplateTree(IUANode root, Dictionary<string, object> row, Dictionary<string, object> phCols)
    {
        if (root == null) return;
        ApplyPhaseDataToUdtTemplateTreeRecursive(root, row, phCols);
    }

    private void ApplyPhaseDataToUdtTemplateTreeRecursive(IUANode node, Dictionary<string, object> row, Dictionary<string, object> phCols)
    {
        if (node is IUAVariable v && IsUdtTemplateTagLevelVariable(v))
        {
            string col = v.BrowseName;
            if (string.IsNullOrEmpty(col) || PhaseType1ReservedColumns.Contains(col)) return;
            object raw = null;
            bool has = false;
            if (row != null && row.TryGetValue(col, out var t) && t != null && t != DBNull.Value)
            {
                raw = t;
                has = true;
            }
            else if (phCols != null && phCols.TryGetValue(col, out var a) && a != null && a != DBNull.Value)
            {
                raw = a;
                has = true;
            }
            if (has)
                WriteUdtTagVariableFromPhaseColumn(v, raw);
            else
                ResetUdtTagVariableToDefault(v);
            return;
        }
        if (node?.Children == null) return;
        foreach (var child in node.Children)
        {
            if (child == null) continue;
            if (ShouldSkipUdtTemplateTreeChildBrowseName(child.BrowseName)) continue;
            ApplyPhaseDataToUdtTemplateTreeRecursive(child, row, phCols);
        }
    }

    private void SetUdtVariableUaValue(IUAVariable v, UAValue ua)
    {
        if (v == null || ua == null) return;
        try
        {
            if (_phaseBufferProgSenderId != 0)
            {
                using (LogicObject.Context.SetCurrentThreadSenderId(_phaseBufferProgSenderId))
                    v.Value = ua;
            }
            else
                v.Value = ua;
        }
        catch { }
    }

    private void ResetUdtTagVariableToDefault(IUAVariable v)
    {
        if (v == null) return;
        try
        {
            object curVal = null;
            try { curVal = v.Value?.Value; } catch { }
            if (curVal is Array arr && arr.Rank == 1 && arr.Length > 0)
            {
                Type et = arr.GetValue(0).GetType();
                Array z = Array.CreateInstance(et, arr.Length);
                for (int i = 0; i < z.Length; i++)
                {
                    if (et == typeof(bool)) z.SetValue(false, i);
                    else if (et == typeof(float)) z.SetValue(0f, i);
                    else if (et == typeof(double)) z.SetValue(0.0, i);
                    else if (et == typeof(int)) z.SetValue(0, i);
                    else if (et == typeof(long)) z.SetValue(0L, i);
                    else if (et == typeof(uint)) z.SetValue(0u, i);
                    else
                        z.SetValue(Convert.ChangeType(0, et, CultureInfo.InvariantCulture), i);
                }
                SetUdtVariableUaValue(v, new UAValue(z));
                return;
            }
            if (v.DataType == OpcUa.DataTypes.Boolean)
                SetUdtVariableUaValue(v, new UAValue(false));
            else if (v.DataType == OpcUa.DataTypes.Float || v.DataType == OpcUa.DataTypes.Double)
                SetUdtVariableUaValue(v, new UAValue(0.0f));
            else
                SetUdtVariableUaValue(v, new UAValue(0));
        }
        catch { }
    }

    private void WriteUdtTagVariableFromPhaseColumn(IUAVariable v, object raw)
    {
        if (v == null || raw == null || raw == DBNull.Value) return;
        try
        {
            object curVal = null;
            try { curVal = v.Value?.Value; } catch { }
            if (curVal is Array template && template.Rank == 1 && template.Length > 0)
            {
                Array filled = CoerceRawToRankOneArray(raw, template);
                if (filled != null)
                    SetUdtVariableUaValue(v, new UAValue(filled));
                return;
            }
            UAValue ua = CoerceScalarRawToUaValue(v, raw);
            if (ua != null)
                SetUdtVariableUaValue(v, ua);
        }
        catch { }
    }

    private static UAValue CoerceScalarRawToUaValue(IUAVariable v, object raw)
    {
        if (raw is bool b0) return new UAValue(b0);
        if (raw is int i) return new UAValue(i);
        if (raw is long l) return new UAValue((int)l);
        if (raw is short sh) return new UAValue((int)sh);
        if (raw is byte by) return new UAValue((int)by);
        if (raw is uint ui) return new UAValue((int)ui);
        if (raw is float f) return new UAValue(f);
        if (raw is double d0) return new UAValue(d0);
        string str = ParamToBufferString(raw);
        if (v.DataType == OpcUa.DataTypes.Boolean)
        {
            bool bv = str == "1" || string.Equals(str, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(str, "True", StringComparison.Ordinal);
            if (!bv && int.TryParse(str.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                bv = iv != 0;
            return new UAValue(bv);
        }
        if (v.DataType == OpcUa.DataTypes.Float || v.DataType == OpcUa.DataTypes.Double)
        {
            if (double.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dbl))
                return new UAValue(dbl);
            return new UAValue(0.0);
        }
        return new UAValue(ParsePhaseParamIntString(str));
    }

    private static Array CoerceRawToRankOneArray(object raw, Array template)
    {
        int len = template.Length;
        if (len <= 0) return null;
        Type et = template.GetValue(0).GetType();
        string s = ParamToBufferString(raw).Trim();

        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                if (et == typeof(bool))
                {
                    var parsed = JsonSerializer.Deserialize<bool[]>(s);
                    return parsed == null ? null : PadOrTrimBoolArray(parsed, len);
                }
                if (et == typeof(float))
                {
                    var parsed = JsonSerializer.Deserialize<double[]>(s);
                    return parsed == null ? null : PadOrTrimFloatArrayFromDouble(parsed, len);
                }
                if (et == typeof(double))
                {
                    var parsed = JsonSerializer.Deserialize<double[]>(s);
                    return parsed == null ? null : PadOrTrimDoubleArray(parsed, len);
                }
                if (et == typeof(int))
                {
                    var parsed = JsonSerializer.Deserialize<int[]>(s);
                    return parsed == null ? null : PadOrTrimIntArray(parsed, len);
                }
                if (et == typeof(long))
                {
                    var parsed = JsonSerializer.Deserialize<long[]>(s);
                    return parsed == null ? null : PadOrTrimLongArray(parsed, len);
                }
            }
            catch { }
        }

        var parts = s.Split(';');
        Array result = Array.CreateInstance(et, len);
        for (int i = 0; i < len; i++)
        {
            string p = i < parts.Length ? parts[i].Trim() : "0";
            result.SetValue(ParseArrayElementString(p, et), i);
        }
        return result;
    }

    private static object ParseArrayElementString(string p, Type et)
    {
        if (et == typeof(bool))
            return p == "1" || string.Equals(p, "true", StringComparison.OrdinalIgnoreCase);
        if (et == typeof(float))
            return float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        if (et == typeof(double))
            return double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        if (et == typeof(int))
            return int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
        if (et == typeof(long))
            return long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0L;
        if (et == typeof(uint))
            return uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u) ? u : 0u;
        return 0;
    }

    private static Array PadOrTrimBoolArray(bool[] parsed, int len)
    {
        var result = new bool[len];
        for (int i = 0; i < len; i++)
            result[i] = i < parsed.Length ? parsed[i] : false;
        return result;
    }

    private static Array PadOrTrimFloatArrayFromDouble(double[] parsed, int len)
    {
        var result = new float[len];
        for (int i = 0; i < len; i++)
            result[i] = i < parsed.Length ? (float)parsed[i] : 0f;
        return result;
    }

    private static Array PadOrTrimDoubleArray(double[] parsed, int len)
    {
        var result = new double[len];
        for (int i = 0; i < len; i++)
            result[i] = i < parsed.Length ? parsed[i] : 0.0;
        return result;
    }

    private static Array PadOrTrimIntArray(int[] parsed, int len)
    {
        var result = new int[len];
        for (int i = 0; i < len; i++)
            result[i] = i < parsed.Length ? parsed[i] : 0;
        return result;
    }

    private static Array PadOrTrimLongArray(long[] parsed, int len)
    {
        var result = new long[len];
        for (int i = 0; i < len; i++)
            result[i] = i < parsed.Length ? parsed[i] : 0L;
        return result;
    }

    /// <summary>供按钮/事件：按当前树选中 Phase 刷新模板 UDT。</summary>
    [ExportMethod]
    public void Export_LoadSelectedPhaseToUdtTemplateBuffer()
    {
        int pid = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        LoadPhaseParametersToUdtTemplateBuffer(pid);
    }
    #endregion
    #endregion

    private void SavePhaseUpdate(PhaseNode ph)
    {
        if (ph.Columns.Count == 0)
        {
            var parts = new List<string> { $"Name='{EscapeSql(ph.Name)}'" };
            AppendPhaseLastModifiedSetClauses(ph, parts);
            _store.Query($"UPDATE {_phaseTableName} SET {string.Join(", ", parts)} WHERE PhaseID={ph.PhaseID}", out _, out _);
            return;
        }
        var setClauses = new List<string>();
        foreach (var kv in ph.Columns)
        {
            if (string.Equals(kv.Key, "PhaseID", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "CreatedBy", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "CreatedDateTime", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "LastModifiedBY", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "LastModifiedDateTime", StringComparison.OrdinalIgnoreCase)) continue;
            if (!HasPhaseColumn(kv.Key)) continue;
            string val = kv.Value == null || kv.Value == DBNull.Value ? "" : kv.Value.ToString();
            setClauses.Add($"{kv.Key}='{EscapeSql(val)}'");
        }
        AppendPhaseLastModifiedSetClauses(ph, setClauses);
        if (setClauses.Count == 0) return;
        _store.Query($"UPDATE {_phaseTableName} SET {string.Join(", ", setClauses)} WHERE PhaseID={ph.PhaseID}", out _, out _);
    }

    private void SavePhaseInsert(PhaseNode ph)
    {
        var colNames = GetPhaseColumnNames();
        if (colNames.Count == 0)
        {
            _phaseTable.Insert(new[] { "PhaseID", "Name" }, new object[,] { { ph.PhaseID, ph.Name } });
            AfterPhaseInsertLinkParameterRow(ph);
            return;
        }
        var values = new List<object>();
        foreach (string col in colNames)
        {
            if (string.Equals(col, "PhaseTemplateTypeID", StringComparison.OrdinalIgnoreCase))
                values.Add(GetPhaseColumnIntOrDefault(ph, col, 1));
            else if (string.Equals(col, "PhaseParameterInfoID", StringComparison.OrdinalIgnoreCase))
                values.Add(GetPhaseColumnIntOrDefault(ph, col, 0));
            else if (ph.Columns.TryGetValue(col, out object val))
                values.Add(val == null || val == DBNull.Value ? "" : val);
            else if (string.Equals(col, "PhaseID", StringComparison.OrdinalIgnoreCase))
                values.Add(ph.PhaseID);
            else if (string.Equals(col, "Name", StringComparison.OrdinalIgnoreCase))
                values.Add(ph.Name);
            else if (string.Equals(col, "LastModifiedBY", StringComparison.OrdinalIgnoreCase))
                values.Add(GetLastModifiedByForPhase(ph));
            else if (string.Equals(col, "LastModifiedDateTime", StringComparison.OrdinalIgnoreCase))
                values.Add(FormatStoredCreatedDateTimeNow());
            else
                values.Add("");
        }
        var row = new object[1, colNames.Count];
        for (int i = 0; i < values.Count; i++) row[0, i] = values[i];
        _phaseTable.Insert(colNames.ToArray(), row);
        AfterPhaseInsertLinkParameterRow(ph);
    }

    private static string BuildIdCsv<T>(List<T> list, Func<T, int> selector)
    {
        var ids = new List<string>();
        foreach (var item in list) ids.Add(selector(item).ToString());
        return string.Join(",", ids);
    }

    private static string EscapeSql(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("'", "''");
    }
    #endregion
}
