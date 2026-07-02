#region Using directives
using System;
using System.Globalization;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.Core;
#endregion

/// <summary>批次运行列表高亮（GenerateBatchRunFlow 未启动时也可扫描 UI 树着色）。</summary>
internal static class BatchRunFlowHighlight
{
    /// <summary>演示用亮绿色，与默认深灰区分明显。</summary>
    public static readonly Color RunningTextColor = new Color(255, 0, 0x9a, 0x3c);
    public static readonly Color DefaultTextColor = new Color(255, 0x33, 0x33, 0x33);
    public static readonly Color TransparentBg = new Color(0, 0xe4, 0xe4, 0xe4);

    public static void ApplyOnProject(int runningOpIndex, int runningPhaseIndex, bool isRunning)
    {
        var root = Project.Current;
        if (root == null) return;
        WalkNode(root, runningOpIndex, runningPhaseIndex, isRunning);
    }

    private static void WalkNode(IUANode node, int runningOp, int runningPhase, bool isRunning)
    {
        if (node == null) return;

        if (node is Container row && row.GetVariable("FlowOpIndex") != null)
        {
            if (TryGetIndices(row, out int op, out int phase))
                ApplyRow(row, op, phase, runningOp, runningPhase, isRunning);
        }

        foreach (var child in node.Children)
            WalkNode(child, runningOp, runningPhase, isRunning);
    }

    private static bool TryGetIndices(Container row, out int opIndex, out int phaseIndex)
    {
        opIndex = 0;
        phaseIndex = -1;
        try
        {
            var o = row.GetVariable("FlowOpIndex");
            if (o?.Value == null) return false;
            opIndex = Convert.ToInt32(o.Value.Value, CultureInfo.InvariantCulture);
            var p = row.GetVariable("FlowPhaseIndex");
            phaseIndex = p?.Value != null ? Convert.ToInt32(p.Value.Value, CultureInfo.InvariantCulture) : -1;
            return opIndex >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyRow(Container row, int op, int phase, int runningOp, int runningPhase, bool isRunning)
    {
        bool highlight = isRunning && op == runningOp
            && (phase < 0 ? true : phase == runningPhase);
        var color = highlight ? RunningTextColor : DefaultTextColor;
        var btn = row.Get<Container>("Container")?.Get<Container>("ItemContainer")?.Get<Button>("ItemButton");
        if (btn == null) return;
        btn.TextColor = color;
        SetColor(btn, "TextColor", color);
    }

    private static void SetColor(IUANode node, string name, Color color)
    {
        try
        {
            var v = node.GetVariable(name);
            if (v != null) v.Value = color;
        }
        catch { }
    }
}
