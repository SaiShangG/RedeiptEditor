#region Using directives

using System;

using System.Collections.Generic;

using System.Globalization;

using UAManagedCore;

using OpcUa = UAManagedCore.OpcUa;

using FTOptix.HMIProject;

using FTOptix.NetLogic;

using FTOptix.SQLiteStore;

using FTOptix.Store;

#endregion



public class GetData : BaseNetLogic

{

    private const string LogCategory = "GetData";

    private const string DbStorePath = "DataStores/DB";

    private const string DbTableName = "DB";

    private const int PollIntervalMs = 1000;

    private const int LookbackSeconds = 10;

    private const string AxisTimeFormat = "yyyy/M/d HH:mm:ss";



    private PeriodicTask _pollTask;

    private IUAVariable _xAxisDataVar;

    private IUAVariable _lineDataVar;

    private string _valueColumnName;

    private bool _schemaEnsured;



    public override void Start()

    {

        EnsureDbSchema();

        if (ResolveChartTargets())

            StartTimer();

    }



    public override void Stop()

    {

        _pollTask?.Dispose();

        _pollTask = null;

        _xAxisDataVar = null;

        _lineDataVar = null;

        _schemaEnsured = false;

    }



    [ExportMethod]

    public void StartTimer()

    {

        _pollTask?.Dispose();

        if (!ResolveChartTargets())

            return;



        _pollTask = new PeriodicTask(GetDatas, PollIntervalMs, LogicObject);

        _pollTask.Start();

    }



    [ExportMethod]

    public void GetDatas()

    {

        try

        {

            if (!_schemaEnsured)

                EnsureDbSchema();



            if (_xAxisDataVar == null || _lineDataVar == null)

            {

                if (!ResolveChartTargets())

                    return;

            }



            DateTime cutoff = DateTime.Now.AddSeconds(-LookbackSeconds);

            string formattedTime = cutoff.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            string sql =

                $"SELECT Timestamp,LocalTimestamp,{_valueColumnName} FROM {DbTableName} WHERE LocalTimestamp > '{formattedTime}' ORDER BY LocalTimestamp ASC";



            if (!TryQuerySeries(sql, out List<string> dates, out List<string> values))

                return;



            WriteStringArray(_xAxisDataVar, dates);

            WriteStringArray(_lineDataVar, values);

        }

        catch (Exception ex)

        {

            Log.Error(LogCategory, $"GetDatas 异常: {ex.Message}");

        }

    }



    private bool ResolveChartTargets()

    {

        _xAxisDataVar = null;

        _lineDataVar = null;



        var chart = LogicObject?.Owner as IUAObject;

        if (chart == null)

        {

            Log.Error(LogCategory, "无法获取图表父节点（LogicObject.Owner 为空）。");

            return false;

        }



        _xAxisDataVar = chart.GetVariable("xAxisData") ?? chart.Get("xAxisData") as IUAVariable;

        var line1 = chart.Get("line1") as IUAObject;

        _lineDataVar = chart.Get("line1/data") as IUAVariable ?? line1?.GetVariable("data");



        if (_xAxisDataVar == null || _lineDataVar == null)

        {

            Log.Error(LogCategory, $"缺少 xAxisData 或 line1/data，图表={chart.BrowseName}");

            return false;

        }



        _valueColumnName = ResolveValueColumnName(chart, line1);

        Log.Info(LogCategory, $"图表={chart.BrowseName}, 查询列={_valueColumnName}");

        return true;

    }



    private static string ResolveValueColumnName(IUAObject chart, IUAObject line1)

    {

        string column = MapTrendLabelLinkToDbColumn(line1);

        if (!string.IsNullOrEmpty(column))

            return column;



        column = MapChartNameToDbColumn(chart?.BrowseName);

        if (column != "Temp" || !string.IsNullOrWhiteSpace(chart?.BrowseName))

            return column;



        return MapLabelTextToDbColumn(line1?.GetVariable("label")?.Value?.ToString()?.Trim());

    }



    private static string MapTrendLabelLinkToDbColumn(IUAObject line1)

    {

        var dynamicLink = line1?.Get("label/DynamicLink") as IUAVariable;

        string path = dynamicLink?.Value?.ToString() ?? string.Empty;

        if (path.Contains("Label1", StringComparison.OrdinalIgnoreCase))

            return "Temp";

        if (path.Contains("Label2", StringComparison.OrdinalIgnoreCase))

            return "PH";

        if (path.Contains("Label3", StringComparison.OrdinalIgnoreCase))

            return "DO";

        if (path.Contains("Label4", StringComparison.OrdinalIgnoreCase))

            return "RPM";

        if (path.Contains("Label5", StringComparison.OrdinalIgnoreCase))

            return "Air";



        return null;

    }



    private static string MapChartNameToDbColumn(string chartName)

    {

        if (string.IsNullOrWhiteSpace(chartName))

            return "Temp";



        return chartName switch

        {

            "LineChart7" => "Temp",

            "LineChart2" => "PH",

            "LineChart3" => "DO",

            "LineChart6" => "RPM",

            "LineChart4" => "RPM",

            "LineChart5" => "Air",

            _ => "Temp"

        };

    }



    private static string MapLabelTextToDbColumn(string labelText)

    {

        if (string.IsNullOrWhiteSpace(labelText))

            return "Temp";



        if (labelText.Equals("Temperature", StringComparison.OrdinalIgnoreCase))

            return "Temp";

        if (labelText.Equals("PH", StringComparison.OrdinalIgnoreCase))

            return "PH";

        if (labelText.Equals("DO", StringComparison.OrdinalIgnoreCase))

            return "DO";

        if (labelText.Equals("RPM", StringComparison.OrdinalIgnoreCase))

            return "RPM";

        if (labelText.Equals("Air", StringComparison.OrdinalIgnoreCase))

            return "Air";

        if (labelText.Contains("Temp", StringComparison.OrdinalIgnoreCase))

            return "Temp";



        return "Temp";

    }



    private void EnsureDbSchema()

    {

        if (_schemaEnsured)

            return;



        var store = Project.Current.Get(DbStorePath) as SQLiteStore;

        if (store == null)

        {

            Log.Error(LogCategory, $"{DbStorePath} 不存在。");

            return;

        }



        string[] columns = { "RPM", "Temp", "DO", "PH", "Pressure", "Energy", "CO2", "Air" };

        foreach (string column in columns)

            TryAddSqliteColumn(store, column);



        _schemaEnsured = true;

    }



    private static void TryAddSqliteColumn(SQLiteStore store, string columnName)

    {

        try

        {

            store.Query($"ALTER TABLE {DbTableName} ADD COLUMN {columnName} REAL", out _, out _);

            Log.Info(LogCategory, $"已为 SQLite 表添加列: {columnName}");

        }

        catch

        {

        }

    }



    private bool TryQuerySeries(string sql, out List<string> dates, out List<string> values)

    {

        dates = new List<string>();

        values = new List<string>();



        var store = Project.Current.Get(DbStorePath) as SQLiteStore;

        if (store == null)

        {

            Log.Error(LogCategory, $"{DbStorePath} 不存在。");

            return false;

        }



        Log.Info(LogCategory, sql);



        try

        {

            store.Query(sql, out _, out object[,] objset);

            if (objset == null)

                return true;



            int rowCount = objset.GetLength(0);

            for (int i = 0; i < rowCount; i++)

            {

                dates.Add(FormatAxisTimeLabel(objset[i, 1]));

                values.Add(objset[i, 2] != null ? objset[i, 2].ToString() : "0");

            }



            return true;

        }

        catch (Exception ex)

        {

            if (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))

            {

                _schemaEnsured = false;

                EnsureDbSchema();

            }



            Log.Error(LogCategory, $"查询失败: {ex.Message}");

            return false;

        }

    }



    private static void WriteStringArray(IUAVariable variable, List<string> items)

    {

        string[] array = items.ToArray();

        variable.ArrayDimensions = new uint[] { (uint)array.Length };

        variable.Value = array;

    }



    private static string FormatAxisTimeLabel(object localTimestamp)

    {

        if (localTimestamp == null)

            return string.Empty;



        if (localTimestamp is DateTime dateTime)

            return dateTime.ToString(AxisTimeFormat, CultureInfo.InvariantCulture);



        string text = localTimestamp.ToString();

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)

            || DateTime.TryParse(text, out parsed))

        {

            return parsed.ToString(AxisTimeFormat, CultureInfo.InvariantCulture);

        }



        return text;

    }

}


