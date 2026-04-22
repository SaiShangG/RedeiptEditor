#region Using directives
using System;
using System.Reflection;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
using FTOptix.Core;
#endregion

/// <summary>
/// 通用删除确认弹窗逻辑：
/// 1) 先配置目标对象与方法；
/// 2) 点击确认时统一执行目标删除方法。
/// </summary>
public class DeletePanelGeneralLogic : BaseNetLogic
{
    private const string LogCategory = "DeletePanelGeneralLogic";

    private const string ActionObjectVarName = "ActionObject";
    private const string ActionMethodVarName = "ActionMethod";
    private const string ActionArgVarName = "ActionArg";

    public override void Start()
    {
        EnsureConfigVariables();
    }

    public override void Stop()
    {
    }

    [ExportMethod]
    public void ConfigureDeleteAction(NodeId actionObjectNodeId, string actionMethodName, string actionArg = "")
    {
        EnsureConfigVariables();

        SetNodeIdVariable(ActionObjectVarName, actionObjectNodeId);
        SetStringVariable(ActionMethodVarName, actionMethodName);
        SetStringVariable(ActionArgVarName, actionArg);
    }

    [ExportMethod]
    public void ConfirmDeleteAction()
    {
        EnsureConfigVariables();

        NodeId objectNodeId = GetNodeIdVariable(ActionObjectVarName);
        string methodName = GetStringVariable(ActionMethodVarName);
        string actionArg = GetStringVariable(ActionArgVarName);

        if (objectNodeId == null || objectNodeId.IsEmpty)
        {
            Log.Warning(LogCategory, "确认删除失败：ActionObject 为空。");
            return;
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            Log.Warning(LogCategory, "确认删除失败：ActionMethod 为空。");
            return;
        }

        var targetNode = InformationModel.Get(objectNodeId);
        if (targetNode == null)
        {
            Log.Warning(LogCategory, $"确认删除失败：目标对象不存在（NodeId={objectNodeId}）。");
            return;
        }

        object targetInstance = ResolveNetLogicInstance(targetNode.BrowseName);
        if (targetInstance == null)
        {
            Log.Warning(LogCategory, $"确认删除失败：未找到 NetLogic 实例（BrowseName={targetNode.BrowseName}）。");
            return;
        }

        if (!TryInvokeTargetMethod(targetInstance, methodName, actionArg))
            Log.Warning(LogCategory, $"确认删除失败：调用方法失败（{targetNode.BrowseName}.{methodName}）。");
    }

    private static bool TryInvokeTargetMethod(object target, string methodName, string actionArg)
    {
        if (target == null || string.IsNullOrWhiteSpace(methodName))
            return false;

        try
        {
            var t = target.GetType();

            // 优先匹配无参删除方法（本项目删除方法大多无参）
            MethodInfo methodNoArg = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (methodNoArg != null)
            {
                methodNoArg.Invoke(target, null);
                return true;
            }

            // 兼容未来扩展：支持单 string 入参
            MethodInfo methodStringArg = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            if (methodStringArg != null)
            {
                methodStringArg.Invoke(target, new object[] { actionArg ?? "" });
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"调用删除方法异常：{ex.Message}");
        }

        return false;
    }

    private static object ResolveNetLogicInstance(string browseName)
    {
        if (string.IsNullOrWhiteSpace(browseName))
            return null;

        switch (browseName.Trim())
        {
            case "BatchEditorLogic":
                return BatchEditorLogic.Instance;
            case "RecipeDatabaseManager":
                return RecipeDatabaseManager.Instance;
            default:
                return null;
        }
    }

    private void EnsureConfigVariables()
    {
        EnsureVariable(ActionObjectVarName, OpcUa.DataTypes.NodeId);
        EnsureVariable(ActionMethodVarName, OpcUa.DataTypes.String);
        EnsureVariable(ActionArgVarName, OpcUa.DataTypes.String);
    }

    private IUAVariable EnsureVariable(string name, NodeId dataType)
    {
        var v = LogicObject.GetVariable(name);
        if (v != null) return v;

        v = InformationModel.MakeVariable(name, dataType);
        LogicObject.Add(v);
        return v;
    }

    private void SetStringVariable(string name, string value)
    {
        var v = EnsureVariable(name, OpcUa.DataTypes.String);
        v.Value = value ?? "";
    }

    private string GetStringVariable(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v?.Value?.Value as string ?? "";
    }

    private void SetNodeIdVariable(string name, NodeId value)
    {
        var v = EnsureVariable(name, OpcUa.DataTypes.NodeId);
        v.Value = value ?? NodeId.Empty;
    }

    private NodeId GetNodeIdVariable(string name)
    {
        var v = LogicObject.GetVariable(name);
        if (v?.Value?.Value is NodeId id)
            return id;
        return NodeId.Empty;
    }
}
