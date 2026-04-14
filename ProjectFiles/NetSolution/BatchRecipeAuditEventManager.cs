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
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

/// <summary>
/// 从 Model/UIData/AuditData 读取 EventItem 与 ActionTemplate，写入 BatchRecipeEditorAuditDB.BatchRecipeAuditTable。
/// </summary>
public static class RecipeAuditLogHelper
{
    private const string LogCategory = "RecipeAuditLog";
    private const string DataStoresName = "DataStores";
    private const string AuditStoreName = "BatchRecipeEditorAuditDB";
    private const string AuditTableName = "BatchRecipeAuditTable";
    private const string OperationModeDefault = "N/A";

    private static readonly object AuditIdLock = new object();
    /// <summary>本进程内已分配的最大 AuditID；与 SQL 的 MAX+1 取较大值，避免 Query 失败或全为 0 时重复。</summary>
    private static int LastAllocatedAuditId = -1;

    /// <summary>占位符替换：先 <c>{{key}}</c> 再 <c>{key}</c>。</summary>
    public static string ApplyActionTemplate(string template, IDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template) || vars == null) return template ?? "";
        string result = template;
        foreach (var kv in vars.OrderByDescending(x => x.Key.Length))
        {
            string v = kv.Value ?? "";
            result = result.Replace("{{" + kv.Key + "}}", v);
            result = result.Replace("{" + kv.Key + "}", v);
        }
        return result;
    }

    /// <summary>写入审计表；失败仅打日志，不抛异常。</summary>
    public static void Append(
        IUANode logicObject,
        string userBrowseName,
        string eventItemVariableBrowseName,
        string actionTemplateVariableBrowseName,
        IDictionary<string, string> templateVars,
        string oldValue = "",
        string newValue = "",
        string status = "ok",
        string operationMode = null)
    {
        if (logicObject == null) return;
        try
        {
            var auditData = GetAuditDataFolder(logicObject);
            if (auditData == null)
            {
                Log.Warning(LogCategory, "AuditData 节点未找到");
                return;
            }

            string eventItemText = ReadStringVariable(auditData, "EventItem", eventItemVariableBrowseName);
            string templateRaw = ReadStringVariable(auditData, "ActionTemplate", actionTemplateVariableBrowseName);
            string actionText = ApplyActionTemplate(templateRaw, templateVars ?? new Dictionary<string, string>());

            var project = Project.Current;
            if (project == null) return;
            var store = project.GetObject(DataStoresName)?.Get<Store>(AuditStoreName);
            if (store == null)
            {
                Log.Warning(LogCategory, $"Store {AuditStoreName} 未找到");
                return;
            }
            var table = store.Tables.Get<Table>(AuditTableName);
            if (table == null)
            {
                Log.Warning(LogCategory, $"Table {AuditTableName} 未找到");
                return;
            }

            int nextId = NextAuditId(store);
            string userName = userBrowseName?.Trim() ?? "";
            string mode = string.IsNullOrWhiteSpace(operationMode) ? OperationModeDefault : operationMode.Trim();

            string[] columns =
            {
                "AuditID", "DateTime", "OperationMode", "EventItem", "UserName", "UserGroup",
                "Action", "Status", "OldValue", "NewValue"
            };
            object[,] row = new object[1, 10];
            row[0, 0] = nextId;
            row[0, 1] = DateTime.Now;
            row[0, 2] = mode;
            row[0, 3] = eventItemText ?? "";
            row[0, 4] = userName;
            row[0, 5] = "";
            row[0, 6] = actionText;
            row[0, 7] = status ?? "";
            row[0, 8] = oldValue ?? "";
            row[0, 9] = newValue ?? "";

            table.Insert(columns, row);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Append 失败: {ex.Message}");
        }
    }

    private static IUANode GetAuditDataFolder(IUANode logicObject)
    {
        var root = GetRedeiptEditorRoot(logicObject);
        return root?.GetObject("Model")?.GetObject("UIData")?.GetObject("AuditData");
    }

    private static IUANode GetRedeiptEditorRoot(IUANode from)
    {
        IUANode n = from;
        while (n != null)
        {
            if (string.Equals(n.BrowseName, "RedeiptEditor", StringComparison.OrdinalIgnoreCase))
                return n;
            n = n.Owner;
        }
        return null;
    }

    private static string ReadStringVariable(IUANode auditData, string folderName, string variableBrowseName)
    {
        try
        {
            var folder = auditData.GetObject(folderName);
            var v = folder?.GetVariable(variableBrowseName);
            return VariableToPlainAuditText(v);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>与模型变量一致：取内部标量/LocalizedText，并去掉 UAValue.ToString 常见的「 (String)」等后缀。</summary>
    private static string VariableToPlainAuditText(IUAVariable v)
    {
        if (v?.Value == null) return "";
        try
        {
            object inner = v.Value.Value;
            if (inner == null) return "";
            if (inner is LocalizedText lt)
                return StripOpcTypeSuffixFromDisplay((lt.Text ?? "").Trim());
            return StripOpcTypeSuffixFromDisplay((Convert.ToString(inner) ?? "").Trim());
        }
        catch
        {
            return StripOpcTypeSuffixFromDisplay(v.Value.ToString()?.Trim() ?? "");
        }
    }

    private static readonly string[] OpcTypeSuffixesInParens =
    {
        "(String)", "(string)", "(LocalizedText)", "(localizedtext)",
        "(Int16)", "(Int32)", "(Int64)", "(UInt32)", "(UInt64)", "(Boolean)", "(Double)", "(Single)", "(DateTime)"
    };

    private static string StripOpcTypeSuffixFromDisplay(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.TrimEnd();
        // 全角括号常见写法
        if (s.EndsWith("（String）", StringComparison.OrdinalIgnoreCase))
            return s.Substring(0, s.Length - "（String）".Length).TrimEnd();
        if (s.EndsWith("（string）", StringComparison.OrdinalIgnoreCase))
            return s.Substring(0, s.Length - "（string）".Length).TrimEnd();
        foreach (var t in OpcTypeSuffixesInParens)
        {
            string suf = " " + t;
            if (s.Length >= suf.Length && s.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                return s.Substring(0, s.Length - suf.Length).TrimEnd();
        }
        return s;
    }

    /// <summary>连续自增：首条为 0；优先 <c>MAX(AuditID)+1</c>，并与进程内单调序列取较大值，保证不重复。</summary>
    private static int NextAuditId(Store store)
    {
        lock (AuditIdLock)
        {
            int fromSql = TryComputeNextAuditIdFromDatabase(store);
            int candidate = fromSql >= 0 ? fromSql : LastAllocatedAuditId + 1;
            if (candidate <= LastAllocatedAuditId)
                candidate = LastAllocatedAuditId + 1;
            LastAllocatedAuditId = candidate;
            return candidate;
        }
    }

    /// <summary>
    /// Store 的 SQL 方言往往不支持 <c>COALESCE</c>（会报 “Syntax error at …”）。
    /// 仅用 <c>MAX(AuditID)</c>，NULL/空表在代码里视为 -1，再 +1 得到首条 0。
    /// </summary>
    /// <returns>无法查询时返回 -1。</returns>
    private static int TryComputeNextAuditIdFromDatabase(Store store)
    {
        if (store == null) return -1;
        string sql = $"SELECT MAX(AuditID) FROM {AuditTableName}";
        try
        {
            store.Query(sql, out _, out object[,] rows);
            if (rows == null || rows.GetLength(0) < 1 || rows.GetLength(1) < 1)
                return -1;
            object cell = rows[0, 0];
            int maxVal = -1;
            if (cell != null && cell != DBNull.Value && TryCoerceToInt32(cell, out int m))
                maxVal = m;
            long next = (long)maxVal + 1;
            if (next > int.MaxValue)
                return int.MaxValue;
            if (next < 0)
                return 0;
            return (int)next;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"AuditID SQL ({sql}): {ex.Message}");
        }
        return -1;
    }

    private static bool TryCoerceToInt32(object cell, out int value)
    {
        value = 0;
        if (cell == null || cell == DBNull.Value) return false;
        try
        {
            switch (cell)
            {
                case int i:
                    value = i;
                    return true;
                case long l:
                    value = l > int.MaxValue ? int.MaxValue : (int)l;
                    return true;
                case uint ui:
                    value = ui > int.MaxValue ? int.MaxValue : (int)ui;
                    return true;
                case ulong ul:
                    value = ul > int.MaxValue ? int.MaxValue : (int)ul;
                    return true;
                case short s:
                    value = s;
                    return true;
                case ushort us:
                    value = us;
                    return true;
                case byte b:
                    value = b;
                    return true;
                case double d:
                    value = (int)d;
                    return true;
                case float f:
                    value = (int)f;
                    return true;
                case decimal m:
                    value = (int)m;
                    return true;
            }
            string str = Convert.ToString(cell, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(str)) return false;
            if (int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ip))
            {
                value = ip;
                return true;
            }
            if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double dp))
            {
                value = (int)dp;
                return true;
            }
        }
        catch { }
        return false;
    }

}

public class BatchRecipeAuditEventManager : BaseNetLogic
{
    public override void Start()
    {
    }

    public override void Stop()
    {
    }
}
