#region Using directives
using System;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.Alarm;
using FTOptix.EventLogger;
using FTOptix.OPCUAServer;
using FTOptix.Store;
using FTOptix.SQLiteStore;
using FTOptix.System;
using FTOptix.SerialPort;
using FTOptix.RecipeX;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.WebUI;
#endregion

public class FilteredAlarmHistoryGridLogic : BaseNetLogic
{
    /// <summary>
    /// This method sets the 'To' and 'From' variables to the current time and 24 hours prior, respectively.
    /// If either variable is missing or null, an error is logged and the method returns immediately.
    /// </summary>
    /// <remarks>
    /// The 'To' variable is set to the current time, and the 'From' variable is set to the current time minus 24 hours.
    /// </remarks>
    public override void Start()
    {
        // After checking validity, we set a default time interval of 24 hours
        var toVariable = Owner.GetVariable("To");
        if (toVariable == null)
        {
            Log.Error("FilteredAlarmHistoryGridLogic", "Missing To variable");
            return;
        }

        if (toVariable.Value == null)
        {
            Log.Error("FilteredAlarmHistoryGridLogic", "Missing To variable value");
            return;
        }

        toVariable.Value = DateTime.Now;
        var fromVariable = Owner.GetVariable("From");
        if (fromVariable == null)
        {
            Log.Error("FilteredAlarmHistoryGridLogic", "Missing From variable");
            return;
        }

        if (fromVariable.Value == null)
        {
            Log.Error("FilteredAlarmHistoryGridLogic", "Missing From variable value");
            return;
        }

        fromVariable.Value = DateTime.Now.AddHours(-24);
    }
}
