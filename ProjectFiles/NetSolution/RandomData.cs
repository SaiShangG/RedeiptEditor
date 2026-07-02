#region Using directives
using System;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
#endregion

public class RandomData : BaseNetLogic
{
    private const string LogCategory = "RandomData";
    private const string SimulateDataBase = "Model/EchartLibrary/SimulateData";
    private const int PollIntervalMs = 1000;

    private PeriodicTask _pollTask;

    private static readonly (string Name, float Value)[] DefaultConstants =
    {
        ("Temp", 37.0f),
        ("DO", 38.0f),
        ("PH", 7.119999885559082f),
        ("Pressure", 0.09000000357627869f),
        ("RPM", 420.0f),
        ("Energy", 150.0f),
        ("CO2", 90.0f),
        ("Air", 210.0f)
    };

    public override void Start()
    {
        ApplyConstantValues();
        _pollTask = new PeriodicTask(ApplyConstantValues, PollIntervalMs, LogicObject);
        _pollTask.Start();
    }

    public override void Stop()
    {
        _pollTask?.Dispose();
        _pollTask = null;
    }

    [ExportMethod]
    public void StartTimer()
    {
        ApplyConstantValues();
    }

    private void ApplyConstantValues()
    {
        foreach (var (name, value) in DefaultConstants)
        {
            string path = $"{SimulateDataBase}/{name}";
            var variable = Project.Current?.GetVariable(path);
            if (variable == null)
            {
                Log.Error(LogCategory, $"变量不存在: {path}");
                continue;
            }

            variable.RemoteWrite(new UAValue(value));
        }
    }
}
