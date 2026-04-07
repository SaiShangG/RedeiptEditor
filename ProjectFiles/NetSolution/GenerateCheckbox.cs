#region Using directives
using System;
using System.Collections.Generic;
using System.Reflection;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

/// <summary>
/// 在 SearchState 的 CheckboxContainer 内按标签动态创建 <c>NewCheckBox</c> 组件实例（子节点 <c>Checkbox1</c> 的勾选与文案），并与模型 <c>FilterConditions/SearchByCategoryData/TypeChecked</c> 同步。
/// 容器路径优先 <c>VerticalLayout1/ScrollView1/CheckboxContainer</c>，兼容旧版 <c>VerticalLayout1/CheckboxContainer</c>。
/// 标签来自 NetLogic 变量 <c>CheckboxData</c>：在 Studio 中通常动态链接到 <c>{StateOrTypeSelect}/CheckBoxCategory</c>（Batch/Recipe/Audit 项数与文案不同）。
/// <c>SearchByCategoryData</c> 使用固定 10 槽位；勾选与 <c>TypeChecked[i]</c> 对齐，Batch/Recipe/Audit 有效项数由当前 <c>CheckBoxCategory</c> 长度决定。
/// 链接在 <c>Start()</c> 之后解析时，会通过对 <c>CheckboxData</c> 的值变化订阅自动重建列表。
/// </summary>
public class GenerateCheckbox : BaseNetLogic
{
    /// <summary>避免依赖 C# 7+ 元组语法，供 Optix NetHelper 等旧版编译器通过。</summary>
    private struct CheckboxSlot
    {
        public int SourceIndex;
        public string Label;
    }

    private const string LogCategory = "GenerateCheckbox";
    private const string PathSearchByCategoryData = "Model/UIData/FilterData/FilterConditions/SearchByCategoryData";

    /// <summary>与模型 <c>TypeLabels</c>/<c>TypeChecked</c> 数组长度一致（通用槽位上限）。</summary>
    private const int FilterCategoryCapacity = 10;

    private static readonly string[] DefaultBatchStateLabels =
    {
        "Idle", "Pause", "Resume", "Start", "Stop", "Running", "Paused", "Safe Restart", "Abort", "Completed"
    };

    private IUAVariable _typeCheckedVariable;
    private IEventRegistration _checkboxDataRegistration;
    private readonly List<IEventRegistration> _checkedRegistrations = new List<IEventRegistration>();
    private uint _affinityId;
    private bool _regenerating;

    public override void Start()
    {
        try
        {
            _affinityId = LogicObject.Context.AssignAffinityId();
            var dataVar = LogicObject.GetVariable("CheckboxData");
            if (dataVar != null)
            {
                var dataObs = new CallbackVariableChangeObserver(OnCheckboxDataVariableChanged);
                _checkboxDataRegistration = dataVar.RegisterEventObserver(
                    dataObs, EventType.VariableValueChanged, _affinityId);
            }
            RegenerateCheckboxesInternal();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"Start: {ex.Message}");
        }
    }

    public override void Stop()
    {
        _checkboxDataRegistration?.Dispose();
        _checkboxDataRegistration = null;
        foreach (var r in _checkedRegistrations)
            r?.Dispose();
        _checkedRegistrations.Clear();
        _typeCheckedVariable = null;
        _affinityId = 0;
    }

    private void OnCheckboxDataVariableChanged(
        IUAVariable variable,
        UAValue newValue,
        UAValue oldValue,
        ElementAccess elementAccess,
        ulong senderId)
    {
        if (_regenerating) return;
        try
        {
            RegenerateCheckboxesInternal();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"OnCheckboxDataVariableChanged: {ex.Message}");
        }
    }

    /// <summary>可在 Prepare 之后由事件再次调用，以刷新选项（需先在 Studio 同步方法）。</summary>
    [ExportMethod]
    public void RegenerateCheckboxes()
    {
        try
        {
            if (_affinityId == 0)
                _affinityId = LogicObject.Context.AssignAffinityId();
            RegenerateCheckboxesInternal();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"RegenerateCheckboxes: {ex.Message}");
        }
    }

    private void RegenerateCheckboxesInternal()
    {
        if (_regenerating) return;
        _regenerating = true;
        try
        {
            foreach (var r in _checkedRegistrations)
                r?.Dispose();
            _checkedRegistrations.Clear();

            var panel = Owner as Item;
            if (panel == null)
            {
                Log.Warning(LogCategory, "Owner is not an Item (SearchState panel expected).");
                return;
            }

            RowLayout container = ResolveCheckboxContainer(panel);
            if (container == null)
            {
                Log.Warning(LogCategory,
                    "未找到 CheckboxContainer（已尝试 VerticalLayout1/ScrollView1/CheckboxContainer 与 VerticalLayout1/CheckboxContainer）。");
                return;
            }

            var project = Project.Current;
            if (project == null)
            {
                Log.Warning(LogCategory, "Project.Current is null.");
                return;
            }

            _typeCheckedVariable = ResolveSearchByCategoryTypeCheckedVariable(project);
            if (_typeCheckedVariable == null)
                Log.Warning(LogCategory,
                    "SearchByCategoryData/TypeChecked 未找到。请在 FilterConditions/SearchByCategoryData 下配置 TypeChecked(Boolean[" + FilterCategoryCapacity.ToString() + "])。");

            List<CheckboxSlot> slots = ReadCheckboxDataSlots(LogicObject.GetVariable("CheckboxData"));
            if (slots.Count == 0)
            {
                for (int d = 0; d < DefaultBatchStateLabels.Length; d++)
                {
                    slots.Add(new CheckboxSlot
                    {
                        SourceIndex = d,
                        Label = DefaultBatchStateLabels[d]
                    });
                }
            }

            NodeId newCheckBoxTypeId = FindCustomTypeNodeId(project, "NewCheckBox");
            if (newCheckBoxTypeId == NodeId.Empty)
            {
                Log.Error(LogCategory, "未在项目中找到 UI 类型 NewCheckBox（Components 下模板），无法生成复选框。");
                return;
            }

            RemoveLegacyCheckboxRows(container);

            // 与类型设计一致：多列换行；若未开启 Wrap，子项会挤在同一行。
            try
            {
                var wrapVar = container.GetVariable("Wrap");
                if (wrapVar != null) wrapVar.Value = true;
            }
            catch { }

            int maxSlot = FilterCategoryCapacity;
            for (int si = 0; si < slots.Count; si++)
            {
                if (slots[si].SourceIndex + 1 > maxSlot)
                    maxSlot = slots[si].SourceIndex + 1;
            }
            if (maxSlot > FilterCategoryCapacity)
                maxSlot = FilterCategoryCapacity;
            var currentChecked = ReadBoolArrayFromVariable(_typeCheckedVariable, maxSlot);
            string localeId = Session?.User?.LocaleId ?? "en-US";

            for (int si = 0; si < slots.Count; si++)
            {
                CheckboxSlot slot = slots[si];
                int sourceIndex = slot.SourceIndex;
                if (sourceIndex < 0 || sourceIndex >= FilterCategoryCapacity)
                    continue;
                string labelTrim = slot.Label != null ? slot.Label.Trim() : "";
                if (labelTrim.Length == 0)
                    continue;

                var row = InformationModel.MakeObject("DynCheckRow_" + sourceIndex.ToString(), newCheckBoxTypeId) as Rectangle;
                if (row == null)
                {
                    Log.Warning(LogCategory, $"MakeObject NewCheckBox 失败: index={sourceIndex}");
                    continue;
                }

                // RowLayout 子项若 HorizontalAlignment=Stretch，会沿横向被均分压缩，类型上的 Width/Height 无法按「整块」显示。
                ApplyNewCheckBoxRowLayout(row);

                var innerCb = row.GetObject("Checkbox1") as CheckBox;
                if (innerCb == null)
                {
                    Log.Warning(LogCategory, "NewCheckBox 实例缺少子节点 Checkbox1。");
                    row.Delete();
                    continue;
                }

                ApplyCheckboxLabel(innerCb, labelTrim, localeId, sourceIndex);

                var checkedVar = innerCb.GetVariable("Checked");
                if (checkedVar != null)
                {
                    bool initial = sourceIndex < currentChecked.Length && currentChecked[sourceIndex];
                    checkedVar.Value = initial;
                    int index = sourceIndex;
                    if (_typeCheckedVariable != null && _affinityId != 0)
                    {
                        var obs = new CallbackVariableChangeObserver((v, nv, ov, access, sender) =>
                            OnCheckboxCheckedChanged(index, nv));
                        var reg = checkedVar.RegisterEventObserver(obs, EventType.VariableValueChanged, _affinityId);
                        _checkedRegistrations.Add(reg);
                    }
                }

                container.Add(row);
            }
        }
        finally
        {
            _regenerating = false;
        }
    }

    /// <summary>
    /// 当前面板结构：<c>VerticalLayout1 → ScrollView1 → CheckboxContainer</c>；
    /// 若无 ScrollView，则使用旧路径 <c>VerticalLayout1 → CheckboxContainer</c>。
    /// </summary>
    private static RowLayout ResolveCheckboxContainer(Item panel)
    {
        if (panel == null)
            return null;

        var outerVl = panel.GetObject("VerticalLayout1") as IUAObject;
        if (outerVl == null)
            return null;

        var scroll = outerVl.GetObject("ScrollView1") as IUAObject;
        if (scroll != null)
        {
            var underScroll = scroll.GetObject("CheckboxContainer") as RowLayout;
            if (underScroll != null)
                return underScroll;
        }

        return outerVl.GetObject("CheckboxContainer") as RowLayout;
    }

    /// <summary>与 NewCheckBox 类型默认一致：固定宽高 + 不拉伸，以便 RowLayout+Wrap 按块换行。</summary>
    private static void ApplyNewCheckBoxRowLayout(Rectangle row)
    {
        if (row == null) return;
        try
        {
            row.HorizontalAlignment = HorizontalAlignment.Left;
            row.VerticalAlignment = VerticalAlignment.Top;
            var w = row.GetVariable("Width");
            if (w != null) w.Value = 240.0;
            var h = row.GetVariable("Height");
            if (h != null) h.Value = 40.0;
        }
        catch { }
    }

    /// <summary>递归查找 BrowseName 与类型名一致的 ObjectType（与 GenerateTreeList 等一致）。</summary>
    private static NodeId FindCustomTypeNodeId(IUANode root, string typeName)
    {
        if (root == null) return NodeId.Empty;
        if (root.BrowseName == typeName && root.NodeClass == NodeClass.ObjectType)
            return root.NodeId;
        foreach (var child in root.Children)
        {
            var result = FindCustomTypeNodeId(child, typeName);
            if (result != NodeId.Empty) return result;
        }
        return NodeId.Empty;
    }

    private void OnCheckboxCheckedChanged(int index, UAValue newValue)
    {
        if (_typeCheckedVariable == null) return;
        if (index < 0 || index >= FilterCategoryCapacity) return;
        try
        {
            bool b = false;
            if (newValue != null && newValue.Value != null && newValue.Value is bool)
                b = (bool)newValue.Value;
            var arr = ReadBoolArrayFromVariable(_typeCheckedVariable, FilterCategoryCapacity);
            if (index >= arr.Length)
                return;
            arr[index] = b;
            _typeCheckedVariable.Value = arr;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"OnCheckboxCheckedChanged[{index}]: {ex.Message}");
        }
    }

    /// <summary>按 <c>CheckboxData</c> 数组下标读取，保证与 <c>TypeChecked[i]</c> 一致；跳过占位 <c>0</c> 与空串。</summary>
    private static List<CheckboxSlot> ReadCheckboxDataSlots(IUAVariable v)
    {
        var result = new List<CheckboxSlot>();
        if (v == null) return result;

        try
        {
            var raw = v.Value;
            if (raw?.Value == null) return result;

            if (raw.Value is string[] sa)
            {
                for (int i = 0; i < sa.Length; i++)
                {
                    string s = sa[i] != null ? sa[i].Trim() : "";
                    if (IsMeaningfulLabel(s))
                        result.Add(new CheckboxSlot { SourceIndex = i, Label = s });
                }
                return result;
            }

            if (raw.Value is object[] oa)
            {
                for (int i = 0; i < oa.Length; i++)
                {
                    string s = ElementToLabelString(oa[i]);
                    if (IsMeaningfulLabel(s))
                        result.Add(new CheckboxSlot { SourceIndex = i, Label = s });
                }
                return result;
            }

            if (raw.Value is Array arr && arr.Rank == 1)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    string s = ElementToLabelString(arr.GetValue(i));
                    if (IsMeaningfulLabel(s))
                        result.Add(new CheckboxSlot { SourceIndex = i, Label = s });
                }
                return result;
            }

            string single = ElementToLabelString(raw.Value);
            if (IsMeaningfulLabel(single))
                result.Add(new CheckboxSlot { SourceIndex = 0, Label = single });
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"ReadCheckboxDataSlots: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// CheckBox 的 <c>Text</c> 为 <c>LocalizedText</c>。空 key 的 <c>LocalizedText("", text)</c> 在部分控件上不渲染；
    /// 若类型上存在动态链接，需先 <c>ResetDynamicLink</c> 再写值。顺序：先写变量，再写 <c>CheckBox.Text</c> 字符串属性（若存在）。
    /// </summary>
    private static void ApplyCheckboxLabel(CheckBox innerCb, string label, string localeId, int sourceIndex)
    {
        if (innerCb == null || string.IsNullOrEmpty(label))
            return;

        var textVar = innerCb.GetVariable("Text");
        if (textVar != null)
        {
            TryResetVariableDynamicLink(textVar);

            if (!TrySetLocalizedTextValue(textVar, label, localeId, sourceIndex))
            {
                try
                {
                    textVar.Value = label;
                }
                catch { }
            }
        }

        try
        {
            innerCb.Text = label;
        }
        catch { }
    }

    private static void TryResetVariableDynamicLink(IUAVariable variable)
    {
        if (variable == null)
            return;
        try
        {
            MethodInfo mi = variable.GetType().GetMethod("ResetDynamicLink", Type.EmptyTypes);
            if (mi != null)
                mi.Invoke(variable, null);
        }
        catch { }
    }

    /// <returns>是否至少一种 LocalizedText 重载写入成功。</returns>
    private static bool TrySetLocalizedTextValue(IUAVariable textVar, string label, string localeId, int sourceIndex)
    {
        string loc = string.IsNullOrEmpty(localeId) ? "en-US" : localeId;
        string translationKey = "GenerateCheckbox_" + sourceIndex.ToString();

        try
        {
            textVar.Value = new LocalizedText(label);
            return true;
        }
        catch { }

        try
        {
            textVar.Value = new LocalizedText(label, loc);
            return true;
        }
        catch { }

        try
        {
            textVar.Value = new LocalizedText(translationKey, label, loc);
            return true;
        }
        catch { }

        try
        {
            textVar.Value = new LocalizedText(loc, label);
            return true;
        }
        catch { }

        try
        {
            textVar.Value = new LocalizedText("", label);
            return true;
        }
        catch { }

        return false;
    }

    /// <summary>与类型上默认占位 <c>["0",...]</c> 区分：未链接或尚未解析时不当作有效标签。</summary>
    private static bool IsMeaningfulLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s == "0") return false;
        return true;
    }

    private static string ElementToLabelString(object o)
    {
        if (o == null) return "";
        var lt = o as LocalizedText;
        if (lt != null)
            return (lt.Text ?? "").Trim();
        return o.ToString()?.Trim() ?? "";
    }

    private static void RemoveLegacyCheckboxRows(RowLayout container)
    {
        var toRemove = new List<IUANode>();
        foreach (var ch in container.Children)
        {
            if (!(ch is IUAObject))
                continue;
            string tail = BrowseNameTail(ch);
            if (tail.StartsWith("Rectangle", StringComparison.OrdinalIgnoreCase) ||
                tail.StartsWith("DynRow_", StringComparison.OrdinalIgnoreCase) ||
                tail.StartsWith("DynCheckRow_", StringComparison.OrdinalIgnoreCase))
                toRemove.Add(ch);
        }
        foreach (var n in toRemove)
        {
            try { n.Delete(); }
            catch { }
        }
    }

    private static string BrowseNameTail(IUANode node)
    {
        string bn = node?.BrowseName?.ToString() ?? "";
        int c = bn.LastIndexOf(':');
        return c >= 0 && c < bn.Length - 1 ? bn.Substring(c + 1) : bn;
    }

    private static IUAVariable ResolveSearchByCategoryTypeCheckedVariable(IUANode project)
    {
        IUAObject data = ResolveSearchByCategoryDataObject(project);
        return data?.GetVariable("TypeChecked");
    }

    /// <summary>解析 <c>SearchByCategoryData</c>：优先标准路径，其次旧路径，最后在 <c>Model</c> 下按 BrowseName 查找。</summary>
    private static IUAObject ResolveSearchByCategoryDataObject(IUANode project)
    {
        if (project == null)
            return null;

        string[] paths =
        {
            PathSearchByCategoryData,
            "Model/UIData/FilterData/SearchByCategoryData"
        };

        foreach (string p in paths)
        {
            var o = project.GetObject(p) as IUAObject;
            if (o != null)
                return o;
        }

        var model = project.GetObject("Model") as IUAObject;
        if (model != null)
        {
            var found = FindDescendantObjectByBrowseTail(model, "SearchByCategoryData");
            if (found != null)
                return found;
        }

        return FindDescendantObjectByBrowseTail(project, "SearchByCategoryData");
    }

    private static IUAObject FindDescendantObjectByBrowseTail(IUANode root, string tail)
    {
        if (root == null || string.IsNullOrEmpty(tail))
            return null;
        if (root is IUAObject obj && string.Equals(BrowseNameTail(root), tail, StringComparison.OrdinalIgnoreCase))
            return obj;
        foreach (var ch in root.Children)
        {
            IUAObject found = FindDescendantObjectByBrowseTail(ch, tail);
            if (found != null)
                return found;
        }
        return null;
    }

    private static bool[] ReadBoolArrayFromVariable(IUAVariable v, int len)
    {
        var a = new bool[len];
        if (v?.Value?.Value == null) return a;
        try
        {
            var val = v.Value.Value;
            if (val is bool[] ba)
            {
                for (int i = 0; i < len && i < ba.Length; i++)
                    a[i] = ba[i];
                return a;
            }
            if (val is object[] oa)
            {
                for (int i = 0; i < len && i < oa.Length; i++)
                {
                    if (oa[i] is bool)
                        a[i] = (bool)oa[i];
                }
            }
        }
        catch { }
        return a;
    }
}
