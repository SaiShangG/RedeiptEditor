#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.WebUI;
#endregion

public class AddDialog : BaseNetLogic
{
    [ExportMethod]
    public void OnClickOK()
    {
        //Get variable CurrentType
        var currentTypeVar = LogicObject.GetVariable("Type");
        if (currentTypeVar != null)
        {
            string type = (string)currentTypeVar.Value;
            //Open dialog with type
            AddNew(type);
        }
    }

    [ExportMethod]
    public void OnClickOpenDialog(string type)
    {
        //Set variable CurrentType to type
        var currentTypeVar = LogicObject.GetVariable("Type");
        if (currentTypeVar != null)
        {
            currentTypeVar.Value = type;
        }
    }

    void AddNew(string type)
    {
        string name = "";
        string des = "";
        var nameVar = LogicObject.GetVariable("Name");
        if (nameVar != null)
        {
            name = (string)nameVar.Value;
        }
        var desVar = LogicObject.GetVariable("Description");
        if (desVar != null)
        {
            des = (string)desVar.Value;
        }
        string createdBy = ResolveCurrentUserBrowseName();
        if (type == "Receipt")
        {
            RecipeDatabaseManager.Instance.AddNewReceipt(name, des, createdBy);
        }
        else if (type == "Operation")
        {
            GenerateOperationPhaseListPanel.Instance.OnCreate(name, des);
        }
        else if (type == "Phase")
        {
            GenerateOperationPhaseListPanel.Instance.OnCreate(name, des);
        }
        else if (type == "Batch")
        {
            BatchEditorLogic.AddNewBatch(name, des, createdBy);
        }

        if (nameVar != null)
            nameVar.Value = "";
        if (desVar != null)
            desVar.Value = "";
    }

    private string ResolveCurrentUserBrowseName()
    {
        string fromLogin = LoginButtonLogic.CurrentLoginUserBrowseName;
        if (!string.IsNullOrWhiteSpace(fromLogin))
            return fromLogin.Trim();
        string fromSession = Session?.User?.BrowseName;
        if (!string.IsNullOrWhiteSpace(fromSession))
            return fromSession.Trim();
        return "Unknown";
    }
}
