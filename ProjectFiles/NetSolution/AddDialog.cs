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
        if (type == "Receipt")
        {
            RecipeDatabaseManager.Instance.AddNewReceipt(name, des);
        }
        else if (type == "Operation")
        {
            GenerateOperationPhaseListPanel.Instance.OnCreate(name, des);
        }
        else if (type == "Phase")
        {
            GenerateOperationPhaseListPanel.Instance.OnCreate(name, des);
        }
    }
}
