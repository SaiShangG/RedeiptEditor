#region Using directives
using System;
using System.Collections.Generic;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
#endregion

/// <summary>从配方数据库读取全部数据并保存到本地 dict 树（Receipt → Operation → Phase）。</summary>
public class RecipeDatabaseTreeLoader : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseTreeLoader";
    private const bool EnableLog = true;

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
        public List<OperationNode> Operations = new List<OperationNode>();
    }

    public class OperationNode
    {
        public int OperationID;
        public string Name;
        public string PhasesCsv;
        public List<PhaseNode> Phases = new List<PhaseNode>();
    }

    public class PhaseNode
    {
        public int PhaseID;
        public string Name;
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
        if (_store == null || string.IsNullOrEmpty(_receiptTableName))
        {
            if (EnableLog) Log.Warning(LogCategory, "数据库未就绪，跳过加载");
            return;
        }

        try
        {
            _store.Query($"SELECT ReceiptID, Name, Sequence, Operations, Description FROM {_receiptTableName} ORDER BY Sequence", out _, out object[,] rRows);
            if (rRows == null || rRows.GetLength(0) == 0)
            {
                if (EnableLog) Log.Info(LogCategory, "Receipt 表无数据");
                return;
            }

            var phaseColumns = GetPhaseColumnNames();
            int rCount = rRows.GetLength(0);

            for (int i = 0; i < rCount; i++)
            {
                int receiptId = CellToInt(rRows[i, 0]);
                string name = CellToString(rRows[i, 1]);
                int seq = CellToInt(rRows[i, 2]);
                string operationsCsv = CellToString(rRows[i, 3]);
                string description = CellToString(rRows[i, 4]);

                var rNode = new ReceiptNode
                {
                    ReceiptID = receiptId,
                    Name = name,
                    Sequence = seq,
                    OperationsCsv = operationsCsv ?? "",
                    Description = description ?? ""
                };
                Tree.Add(rNode);
                ReceiptById[receiptId] = rNode;

                var opIdList = ParseIdList(rNode.OperationsCsv);
                if (opIdList.Count > 0 && _opTable != null && !string.IsNullOrEmpty(_opTableName))
                {
                    string inClause = string.Join(",", opIdList);
                    _store.Query($"SELECT OperationID, Name, Phases FROM {_opTableName} WHERE OperationID IN ({inClause})", out _, out object[,] oRows);
                    var opById = BuildOpDict(oRows);

                    foreach (int oId in opIdList)
                    {
                        if (!opById.TryGetValue(oId, out var opPair)) continue;
                        var oNode = new OperationNode { OperationID = oId, Name = opPair.Name, PhasesCsv = opPair.Phases ?? "" };
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

    private static Dictionary<int, (string Name, string Phases)> BuildOpDict(object[,] oRows)
    {
        var d = new Dictionary<int, (string, string)>();
        if (oRows == null) return d;
        for (int r = 0; r < oRows.GetLength(0); r++)
        {
            int id = CellToInt(oRows[r, 0]);
            d[id] = (CellToString(oRows[r, 1]), CellToString(oRows[r, 2]));
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
}
