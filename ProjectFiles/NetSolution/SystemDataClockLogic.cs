#region Using directives
using System;
using System.Globalization;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
#endregion

public class SystemDataClockLogic : BaseNetLogic
{
    private const string LogCategory = "SystemDataClockLogic";
    private const string DateTimeFormat = "dd/MM/yyyy HH:mm:ss";
    private const string DateTimePath = "Model/UIData/SystemData/SystemData/DateTime";
    private const string DateTimeTextPath = "Model/UIData/SystemData/SystemData/DateTimeText";

    private IUAVariable _dateTimeVar;
    private IUAVariable _dateTimeTextVar;
    private PeriodicTask _clockTask;

    public override void Start()
    {
        _dateTimeVar = Project.Current?.GetVariable(DateTimePath);
        _dateTimeTextVar = Project.Current?.GetVariable(DateTimeTextPath);

        if (_dateTimeVar == null || _dateTimeTextVar == null)
        {
            Log.Error(LogCategory, $"变量不存在：{DateTimePath} 或 {DateTimeTextPath}");
            return;
        }

        RefreshNow();
        _clockTask?.Dispose();
        _clockTask = new PeriodicTask(RefreshNow, 1000, LogicObject);
        _clockTask.Start();
    }

    public override void Stop()
    {
        _clockTask?.Dispose();
        _clockTask = null;
        _dateTimeVar = null;
        _dateTimeTextVar = null;
    }

    [ExportMethod]
    public void RefreshNow()
    {
        if (_dateTimeVar == null || _dateTimeTextVar == null)
            return;

        DateTime now = DateTime.Now;
        _dateTimeVar.Value = now;
        _dateTimeTextVar.Value = now.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
    }
}
