#region Using directives
using System;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using System.Collections.Generic;
using System.Reflection;
using System.Linq.Expressions;
using FTOptix.RecipeX;
using FTOptix.SQLiteStore;
using FTOptix.Store;
#endregion

public class MatrixEditorUpdater : BaseNetLogic
{
    /// <summary>
    /// This method initializes the logic object, sets up affinity and sender IDs, and sets up event observers for variable changes.
    /// It also initializes a grid based on a 2D array and handles variable value changes.
    /// </summary>
    public override void Start()
    {
        var context = LogicObject.Context;
        logicObjectAffinityId = context.AssignAffinityId();
        logicObjectSenderId = context.AssignSenderId();

        // Check if the given array is valid and convert it to a C# Array
        matrixValueVariable = Owner.GetVariable("MatrixValue");
        if (matrixValueVariable == null)
            throw new CoreConfigurationException("Unable to find MatrixValue variable");
        var matrixValueVariableValue = matrixValueVariable.Value.Value;
        if (!matrixValueVariableValue.GetType().IsArray)
            throw new CoreConfigurationException("MatrixValue is not an array");
        var matrixArray = (Array)matrixValueVariableValue;
        if (matrixArray.Rank != 2)
            throw new CoreConfigurationException("Only two-dimensional arrays are supported");

        // GridModel represents a support variable that acts as a link between the VectorValue model variable and the widget data grid.
        gridModelVariable = LogicObject.GetVariable("GridModel");

        using (var resumeDispatchOnExit = context.SuspendDispatch(logicObjectAffinityId))
        {
            // Register the observer on MatrixValue
            matrixValueVariableChangeObserver = new CallbackVariableChangeObserver(MatrixValueVariableValueChanged);
            matrixValueVariableRegistration = matrixValueVariable.RegisterEventObserver(
                matrixValueVariableChangeObserver, EventType.VariableValueChanged, logicObjectAffinityId);

            cellVariableChangeObserver = new CallbackVariableChangeObserver(CellVariableValueChanged);
            CreateGrid(matrixArray);
        }
    }

    /// <summary>
    /// This method stops the logic object, releasing resources and resetting various variables.
    /// </summary>
    public override void Stop()
    {
        using (var destroyDispatchOnExit = LogicObject.Context.TerminateDispatchOnStop(logicObjectAffinityId))
        {
            if (cellVariableRegistrations != null)
            {
                cellVariableRegistrations.ForEach(registration => registration.Dispose());
                cellVariableRegistrations = null;
            }

            if (matrixValueVariableRegistration != null)
            {
                matrixValueVariableRegistration.Dispose();
                matrixValueVariableRegistration = null;
            }

            if (gridModelVariable != null)
                gridModelVariable.Value = NodeId.Empty;

            if (gridObject != null)
            {
                gridObject.Delete();
                gridObject = null;
            }

            currentRowCount = 0;
            currentCellCount = 0;

            gridModelVariable = null;
            matrixValueVariable = null;
            logicObjectSenderId = 0;
            logicObjectAffinityId = 0;
        }
    }

    #region Initialize GridModel from MatrixValue
    /// <summary>
    /// This method creates a grid from a 2D array and adds it to a logic object.
    /// </summary>
    /// <param name="matrixArray">A 2D array representing the grid data.</param>
    /// <remarks>
    /// The method first disposes of any existing registrations, clears the list, and initializes
    /// the grid object with the specified dimensions. It then iterates through each row, creates
    /// a row object from the array, and adds it to the grid object. Finally, the grid object
    /// is added to the logic object and its node ID is assigned to the gridModelVariable.
    /// </remarks>
    private void CreateGrid(Array matrixArray)
    {
        if (cellVariableRegistrations != null)
        {
            cellVariableRegistrations.ForEach(registration => registration.Dispose());
            cellVariableRegistrations.Clear();
        }
        else
            cellVariableRegistrations = new List<IEventRegistration>();

        currentRowCount = (uint)matrixArray.GetLength(0);
        currentCellCount = (uint)matrixArray.GetLength(1);

        // Create and initialize the Grid-supporting object
        gridObject = InformationModel.MakeObject("Grid");
        for (uint rowIndex = 0; rowIndex < currentRowCount; ++rowIndex)
            gridObject.Add(CreateRow(matrixArray, rowIndex));

        LogicObject.Add(gridObject);
        gridModelVariable.Value = gridObject.NodeId;
    }

    /// <summary>
    /// This method creates a row object based on a matrix array and a specified row index.
    /// It constructs a UA (OPC UA) object representing a row in a matrix, with each cell
    /// represented as a variable with a corresponding value from the matrix.
    /// </summary>
    /// <param name="matrixArray">The matrix array containing the values to be used for the row.</param>
    /// <param name="rowIndex">The index of the row in the matrix array to be created.</param>
    /// <returns>
    /// An <see cref="IUAObject"/> representing the created row with all its variables and values.
    /// </returns>
    private IUAObject CreateRow(Array matrixArray, uint rowIndex)
    {
        var rowObject = InformationModel.MakeObject($"Row{rowIndex}");

        // Determine the OPC UA type from the given C# Array
        var netType = matrixArray.GetType().GetElementType().GetTypeInfo();
        var opcuaTypeNodeId = DataTypesHelper.GetDataTypeIdByNetType(netType);
        if (opcuaTypeNodeId == null)
            throw new CoreConfigurationException($"Unable to find an OPC UA data type corresponding to the {netType} .NET type");

        var cellCount = (uint)matrixArray.GetLength(1);
        for (uint cellIndex = 0; cellIndex < cellCount; ++cellIndex)
        {
            // Create the cell variable and register for changes
            var cellVariable = InformationModel.MakeVariable($"Cell{cellIndex}", opcuaTypeNodeId);
            cellVariable.Value = new UAValue(matrixArray.GetValue(rowIndex, cellIndex));
            cellVariableRegistrations.Add(cellVariable.RegisterEventObserver(cellVariableChangeObserver,
                EventType.VariableValueChanged, logicObjectAffinityId));

            // Add the cell variable to the grid
            rowObject.Add(cellVariable);
        }

        return rowObject;
    }

    #endregion

    #region Monitor each element inside MatrixValue
    /// <summary>
    /// This method handles the value change event for a cell variable in the grid.
    /// It updates the corresponding value in the MatrixValue variable.
    /// It first checks if the sender ID matches the logic object sender ID to avoid recursion.
    /// It then retrieves the cell and row indices from the variable's browse name,
    /// and sets the new value in the MatrixValue variable.
    /// </summary>
    /// <param name="variable">The variable that triggered the event.</param>
    /// <param name="newValue">The new value of the variable.</param>
    /// <param name="oldValue">The old value of the variable.</param>
    /// <param name="indexes">The indexes of the variable.</param>
    /// <param name="senderId">The sender ID of the event.</param>
    private void CellVariableValueChanged(IUAVariable variable, UAValue newValue, UAValue oldValue, ElementAccess elementAccess, ulong senderId)
    {
        if (senderId == logicObjectSenderId)
            return;

        var cellBrowseName = variable.BrowseName;
        var cellIndex = uint.Parse(cellBrowseName.Remove(0, "Cell".Length));

        var rowBrowseName = variable.Owner.BrowseName;
        var rowIndex = uint.Parse(rowBrowseName.Remove(0, "Row".Length));

        using (var restorePreviousSenderIdOnExit = LogicObject.Context.SetCurrentThreadSenderId(logicObjectSenderId))
        {
            matrixValueVariable.SetValue(newValue.Value, new uint[] { rowIndex, cellIndex });
        }
    }

    #endregion

    #region Monitor MatrixValue variable
    /// <summary>
    /// This method handles the value change event for the MatrixValue variable.
    /// It updates the grid model based on the new value.
    /// It first checks if the sender ID matches the logic object sender ID to avoid recursion.
    /// It then checks if the new value is an array and updates the grid accordingly.
    /// If the new value is not an array, it updates all cell values.
    /// </summary>
    /// <param name="variable">The variable that triggered the event.</param>
    /// <param name="newValue">The new value of the variable.</param>
    /// <param name="oldValue">The old value of the variable.</param>
    /// <param name="indexes">The indexes of the variable.</param>
    /// <param name="senderId">The sender ID of the event.</param>
    private void MatrixValueVariableValueChanged(IUAVariable variable, UAValue newValue, UAValue oldValue, ElementAccess elementAccess, ulong senderId)
    {
        if (senderId == logicObjectSenderId)
            return;

        if (elementAccess.ArrayIndex.Length > 0)
            UpdateCellValue(newValue, elementAccess.ArrayIndex);
        else
            UpdateAllCellValues((Array)newValue.Value);
    }

    /// <summary>
    /// This method updates the values of all cells in a matrix. It first checks if the matrix dimensions have changed. If they have, it adjusts the grid accordingly. Then it updates each cell's value based on the provided matrix.
    /// </summary>
    /// <param name="matrixArray">The 2D array representing the matrix whose cell values need to be updated.</param>
    private void UpdateAllCellValues(Array matrixArray)
    {
        var rowCount = (uint)matrixArray.GetLength(0);
        var cellCount = (uint)matrixArray.GetLength(1);

        // Rebuild the entire grid model if the number of cells changes
        if (cellCount != currentCellCount)
        {
            CreateGrid(matrixArray);
            return;
        }

        // Add or remove rows if the number of rows changes
        if (rowCount > currentRowCount)
            AddRows(currentRowCount, rowCount - 1, matrixArray);
        else if (rowCount < currentRowCount)
            RemoveLastRows(rowCount, currentRowCount - 1);

        currentRowCount = rowCount;
        currentCellCount = cellCount;

        for (uint rowIndex = 0; rowIndex < rowCount; ++rowIndex)
            for (uint cellIndex = 0; cellIndex < cellCount; ++cellIndex)
                UpdateCellValue(new UAValue(matrixArray.GetValue(rowIndex, cellIndex)), new uint[] { rowIndex, cellIndex });
    }

    /// <summary>
    /// This method adds rows to the grid object. It iterates from the specified 'fromRow' to 'toRow' (inclusive), and for each row index, it creates a new row using the provided 'values' array and adds it to the grid.
    /// </summary>
    /// <param name="fromRow">The starting row index to begin adding.</param>
    /// <param name="toRow">The ending row index to stop adding.</param>
    /// <param name="values">An array of values to use for creating each row.</param>
    private void AddRows(uint fromRow, uint toRow, Array values)
    {
        for (uint rowIndex = fromRow; rowIndex <= toRow; ++rowIndex)
            gridObject.Add(CreateRow(values, rowIndex));
    }

    /// <summary>
    /// This method removes specified rows from a grid object. It iterates from the specified
    /// starting row (fromRow) to the ending row (toRow), inclusive, and deletes each row
    /// in that range.
    /// </summary>
    /// <param name="fromRow">The starting row index to remove.</param>
    /// <param name="toRow">The ending row index to remove (inclusive).</param>
    /// <remarks>
    /// The method assumes that <see cref="gridObject.Get(string)"/> returns a valid row object
    /// for the given row index. If the index is out of bounds, it may throw an exception.
    /// </remarks>
    private void RemoveLastRows(uint fromRow, uint toRow)
    {
        for (uint rowIndex = fromRow; rowIndex <= toRow; ++rowIndex)
        {
            var rowObject = gridObject.Get($"Row{rowIndex}");
            rowObject.Delete();
        }
    }

    /// <summary>
    /// Updates the specified cell value in the grid.
    /// </summary>
    /// <param name="newValue">The new value to set in the cell.</param>
    /// <param name="indexes">An array of two integers representing the row and column indices of the cell.</param>
    private void UpdateCellValue(UAValue newValue, uint[] indexes)
    {
        var cellObject = gridObject.Get($"Row{indexes[0]}").GetVariable($"Cell{indexes[1]}");

        using (var restorePreviousSenderIdOnExit = LogicObject.Context.SetCurrentThreadSenderId(logicObjectSenderId))
        {
            cellObject.Value = newValue;
        }
    }

    #endregion

    private uint logicObjectAffinityId;
    private ulong logicObjectSenderId;

    private IUAVariable matrixValueVariable;
    private IUAVariable gridModelVariable;
    private IUAObject gridObject;
    private uint currentRowCount = 0;
    private uint currentCellCount = 0;

    private IEventObserver matrixValueVariableChangeObserver;
    private IEventObserver cellVariableChangeObserver;
    private IEventRegistration matrixValueVariableRegistration;
    private List<IEventRegistration> cellVariableRegistrations;
}
