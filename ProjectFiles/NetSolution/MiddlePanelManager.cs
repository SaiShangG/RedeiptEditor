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

public class MiddlePanelManager : BaseNetLogic
{
    public static MiddlePanelManager Instance { get; private set; }

    #region 挂的变量引用
    private IUAVariable _itemPrefabVar;
    private IUAVariable _leftPanelVisiableVar;
    private IUAVariable _rightPanelVisiableVar;
    private IUAVariable _leftPanelVar;
    private IUAVariable _rightPanelVar;
    #endregion

    #region 生命周期
    public override void Start()
    {
        Instance = this;
        _itemPrefabVar = LogicObject.GetVariable("ItemPrefab");
        _leftPanelVisiableVar = LogicObject.GetVariable("LeftPanelVisiable");
        _rightPanelVisiableVar = LogicObject.GetVariable("RightPanelVisiable");
        _leftPanelVar = LogicObject.GetVariable("LeftPanel");
        _rightPanelVar = LogicObject.GetVariable("RightPanel");
        if (_itemPrefabVar == null || _leftPanelVisiableVar == null || _rightPanelVisiableVar == null || _leftPanelVar == null || _rightPanelVar == null)
        {
            Log.Error(nameof(MiddlePanelManager), "未找到挂载变量 ItemPrefab / LeftPanelVisiable / RightPanelVisiable / LeftPanel / RightPanel");
            return;
        }
    }

    public override void Stop()
    {
        Instance = null;
    }
    #endregion

    #region 标题点击通知
    /// <summary>收到标题点击后更新左右面板可见性：Operation -> left+right，Phase -> only right。并通知 Panel1/Panel2 的 SelectedLayerPanelController。</summary>
    public void NotifyTitleClicked(string mode, string versionItem)
    {
        if (_leftPanelVisiableVar == null || _rightPanelVisiableVar == null || _leftPanelVar == null || _rightPanelVar == null) return;
        bool isOperation = string.Equals(mode, "Operation", StringComparison.OrdinalIgnoreCase);
        bool isPhase = string.Equals(mode, "Phase", StringComparison.OrdinalIgnoreCase);
        _leftPanelVisiableVar.Value = isOperation;
        _rightPanelVisiableVar.Value = isOperation || isPhase;

        InvokeControllerUpdate(_leftPanelVar.Value.Value as NodeId, mode, versionItem, isLeftPanel: true);
        InvokeControllerUpdate(_rightPanelVar.Value.Value as NodeId, mode, versionItem, isLeftPanel: false);
    }

    private void InvokeControllerUpdate(NodeId panelNodeId, string mode, string item, bool isLeftPanel)
    {
        if (SelectedLayerPanelController.TryGetByPanel(panelNodeId, out var sc)) sc.UpdatePanelContent(mode, item, isLeftPanel);
    }
    #endregion
}
