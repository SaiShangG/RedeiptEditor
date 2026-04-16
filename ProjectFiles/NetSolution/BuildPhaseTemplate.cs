#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
#endregion

public class BuildPhaseTemplate : BaseNetLogic
{
    private const string DefaultLayoutJsonFileName = "";
    private const string DefaultPhaseBufferObjectPath = "";
    private const string DefaultTargetPanelPath = "";

    [ExportMethod]
    public void BuildTemplateUI()
    {
        BuildSingleTemplateDesignTime();
    }

    [ExportMethod]
    public void BuildSingleTemplateDesignTime()
    {
        var targetPanel = ResolveTargetPanel();
        if (targetPanel == null)
        {
            Log.Error(nameof(BuildPhaseTemplate), "Target panel not found from property 'TargetPanel' or default path.");
            return;
        }

        string path = ResolveTemplateJsonPath();
        var layout = path != null ? PhaseUILayoutJson.TryLoadFromFile(path) : null;
        if (layout == null)
        {
            Log.Error(nameof(BuildPhaseTemplate), "Layout JSON not found/invalid from property 'TemplateJsonFile' or default file.");
            return;
        }

        var buffer = ResolveBufferObject();
        if (buffer == null)
        {
            Log.Error(nameof(BuildPhaseTemplate), "Buffer object not found from property 'TargetUIModelBufferLink' or default path.");
            return;
        }

        try
        {
            BuildFromLayout(targetPanel, layout, buffer);
            Log.Info(nameof(BuildPhaseTemplate), $"Build completed. panel={targetPanel.BrowseName}, layout={path}, buffer={buffer.BrowseName}");
        }
        catch (Exception ex)
        {
            Log.Error(nameof(BuildPhaseTemplate), $"Build failed: {ex.Message}");
        }
    }

    private void BuildFromLayout(IUAObject owner, PhaseUILayoutRoot root, IUAObject buffer)
    {
        if (owner == null || root?.Sections == null) return;
        RecipeDatabaseTreeLoader.EnsurePhaseUiBufferModelVariables(DefaultPhaseBufferObjectPath);
        var rows = owner.Get("ScrollView1/Rows");
        if (rows == null)
        {
            Log.Error(nameof(BuildPhaseTemplate), "Target panel has no ScrollView1/Rows.");
            return;
        }

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

        AttachPhaseBufferDynamicLinks(owner, root, buffer);
    }

    private void AttachPhaseBufferDynamicLinks(IUAObject owner, PhaseUILayoutRoot root, IUAObject buffer)
    {
        if (owner == null || root?.Sections == null || buffer == null) return;

        int bindAttempts = 0;
        int bindSuccess = 0;
        int missingModelVars = 0;
        int missingWidgets = 0;

        bool BindText(IUAObject widget, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            bindAttempts++;
            var mv = buffer.GetVariable(key);
            if (mv == null) { missingModelVars++; return false; }
            TryDynamicLinkSingleParaText(widget, mv);
            bindSuccess++;
            return true;
        }

        bool BindSwitch(IUAObject widget, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            bindAttempts++;
            var mv = buffer.GetVariable(key);
            if (mv == null) { missingModelVars++; return false; }
            TryDynamicLinkValveSwitch(widget, mv);
            bindSuccess++;
            return true;
        }

        bool BindCombo(IUAObject widget, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            bindAttempts++;
            var mv = buffer.GetVariable(key);
            if (mv == null) { missingModelVars++; return false; }
            TryDynamicLinkComboSelectedValue(widget, mv);
            bindSuccess++;
            return true;
        }

        foreach (var sec in root.Sections)
        {
            if (sec?.Items == null) continue;
            string rp = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            foreach (var item in sec.Items)
            {
                if (string.IsNullOrEmpty(item?.Id)) continue;
                var n = owner.GetObject("ScrollView1/Rows/" + sec.Id + "/" + rp + "/" + item.Id);
                if (!(n is IUAObject widget))
                {
                    missingWidgets++;
                    continue;
                }

                string wt = item.WidgetType?.Trim() ?? "";
                if (string.Equals(wt, "PhaseSinglePara", StringComparison.OrdinalIgnoreCase))
                {
                    BindText(widget, item.BindKey);
                    continue;
                }
                if (string.Equals(wt, "PhaseValvePanel", StringComparison.OrdinalIgnoreCase))
                {
                    BindSwitch(widget, item.BindKey);
                    continue;
                }
                if (string.Equals(wt, "PhaseUserAck", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(wt, "PhaseAndorOr", StringComparison.OrdinalIgnoreCase))
                {
                    BindSwitch(widget, item.BindKeySwitch);
                    continue;
                }
                if (string.Equals(wt, "PhaseRunningTime", StringComparison.OrdinalIgnoreCase))
                {
                    BindSwitch(widget, item.BindKeySwitch);
                    BindText(widget, item.BindKeyText);
                    continue;
                }
                if (string.Equals(wt, "PhaseParaCompare1", StringComparison.OrdinalIgnoreCase))
                {
                    BindSwitch(widget, item.BindKeySwitch);
                    BindText(widget, item.BindKeyText);
                    continue;
                }
                if (string.Equals(wt, "PhaseParaCompare2", StringComparison.OrdinalIgnoreCase))
                {
                    BindSwitch(widget, item.BindKeySwitch);
                    BindText(widget, item.BindKeyText);
                    BindCombo(widget, item.BindKeyCombo);
                }
            }
        }

        Log.Info(nameof(BuildPhaseTemplate),
            $"DynamicLink summary: attempts={bindAttempts}, success={bindSuccess}, missingVars={missingModelVars}, missingWidgets={missingWidgets}");
    }

    private string ResolveTemplateJsonPath()
    {
        string configured = "";
        try
        {
            configured = Convert.ToString(LogicObject?.GetVariable("TemplateJsonFile")?.Value?.Value);
        }
        catch { configured = ""; }
        configured = configured?.Trim() ?? "";

        if (!string.IsNullOrEmpty(configured))
        {
            if (File.Exists(configured))
                return configured;
            // If a file name is provided, try existing resolver directories.
            string fromResolver = PhaseUILayoutJson.ResolveLayoutPath(configured);
            if (!string.IsNullOrEmpty(fromResolver))
                return fromResolver;
        }

        return PhaseUILayoutJson.ResolveLayoutPath(DefaultLayoutJsonFileName);
    }

    private IUAObject ResolveTargetPanel()
    {
        try
        {
            var v = LogicObject?.GetVariable("TargetPanel");
            if (v?.Value?.Value is NodeId nid)
            {
                var n = InformationModel.Get(nid);
                if (n is IUAObject obj) return obj;
            }
        }
        catch { }

        try
        {
            return Project.Current?.GetObject(DefaultTargetPanelPath);
        }
        catch
        {
            return null;
        }
    }

    private IUAObject ResolveBufferObject()
    {
        // 1) Property value as NodeId.
        try
        {
            var v = LogicObject?.GetVariable("TargetUIModelBufferLink");
            if (v?.Value?.Value is NodeId nid)
            {
                var n = InformationModel.Get(nid);
                if (n is IUAObject obj) return obj;
            }
        }
        catch { }

        // 2) Property dynamic link target as NodePath.
        try
        {
            var v = LogicObject?.GetVariable("TargetUIModelBufferLink");
            var dl = v?.GetVariable("DynamicLink");
            string nodePath = Convert.ToString(dl?.Value?.Value);
            if (!string.IsNullOrEmpty(nodePath))
            {
                var n = LogicObject?.Get(nodePath);
                if (n is IUAObject obj) return obj;
            }
        }
        catch { }

        // 3) Fallback default project path.
        try
        {
            return Project.Current?.GetObject(DefaultPhaseBufferObjectPath);
        }
        catch
        {
            return null;
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
            Log.Warning(nameof(BuildPhaseTemplate), $"DynamicLink Text↔{modelVar.BrowseName}: {ex.Message}");
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
            Log.Warning(nameof(BuildPhaseTemplate), $"DynamicLink Checked↔{modelVar.BrowseName}: {ex.Message}");
        }
    }

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
            Log.Warning(nameof(BuildPhaseTemplate), $"DynamicLink Combo SelectedValue↔{modelVar.BrowseName}: {ex.Message}");
        }
    }

    private static void ClearScrollRows(IUANode container)
    {
        if (container?.Children == null) return;
        var copy = new List<IUANode>();
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
}
