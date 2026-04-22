#region Using directives
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.System;
using FTOptix.UI;
using System;
using System.Collections.Generic;
using UAManagedCore;
using FTOptix.SerialPort;
using FTOptix.RecipeX;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.WebUI;
#endregion

public class DateAndTimeWidgetLogic : BaseNetLogic
{
    private const string LOGGING_CATEGORY = nameof(DateAndTimeWidgetLogic);

    /// <summary>
    /// This method retrieves the SystemNode pointer from the owner, checks if it exists, and then retrieves its NodeId.
    /// If the SystemNode is not found or the NodeId is invalid, it logs an error and returns.
    /// It then initializes the time zone combo box bindings and local NTP server interface checkboxes.
    /// </summary>
    /// <remarks>
    /// The method assumes that <see cref="Owner"/> is a valid object with a <see cref="GetVariable"/> method.
    /// It also assumes that <see cref="Log.Error"/> is a valid logging method that accepts a category and message.
    /// </remarks>
    public override void Start()
    {
        #region SystemNode reference
        IUAVariable systemNodePointer = Owner.GetVariable("SystemNode");
        if (systemNodePointer == null)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode NodePointer not found.");
            return;
        }

        NodeId systemNodeId = (NodeId)systemNodePointer.Value;
        if (systemNodeId == null || systemNodeId == NodeId.Empty)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode is not defined.");
            return;
        }

        systemNode = InformationModel.Get(systemNodeId) as FTOptix.System.System;
        if (systemNode == null)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode not found.");
            return;
        }
        #endregion

        InitializeSynchronizationModeRadioButtons();

        InitializeTimeZoneComboBoxBindings();

        InitializeLocalNTPServerInterfacesCheckBoxes();
    }

    /// <summary>
    /// This method releases the resources associated with the system node, time zones enumeration object, synchronization auto mode option, and the LAN/WAN checkboxes.
    /// </summary>
    /// <remarks>
    /// The method sets all referenced objects to null to indicate that they are no longer in use.
    /// </remarks>
    public override void Stop()
    {
        // destruct class objects
        systemNode = null;
        timeZonesEnumerationObject = null;
        synchAutoModeOption = null;
        lanCheckBox = null;
        wanCheckBox = null;
    }

    #region Time Synchronization Mode
    /// <summary>
    /// This method initializes the synchronization mode radio buttons based on the system's synchronization mode.
    /// If the "ModesLayout" is not found, an error is logged and the method returns immediately.
    /// It retrieves the appropriate radio buttons and sets their initial state to unchecked.
    /// If the system's synchronization mode is set to Auto, the "SynchronizationAutoMode" radio button is checked.
    /// If the mode is Manual or PLC, the "SynchronizationManualorPLCMode" radio button is checked.
    /// </summary>
    /// <remarks>
    /// The method assumes that the "ModesLayout" and radio buttons are properly initialized in the UI context.
    /// </remarks>
    private void InitializeSynchronizationModeRadioButtons()
    {
        RowLayout modesLayout = Owner.Get<RowLayout>("ModesLayout");
        if (modesLayout == null)
        {
            Log.Error(LOGGING_CATEGORY, "ModesLayout for radio button not found.");
            return;
        }

        synchAutoModeOption = modesLayout.Get<RadioButton>("SynchronizationAutoMode");
        if (synchAutoModeOption == null)
        {
            Log.Error(LOGGING_CATEGORY, "SynchronizationAutoMode radio button not found.");
            return;
        }

        RadioButton synchManualModeOrPLCOption = modesLayout.Get<RadioButton>("SynchronizationManualorPLCMode");
        if (synchManualModeOrPLCOption == null)
        {
            Log.Error(LOGGING_CATEGORY, "SynchronizationManualMode radio button not found.");
            return;
        }

        synchAutoModeOption.Checked = false;
        synchManualModeOrPLCOption.Checked = false;

        if (systemNode.DateAndTime.SynchronizationMode == TimeSynchronizationMode.Auto)
            synchAutoModeOption.Checked = true;
        else if (systemNode.DateAndTime.SynchronizationMode == TimeSynchronizationMode.Manual || systemNode.DateAndTime.SynchronizationMode == TimeSynchronizationMode.PLC)
            synchManualModeOrPLCOption.Checked = true;
    }
    #endregion

    #region TimeZone Setup
    /// <summary>
    /// This method initializes the time zone combo box bindings.
    /// It retrieves the time zone combo box from the owner and checks if it exists.
    /// If not, an error is logged and the method returns.
    /// It then retrieves the starting time zone from the system node and checks if it is valid.
    /// If the time zone is valid, it initializes the time zones object.
    /// Finally, it sets up the dynamic link for the time zone combo box to bind to the system node's time zone variable.
    /// </summary>
    private void InitializeTimeZoneComboBoxBindings()
    {
        var timeZoneComboBox = Owner.Get<ComboBox>("TimeZoneComboBox");
        if (timeZoneComboBox == null)
        {
            Log.Error(LOGGING_CATEGORY, "TimeZoneComboBox not found.");
            return;
        }
        var startingTimeZone = systemNode.DateAndTime.TimeZone;

        var representativeTimeZone = GetCurrentTimeZone(startingTimeZone);
        if (string.IsNullOrEmpty(representativeTimeZone))
        {
            Log.Error(LOGGING_CATEGORY, $"Time zone {startingTimeZone} is not recognized as a valid time zone.");
            return;
        }

        InitializeTimeZonesObject();

        timeZoneComboBox.SelectedValueVariable.ResetDynamicLink();
        timeZoneComboBox.SelectedValueVariable.SetDynamicLink(systemNode.DateAndTime.TimeZoneVariable, DynamicLinkMode.ReadWrite);
    }

    /// <summary>
    /// Initializes the TimeZonesObject by retrieving it from the Owner's object collection.
    /// If the object is not found, an error is logged.
    /// Then, it iterates over the timeZonesMap to add each time zone pair to the TimeZonesEnumerationObject.
    /// </summary>
    /// <param name="timeZonesEnumerationObject">The object to store the time zone data in.</param>
    /// <param name="timeZonesMap">A dictionary containing time zone pairs to add to the object.</param>
    private void InitializeTimeZonesObject()
    {
        timeZonesEnumerationObject = Owner.GetObject("TimeZonesEnumeration");
        if (timeZonesEnumerationObject == null)
            Log.Error(LOGGING_CATEGORY, "TimeZonesEnumeration object not found.");

        int cnt = 0;
        foreach (var timeZonePair in timeZonesMap)
            AddTimeZone(timeZonePair.Key, timeZonePair.Value, cnt++);
    }

    /// <summary>
    /// This method adds a time zone variable to the timeZonesEnumerationObject.
    /// It creates a new time zone variable with the specified index, sets its value
    /// to the first element of the provided list, and assigns a localized display name
    /// based on the current user's locale.
    /// </summary>
    /// <param name="value">A list of strings representing the time zone value.</param>
    /// <param name="displayName">The display name of the time zone.</param>
    /// <param name="index">The index of the time zone in the enumeration.</param>
    /// <remarks>
    /// If no locale is found for the current user, an error is logged.
    /// </remarks>
    private void AddTimeZone(List<string> value, string displayName, int index)
    {
        var timeZoneVariable = InformationModel.MakeVariable("TimeZone" + index, UAManagedCore.OpcUa.DataTypes.String);
        timeZoneVariable.Value = value[0]; // take first item as value

        var localeId = Session.User.LocaleId;
        if (String.IsNullOrEmpty(localeId))
            Log.Error(LOGGING_CATEGORY, "No locale found for the current user.");

        timeZoneVariable.DisplayName = new LocalizedText("TimeZone" + index + "DisplayName", displayName, localeId);

        timeZonesEnumerationObject.Add(timeZoneVariable);
    }

    /// <summary>
    /// This method retrieves the first character of the specified time zone string.
    /// If the time zone string contains the specified keyword, it returns the first character of the time zone.
    /// If no matching time zone is found, it returns an empty string.
    /// </summary>
    /// <param name="timeZone">The time zone string to search in.</param>
    /// <returns>
    /// The first character of the time zone string if a match is found, otherwise an empty string.
    /// </returns>
    private static string GetCurrentTimeZone(string timeZone)
    {
        // Get the representative timeZone for each group (by default the first element of the group)
        foreach (var timeZonePair in timeZonesMap)
        {
            var currentTimeZone = timeZonePair.Key;
            if (currentTimeZone.Contains(timeZone))
                return currentTimeZone[0];
        }

        return string.Empty;
    }
    #endregion

    #region Local NTP server interfaces

    /// <summary>
    /// This method initializes the local NTP server interface checkboxes.
    /// It retrieves the LAN and WAN checkboxes from the owner and checks if they exist.
    /// If not, an error is logged and the method returns.
    /// It then sets the initial state of the checkboxes based on the system node's local NTP server interfaces.
    /// Finally, it sets the checkboxes to unchecked.
    /// </summary>
    private void InitializeLocalNTPServerInterfacesCheckBoxes()
    {
        lanCheckBox = Owner.Get<CheckBox>("LocalNTPServerLANCheckbox");
        if (lanCheckBox == null)
        {
            Log.Error(LOGGING_CATEGORY, "LocalNTPServerLANCheckbox not found.");
            return;
        }

        wanCheckBox = Owner.Get<CheckBox>("LocalNTPServerWANCheckbox");
        if (wanCheckBox == null)
        {
            Log.Error(LOGGING_CATEGORY, "LocalNTPServerWANCheckbox not found.");
            return;
        }

        lanCheckBox.Checked = false;
        wanCheckBox.Checked = false;

        foreach (string localNTPServerInterface in (Array)systemNode.DateAndTime.LocalNTPServerInterfaces)
        {
            if (localNTPServerInterface == LAN_INTERFACE_NAME)
                lanCheckBox.Checked = true;
            else if (localNTPServerInterface == WAN_INTERFACE_NAME)
                wanCheckBox.Checked = true;
        }
    }
    #endregion

    /// <summary>
    /// This method checks if the specified time synchronization mode is available in the system node.
    /// It retrieves the available synchronization modes from the system node and checks if the specified mode is present.
    /// If the mode is found, it returns true; otherwise, it returns false.
    /// </summary>
    /// <param name="synchMode">The time synchronization mode to check.</param>
    /// <returns>
    /// True if the specified synchronization mode is available; otherwise, false.
    /// </returns>
    private bool IsSynchronizationModeAvailable(TimeSynchronizationMode synchMode)
    {
        if (Array.IndexOf((Array)systemNode.DateAndTime.AvailableSynchronizationModes, synchMode) != -1)
            return true;

        return false;
    }

    /// <summary>
    /// This method reboots the system node if it is defined.
    /// </summary>
    /// <remarks>
    /// The method checks if the systemNode reference is defined. If not, it logs an error and returns.
    /// </remarks>
    [ExportMethod]
    public void Reboot_Device()
    {
        if (systemNode == null)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode reference not defined. Reboot failed.");
            return;
        }

        // reboot the device
        systemNode.Reboot();
    }

    /// <summary>
    /// This method updates the synchronization mode based on user selection.
    /// If the synchronization auto mode option is not found, it logs an error and returns.
    /// If the system node is not defined, it logs an error and returns.
    /// If the auto mode is selected, it sets the synchronization mode to Auto.
    /// If the auto mode is not selected, it checks available modes (PLC, Manual) and sets the mode accordingly.
    /// </summary>
    /// <remarks>
    /// The method assumes the existence of <see cref="synchAutoModeOption"/> and <see cref="systemNode"/>.
    /// It logs errors if either is not found or if no available mode is available.
    /// </remarks>
    [ExportMethod]
    public void SynchronizationModeChanged()
    {
        if (synchAutoModeOption == null)
        {
            Log.Error(LOGGING_CATEGORY, "SynchronizationAutoMode radio button not found.");
            return;
        }

        if (systemNode == null)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode reference not defined. SynchronizationMode update failed.");
            return;
        }

        if (synchAutoModeOption.Checked)
        {
            systemNode.DateAndTime.SynchronizationMode = TimeSynchronizationMode.Auto;
        }
        else
        {
            if (IsSynchronizationModeAvailable(TimeSynchronizationMode.PLC))
            {
                systemNode.DateAndTime.SynchronizationMode = TimeSynchronizationMode.PLC;
            }
            else if (IsSynchronizationModeAvailable(TimeSynchronizationMode.Manual))
            {
                systemNode.DateAndTime.SynchronizationMode = TimeSynchronizationMode.Manual;

            }
            else
            {
                // Invalid Time Synchronization Mode
            }
        }
    }

    /// <summary>
    /// This method updates the local and wide area network (LAN/WAN) NTP server interfaces based on the selected checkboxes.
    /// It checks if the necessary components (checkboxes and system node) are initialized and sets the appropriate NTP interfaces.
    /// </summary>
    /// <remarks>
    /// The method ensures that the system node is properly referenced before attempting to update the NTP server interfaces.
    /// It also handles cases where the LAN or WAN checkboxes are unchecked by adding an empty string to the list of interfaces.
    /// </remarks>
    [ExportMethod]
    public void LocalNTPServerOptionsChanged()
    {
        if (lanCheckBox == null)
        {
            Log.Error(LOGGING_CATEGORY, "LocalNTPServerLANCheckbox not found.");
            return;
        }

        if (wanCheckBox == null)
        {
            Log.Error(LOGGING_CATEGORY, "LocalNTPServerWANCheckbox not found.");
            return;
        }

        if (systemNode == null)
        {
            Log.Error(LOGGING_CATEGORY, "SystemNode reference not defined. LocalNTPServer update failed.");
            return;
        }

        List<string> listOfEnabledLocalNTPServerInterfaces = new();
        if (lanCheckBox.Checked)
            listOfEnabledLocalNTPServerInterfaces.Add(LAN_INTERFACE_NAME);
        else
            listOfEnabledLocalNTPServerInterfaces.Add(""); // Add empty array items to keep same array size for variable value
        if (wanCheckBox.Checked)
            listOfEnabledLocalNTPServerInterfaces.Add(WAN_INTERFACE_NAME);
        else if (wanCheckBox.Enabled)
            listOfEnabledLocalNTPServerInterfaces.Add(""); // Add empty array items to keep same array size for variable value
        systemNode.DateAndTime.LocalNTPServerInterfaces = listOfEnabledLocalNTPServerInterfaces.ToArray();
        listOfEnabledLocalNTPServerInterfaces.Clear();
    }

    private FTOptix.System.System systemNode;

    private IUAObject timeZonesEnumerationObject;

    private RadioButton synchAutoModeOption;
    private CheckBox lanCheckBox;
    private CheckBox wanCheckBox;

    private const string LAN_INTERFACE_NAME = "LAN";
    private const string WAN_INTERFACE_NAME = "WAN";

    #region TimeZonesMap
    private static readonly Dictionary<List<string>, string> timeZonesMap = new()
    {
        { new List<string>(){"UTC"                                                                                               } , "(UTC) Coordinated Universal Time"},
        { new List<string>(){"Africa/Casablanca"                                                                                 } , "(UTC+00:00) Casablanca"},
        { new List<string>(){"Europe/London", "Europe/Dublin", "Europe/Lisbon", "GMT"                                            } , "(UTC+00:00) Dublin, Edinburgh, Lisbon, London"},
        { new List<string>(){"Africa/Monrovia", "Atlantic/Reykjavik"                                                             } , "(UTC+00:00) Monrovia, Reykjavik"},
        { new List<string>(){"CET", "Europe/Amsterdam", "Europe/Berlin", "Europe/Rome", "Europe/Stockholm", "Europe/Vienna"      } , "(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna"},
        { new List<string>(){"Europe/Budapest", "Europe/Belgrade", "Europe/Bratislava", "Europe/Ljubljana", "Europe/Prague"      } , "(UTC+01:00) Belgrade, Bratislava, Budapest, Ljubljana, Prague"},
        { new List<string>(){"Europe/Brussels", "Europe/Copenhagen", "Europe/Madrid", "Europe/Paris"                             } , "(UTC+01:00) Brussels, Copenhagen, Madrid, Paris"},
        { new List<string>(){"Europe/Belgrade", "Europe/Sarajevo", "Europe/Skopje", "Europe/Warsaw", "Europe/Zagreb"             } , "(UTC+01:00) Sarajevo, Skopje, Warsaw, Zagreb"},
        { new List<string>(){"Africa/Lagos"                                                                                      } , "(UTC+01:00) West Central Africa"},
        { new List<string>(){"Africa/Windhoek"                                                                                   } , "(UTC+01:00) Windhoek"},
        { new List<string>(){"Asia/Amman"                                                                                        } , "(UTC+02:00) Amman"},
        { new List<string>(){"Europe/Athens", "Europe/Bucharest"                                                                 } , "(UTC+02:00) Athens, Bucharest"},
        { new List<string>(){"Asia/Beirut"                                                                                       } , "(UTC+02:00) Beirut"},
        { new List<string>(){"Africa/Cairo"                                                                                      } , "(UTC+02:00) Cairo"},
        { new List<string>(){"Europe/Chisinau"                                                                                   } , "(UTC+02:00) Chisinau"},
        { new List<string>(){"Asia/Damascus"                                                                                     } , "(UTC+02:00) Damascus"},
        { new List<string>(){"Asia/Gaza", "Asia/Hebron"                                                                          } , "(UTC+02:00) Gaza, Hebron"},
        { new List<string>(){"Africa/Harare"                                                                                     } , "(UTC+02:00) Harare, Pretoria"},
        { new List<string>(){"Europe/Helsinki", "Europe/Kyiv", "Europe/Riga", "Europe/Sofia", "Europe/Tallinn", "Europe/Vilnius" } , "(UTC+02:00) Helsinki, Kyiv, Riga, Sofia, Tallinn, Vilnius"},
        { new List<string>(){"Asia/Jerusalem"                                                                                    } , "(UTC+02:00) Jerusalem"},
        { new List<string>(){"Europe/Kalining"                                                                                   } , "(UTC+02:00) Kaliningrad"},
        { new List<string>(){"Africa/Tripoli"                                                                                    } , "(UTC+02:00) Tripoli"},
        { new List<string>(){"Asia/Baghdad"                                                                                      } , "(UTC+03:00) Baghdad"},
        { new List<string>(){"Europe/Istanbul"                                                                                   } , "(UTC+03:00) Istanbul"},
        { new List<string>(){"Asia/Riyadh", "Asia/Kuwait"                                                                        } , "(UTC+03:00) Kuwait, Riyadh"},
        { new List<string>(){"Europe/Minsk"                                                                                      } , "(UTC+03:00) Minsk"},
        { new List<string>(){"Europe/Moscow", "Europe/Volgogra"                                                                  } , "(UTC+03:00) Moscow, St. Petersburg, Volgograd"},
        { new List<string>(){"Africa/Nairobi"                                                                                    } , "(UTC+03:00) Nairobi"},
        { new List<string>(){"Asia/Tehran"                                                                                       } , "(UTC+03:30) Tehran"},
        { new List<string>(){"Asia/Dubai"                                                                                        } , "(UTC+04:00) Abu Dhabi, Muscat"},
        { new List<string>(){"Europe/Astrakhan", "Europe/Ulyanovsk"                                                              } , "(UTC+04:00) Astrakhan, Ulyanovsk"},
        { new List<string>(){"Asia/Baku"                                                                                         } , "(UTC+04:00) Baku"},
        { new List<string>(){"Europe/Samara"                                                                                     } , "(UTC+04:00) Izhevsk, Samara"},
        { new List<string>(){"Indian/Mauritius"                                                                                  } , "(UTC+04:00) Port Louis"},
        { new List<string>(){"Asia/Tbilisi"                                                                                      } , "(UTC+04:00) Tbilisi"},
        { new List<string>(){"Asia/Yerevan"                                                                                      } , "(UTC+04:00) Yerevan"},
        { new List<string>(){"Asia/Kabul"                                                                                        } , "(UTC+04:30) Kabul"},
        { new List<string>(){"Asia/Ashgabat", "Asia/Tashkent"                                                                    } , "(UTC+05:00) Ashgabat, Tashkent"},
        { new List<string>(){"Asia/Yekaterinburg"                                                                                } , "(UTC+05:00) Ekaterinburg"},
        { new List<string>(){"Asia/Karachi"                                                                                      } , "(UTC+05:00) Islamabad, Karachi"},
        { new List<string>(){"Asia/Kolkata"                                                                                      } , "(UTC+05:30) Chennai, Kolkata, Mumbai, New Delhi"},
        { new List<string>(){"Asia/Colombo"                                                                                      } , "(UTC+05:30) Sri Jayawardenepura"},
        { new List<string>(){"Asia/Kathmandu"                                                                                    } , "(UTC+05:45) Kathmandu"},
        { new List<string>(){"Asia/Almaty"                                                                                       } , "(UTC+06:00) Astana"},
        { new List<string>(){"Asia/Dhaka"                                                                                        } , "(UTC+06:00) Dhaka"},
        { new List<string>(){"Asia/Omsk"                                                                                         } , "(UTC+06:00) Omsk"},
        { new List<string>(){"Asia/Yangon", "Asia/Rangoon"                                                                       } , "(UTC+06:30) Yangon (Rangoon)"},
        { new List<string>(){"Asia/Bangkok", "Asia/Jakarta"                                                                      } , "(UTC+07:00) Bangkok, Hanoi, Jakarta"},
        { new List<string>(){"Asia/Barnaul"                                                                                      } , "(UTC+07:00) Barnaul, Gorno-Altaysk"},
        { new List<string>(){"Asia/Hovd"                                                                                         } , "(UTC+07:00) Hovd"},
        { new List<string>(){"Asia/Krasnoyarsk"                                                                                  } , "(UTC+07:00) Krasnoyarsk"},
        { new List<string>(){"Asia/Novosibirsk"                                                                                  } , "(UTC+07:00) Novosibirsk"},
        { new List<string>(){"Asia/Tomsk"                                                                                        } , "(UTC+07:00) Tomsk"},
        { new List<string>(){"Asia/Hong_Kong", "Asia/Chongqing", "Asia/Urumqi"                                                   } , "(UTC+08:00) Beijing, Chongqing, Hong Kong, Urumqi"},
        { new List<string>(){"Asia/Irkutsk"                                                                                      } , "(UTC+08:00) Irkutsk"},
        { new List<string>(){"Asia/Singapore", "Asia/Kuala_Lumpur"                                                               } , "(UTC+08:00) Kuala Lumpur, Singapore"},
        { new List<string>(){"Australia/Perth"                                                                                   } , "(UTC+08:00) Perth"},
        { new List<string>(){"Asia/Taipei"                                                                                       } , "(UTC+08:00) Taipei"},
        { new List<string>(){"Asia/Ulaanbaatar"                                                                                  } , "(UTC+08:00) Ulaanbaatar"},
        { new List<string>(){"Asia/Pyongyang"                                                                                    } , "(UTC+08:30) Pyongyang"},
        { new List<string>(){"Australia/Eucla"                                                                                   } , "(UTC+08:45) Eucla"},
        { new List<string>(){"Asia/Chita"                                                                                        } , "(UTC+09:00) Chita"},
        { new List<string>(){"Asia/Tokyo"                                                                                        } , "(UTC+09:00) Osaka, Sapporo, Tokyo"},
        { new List<string>(){"Asia/Seoul"                                                                                        } , "(UTC+09:00) Seoul"},
        { new List<string>(){"Asia/Yakutsk"                                                                                      } , "(UTC+09:00) Yakutsk"},
        { new List<string>(){"Australia/Adelaide"                                                                                } , "(UTC+09:30) Adelaide"},
        { new List<string>(){"Australia/Darwin"                                                                                  } , "(UTC+09:30) Darwin"},
        { new List<string>(){"Australia/Brisbane"                                                                                } , "(UTC+10:00) Brisbane"},
        { new List<string>(){"Australia/Sydney", "Australia/Canberra", "Australia/Melbourne"                                     } , "(UTC+10:00) Canberra, Melbourne, Sydney"},
        { new List<string>(){"Pacific/Guam", "Pacific/Port_Moresby"                                                              } , "(UTC+10:00) Guam, Port Moresby"},
        { new List<string>(){"Australia/Hobart"                                                                                  } , "(UTC+10:00) Hobart"},
        { new List<string>(){"Asia/Vladivostok"                                                                                  } , "(UTC+10:00) Vladivostok"},
        { new List<string>(){"Australia/Lord_Howe"                                                                               } , "(UTC+10:30) Lord Howe Island"},
        { new List<string>(){"Pacific/Bougainville"                                                                              } , "(UTC+11:00) Bougainville Island"},
        { new List<string>(){"Asia/Srednekolymsk"                                                                                } , "(UTC+11:00) Chokurdakh"},
        { new List<string>(){"Asia/Magadan"                                                                                      } , "(UTC+11:00) Magadan"},
        { new List<string>(){"Pacific/Norfolk"                                                                                   } , "(UTC+11:00) Norfolk Island"},
        { new List<string>(){"Asia/Sakhalin"                                                                                     } , "(UTC+11:00) Sakhalin"},
        { new List<string>(){"Pacific/Noumea"                                                                                    } , "(UTC+11:00) Solomon Is., New Caledonia"},
        { new List<string>(){"Asia/Anadyr"                                                                                       } , "(UTC+12:00) Anadyr, Petropavlovsk-Kamchatsky"},
        { new List<string>(){"Pacific/Auckland"                                                                                  } , "(UTC+12:00) Auckland, Wellington"},
        { new List<string>(){"Pacific/Funafuti"                                                                                  } , "(UTC+12:00) Coordinated Universal Time +12"},
        { new List<string>(){"Pacific/Fiji"                                                                                      } , "(UTC+12:00) Fiji"},
        { new List<string>(){"Pacific/Chatham"                                                                                   } , "(UTC+12:45) Chatham Islands"},
        { new List<string>(){"Pacific/Tongatapu"                                                                                 } , "(UTC+13:00) Nuku'alofa"},
        { new List<string>(){"Pacific/Apia"                                                                                      } , "(UTC+13:00) Samoa"},
        { new List<string>(){"Pacific/Kiritimati"                                                                                } , "(UTC+14:00) Kiritimati Island"},
        { new List<string>(){"Atlantic/Azores"                                                                                   } , "(UTC-01:00) Azores"},
        { new List<string>(){"Atlantic/Cape_Verde"                                                                               } , "(UTC-01:00) Cabo Verde Is."},
        { new List<string>(){"America/Noronha"                                                                                   } , "(UTC-02:00) Coordinated Universal Time -02"},
        { new List<string>(){"America/Araguaina"                                                                                 } , "(UTC-03:00) Araguaina"},
        { new List<string>(){"America/Sao_Paulo"                                                                                 } , "(UTC-03:00) Brasilia"},
        { new List<string>(){"America/Buenos_Aires", "America/Argentina/Buenos_Aires"                                            } , "(UTC-03:00) Buenos Aires"},
        { new List<string>(){"America/Cayenne", "America/Fortaleza"                                                              } , "(UTC-03:00) Cayenne, Fortaleza"},
        { new List<string>(){"America/Montevideo"                                                                                } , "(UTC-03:00) Montevideo"},
        { new List<string>(){"America/Miquelon"                                                                                  } , "(UTC-03:00) Saint Pierre and Miquelon"},
        { new List<string>(){"America/St_Johns"                                                                                  } , "(UTC-03:30) Newfoundland"},
        { new List<string>(){"America/Asuncion"                                                                                  } , "(UTC-04:00) Asuncion"},
        { new List<string>(){"America/Halifax"                                                                                   } , "(UTC-04:00) Atlantic Time (Canada)"},
        { new List<string>(){"America/Caracas"                                                                                   } , "(UTC-04:00) Caracas"},
        { new List<string>(){"America/Cuiaba"                                                                                    } , "(UTC-04:00) Cuiaba"},
        { new List<string>(){"America/Manaus", "America/Argentina/San_Juan"                                                      } , "(UTC-04:00) Georgetown, La Paz, Manaus, San Juan"},
        { new List<string>(){"America/Godthab"                                                                                   } , "(UTC-03:00) Greenland"},
        { new List<string>(){"America/Santiago"                                                                                  } , "(UTC-04:00) Santiago"},
        { new List<string>(){"America/Grand_Turk"                                                                                } , "(UTC-04:00) Turks and Caicos"},
        { new List<string>(){"America/Bogota", "America/Lima", "America/Rio_Branco"                                              } , "(UTC-05:00) Bogota, Lima, Quito, Rio Branco"},
        { new List<string>(){"America/Cancun"                                                                                    } , "(UTC-05:00) Chetumal"},
        { new List<string>(){"Canada/Eastern"                                                                                    } , "(UTC-05:00) Eastern Time (US & Canada)"},
        { new List<string>(){"America/Port-au-Prince"                                                                            } , "(UTC-05:00) Haiti"},
        { new List<string>(){"America/Havana"                                                                                    } , "(UTC-05:00) Havana"},
        { new List<string>(){"America/Indiana/Indianapolis"                                                                      } , "(UTC-05:00) Indiana (East)"},
        { new List<string>(){"America/Chicago"                                                                                   } , "(UTC-06:00) Central America"},
        { new List<string>(){"America/Winnipeg"                                                                                  } , "(UTC-06:00) Central Time (US & Canada)"},
        { new List<string>(){"Pacific/Easter"                                                                                    } , "(UTC-06:00) Easter Island"},
        { new List<string>(){"America/Mexico_City"                                                                               } , "(UTC-06:00) Guadalajara, Mexico City, Monterrey"},
        { new List<string>(){"America/Regina"                                                                                    } , "(UTC-06:00) Saskatchewan"},
        { new List<string>(){"America/Phoenix"                                                                                   } , "(UTC-07:00) Arizona"},
        { new List<string>(){"America/Chihuahua", "America/Mazatlan"                                                             } , "(UTC-07:00) Chihuahua, La Paz, Mazatlan"},
        { new List<string>(){"America/Edmonton"                                                                                  } , "(UTC-07:00) Mountain Time (US & Canada)"},
        { new List<string>(){"America/Tijuana"                                                                                   } , "(UTC-08:00) Baja California"},
        { new List<string>(){"Pacific/Pitcairn"                                                                                  } , "(UTC-08:00) Coordinated Universal Time-08"},
        { new List<string>(){"America/Vancouver"                                                                                 } , "(UTC-08:00) Pacific Time (US & Canada)"},
        { new List<string>(){"America/Anchorage"                                                                                 } , "(UTC-09:00) Alaska"},
        { new List<string>(){"Pacific/Gambier"                                                                                   } , "(UTC-09:00) Coordinated Universal Time-09"},
        { new List<string>(){"Pacific/Marquesas"                                                                                 } , "(UTC-09:30) Marquesas Islands"},
        { new List<string>(){"America/Adak"                                                                                      } , "(UTC-10:00) Aleutian Islands"},
        { new List<string>(){"Pacific/Honolulu"                                                                                  } , "(UTC-10:00) Hawaii"},
        { new List<string>(){"Pacific/Pago_Pago"                                                                                 } , "(UTC-11:00) Coordinated Universal Time -11"}
    };
    #endregion
}
