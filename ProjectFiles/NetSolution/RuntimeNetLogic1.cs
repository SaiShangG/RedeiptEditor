#region Using directives
using System;
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
#endregion

public class RuntimeNetLogic1 : BaseNetLogic
{
    public override void Start()
    {




        var ParaPanel1 = InformationModel.Make<PhaseParasPanel>("ParaPanel1");
        var ParaPanel2 = InformationModel.Make<PhaseParasPanel>("ParaPanel2");
        ParaPanel1.Get<Label>("VerticalLayout1/Title").Text = "This is the Phase Parameter Area";
        ParaPanel2.Get<Label>("VerticalLayout1/Title").Text = "This is the End Conditions Area";

        Owner.Get("ScrollView1/Rows").Add(ParaPanel1);
        Owner.Get("ScrollView1/Rows").Add(ParaPanel2);


        for (int i = 0; i < 10; i++) { 
        var Para = InformationModel.Make<PhaseSinglePara>("Para"+ i.ToString());
            Owner.Get("ScrollView1/Rows/ParaPanel1/VerticalLayout1/HorizontalLayout1").Add(Para);
        }

        for (int i = 0; i < 16; i++)
        {
            var Para = InformationModel.Make<PhaseSinglePara>("Para" + i.ToString());
            Owner.Get("ScrollView1/Rows/ParaPanel2/VerticalLayout1/HorizontalLayout1").Add(Para);
        }


        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
}
