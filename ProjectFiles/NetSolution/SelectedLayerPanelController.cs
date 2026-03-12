#region Using directives
using System;
using System.Collections.Generic;
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

public class SelectedLayerPanelController : BaseNetLogic
{
    #region 按面板查找实例
    private static readonly Dictionary<NodeId, SelectedLayerPanelController> ByPanel = new();

    public static bool TryGetByPanel(NodeId panelNodeId, out SelectedLayerPanelController controller)
    {
        return ByPanel.TryGetValue(panelNodeId, out controller);
    }
    #endregion

    #region 挂的变量引用
    private IUAVariable _iconTypeVar;
    private IUAVariable _titleVar;
    private IUAVariable _typeVar;
    private IUAVariable _iconTextVar;
    private IUAVariable _iconText2Var;
    #endregion

    #region 生命周期
    public override void Start()
    {
        var panel = LogicObject.Owner;
        if (panel != null) ByPanel[panel.NodeId] = this;

        _iconTypeVar = LogicObject.GetVariable("IconType");
        _titleVar = LogicObject.GetVariable("Title");
        _typeVar = LogicObject.GetVariable("Type");
        _iconTextVar = LogicObject.GetVariable("IconText");
        _iconText2Var = LogicObject.GetVariable("IconText2");
    }

    public override void Stop()
    {
        var panel = LogicObject.Owner;
        if (panel != null) ByPanel.Remove(panel.NodeId);
    }
    #endregion

    #region 对外方法
    /// <summary>根据 mode、item 和是否左侧面板更新面板显示的变量值。</summary>
    public void UpdatePanelContent(string mode, string item, bool isLeftPanel)
    {
        if (_typeVar != null) _typeVar.Value = mode ?? string.Empty;
        if (_titleVar != null)
        {
            if (string.Equals(mode, "Operation", StringComparison.OrdinalIgnoreCase))
                _titleVar.Value = isLeftPanel ? "Phases in selected operation:" : "Operations in used receipts:";
            else
                _titleVar.Value = "Phases in used operations:";
        }
        if (_iconTextVar != null) _iconTextVar.Value = item ?? "None";
        if (_iconText2Var != null) _iconText2Var.Value = item ?? "None";
    }
    #endregion
}
