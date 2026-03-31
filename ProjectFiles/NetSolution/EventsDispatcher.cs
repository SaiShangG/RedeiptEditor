#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.HMIProject;
using FTOptix.Retentivity;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.EventLogger;
using FTOptix.Store;
using FTOptix.Recipe;
using FTOptix.SQLiteStore;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
#endregion

public class EventsDispatcher : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]
    public void TriggerUserSessionEvent(object[] inputArgs)
    {
        // Convert input arguments to the expected types
        var sourceName = (string) inputArgs[0];
        var sourceNode = (NodeId) inputArgs[1];
        var clientUserId = (string) inputArgs[2];
        var status = (bool) inputArgs[3];
        var message = new LocalizedText((string) inputArgs[4], "en-US");

        // Get the UserSessionEvent object type
        var userSessionEvent = (IUAObjectType) InformationModel.Get(FTOptix.Core.ObjectTypes.UserSessionEvent);

        // Create a list to store the event arguments
        List<object> argumentList = [];
        if (userSessionEvent.EventArguments != null)
        {
            argumentList.AddRange(new object[userSessionEvent.EventArguments.GetFields().Count]);
            foreach (var field in userSessionEvent.EventArguments.GetFields())
            {
                // Set the field value based on the field name
                object fieldValue = field switch
                {
                    "EventId" => GenerateEventIdFromNodeId((Guid) Session.NodeId.Id),
                    "EventType" => FTOptix.Core.ObjectTypes.UserSessionEvent,
                    "SourceName" => sourceName,
                    "SourceNode" => sourceNode,
                    "ClientUserId" => clientUserId,
                    "Status" => status,
                    "Message" => message,
                    "Time" => DateTime.Now,
                    "Severity" => new Random().Next(1, 500),
                    _ => null
                };
                if (fieldValue != null)
                {
                    userSessionEvent.EventArguments.SetFieldValue(argumentList, field, fieldValue);
                }
            }
            // Dispatch the event to the Server object
            LogicObject.Context.GetObject(OpcUa.Objects.Server).DispatchUAEvent(FTOptix.Core.ObjectTypes.UserSessionEvent, argumentList.AsReadOnly());
        }
    }

    public static ByteString GenerateEventIdFromNodeId(Guid nodeId)
    {
        string baseString = nodeId.ToString() + DateTime.UtcNow.Ticks.ToString();
        using SHA256 sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        return new ByteString(hashBytes);
    }
}
