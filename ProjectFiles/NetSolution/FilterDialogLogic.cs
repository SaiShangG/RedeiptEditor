#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

/// <summary>
/// 筛选对话框：从模型读取维度与条件，<see cref="ApplyFilter"/> 带 Store 名与 SQL 表名（Audit 用）。
/// </summary>
public class FilterDialogLogic : BaseNetLogic
{
    private const string LogCategory = "FilterDialogLogic";

    private const string PathFilterDataType = "Model/UIData/FilterData/FilterData/FilterType";
    private const string PathFilterDataInnerObject = "Model/UIData/FilterData/FilterData";
    private const string PathFilterContext = "Model/UIData/FilterData/FilterContext";
    private const string PathFilterSceneType = "Model/UIData/FilterData/FilterContext/FilterType";
    private const string PathSearchByCategoryData = "Model/UIData/FilterData/FilterConditions/SearchByCategoryData";
    private const string PathFilterConditions = "Model/UIData/FilterData/FilterConditions";
    private const string PathAuditPanel = "UI/Panels/AuditPanel";

    private const string VarSearchedString = "SearchedString";
    private const string VarDateFrom = "DateFrom";
    private const string VarDateTo = "DateTo";
    private const string VarFilterScene = "FilterScene";
    private const string VarFilterDimension = "FilterDimension";

    private const int FilterCategoryCapacity = 10;

    public override void Start()
    {
    }

    public override void Stop()
    {
    }

    /// <summary>Audit：设置 DataGrid Store（<paramref name="databaseName"/>）与 SQL 表名（<paramref name="tableName"/>）；Batch/Recipe 忽略。</summary>
    [ExportMethod]
    public void ApplyFilter(string databaseName, string tableName)
    {
        try
        {
            var project = Project.Current;
            if (project == null)
            {
                Log.Warning(LogCategory, "ApplyFilter: Project.Current is null.");
                return;
            }

            ApplyFilterCore(project, databaseName ?? "", tableName ?? "");
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"ApplyFilter: {ex.Message}");
        }
        finally
        {
            CloseHostDialog();
        }
    }

    private void ApplyFilterCore(IUANode project, string filterStoreName, string filterTableName)
    {
        IUANode root = ResolveProjectRootForUi(project, LogicObject);
        if (root == null)
        {
            Log.Warning(LogCategory, "ApplyFilterCore: 无法解析项目根节点。");
            return;
        }

        var fc = root.GetObject(PathFilterConditions) as IUAObject;

        int scene = ReadFilterSceneFromModel(root, fc);
        if (scene < 0)
        {
            Log.Warning(LogCategory,
                "ApplyFilterCore: 无法解析场景。请绑定 Model/UIData/FilterData/FilterContext/FilterType，或在 FilterConditions 增加 String 变量 FilterScene（Recipe/Batch/Audit 或 0/1/2）。");
            return;
        }

        string dimension = ReadFilterDimension(root, fc);
        string nameText = ResolveSearchedString(fc);
        DateTime? dateFrom = ReadNullableDateTimeFromObject(fc, VarDateFrom);
        DateTime? dateTo = ReadNullableDateTimeFromObject(fc, VarDateTo);

        switch (scene)
        {
            case 1:
                ApplyBatch(dimension, nameText, root, dateFrom, dateTo);
                break;
            case 0:
                ApplyRecipe(dimension, nameText, root, dateFrom, dateTo);
                break;
            case 2:
                ApplyAudit(dimension, nameText, root, filterStoreName, filterTableName, dateFrom, dateTo);
                break;
            default:
                Log.Warning(LogCategory, $"ApplyFilterCore: unsupported scene value {scene}.");
                break;
        }
    }

    private static void ApplyBatch(string dimension, string nameText, IUANode project, DateTime? dateFrom, DateTime? dateTo)
    {
        var batchLogic = BatchEditorLogic.Instance;
        if (batchLogic == null)
        {
            Log.Warning(LogCategory, "ApplyBatch: BatchEditorLogic.Instance is null.");
            return;
        }

        if (string.Equals(dimension, "Name", StringComparison.OrdinalIgnoreCase))
        {
            batchLogic.RefreshBatchGridBySearchText(nameText);
            return;
        }

        if (string.Equals(dimension, "State", StringComparison.OrdinalIgnoreCase))
        {
            batchLogic.RefreshBatchGridByStateLabels(BuildCommaSeparatedCheckedLabels(project));
            return;
        }

        if (string.Equals(dimension, "DateRange", StringComparison.OrdinalIgnoreCase))
        {
            batchLogic.RefreshBatchGridByCreatedDateRange(dateFrom, dateTo);
            return;
        }

        batchLogic.RefreshBatchGridBySearchText(nameText);
    }

    private static void ApplyRecipe(string dimension, string nameText, IUANode project, DateTime? dateFrom, DateTime? dateTo)
    {
        var tree = GenerateTreeList.Instance;
        if (tree == null)
        {
            Log.Warning(LogCategory, "ApplyRecipe: GenerateTreeList.Instance is null.");
            return;
        }

        if (string.Equals(dimension, "DateRange", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info(LogCategory, "Recipe date range filter: not implemented for tree list.");
            return;
        }

        if (string.Equals(dimension, "Name", StringComparison.OrdinalIgnoreCase))
        {
            tree.RefreshTreeListBySearchText(nameText);
            return;
        }

        if (string.Equals(dimension, "State", StringComparison.OrdinalIgnoreCase))
        {
            bool d0 = ReadBoolArrayIndex(project, 0);
            bool d1 = ReadBoolArrayIndex(project, 1);
            bool d2 = ReadBoolArrayIndex(project, 2);
            tree.ApplyFilterDialogRecipe(nameText, d0, d1, d2);
            return;
        }

        tree.RefreshTreeListBySearchText(nameText);
    }

    private static void ApplyAudit(string dimension, string nameText, IUANode project, string filterStoreName, string filterTableName, DateTime? dateFrom, DateTime? dateTo)
    {
        IUAObject grid = FindAuditDataGrid(project);
        IUAVariable queryVar = TryGetQueryVariable(grid);
        if (queryVar == null)
        {
            Log.Warning(LogCategory,
                "ApplyAudit: Audit DataGrid Query not found（已尝试 UI/Panels/AuditPanel/DataGrid2 与 Query 含 BatchRecipeAuditTable 的 DataGrid）。");
            return;
        }

        string sqlTable = ResolveAuditSqlTableName(filterTableName);
        var typeData = ResolveSearchByCategoryDataObject(project);
        int n = GetActiveCategorySlotCount(project);
        if (n <= 0) n = FilterCategoryCapacity;
        string[] labels = ReadStringArray(typeData?.GetVariable("TypeLabels"), FilterCategoryCapacity);
        bool[] checks = ReadBoolArray(typeData?.GetVariable("TypeChecked"), FilterCategoryCapacity);

        string sql;
        if (string.Equals(dimension, "State", StringComparison.OrdinalIgnoreCase))
            sql = BuildAuditUserQuery(nameText, labels, checks, n, sqlTable);
        else if (string.Equals(dimension, "Name", StringComparison.OrdinalIgnoreCase))
            sql = BuildAuditActionQuery(nameText, sqlTable);
        else if (string.Equals(dimension, "DateRange", StringComparison.OrdinalIgnoreCase))
            sql = BuildAuditDateRangeQuery(sqlTable, dateFrom, dateTo);
        else
            sql = BuildAuditActionQuery(nameText, sqlTable);

        NodeId storeId = TryResolveDataStoreNodeId(project, filterStoreName);
        if (!storeId.IsEmpty)
        {
            IUAVariable modelVar = TryGetModelVariable(grid);
            if (modelVar != null)
            {
                try
                {
                    modelVar.Value = storeId;
                }
                catch (Exception ex)
                {
                    Log.Warning(LogCategory, $"ApplyAudit: set DataGrid Model: {ex.Message}");
                }
            }
        }

        queryVar.Value = sql;
    }

    private static string ResolveAuditSqlTableName(string requestedTable)
    {
        string safe = SanitizeSqlTableIdentifier(requestedTable);
        return string.IsNullOrEmpty(safe) ? "BatchRecipeAuditTable" : safe;
    }

    private static string SanitizeSqlTableIdentifier(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        string t = s.Trim();
        if (t.Length == 0)
            return null;
        char c0 = t[0];
        if (!char.IsLetter(c0) && c0 != '_')
            return null;
        for (int i = 1; i < t.Length; i++)
        {
            char c = t[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
                return null;
        }
        return t;
    }

    private static NodeId TryResolveDataStoreNodeId(IUANode project, string storeBrowseNameOrPath)
    {
        if (project == null || string.IsNullOrWhiteSpace(storeBrowseNameOrPath))
            return NodeId.Empty;
        string key = storeBrowseNameOrPath.Trim();
        var ds = project.GetObject("DataStores") as IUAObject;
        if (ds != null)
        {
            var byName = ds.GetObject(key) as IUAObject;
            if (byName != null)
                return byName.NodeId;
        }

        var byPath = project.GetObject(key) as IUAObject;
        if (byPath != null && key.IndexOf('/') >= 0)
            return byPath.NodeId;
        return NodeId.Empty;
    }

    private static string BuildAuditUserQuery(string text, IReadOnlyList<string> labels, IReadOnlyList<bool> checks, int maxCategorySlots, string sqlTableName)
    {
        var ors = new List<string>();
        string t = text?.Trim() ?? "";
        if (t.Length > 0)
            ors.Add($"UserName LIKE '%{EscapeSqlLike(t)}%' ESCAPE '\\'");

        int limit = Math.Min(maxCategorySlots, Math.Min(labels.Count, checks.Count));
        for (int i = 0; i < limit; i++)
        {
            if (!checks[i]) continue;
            string lab = labels[i]?.Trim() ?? "";
            if (lab.Length == 0) continue;
            ors.Add($"UserName LIKE '%{EscapeSqlLike(lab)}%' ESCAPE '\\'");
        }

        if (ors.Count == 0)
            return "SELECT * FROM " + sqlTableName;
        return "SELECT * FROM " + sqlTableName + " WHERE (" + string.Join(" OR ", ors) + ")";
    }

    private static string BuildAuditActionQuery(string text, string sqlTableName)
    {
        string t = text?.Trim() ?? "";
        if (t.Length == 0)
            return "SELECT * FROM " + sqlTableName;
        string e = EscapeSqlLike(t);
        return "SELECT * FROM " + sqlTableName + " WHERE (Action LIKE '%" + e + "%' ESCAPE '\\' OR EventItem LIKE '%" + e + "%' ESCAPE '\\')";
    }

    private static string BuildAuditDateRangeQuery(string sqlTableName, DateTime? dateFrom, DateTime? dateTo)
    {
        bool useFrom = dateFrom.HasValue && dateFrom.Value != DateTime.MinValue;
        bool useTo = dateTo.HasValue && dateTo.Value != DateTime.MinValue;
        if (!useFrom && !useTo)
            return "SELECT * FROM " + sqlTableName;

        var parts = new List<string>();
        if (useFrom)
            parts.Add("\"DateTime\" >= '" + EscapeSqlStringLiteral(FormatSqlDateTimeUtc(dateFrom.Value)) + "'");
        if (useTo)
            parts.Add("\"DateTime\" <= '" + EscapeSqlStringLiteral(FormatSqlDateTimeUtc(dateTo.Value)) + "'");
        return "SELECT * FROM " + sqlTableName + " WHERE (" + string.Join(" AND ", parts) + ")";
    }

    private static string FormatSqlDateTimeUtc(DateTime dt)
    {
        return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string EscapeSqlLike(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("'", "''");
    }

    private static string EscapeSqlStringLiteral(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("'", "''");
    }

    private static IUAObject FindAuditDataGrid(IUANode projectRoot)
    {
        if (projectRoot == null)
            return null;

        var panel = projectRoot.GetObject(PathAuditPanel) as IUAObject;
        if (panel != null)
        {
            var byPanel = FindDescendantObjectByBrowseTail(panel, "DataGrid2");
            if (byPanel != null && TryGetQueryVariable(byPanel) != null)
                return byPanel;
        }

        var bySql = FindDataGridWithAuditTableQuery(projectRoot);
        if (bySql != null)
            return bySql;

        if (panel != null)
            return FindDescendantObjectByBrowseTail(panel, "DataGrid2");
        return null;
    }

    private static IUAObject FindDataGridWithAuditTableQuery(IUANode root)
    {
        if (root == null)
            return null;
        if (root is IUAObject obj)
        {
            var q = TryGetQueryVariable(obj);
            if (q?.Value?.Value is string qs &&
                qs.IndexOf("BatchRecipeAuditTable", StringComparison.OrdinalIgnoreCase) >= 0)
                return obj;
        }
        foreach (var child in root.Children)
        {
            var found = FindDataGridWithAuditTableQuery(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private static IUAVariable TryGetQueryVariable(IUAObject grid)
    {
        if (grid == null)
            return null;
        var v = grid.GetVariable("Query");
        if (v != null)
            return v;
        return grid.GetObject("Query") as IUAVariable;
    }

    private static IUAVariable TryGetModelVariable(IUAObject grid)
    {
        if (grid == null)
            return null;
        var v = grid.GetVariable("Model");
        if (v != null)
            return v;
        return grid.GetObject("Model") as IUAVariable;
    }

    /// <summary>使 <c>UI/Panels/...</c> 相对 RedeiptEditor 根解析；<c>Project.Current</c> 在部分上下文中可能不是该根。</summary>
    private static IUANode ResolveProjectRootForUi(IUANode projectCurrent, IUANode logicObject)
    {
        if (projectCurrent != null)
        {
            if (projectCurrent.GetObject(PathAuditPanel) != null)
                return projectCurrent;
            var namedChild = projectCurrent.GetObject("RedeiptEditor") as IUANode;
            if (namedChild != null && namedChild.GetObject(PathAuditPanel) != null)
                return namedChild;
        }

        for (IUANode n = logicObject; n != null; n = n.Owner)
        {
            if (!BrowseNameEqualsRedeiptEditor(n))
                continue;
            if (n.GetObject(PathAuditPanel) != null)
                return n;
        }

        for (IUANode n = logicObject; n != null; n = n.Owner)
        {
            if (BrowseNameEqualsRedeiptEditor(n))
                return n;
        }

        return projectCurrent;
    }

    private static bool BrowseNameEqualsRedeiptEditor(IUANode node)
    {
        if (node?.BrowseName == null)
            return false;
        string bn = node.BrowseName.ToString() ?? "";
        if (string.Equals(bn, "RedeiptEditor", StringComparison.OrdinalIgnoreCase))
            return true;
        int c = bn.LastIndexOf(':');
        if (c >= 0 && c < bn.Length - 1)
            return string.Equals(bn.Substring(c + 1), "RedeiptEditor", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static IUAObject FindDescendantObjectByBrowseTail(IUANode root, string tail)
    {
        if (root == null || string.IsNullOrEmpty(tail)) return null;
        if (root is IUAObject obj && string.Equals(BrowseNameTail(root), tail, StringComparison.OrdinalIgnoreCase))
            return obj;
        foreach (var ch in root.Children)
        {
            var found = FindDescendantObjectByBrowseTail(ch, tail);
            if (found != null) return found;
        }
        return null;
    }

    private static IUAObject ResolveSearchByCategoryDataObject(IUANode project)
    {
        if (project == null)
            return null;

        foreach (string p in new[] { PathSearchByCategoryData, "Model/UIData/FilterData/SearchByCategoryData" })
        {
            var o = project.GetObject(p) as IUAObject;
            if (o != null)
                return o;
        }

        var model = project.GetObject("Model") as IUAObject;
        if (model != null)
        {
            var found = FindDescendantObjectByBrowseTail(model, "SearchByCategoryData");
            if (found != null)
                return found;
        }

        return FindDescendantObjectByBrowseTail(project, "SearchByCategoryData");
    }

    private void CloseHostDialog()
    {
        try
        {
            for (IUANode n = LogicObject?.Owner; n != null; n = n.Owner)
            {
                if (n is Dialog dlg)
                {
                    dlg.Close();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"CloseHostDialog: {ex.Message}");
        }
    }

    private static string BrowseNameTail(IUANode node)
    {
        string bn = node?.BrowseName?.ToString() ?? "";
        int c = bn.LastIndexOf(':');
        return c >= 0 && c < bn.Length - 1 ? bn.Substring(c + 1) : bn;
    }

    private static IUAVariable FindDirectChildVariableByBrowseTail(IUAObject root, string tail)
    {
        if (root == null || string.IsNullOrEmpty(tail))
            return null;
        foreach (var ch in root.Children)
        {
            if (ch is IUAVariable vv && string.Equals(BrowseNameTail(ch), tail, StringComparison.OrdinalIgnoreCase))
                return vv;
        }
        return null;
    }

    private static int ReadFilterSceneFromModel(IUANode project, IUAObject filterConditions)
    {
        if (project == null)
            return -1;

        int s = TryParseFilterSceneVariable(project.GetObject(PathFilterSceneType) as IUAVariable);
        if (s >= 0)
            return s;

        var ctx = project.GetObject(PathFilterContext) as IUAObject;
        if (ctx != null)
        {
            s = TryParseFilterSceneVariable(ctx.GetVariable("FilterType"));
            if (s >= 0)
                return s;

            s = TryParseFilterSceneVariable(FindDirectChildVariableByBrowseTail(ctx, "FilterType"));
            if (s >= 0)
                return s;
        }

        if (filterConditions != null)
        {
            s = ParseFilterSceneString(ReadStringVariableFromObject(filterConditions, VarFilterScene));
            if (s >= 0)
                return s;
        }

        return -1;
    }

    private static int TryParseFilterSceneVariable(IUAVariable v)
    {
        if (v == null)
            return -1;
        try
        {
            if (v.Value == null || v.Value.Value == null)
                return -1;
        }
        catch
        {
            return -1;
        }

        return CoerceValueToSceneIndex(v.Value.Value);
    }

    private static int CoerceValueToSceneIndex(object val)
    {
        if (val == null)
            return -1;

        if (val is int i)
            return NormalizeSceneIndex(i);
        if (val is long l)
            return NormalizeSceneIndex((int)l);
        if (val is uint u)
            return NormalizeSceneIndex((int)u);
        if (val is byte b)
            return NormalizeSceneIndex(b);
        if (val is short sh)
            return NormalizeSceneIndex(sh);
        if (val is ushort us)
            return NormalizeSceneIndex(us);

        if (val is string str)
            return ParseFilterSceneString(str);

        try
        {
            return NormalizeSceneIndex(Convert.ToInt32(val, CultureInfo.InvariantCulture));
        }
        catch
        {
        }

        string t = val.ToString()?.Trim();
        return ParseFilterSceneString(t);
    }

    private static int NormalizeSceneIndex(int n)
    {
        if (n >= 0 && n <= 2)
            return n;
        return -1;
    }

    private static int ParseFilterSceneString(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return -1;
        s = s.Trim();
        if (string.Equals(s, "Recipe", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(s, "Batch", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(s, "Audit", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
            return NormalizeSceneIndex(k);

        if (string.Equals(s, "Value0", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(s, "Value1", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(s, "Value2", StringComparison.OrdinalIgnoreCase))
            return 2;
        return -1;
    }

    private static string ReadFilterDimension(IUANode project, IUAObject filterConditions)
    {
        IUAVariable v = ResolveFilterDimensionVariable(project);
        string dim = ParseFilterDimensionUaValue(v);
        if (dim != null)
            return NormalizeFilterDimension(dim);

        if (filterConditions != null)
        {
            string s = ReadStringVariableFromObject(filterConditions, VarFilterDimension);
            if (!string.IsNullOrWhiteSpace(s))
                return NormalizeFilterDimension(s.Trim());
        }

        return "State";
    }

    private static IUAVariable ResolveFilterDimensionVariable(IUANode project)
    {
        if (project == null)
            return null;

        var direct = project.GetObject(PathFilterDataType) as IUAVariable;
        if (direct != null)
            return direct;

        var inner = project.GetObject(PathFilterDataInnerObject) as IUAObject;
        if (inner == null)
            return null;

        var byGet = inner.GetVariable("FilterType");
        if (byGet != null)
            return byGet;

        return FindDirectChildVariableByBrowseTail(inner, "FilterType");
    }

    private static string ParseFilterDimensionUaValue(IUAVariable v)
    {
        if (v == null)
            return null;
        object val;
        try
        {
            if (v.Value == null || v.Value.Value == null)
                return null;
            val = v.Value.Value;
        }
        catch
        {
            return null;
        }

        if (val is LocalizedText lt)
            return (lt.Text ?? "").Trim();

        if (val is string s)
        {
            s = s.Trim();
            if (s.Length > 0)
                return s;
        }

        try
        {
            int i = Convert.ToInt32(val, CultureInfo.InvariantCulture);
            if (i == 1)
                return "Name";
            if (i == 2)
                return "DateRange";
            if (i == 0)
                return "State";
        }
        catch
        {
        }

        return val.ToString()?.Trim() ?? "";
    }

    /// <summary>将 UI 文案（如 Batch Name、Date Range）归一为 Name/State/DateRange。</summary>
    private static string NormalizeFilterDimension(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "State";
        string t = raw.Trim();

        if (t.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(t, "DateRange", StringComparison.OrdinalIgnoreCase))
            return "DateRange";

        if (t.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Name";

        return "State";
    }

    private static string ResolveSearchedString(IUAObject filterConditions)
    {
        string s = ReadStringVariableFromObject(filterConditions, VarSearchedString);
        if (string.IsNullOrWhiteSpace(s))
            return "";
        s = s.Trim();
        if (string.Equals(s, "0", StringComparison.Ordinal))
            return "";
        return s;
    }

    private static DateTime? ReadNullableDateTimeFromObject(IUAObject obj, string variableName)
    {
        if (obj == null || string.IsNullOrEmpty(variableName))
            return null;
        IUAVariable v = obj.GetVariable(variableName);
        if (v?.Value?.Value == null)
            return null;
        object val = v.Value.Value;
        if (val is DateTime dt)
        {
            if (dt == DateTime.MinValue)
                return null;
            return dt;
        }

        try
        {
            DateTime parsed = Convert.ToDateTime(val, CultureInfo.InvariantCulture);
            if (parsed == DateTime.MinValue)
                return null;
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadStringVariableFromObject(IUAObject obj, string variableName)
    {
        if (obj == null || string.IsNullOrEmpty(variableName))
            return "";
        IUAVariable v = obj.GetVariable(variableName);
        if (v?.Value?.Value == null)
            return "";
        object val = v.Value.Value;
        if (val is LocalizedText lt)
            return lt.Text?.Trim() ?? "";
        return val.ToString()?.Trim() ?? "";
    }

    private static string BuildCommaSeparatedCheckedLabels(IUANode project)
    {
        var typeData = ResolveSearchByCategoryDataObject(project);
        int n = GetActiveCategorySlotCount(project);
        if (n <= 0) n = FilterCategoryCapacity;
        string[] labels = ReadStringArray(typeData?.GetVariable("TypeLabels"), FilterCategoryCapacity);
        bool[] checks = ReadBoolArray(typeData?.GetVariable("TypeChecked"), FilterCategoryCapacity);
        var picked = new List<string>();
        int limit = Math.Min(n, Math.Min(labels.Length, checks.Length));
        for (int i = 0; i < limit; i++)
        {
            if (!checks[i]) continue;
            string lab = labels[i]?.Trim() ?? "";
            if (lab.Length > 0)
                picked.Add(lab);
        }
        return string.Join(",", picked);
    }

    private static bool ReadBoolArrayIndex(IUANode project, int index)
    {
        var typeData = ResolveSearchByCategoryDataObject(project);
        var arr = ReadBoolArray(typeData?.GetVariable("TypeChecked"), FilterCategoryCapacity);
        return index >= 0 && index < arr.Length && arr[index];
    }

    private static int GetActiveCategorySlotCount(IUANode project)
    {
        var typeData = ResolveSearchByCategoryDataObject(project);
        int meaningful = CountMeaningfulCategoryPrefix(typeData?.GetVariable("TypeLabels"));
        if (meaningful > 0)
            return Math.Min(FilterCategoryCapacity, meaningful);
        int len = GetStringArrayPhysicalLength(typeData?.GetVariable("TypeLabels"));
        if (len > 0)
            return Math.Min(FilterCategoryCapacity, len);
        return 0;
    }

    private static int GetStringArrayPhysicalLength(IUAVariable v)
    {
        if (v?.Value?.Value == null)
            return 0;
        object val = v.Value.Value;
        if (val is string[] sa)
            return sa.Length;
        if (val is object[] oa)
            return oa.Length;
        if (val is Array arr && arr.Rank == 1)
            return arr.Length;
        return 0;
    }

    private static int CountMeaningfulCategoryPrefix(IUAVariable labelsVar)
    {
        string[] a = ReadStringArray(labelsVar, FilterCategoryCapacity);
        int last = -1;
        for (int i = 0; i < a.Length; i++)
        {
            string s = a[i]?.Trim() ?? "";
            if (s.Length > 0 && s != "0")
                last = i;
        }
        return last < 0 ? 0 : last + 1;
    }

    private static string[] ReadStringArray(IUAVariable v, int len)
    {
        var a = new string[len];
        if (v?.Value?.Value == null) return a;
        try
        {
            var ua = v.Value;
            if (ua.Value is string[] sa)
            {
                for (int i = 0; i < len && i < sa.Length; i++)
                    a[i] = sa[i] ?? "";
                return a;
            }
            if (ua.Value is object[] oa)
            {
                for (int i = 0; i < len && i < oa.Length; i++)
                    a[i] = oa[i]?.ToString() ?? "";
            }
        }
        catch { }
        return a;
    }

    private static bool[] ReadBoolArray(IUAVariable v, int len)
    {
        var a = new bool[len];
        if (v?.Value?.Value == null) return a;
        try
        {
            var ua = v.Value;
            if (ua.Value is bool[] ba)
            {
                for (int i = 0; i < len && i < ba.Length; i++)
                    a[i] = ba[i];
                return a;
            }
            if (ua.Value is object[] oa)
            {
                for (int i = 0; i < len && i < oa.Length; i++)
                {
                    if (oa[i] is bool)
                        a[i] = (bool)oa[i];
                }
            }
        }
        catch { }
        return a;
    }
}
