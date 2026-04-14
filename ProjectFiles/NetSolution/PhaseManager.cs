#region Using directives
using System;
using System.Collections.Generic;
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
#endregion

public class PhaseManager : BaseNetLogic
{
    private const string LayoutJsonFileName = "phase_ui_layout.sample.json";
    private const string PhaseBufferObjectPath = "Model/UIData/PhaseData/PhaseUIBufferData";

    #region 输入变更订阅
    private uint _phaseInputAffinityId;
    private readonly List<IEventRegistration> _phaseInputRegs = new List<IEventRegistration>();

    private void EnsurePhaseInputAffinity()
    {
        if (_phaseInputAffinityId != 0) return;
        _phaseInputAffinityId = LogicObject.Context.AssignAffinityId();
    }

    private void ClearPhaseInputRegs()
    {
        foreach (var r in _phaseInputRegs)
            r?.Dispose();
        _phaseInputRegs.Clear();
    }

    private void RegisterPhaseParaValueEditors(IUAObject widget, string tag)
    {
        if (widget == null || _phaseInputAffinityId == 0) return;
        var pv = widget.GetObject("VerticalLayout1/ParaValue") as IUAObject;
        if (pv?.Children == null) return;
        foreach (var node in pv.Children)
        {
            if (!(node is IUAObject child)) continue;
            string suffix = node.BrowseName;
            if (!TryGetParaValueEditorVariable(child, suffix, out IUAVariable v) || v == null) continue;
            string logTag = tag + "/" + suffix;
            try
            {
                var obs = new CallbackVariableChangeObserver((iv, nv, ov, access, sender) =>
                    LogPhaseInputFirstChar(logTag, nv));
                _phaseInputRegs.Add(v.RegisterEventObserver(obs, EventType.VariableValueChanged, _phaseInputAffinityId));
            }
            catch (Exception ex)
            {
                Log.Warning(nameof(PhaseManager), $"输入订阅失败 {logTag}: {ex.Message}");
            }
        }
    }

    private static bool TryGetParaValueEditorVariable(IUAObject child, string browseName, out IUAVariable v)
    {
        v = null;
        if (string.IsNullOrEmpty(browseName) || child == null) return false;
        if (browseName.StartsWith("TextBox", StringComparison.Ordinal))
        {
            v = child.GetVariable("Text");
            return v != null;
        }
        if (browseName.StartsWith("Switch", StringComparison.Ordinal))
        {
            v = child.GetVariable("Checked");
            return v != null;
        }
        if (browseName.StartsWith("ComboBox", StringComparison.Ordinal))
        {
            v = child.GetVariable("SelectedValue");
            return v != null;
        }
        return false;
    }

    private static void LogPhaseInputFirstChar(string tag, UAValue nv)
    {
        char c = FirstCharOfValue(nv);
        Log.Info(nameof(PhaseManager), tag + " → " + c);
    }

    private static char FirstCharOfValue(UAValue nv)
    {
        if (nv?.Value == null) return '_';
        object val = nv.Value;
        if (val is bool b) return b ? 'T' : 'F';
        if (val is string s) return s.Length > 0 ? s[0] : '_';
        if (val is LocalizedText lt)
        {
            string t = lt.Text;
            return !string.IsNullOrEmpty(t) ? t[0] : '_';
        }
        string u = Convert.ToString(val);
        return !string.IsNullOrEmpty(u) ? u[0] : '_';
    }

    private void WireLayoutPhaseInputObservers(IUAObject owner, PhaseUILayoutRoot root)
    {
        if (owner == null || root?.Sections == null || _phaseInputAffinityId == 0) return;
        foreach (var sec in root.Sections)
        {
            if (sec?.Items == null) continue;
            string rp = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            foreach (var item in sec.Items)
            {
                if (string.IsNullOrEmpty(item?.Id)) continue;
                var w = owner.GetObject("ScrollView1/Rows/" + sec.Id + "/" + rp + "/" + item.Id) as IUAObject;
                RegisterPhaseParaValueEditors(w, sec.Id + "/" + item.Id);
            }
        }
    }

    private void WireLegacyPhaseInputObservers()
    {
        if (_phaseInputAffinityId == 0 || Owner == null) return;
        var rows = Owner.GetObject("ScrollView1/Rows") as IUAObject;
        if (rows?.Children == null) return;
        foreach (var pn in rows.Children)
        {
            if (!(pn is IUAObject panel)) continue;
            var hl = panel.GetObject("VL/HL") as IUAObject;
            if (hl?.Children == null) continue;
            foreach (var it in hl.Children)
            {
                if (it is IUAObject io)
                    RegisterPhaseParaValueEditors(io, panel.BrowseName + "/" + it.BrowseName);
            }
        }
    }
    #endregion

    public override void Start()
    {
        EnsurePhaseInputAffinity();
        if (Owner is IUAObject ownerObj)
        {
            string path = PhaseUILayoutJson.ResolveLayoutPath(LayoutJsonFileName);
            var layout = path != null ? PhaseUILayoutJson.TryLoadFromFile(path) : null;
            if (layout != null)
            {
                try
                {
                    BuildPhaseUiFromLayout(ownerObj, layout);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning("PhaseManager", $"JSON 布局失败，回退硬编码: {ex.Message}");
                }
            }
        }

        BuildPhaseUiLegacy();
    }

    private void BuildPhaseUiLegacy()
    {
        RecipeDatabaseTreeLoader.EnsurePhaseUiBufferModelVariables(PhaseBufferObjectPath);
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
        WireLegacyPhaseInputObservers();
        if (Owner is IUAObject ownerObj)
            AttachLegacyPhaseBufferDynamicLinks(ownerObj);
    }

    /// <summary>Legacy 构建路径无 JSON 布局时的绑定；与 phase_ui_layout.sample.json 中 Para0..9、Valve0..11 的 bindKey 一致。</summary>
    private void AttachLegacyPhaseBufferDynamicLinks(IUAObject owner)
    {
        if (owner == null) return;
        IUAObject buffer;
        try { buffer = Project.Current?.GetObject(PhaseBufferObjectPath); }
        catch { buffer = null; }
        if (buffer == null) return;

        string[] paraKeys =
        {
            "Parameter1", "Parameter2", "Parameter3", "Parameter4", "Parameter5",
            "Parameter6", "ParaSlot6", "ParaSlot7", "ParaSlot8", "ParaSlot9"
        };
        for (int i = 0; i < paraKeys.Length; i++)
        {
            var modelVar = buffer.GetVariable(paraKeys[i]);
            if (modelVar == null) continue;
            var widget = owner.GetObject("ScrollView1/Rows/ParaPanel1/VL/HL/Para" + i) as IUAObject;
            if (widget != null)
                TryDynamicLinkSingleParaText(widget, modelVar);
        }
        for (int i = 0; i < 12; i++)
        {
            var modelVar = buffer.GetVariable("Valve" + i);
            if (modelVar == null) continue;
            var widget = owner.GetObject("ScrollView1/Rows/ParaPanel3/VL/HL/ValveSetting" + i) as IUAObject;
            if (widget != null)
                TryDynamicLinkValveSwitch(widget, modelVar);
        }
        AttachLegacyPanel2EndConditionLinks(owner, buffer);
    }

    /// <summary>Legacy 路径下 ParaPanel2 与 sample JSON 相同的 bindKey*。</summary>
    private void AttachLegacyPanel2EndConditionLinks(IUAObject owner, IUAObject buffer)
    {
        if (owner == null || buffer == null) return;
        const string p2 = "ScrollView1/Rows/ParaPanel2/VL/HL/";
        void Sw(string itemId, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var mv = buffer.GetVariable(key);
            var w = owner.GetObject(p2 + itemId) as IUAObject;
            if (mv != null && w != null) TryDynamicLinkValveSwitch(w, mv);
        }
        void Tx(string itemId, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var mv = buffer.GetVariable(key);
            var w = owner.GetObject(p2 + itemId) as IUAObject;
            if (mv != null && w != null) TryDynamicLinkSingleParaText(w, mv);
        }
        void Cb(string itemId, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var mv = buffer.GetVariable(key);
            var w = owner.GetObject(p2 + itemId) as IUAObject;
            if (mv != null && w != null) TryDynamicLinkComboSelectedValue(w, mv);
        }
        Sw("ParaUserAck", "EcUserAckEnabled");
        Sw("ParaAndandOr1", "EcAndOr1");
        Sw("ParaRunningTIme", "EcRunTimeEnabled");
        Tx("ParaRunningTIme", "EcRunTimeHms");
        Sw("ParaCP1", "EcCp1Enabled");
        Tx("ParaCP1", "EcCp1Level");
        Sw("ParaCP2", "EcCp2Enabled");
        Tx("ParaCP2", "EcCp2Level");
        Cb("ParaCP2", "EcCp2Op");
        Sw("ParaAndandOr2", "EcAndOr2");
        Sw("ParaCP3", "EcCp3Enabled");
        Tx("ParaCP3", "EcCp3Level");
        Cb("ParaCP3", "EcCp3Op");
        Sw("ParaCP4", "EcCp4Enabled");
        Tx("ParaCP4", "EcCp4Level");
    }

    #region Phase JSON 构建
    private void BuildPhaseUiFromLayout(IUAObject owner, PhaseUILayoutRoot root)
    {
        if (owner == null || root?.Sections == null) return;
        RecipeDatabaseTreeLoader.EnsurePhaseUiBufferModelVariables(PhaseBufferObjectPath);
        var rows = owner.Get("ScrollView1/Rows");
        if (rows == null) return;
        ClearPhaseInputRegs();
        ClearScrollRows(rows);

        foreach (var sec in root.Sections)
        {
            if (string.IsNullOrEmpty(sec?.Id)) continue;
            if (!string.Equals(sec.PanelType, "PhaseParasPanel", StringComparison.OrdinalIgnoreCase))
                continue;

            var panel = InformationModel.Make<PhaseParasPanel>(sec.Id);
            var titleBox = panel.Get<TextBox>("BG/Title");
            if (titleBox != null && !string.IsNullOrEmpty(sec.Title))
                titleBox.Text = sec.Title;

            string rowPath = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            var rowLayout = panel.Get<RowLayout>(rowPath);
            if (rowLayout == null) continue;
            if (sec.RowLayoutHorizontalGap.HasValue)
                rowLayout.HorizontalGap = sec.RowLayoutHorizontalGap.Value;

            if (sec.Items != null)
            {
                foreach (var item in sec.Items)
                    AddLayoutWidget(rowLayout, item);
            }

            rows.Add(panel);
        }

        AttachPhaseBufferDynamicLinks(owner, root);
        WireLayoutPhaseInputObservers(owner, root);
    }

    #region Buffer DynamicLink（bindKey* ↔ PhaseUIBufferData）
    private void AttachPhaseBufferDynamicLinks(IUAObject owner, PhaseUILayoutRoot root)
    {
        if (owner == null || root?.Sections == null) return;
        IUAObject buffer;
        try { buffer = Project.Current?.GetObject(PhaseBufferObjectPath); }
        catch { buffer = null; }
        if (buffer == null) return;

        foreach (var sec in root.Sections)
        {
            if (sec?.Items == null) continue;
            string rp = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            foreach (var item in sec.Items)
            {
                if (string.IsNullOrEmpty(item?.Id)) continue;
                var n = owner.GetObject("ScrollView1/Rows/" + sec.Id + "/" + rp + "/" + item.Id);
                if (!(n is IUAObject widget)) continue;
                string wt = item.WidgetType?.Trim() ?? "";

                if (string.Equals(wt, "PhaseSinglePara", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.BindKey)) continue;
                    var mv = buffer.GetVariable(item.BindKey);
                    if (mv != null) TryDynamicLinkSingleParaText(widget, mv);
                    continue;
                }
                if (string.Equals(wt, "PhaseValvePanel", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.BindKey)) continue;
                    var mv = buffer.GetVariable(item.BindKey);
                    if (mv != null) TryDynamicLinkValveSwitch(widget, mv);
                    continue;
                }
                if (string.Equals(wt, "PhaseUserAck", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(wt, "PhaseAndorOr", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(item.BindKeySwitch))
                    {
                        var mv = buffer.GetVariable(item.BindKeySwitch);
                        if (mv != null) TryDynamicLinkValveSwitch(widget, mv);
                    }
                    continue;
                }
                if (string.Equals(wt, "PhaseRunningTime", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(item.BindKeySwitch))
                    {
                        var mv = buffer.GetVariable(item.BindKeySwitch);
                        if (mv != null) TryDynamicLinkValveSwitch(widget, mv);
                    }
                    if (!string.IsNullOrEmpty(item.BindKeyText))
                    {
                        var mv = buffer.GetVariable(item.BindKeyText);
                        if (mv != null) TryDynamicLinkSingleParaText(widget, mv);
                    }
                    continue;
                }
                if (string.Equals(wt, "PhaseParaCompare1", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(item.BindKeySwitch))
                    {
                        var mv = buffer.GetVariable(item.BindKeySwitch);
                        if (mv != null) TryDynamicLinkValveSwitch(widget, mv);
                    }
                    if (!string.IsNullOrEmpty(item.BindKeyText))
                    {
                        var mv = buffer.GetVariable(item.BindKeyText);
                        if (mv != null) TryDynamicLinkSingleParaText(widget, mv);
                    }
                    continue;
                }
                if (string.Equals(wt, "PhaseParaCompare2", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(item.BindKeySwitch))
                    {
                        var mv = buffer.GetVariable(item.BindKeySwitch);
                        if (mv != null) TryDynamicLinkValveSwitch(widget, mv);
                    }
                    if (!string.IsNullOrEmpty(item.BindKeyText))
                    {
                        var mv = buffer.GetVariable(item.BindKeyText);
                        if (mv != null) TryDynamicLinkSingleParaText(widget, mv);
                    }
                    if (!string.IsNullOrEmpty(item.BindKeyCombo))
                    {
                        var mv = buffer.GetVariable(item.BindKeyCombo);
                        if (mv != null) TryDynamicLinkComboSelectedValue(widget, mv);
                    }
                }
            }
        }
    }

    private void TryDynamicLinkSingleParaText(IUAObject panel, IUAVariable modelVar)
    {
        var tb = panel.Get<TextBox>("VerticalLayout1/ParaValue/TextBox1");
        var uiVar = tb?.GetVariable("Text");
        if (uiVar == null) return;
        try
        {
            uiVar.ResetDynamicLink();
            uiVar.SetDynamicLink(modelVar, DynamicLinkMode.ReadWrite);
        }
        catch (Exception ex)
        {
            Log.Warning(nameof(PhaseManager), $"DynamicLink Text↔{modelVar.BrowseName}: {ex.Message}");
        }
    }

    private void TryDynamicLinkValveSwitch(IUAObject panel, IUAVariable modelVar)
    {
        var sw = panel.Get<Switch>("VerticalLayout1/ParaValue/Switch1");
        var uiVar = sw?.GetVariable("Checked");
        if (uiVar == null) return;
        try
        {
            uiVar.ResetDynamicLink();
            uiVar.SetDynamicLink(modelVar, DynamicLinkMode.ReadWrite);
        }
        catch (Exception ex)
        {
            Log.Warning(nameof(PhaseManager), $"DynamicLink Checked↔{modelVar.BrowseName}: {ex.Message}");
        }
    }
    #endregion

    private static void ClearScrollRows(IUANode container)
    {
        if (container?.Children == null) return;
        var copy = new System.Collections.Generic.List<IUANode>();
        foreach (var c in container.Children) copy.Add(c);
        foreach (var c in copy) c.Delete();
    }

    private static void AddLayoutWidget(RowLayout rowLayout, PhaseUILayoutItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.WidgetType)) return;
        string t = item.WidgetType.Trim();

        if (string.Equals(t, "PhaseSinglePara", StringComparison.OrdinalIgnoreCase))
        {
            rowLayout.Add(InformationModel.Make<PhaseSinglePara>(item.Id));
            return;
        }
        if (string.Equals(t, "PhaseUserAck", StringComparison.OrdinalIgnoreCase))
        {
            rowLayout.Add(InformationModel.Make<PhaseUserAck>(item.Id));
            return;
        }
        if (string.Equals(t, "PhaseAndorOr", StringComparison.OrdinalIgnoreCase))
        {
            var w = InformationModel.Make<PhaseAndorOr>(item.Id);
            if (item.Width.HasValue) w.Width = item.Width.Value;
            rowLayout.Add(w);
            return;
        }
        if (string.Equals(t, "PhaseRunningTime", StringComparison.OrdinalIgnoreCase))
        {
            rowLayout.Add(InformationModel.Make<PhaseRunningTime>(item.Id));
            return;
        }
        if (string.Equals(t, "PhaseParaCompare1", StringComparison.OrdinalIgnoreCase))
        {
            rowLayout.Add(InformationModel.Make<PhaseParaCompare1>(item.Id));
            return;
        }
        if (string.Equals(t, "PhaseParaCompare2", StringComparison.OrdinalIgnoreCase))
        {
            rowLayout.Add(InformationModel.Make<PhaseParaCompare2>(item.Id));
            return;
        }
        if (string.Equals(t, "PhaseValvePanel", StringComparison.OrdinalIgnoreCase))
        {
            var w = InformationModel.Make<PhaseValvePanel>(item.Id);
            string lbl = !string.IsNullOrEmpty(item.ValveLabel) ? item.ValveLabel : item.Id;
            var label = w.Get<Label>("VerticalLayout1/ParaName/Label1");
            if (label != null) label.Text = lbl;
            rowLayout.Add(w);
        }
    }
    #endregion

    private void TryDynamicLinkComboSelectedValue(IUAObject panel, IUAVariable modelVar)
    {
        var cb = panel.Get<ComboBox>("VerticalLayout1/ParaValue/ComboBox1");
        var uiVar = cb?.GetVariable("SelectedValue");
        if (uiVar == null) return;
        try
        {
            uiVar.ResetDynamicLink();
            uiVar.SetDynamicLink(modelVar, DynamicLinkMode.ReadWrite);
        }
        catch (Exception ex)
        {
            Log.Warning(nameof(PhaseManager), "DynamicLink Combo SelectedValue: " + modelVar.BrowseName + " " + ex.Message);
        }
    }

    public override void Stop()
    {
        ClearPhaseInputRegs();
        _phaseInputAffinityId = 0;
    }
}
