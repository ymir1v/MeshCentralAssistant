﻿/*
Copyright 2009-2021 Intel Corporation

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.IO.Pipes;
using System.Collections;
using System.ServiceProcess;
using System.Security.Principal;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MeshAssistant
{
    public class MeshAgent
    {
        private static ServiceController agentService = null;
        private byte[] pipeBuffer = new byte[65535];
        private bool pipeWritePending = false;
        private ArrayList pipeWrites = new ArrayList();
        private NamedPipeClientStream pipeClient = null;
        public int State = 0;
        public int ServerState = 0;
        public Uri ServerUri = null;
        public bool ServiceAgent = true;
        public bool IntelAmtSupport = false;
        private string selfExecutableHashHex = null;
        private string softwareName = null;
        private string serviceName = null;
        private string[] nodeids = null;

        // Sessions
        public Dictionary<string, object> DesktopSessions = null;
        public Dictionary<string, object> TerminalSessions = null;
        public Dictionary<string, object> FilesSessions = null;
        public Dictionary<string, object> TcpSessions = null;
        public Dictionary<string, object> UdpSessions = null;
        public Dictionary<string, object> MessagesSessions = null;

        public delegate void onQueryResultHandler(string value, string result);
        public event onQueryResultHandler onQueryResult;

        public delegate void onStateChangedHandler(int state, int serverState);
        public event onStateChangedHandler onStateChanged;

        public delegate void onSessionChangedHandler();
        public event onSessionChangedHandler onSessionChanged;

        public delegate void onAmtStateHandler(Dictionary<string, object> state);
        public event onAmtStateHandler onAmtState;

        public delegate void onSelfUpdateHandler(string name, string hash, string url, string serverhash);
        public event onSelfUpdateHandler onSelfUpdate;

        public delegate void onCancelHelpHandler();
        public event onCancelHelpHandler onCancelHelp;

        public delegate void onConsoleHandler(string str);
        public event onConsoleHandler onConsoleMessage;

        public static ServiceControllerStatus GetServiceStatus() { agentService.Refresh(); return agentService.Status; }
        public static void StartService() { try { agentService.Start(); } catch (Exception) { } }
        public static void StopService() { try { agentService.Stop(); } catch (Exception) { } }

        public MeshAgent(string softwareName, string serviceName, string nodeids)
        {
            this.softwareName = softwareName;
            this.serviceName = serviceName;
            this.nodeids = null;
            if (nodeids != null) { this.nodeids = nodeids.Split(','); }
            agentService = new ServiceController(serviceName);

            if (softwareName != null)
            {
                // Hash our own executable
                byte[] selfHash;
                using (var sha384 = SHA384Managed.Create()) { using (var stream = File.OpenRead(System.Reflection.Assembly.GetEntryAssembly().Location)) { selfHash = sha384.ComputeHash(stream); } }
                selfExecutableHashHex = BitConverter.ToString(selfHash).Replace("-", string.Empty).ToLower();
            }
        }

        /// <summary>
        /// Return a list of agent names and comma seperated nodeid's
        /// </summary>
        /// <returns></returns>
        public static Dictionary<string, string> GetAgentInfo(string selectedAgentName)
        {
            Dictionary<string, string> connections = new Dictionary<string, string>();
            for (var i = 0; i < 4; i++) { GetAgentInfoEx(connections, i, selectedAgentName); }
            return connections;
        }

        private static int GetAgentInfoEx(Dictionary<string, string> connections, int flags, string selectedAgentName)
        {
            int added = 0;
            try
            {
                RegistryKey localKey = RegistryKey.OpenBaseKey(((flags & 1) == 0)?Microsoft.Win32.RegistryHive.LocalMachine: Microsoft.Win32.RegistryHive.CurrentUser, ((flags & 2) == 0) ? RegistryView.Registry32 : RegistryView.Registry64);
                localKey = localKey.OpenSubKey(@"SOFTWARE\Open Source");
                if (localKey != null)
                {
                    string[] subKeys = localKey.GetSubKeyNames();
                    foreach (string subKey in subKeys)
                    {
                        if (subKey == "MeshAgent2") continue; // This is the old agent key, ignore it.
                        if ((selectedAgentName != null) && (selectedAgentName != subKey)) continue; // This is not an agent we care about
                        try
                        {
                            RegistryKey localKey2 = localKey.OpenSubKey(subKey);
                            if (localKey2.GetValueKind("NodeId") == RegistryValueKind.String)
                            {
                                string nodeid = localKey2.GetValue("NodeId").ToString().Replace('@', '+').Replace('$', '/');
                                if (connections.ContainsKey(subKey))
                                {
                                    string ids = connections[subKey];
                                    if (ids.IndexOf(nodeid) == -1) { ids += (',' + nodeid); connections[subKey] = ids; }
                                }
                                else
                                {
                                    connections.Add(subKey, nodeid);
                                }
                                added++;
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return added;
        }

        public string GetNodeId32()
        {
            try
            {
                RegistryKey localKey32 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry32);
                localKey32 = localKey32.OpenSubKey(@"SOFTWARE\Open Source\" + serviceName);
                if (localKey32 != null)
                {
                    string nodeidb64 = localKey32.GetValue("NodeId").ToString().Replace('@', '+').Replace('$', '/');
                    return string.Concat(Convert.FromBase64String(nodeidb64).Select(b => b.ToString("X2")));
                }
            }
            catch (Exception) { }
            return null;
        }


        public string GetNodeId64()
        {
            try
            {
                RegistryKey localKey64 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
                localKey64 = localKey64.OpenSubKey(@"SOFTWARE\Open Source\" + serviceName);
                if (localKey64 != null)
                {
                    string nodeidb64 = localKey64.GetValue("NodeId").ToString().Replace('@', '+').Replace('$', '/');
                    return string.Concat(Convert.FromBase64String(nodeidb64).Select(b => b.ToString("X2")));
                }
            }
            catch (Exception) { }
            return null;
        }

        public string GetUserNodeId32()
        {
            try
            {
                RegistryKey localKey32 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry32);
                localKey32 = localKey32.OpenSubKey(@"SOFTWARE\Open Source\" + serviceName);
                if (localKey32 != null)
                {
                    string nodeidb64 = localKey32.GetValue("NodeId").ToString().Replace('@', '+').Replace('$', '/');
                    return string.Concat(Convert.FromBase64String(nodeidb64).Select(b => b.ToString("X2")));
                }
            }
            catch (Exception) { }
            return null;
        }


        public string GetUserNodeId64()
        {
            try
            {
                RegistryKey localKey64 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry64);
                localKey64 = localKey64.OpenSubKey(@"SOFTWARE\Open Source\" + serviceName);
                if (localKey64 != null)
                {
                    string nodeidb64 = localKey64.GetValue("NodeId").ToString().Replace('@', '+').Replace('$', '/');
                    return string.Concat(Convert.FromBase64String(nodeidb64).Select(b => b.ToString("X2")));
                }
            }
            catch (Exception) { }
            return null;
        }

        private void ChangeState(int state, int serverState)
        {
            if ((State == state) && (ServerState == serverState)) return;
            State = state;
            if (State == 0) { ServerState = 0; } else { ServerState = serverState; }
            if (onStateChanged != null) { onStateChanged(State, ServerState); }
            if (serverState == 1) SendSelfUpdateQuery();
        }

        public bool SendCommand(string cmd, string value)
        {
            if ((pipeClient == null) || (pipeClient.IsConnected == false)) return false;
            string data = "{\"cmd\":\"" + cmd + "\",\"value\":\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
            byte[] buf2 = UTF8Encoding.UTF8.GetBytes(data);
            byte[] buf1 = BitConverter.GetBytes(buf2.Length + 4);
            byte[] buf = new byte[4 + buf2.Length];
            Array.Copy(buf1, 0, buf, 0, 4);
            Array.Copy(buf2, 0, buf, 4, buf2.Length);
            if (pipeWritePending == true) {
                pipeWrites.Add(buf);
            } else {
                pipeWritePending = true;
                pipeClient.BeginWrite(buf, 0, buf.Length, new AsyncCallback(WritePipe), null);
            }
            return true;
        }

        public bool SendSelfUpdateQuery()
        {
            if ((pipeClient == null) || (pipeClient.IsConnected == false) || (softwareName == null) || (selfExecutableHashHex == null)) return false;
            string data = "{\"cmd\":\"meshToolInfo\",\"name\":\"" + softwareName + "\",\"hash\":\"" + selfExecutableHashHex + "\",\"cookie\":true}";
            byte[] buf2 = UTF8Encoding.UTF8.GetBytes(data);
            byte[] buf1 = BitConverter.GetBytes(buf2.Length + 4);
            byte[] buf = new byte[4 + buf2.Length];
            Array.Copy(buf1, 0, buf, 0, 4);
            Array.Copy(buf2, 0, buf, 4, buf2.Length);
            if (pipeWritePending == true)
            {
                pipeWrites.Add(buf);
            }
            else
            {
                pipeWritePending = true;
                pipeClient.BeginWrite(buf, 0, buf.Length, new AsyncCallback(WritePipe), null);
            }
            return true;
        }

        private void WritePipe(IAsyncResult r)
        {
            pipeClient.EndWrite(r);
            if (pipeWrites.Count > 0)
            {
                byte[] buf = (byte[])pipeWrites[0];
                pipeWrites.RemoveAt(0);
                pipeClient.BeginWrite(buf, 0, buf.Length, new AsyncCallback(WritePipe), null);
            }
            else
            {
                pipeWritePending = false;
            }
        }

        public bool ConnectPipe()
        {
            string userName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            if (pipeClient != null) return true;

            if (nodeids != null)
            {
                foreach (string n in nodeids) {
                    string nodeidhex = string.Concat(Convert.FromBase64String(n).Select(b => b.ToString("X2")));
                    pipeClient = new NamedPipeClientStream(".", nodeidhex + "-DAIPC", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.None);
                    try { pipeClient.Connect(10); } catch (Exception) { pipeClient = null; }
                    if ((pipeClient != null) && pipeClient.IsConnected)
                    {
                        ServiceAgent = true;
                        ChangeState(1, 0);
                        SendCommand("register", userName);
                        SendCommand("sessions", "");
                        pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
                        return true;
                    }
                }
                return false;
            }
            else
            {
                string xnodeid = GetNodeId64();
                if (xnodeid != null)
                {
                    pipeClient = new NamedPipeClientStream(".", xnodeid + "-DAIPC", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.None);
                    try { pipeClient.Connect(10); } catch (Exception) { pipeClient = null; }
                    if ((pipeClient != null) && pipeClient.IsConnected)
                    {
                        ServiceAgent = true;
                        ChangeState(1, 0);
                        SendCommand("register", userName);
                        SendCommand("sessions", "");
                        pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
                        return true;
                    }
                    pipeClient = null;
                }

                xnodeid = GetNodeId32();
                if (xnodeid != null)
                {
                    pipeClient = new NamedPipeClientStream(".", xnodeid + "-DAIPC", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    try { pipeClient.Connect(10); } catch (Exception) { pipeClient = null; }
                    if ((pipeClient != null) && pipeClient.IsConnected)
                    {
                        ServiceAgent = true;
                        ChangeState(1, 0);
                        SendCommand("register", userName);
                        SendCommand("sessions", "");
                        pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
                        return true;
                    }
                    pipeClient = null;
                }

                xnodeid = GetUserNodeId64();
                if (xnodeid != null)
                {
                    pipeClient = new NamedPipeClientStream(".", xnodeid + "-DAIPC", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    try { pipeClient.Connect(10); } catch (Exception) { pipeClient = null; }
                    if ((pipeClient != null) && pipeClient.IsConnected)
                    {
                        ServiceAgent = false;
                        ChangeState(1, 0);
                        SendCommand("register", userName);
                        SendCommand("sessions", "");
                        pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
                        return true;
                    }
                    pipeClient = null;
                }

                xnodeid = GetUserNodeId32();
                if (xnodeid != null)
                {
                    pipeClient = new NamedPipeClientStream(".", xnodeid + "-DAIPC", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    try { pipeClient.Connect(10); } catch (Exception) { pipeClient = null; }
                    if ((pipeClient != null) && pipeClient.IsConnected)
                    {
                        ServiceAgent = false;
                        ChangeState(1, 0);
                        SendCommand("register", userName);
                        SendCommand("sessions", "");
                        pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
                        return true;
                    }
                    pipeClient = null;
                }
            }
            return false;
        }

        public void DisconnectPipe()
        {
            if (pipeClient != null) { pipeClient.Close(); }
            ServerState = 0;
            ServerUri = null;
            pipeWritePending = false;
            pipeWrites = new ArrayList();
            DesktopSessions = null;
            TerminalSessions = null;
            FilesSessions = null;
            TcpSessions = null;
            UdpSessions = null;
            MessagesSessions = null;
        }

        public bool QueryDescriptors()
        {
            return SendCommand("query", "descriptors");
        }

        public bool RequestHelp(string details)
        {
            return SendCommand("requesthelp", details);
        }

        public bool CancelHelpRequest()
        {
            return SendCommand("cancelhelp", "");
        }

        public bool RequestIntelAmtState()
        {
            return SendCommand("amtstate", "");
        }

        public bool SendConsoleCommand(string cmd)
        {
            return SendCommand("console", cmd);
        }

        private void ReadPipe(IAsyncResult r)
        {
            int len = pipeClient.EndRead(r);
            if (len == 0) { pipeClient.Dispose(); pipeClient = null; ServiceAgent = true; ChangeState(0, 0); return; }
            if (len < 4) { return; }

            int chunklen = BitConverter.ToInt32(pipeBuffer, 0);
            string data = UTF8Encoding.UTF8.GetString(pipeBuffer, 4, chunklen - 4);

            // Parse the received JSON
            Dictionary<string, object> jsonAction = new Dictionary<string, object>();
            jsonAction = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(data);
            if (jsonAction.ContainsKey("cmd") == false) return;
            if (jsonAction != null && jsonAction["cmd"].GetType() == typeof(string))
            {
                string action = jsonAction["cmd"].ToString();
                switch (action)
                {
                    case "serverstate":
                        {
                            if (jsonAction.ContainsKey("url") && (jsonAction["url"] != null)) { try { ServerUri = new Uri(jsonAction["url"].ToString().Replace("wss://", "https://").Replace("ws://", "http://").Replace("/agent.ashx", "")); } catch (Exception) { ServerUri = null; } } else { ServerUri = null; }
                            if (jsonAction.ContainsKey("amt")) { IntelAmtSupport = (bool)jsonAction["amt"]; }
                            ChangeState(1, int.Parse(jsonAction["value"].ToString()));
                            break;
                        }
                    case "query":
                        {
                            string value = jsonAction["value"].ToString();
                            string result = jsonAction["result"].ToString().Replace("\n", "\r\n");
                            if (onQueryResult != null) { onQueryResult(value, result); }
                            break;
                        }
                    case "sessions":
                        {
                            if (jsonAction.ContainsKey("sessions") == false) return;
                            Dictionary<string, object> jsonSessions = (Dictionary<string, object>)jsonAction["sessions"];
                            if (jsonSessions.ContainsKey("desktop")) { DesktopSessions = (Dictionary<string, object>)jsonSessions["desktop"]; }
                            if (jsonSessions.ContainsKey("terminal")) { TerminalSessions = (Dictionary<string, object>)jsonSessions["terminal"]; }
                            if (jsonSessions.ContainsKey("files")) { FilesSessions = (Dictionary<string, object>)jsonSessions["files"]; }
                            if (jsonSessions.ContainsKey("tcp")) { TcpSessions = (Dictionary<string, object>)jsonSessions["tcp"]; }
                            if (jsonSessions.ContainsKey("udp")) { UdpSessions = (Dictionary<string, object>)jsonSessions["udp"]; }
                            if (jsonSessions.ContainsKey("msg")) { MessagesSessions = (Dictionary<string, object>)jsonSessions["msg"]; }
                            if (onSessionChanged != null) { onSessionChanged(); }
                            break;
                        }
                    case "amtstate":
                        {
                            if (jsonAction.ContainsKey("value") == false) return;
                            Dictionary<string, object> amtstate = (Dictionary<string, object>)jsonAction["value"];
                            if (onAmtState != null) { onAmtState(amtstate); }
                            break;
                        }
                    case "meshToolInfo":
                        {
                            string name = null;
                            string hash = null;
                            string url = null;
                            string serverhash = null;
                            if (jsonAction.ContainsKey("name")) { name = jsonAction["name"].ToString(); } // Download tool name
                            if (jsonAction.ContainsKey("hash")) { hash = jsonAction["hash"].ToString(); } // File Hash
                            if (jsonAction.ContainsKey("url")) { url = jsonAction["url"].ToString(); } // Server url
                            if (jsonAction.ContainsKey("serverhash")) { serverhash = jsonAction["serverhash"].ToString(); } // Server TLS certificate hash
                            if ((name != null) && (hash != null) && (url != null) && (onSelfUpdate != null) && (name == softwareName)) { onSelfUpdate(name, hash, url, serverhash); }
                            break;
                        }
                    case "cancelhelp":
                        {
                            if (onCancelHelp != null) { onCancelHelp(); }
                            break;
                        }
                    case "console":
                        {
                            string cmd = null;
                            if (jsonAction.ContainsKey("value")) { cmd = jsonAction["value"].ToString(); } // Download tool name
                            if ((cmd != null) && (onConsoleMessage != null)) { onConsoleMessage(cmd); }
                            break;
                        }
                }
            }

            pipeClient.BeginRead(pipeBuffer, 0, pipeBuffer.Length, new AsyncCallback(ReadPipe), null);
        }

    }
}