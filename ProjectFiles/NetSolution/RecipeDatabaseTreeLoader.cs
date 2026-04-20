#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpcUa = UAManagedCore.OpcUa;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.HMIProject;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
#endregion

/// <summary>从配方数据库读取全部数据并保存到本地 dict 树（Receipt → Operation → Phase）。</summary>
public partial class RecipeDatabaseTreeLoader : BaseNetLogic
{
    private const string LogCategory = "RecipeDatabaseTreeLoader";
    private const bool EnableLog = true;
    /// <summary>新建 Receipt 时 Status 列的默认值（与 DataStores 中 StoreColumn 默认值一致）。</summary>
    public const string DefaultReceiptStatus = "Development";

    /// <summary>Receipt.CreatedDateTime 在库内/内存中的统一格式，例如 2026-03-22T16:49:03.6596165。</summary>
    public const string CreatedDateTimeStorageFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";

    /// <summary>当前本地时刻，按 <see cref="CreatedDateTimeStorageFormat"/> 格式化，用于新建 Receipt。</summary>
    public static string FormatStoredCreatedDateTimeNow()
    {
        return DateTime.Now.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>将数据库单元格或字符串统一为 <see cref="CreatedDateTimeStorageFormat"/>；无法解析时保留原字符串修剪结果。</summary>
    public static string NormalizeStoredCreatedDateTime(object value)
    {
        if (value == null || value == DBNull.Value) return "";
        if (value is DateTime dt)
            return dt.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        string s = Convert.ToString(value)?.Trim() ?? "";
        if (string.IsNullOrEmpty(s)) return "";
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime p1))
            return p1.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime p2))
            return p2.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        foreach (string cultureName in new[] { "zh-CN", "en-US" })
        {
            try
            {
                if (DateTime.TryParse(s, CultureInfo.GetCultureInfo(cultureName), DateTimeStyles.None, out DateTime p3))
                    return p3.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
            }
            catch (CultureNotFoundException) { }
        }
        string[] exactFormats =
        {
            "yyyy/M/d H:mm:ss", "yyyy/M/d HH:mm:ss", "yyyy/MM/dd H:mm:ss", "yyyy/MM/dd HH:mm:ss",
            "yyyy-M-d H:mm:ss", "yyyy-MM-dd HH:mm:ss"
        };
        foreach (string fmt in exactFormats)
        {
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime p4))
                return p4.ToString(CreatedDateTimeStorageFormat, CultureInfo.InvariantCulture);
        }
        return s;
    }

    public static RecipeDatabaseTreeLoader Instance { get; private set; }

    #region Phase 模板 UDT 缓冲（监听与加载深度；程序化写入用 SenderId 排除）
    private ulong _phaseBufferProgSenderId;
    private uint _phaseBufferDirtyAffinityId;
    private readonly List<IEventRegistration> _udtTemplateBufferDirtyRegs = new List<IEventRegistration>();
    private int _phaseBufferLoadDepth;
    /// <summary>为 true 时表示正从 PhaseType1/内存树灌入模板 UDT，此时 UI 联动变更不应标脏。</summary>
    public bool IsPhaseUdtTemplateLoading => _phaseBufferLoadDepth > 0;
    #endregion

    private Store _store;
    private Table _receiptTable, _opTable, _phaseTable;
    private string _receiptTableName, _opTableName, _phaseTableName;

    private Store _phaseParamsStore;
    private Table _phaseType1Table;
    private const string PhaseParametersDbName = "PhaseParametersDB";
    private const string PhaseType1TableName = "PhaseType1";
    /// <summary>模板相位 UDT 根路径（与 PhaseManager 中 DynamicLink 根一致）。</summary>
    public const string DefaultUdtPhaseTemplateBufferObjectPath = "Model/UIData/PhaseData/UDT_PhaseTemplateUIBuffer1";
    private const string PhaseLayoutJsonFileName = "phase_ui_layout.sample.json";
    /// <summary>PhaseType1 中非业务数据列（不参与 SELECT/UPDATE 业务字段）。</summary>
    private static readonly HashSet<string> PhaseType1ReservedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PhaseParameterInfoID", "TypeID", "PhaseTemplateTypeID"
    };

    #region 树节点类型
    public class ReceiptNode
    {
        public int ReceiptID;
        public string Name;
        public int Sequence;
        public string OperationsCsv;
        public string Description;
        /// <summary>状态（与 Receipt 表 Status 列对应；新建时默认为 Development）。</summary>
        public string Status = DefaultReceiptStatus;
        /// <summary>创建人（与 Receipt 表 CreatedBy 列对应，表无此列时不参与 SQL）。</summary>
        public string CreatedBy = "";
        /// <summary>创建时间（与 Receipt 表 CreatedDateTime 列对应，表无此列时不参与 SQL）。</summary>
        public string CreatedDateTime = "";
        public List<OperationNode> Operations = new List<OperationNode>();
    }

    public class OperationNode
    {
        public int OperationID;
        public string Name;
        public string Description;
        public string PhasesCsv;
        /// <summary>创建人（与 Operations 表 CreatedBy 列对应，表无此列时不参与 SQL）。</summary>
        public string CreatedBy = "";
        /// <summary>创建时间（与 Operations 表 CreatedDateTime 列对应）。</summary>
        public string CreatedDateTime = "";
        public List<PhaseNode> Phases = new List<PhaseNode>();
    }

    public class PhaseNode
    {
        public int PhaseID;
        public string Name;
        public string Description;
        public Dictionary<string, object> Columns = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }
    #endregion

    #region 本地 dict 树（只读）
    /// <summary>按 Sequence 排序的配方树根列表。</summary>
    public List<ReceiptNode> Tree { get; private set; } = new List<ReceiptNode>();

    /// <summary>ReceiptID → ReceiptNode 快速查找。</summary>
    public Dictionary<int, ReceiptNode> ReceiptById { get; private set; } = new Dictionary<int, ReceiptNode>();

    /// <summary>OperationID → OperationNode 快速查找。</summary>
    public Dictionary<int, OperationNode> OperationById { get; private set; } = new Dictionary<int, OperationNode>();

    /// <summary>PhaseID → PhaseNode 快速查找。</summary>
    public Dictionary<int, PhaseNode> PhaseById { get; private set; } = new Dictionary<int, PhaseNode>();

    /// <summary>按名称查找 OperationNode（忽略大小写）。返回第一个匹配项，未找到返回 null。</summary>
    public OperationNode FindOperationByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var op in OperationById.Values)
            if (string.Equals(op.Name, name, StringComparison.OrdinalIgnoreCase))
                return op;
        return null;
    }

    /// <summary>按名称查找所有匹配的 OperationNode（忽略大小写）。</summary>
    public List<OperationNode> FindOperationsByName(string name)
    {
        var result = new List<OperationNode>();
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var op in OperationById.Values)
            if (string.Equals(op.Name, name, StringComparison.OrdinalIgnoreCase))
                result.Add(op);
        return result;
    }

    /// <summary>按名称查找 PhaseNode（忽略大小写）。返回第一个匹配项，未找到返回 null。</summary>
    public PhaseNode FindPhaseByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var ph in PhaseById.Values)
            if (string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase))
                return ph;
        return null;
    }

    /// <summary>按名称查找所有匹配的 PhaseNode（忽略大小写）。</summary>
    public List<PhaseNode> FindPhasesByName(string name)
    {
        var result = new List<PhaseNode>();
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var ph in PhaseById.Values)
            if (string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase))
                result.Add(ph);
        return result;
    }
    #endregion

    #region 生命周期与打开数据库
    public override void Start()
    {
        Instance = this;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseTreeLoader 已启动");
        OpenTables();
        LoadAllToTree();
        try
        {
            _phaseBufferProgSenderId = LogicObject.Context.AssignSenderId();
            _phaseBufferDirtyAffinityId = LogicObject.Context.AssignAffinityId();
        }
        catch { _phaseBufferProgSenderId = 0; _phaseBufferDirtyAffinityId = 0; }
        AttachUdtTemplateBufferDirtyObservers();
    }

    public override void Stop()
    {
        DetachUdtTemplateBufferDirtyObservers();
        ClearTree();
        Instance = null;
        _store = null;
        _receiptTable = _opTable = _phaseTable = null;
        _receiptTableName = _opTableName = _phaseTableName = null;
        _phaseType1Table = null;
        _phaseParamsStore = null;
        if (EnableLog) Log.Info(LogCategory, "RecipeDatabaseTreeLoader 已停止");
    }

    /// <summary>解析 ReceiptDB/OperationDB/PhaseDB，打开 Store 与表。</summary>
    private void OpenTables()
    {
        _store = null;
        _receiptTable = null;
        _receiptTableName = null;
        _opTable = null;
        _opTableName = null;
        _phaseTable = null;
        _phaseTableName = null;

        if (!ResolveTable("ReceiptDB", out _store, out _receiptTable, out _receiptTableName)) return;
        if (!ResolveTable("OperationDB", out _, out _opTable, out _opTableName)) return;
        if (!ResolveTable("PhaseDB", out _, out _phaseTable, out _phaseTableName)) return;
    }

    private bool ResolveTable(string varName, out Store store, out Table table, out string tableName)
    {
        store = null;
        table = null;
        tableName = null;
        var v = LogicObject.GetVariable(varName);
        if (v == null) { if (EnableLog) Log.Error(LogCategory, $"未配置 {varName} 变量"); return false; }
        var node = InformationModel.Get(v.Value);
        if (node == null) { if (EnableLog) Log.Error(LogCategory, $"{varName} 指向的节点无效"); return false; }
        store = GetStoreFromNode(node);
        if (store == null) return false;
        tableName = node.BrowseName;
        table = node as Table ?? GetTableFromStoreByNodeId(store, node.NodeId);
        if (table == null && EnableLog) Log.Error(LogCategory, $"无法获取表: {tableName}");
        return table != null;
    }



    private static Store GetStoreFromNode(IUANode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Store s) return s;
            current = current.Owner;
        }
        return null;
    }

    private static Table GetTableFromStoreByNodeId(Store store, NodeId tableNodeId)
    {
        foreach (var child in store.Children)
        {
            if (child.NodeId == tableNodeId && child is Table t) return t;
        }
        return null;
    }
    #endregion
}