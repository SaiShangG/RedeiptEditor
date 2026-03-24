#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
#endregion

/// <summary>从配方数据库读取全部数据并保存到本地 dict 树（Receipt → Operation → Phase）。</summary>
public class RecipeDatabaseTreeLoader : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseTreeLoader";
    private const bool EnableLog = true;
    /// <summary>新建 Receipt 时 Status 列的默认值（与 DataStores 中 StoreColumn 默认值一致）。</summary>
    public const string DefaultReceiptStatus = "Development";

    /// <summary>Receipt.CreatedDateTime 在库内/内存中的统一格式，例如 2026-03-22T16:49:03.6596165。</summary>
    public const string CreatedDateTimeStorageFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";

    /// <summary>当前本地时刻，按 <see cref="CreatedDateTimeStorageFormat"/> 格式化，用于新建 Receipt。</summary>
    public static string FormatStoredCreatedDateTimeNow()
    {
        return DateTime.Now.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>将数据库单元格或字符串统一为 <see cref="CreatedDateTimeStorageFormat"/>；无法解析时保留原字符串修剪结果。</summary>
    public static string NormalizeStoredCreatedDateTime(object value)
    {
        if (value == null || value == DBNull.Value) return "";
        if (value is DateTime dt)
            return dt.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        string s = Convert.ToString(value)?.Trim() ?? "";
        if (string.IsNullOrEmpty(s)) return "";
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime p1))
            return p1.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime p2))
            return p2.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        foreach (string cultureName in new[] { "zh-CN", "en-US" })
        {
            try
            {
                if (DateTime.TryParse(s, CultureInfo.GetCultureInfo(cultureName), DateTimeStyles.None, out DateTime p3))
                    return p3.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
            }
            catch (CultureNotFoundException) { }
        }
        string[] exactFormats =
        {
            "yyyy/M/d H:mm:ss", "yyyy/M/d HH:mm:ss", "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd HH:mm:ss",
            "yyyy-M-d H:mm:ss", "yyyy-MM-dd HH:mm:ss"
        };
        foreach (string fmt in exactFormats)
        {
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime p4))
                return p4.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        }
        return s;
    }

    public static RecipeDatabaseTreeLoader Instance { get; private set; }

    private Store _store;
    private Table _receiptTable, _opTable, _phaseTable;
    private string _receiptTableName, _opTableName, _phaseTableName;

    #region 树节点类型
    public class ReceiptNode
    {
        public int ReceiptID;
        public string Name;
        public int Sequence;
        public string OperationsCsv;
        public string Description;
        /// <summary>状态（与 Receipt 表 Status 列对应；新建时默认为 Development）。</summary>
        public string Status = DefaultReceiptStatus;
        /// <summary>创建人（与 Receipt 表 CreatedBy 列对应，表无此列时不参与 SQL）。</summary>
        public string CreatedBy = "";
        /// <summary>创建时间（与 Receipt 表 CreatedDateTime 列对应，表无此列时不参与 SQL）。</summary>
        public string CreatedDateTime = "";
        public List<OperationNode> Operations = new List<OperationNode>();
    }

    public class OperationNode
    {
        public int OperationID;
        public string Name;
        public string Description;
        public string PhasesCsv;
        public List<PhaseNode> Phases = new List<PhaseNode>();
    }

    public class PhaseNode
    {
        public int PhaseID;
        public string Name;
        public string Description;
        public Dictionary<string, object> Columns = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }
    #endregion

    #region 本地 dict 树（只读）
    /// <summary>按 Sequence 排序的配方树根列表。</summary>
    public List<ReceiptNode> Tree { get; private set; } = new List<ReceiptNode>();

    /// <summary>ReceiptID → ReceiptNode 快速查找。</summary>
    public Dictionary<int, ReceiptNode> ReceiptById { get; private set; } = new Dictionary<int, ReceiptNode>();

    /// <summary>OperationID → OperationNode 快速查找。</summary>
    public Dictionary<int, OperationNode> OperationById { get; private set; } = new Dictionary<int, OperationNode>();

    /// <summary>PhaseID → PhaseNode 快速查找。</summary>
    public Dictionary<int, PhaseNode> PhaseById { get; private set; } = new Dictionary<int, PhaseNode>();

    /// <summary>按名称查找 OperationNode（忽略大小写）。返回第一个匹配项，未找到返回 null。</summary>
    public OperationNode FindOperationByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var op in OperationById.Values)
            if (string.Equals(op.Name, name, StringComparison.OrdinalIgnoreCase))
                return op;
        return null;
    }

    /// <summary>按名称查找所有匹配的 OperationNode（忽略大小写）。</summary>
    public List<OperationNode> FindOperationsByName(string name)
    {
        var result = new List<OperationNode>();
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var op in OperationById.Values)
            if (string.Equals(op.Name, name, StringComparison.OrdinalIgnoreCase))
                result.Add(op);
        return result;
    }

    /// <summary>按名称查找 PhaseNode（忽略大小写）。返回第一个匹配项，未找到返回 null。</summary>
    public PhaseNode FindPhaseByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var ph in PhaseById.Values)
            if (string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase))
                return ph;
        return null;
    }

    /// <summary>按名称查找所有匹配的 PhaseNode（忽略大小写）。</summary>
    public List<PhaseNode> FindPhasesByName(string name)
    {
        var result = new List<PhaseNode>();
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var ph in PhaseById.Values)
            if (string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase))
                result.Add(ph);
        return result;
    }
    #endregion

    #region 生命周期与打开数据库
    public override void Start()
    {
        Instance = this;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseTreeLoader 已启动");
        OpenTables();
        LoadAllToTree();
    }

    public override void Stop()
    {
        ClearTree();
        Instance = null;
        _store = null;
        _receiptTable = _opTable = _phaseTable = null;
        _receiptTableName = _opTableName = _phaseTableName = null;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseTreeLoader 已停止");
    }

    /// <summary>解析 ReceiptDB/OperationDB/PhaseDB，打开 Store 与表。</summary>
    private void OpenTables()
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
        return table != null;
    }



    private static Store GetStoreFromNode(IUANode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Store s) return s;
            current = current.Owner;
        }
        return null;
    }

    private static Table GetTableFromStoreByNodeId(Store store, NodeId tableNodeId)
    {
        foreach (var child in store.Children)
        {
            if (child.NodeId == tableNodeId && child is Table t) return t;
        }
        return null;
    }
    #endregion

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
                    _store.Query($"SELECT OperationID, Name, Description, Phases FROM {_opTableName} WHERE OperationID IN ({inClause})", out _, out object[,] oRows);
                    var opById = BuildOpDict(oRows);

                    foreach (int oId in opIdList)
                    {
                        if (!opById.TryGetValue(oId, out var opPair)) continue;
                        var oNode = new OperationNode { OperationID = oId, Name = opPair.Name, Description = opPair.Description ?? "", PhasesCsv = opPair.Phases ?? "" };
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
                _store.Query($"SELECT OperationID, Name, Description, Phases FROM {_opTableName}", out _, out object[,] allOpRows);
                if (allOpRows != null)
                    for (int r = 0; r < allOpRows.GetLength(0); r++)
                    {
                        int oId = CellToInt(allOpRows[r, 0]);
                        if (OperationById.ContainsKey(oId)) continue;
                        var oNode = new OperationNode { OperationID = oId, Name = CellToString(allOpRows[r, 1]), Description = CellToString(allOpRows[r, 2]) ?? "", PhasesCsv = CellToString(allOpRows[r, 3]) ?? "" };
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
        }
        catch (Exception ex)
        {
            if (EnableLog) Log.Error(LogCategory, $"LoadAllToTree 失败: {ex.Message}");
        }
    }

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

    private static Dictionary<int, (string Name, string Description, string Phases)> BuildOpDict(object[,] oRows)
    {
        var d = new Dictionary<int, (string, string, string)>();
        if (oRows == null) return d;
        for (int r = 0; r < oRows.GetLength(0); r++)
        {
            int id = CellToInt(oRows[r, 0]);
            d[id] = (CellToString(oRows[r, 1]), CellToString(oRows[r, 2]), CellToString(oRows[r, 3]));
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

    private void ClearDirty()
    {
        _dirtyReceiptIds.Clear();
        _dirtyOperationIds.Clear();
        _dirtyPhaseIds.Clear();
    }
    #endregion

    #region 树增删改 API（仅修改内存树，不写 Store）
    // ── Receipt ──────────────────────────────────────────────────────────────

    /// <summary>新增 Receipt 到本地树，返回新 ReceiptID。若表含 CreatedBy/CreatedDateTime 列，应传入当前用户与时间字符串。</summary>
    public int AddReceipt(string name, string description = "", string createdBy = "", string createdDateTime = "")
    {
        int newId = ReceiptById.Count > 0 ? MaxDictKey(ReceiptById) + 1 : 1;
        int newSeq = Tree.Count > 0 ? MaxReceiptSeq() + 1 : 1;
        var node = new ReceiptNode
        {
            ReceiptID = newId,
            Name = name ?? "",
            Sequence = newSeq,
            Description = description ?? "",
            Status = DefaultReceiptStatus,
            CreatedBy = createdBy ?? "",
            CreatedDateTime = string.IsNullOrWhiteSpace(createdDateTime) ? "" : NormalizeStoredCreatedDateTime(createdDateTime)
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

    /// <summary>在指定 Receipt 下新增 Operation，返回新 OperationID。</summary>
    public int AddOperation(int receiptId, string name, string description = "")
    {
        if (!ReceiptById.TryGetValue(receiptId, out var rNode)) return -1;
        int newId = OperationById.Count > 0 ? MaxDictKey(OperationById) + 1 : 1;
        var node = new OperationNode { OperationID = newId, Name = name ?? "", Description = description ?? "" };
        rNode.Operations.Add(node);
        OperationById[newId] = node;
        MarkModified();
        return newId;
    }

    /// <summary>更新本地树中指定 Operation 的 Name。</summary>
    public bool UpdateOperation(int operationId, string name)
    {
        if (!OperationById.TryGetValue(operationId, out var node)) return false;
        if (name != null) node.Name = name;
        MarkModified();
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
        return true;
    }

    /// <summary>仅新建到 Operation 表并加入 OperationById，不加入任何 Receipt 树；用于“创建新 Operation”仅写库、刷新右侧 List。</summary>
    public int AddOperationStandalone(string name, string description = "")
    {
        if (_opTable == null || string.IsNullOrEmpty(_opTableName)) return -1;
        int newId = OperationById.Count > 0 ? MaxDictKey(OperationById) + 1 : 1;
        var node = new OperationNode { OperationID = newId, Name = name ?? "", Description = description ?? "" };
        OperationById[newId] = node;
        try
        {
            _opTable.Insert(new[] { "OperationID", "Name", "Description", "Phases" }, new object[,] { { newId, name ?? "", description ?? "", "" } });
        }
        catch (Exception ex)
        {
            OperationById.Remove(newId);
            if (EnableLog) Log.Error(LogCategory, $"AddOperationStandalone 写入表失败: {ex.Message}");
            return -1;
        }
        if (EnableLog) Log.Info(LogCategory, $"AddOperationStandalone: OperationID={newId}, Name='{name}'");
        return newId;
    }

    // ── Phase ─────────────────────────────────────────────────────────────────

    /// <summary>在指定 Operation 下新增 Phase，返回新 PhaseID。</summary>
    public int AddPhase(int operationId, string name, Dictionary<string, object> columns = null)
    {
        if (!OperationById.TryGetValue(operationId, out var opNode)) return -1;
        int newId = PhaseById.Count > 0 ? MaxDictKey(PhaseById) + 1 : 1;
        var node = new PhaseNode { PhaseID = newId, Name = name ?? "" };
        if (columns != null)
            foreach (var kv in columns) node.Columns[kv.Key] = kv.Value;
        node.Columns["PhaseID"] = newId;
        node.Columns["Name"] = node.Name;
        if (node.Columns.TryGetValue("Description", out object descObj))
            node.Description = descObj?.ToString() ?? "";
        opNode.Phases.Add(node);
        PhaseById[newId] = node;
        MarkModified();
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
        return true;
    }

    /// <summary>仅新建到 Phase 表并加入 PhaseById，不加入任何 Operation 树；用于“创建新 Phase”仅写库、刷新右侧 List。</summary>
    public int AddPhaseStandalone(string name, string description = "")
    {
        if (_phaseTable == null || string.IsNullOrEmpty(_phaseTableName)) return -1;
        int newId = PhaseById.Count > 0 ? MaxDictKey(PhaseById) + 1 : 1;
        var node = new PhaseNode { PhaseID = newId, Name = name ?? "", Description = description ?? "" };
        node.Columns["PhaseID"] = newId;
        node.Columns["Name"] = node.Name;
        node.Columns["Description"] = description ?? "";
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
        int seqA = Tree[idx].Sequence;
        int seqB = Tree[swapIdx].Sequence;
        Tree[idx].Sequence = seqB;
        Tree[swapIdx].Sequence = seqA;
        Tree[idx] = Tree[swapIdx];
        Tree[swapIdx] = node;
        MarkModified();
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
        var tmp = ops[idx]; ops[idx] = ops[swapIdx]; ops[swapIdx] = tmp;
        MarkModified();
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
        var tmp = phases[idx]; phases[idx] = phases[swapIdx]; phases[swapIdx] = tmp;
        MarkModified();
        return true;
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
                    var row = new object[1, insVals.Count];
                    for (int c = 0; c < insVals.Count; c++) row[0, c] = insVals[c];
                    _receiptTable.Insert(insCols.ToArray(), row);
                }

                foreach (var op in receipt.Operations)
                {
                    string phsCsv = BuildIdCsv(op.Phases, ph => ph.PhaseID);
                    op.PhasesCsv = phsCsv;
                    if (dbOpIds.Contains(op.OperationID))
                        _store.Query(
                            $"UPDATE {_opTableName} SET Name='{EscapeSql(op.Name)}', Description='{EscapeSql(op.Description ?? "")}', Phases='{EscapeSql(phsCsv)}' WHERE OperationID={op.OperationID}",
                            out _, out _);
                    else
                        _opTable.Insert(
                            new[] { "OperationID", "Name", "Description", "Phases" },
                            new object[,] { { op.OperationID, op.Name, op.Description ?? "", phsCsv } });

                    foreach (var ph in op.Phases)
                    {
                        ph.Columns["PhaseID"] = ph.PhaseID;
                        ph.Columns["Name"] = ph.Name;
                        if (dbPhaseIds.Contains(ph.PhaseID))
                            SavePhaseUpdate(ph);
                        else
                            SavePhaseInsert(ph);
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
                        _store.Query($"DELETE FROM {_phaseTableName} WHERE PhaseID={id}", out _, out _);

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

    private void SavePhaseUpdate(PhaseNode ph)
    {
        if (ph.Columns.Count == 0)
        {
            _store.Query($"UPDATE {_phaseTableName} SET Name='{EscapeSql(ph.Name)}' WHERE PhaseID={ph.PhaseID}", out _, out _);
            return;
        }
        var setClauses = new List<string>();
        foreach (var kv in ph.Columns)
        {
            if (string.Equals(kv.Key, "PhaseID", StringComparison.OrdinalIgnoreCase)) continue;
            string val = kv.Value == null || kv.Value == DBNull.Value ? "" : kv.Value.ToString();
            setClauses.Add($"{kv.Key}='{EscapeSql(val)}'");
        }
        if (setClauses.Count == 0) return;
        _store.Query($"UPDATE {_phaseTableName} SET {string.Join(", ", setClauses)} WHERE PhaseID={ph.PhaseID}", out _, out _);
    }

    private void SavePhaseInsert(PhaseNode ph)
    {
        var colNames = GetPhaseColumnNames();
        if (colNames.Count == 0)
        {
            _phaseTable.Insert(new[] { "PhaseID", "Name" }, new object[,] { { ph.PhaseID, ph.Name } });
            return;
        }
        var values = new List<object>();
        foreach (string col in colNames)
        {
            if (ph.Columns.TryGetValue(col, out object val))
                values.Add(val == null || val == DBNull.Value ? "" : val);
            else if (string.Equals(col, "PhaseID", StringComparison.OrdinalIgnoreCase))
                values.Add(ph.PhaseID);
            else if (string.Equals(col, "Name", StringComparison.OrdinalIgnoreCase))
                values.Add(ph.Name);
            else
                values.Add("");
        }
        var row = new object[1, colNames.Count];
        for (int i = 0; i < values.Count; i++) row[0, i] = values[i];
        _phaseTable.Insert(colNames.ToArray(), row);
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
