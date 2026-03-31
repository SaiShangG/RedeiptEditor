#region Using directives
using System;
using System.Collections.Generic;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.OPCUAServer;
using FTOptix.RecipeX;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
#endregion

public class BackProvider : BaseNetLogic
{
	public override void Start()
	{
		oldPanelStack = new Stack<NodeId>();

		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");
		panelLoader.PanelVariable.VariableChange += PanelVariable_VariableChange;
	}

	/// <summary>
	/// Handles the change event for a panel variable.
	/// Updates the old panel and its node ID before pushing it onto the stack.
	/// </summary>
	/// <param name="sender">Event source.</param>
	/// <param name="e">Event arguments containing the old value.</param>
	/// <remarks>
	/// Old panel is updated from the old value, and its node ID is extracted.
	/// The old panel's node ID is then pushed onto the stack.
	/// </remarks>
	private void PanelVariable_VariableChange(object sender, VariableChangeEventArgs e)
	{
		var oldPanel = InformationModel.Get(e.OldValue);
		NodeId oldPanelNodeId = e.OldValue;
		oldPanelStack.Push(oldPanelNodeId);
	}

	public override void Stop()
	{
		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");

		panelLoader.PanelVariable.VariableChange -= PanelVariable_VariableChange;
	}

	/// <summary>
	/// Method to navigate back through panels.
	/// If no panel loader or stack is available, logs an error message.
	/// Otherwise, pops the top panel from the stack, unregisters the variable change event,
	/// changes the panel using the provided node ID, and then reenables the variable change event.
	/// </summary>
	/// <remarks>
	/// If there are no panels left on the stack when navigating back, this method does nothing.
	/// </remarks>
	[ExportMethod]
	public void Back()
	{
		var panelLoader = Owner as PanelLoader;
		if (panelLoader == null)
			Log.Error("BackProvider", "Panel loader not found");

		if (oldPanelStack.Count == 0)
			return;

		var panelNodeId = oldPanelStack.Pop();
		panelLoader.PanelVariable.VariableChange -= PanelVariable_VariableChange;
		panelLoader.ChangePanel(panelNodeId, NodeId.Empty);
		panelLoader.PanelVariable.VariableChange += PanelVariable_VariableChange;
	}

	private Stack<NodeId> oldPanelStack;
}
