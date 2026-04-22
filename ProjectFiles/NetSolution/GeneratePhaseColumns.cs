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
using FTOptix.RAEtherNetIP;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using FTOptix.WebUI;
#endregion

public class GeneratePhaseColumns : BaseNetLogic
{
    private const string DefaultTablePath = "DataStores/PhaseParametersDB/Tables/PhaseType1";
    private const string DefaultBufferPath = "Model/UIData/PhaseData/UDT_PhaseTemplateUIBuffer1";

    [ExportMethod]
    public void GenerateColumns()
    {
        var modelRoot = GetModelRootNode();
        if (modelRoot == null)
        {
            Log.Error(nameof(GeneratePhaseColumns), "Cannot resolve model root node.");
            return;
        }

        var tableNode = modelRoot.Get(DefaultTablePath);
        if (tableNode is not Table table)
        {
            Log.Error(nameof(GeneratePhaseColumns), $"Invalid table path: {DefaultTablePath}");
            return;
        }

        var bufferNode = modelRoot.Get(DefaultBufferPath);
        Log.Info(bufferNode.BrowseName);
        if (bufferNode == null)
        {
            Log.Error(nameof(GeneratePhaseColumns), $"Cannot find buffer object by path: '{DefaultBufferPath}'.");
            return;
        }

        var columnsObject = table.GetObject("Columns");
        if (columnsObject == null)
        {
            Log.Error(nameof(GeneratePhaseColumns), $"Table '{table.BrowseName}' has no Columns object.");
            return;
        }

        int added = 0;
        var leafTags = new List<(IUAVariable Variable, string PathName)>();
        CollectTagLevelVariables(bufferNode, "", leafTags);

        foreach (var item in leafTags)
        {
            string baseName = MakeSafeColumnName(item.Variable.BrowseName);
            if (string.IsNullOrEmpty(baseName))
                continue;

            if (EnsureColumn(columnsObject, baseName, item.Variable))
                added++;
        }

        Log.Info(nameof(GeneratePhaseColumns), $"GenerateColumns completed. Added {added} columns.");
    }

    private static bool EnsureColumn(IUAObject columnsObject, string columnName, IUAVariable sourceTag)
    {
        if (columnsObject == null || string.IsNullOrEmpty(columnName))
            return false;
        if (sourceTag == null)
            return false;

        var existing = columnsObject.Get(columnName) as StoreColumn;
        if (existing != null)
        {
            SyncColumnType(existing, sourceTag);
            return false;
        }

        var col = InformationModel.Make<StoreColumn>(columnName);
        SyncColumnType(col, sourceTag);
        columnsObject.Add(col);
        return true;
    }

    private static void SyncColumnType(StoreColumn col, IUAVariable sourceTag)
    {
        if (col == null || sourceTag == null)
            return;

        col.DataType = sourceTag.DataType;
        col.ValueRank = sourceTag.ValueRank;
        try
        {
            if (sourceTag.ArrayDimensions != null && sourceTag.ArrayDimensions.Length > 0)
            {
                col.ArrayDimensions = (uint[])sourceTag.ArrayDimensions.Clone();
            }
            else
            {
                col.ArrayDimensions = null;
            }
        }
        catch
        {
        }
    }

    private IUAObject GetModelRootNode()
    {
        // 1) Try to get project object root from current logic position.
        var n = LogicObject as IUANode;
        while (n != null)
        {
            if (string.Equals(n.BrowseName, "RedeiptEditor", StringComparison.OrdinalIgnoreCase))
                return n as IUAObject;
            n = n.Owner;
        }

        // 2) Fallback to Project.Current.
        return Project.Current;
    }

    private static void CollectTagLevelVariables(IUANode node, string currentPath, List<(IUAVariable Variable, string PathName)> result)
    {
        if (node == null)
            return;

        string thisPath = string.IsNullOrEmpty(currentPath) ? node.BrowseName : $"{currentPath}_{node.BrowseName}";

        if (node is IUAVariable variable && IsTagLevelVariable(variable))
        {
            result.Add((variable, thisPath));
            return;
        }

        if (node.Children == null)
            return;

        foreach (var child in node.Children)
        {
            if (child == null)
                continue;

            if (string.Equals(child.BrowseName, "SymbolName", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(child.BrowseName, "EnableBlockRead", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(child.BrowseName, "ArrayUpdateRate", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(child.BrowseName, "SamplingMode", StringComparison.OrdinalIgnoreCase))
                continue;

            CollectTagLevelVariables(child, thisPath, result);
        }
    }

    private static bool IsTagLevelVariable(IUAVariable variable)
    {
        if (variable == null)
            return false;

        // Exclude metadata/properties that are not process tags.
        if (string.Equals(variable.BrowseName, "SymbolName", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(variable.BrowseName, "EnableBlockRead", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(variable.BrowseName, "ArrayUpdateRate", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(variable.BrowseName, "SamplingMode", StringComparison.OrdinalIgnoreCase))
            return false;

        // Tag-level: no meaningful process child nodes.
        if (variable.Children == null)
            return true;

        foreach (var child in variable.Children)
        {
            if (child == null)
                continue;
            if (string.Equals(child.BrowseName, "SymbolName", StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }

    private static string MakeSafeColumnName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        var chars = rawName.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (char.IsLetterOrDigit(c) || c == '_')
                continue;
            chars[i] = '_';
        }

        string result = new string(chars).Trim('_');
        if (string.IsNullOrEmpty(result))
            return string.Empty;

        if (!char.IsLetter(result[0]) && result[0] != '_')
            result = "_" + result;

        return result;
    }
}
