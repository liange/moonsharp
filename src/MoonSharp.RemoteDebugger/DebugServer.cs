﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.RemoteDebugger.Network;
using MoonSharp.RemoteDebugger.Threading;

namespace MoonSharp.RemoteDebugger
{
	public class DebugServer : IDebugger
	{
		List<DynamicExpression> m_Watches = new List<DynamicExpression>();
		HashSet<string> m_WatchesChanging = new HashSet<string>();
		Utf8TcpServer m_Server;
		Script m_Script;
		string m_AppName;
		object m_Lock = new object();
		BlockingQueue<DebuggerAction> m_QueuedActions = new BlockingQueue<DebuggerAction>();
		SourceRef m_LastSentSourceRef = null;
		bool m_InGetActionLoop = false;
		bool m_HostBusySent = false;
		private bool m_RequestPause = false;
		string[] m_CachedWatches = new string[(int)WatchType.MaxValue];


		public DebugServer(string appName, Script script, int port, bool localOnly)
		{
			m_AppName = appName;

			m_Server = new Utf8TcpServer(port, 1 << 20, '\0', localOnly ? Utf8TcpServerOptions.LocalHostOnly : Utf8TcpServerOptions.Default);
			m_Server.Start();
			m_Server.DataReceived += m_Server_DataReceived;
			m_Server.ClientConnected += m_Server_ClientConnected;
			m_Script = script;
		}

		void m_Server_ClientConnected(object sender, Utf8TcpPeerEventArgs e)
		{
			SendWelcome();

			for (int i = 0; i < m_Script.SourceCodeCount; i++)
				SetSourceCode(m_Script.GetSourceCode(i));
		}

		#region Writes


		public void SetSourceCode(SourceCode sourceCode)
		{
			Send(xw =>
			{
				using (xw.Element("source-code"))
				{
					xw.Attribute("id", sourceCode.SourceID)
						.Attribute("name", sourceCode.Name);

					foreach (string line in sourceCode.Lines)
						xw.ElementCData("l", EpurateNewLines(line));
				}
			});
		}

		private string EpurateNewLines(string line)
		{
			return line.Replace('\n', ' ').Replace('\r', ' ');
		}


		private void Send(Action<XmlWriter> a)
		{
			XmlWriterSettings xs = new XmlWriterSettings()
			{
				CheckCharacters = true,
				CloseOutput = true,
				ConformanceLevel = ConformanceLevel.Fragment,
				Encoding = Encoding.UTF8,
				Indent = false,
			};

			StringBuilder sb = new StringBuilder();
			XmlWriter xw = XmlWriter.Create(sb, xs);

			a(xw);

			xw.Close();

			string xml = sb.ToString();
			m_Server.BroadcastMessage(xml);
			//Console.WriteLine(xml);
		}


		private void SendWelcome()
		{
			Send(xw =>
			{
				using (xw.Element("welcome"))
				{
					xw.Attribute("app", m_AppName)
						.Attribute("moonsharpver", Assembly.GetAssembly(typeof(Script)).GetName().Version.ToString());

				}
			});
		}

		public void Update(WatchType watchType, IEnumerable<WatchItem> items)
		{
			if (watchType != WatchType.CallStack && watchType != WatchType.Watches)
				return;

			int watchIdx = (int)watchType;

			string watchHash = string.Join("|", items.Select(l => l.ToString()).ToArray());

			if (m_CachedWatches[watchIdx] == null || m_CachedWatches[watchIdx] != watchHash)
			{
				m_CachedWatches[watchIdx] = watchHash;

				Send(xw =>
				{
					using (xw.Element(watchType.ToString().ToLowerInvariant()))
					{
						foreach (WatchItem wi in items)
						{
							using (xw.Element("item"))
							{
								if (wi.Name == null)
								{
									if (watchType == WatchType.CallStack)
									{
										xw.Attribute("name", ((wi.RetAddress < 0) ? "<chunk-root>" : "<??unknown??>"));
									}
									else
									{
										xw.Attribute("name", "(null name ??)");
									}
								}
								else
								{
									xw.Attribute("name", wi.Name);
								}

								

								if (wi.Value != null)
								{
									xw.Attribute("value", wi.Value.ToString());
									xw.Attribute("type", 
										wi.IsError ? "error" : 
										wi.Value.Type.ToLuaDebuggerString());
								}

								xw.Attribute("address", wi.Address.ToString("X8"));
								xw.Attribute("baseptr", wi.BasePtr.ToString("X8"));
								xw.Attribute("lvalue", wi.LValue);
								xw.Attribute("retaddress", wi.RetAddress.ToString("X8"));
							}
						}
					}
				});
			}
		}

		public void SetByteCode(string[] byteCode)
		{
			// Skip sending bytecode updates for now.
			//Send(xw =>
			//	{
			//		using (xw.Element("bytecode"))
			//		{
			//			foreach (string line in byteCode)
			//				xw.Element("l", line);
			//		}
			//	});
		}

		#endregion

		public void QueueAction(DebuggerAction action)
		{
			m_QueuedActions.Enqueue(action);
		}

		public DebuggerAction GetAction(int ip, SourceRef sourceref)
		{
			try
			{
				m_InGetActionLoop = true;
				m_RequestPause = false;

				if (m_HostBusySent)
				{
					m_HostBusySent = false;
					SendMessage("Host ready!");
				}

				if (sourceref != m_LastSentSourceRef)
				{
					Send(xw =>
						{
							SendSourceRef(xw, sourceref);
						});
				}

				while (true)
				{
					DebuggerAction da = m_QueuedActions.Dequeue();

					if (da.Action == DebuggerAction.ActionType.Refresh || da.Action == DebuggerAction.ActionType.HardRefresh)
					{
						lock (m_Lock)
						{
							HashSet<string> existing = new HashSet<string>();

							// remove all not present anymore
							m_Watches.RemoveAll(de => !m_WatchesChanging.Contains(de.ExpressionCode));

							// add all missing
							existing.UnionWith(m_Watches.Select(de => de.ExpressionCode));

							m_Watches.AddRange(m_WatchesChanging
								.Where(code => !existing.Contains(code))
								.Select(code => CreateDynExpr(code)));
						}

						return da;
					}

					if (da.Action == DebuggerAction.ActionType.ToggleBreakpoint)
						return da;

					if (da.Age < TimeSpan.FromMilliseconds(100))
						return da;
				}
			}
			finally
			{
				m_InGetActionLoop = false;
			}
		}

		private DynamicExpression CreateDynExpr(string code)
		{
			try
			{
				return m_Script.CreateDynamicExpression(code);
			}
			catch (Exception ex)
			{
				SendMessage(string.Format("Error setting watch {0} :\n{1}", code, ex.Message));
				return m_Script.CreateConstantDynamicExpression(code, DynValue.NewString(ex.Message));
			}
		}

		private void SendSourceRef(XmlWriter xw, SourceRef sourceref)
		{
			using (xw.Element("source-loc"))
			{
				xw.Attribute("srcid", sourceref.SourceIdx)
					.Attribute("cf", sourceref.FromChar)
					.Attribute("ct", sourceref.ToChar)
					.Attribute("lf", sourceref.FromLine)
					.Attribute("lt", sourceref.ToLine);
			}
		}

		void m_Server_DataReceived(object sender, Utf8TcpPeerEventArgs e)
		{
			XmlDocument xdoc = new XmlDocument();
			xdoc.LoadXml(e.Message);

			if (xdoc.DocumentElement.Name == "Command")
			{
				string cmd = xdoc.DocumentElement.GetAttribute("cmd").ToLowerInvariant();
				string arg = xdoc.DocumentElement.GetAttribute("arg");

				switch (cmd)
				{
					case "stepin":
						QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepIn });
						break;
					case "refresh":
						lock (m_Lock)
						{
							for (int i = 0; i < (int)WatchType.MaxValue; i++)
								m_CachedWatches[i] = null;
						}
						QueueRefresh();
						break;
					case "run":
						QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.Run });
						break;
					case "stepover":
						QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepOver });
						break;
					case "stepout":
						QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.StepOut });
						break;
					case "pause":
						m_RequestPause = true;
						break;
					case "addwatch":
						lock (m_Lock)
							m_WatchesChanging.UnionWith(arg.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

						QueueRefresh();
						break;
					case "delwatch":
						lock (m_Lock)
						{
							var args = arg.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

							foreach(var a in args)
								m_WatchesChanging.Remove(a);
						}
						QueueRefresh();
						break;
					case "breakpoint":
						QueueAction(new DebuggerAction() 
						{
							Action = DebuggerAction.ActionType.ToggleBreakpoint,
							SourceID = int.Parse(xdoc.DocumentElement.GetAttribute("src")),
							SourceLine = int.Parse(xdoc.DocumentElement.GetAttribute("line")),
							SourceCol = int.Parse(xdoc.DocumentElement.GetAttribute("col")),
						});
						break;
				}

			}
		}

		private void QueueRefresh()
		{
			if (!m_InGetActionLoop)
			{
				SendMessage("Host busy, wait for it to become ready...");
				m_HostBusySent = true;
			}

			QueueAction(new DebuggerAction() { Action = DebuggerAction.ActionType.HardRefresh });
		}

		private void SendMessage(string text)
		{
			Send(xw =>
			{
				xw.ElementCData("message", text);
			});
		}


		public List<DynamicExpression> GetWatchItems()
		{
			return m_Watches;
		}


		public bool IsPauseRequested()
		{
			return m_RequestPause;
		}


		public void SignalExecutionEnded()
		{
			Send(xw => xw.Element("execution-completed", ""));
		}


		public void RefreshBreakpoints(IEnumerable<SourceRef> refs)
		{
			Send(xw =>
			{
				using (xw.Element("breakpoints"))
				{
					foreach (var rf in refs)
						SendSourceRef(xw, rf);
				}
			});
		}






	}
}
