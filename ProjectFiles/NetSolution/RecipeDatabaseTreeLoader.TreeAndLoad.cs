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
using FTOptix.WebUI;
#endregion

public partial class RecipeDatabaseTreeLoader
{
    #region 读取全部数据并写入本地 dict 树
    /// <summary>从数据库读取所有 Receipt/Operation/Phase 并填充 Tree 与 xxxById 字典。</summary>
    [ExportMethod]
    public void LoadAllToTree()
    {
        ClearTree();
        IsModified = false;
        if (_store == null || string.IsNullOrEmpty(_receiptTableName))
        {
            if (EnableLog) Log.Warning(LogCategory, "数据库未就绪，跳过加载");
            return;
        }

        try
        {
            var receiptCols = GetReceiptSelectColumnNames();
            string receiptSelectList = string.Join(", ", receiptCols);
            _store.Query($"SELECT {receiptSelectList} FROM {_receiptTableName} ORDER BY Sequence", out _, out object[,] rRows);
            if (rRows == null || rRows.GetLength(0) == 0)
            {
                if (EnableLog) Log.Info(LogCategory, "Receipt 表无数据");
                ApplyDiscardedUnusedQueriesToModel();
                BumpDiscardedDataGridQueryNonces();
                return;
            }

            int Idx(string col) => receiptCols.FindIndex(c => string.Equals(c, col, StringComparison.OrdinalIgnoreCase));

            var phaseColumns = GetPhaseColumnNames();
            int rCount = rRows.GetLength(0);

            for (int i = 0; i < rCount; i++)
            {
                int receiptId = CellToInt(rRows[i, Idx("ReceiptID")]);
                string name = CellToString(rRows[i, Idx("Name")]);
                int seq = CellToInt(rRows[i, Idx("Sequence")]);
                string operationsCsv = CellToString(rRows[i, Idx("Operations")]);
                string description = CellToString(rRows[i, Idx("Description")]);
                int iSt = Idx("Status");
                int iBy = Idx("CreatedBy");
                int iDt = Idx("CreatedDateTime");
                string status = iSt >= 0 ? CellToString(rRows[i, iSt]) : "";
                string createdBy = iBy >= 0 ? CellToString(rRows[i, iBy]) : "";
                string createdDt = iDt >= 0 ? NormalizeStoredCreatedDateTime(rRows[i, iDt]) : "";
                if (string.IsNullOrEmpty(status)) status = DefaultReceiptStatus;

                var rNode = new ReceiptNode
                {
                    ReceiptID = receiptId,
                    Name = name,
                    Sequence = seq,
                    OperationsCsv = operationsCsv ?? "",
                    Description = description ?? "",
                    Status = status ?? DefaultReceiptStatus,
                    CreatedBy = createdBy ?? "",
                    CreatedDateTime = createdDt ?? ""
                };
                Tree.Add(rNode);
                ReceiptById[receiptId] = rNode;

                var opIdList = ParseIdList(rNode.OperationsCsv);
                if (opIdList.Count > 0 && _opTable != null && !string.IsNullOrEmpty(_opTableName))
                {
                    string inClause = string.Join(",", opIdList);
                    var opColList = GetOperationSelectColumnNames();
                    string opSel = string.Join(", ", opColList);
                    _store.Query($"SELECT {opSel} FROM {_opTableName} WHERE OperationID IN ({inClause})", out _, out object[,] oRows);
                    var opById = BuildOpDict(oRows, opColList);

                    foreach (int oId in opIdList)
                    {
                        if (!opById.TryGetValue(oId, out var opPair)) continue;
                        var oNode = new OperationNode
                        {
                            OperationID = oId,
                            Name = opPair.Name,
                            Description = opPair.Description ?? "",
                            PhasesCsv = opPair.Phases ?? "",
                            CreatedBy = opPair.CreatedBy ?? "",
                            CreatedDateTime = opPair.CreatedDateTime ?? ""
                        };
                        rNode.Operations.Add(oNode);
                        OperationById[oId] = oNode;

                        var phaseIdList = ParseIdList(oNode.PhasesCsv);
                        if (phaseIdList.Count > 0 && _phaseTable != null && !string.IsNullOrEmpty(_phaseTableName) && phaseColumns.Count > 0)
                        {
                            string pInClause = string.Join(",", phaseIdList);
                            string selectList = string.Join(", ", phaseColumns);
                            _store.Query($"SELECT {selectList} FROM {_phaseTableName} WHERE PhaseID IN ({pInClause})", out _, out object[,] pRows);
                            var phaseById = BuildPhaseDict(pRows, phaseColumns);

                            foreach (int pId in phaseIdList)
                            {
                                if (!phaseById.TryGetValue(pId, out var pNode)) continue;
                                oNode.Phases.Add(pNode);
                                PhaseById[pId] = pNode;
                            }
                        }
                    }
                }
            }

            // 加载未挂在任何 Receipt 下的独立 Operation（仅入 OperationById，供右侧 List 显示）
            if (_opTable != null && !string.IsNullOrEmpty(_opTableName))
            {
                var opColListAll = GetOperationSelectColumnNames();
                string opSelAll = string.Join(", ", opColListAll);
                _store.Query($"SELECT {opSelAll} FROM {_opTableName}", out _, out object[,] allOpRows);
                var opDictAll = BuildOpDict(allOpRows, opColListAll);
                if (opDictAll != null)
                    foreach (var kv in opDictAll)
                    {
                        int oId = kv.Key;
                        if (OperationById.ContainsKey(oId)) continue;
                        var op = kv.Value;
                        var oNode = new OperationNode
                        {
                            OperationID = oId,
                            Name = op.Name,
                            Description = op.Description ?? "",
                            PhasesCsv = op.Phases ?? "",
                            CreatedBy = op.CreatedBy ?? "",
                            CreatedDateTime = op.CreatedDateTime ?? ""
                        };
                        OperationById[oId] = oNode;
                    }
            }

            // 加载未挂在任何 Operation 下的独立 Phase（仅入 PhaseById，供右侧 List 显示）
            if (_phaseTable != null && !string.IsNullOrEmpty(_phaseTableName) && phaseColumns.Count > 0)
            {
                string selectList = string.Join(", ", phaseColumns);
                _store.Query($"SELECT {selectList} FROM {_phaseTableName}", out _, out object[,] allPhRows);
                if (allPhRows != null)
                {
                    int phaseIdIdx = phaseColumns.FindIndex(c => string.Equals(c, "PhaseID", StringComparison.OrdinalIgnoreCase));
                    int nameIdx = phaseColumns.FindIndex(c => string.Equals(c, "Name", StringComparison.OrdinalIgnoreCase));
                    for (int r = 0; r < allPhRows.GetLength(0); r++)
                    {
                        int pId = phaseIdIdx >= 0 ? CellToInt(allPhRows[r, phaseIdIdx]) : CellToInt(allPhRows[r, 0]);
                        if (PhaseById.ContainsKey(pId)) continue;
                        var pNode = new PhaseNode { PhaseID = pId };
                        for (int c = 0; c < phaseColumns.Count && c < allPhRows.GetLength(1); c++)
                            pNode.Columns[phaseColumns[c]] = allPhRows[r, c] == null || allPhRows[r, c] == DBNull.Value ? "" : allPhRows[r, c];
                        pNode.Name = nameIdx >= 0 ? CellToString(allPhRows[r, nameIdx]) : "";
                        int descIdx2 = phaseColumns.FindIndex(c => string.Equals(c, "Description", StringComparison.OrdinalIgnoreCase));
                        pNode.Description = descIdx2 >= 0 ? CellToString(allPhRows[r, descIdx2]) : "";
                        PhaseById[pId] = pNode;
                    }
                }
            }

            if (EnableLog) Log.Info(LogCategory, $"已加载 {Tree.Count} 条配方到本地 dict 树");
            ApplyDiscardedUnusedQueriesToModel();
            BumpDiscardedDataGridQueryNonces();
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"LoadAllToTree 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Discarded 三个 DataGrid 的 Query 若文本不变，绑 Store 时往往不会重新查库；在 SQL 末尾追加恒真谓词 <c>(t=t)</c>（t 为 Ticks）强制刷新。
    /// </summary>
    private void BumpDiscardedDataGridQueryNonces()
    {
        try
        {
            var root = GetProjectRedeiptEditorRoot(LogicObject);
            var dd = root?.GetObject("Model")?.GetObject("UIData")?.GetObject("DiscardedData");
            if (dd == null) return;
            long t = DateTime.UtcNow.Ticks;
            string nonce = $" AND ({t}={t})";

            void Bump(IUANode childObj)
            {
                var v = childObj?.GetVariable("Query");
                if (v == null) return;
                if (v.Value?.Value is not string sql || string.IsNullOrWhiteSpace(sql)) return;
                string baseSql = StripDiscardedGridRefreshNonce(sql.Trim().TrimEnd(';'));
                v.Value = new UAValue(baseSql + nonce);
            }

            Bump(dd.GetObject("DiscardedRecipes"));
            Bump(dd.GetObject("UnusedOperation"));
            Bump(dd.GetObject("UnusedPhases"));
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"BumpDiscardedDataGridQueryNonces: {ex.Message}");
        }
    }

    private static string StripDiscardedGridRefreshNonce(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return sql;
        string s = sql.TrimEnd();
        while (true)
        {
            int p = s.LastIndexOf(" AND (", StringComparison.OrdinalIgnoreCase);
            if (p < 0) break;
            string tail = s.Substring(p + " AND (".Length);
            int close = tail.IndexOf(')');
            if (close < 0) break;
            string inner = tail.Substring(0, close).Trim();
            int eq = inner.IndexOf('=');
            if (eq <= 0) break;
            string left = inner.Substring(0, eq).Trim();
            string right = inner.Substring(eq + 1).Trim();
            if (left == right && long.TryParse(left, out _))
                s = s.Substring(0, p).TrimEnd();
            else
                break;
        }
        return s;
    }

    #region Discarded 面板：UnusedOperation / UnusedPhases 简单 Query（供 DataGrid）
    /// <summary>
    /// 从数据库统计「未出现在任何 Receipt.Operations 的工序」与「未出现在任何 Operation.Phases 的阶段」，
    /// 写成 <c>SELECT * FROM ... WHERE Id IN (...)</c> 写入 UIData（DataGrid 不支持子查询）。
    /// </summary>
    private void ApplyDiscardedUnusedQueriesToModel()
    {
        try
        {
            if (_store == null || string.IsNullOrEmpty(_opTableName)) return;

            ComputeUnusedIdsFromDatabase(out List<int> unusedOpIds, out List<int> unusedPhIds);

            string qOp = BuildSimpleIdInQuery(_opTableName, "OperationID", unusedOpIds);
            string qPh = string.IsNullOrEmpty(_phaseTableName)
                ? "SELECT * FROM Phases WHERE 1=0"
                : BuildSimpleIdInQuery(_phaseTableName, "PhaseID", unusedPhIds);

            var root = GetProjectRedeiptEditorRoot(LogicObject);
            if (root == null) return;

            var unusedOpNode = root.GetObject("Model")?.GetObject("UIData")?.GetObject("DiscardedData")?.GetObject("UnusedOperation");
            var unusedPhNode = root.GetObject("Model")?.GetObject("UIData")?.GetObject("DiscardedData")?.GetObject("UnusedPhases");
            var vOp = unusedOpNode?.GetVariable("Query");
            var vPh = unusedPhNode?.GetVariable("Query");
            if (vOp != null) vOp.Value = new UAValue(qOp);
            if (vPh != null) vPh.Value = new UAValue(qPh);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"ApplyDiscardedUnusedQueriesToModel: {ex.Message}");
        }
    }

    /// <summary>按当前数据库内容刷新 Discarded 面板的 DataGrid Query，并 bump 全部 Query 以强制网格重查。</summary>
    public void RefreshDiscardedUnusedGridQueries()
    {
        ApplyDiscardedUnusedQueriesToModel();
        BumpDiscardedDataGridQueryNonces();
    }

    private static IUANode GetProjectRedeiptEditorRoot(IUANode from)
    {
        var n = from;
        while (n != null)
        {
            if (string.Equals(n.BrowseName, "RedeiptEditor", StringComparison.OrdinalIgnoreCase))
                return n;
            n = n.Owner;
        }
        return null;
    }

    private static string BuildSimpleIdInQuery(string tableName, string idColumn, List<int> ids)
    {
        // Store SQL 不支持 “WHERE 0” 这类写法，需用恒假谓词（如 1=0）表示空结果集
        if (string.IsNullOrEmpty(tableName)) return "SELECT * FROM Operations WHERE 1=0";
        if (ids == null || ids.Count == 0)
            return $"SELECT * FROM {tableName} WHERE 1=0";
        return $"SELECT * FROM {tableName} WHERE {idColumn} IN ({string.Join(",", ids)})";
    }

    private void ComputeUnusedIdsFromDatabase(out List<int> unusedOpIds, out List<int> unusedPhIds)
    {
        unusedOpIds = new List<int>();
        unusedPhIds = new List<int>();

        var usedOpIds = new HashSet<int>();
        if (!string.IsNullOrEmpty(_receiptTableName))
        {
            try
            {
                _store.Query($"SELECT Operations FROM {_receiptTableName}", out _, out object[,] rRows);
                if (rRows != null)
                    for (int i = 0; i < rRows.GetLength(0); i++)
                        foreach (int id in ParseIdList(CellToString(rRows[i, 0])))
                            usedOpIds.Add(id);
            }
            catch (Exception ex)
            {
                if (EnableLog) Log.Warning(LogCategory, $"ComputeUnusedIdsFromDatabase(Receipts): {ex.Message}");
            }
        }

        foreach (int id in QueryIdSet(_opTableName, "OperationID"))
            if (!usedOpIds.Contains(id))
                unusedOpIds.Add(id);
        unusedOpIds.Sort();

        var usedPhaseIds = new HashSet<int>();
        if (!string.IsNullOrEmpty(_opTableName))
        {
            try
            {
                _store.Query($"SELECT Phases FROM {_opTableName}", out _, out object[,] oRows);
                if (oRows != null)
                    for (int i = 0; i < oRows.GetLength(0); i++)
                        foreach (int id in ParseIdList(CellToString(oRows[i, 0])))
                            usedPhaseIds.Add(id);
            }
            catch (Exception ex)
            {
                if (EnableLog) Log.Warning(LogCategory, $"ComputeUnusedIdsFromDatabase(Phases): {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(_phaseTableName))
        {
            foreach (int id in QueryIdSet(_phaseTableName, "PhaseID"))
                if (!usedPhaseIds.Contains(id))
                    unusedPhIds.Add(id);
            unusedPhIds.Sort();
        }
    }
    #endregion

    private void ClearTree()
    {
        Tree.Clear();
        ReceiptById.Clear();
        OperationById.Clear();
        PhaseById.Clear();
    }

    private bool HasReceiptColumn(string columnName)
    {
        if (_receiptTable?.Columns == null) return false;
        foreach (var col in _receiptTable.Columns)
        {
            string n = col?.BrowseName?.Trim();
            if (string.Equals(n, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    #region Operation / Phase 表可选列（审计）
    private bool HasOperationColumn(string columnName)
    {
        if (_opTable?.Columns == null) return false;
        foreach (var col in _opTable.Columns)
        {
            string n = col?.BrowseName?.Trim();
            if (string.Equals(n, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool HasPhaseColumn(string columnName)
    {
        if (_phaseTable?.Columns == null) return false;
        foreach (var col in _phaseTable.Columns)
        {
            string n = col?.BrowseName?.Trim();
            if (string.Equals(n, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>与 RecipeDatabaseManager 一致：当前会话用户 BrowseName。</summary>
    private string GetSessionUserBrowseName()
    {
        string fromLogin = LoginButtonLogic.CurrentLoginUserBrowseName;
        if (!string.IsNullOrWhiteSpace(fromLogin))
            return fromLogin.Trim();
        try
        {
            var u = Session?.User;
            if (u != null && !string.IsNullOrEmpty(u.BrowseName))
                return u.BrowseName.Trim();
        }
        catch { }
        string fromMgr = RecipeDatabaseManager.TryGetInstanceUserBrowseName();
        if (!string.IsNullOrEmpty(fromMgr)) return fromMgr;
        return "";
    }

    /// <summary>写入 Receipt.LastModifiedBY：优先当前登录用户，否则用配方创建人（与列表 Created By 一致），再否则 Anonymous。</summary>
    private string GetLastModifiedByForReceipt(ReceiptNode receipt)
    {
        string u = GetSessionUserBrowseName();
        if (!string.IsNullOrEmpty(u)) return u;
        string by = receipt?.CreatedBy?.Trim() ?? "";
        if (!string.IsNullOrEmpty(by)) return by;
        return "Anonymous";
    }

    private string GetLastModifiedByForOperation(OperationNode op)
    {
        string u = GetSessionUserBrowseName();
        if (!string.IsNullOrEmpty(u)) return u;
        string by = op?.CreatedBy?.Trim() ?? "";
        if (!string.IsNullOrEmpty(by)) return by;
        return "Anonymous";
    }

    private string GetLastModifiedByForPhase(PhaseNode ph)
    {
        string u = GetSessionUserBrowseName();
        if (!string.IsNullOrEmpty(u)) return u;
        if (ph?.Columns != null && ph.Columns.TryGetValue("CreatedBy", out object cb) && cb != null)
        {
            string s = cb.ToString()?.Trim();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "Anonymous";
    }

    private List<string> GetOperationSelectColumnNames()
    {
        var list = new List<string> { "OperationID", "Name", "Description", "Phases" };
        if (HasOperationColumn("CreatedBy")) list.Add("CreatedBy");
        if (HasOperationColumn("CreatedDateTime")) list.Add("CreatedDateTime");
        return list;
    }
    #endregion

    /// <summary>与 Load/Save 一致的 Receipt 表列顺序（含可选审计列）。</summary>
    private List<string> GetReceiptSelectColumnNames()
    {
        var list = new List<string> { "ReceiptID", "Name", "Sequence", "Operations", "Description" };
        if (HasReceiptColumn("Status")) list.Add("Status");
        if (HasReceiptColumn("CreatedBy")) list.Add("CreatedBy");
        if (HasReceiptColumn("CreatedDateTime")) list.Add("CreatedDateTime");
        return list;
    }

    private List<string> GetPhaseColumnNames()
    {
        var list = new List<string>();
        if (_phaseTable == null) return list;
        try
        {
            if (_phaseTable.Columns != null)
                foreach (var col in _phaseTable.Columns)
                {
                    string n = col?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(n)) list.Add(n);
                }
            if (list.Count == 0)
                foreach (var child in _phaseTable.Children)
                {
                    string n = child?.BrowseName?.Trim();
                    if (!string.IsNullOrEmpty(n)) list.Add(n);
                }
        }
        catch { }
        if (list.Count == 0) list.Add("Name");
        return list;
    }

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

    private static Dictionary<int, (string Name, string Description, string Phases, string CreatedBy, string CreatedDateTime)> BuildOpDict(object[,] oRows, List<string> cols)
    {
        var d = new Dictionary<int, (string, string, string, string, string)>();
        if (oRows == null || cols == null || cols.Count == 0) return d;
        int Col(string name) => cols.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        int iId = Col("OperationID"), iName = Col("Name"), iDesc = Col("Description"), iPh = Col("Phases");
        int iBy = Col("CreatedBy"), iDt = Col("CreatedDateTime");
        if (iId < 0 || iName < 0 || iDesc < 0 || iPh < 0) return d;
        for (int r = 0; r < oRows.GetLength(0); r++)
        {
            int id = CellToInt(oRows[r, iId]);
            string cb = iBy >= 0 ? CellToString(oRows[r, iBy]) : "";
            string cdt = iDt >= 0 ? NormalizeStoredCreatedDateTime(oRows[r, iDt]) : "";
            d[id] = (CellToString(oRows[r, iName]), CellToString(oRows[r, iDesc]), CellToString(oRows[r, iPh]), cb, cdt);
        }
        return d;
    }

    private static Dictionary<int, PhaseNode> BuildPhaseDict(object[,] pRows, List<string> columns)
    {
        var d = new Dictionary<int, PhaseNode>();
        if (pRows == null || columns == null || columns.Count == 0) return d;
        int phaseIdIdx = columns.FindIndex(c => string.Equals(c, "PhaseID", StringComparison.OrdinalIgnoreCase));
        int nameIdx = columns.FindIndex(c => string.Equals(c, "Name", StringComparison.OrdinalIgnoreCase));
        for (int r = 0; r < pRows.GetLength(0); r++)
        {
            int id = phaseIdIdx >= 0 ? CellToInt(pRows[r, phaseIdIdx]) : CellToInt(pRows[r, 0]);
            var pNode = new PhaseNode { PhaseID = id };
            for (int c = 0; c < columns.Count && c < pRows.GetLength(1); c++)
            {
                object val = pRows[r, c];
                pNode.Columns[columns[c]] = val == null || val == DBNull.Value ? "" : val;
            }
            pNode.Name = nameIdx >= 0 ? CellToString(pRows[r, nameIdx]) : "";
            int descIdx = columns.FindIndex(c => string.Equals(c, "Description", StringComparison.OrdinalIgnoreCase));
            pNode.Description = descIdx >= 0 ? CellToString(pRows[r, descIdx]) : "";
            d[id] = pNode;
        }
        return d;
    }

    private static string CellToString(object cell)
    {
        if (cell == null || cell == DBNull.Value) return "";
        return cell is string str ? str.Trim() : (cell.ToString() ?? "").Trim();
    }

    private static int CellToInt(object cell)
    {
        if (cell == null || cell == DBNull.Value) return 0;
        if (cell is int i) return i;
        return int.TryParse(cell.ToString(), out int v) ? v : 0;
    }
    #endregion

    #region 修改标记与未保存项（UI 显示 *）
    /// <summary>本地树是否已被修改（相对于上次 Load 或 Save）。</summary>
    public bool IsModified { get; private set; }

    /// <summary>标记本地树已被修改。</summary>
    public void MarkModified() => IsModified = true;

    private readonly HashSet<int> _dirtyReceiptIds = new HashSet<int>();
    private readonly HashSet<int> _dirtyOperationIds = new HashSet<int>();
    private readonly HashSet<int> _dirtyPhaseIds = new HashSet<int>();

    public bool IsDirtyReceipt(int receiptId) => _dirtyReceiptIds.Contains(receiptId);
    public bool IsDirtyOperation(int operationId) => _dirtyOperationIds.Contains(operationId);
    public bool IsDirtyPhase(int phaseId) => _dirtyPhaseIds.Contains(phaseId);

    public void MarkDirtyReceipt(int receiptId) => _dirtyReceiptIds.Add(receiptId);
    public void MarkDirtyOperation(int operationId) => _dirtyOperationIds.Add(operationId);
    public void MarkDirtyPhase(int phaseId) => _dirtyPhaseIds.Add(phaseId);

    /// <summary>配方本身或其下工序/阶段被标为 dirty（含仅 MarkDirtyOperation 未保存场景）时，需要回写 Receipt 的 LastModified*。</summary>
    private bool ReceiptNeedsLastModifiedStamp(int receiptId)
    {
        if (receiptId <= 0) return false;
        if (IsDirtyReceipt(receiptId)) return true;
        if (!ReceiptById.TryGetValue(receiptId, out var r)) return false;
        foreach (var o in r.Operations)
        {
            if (IsDirtyOperation(o.OperationID)) return true;
            foreach (var p in o.Phases)
            {
                if (IsDirtyPhase(p.PhaseID)) return true;
            }
        }
        return false;
    }

    private bool OperationNeedsLastModifiedStamp(int operationId)
    {
        if (operationId <= 0) return false;
        if (IsDirtyOperation(operationId)) return true;
        if (!OperationById.TryGetValue(operationId, out var op)) return false;
        foreach (var p in op.Phases)
        {
            if (IsDirtyPhase(p.PhaseID)) return true;
        }
        return false;
    }

    private bool PhaseNeedsLastModifiedStamp(int phaseId) => phaseId > 0 && IsDirtyPhase(phaseId);

    /// <summary>将挂有该工序的配方标为已改，以便 Save 时写入 Receipt 的 LastModified*。</summary>
    private void MarkReceiptsAffectedByOperation(int operationId)
    {
        if (operationId <= 0) return;
        foreach (var r in Tree)
        {
            if (r.Operations.Exists(o => o.OperationID == operationId))
                MarkDirtyReceipt(r.ReceiptID);
        }
    }

    /// <summary>将包含该阶段的配方标为已改。</summary>
    private void MarkReceiptAffectedByPhase(int phaseId)
    {
        if (phaseId <= 0) return;
        foreach (var r in Tree)
        {
            foreach (var o in r.Operations)
            {
                if (o.Phases.Exists(p => p.PhaseID == phaseId))
                {
                    MarkDirtyReceipt(r.ReceiptID);
                    return;
                }
            }
        }
    }

    private void ClearDirty()
    {
        _dirtyReceiptIds.Clear();
        _dirtyOperationIds.Clear();
        _dirtyPhaseIds.Clear();
    }

    /// <summary>
    /// 仅更新库中 Receipt 的 LastModified*（不改 Operations 等）。用于 Insert 工序/阶段且 persistToDb=false 时，
    /// 配方列表等直接读库的界面能立即看到最后修改人与时间。
    /// </summary>
    public void TouchReceiptLastModifiedInStore(int receiptId)
    {
        if (_store == null || string.IsNullOrEmpty(_receiptTableName) || receiptId <= 0) return;
        if (!ReceiptById.TryGetValue(receiptId, out var receipt)) return;
        if (!HasReceiptColumn("LastModifiedBY") && !HasReceiptColumn("LastModifiedDateTime")) return;
        var parts = new List<string>();
        if (HasReceiptColumn("LastModifiedBY"))
            parts.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForReceipt(receipt))}'");
        if (HasReceiptColumn("LastModifiedDateTime"))
            parts.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
        if (parts.Count == 0) return;
        try
        {
            _store.Query(
                $"UPDATE {_receiptTableName} SET {string.Join(", ", parts)} WHERE ReceiptID={receiptId}",
                out _, out _);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TouchReceiptLastModifiedInStore({receiptId}): {ex.Message}");
        }
    }

    private void TouchReceiptLastModifiedInStoreForOperation(int operationId)
    {
        if (operationId <= 0) return;
        foreach (var r in Tree)
        {
            if (r.Operations.Exists(o => o.OperationID == operationId))
                TouchReceiptLastModifiedInStore(r.ReceiptID);
        }
    }

    public void TouchOperationLastModifiedInStore(int operationId)
    {
        if (_store == null || string.IsNullOrEmpty(_opTableName) || operationId <= 0) return;
        if (!OperationById.TryGetValue(operationId, out var op)) return;
        if (!HasOperationColumn("LastModifiedBY") && !HasOperationColumn("LastModifiedDateTime")) return;
        var parts = new List<string>();
        if (HasOperationColumn("LastModifiedBY"))
            parts.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForOperation(op))}'");
        if (HasOperationColumn("LastModifiedDateTime"))
            parts.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
        if (parts.Count == 0) return;
        try
        {
            _store.Query(
                $"UPDATE {_opTableName} SET {string.Join(", ", parts)} WHERE OperationID={operationId}",
                out _, out _);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TouchOperationLastModifiedInStore({operationId}): {ex.Message}");
        }
    }

    public void TouchPhaseLastModifiedInStore(int phaseId)
    {
        if (_store == null || string.IsNullOrEmpty(_phaseTableName) || phaseId <= 0) return;
        if (!PhaseById.TryGetValue(phaseId, out var ph)) return;
        if (!HasPhaseColumn("LastModifiedBY") && !HasPhaseColumn("LastModifiedDateTime")) return;
        var parts = new List<string>();
        if (HasPhaseColumn("LastModifiedBY"))
            parts.Add($"LastModifiedBY='{EscapeSql(GetLastModifiedByForPhase(ph))}'");
        if (HasPhaseColumn("LastModifiedDateTime"))
            parts.Add($"LastModifiedDateTime='{EscapeSql(FormatStoredCreatedDateTimeNow())}'");
        if (parts.Count == 0) return;
        try
        {
            _store.Query(
                $"UPDATE {_phaseTableName} SET {string.Join(", ", parts)} WHERE PhaseID={phaseId}",
                out _, out _);
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Warning(LogCategory, $"TouchPhaseLastModifiedInStore({phaseId}): {ex.Message}");
        }
    }

    private void TouchOperationLastModifiedInStoreForPhase(int phaseId)
    {
        if (phaseId <= 0) return;
        foreach (var op in OperationById.Values)
        {
            if (op.Phases.Exists(p => p.PhaseID == phaseId))
            {
                TouchOperationLastModifiedInStore(op.OperationID);
                return;
            }
        }
    }
    #endregion

    #region 树增删改 API（仅修改内存树，不写 Store）
    // ── Receipt ──────────────────────────────────────────────────────────────

    /// <summary>新增 Receipt 到本地树，返回新 ReceiptID。createdBy/createdDateTime 为空时与 <see cref="AddOperation"/> 一致：自动填当前会话用户与当前时间（Save 时写入表）。</summary>
    public int AddReceipt(string name, string description = "", string createdBy = "", string createdDateTime = "")
    {
        int newId = ReceiptById.Count > 0 ? MaxDictKey(ReceiptById) + 1 : 1;
        int newSeq = Tree.Count > 0 ? MaxReceiptSeq() + 1 : 1;
        string by = !string.IsNullOrEmpty(createdBy) ? createdBy : GetSessionUserBrowseName();
        string dt = !string.IsNullOrEmpty(createdDateTime) ? NormalizeStoredCreatedDateTime(createdDateTime) : FormatStoredCreatedDateTimeNow();
        var node = new ReceiptNode
        {
            ReceiptID = newId,
            Name = name ?? "",
            Sequence = newSeq,
            Description = description ?? "",
            Status = DefaultReceiptStatus,
            CreatedBy = by,
            CreatedDateTime = dt
        };
        Tree.Add(node);
        ReceiptById[newId] = node;
        MarkModified();
        return newId;
    }

    /// <summary>更新本地树中指定 Receipt 的 Name/Description。</summary>
    public bool UpdateReceipt(int receiptId, string name, string description)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var node)) return false;
        if (name != null) node.Name = name;
        if (description != null) node.Description = description;
        MarkModified();
        MarkDirtyReceipt(receiptId);
        return true;
    }

    /// <summary>更新 Receipt.Status（下拉暂存后在保存前写入）。</summary>
    public bool UpdateReceiptStatus(int receiptId, string status)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var node)) return false;
        node.Status = string.IsNullOrWhiteSpace(status) ? DefaultReceiptStatus : status.Trim();
        MarkModified();
        MarkDirtyReceipt(receiptId);
        return true;
    }

    /// <summary>从本地树删除指定 Receipt（含其所有 Operation/Phase）。</summary>
    public bool RemoveReceipt(int receiptId)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return false;
        foreach (var op in rNode.Operations)
        {
            foreach (var ph in op.Phases) PhaseById.Remove(ph.PhaseID);
            OperationById.Remove(op.OperationID);
        }
        Tree.Remove(rNode);
        ReceiptById.Remove(receiptId);
        MarkModified();
        return true;
    }

    // ── Operation ────────────────────────────────────────────────────────────

    /// <summary>在指定 Receipt 下新增 Operation，返回新 OperationID。createdBy/createdDateTime 为空时自动填当前用户与当前时间（表含对应列时 Save 会写入）。</summary>
    public int AddOperation(int receiptId, string name, string description = "", string createdBy = "", string createdDateTime = "")
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return -1;
        int newId = OperationById.Count > 0 ? MaxDictKey(OperationById) + 1 : 1;
        string by = !string.IsNullOrEmpty(createdBy) ? createdBy : GetSessionUserBrowseName();
        string dt = !string.IsNullOrEmpty(createdDateTime) ? NormalizeStoredCreatedDateTime(createdDateTime) : FormatStoredCreatedDateTimeNow();
        var node = new OperationNode
        {
            OperationID = newId,
            Name = name ?? "",
            Description = description ?? "",
            CreatedBy = by,
            CreatedDateTime = dt
        };
        rNode.Operations.Add(node);
        OperationById[newId] = node;
        MarkModified();
        MarkDirtyReceipt(receiptId);
        TouchReceiptLastModifiedInStore(receiptId);
        TouchOperationLastModifiedInStore(newId);
        return newId;
    }

    /// <summary>工序是否未挂在任何配方的 <see cref="ReceiptNode.Operations"/> 下（仅存在于 <see cref="OperationById"/> 的独立工序）。</summary>
    public bool IsOperationUnattachedToAnyReceipt(int operationId)
    {
        if (operationId <= 0) return false;
        foreach (var r in Tree)
            foreach (var op in r.Operations)
                if (op.OperationID == operationId)
                    return false;
        return true;
    }

    /// <summary>
    /// 将已在库中的工序挂到指定配方下：不新建 OperationID、不复制 Phase，仅把同一节点加入配方序列（Save 时只更新 Receipt.Operations CSV）。
    /// 用于「Create New Operation」已生成独立行后，Insert 到配方时避免同名同版本重复行。
    /// </summary>
    /// <param name="insertAfterOperationId">0 表示插在配方工序列表末尾；否则插在该 OperationID 之后。</param>
    /// <returns>成功返回 <paramref name="operationId"/>；已在树中或其它配方下占用时返回 -1。</returns>
    public int AttachExistingOperationToReceipt(int receiptId, int operationId, int insertAfterOperationId)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return -1;
        if (!OperationById.TryGetValue(operationId, out var opNode)) return -1;
        if (rNode.Operations.Exists(o => o.OperationID == operationId)) return -1;
        foreach (var r in Tree)
        {
            if (r.ReceiptID == receiptId) continue;
            if (r.Operations.Exists(o => o.OperationID == operationId)) return -1;
        }
        rNode.Operations.Add(opNode);
        int insertIdx = rNode.Operations.Count - 1;
        int srcIdx = insertAfterOperationId > 0 ? rNode.Operations.FindIndex(o => o.OperationID == insertAfterOperationId) : -1;
        if (insertIdx > 0 && srcIdx >= 0 && insertIdx != srcIdx + 1)
        {
            rNode.Operations.RemoveAt(insertIdx);
            rNode.Operations.Insert(srcIdx + 1, opNode);
        }
        MarkModified();
        MarkDirtyReceipt(receiptId);
        TouchReceiptLastModifiedInStore(receiptId);
        TouchOperationLastModifiedInStore(operationId);
        return operationId;
    }

    /// <summary>更新本地树中指定 Operation 的 Name。</summary>
    public bool UpdateOperation(int operationId, string name)
    {
        if (!OperationById.TryGetValue(operationId, out var node)) return false;
        if (name != null) node.Name = name;
        MarkModified();
        MarkReceiptsAffectedByOperation(operationId);
        TouchOperationLastModifiedInStore(operationId);
        return true;
    }

    /// <summary>从本地树删除指定 Operation（含其所有 Phase），并从所属 Receipt 列表中移除。</summary>
    public bool RemoveOperation(int receiptId, int operationId)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return false;
        if (!OperationById.TryGetValue(operationId, out var opNode)) return false;
        foreach (var ph in opNode.Phases) PhaseById.Remove(ph.PhaseID);
        rNode.Operations.Remove(opNode);
        OperationById.Remove(operationId);
        MarkModified();
        MarkDirtyReceipt(receiptId);
        return true;
    }

    /// <summary>
    /// Discarded「Unused Operation」网格：删除未挂在任何配方下的工序及其内存中的 Phase 引用。
    /// </summary>
    public bool RemoveStandaloneOperationForDiscardedGrid(int operationId)
    {
        if (operationId <= 0) return false;
        if (!OperationById.TryGetValue(operationId, out var opNode)) return false;
        if (!IsOperationUnattachedToAnyReceipt(operationId)) return false;
        var phasesCopy = new List<PhaseNode>(opNode.Phases);
        foreach (var ph in phasesCopy)
            PhaseById.Remove(ph.PhaseID);
        opNode.Phases.Clear();
        OperationById.Remove(operationId);
        MarkModified();
        return true;
    }

    /// <summary>
    /// Discarded「Unused Phases」网格：删除未出现在任何 Operation.Phases 中的阶段（与 ComputeUnusedIdsFromDatabase 定义一致）。
    /// </summary>
    public bool RemoveStandalonePhaseForDiscardedGrid(int phaseId)
    {
        if (phaseId <= 0) return false;
        if (!PhaseById.TryGetValue(phaseId, out _)) return false;
        foreach (var op in OperationById.Values)
            if (op.Phases.Exists(p => p.PhaseID == phaseId))
                return false;
        PhaseById.Remove(phaseId);
        MarkModified();
        return true;
    }

    /// <summary>仅新建到 Operation 表并加入 OperationById，不加入任何 Receipt 树；用于“创建新 Operation”仅写库、刷新右侧 List。</summary>
    public int AddOperationStandalone(string name, string description = "")
    {
        if (_opTable == null || string.IsNullOrEmpty(_opTableName)) return -1;
        int newId = OperationById.Count > 0 ? MaxDictKey(OperationById) + 1 : 1;
        string user = GetSessionUserBrowseName();
        string dt = FormatStoredCreatedDateTimeNow();
        var node = new OperationNode { OperationID = newId, Name = name ?? "", Description = description ?? "", CreatedBy = user, CreatedDateTime = dt };
        OperationById[newId] = node;
        try
        {
            var insCols = new List<string> { "OperationID", "Name", "Description", "Phases" };
            var insVals = new List<object> { newId, name ?? "", description ?? "", "" };
            if (HasOperationColumn("CreatedBy")) { insCols.Add("CreatedBy"); insVals.Add(user); }
            if (HasOperationColumn("CreatedDateTime")) { insCols.Add("CreatedDateTime"); insVals.Add(dt); }
            if (HasOperationColumn("LastModifiedBY")) { insCols.Add("LastModifiedBY"); insVals.Add(user); }
            if (HasOperationColumn("LastModifiedDateTime")) { insCols.Add("LastModifiedDateTime"); insVals.Add(dt); }
            var row = new object[1, insVals.Count];
            for (int c = 0; c < insVals.Count; c++) row[0, c] = insVals[c];
            _opTable.Insert(insCols.ToArray(), row);
        }
        catch (Exception ex)
        {
            OperationById.Remove(newId);
            if (EnableLog) Log.Error(LogCategory, $"AddOperationStandalone 写入表失败: {ex.Message}");
            return -1;
        }
        if (EnableLog) Log.Info(LogCategory, $"AddOperationStandalone: OperationID={newId}, Name='{name}'");
        RefreshDiscardedUnusedGridQueries();
        return newId;
    }

    // ── Phase ─────────────────────────────────────────────────────────────────

    #region 新建 Phase 模板与参数表

    /// <summary>新建内存 Phase：参数行 ID 置 0 待分配；模板类型缺省 1。</summary>
    private void ApplyNewPhaseIdentityForCreate(PhaseNode node)
    {
        if (node?.Columns == null) return;
        node.Columns["PhaseParameterInfoID"] = 0;
        if (!HasPhaseColumn("PhaseTemplateTypeID")) return;
        if (!node.Columns.TryGetValue("PhaseTemplateTypeID", out object tt) || tt == null || tt == DBNull.Value || CellToInt(tt) == 0)
            node.Columns["PhaseTemplateTypeID"] = 1;
    }
    #endregion

    /// <summary>在指定 Operation 下新增 Phase，返回新 PhaseID。createdBy/createdDateTime 用于另存为等新行审计；为空则用当前用户与时间。</summary>
    public int AddPhase(int operationId, string name, Dictionary<string, object> columns = null, string createdBy = null, string createdDateTime = null)
    {
        if (!OperationById.TryGetValue(operationId, out var opNode)) return -1;
        int newId = PhaseById.Count > 0 ? MaxDictKey(PhaseById) + 1 : 1;
        var node = new PhaseNode { PhaseID = newId, Name = name ?? "" };
        if (columns != null)
            foreach (var kv in columns) node.Columns[kv.Key] = kv.Value;
        node.Columns["PhaseID"] = newId;
        node.Columns["Name"] = node.Name;
        ApplyNewPhaseIdentityForCreate(node);
        if (node.Columns.TryGetValue("Description", out object descObj))
            node.Description = descObj?.ToString() ?? "";
        if (HasPhaseColumn("CreatedBy"))
            node.Columns["CreatedBy"] = !string.IsNullOrEmpty(createdBy) ? createdBy : GetSessionUserBrowseName();
        if (HasPhaseColumn("CreatedDateTime"))
            node.Columns["CreatedDateTime"] = !string.IsNullOrEmpty(createdDateTime)
                ? NormalizeStoredCreatedDateTime(createdDateTime)
                : FormatStoredCreatedDateTimeNow();
        opNode.Phases.Add(node);
        PhaseById[newId] = node;
        MarkModified();
        MarkReceiptsAffectedByOperation(operationId);
        TouchReceiptLastModifiedInStoreForOperation(operationId);
        TouchOperationLastModifiedInStore(operationId);
        TouchPhaseLastModifiedInStore(newId);
        return newId;
    }

    /// <summary>更新本地树中指定 Phase 的 Name 与列值。</summary>
    public bool UpdatePhase(int phaseId, string name, Dictionary<string, object> columns = null)
    {
        if (!PhaseById.TryGetValue(phaseId, out var node)) return false;
        if (name != null) { node.Name = name; node.Columns["Name"] = name; }
        if (columns != null)
            foreach (var kv in columns) node.Columns[kv.Key] = kv.Value;
        MarkModified();
        MarkReceiptAffectedByPhase(phaseId);
        TouchPhaseLastModifiedInStore(phaseId);
        TouchOperationLastModifiedInStoreForPhase(phaseId);
        return true;
    }

    /// <summary>从本地树删除指定 Phase，并从所属 Operation 列表中移除。</summary>
    public bool RemovePhase(int operationId, int phaseId)
    {
        if (!OperationById.TryGetValue(operationId, out var opNode)) return false;
        if (!PhaseById.TryGetValue(phaseId, out var pNode)) return false;
        opNode.Phases.Remove(pNode);
        PhaseById.Remove(phaseId);
        MarkModified();
        MarkReceiptsAffectedByOperation(operationId);
        TouchOperationLastModifiedInStore(operationId);
        return true;
    }

    /// <summary>仅新建到 Phase 表并加入 PhaseById，不加入任何 Operation 树；用于“创建新 Phase”仅写库、刷新右侧 List。</summary>
    public int AddPhaseStandalone(string name, string description = "")
    {
        if (_phaseTable == null || string.IsNullOrEmpty(_phaseTableName)) return -1;
        int newId = PhaseById.Count > 0 ? MaxDictKey(PhaseById) + 1 : 1;
        string user = GetSessionUserBrowseName();
        string dt = FormatStoredCreatedDateTimeNow();
        var node = new PhaseNode { PhaseID = newId, Name = name ?? "", Description = description ?? "" };
        node.Columns["PhaseID"] = newId;
        node.Columns["Name"] = node.Name;
        node.Columns["Description"] = description ?? "";
        if (HasPhaseColumn("CreatedBy")) node.Columns["CreatedBy"] = user;
        if (HasPhaseColumn("CreatedDateTime")) node.Columns["CreatedDateTime"] = dt;
        ApplyNewPhaseIdentityForCreate(node);
        PhaseById[newId] = node;
        try
        {
            SavePhaseInsert(node);
        }
        catch (Exception ex)
        {
            PhaseById.Remove(newId);
            if (EnableLog) Log.Error(LogCategory, $"AddPhaseStandalone 写入表失败: {ex.Message}");
            return -1;
        }
        if (EnableLog) Log.Info(LogCategory, $"AddPhaseStandalone: PhaseID={newId}, Name='{name}'");
        RefreshDiscardedUnusedGridQueries();
        return newId;
    }

    // ── 上下移 ───────────────────────────────────────────────────────────────

    /// <summary>在 Tree 列表中将指定 Receipt 与其相邻项交换 Sequence，并保持 Tree 按 Sequence 升序排序。</summary>
    public bool MoveReceiptUp(int receiptId) => SwapReceiptSequence(receiptId, up: true);
    public bool MoveReceiptDown(int receiptId) => SwapReceiptSequence(receiptId, up: false);

    private bool SwapReceiptSequence(int receiptId, bool up)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var node)) return false;
        int idx = Tree.IndexOf(node);
        if (idx < 0) return false;
        int swapIdx = up ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= Tree.Count) return false;
        int dirtyA = Tree[idx].ReceiptID;
        int dirtyB = Tree[swapIdx].ReceiptID;
        int seqA = Tree[idx].Sequence;
        int seqB = Tree[swapIdx].Sequence;
        Tree[idx].Sequence = seqB;
        Tree[swapIdx].Sequence = seqA;
        Tree[idx] = Tree[swapIdx];
        Tree[swapIdx] = node;
        MarkModified();
        MarkDirtyReceipt(dirtyA);
        MarkDirtyReceipt(dirtyB);
        return true;
    }

    /// <summary>在所属 Receipt 的 Operations 列表中将指定 Operation 与相邻项交换位置。</summary>
    public bool MoveOperationUp(int receiptId, int operationId) => SwapOperationOrder(receiptId, operationId, up: true);
    public bool MoveOperationDown(int receiptId, int operationId) => SwapOperationOrder(receiptId, operationId, up: false);

    private bool SwapOperationOrder(int receiptId, int operationId, bool up)
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return false;
        var ops = rNode.Operations;
        int idx = ops.FindIndex(o => o.OperationID == operationId);
        if (idx < 0) return false;
        int swapIdx = up ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= ops.Count) return false;
        int opIdA = ops[idx].OperationID;
        int opIdB = ops[swapIdx].OperationID;
        var tmp = ops[idx]; ops[idx] = ops[swapIdx]; ops[swapIdx] = tmp;
        MarkModified();
        MarkDirtyReceipt(receiptId);
        TouchOperationLastModifiedInStore(opIdA);
        TouchOperationLastModifiedInStore(opIdB);
        return true;
    }

    /// <summary>在所属 Operation 的 Phases 列表中将指定 Phase 与相邻项交换位置。</summary>
    public bool MovePhaseUp(int operationId, int phaseId) => SwapPhaseOrder(operationId, phaseId, up: true);
    public bool MovePhaseDown(int operationId, int phaseId) => SwapPhaseOrder(operationId, phaseId, up: false);

    private bool SwapPhaseOrder(int operationId, int phaseId, bool up)
    {
        if (!OperationById.TryGetValue(operationId, out var opNode)) return false;
        var phases = opNode.Phases;
        int idx = phases.FindIndex(p => p.PhaseID == phaseId);
        if (idx < 0) return false;
        int swapIdx = up ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= phases.Count) return false;
        int idA = phases[idx].PhaseID;
        int idB = phases[swapIdx].PhaseID;
        var tmp = phases[idx]; phases[idx] = phases[swapIdx]; phases[swapIdx] = tmp;
        MarkModified();
        MarkReceiptsAffectedByOperation(operationId);
        TouchOperationLastModifiedInStore(operationId);
        TouchPhaseLastModifiedInStore(idA);
        TouchPhaseLastModifiedInStore(idB);
        return true;
    }

    /// <summary>根据当前树选中项判断是否可以上移/下移（与 Swap* 中索引边界一致）。</summary>
    public bool TryGetMoveAvailability(int receiptId, int operationId, int phaseId, out bool canUp, out bool canDown)
    {
        canUp = canDown = false;
        if (phaseId > 0 && operationId > 0)
        {
            if (!OperationById.TryGetValue(operationId, out var opNode)) return false;
            var phases = opNode.Phases;
            int idx = phases.FindIndex(p => p.PhaseID == phaseId);
            if (idx < 0) return false;
            canUp = idx > 0;
            canDown = idx < phases.Count - 1;
            return true;
        }
        if (operationId > 0 && receiptId > 0)
        {
            if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return false;
            var ops = rNode.Operations;
            int idx = ops.FindIndex(o => o.OperationID == operationId);
            if (idx < 0) return false;
            canUp = idx > 0;
            canDown = idx < ops.Count - 1;
            return true;
        }
        if (receiptId > 0)
        {
            if (!ReceiptById.TryGetValue(receiptId, out var node)) return false;
            int idx = Tree.IndexOf(node);
            if (idx < 0) return false;
            canUp = idx > 0;
            canDown = idx < Tree.Count - 1;
            return true;
        }
        return false;
    }

    private static int MaxDictKey<T>(Dictionary<int, T> dict)
    {
        int max = 0;
        foreach (int k in dict.Keys) if (k > max) max = k;
        return max;
    }

    private int MaxReceiptSeq()
    {
        int max = 0;
        foreach (var r in Tree) if (r.Sequence > max) max = r.Sequence;
        return max;
    }
    #endregion
}
