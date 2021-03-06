﻿using System;
using System.Text;
using System.Reflection;
using System.Globalization;
using Communication.Interface;
using Communication.Interface.UI;
using System.Threading;
using System.Collections.Generic;
using System.IO;

namespace Communication.Interface
{
    /// <summary>
    /// Communication interface factory class
    /// Normally user will use this class to instance communication interface
    /// </summary>
    public class CommunicationManager
    {
        private static readonly object locker = new object();
        private static CommunicationViewer viewer = null;

        /// <summary>
        /// Query communication interface implementation from external assembly which locat in current assembly directory.
        /// And also current assembly for build in interface implementation.
        /// </summary>
        /// <returns>dictionary for scheme and interface implementation pair</returns>
        private static Dictionary<string, InterfaceImplementation> QueryImplementation()
        {
            Dictionary<string, InterfaceImplementation> ImplementationDictionary = new Dictionary<string, InterfaceImplementation>();
            string AssemblyPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof(CommunicationManager)).Location);
            string[] DLLFileNames = Directory.GetFiles(AssemblyPath, "*.dll");

            // Query from external assemblies
            foreach (string FileName in DLLFileNames)
            {
                Assembly DLLAssembly = null;
                try
                {
                    DLLAssembly = Assembly.LoadFrom(FileName);
                    if (DLLAssembly != null)
                    {
                        QueryImplementationFromAssembly(ImplementationDictionary, DLLAssembly);
                    }
                }
                catch (Exception) { }   // Load win32 dll will cause exception, Ignore this kind of exception
            }

            // Query implementation in current assembly
            QueryImplementationFromAssembly(ImplementationDictionary, System.Reflection.Assembly.GetExecutingAssembly());
            return ImplementationDictionary;
        }

        private static void QueryImplementationFromAssembly(Dictionary<string, InterfaceImplementation> ImplementationDictionary, Assembly Assembly)
        {
            Type[] Types = Assembly.GetTypes();

            foreach (Type ClassType in Types)
            {
                InterfaceImplementationAttribute[] ImplementationAttributes = (InterfaceImplementationAttribute[])ClassType.GetCustomAttributes(typeof(InterfaceImplementationAttribute), false);

                foreach (InterfaceImplementationAttribute Attribute in ImplementationAttributes)
                {
                    if (ImplementationDictionary.ContainsKey(Attribute.Scheme))
                    {
                        // Update if scheme exist in dictionary, so only last discovered implementation available
                        ImplementationDictionary[Attribute.Scheme] = new InterfaceImplementation(Attribute.Name, Attribute.Scheme, ClassType, Attribute.ConfigPanel);
                    }
                    else
                    {
                        // Add new item if scheme not exist in dictionary
                        ImplementationDictionary.Add(Attribute.Scheme, new InterfaceImplementation(Attribute.Name, Attribute.Scheme, ClassType, Attribute.ConfigPanel));
                    }
                }   
            }
        }

        /// <summary>
        /// Instance communication interface based on connection string
        /// </summary>
        /// <param name="ConnectionString">connection string input, example: 
        /// SerialPort: Port=COM3,BaudRate=38400,DataBits=8,Parity=None,StopBits=One
        /// Telnet: IP=127.0.0.1,Port=23
        /// </param>
        /// <returns>interface instance created by this method, null if failed</returns>
        public static ICommunicationInterface InstanceInterface(string ConnectionString)
        {
            return InstanceInterface(ConnectionString, string.Empty);
        }

        /// <summary>
        /// Instance communication interface based on connection string
        /// </summary>
        /// <param name="ConnectionString">connection string input, example: 
        /// SerialPort: Port=COM3,BaudRate=38400,DataBits=8,Parity=None,StopBits=One
        /// Telnet: IP=127.0.0.1,Port=23
        /// </param>
        /// <param name="FriendlyName">Firiednly Name will display in communication viewer to present the interface</param>
        /// <returns>interface instance created by this method, null if failed</returns>
        public static ICommunicationInterface InstanceInterface(string ConnectionString, string FriendlyName)
        {
            return InstanceInterface(ConnectionString, FriendlyName, true);
        }
        
        /// <summary>
        /// Instance communication interface based on connection string
        /// </summary>
        /// <param name="ConnectionString">connection string input, example: 
        /// SerialPort: Port=COM3,BaudRate=38400,DataBits=8,Parity=None,StopBits=One
        /// Telnet: IP=127.0.0.1,Port=23
        /// </param>
        /// <param name="FriendlyName">Firiednly Name will display in communication viewer to present the interface</param>
        /// <param name="ClearPrevious">Clear previous log in communication viewer</param>
        /// <returns>interface instance created by this method, null if failed</returns>
        public static ICommunicationInterface InstanceInterface(string ConnectionString, string FriendlyName, bool ClearPrevious)
        {
            ICommunicationInterface CommunicationInterface = null;
            string[] ConnStr = ConnectionString.Split(new char[] { ':' });
            string Scheme = ConnStr[0];

            InterfaceImplementation Implementation = QueryImplementation()[Scheme];
            if (Implementation != null)
            {
                CommunicationInterface = Implementation.Instance(ConnectionString.Substring(Scheme.Length+1), FriendlyName);
                if (CommunicationInterface != null && !string.IsNullOrEmpty(FriendlyName))
                {
                    GetViewer().AttachInterface(CommunicationInterface, ClearPrevious);
                }
            }
            return CommunicationInterface;
        }

        /// <summary>
        ///  Deattach communication interface from communication viewer
        /// </summary>
        /// <param name="CommunicationInterface">Communication interface</param>
        public static void DeattachInterface(ICommunicationInterface CommunicationInterface)
        {
            if (CommunicationInterface != null)
            {
                GetViewer().DeattachInterface(CommunicationInterface);
            }
        }


        /// <summary>
        /// Display communication builder dialog
        /// </summary>
        /// <returns>Connection string generated by builder dialog</returns>
        public static string ShowCommunicationBuilder()
        {
            CommunicationBuilder Builder = new CommunicationBuilder(QueryImplementation());
            if (Builder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                return Builder.ConnectionString;
            }
            return string.Empty;
        }

        /// <summary>
        /// Get communiction viewer instance
        /// </summary>
        /// <returns>communiction viewer instance</returns>
        public static ICommunicationViewer GetCommunicationViewer()
        {
            return GetViewer();
        }

        public static ICommunicationViewer GetViewer()
        {
            if (viewer == null)
            {
                InitViewer(DockType.None);
            }

            return viewer;
        }

        /// <summary>
        /// Initial communiction viewer
        /// </summary>
        /// <returns>communiction viewer instance</returns>
        public static ICommunicationViewer InitCommunicationViewer()
        {
            return InitViewer();
        }

        public static ICommunicationViewer InitViewer()
        {
            return InitViewer(DockType.None);
        }

                /// <summary>
        /// Initial communiction viewer
        /// </summary>
        /// <param name="Dock">Specifies the position which communication viewer will dock to.</param>
        /// <returns>communiction viewer instance</returns>
        public static ICommunicationViewer InitCommunicationViewer(DockType Dock)
        {
            return InitViewer(Dock);
        }

        public static ICommunicationViewer InitViewer(UI.DockType DockType)
        {
            if (viewer == null)
            {
                viewer = new CommunicationViewer(DockType);
            }

            return viewer;
        }

        /// <summary>
        /// Show the communicaiton viewer dialog
        /// </summary>
        public static void ShowCommunicationViewer()
        {
            ShowViewer();
        }

        public static void ShowViewer()
        {
            GetViewer().ShowViewer();
        }

        /// <summary>
        /// Hide the communicaiton viewer dialog
        /// </summary>
        public static void HideCommunicationViewer()
        {
            HideViewer();
        }

        public static void HideViewer()
        {
            GetViewer().HideViewer();
        }

        /// <summary>
        /// Release the communication viewer
        /// </summary>
        public static void Cleanup()
        {
            GetViewer().Release();
        } 
    }
}
