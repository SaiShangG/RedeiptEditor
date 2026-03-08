#region Using directives
using System;
using System.Text;
using System.Text.RegularExpressions;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
#endregion

public class RecipeDatabaseManager : BaseNetLogic
{
    private Store _dbStore;

    #region 生命周期 (Life Cycle)

    public override void Start()
    {
        // 1. 直接获取 Optix 内置的 Store (ReceiptDB)
        // 注意：请确保项目目录树中存在 DataStores 文件夹，且里面有名为 ReceiptDB 的 Store 对象
        _dbStore = Project.Current.Get<Store>("DataStores/ReceiptDB");

        if (_dbStore == null)
        {
            Log.Error("RuntimeNetLogic2", "未找到 DataStores/ReceiptDB 数据库！请检查路径。");
        }
        else
        {
            Log.Info("RuntimeNetLogic2", "成功连接内置数据库 ReceiptDB。");
        }
    }

    public override void Stop()
    {
        // Optix 自动管理内置 Store 的生命周期，无需手动 Close 或释放连接
    }

    #endregion

    #region 内部辅助方法 (Internal Helpers)

    private void ExecuteSql(string sql)
    {
        if (_dbStore == null) return;
        _dbStore.Query(sql, out string[] header, out object[,] resultSet);
    }

    private object[,] QuerySql(string sql)
    {
        if (_dbStore == null) return null;
        _dbStore.Query(sql, out string[] header, out object[,] resultSet);
        return resultSet;
    }

    // 处理 SQL 字符串值的引号问题，防止语法错误和简单的注入
    private string FormatSqlValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.ToUpper() == "NULL") return "NULL";
        if (double.TryParse(value, out _)) return value; // 数字不加引号
        return $"'{value.Replace("'", "''")}'"; // 字符串加单引号
    }

    #endregion

    #region 通用增删改查 (Generic CRUD)

    /// <summary>
    /// [Create] 纯代码实现主键自动加 1
    /// 示例: CreateRecord("Phases", "PhaseID", "Name,Pressure,Temperature", "Heating_001,10.5,80")
    /// 返回值: 刚刚计算并插入的新 ID
    /// </summary>
    [ExportMethod]
    public int CreateRecord(string tableName, string idColName, string columnsCSV, string valuesCSV)
    {
        // 1. 纯代码计算下一个 ID (MAX + 1)
        string getMaxSql = $"SELECT MAX({idColName}) FROM {tableName}";
        var maxResult = QuerySql(getMaxSql);

        int newId = 1; // 如果表是空的，默认从 1 开始
        if (maxResult != null && maxResult.GetLength(0) > 0 && maxResult[0, 0] != DBNull.Value)
        {
            newId = Convert.ToInt32(maxResult[0, 0]) + 1;
        }

        // 2. 解析传入的列和值
        string[] columns = columnsCSV.Split(',');
        string[] values = valuesCSV.Split(',');

        if (columns.Length != values.Length)
        {
            Log.Error("RuntimeNetLogic2", $"插入 {tableName} 失败：列数({columns.Length})与值数({values.Length})不匹配！");
            return -1;
        }

        // 去除空格并格式化
        for (int i = 0; i < columns.Length; i++) columns[i] = columns[i].Trim();
        string[] formattedValues = new string[values.Length];
        for (int i = 0; i < values.Length; i++) formattedValues[i] = FormatSqlValue(values[i].Trim());

        // 3. 将我们计算好的新 ID 拼接到 SQL 语句的最前面
        string finalCols = $"{idColName}, " + string.Join(", ", columns);
        string finalVals = $"{newId}, " + string.Join(", ", formattedValues);

        string sql = $"INSERT INTO {tableName} ({finalCols}) VALUES ({finalVals})";

        // 4. 执行插入
        ExecuteSql(sql);
        Log.Info("RuntimeNetLogic2", $"成功向 {tableName} 插入新记录，纯代码分配的 ID 为: {newId}");

        return newId;
    }

    /// <summary>
    /// [Mapping] 为树状图建立映射关系 (包含映射表自身的 ID 自动加 1)
    /// </summary>
    [ExportMethod]
    public void InsertMapRelation(string mapTableName, string mapIdColName, string parentCol, string parentId, string childCol, string childId)
    {
        // 1. 计算映射表自身的下一个主键 ID (比如 ReceiptOperationMapID)
        string getMaxMapIdSql = $"SELECT MAX({mapIdColName}) FROM {mapTableName}";
        var idResult = QuerySql(getMaxMapIdSql);
        int newMapId = 1;
        if (idResult != null && idResult.GetLength(0) > 0 && idResult[0, 0] != DBNull.Value)
        {
            newMapId = Convert.ToInt32(idResult[0, 0]) + 1;
        }

        // 2. 计算树状节点排序的 Sequence
        string getMaxSeqSql = $"SELECT MAX(Sequence) FROM {mapTableName} WHERE {parentCol} = {parentId}";
        var seqResult = QuerySql(getMaxSeqSql);
        int nextSeq = 1;
        if (seqResult != null && seqResult.GetLength(0) > 0 && seqResult[0, 0] != DBNull.Value)
        {
            nextSeq = Convert.ToInt32(seqResult[0, 0]) + 1;
        }

        // 3. 将计算好的主键 ID 和排序 Sequence 一起插入
        string sql = $"INSERT INTO {mapTableName} ({mapIdColName}, {parentCol}, {childCol}, Sequence) VALUES ({newMapId}, {parentId}, {childId}, {nextSeq})";
        ExecuteSql(sql);
    }


    /// <summary>
    /// [Update] 通用单字段更新
    /// 示例: UpdateRecord("Phases", "PhaseID", "1", "Temperature", "90")
    /// </summary>
    [ExportMethod]
    public void UpdateRecord(string tableName, string idColName, string idValue, string updateColName, string newValue)
    {
        string formattedValue = FormatSqlValue(newValue);
        string sql = $"UPDATE {tableName} SET {updateColName} = {formattedValue} WHERE {idColName} = {idValue}";
        ExecuteSql(sql);
    }

    /// <summary>
    /// [Delete] 通用删除，包含级联清理逻辑
    /// 示例: DeleteRecord("Operations", "OperationID", "2")
    /// </summary>
    [ExportMethod]
    public void DeleteRecord(string tableName, string idColName, string idValue)
    {
        // 1. 删除基础表数据
        ExecuteSql($"DELETE FROM {tableName} WHERE {idColName} = {idValue}");

        // 2. 级联清理映射表中的垃圾数据 (Cascade Delete)
        if (tableName == "Receipts")
        {
            ExecuteSql($"DELETE FROM ReceiptOperationMap WHERE ReceiptID = {idValue}");
        }
        else if (tableName == "Operations")
        {
            ExecuteSql($"DELETE FROM ReceiptOperationMap WHERE OperationID = {idValue}");
            ExecuteSql($"DELETE FROM OperationPhaseMap WHERE OperationID = {idValue}");
        }
        else if (tableName == "Phases")
        {
            ExecuteSql($"DELETE FROM OperationPhaseMap WHERE PhaseID = {idValue}");
        }
        Log.Info("RuntimeNetLogic2", $"已删除 {tableName} 中 ID={idValue} 的记录及关联映射。");
    }

    /// <summary>
    /// [Read] 通用查询：将整张表的内容转为字符串，并直接写入指定的 UI 变量中
    /// 完美替代原代码中 QueryReceiptsToVariable 等 5 个高度重复的方法
    /// </summary>
    [ExportMethod]
    public void QueryTableToVariable(string tableName, string orderByColumn, NodeId resultVariableNodeId)
    {
        string sql = $"SELECT * FROM {tableName}";
        if (!string.IsNullOrEmpty(orderByColumn))
        {
            sql += $" ORDER BY {orderByColumn}";
        }

        _dbStore.Query(sql, out string[] header, out object[,] resultSet);

        StringBuilder sb = new StringBuilder();

        // 拼接表头
        if (header != null)
        {
            sb.AppendLine(string.Join("\t", header));
        }

        // 拼接数据内容
        if (resultSet != null)
        {
            int rowCount = resultSet.GetLength(0);
            int colCount = resultSet.GetLength(1);

            for (int r = 0; r < rowCount; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    sb.Append(resultSet[r, c]?.ToString() ?? "");
                    sb.Append(c < colCount - 1 ? "\t" : "\n");
                }
            }
        }

        // 写入 Optix 变量节点
        var variable = InformationModel.Get<IUAVariable>(resultVariableNodeId);
        if (variable != null)
        {
            variable.Value = sb.ToString();
        }
        else
        {
            Log.Error("RuntimeNetLogic2", "指定的结果变量 NodeId 无效！");
        }
    }

    #endregion

    #region 关系映射专用方法 (Map Mapping)

    /// <summary>
    /// [Mapping] 为树状图建立映射关系 (插入 Map 表)
    /// </summary>
    [ExportMethod]
    public void InsertMapRelation(string mapTableName, string parentCol, string parentId, string childCol, string childId)
    {
        // 获取当前父节点下最大的 Sequence
        string getMaxSql = $"SELECT MAX(Sequence) FROM {mapTableName} WHERE {parentCol} = {parentId}";
        var result = QuerySql(getMaxSql);

        int nextSeq = 1;
        if (result != null && result.GetLength(0) > 0 && result[0, 0] != DBNull.Value)
        {
            nextSeq = Convert.ToInt32(result[0, 0]) + 1;
        }

        string sql = $"INSERT INTO {mapTableName} ({parentCol}, {childCol}, Sequence) VALUES ({parentId}, {childId}, {nextSeq})";
        ExecuteSql(sql);
    }

    /// <summary>
    /// [Mapping] 从树状图中移除映射关系 (Remove from Map)
    /// </summary>
    [ExportMethod]
    public void DeleteMapRelation(string mapTableName, string parentCol, string parentId, string childCol, string childId)
    {
        string sql = $"DELETE FROM {mapTableName} WHERE {parentCol} = {parentId} AND {childCol} = {childId}";
        ExecuteSql(sql);
    }

    #endregion
}