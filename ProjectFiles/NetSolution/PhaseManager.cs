#region Using directives
using System;
using System.Collections.Generic;
using System.Text;
using UAManagedCore;
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
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.WebUI;
#endregion

public class PhaseManager : BaseNetLogic
{
    public static PhaseManager Instance { get; private set; }
    private const string UdtPhaseTemplateUiBufferRootPath = "Model/UIData/PhaseData/UDT_PhaseTemplateUIBuffer1";
    private const string TestTextTargetVariablePath = UdtPhaseTemplateUiBufferRootPath + "/PP/FixedSetPointValue";
    private const string ScrollRowsPath = "Background/ScrollView1/Rows";

    public override void Start()
    {
        Instance = this;
    }

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
                {
                    LogPhaseInputFirstChar(logTag, nv);
                    if (RecipeDatabaseTreeLoader.Instance != null && RecipeDatabaseTreeLoader.Instance.IsPhaseUdtTemplateLoading)
                        return;
                    RecipeDatabaseManager.Instance?.NotifyPhaseParameterBufferEdited();
                });
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
        if (owner.Get(ScrollRowsPath) == null) return;
        foreach (var sec in root.Sections)
        {
            if (sec?.Items == null) continue;
            string rp = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            foreach (var item in sec.Items)
            {
                if (string.IsNullOrEmpty(item?.Id)) continue;
                var w = owner.GetObject(ScrollRowsPath + "/" + sec.Id + "/" + rp + "/" + item.Id) as IUAObject;
                RegisterPhaseParaValueEditors(w, sec.Id + "/" + item.Id);

                if (WidgetTypeIs(item.WidgetType, "PanelEndConditionGroup"))
                    RegisterEndConditionUnitLabelObserver(w, item, sec.Id + "/" + item.Id);
            }
        }
    }

    /// <summary>按 JSON 中每项 unit 配置，在下拉选项变更时更新 SelectItemTemplatePanel 的单位标签。</summary>
    private void RegisterEndConditionUnitLabelObserver(IUAObject groupWidget, PhaseUILayoutItem item, string logTag)
    {
        if (groupWidget == null || item == null) return;

        var unitMap = BuildEndConditionUnitMap(item);
        if (unitMap.Count == 0) return;

        var comboBox = groupWidget.Get<ComboBox>("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/ComboBox1");
        var unitLabel = groupWidget.Get<Label>("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/SelectItemTemplatePanel1/HL/Unit");
        var selectedValueVar = comboBox?.GetVariable("SelectedValue");
        if (selectedValueVar == null || unitLabel == null) return;

        ApplyEndConditionUnitText(unitLabel, selectedValueVar.Value, unitMap);

        try
        {
            var obs = new CallbackVariableChangeObserver((iv, nv, ov, access, sender) =>
                ApplyEndConditionUnitText(unitLabel, nv, unitMap));
            _phaseInputRegs.Add(selectedValueVar.RegisterEventObserver(obs, EventType.VariableValueChanged, _phaseInputAffinityId));
        }
        catch (Exception ex)
        {
            Log.Warning(nameof(PhaseManager), $"EndCondition 单位标签订阅失败 {logTag}: {ex.Message}");
        }
    }

    private static Dictionary<int, string> BuildEndConditionUnitMap(PhaseUILayoutItem item)
    {
        var map = new Dictionary<int, string>();
        if (item?.Config?.ConditionSelector?.Items == null) return map;

        foreach (var option in item.Config.ConditionSelector.Items)
        {
            if (option == null) continue;
            map[option.Value] = option.Unit ?? string.Empty;
        }

        return map;
    }

    private static void ApplyEndConditionUnitText(Label unitLabel, UAValue selectedValue, Dictionary<int, string> unitMap)
    {
        if (unitLabel == null || unitMap == null) return;

        int selected = 0;
        try
        {
            if (selectedValue?.Value != null)
                selected = Convert.ToInt32(selectedValue.Value);
        }
        catch
        {
            selected = 0;
        }

        unitMap.TryGetValue(selected, out string unitText);
        unitLabel.Text = unitText ?? string.Empty;
    }
    #endregion
    [ExportMethod]
    public void RedrawPhaseParameterUI()
    {
        int selectedPhaseId = GenerateTreeList.Instance?.SelectedPhaseId ?? 0;
        if (selectedPhaseId > 0)
            RecipeDatabaseTreeLoader.Instance?.LoadPhaseParametersToUdtTemplateBuffer(selectedPhaseId);

        EnsurePhaseInputAffinity();
        if (!(Owner is IUAObject ownerObj)) return;

        var jsonVar = LogicObject.GetVariable("JsonFile");
        string jsonPath = PhaseUILayoutJson.ResolveJsonFilePathFromVariable(jsonVar);
        var layout = !string.IsNullOrEmpty(jsonPath) ? PhaseUILayoutJson.TryLoadFromFile(jsonPath) : null;
        if (layout == null)
        {
            string hint = DescribeJsonFileVariableForLog(jsonVar);
            Log.Warning(nameof(PhaseManager),
                "无法加载相位 UI 布局 JSON（JsonFile 路径无效或文件不存在）。详情: " + hint);
            return;
        }

        try
        {
            // 当前阶段仅按 JSON 生成 UI，不依赖 UDT 缓冲；DynamicLink 绑定后续再接。
            BuildPhaseUiFromLayout(ownerObj, layout);
        }
        catch (Exception ex)
        {
            Log.Error(nameof(PhaseManager), "构建相位 UI 失败: " + ex.Message);
        }
    }


    /// <summary>记录 JsonFile 变量当前值类型与摘要，便于排查路径解析失败。</summary>
    private static string DescribeJsonFileVariableForLog(IUAVariable jsonVar)
    {
        if (jsonVar == null) return "JsonFile 变量不存在";
        try
        {
            if (jsonVar.Value == null || jsonVar.Value.Value == null)
                return "JsonFile 值为空";
            object v = jsonVar.Value.Value;
            string typeName = v.GetType().FullName ?? v.GetType().Name;
            string sample = Convert.ToString(v);
            if (!string.IsNullOrEmpty(sample) && sample.Length > 120)
                sample = sample.Substring(0, 120) + "…";
            return typeName + " → " + sample;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    #region Phase JSON 构建与 UDT_Phase 绑定
    private void BuildPhaseUiFromLayout(IUAObject owner, PhaseUILayoutRoot root)
    {
        if (owner == null || root?.Sections == null) return;
        ValidateUniqueItemIdsOrThrow(root);
        var rows = owner.Get(ScrollRowsPath);
        if (rows == null) return;
        if (rows is ColumnLayout rowsLayout)
            rowsLayout.VerticalAlignment = VerticalAlignment.Top;
        ClearPhaseInputRegs();
        ClearScrollRows(rows);

        foreach (var sec in root.Sections)
        {
            if (string.IsNullOrEmpty(sec?.Id)) continue;
            // 与 PhaseParaTemplateUnit 中对象类型名一致（如 PhaseParasPanel1）
            if (!PanelTypeIsPhaseParas(sec.PanelType))
                continue;

            var panel = InformationModel.Make<PhaseParasPanel>(sec.Id);
            panel.VerticalAlignment = VerticalAlignment.Top;
            // 分区级垂直间距：由 JSON sectionVerticalGap 控制
            if (sec.SectionVerticalGap.HasValue)
                panel.TopMargin = sec.SectionVerticalGap.Value;
            var titleLabel = panel.Get<Label>("BG/Rectangle1/Title");
            if (titleLabel != null && !string.IsNullOrEmpty(sec.Title))
                titleLabel.Text = sec.Title;

            string rowPath = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            var rowLayout = panel.Get<RowLayout>(rowPath);
            if (rowLayout == null) continue;
            float? rowGap = ResolveSectionRowHorizontalGap(sec);
            if (rowGap.HasValue)
                rowLayout.HorizontalGap = rowGap.Value;

            if (sec.Items != null)
            {
                foreach (var item in sec.Items)
                    AddLayoutWidget(rowLayout, item);
            }

            rows.Add(panel);
        }

        AttachUdtPhaseBufferBinds(owner, root);
        WireLayoutPhaseInputObservers(owner, root);
    }

    /// <summary>校验每个 Section 内 item.id 唯一，重复时抛异常，避免运行时节点寻址冲突。</summary>
    private static void ValidateUniqueItemIdsOrThrow(PhaseUILayoutRoot root)
    {
        if (root?.Sections == null) return;
        foreach (var sec in root.Sections)
        {
            if (sec == null || string.IsNullOrEmpty(sec.Id) || sec.Items == null) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in sec.Items)
            {
                if (item == null) continue;
                string itemId = item.Id?.Trim();
                if (string.IsNullOrEmpty(itemId))
                    throw new InvalidOperationException($"布局错误：section={sec.Id} 存在空 item.id。");
                if (!seen.Add(itemId))
                    throw new InvalidOperationException($"布局错误：section={sec.Id} 内 item.id 重复：{itemId}。");
            }
        }
    }

    /// <summary>解析 sourceTagPath（如 PP.FixedSetPointValue[0]）为字段路径与可选数组索引。</summary>
    private static bool TryParseSourceTagPath(string sourceTagPath, out string bufferFieldPath, out int? arrayIndex, out string error)
    {
        bufferFieldPath = null;
        arrayIndex = null;
        error = null;

        if (string.IsNullOrWhiteSpace(sourceTagPath))
        {
            error = "sourceTagPath 为空。";
            return false;
        }

        string raw = sourceTagPath.Trim();
        int lb = raw.LastIndexOf('[');
        int rb = raw.LastIndexOf(']');

        if (lb < 0 && rb < 0)
        {
            bufferFieldPath = raw;
            return true;
        }

        if (lb < 0 || rb < 0 || rb <= lb || rb != raw.Length - 1)
        {
            error = "sourceTagPath 索引格式非法: " + raw;
            return false;
        }

        string idxText = raw.Substring(lb + 1, rb - lb - 1).Trim();
        if (!int.TryParse(idxText, out int idx) || idx < 0)
        {
            error = "sourceTagPath 索引不是非负整数: " + raw;
            return false;
        }

        string path = raw.Substring(0, lb).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "sourceTagPath 字段路径为空: " + raw;
            return false;
        }

        bufferFieldPath = path;
        arrayIndex = idx;
        return true;
    }


    /// <summary>按 JSON <c>bind.sourceTagPath</c> 将控件绑定到 UDT_Phase 成员；带 [index] 时绑定数组元素。</summary>
    private void AttachUdtPhaseBufferBinds(IUAObject owner, PhaseUILayoutRoot root)
    {
        if (owner == null || root?.Sections == null) return;
        if (owner.Get(ScrollRowsPath) == null) return;

        foreach (var sec in root.Sections)
        {
            if (sec?.Items == null) continue;
            string rp = string.IsNullOrEmpty(sec.RowLayoutPath) ? "VL/HL" : sec.RowLayoutPath;
            foreach (var item in sec.Items)
            {
                if (string.IsNullOrEmpty(item?.Id)) continue;

                var widget = owner.GetObject(ScrollRowsPath + "/" + sec.Id + "/" + rp + "/" + item.Id) as IUAObject;
                if (widget == null)
                    throw new InvalidOperationException($"绑定失败 {sec.Id}/{item.Id}: 未找到 UI 控件节点。");

                // EndCondition 组内联动：Enable 未勾选时隐藏下方选择区域。
                if (WidgetTypeIs(item.WidgetType, "PanelEndConditionGroup"))
                    WireEndConditionSelectionVisibility(widget);

                foreach (var slotEntry in EnumerateSlotBindSpecs(item))
                    AttachUdtPhaseBufferBind(widget, item.WidgetType, slotEntry.Value, sec.Id, item.Id, slotEntry.Key);

                foreach (var configBindEntry in EnumeratePanelEndConditionGroupConfigBindSpecs(item))
                    AttachUdtPhaseBufferBind(widget, item.WidgetType, configBindEntry.Value, sec.Id, item.Id, configBindEntry.Key);

                foreach (var bindSpec in EnumerateBindSpecs(item))
                    AttachUdtPhaseBufferBind(widget, item.WidgetType, bindSpec, sec.Id, item.Id);

            }
        }
    }

    private static void AttachUdtPhaseBufferBind(IUAObject widget, string widgetType, PhaseUILayoutBindSpec bindSpec, string sectionId, string itemId, string slotKey = null)
    {
        if (bindSpec == null) return;
        if (string.IsNullOrWhiteSpace(bindSpec.SourceTagPath))
            throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: bind.sourceTagPath 为空。");

        if (!TryParseSourceTagPath(bindSpec.SourceTagPath, out string bufferFieldPath, out int? parsedIndex, out string parseError))
            throw new InvalidOperationException($"绑定失败，index解析失败 {sectionId}/{itemId}: {parseError}");

        string modelVarPath = UdtPhaseTemplateUiBufferRootPath + "/" + bufferFieldPath.Replace('.', '/');
        IUAVariable modelVar = Project.Current.GetVariable(modelVarPath);
        if (modelVar == null && parsedIndex.HasValue)
        {
            modelVar = Project.Current.GetVariable(modelVarPath + "[" + parsedIndex.Value + "]");
            if (modelVar == null)
                modelVar = Project.Current.GetVariable(modelVarPath + "/" + parsedIndex.Value);
        }
        if (modelVar == null)
            throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: 未找到模型变量 {modelVarPath}" + (parsedIndex.HasValue ? $"（index={parsedIndex.Value}）" : "") + "。");

        IUAVariable uiVar = ResolveUiVariable(widget, widgetType, bindSpec, sectionId, itemId, slotKey);
        if (uiVar == null)
            throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: 未找到 UI 属性变量 {bindSpec.UiProperty}。");

        uiVar.ResetDynamicLink();
        if (parsedIndex.HasValue)
            uiVar.SetDynamicLink(modelVar, (uint)parsedIndex.Value, DynamicLinkMode.ReadWrite);
        else
            uiVar.SetDynamicLink(modelVar, DynamicLinkMode.ReadWrite);
    }

    /// <summary>兼容旧版单 bind 与新版 binds[]。</summary>
    private static IEnumerable<PhaseUILayoutBindSpec> EnumerateBindSpecs(PhaseUILayoutItem item)
    {
        if (item == null) yield break;
        if (item.Bind != null) yield return item.Bind;
        if (item.Binds == null) yield break;
        foreach (var spec in item.Binds)
            if (spec != null) yield return spec;
    }

    private static IEnumerable<KeyValuePair<string, PhaseUILayoutBindSpec>> EnumerateSlotBindSpecs(PhaseUILayoutItem item)
    {
        var bindings = item?.Bindings;
        if (bindings == null) yield break;
        foreach (var entry in bindings)
            if (entry.Value != null) yield return entry;
    }

    private static IEnumerable<KeyValuePair<string, PhaseUILayoutBindSpec>> EnumeratePanelEndConditionGroupConfigBindSpecs(PhaseUILayoutItem item)
    {
        if (item?.Config?.ConditionSelector?.Items == null) yield break;
        foreach (var option in item.Config.ConditionSelector.Items)
        {
            if (option?.Bindings == null) continue;
            foreach (var entry in option.Bindings)
            {
                if (entry.Value == null) continue;
                if (!IsSupportedPanelEndConditionGroupBindingKey(entry.Key)) continue;
                yield return entry;
            }
        }
    }

    private static bool IsSupportedPanelEndConditionGroupBindingKey(string bindingKey)
    {
        if (string.IsNullOrWhiteSpace(bindingKey)) return false;
        return string.Equals(bindingKey, "timeHr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "timeMin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "timeSec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "weightOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "weightValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "phOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "phValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "oxygenOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "oxygenValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "tempOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindingKey, "tempValue", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UiBindingGuide
    {
        public UiBindingGuide(string uiPath, string uiProperty)
        {
            UiPath = uiPath;
            UiProperty = uiProperty;
        }

        public string UiPath { get; }
        public string UiProperty { get; }
    }

    private static UiBindingGuide GetDefaultUiBindingGuide(string widgetType, string slotKey = null)
    {
        if (WidgetTypeIs(widgetType, "PhaseSinglePara"))
            return new UiBindingGuide("VerticalLayout1/ParaValue/TextBox1", "Text");
        if (WidgetTypeIs(widgetType, "PhaseValvePanel"))
            return new UiBindingGuide("VerticalLayout1/ParaValue/Switch1", "Checked");
        if (WidgetTypeIs(widgetType, "PanelEndConditionsOperator"))
            return new UiBindingGuide("Rectangle_Border/Switch_AndOr", "Checked");
        if (WidgetTypeIs(widgetType, "PanelEndConditionGroup"))
            return GetPanelEndConditionGroupSlotGuide(slotKey);
        return null;
    }

    private static UiBindingGuide GetPanelEndConditionGroupSlotGuide(string slotKey)
    {
        if (string.IsNullOrWhiteSpace(slotKey)) return null;
        if (string.Equals(slotKey, "enableSwitch", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionsEnable/Rectangle_Border/HorizontalLayout1/Switch_Vlv1", "Checked");
        if (string.Equals(slotKey, "conditionSelector", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/ComboBox1", "SelectedValue");
        if (string.Equals(slotKey, "timeHr", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/TimeContainer/SpinBox_Hr", "Value");
        if (string.Equals(slotKey, "timeMin", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/TimeContainer/SpinBox_Min", "Value");
        if (string.Equals(slotKey, "timeSec", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/TimeContainer/SpinBox_Sec", "Value");
        if (string.Equals(slotKey, "weightOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "phOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "oxygenOperator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "tempOperator", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/SelectItemTemplatePanel1/HL/Switch", "Checked");
        if (string.Equals(slotKey, "weightValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "phValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "oxygenValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotKey, "tempValue", StringComparison.OrdinalIgnoreCase))
            return new UiBindingGuide("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/SelectItemTemplatePanel1/HL/SpinBox", "Value");
        return null;
    }

    /// <summary>按显式 bind 或组件默认映射解析目标 UI 变量。</summary>
    private static IUAVariable ResolveUiVariable(IUAObject widget, string widgetType, PhaseUILayoutBindSpec bindSpec, string sectionId, string itemId, string slotKey = null)
    {
        if (widget == null || bindSpec == null) return null;
        var defaultGuide = GetDefaultUiBindingGuide(widgetType, slotKey);
        string uiPath = !string.IsNullOrWhiteSpace(bindSpec.UiPath)
            ? bindSpec.UiPath.Trim()
            : defaultGuide?.UiPath;
        string uiProperty = !string.IsNullOrWhiteSpace(bindSpec.UiProperty)
            ? bindSpec.UiProperty.Trim()
            : defaultGuide?.UiProperty;
        if (string.IsNullOrEmpty(uiProperty))
            throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: bind.uiProperty 为空，且 widgetType={widgetType} 未配置默认 UI 映射。");

        IUAObject scopeObject = widget;
        if (!string.IsNullOrWhiteSpace(uiPath))
        {
            var scopeNode = widget.Get(uiPath);
            if (scopeNode == null)
                throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: 未找到 uiPath={uiPath}。");
            if (scopeNode is IUAVariable scopeVar)
            {
                if (string.IsNullOrEmpty(uiProperty) || string.Equals(uiProperty, scopeVar.BrowseName, StringComparison.OrdinalIgnoreCase))
                    return scopeVar;
                throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: uiPath 指向变量，但 uiProperty={uiProperty} 不匹配。");
            }
            scopeObject = scopeNode as IUAObject;
            if (scopeObject == null)
                throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: uiPath 目标不是对象节点。");
        }

        if (string.IsNullOrWhiteSpace(uiPath))
        {
            if (string.Equals(uiProperty, "Text", StringComparison.OrdinalIgnoreCase))
            {
                var targetTextBox = FindFirstDescendant<TextBox>(scopeObject);
                if (targetTextBox == null)
                    throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: uiProperty=Text，但在控件及其子节点中未找到 TextBox。");
                return targetTextBox.GetVariable("Text");
            }
            if (string.Equals(uiProperty, "Checked", StringComparison.OrdinalIgnoreCase))
            {
                var targetSwitch = FindFirstDescendant<Switch>(scopeObject);
                if (targetSwitch == null)
                    throw new InvalidOperationException($"绑定失败 {sectionId}/{itemId}: uiProperty=Checked，但在控件及其子节点中未找到 Switch。");
                return targetSwitch.GetVariable("Checked");
            }
        }

        return scopeObject.GetVariable(uiProperty);
    }

    /// <summary>将 PanelEndConditionSelection1.Visible 绑定到组内 Enable 开关 Checked。</summary>
    private static void WireEndConditionSelectionVisibility(IUAObject groupWidget)
    {
        if (groupWidget == null) return;
        var enableSwitch = groupWidget.GetObject("VL/PanelEndConditionsEnable/Rectangle_Border/HorizontalLayout1/Switch_Vlv1") as Switch;
        var selectionPanel = groupWidget.GetObject("VL/PanelEndConditionSelection") as IUAObject;
        var checkedVar = enableSwitch?.GetVariable("Checked");
        var visibleVar = selectionPanel?.GetVariable("Visible");
        if (checkedVar == null || visibleVar == null) return;
        visibleVar.ResetDynamicLink();
        visibleVar.SetDynamicLink(checkedVar, DynamicLinkMode.ReadWrite);
    }

    /// <summary>深度优先遍历节点树，返回第一个匹配类型的节点。</summary>
    private static T FindFirstDescendant<T>(IUANode node) where T : class, IUANode
    {
        if (node == null) return null;
        if (node is T match) return match;
        if (node.Children == null) return null;

        foreach (var child in node.Children)
        {
            var found = FindFirstDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }


    private static void ClearScrollRows(IUANode container)
    {
        if (container?.Children == null) return;
        var copy = new System.Collections.Generic.List<IUANode>();
        foreach (var c in container.Children) copy.Add(c);
        foreach (var c in copy) c.Delete();
    }

    /// <summary>兼容 rowLayoutHorizontalGap / rowHorizontalGap 两种 JSON 写法。</summary>
    private static float? ResolveSectionRowHorizontalGap(PhaseUILayoutSection sec)
    {
        if (sec == null) return null;
        if (sec.RowLayoutHorizontalGap.HasValue) return sec.RowLayoutHorizontalGap.Value;
        if (sec.RowHorizontalGap.HasValue) return sec.RowHorizontalGap.Value;
        return null;
    }

    /// <summary>JSON 中可写 PhaseParasPanel 或 PhaseParasPanel1。</summary>
    private static bool PanelTypeIsPhaseParas(string panelType)
    {
        if (string.IsNullOrEmpty(panelType)) return false;
        return string.Equals(panelType, "PhaseParasPanel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(panelType, "PhaseParasPanel1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>JSON 中可写 PhaseSinglePara 或 PhaseSinglePara1（与 Optix 类型后缀一致）。</summary>
    private static bool WidgetTypeIs(string t, string logicalName)
    {
        if (string.IsNullOrEmpty(t)) return false;
        return string.Equals(t, logicalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, logicalName + "1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>比较控件：JSON 中 PhaseParaCompare1/2 对应模板类型 PhaseParaCompare3/4。</summary>
    private static bool WidgetTypeIsParaCompare(string t, int oneOrTwo)
    {
        if (string.IsNullOrEmpty(t)) return false;
        if (oneOrTwo == 1)
            return string.Equals(t, "PhaseParaCompare1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "PhaseParaCompare3", StringComparison.OrdinalIgnoreCase);
        return string.Equals(t, "PhaseParaCompare2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "PhaseParaCompare4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按 JSON 重建 ComboBox 的 EndConditionItems 选项变量；下拉下方的 SelectItemTemplatePanel 由 Optix 模板静态实例提供，此处不再创建/删除面板。
    /// </summary>
    private static void RebuildEndConditionItems(IUAObject groupWidget, PhaseUILayoutItem item)
    {
        if (groupWidget == null || item?.Config?.ConditionSelector?.Items == null) return;

        var endConditionItems = groupWidget.GetObject("EndConditionItems") as IUAObject;
        if (endConditionItems == null) return;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var oldChildren = new List<IUANode>();
        if (endConditionItems.Children != null)
        {
            foreach (var child in endConditionItems.Children)
                oldChildren.Add(child);
        }
        foreach (var child in oldChildren)
            child.Delete();

        AddEmptyConditionSelectorOption(endConditionItems, usedNames);

        foreach (var option in item.Config.ConditionSelector.Items)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.Label))
                continue;

            string safeName = MakeUniqueNodeName(usedNames, option.Label);
            int numericValue = option.Value;
            var variable = InformationModel.MakeVariable(safeName, UAManagedCore.OpcUa.DataTypes.Int32);
            variable.Description = new LocalizedText(option.Label);
            variable.DisplayName = new LocalizedText(option.Label);
            variable.Value = numericValue;
            endConditionItems.Add(variable);
        }
    }

    private static void AddEmptyConditionSelectorOption(IUAObject endConditionItems, ISet<string> usedNames)
    {
        if (endConditionItems == null || usedNames == null) return;

        string safeName = MakeUniqueNodeName(usedNames, "Empty");
        var variable = InformationModel.MakeVariable(safeName, UAManagedCore.OpcUa.DataTypes.Int32);
        variable.Description = new LocalizedText(string.Empty);
        variable.DisplayName = new LocalizedText(string.Empty);
        variable.Value = -1;
        endConditionItems.Add(variable);
    }

    private static string MakeSafeNodeName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Item";

        string raw = text.Trim()
            .Replace(" ", "_")
            .Replace("/", "_")
            .Replace("\\", "_")
            .Replace(":", "_");

        var builder = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                builder.Append(ch);
        }

        string safe = builder.ToString().Trim('_');

        return string.IsNullOrEmpty(safe) ? "Item" : safe;
    }

    private static string MakeUniqueNodeName(ISet<string> usedNames, string text)
    {
        string baseName = MakeSafeNodeName(text);
        string candidate = baseName;
        int index = 2;

        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName}_{index}";
            index++;
        }

        return candidate;
    }

    private static void AddLayoutWidget(RowLayout rowLayout, PhaseUILayoutItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.WidgetType)) return;
        string t = item.WidgetType.Trim();

        if (WidgetTypeIs(t, "PhaseSinglePara"))
        {
            var w = InformationModel.Make<PhaseSinglePara>(item.Id);
            string lbl = !string.IsNullOrEmpty(item.label) ? item.label : item.Id;
            var label = w.Get<Label>("VerticalLayout1/ParaName/Label1");
            if (label != null) label.Text = lbl;
            rowLayout.Add(w);
            return;
        }
        if (WidgetTypeIsParaCompare(t, 2))
        {
            rowLayout.Add(InformationModel.Make<PhaseParaCompare>(item.Id));
            return;
        }
        if (WidgetTypeIs(t, "PanelEndConditionsOperator"))
        {
            rowLayout.Add(InformationModel.Make<PanelEndConditionsOperator>(item.Id));
            return;
        }
        if (WidgetTypeIs(t, "PanelEndConditionGroup"))
        {
            var w = InformationModel.Make<PanelEndConditionGroup>(item.Id);
            if (item.Width.HasValue) w.Width = item.Width.Value;
            int groupNo = ExtractTrailingNumberOrDefault(item.Id, 1);
            var enableLabel = w.Get<Label>("VL/PanelEndConditionsEnable/Rectangle_Border/HorizontalLayout1/LabelValve1");
            if (enableLabel != null) enableLabel.Text = "EC" + groupNo;
            var titleLabel = w.Get<Label>("VL/PanelEndConditionSelection/Rectangle_Border/Panel2/Label1");
            if (titleLabel != null) titleLabel.Text = $"End Condition {groupNo} ( EC {groupNo} )";
            RebuildEndConditionItems(w, item);
            rowLayout.Add(w);
            return;
        }
        if (WidgetTypeIs(t, "PhaseValvePanel"))
        {
            var w = InformationModel.Make<PhaseValvePanel>(item.Id);
            string lbl = !string.IsNullOrEmpty(item.label) ? item.label : item.Id;
            var label = w.Get<Label>("VerticalLayout1/ParaName/Label1");
            if (label != null) label.Text = lbl;
            rowLayout.Add(w);
        }
    }

    /// <summary>从节点名尾部提取正整数序号；失败时返回默认值。</summary>
    private static int ExtractTrailingNumberOrDefault(string text, int fallback)
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        int i = text.Length - 1;
        while (i >= 0 && char.IsDigit(text[i])) i--;
        if (i == text.Length - 1) return fallback;
        string n = text.Substring(i + 1);
        return int.TryParse(n, out int v) && v > 0 ? v : fallback;
    }
    #endregion

    public override void Stop()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
        ClearPhaseInputRegs();
        _phaseInputAffinityId = 0;
    }
}
