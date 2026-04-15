#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
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
    /// 写库前将当前选中 Phase 的 <see cref="DefaultPhaseBufferObjectPath"/> 变量并入 <see cref="PhaseNode.Columns"/> 并标脏。
    /// 否则 <see cref="TryPersistPhaseType1FromPhaseNode"/> 因未标脏被跳过，或 Buffer 未合并导致新列（如 Parameter6）不落库。
    /// </summary>
    public void FlushSelectedPhaseUiBufferToTreeForPersist()
    {
        int pId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (pId <= 0) return;
        MergePhaseUiBufferIntoPhaseNode(pId);
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

    /// <summary>从布局 JSON 收集缓冲变量名：bindKeySwitch → Boolean，其余 BindKey* → String（与 PhaseType1 列名一致即可落库）。</summary>
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
                    foreach (var pair in new[] {
                        (it.BindKey, false),
                        (it.BindKeySwitch, true),
                        (it.BindKeyText, false),
                        (it.BindKeyCombo, false)
                    })
                    {
                        string k = pair.Item1;
                        bool asBool = pair.Item2;
                        if (string.IsNullOrEmpty(k) || !IsSafeSqlIdentifier(k)) continue;
                        if (seen.Add(k))
                            allBufferKeyNames.Add(k);
                        if (asBool)
                            booleanBufferKeys.Add(k);
                    }
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

    #region PhaseUIBufferData（PhaseType1 动态列 ↔ PhaseUIBufferData 同名变量）
    private static bool IsValveBufferBrowseName(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("Valve", StringComparison.OrdinalIgnoreCase)) return false;
        string tail = name.Length > 5 ? name.Substring(5) : "";
        return tail.Length > 0 && tail.All(char.IsDigit);
    }

    private static NodeId ResolveAutoBufferVariableDataType(string name, HashSet<string> layoutBooleanBufferKeys)
    {
        if (layoutBooleanBufferKeys != null && layoutBooleanBufferKeys.Contains(name))
            return OpcUa.DataTypes.Boolean;
        return IsValveBufferBrowseName(name) ? OpcUa.DataTypes.Boolean : OpcUa.DataTypes.String;
    }

    private static UAValue InitialUaValueForAutoBufferVariable(string name, HashSet<string> layoutBooleanBufferKeys)
    {
        if (layoutBooleanBufferKeys != null && layoutBooleanBufferKeys.Contains(name))
            return new UAValue(false);
        return IsValveBufferBrowseName(name) ? new UAValue(false) : new UAValue("0");
    }
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

    /// <summary>与 LoadPhaseParametersToPhaseUIBuffer 一致：按 PhaseType1 表业务列合并 Phases 与 Type1；无 Type1 表时仅处理 Parameter1..3。</summary>
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

    private void WritePhaseBufferStringVar(IUAObject buffer, string name, string value)
    {
        if (buffer == null) return;
        var v = buffer.GetVariable(name);
        if (v == null) return;
        try
        {
            UAValue ua;
            if (v.DataType == OpcUa.DataTypes.Boolean)
            {
                bool b = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "True", StringComparison.Ordinal);
                if (!b && int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                    b = iv != 0;
                ua = new UAValue(b);
            }
            else if (IsValveBufferBrowseName(name))
            {
                bool b = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "True", StringComparison.Ordinal);
                ua = new UAValue(b);
            }
            else
                ua = new UAValue(value ?? "0");
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

    /// <summary>按 PhaseType1 业务列在缓冲下自动创建缺失的 String 变量；无表时创建 Parameter1..3。</summary>
    public static bool EnsurePhaseUiBufferModelVariables(string bufferObjectPath = DefaultPhaseBufferObjectPath)
    {
        IUAObject buffer = null;
        try { buffer = Project.Current?.GetObject(bufferObjectPath); }
        catch { buffer = null; }
        if (buffer == null) return false;

        Instance?.ClearPhaseType1TableCache();

        Table t1 = null;
        try
        {
            var ds = Project.Current?.GetObject("DataStores");
            var ps = ds?.Get<Store>(PhaseParametersDbName);
            t1 = ps?.Tables.Get<Table>(PhaseType1TableName);
        }
        catch { t1 = null; }

        var names = new List<string>();
        if (t1 != null)
        {
            foreach (string col in GetPhaseType1DataColumnNames(t1))
            {
                if (IsSafeSqlIdentifier(col)) names.Add(col);
            }
        }
        if (names.Count == 0)
        {
            names.Add("Parameter1");
            names.Add("Parameter2");
            names.Add("Parameter3");
        }

        CollectPhaseLayoutKeysFromJson(out var layoutBoolKeys, out var layoutAllKeys);
        foreach (var k in layoutAllKeys)
            names.Add(k);
        foreach (var k in DefaultPhaseUiLayoutBindKeys)
            names.Add(k);
        foreach (var k in DefaultPhaseUiEndConditionStringKeys)
        {
            if (IsSafeSqlIdentifier(k)) names.Add(k);
        }
        foreach (var k in DefaultPhaseUiEndConditionBoolKeys)
        {
            if (IsSafeSqlIdentifier(k)) names.Add(k);
        }

        var effectiveBoolKeys = new HashSet<string>(layoutBoolKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in DefaultPhaseUiEndConditionBoolKeys)
        {
            if (IsSafeSqlIdentifier(k)) effectiveBoolKeys.Add(k);
        }

        bool added = false;
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(name) || buffer.GetVariable(name) != null) continue;
            try
            {
                NodeId dt = ResolveAutoBufferVariableDataType(name, effectiveBoolKeys);
                var nv = InformationModel.MakeVariable(name, dt);
                nv.Value = InitialUaValueForAutoBufferVariable(name, effectiveBoolKeys);
                buffer.Add(nv);
                added = true;
            }
            catch (Exception ex)
            {
                if (EnableLog) Log.Warning(LogCategory, $"EnsurePhaseUiBufferModelVariables Add '{name}': {ex.Message}");
            }
        }

        if (added && Instance != null)
        {
            Instance.DetachPhaseBufferDirtyObservers();
            Instance.AttachPhaseBufferDirtyObservers();
        }
        return added;
    }

    /// <summary>与 <see cref="EnsurePhaseUiBufferModelVariables"/> 相同；供实例侧显式调用。</summary>
    public void EnsurePhaseUiBufferVariables(string bufferObjectPath = DefaultPhaseBufferObjectPath)
    {
        EnsurePhaseUiBufferModelVariables(bufferObjectPath);
    }

    private void AttachPhaseBufferDirtyObservers()
    {
        DetachPhaseBufferDirtyObservers();
        if (_phaseBufferDirtyAffinityId == 0) return;
        IUAObject buffer = null;
        try { buffer = Project.Current?.GetObject(DefaultPhaseBufferObjectPath); }
        catch { buffer = null; }
        if (buffer?.Children == null) return;
        foreach (var n in buffer.Children)
        {
            if (!(n is IUAVariable v)) continue;
            try
            {
                var obs = new CallbackVariableChangeObserver(OnPhaseBufferVariableChanged);
                _phaseBufferDirtyRegs.Add(v.RegisterEventObserver(obs, EventType.VariableValueChanged, _phaseBufferDirtyAffinityId));
            }
            catch { }
        }
    }

    private void DetachPhaseBufferDirtyObservers()
    {
        foreach (var r in _phaseBufferDirtyRegs)
            r?.Dispose();
        _phaseBufferDirtyRegs.Clear();
    }

    private void OnPhaseBufferVariableChanged(
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

    /// <summary>将 PhaseUIBufferData 各变量写回内存中的 Phase 节点（供 Save 与 * 逻辑）；不直接写主库。</summary>
    public void MergePhaseUiBufferIntoPhaseNode(int phaseId, string bufferObjectPath = DefaultPhaseBufferObjectPath)
    {
        if (phaseId <= 0 || !PhaseById.TryGetValue(phaseId, out var ph)) return;
        IUAObject buffer = null;
        try { buffer = Project.Current?.GetObject(bufferObjectPath); }
        catch { buffer = null; }
        if (buffer?.Children == null) return;
        if (ph.Columns == null) return;
        foreach (var n in buffer.Children)
        {
            if (!(n is IUAVariable v)) continue;
            string key = n.BrowseName;
            if (string.IsNullOrEmpty(key)) continue;
            object val = null;
            try { val = v.Value?.Value; } catch { continue; }
            if (val == null)
                ph.Columns[key] = "";
            else if (val is LocalizedText lt)
                ph.Columns[key] = lt.Text ?? "";
            else if (val is bool b)
                ph.Columns[key] = b;
            else
                ph.Columns[key] = val;
        }
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

    /// <summary>将 PhaseType1 业务列写入 PhaseUIBufferData 同名变量（Studio 中变量 BrowseName 与列名一致）；无表时仅 Parameter1..3。</summary>
    public void LoadPhaseParametersToPhaseUIBuffer(int phaseId, string bufferObjectPath = DefaultPhaseBufferObjectPath)
    {
        IUAObject buffer;
        try { buffer = Project.Current?.GetObject(bufferObjectPath); }
        catch { buffer = null; }
        if (buffer == null) return;
        EnsurePhaseUiBufferModelVariables(bufferObjectPath);

        _phaseBufferLoadDepth++;
        try
        {
            if (!TryGetPhaseType1Table(out var t1))
            {
                LoadPhaseParametersToBufferLegacyTriple(buffer, phaseId);
                return;
            }
            var dataCols = GetPhaseType1DataColumnNames(t1);
            if (dataCols.Count == 0)
            {
                LoadPhaseParametersToBufferLegacyTriple(buffer, phaseId);
                return;
            }
            var layoutBindKeys = TryGetPhaseLayoutBindKeys();
            var dataColSet = new HashSet<string>(dataCols, StringComparer.OrdinalIgnoreCase);
            var toLoad = new List<string>(dataCols);
            foreach (var k in layoutBindKeys)
            {
                if (!dataColSet.Contains(k)) toLoad.Add(k);
            }

            if (phaseId <= 0 || !PhaseById.TryGetValue(phaseId, out var ph))
            {
                foreach (string col in toLoad)
                    WritePhaseBufferStringVar(buffer, col, "0");
                return;
            }

            int infoId = GetPhaseColumnIntOrDefault(ph, "PhaseParameterInfoID", 0);
            if (infoId <= 0) infoId = QueryPhaseParameterInfoIdFromDb(phaseId);
            Dictionary<string, object> row = null;
            if (infoId > 0) TrySelectPhaseType1DataRow(infoId, out row);
            var phCols = ph.Columns;
            foreach (string col in toLoad)
            {
                string s = "0";
                if (dataColSet.Contains(col))
                {
                    object t = null, a = null;
                    bool hasT1 = row != null && row.TryGetValue(col, out t) && t != null && t != DBNull.Value;
                    bool hasPh = phCols != null && phCols.TryGetValue(col, out a) && a != null && a != DBNull.Value;
                    if (hasT1)
                        s = ParamToBufferString(t);
                    else if (hasPh)
                        s = ParamToBufferString(a);
                }
                else if (phCols != null && phCols.TryGetValue(col, out var a2) && a2 != null && a2 != DBNull.Value)
                    s = ParamToBufferString(a2);
                WritePhaseBufferStringVar(buffer, col, s);
            }
        }
        finally
        {
            if (_phaseBufferLoadDepth > 0) _phaseBufferLoadDepth--;
        }
    }

    /// <summary>无 PhaseType1 表或无任何业务列时：仅同步 Parameter1..3（与旧版兼容）。</summary>
    private void LoadPhaseParametersToBufferLegacyTriple(IUAObject buffer, int phaseId)
    {
        if (phaseId <= 0 || !PhaseById.TryGetValue(phaseId, out var ph))
        {
            WritePhaseBufferStringVar(buffer, "Parameter1", "0");
            WritePhaseBufferStringVar(buffer, "Parameter2", "0");
            WritePhaseBufferStringVar(buffer, "Parameter3", "0");
            return;
        }
        int infoId = GetPhaseColumnIntOrDefault(ph, "PhaseParameterInfoID", 0);
        if (infoId <= 0) infoId = QueryPhaseParameterInfoIdFromDb(phaseId);
        string s1 = "0", s2 = "0", s3 = "0";
        if (ph.Columns != null)
        {
            if (ph.Columns.TryGetValue("Parameter1", out var a1) && a1 != null && a1 != DBNull.Value) s1 = ParamToBufferString(a1);
            if (ph.Columns.TryGetValue("Parameter2", out var a2) && a2 != null && a2 != DBNull.Value) s2 = ParamToBufferString(a2);
            if (ph.Columns.TryGetValue("Parameter3", out var a3) && a3 != null && a3 != DBNull.Value) s3 = ParamToBufferString(a3);
        }
        WritePhaseBufferStringVar(buffer, "Parameter1", s1);
        WritePhaseBufferStringVar(buffer, "Parameter2", s2);
        WritePhaseBufferStringVar(buffer, "Parameter3", s3);
    }

    /// <summary>供按钮/事件：按当前树选中 Phase 刷新 Buffer。</summary>
    [ExportMethod]
    public void Export_LoadSelectedPhaseToPhaseUIBuffer()
    {
        int pid = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        LoadPhaseParametersToPhaseUIBuffer(pid);
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
