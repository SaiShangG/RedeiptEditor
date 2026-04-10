#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.RecipeX;
#endregion

public class BatchEditorLogic : BaseNetLogic
{
    public static BatchEditorLogic Instance { get; private set; }

    private IEventRegistration _uiSelectedRegistration;
    private IUAVariable _uiSelectedItemVariable;
    private uint _affinityId;

    /// <summary>与搜索框一致；插入/删除后按此条件重建 Query。</summary>
    private static string _lastBatchGridSearchFilter = "";

    /// <summary>与日期范围过滤一致；插入/删除后按此条件重建 Query。</summary>
    private static DateTime? _lastBatchGridDateFrom;

    private static DateTime? _lastBatchGridDateTo;

    private const string AuditEvtButton = "Evt_ButtonClick";

    private static string ResolveBatchAuditUserBrowseName(string sessionFallback = null)
    {
        try
        {
            var u = Instance?.Session?.User;
            if (u != null && !string.IsNullOrEmpty(u.BrowseName))
                return u.BrowseName.Trim();
        }
        catch { }
        string fromManager = RecipeDatabaseManager.TryGetInstanceUserBrowseName();
        if (!string.IsNullOrEmpty(fromManager)) return fromManager;
        if (!string.IsNullOrWhiteSpace(sessionFallback)) return sessionFallback.Trim();
        return "";
    }

    private void AuditBatch(string actionTemplate, Dictionary<string, string> vars, string oldValue = "", string newValue = "", string status = "ok")
    {
        string user = ResolveBatchAuditUserBrowseName();
        RecipeAuditLogHelper.Append(LogicObject, user, AuditEvtButton, actionTemplate, vars, oldValue ?? "", newValue ?? "", status ?? "ok");
    }

    private static void AuditBatchStatic(string actionTemplate, Dictionary<string, string> vars, string userBrowseName, string oldValue = "", string newValue = "", string status = "ok")
    {
        IUANode node = Instance?.LogicObject;
        RecipeAuditLogHelper.Append(node, userBrowseName ?? "", AuditEvtButton, actionTemplate, vars, oldValue ?? "", newValue ?? "", status ?? "ok");
    }

    public override void Start()
    {
        Instance = this;
        RefreshRecipeListFromStore();
        SetupBatchGridSelectionSync();
    }

    public override void Stop()
    {
        if (_affinityId != 0)
        {
            using (LogicObject.Context.TerminateDispatchOnStop(_affinityId))
            {
                _uiSelectedRegistration?.Dispose();
                _uiSelectedRegistration = null;
            }
            _affinityId = 0;
        }
        else
        {
            _uiSelectedRegistration?.Dispose();
            _uiSelectedRegistration = null;
        }
        _uiSelectedItemVariable = null;
        if (Instance == this) Instance = null;
    }

    private void SetupBatchGridSelectionSync()
    {
        try
        {
            IUANode projectRoot = Project.Current;
            if (projectRoot == null)
            {
                Log.Warning("BatchEditorLogic", "Selection sync: Project.Current is null.");
                return;
            }

            IUANode panelNode = projectRoot.GetObject("UI/Panels/BatchEditor") as IUANode;
            if (panelNode == null)
            {
                var ui = projectRoot.GetObject("UI") as IUANode;
                var panels = ui?.GetObject("Panels") as IUANode;
                panelNode = panels?.GetObject("BatchEditor") as IUANode;
            }

            IUANode searchRoot = panelNode ?? projectRoot;
            IUAObject grid = GetBatchEditorDataGridFromPanelRoot(searchRoot);
            if (grid == null)
            {
                Log.Warning("BatchEditorLogic", "BatchEditor Batches 表格未找到（DataGrid2 / Query 含 Batches），已禁用选中同步。");
                return;
            }

            _uiSelectedItemVariable = grid.GetVariable("UISelectedItem");
            if (_uiSelectedItemVariable == null)
            {
                Log.Warning("BatchEditorLogic", "DataGrid2.UISelectedItem not found.");
                return;
            }

            _affinityId = LogicObject.Context.AssignAffinityId();
            var observer = new CallbackVariableChangeObserver(OnUiSelectedItemChanged);
            _uiSelectedRegistration = _uiSelectedItemVariable.RegisterEventObserver(
                observer, EventType.VariableValueChanged, _affinityId);

            var current = _uiSelectedItemVariable.Value;
            if (current?.Value is NodeId nid && !nid.IsEmpty)
                ApplyRowToBatchEditorData(nid);
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"Selection sync setup failed: {ex.Message}");
        }
    }

    /// <summary>定位 BatchEditor 面板内的 Batches 表格（与选中同步共用）。</summary>
    private static IUAObject GetBatchEditorDataGridFromPanelRoot(IUANode panelOrSearchRoot)
    {
        if (panelOrSearchRoot == null) return null;
        return FindDescendantObjectByBrowseName(panelOrSearchRoot, "DataGrid2")
            ?? FindDataGridWithBatchesQuery(panelOrSearchRoot);
    }

    private static IUAObject GetBatchEditorDataGrid()
    {
        IUANode projectRoot = Project.Current;
        if (projectRoot == null) return null;

        IUANode panelNode = projectRoot.GetObject("UI/Panels/BatchEditor") as IUANode;
        if (panelNode == null)
        {
            
            var ui = projectRoot.GetObject("UI") as IUANode;
            var panels = ui?.GetObject("Panels") as IUANode;
            panelNode = panels?.GetObject("BatchEditor") as IUANode;
        }

        return GetBatchEditorDataGridFromPanelRoot(panelNode ?? projectRoot);
    }

    [ExportMethod]
    public void RefreshBatchGridBySearchText(string filterText)
    {
        try
        {
            IUAObject grid = GetBatchEditorDataGrid();
            if (grid == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridBySearchText: DataGrid2 not found.");
                return;
            }

            IUAVariable queryVar = grid.GetVariable("Query");
            if (queryVar == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridBySearchText: Query variable not found.");
                return;
            }

            _lastBatchGridSearchFilter = filterText?.Trim() ?? "";
            _lastBatchGridDateFrom = null;
            _lastBatchGridDateTo = null;
            queryVar.Value = BuildBatchesNameFilterQuery(filterText);

            TryClearBatchDataGridSelection(grid);
            if (Instance == null)
                ClearBatchEditorFormFields();
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"RefreshBatchGridBySearchText failed: {ex.Message}");
        }
    }

    /// <summary>按 <c>Batches.State</c> 显示名多选过滤（逗号分隔）。空字符串表示不限制状态。</summary>
    [ExportMethod]
    public void RefreshBatchGridByStateLabels(string commaSeparatedDisplayNames)
    {
        try
        {
            IUAObject grid = GetBatchEditorDataGrid();
            if (grid == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridByStateLabels: DataGrid2 not found.");
                return;
            }

            IUAVariable queryVar = grid.GetVariable("Query");
            if (queryVar == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridByStateLabels: Query variable not found.");
                return;
            }

            var parts = (commaSeparatedDisplayNames ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string sql;
            if (parts.Count == 0)
                sql = "SELECT * FROM Batches";
            else
            {
                var inList = string.Join(",", parts.Select(p => "'" + EscapeSqlStringForInClause(p) + "'"));
                sql = "SELECT * FROM Batches WHERE State IN (" + inList + ")";
            }

            _lastBatchGridSearchFilter = "";
            _lastBatchGridDateFrom = null;
            _lastBatchGridDateTo = null;
            queryVar.Value = sql;

            TryClearBatchDataGridSelection(grid);
            if (Instance == null)
                ClearBatchEditorFormFields();
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"RefreshBatchGridByStateLabels failed: {ex.Message}");
        }
    }

    /// <summary>按 <c>Batches.CreatedDateTime</c> 过滤；与 <c>FilterConditions.DateFrom/DateTo</c> 对应。</summary>
    public void RefreshBatchGridByCreatedDateRange(DateTime? dateFrom, DateTime? dateTo)
    {
        try
        {
            IUAObject grid = GetBatchEditorDataGrid();
            if (grid == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridByCreatedDateRange: DataGrid2 not found.");
                return;
            }

            IUAVariable queryVar = grid.GetVariable("Query");
            if (queryVar == null)
            {
                Log.Warning("BatchEditorLogic", "RefreshBatchGridByCreatedDateRange: Query variable not found.");
                return;
            }

            _lastBatchGridSearchFilter = "";
            _lastBatchGridDateFrom = dateFrom;
            _lastBatchGridDateTo = dateTo;
            queryVar.Value = BuildBatchesDateRangeQuery(dateFrom, dateTo);

            TryClearBatchDataGridSelection(grid);
            if (Instance == null)
                ClearBatchEditorFormFields();
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"RefreshBatchGridByCreatedDateRange failed: {ex.Message}");
        }
    }

    private static string EscapeSqlStringForInClause(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("'", "''");
    }

    /// <param name="refreshNonce">非 null 时追加恒真谓词 <c>(t=t)</c>，使 SQL 文本变化以触发 DataGrid 重新查询（勿用块注释，Optix 解析器不支持）。</param>
    private static string BuildBatchesNameFilterQuery(string filterText, long? refreshNonce = null)
    {
        string term = filterText?.Trim() ?? "";
        string nonceTail = "";
        if (refreshNonce.HasValue)
        {
            long t = refreshNonce.Value;
            nonceTail = $" AND ({t}={t})";
        }

        if (term.Length == 0)
        {
            if (refreshNonce.HasValue)
            {
                long t = refreshNonce.Value;
                return $"SELECT * FROM Batches WHERE ({t}={t})";
            }
            return "SELECT * FROM Batches";
        }

        string pat = EscapeSqlLikePatternForClause(term);
        return $"SELECT * FROM Batches WHERE Name LIKE '%{pat}%' ESCAPE '\\'{nonceTail}";
    }

    private static string BuildBatchesDateRangeQuery(DateTime? dateFrom, DateTime? dateTo, long? refreshNonce = null)
    {
        bool useFrom = dateFrom.HasValue && dateFrom.Value != DateTime.MinValue;
        bool useTo = dateTo.HasValue && dateTo.Value != DateTime.MinValue;
        string nonceTail = "";
        if (refreshNonce.HasValue)
        {
            long t = refreshNonce.Value;
            nonceTail = $" AND ({t}={t})";
        }

        if (!useFrom && !useTo)
        {
            if (refreshNonce.HasValue)
            {
                long t = refreshNonce.Value;
                return $"SELECT * FROM Batches WHERE ({t}={t})";
            }
            return "SELECT * FROM Batches";
        }

        var parts = new List<string>();
        if (useFrom)
            parts.Add($"CreatedDateTime >= '{FormatSqlDateTime(dateFrom.Value)}'");
        if (useTo)
            parts.Add($"CreatedDateTime <= '{FormatSqlDateTime(dateTo.Value)}'");
        return "SELECT * FROM Batches WHERE " + string.Join(" AND ", parts) + nonceTail;
    }

    private static string FormatSqlDateTime(DateTime dt)
    {
        return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>插入/删除后刷新网格；保留当前搜索条件。不使用 SQL 注释语法。</summary>
    private static void RefreshBatchDataGridAfterStoreMutation()
    {
        try
        {
            IUAObject grid = GetBatchEditorDataGrid();
            if (grid == null)
                return;

            IUAVariable queryVar = grid.GetVariable("Query");
            if (queryVar == null)
                return;

            long tick = DateTime.UtcNow.Ticks;
            if (_lastBatchGridDateFrom.HasValue || _lastBatchGridDateTo.HasValue)
                queryVar.Value = BuildBatchesDateRangeQuery(_lastBatchGridDateFrom, _lastBatchGridDateTo, tick);
            else
                queryVar.Value = BuildBatchesNameFilterQuery(_lastBatchGridSearchFilter, tick);

            TryClearBatchDataGridSelection(grid);
            if (Instance == null)
                ClearBatchEditorFormFields();
        }
        catch (Exception ex)
        {
            Log.Warning("BatchEditorLogic", $"RefreshBatchDataGridAfterStoreMutation: {ex.Message}");
        }
    }

    /// <summary>
    /// DataGrid 的 <c>UISelectedItem</c> 在模型里常为 <c>AccessLevel: Read</c>，写入会报 permission denied；
    /// 应通过 <c>SelectedItem</c>（NodePointer）清空选中。
    /// </summary>
    private static void TryClearBatchDataGridSelection(IUAObject grid)
    {
        if (grid == null)
            return;
        IUAVariable sel = grid.GetVariable("SelectedItem");
        if (sel == null)
            return;
        try
        {
            sel.Value = NodeId.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning("BatchEditorLogic", $"TryClearBatchDataGridSelection: {ex.Message}");
        }
    }

    /// <summary>供 LIKE … ESCAPE '\' 使用：转义 \、%、_ 与 SQL 字符串中的 '。</summary>
    private static string EscapeSqlLikePatternForClause(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("'", "''");
    }

    private static bool BrowseNameEquals(IUANode node, string expected)
    {
        if (node?.BrowseName == null || expected == null) return false;
        string bn = node.BrowseName.ToString() ?? "";
        if (string.Equals(bn, expected, StringComparison.OrdinalIgnoreCase)) return true;
        int colon = bn.LastIndexOf(':');
        if (colon >= 0 && colon < bn.Length - 1)
        {
            string tail = bn.Substring(colon + 1);
            if (string.Equals(tail, expected, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IUAObject FindDescendantObjectByBrowseName(IUANode root, string browseName)
    {
        if (root == null) return null;
        if (BrowseNameEquals(root, browseName))
            return root as IUAObject;
        foreach (var child in root.Children)
        {
            var found = FindDescendantObjectByBrowseName(child, browseName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>项目中仅 BatchEditor 使用 SELECT * FROM Batches，可作为稳定定位方式。</summary>
    private static IUAObject FindDataGridWithBatchesQuery(IUANode root)
    {
        if (root == null) return null;
        if (root is IUAObject obj)
        {
            var q = obj.GetVariable("Query");
            if (q?.Value?.Value is string qs &&
                qs.IndexOf("FROM", StringComparison.OrdinalIgnoreCase) >= 0 &&
                qs.IndexOf("Batches", StringComparison.OrdinalIgnoreCase) >= 0)
                return obj;
        }
        foreach (var child in root.Children)
        {
            var found = FindDataGridWithBatchesQuery(child);
            if (found != null) return found;
        }
        return null;
    }

    private void OnUiSelectedItemChanged(
        IUAVariable variable,
        UAValue newValue,
        UAValue oldValue,
        ElementAccess elementAccess,
        ulong senderId)
    {
        if (newValue?.Value is not NodeId nid || nid.IsEmpty)
            ClearBatchEditorData();
        else
            ApplyRowToBatchEditorData(nid);
    }

    private static void ClearBatchEditorFormFields()
    {
        var editor = GetBatchEditorDataNode();
        if (editor == null) return;
        SetStringVariable(editor, "Name", "");
        SetStringVariable(editor, "Recipe", "");
        SetStringVariable(editor, "Comments", "");
    }

    private void ClearBatchEditorData()
    {
        ClearBatchEditorFormFields();
    }

    private void ApplyRowToBatchEditorData(NodeId rowNodeId)
    {
        var row = InformationModel.Get(rowNodeId) as IUAObject;
        var editor = GetBatchEditorDataNode();
        if (row == null || editor == null) return;

        string name = ReadRowString(row, "Name");
        string recipe = ReadRowString(row, "RecipeName");
        string comments = ReadRowString(row, "Comments");

        // DataGrid 行对象在切换选中时，空单元格对应的变量可能仍保留上一行的值；以 Batches 表为准刷新。
        if (!string.IsNullOrEmpty(name))
            TryLoadBatchRecipeAndCommentsFromDb(name, ref recipe, ref comments);

        SetStringVariable(editor, "Name", name);
        SetStringVariable(editor, "Recipe", recipe);
        SetStringVariable(editor, "Comments", comments);
    }

    private static void TryLoadBatchRecipeAndCommentsFromDb(string batchName, ref string recipe, ref string comments)
    {
        try
        {
            var store = Project.Current?.GetObject("DataStores")?.Get<Store>("ReceiptDB");
            if (store == null) return;

            store.Query(
                $"SELECT RecipeName, Comments FROM Batches WHERE Name='{EscapeSql(batchName)}'",
                out _, out object[,] rows);

            if (rows == null || rows.GetLength(0) == 0)
                return;

            recipe = NormalizeDbCell(rows[0, 0]);
            comments = NormalizeDbCell(rows[0, 1]);
        }
        catch
        {
            // 保留从行节点读取的值
        }
    }

    private static string NormalizeDbCell(object cell)
    {
        if (cell == null || cell == DBNull.Value) return "";
        if (cell is string s) return s;
        return cell.ToString() ?? "";
    }

    private static IUAObject GetBatchEditorDataNode()
        => Project.Current.GetObject("Model/UIData/BatchesEditorData/BatchEditorData");

    private static string ReadRowString(IUAObject row, string varName)
    {
        var v = row.GetVariable(varName);
        if (v?.Value == null) return "";
        var val = v.Value.Value;
        if (val == null) return "";
        if (val is LocalizedText lt) return lt.Text ?? "";
        return val.ToString() ?? "";
    }

    private static void SetStringVariable(IUAObject owner, string name, string value)
    {
        var v = owner.GetVariable(name);
        if (v != null)
            v.Value = value ?? "";
    }

    /// <summary>
    /// 从 <c>ReceiptDB.Receipts</c> 重建 <c>RecipeList</c> 子节点，供 Batch Editor 下拉使用。
    /// 在配方增删改并已 <c>Loader.Save()</c> 后由 <see cref="RecipeDatabaseManager"/> 调用；也可在 NetLogic 启动时调用。
    /// </summary>
    public static void RefreshRecipeListFromStore()
    {
        try
        {
            var project = Project.Current;
            if (project == null) return;

            var store = project.GetObject("DataStores")?.Get<Store>("ReceiptDB");
            if (store == null) return;

            var recipeList = project.GetObject("Model/UIData/RecipeList");
            if (recipeList == null) return;

            foreach (var child in recipeList.Children.ToList())
                child.Delete();

            store.Query("SELECT Name FROM Receipts", out _, out object[,] resultSet);
            if (resultSet == null) return;

            int rowCount = resultSet.GetLength(0);
            for (int i = 0; i < rowCount; i++)
            {
                string recipeName = resultSet[i, 0]?.ToString();
                if (string.IsNullOrEmpty(recipeName)) continue;
                try
                {
                    string safeName = recipeName.Replace(".", "_").Replace("/", "_").Replace(":", "_");
                    var variable = InformationModel.MakeVariable(safeName, OpcUa.DataTypes.String);
                    variable.DisplayName = new LocalizedText(recipeName);
                    variable.Value = recipeName;
                    recipeList.Add(variable);
                }
                catch
                {
                    var variable = InformationModel.MakeVariable("Recipe_" + i, OpcUa.DataTypes.String);
                    variable.DisplayName = new LocalizedText(recipeName);
                    variable.Value = recipeName;
                    recipeList.Add(variable);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"RefreshRecipeListFromStore: {ex.Message}");
        }
    }

    [ExportMethod]
    public void PopulateRecipeList()
    {
        RefreshRecipeListFromStore();
    }

    [ExportMethod]
    public void SaveBatchEdits()
    {
        try
        {
            var editor = GetBatchEditorDataNode();
            if (editor == null)
            {
                Log.Error("BatchEditorLogic", "BatchEditorData node not found.");
                return;
            }

            string batchName = editor.GetVariable("Name")?.Value?.Value as string ?? "";
            string recipeName = editor.GetVariable("Recipe")?.Value?.Value as string ?? "";
            string comments = editor.GetVariable("Comments")?.Value?.Value as string ?? "";

            if (string.IsNullOrWhiteSpace(batchName))
            {
                Log.Warning("BatchEditorLogic", "Save skipped: batch name is empty.");
                return;
            }

            var store = Project.Current.GetObject("DataStores").Get<Store>("ReceiptDB");
            if (store == null)
            {
                Log.Error("BatchEditorLogic", "Store ReceiptDB not found.");
                return;
            }

            string oldRecipe = "", oldComments = "", oldStatus = "";
            try
            {
                store.Query(
                    $"SELECT RecipeName, Comments, Status FROM Batches WHERE Name='{EscapeSql(batchName)}'",
                    out _, out object[,] oldRows);
                if (oldRows != null && oldRows.GetLength(0) >= 1)
                {
                    oldRecipe = NormalizeDbCell(oldRows[0, 0]);
                    oldComments = NormalizeDbCell(oldRows[0, 1]);
                    oldStatus = NormalizeDbCell(oldRows[0, 2]);
                }
            }
            catch { }

            string user = Session?.User?.BrowseName ?? "Unknown";
            string modifiedDt = RecipeDatabaseTreeLoader.FormatStoredCreatedDateTimeNow();
            string receiptStatus = LookupReceiptStatusForRecipe(store, recipeName);

            string sql =
                "UPDATE Batches SET " +
                $"RecipeName='{EscapeSql(recipeName)}', " +
                $"Comments='{EscapeSql(comments)}', " +
                $"Status='{EscapeSql(receiptStatus)}', " +
                $"LastModifiedBY='{EscapeSql(user)}', " +
                $"LastModifiedDateTime='{EscapeSql(modifiedDt)}' " +
                $"WHERE Name='{EscapeSql(batchName)}'";

            store.Query(sql, out _, out _);
            Log.Info("BatchEditorLogic", $"Updated batch '{batchName}' (RecipeName, Comments, Status={receiptStatus}).");

            string oldSnap = $"RecipeName={oldRecipe}; Comments={oldComments}; Status={oldStatus}";
            string newSnap = $"RecipeName={recipeName}; Comments={comments}; Status={receiptStatus}";
            AuditBatch("Act_Save", new Dictionary<string, string> { ["name"] = batchName, ["type"] = "Batch" }, oldSnap, newSnap);
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"SaveBatchEdits failed: {ex.Message}");
        }
    }

    [ExportMethod]
    public void DeleteSelectedBatch()
    {
        try
        {
            var editor = GetBatchEditorDataNode();
            if (editor == null)
            {
                Log.Error("BatchEditorLogic", "BatchEditorData node not found.");
                return;
            }

            string batchName = editor.GetVariable("Name")?.Value?.Value as string ?? "";
            if (string.IsNullOrWhiteSpace(batchName))
            {
                Log.Warning("BatchEditorLogic", "Delete skipped: batch name is empty.");
                return;
            }

            var store = Project.Current?.GetObject("DataStores")?.Get<Store>("ReceiptDB");
            if (store == null)
            {
                Log.Error("BatchEditorLogic", "Store ReceiptDB not found.");
                return;
            }

            store.Query(
                $"DELETE FROM Batches WHERE Name='{EscapeSql(batchName)}'",
                out _, out _);

            RefreshBatchDataGridAfterStoreMutation();

            Log.Info("BatchEditorLogic", $"Deleted batch '{batchName}'.");
            AuditBatch("Act_Delete", new Dictionary<string, string> { ["name"] = batchName, ["type"] = "Batch" });
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"DeleteSelectedBatch failed: {ex.Message}");
        }
    }

    private static string EscapeSql(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("'", "''");
    }

    /// <summary>按配方名在 Receipts 表查 Status；无配方名或未命中时使用项目默认状态。</summary>
    private static string LookupReceiptStatusForRecipe(Store store, string recipeName)
    {
        if (store == null || string.IsNullOrWhiteSpace(recipeName))
            return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
        try
        {
            store.Query(
                $"SELECT Status FROM Receipts WHERE Name='{EscapeSql(recipeName)}'",
                out _, out object[,] rows);
            if (rows != null && rows.GetLength(0) >= 1)
            {
                object cell = rows[0, 0];
                if (cell != null && cell != DBNull.Value)
                {
                    string s = cell.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("BatchEditorLogic", $"LookupReceiptStatusForRecipe: {ex.Message}");
        }
        return RecipeDatabaseTreeLoader.DefaultReceiptStatus;
    }

    [ExportMethod]
    public static void AddNewBatch(string name, string description, string createdBy)
    {
        try
        {
            var project = Project.Current;
            var store = project.GetObject("DataStores").Get<Store>("ReceiptDB");
            if (store == null)
            {
                Log.Error("BatchEditorLogic", "Store ReceiptDB not found");
                return;
            }

            var batchesTable = store.Tables.Get<Table>("Batches");
            if (batchesTable == null)
            {
                Log.Error("BatchEditorLogic", "Table Batches not found");
                return;
            }

            DateTime createdDatetime = DateTime.Now;

            string batchId = DateTime.Now.ToString("yyyyMMddHHmmssfff");

            string[] columns = new string[] { "BatchID", "Name", "Description", "CreatedBy", "CreatedDateTime" };
            object[,] values = new object[1, 5];
            values[0, 0] = batchId;
            values[0, 1] = name;
            values[0, 2] = description;
            values[0, 3] = createdBy;
            values[0, 4] = createdDatetime;

            batchesTable.Insert(columns, values);
            Log.Info("BatchEditorLogic", $"Batch {name} added successfully with ID {batchId}");
            string auditUser = ResolveBatchAuditUserBrowseName(createdBy);
            AuditBatchStatic(
                "Act_Create",
                new Dictionary<string, string> { ["name"] = name ?? "", ["type"] = "Batch" },
                auditUser);
            RefreshBatchDataGridAfterStoreMutation();
        }
        catch (Exception ex)
        {
            Log.Error("BatchEditorLogic", $"Error adding batch: {ex.Message}");
        }
    }
}
