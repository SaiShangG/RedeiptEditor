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

public class PhaseManager : BaseNetLogic
{
    public override void Start()
    {




        var ParaPanel1 = InformationModel.Make<PhaseParasPanel>("ParaPanel1");
        var ParaPanel2 = InformationModel.Make<PhaseParasPanel>("ParaPanel2");
        var ParaPanel3 = InformationModel.Make<PhaseParasPanel>("ParaPanel3");

        ParaPanel1.Get<TextBox>("BG/Title").Text = "This is the Phase Parameter Area";
        ParaPanel2.Get<TextBox>("BG/Title").Text = "This is the End Conditions Area";
        ParaPanel2.Get<RowLayout>("VL/HL").HorizontalGap = 0;


        ParaPanel3.Get<TextBox>("BG/Title").Text = "This is the Valve Setting Area";



        Owner.Get("ScrollView1/Rows").Add(ParaPanel1);
        Owner.Get("ScrollView1/Rows").Add(ParaPanel2);
        Owner.Get("ScrollView1/Rows").Add(ParaPanel3);


        // Phase Single Parameter -  - ParaPanel1
        for (int i = 0; i < 10; i++) {
            var ParaSingle = InformationModel.Make<PhaseSinglePara>("Para" + i.ToString());
            Owner.Get("ScrollView1/Rows/ParaPanel1/VL/HL").Add(ParaSingle);
        }

        // Phase End Conditions  - ParaPanel2
        var ParaEC = InformationModel.Make<PhaseUserAck>("Para" + "UserAck");
        ParaPanel2.Get("VL/HL").Add(ParaEC);

        var ParaAAO1 = InformationModel.Make<PhaseAndorOr>("Para" + "AndandOr1");
        ParaAAO1.Width = 80;
        ParaPanel2.Get("VL/HL").Add(ParaAAO1);

        var ParaRT = InformationModel.Make<PhaseRunningTime>("Para" + "RunningTIme");
        ParaPanel2.Get("VL/HL").Add(ParaRT);

        var ParaCP1 = InformationModel.Make<PhaseParaCompare1>("Para" + "CP1");
        ParaPanel2.Get("VL/HL").Add(ParaCP1);

        var ParaCP2 = InformationModel.Make<PhaseParaCompare2>("Para" + "CP2");
        ParaPanel2.Get("VL/HL").Add(ParaCP2);

        var ParaAAO2 = InformationModel.Make<PhaseAndorOr>("Para" + "AndandOr2");
        ParaAAO2.Width = 80;
        ParaPanel2.Get("VL/HL").Add(ParaAAO2);

        var ParaCP3 = InformationModel.Make<PhaseParaCompare2>("Para" + "CP3");
        ParaPanel2.Get("VL/HL").Add(ParaCP3);

        var ParaCP4 = InformationModel.Make<PhaseParaCompare1>("Para" + "CP4");
        ParaPanel2.Get("VL/HL").Add(ParaCP4);

        // Phase Valve Setting - ParaPanel3
        for (int i = 0; i < 12; i++)
        {
            var ValveSingle = InformationModel.Make<PhaseValvePanel>("ValveSetting" + i.ToString());
            ValveSingle.Get<Label>("VerticalLayout1/ParaName/Label1").Text = "Valve" + i.ToString();
            ParaPanel3.Get("VL/HL").Add(ValveSingle);
        }




        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
}
