using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Text;
using System.Xml;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace MSBuildLogger
{
    // This logger will derive from the Microsoft.Build.Utilities.Logger class,
    // which provides it with getters and setters for Verbosity and Parameters,
    // and a default empty Shutdown() implementation.
    public class JunitLogger : Logger
    {
        /// <summary>
        /// Initialize is guaranteed to be called by MSBuild at the start of the build
        /// before any events are raised.
        /// </summary>
        public override void Initialize(IEventSource eventSource)
        {
            // The name of the log file should be passed as the first item in the
            // "parameters" specification in the /logger switch.  It is required
            // to pass a log file to this logger. Other loggers may have zero or more than 
            // one parameters.
            if (null == Parameters)
            {
                throw new LoggerException("Log file was not set. 1");
            }
            string[] parameters = Parameters.Split(';');

            string logFile = parameters[0];
            if (String.IsNullOrEmpty(logFile))
            {
                throw new LoggerException("Log file was not set. 2");
            }

            if (parameters.Length > 1)
            {
                throw new LoggerException("Too many parameters passed.");
            }

            this.logFile = logFile;
            
            eventSource.WarningRaised += new BuildWarningEventHandler(eventSource_WarningRaised);
            eventSource.ErrorRaised += new BuildErrorEventHandler(eventSource_ErrorRaised);
            eventSource.MessageRaised += new BuildMessageEventHandler(eventSource_MessageRaised);
        }

        private void eventSource_MessageRaised(object sender, BuildMessageEventArgs e)
        {
            // Message raised for compiling a source file
            Regex rx = new Regex(@"^\w+\.(cpp|hpp|cs|c)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            if (rx.IsMatch(e.Message))
            {
                var info = new Message
                {
                    ProjectFile = Path.GetFileNameWithoutExtension(e.ProjectFile),
                    Description = e.Message,
                    Severity = Severity.Info,
                    Code = "",
                    File = e.Message
                };
                this.messages.Add(info);
            }
        }

        void eventSource_ErrorRaised(object sender, BuildErrorEventArgs e)
        {
            //Console.WriteLine("Error Raised");
            var error = new Message
            {
                ProjectFile = Path.GetFileNameWithoutExtension(e.ProjectFile),
                Description = e.Message,
                Code = e.Code,
                Severity = Severity.Warning,
                File = Path.GetFileName(e.File),
                Line = e.LineNumber,
                Column = e.ColumnNumber
            };

            //Console.WriteLine("Subcategory: {0}, Code: {1}, File: {2}", error.Subcategory, error.Code, error.File);
                
            this.messages.Add(error);

            // This has the effect of terminating the build process
            // on first error
            // Environment.Exit(-1);
        }

        void eventSource_WarningRaised(object sender, BuildWarningEventArgs e)
        {
            //Console.WriteLine("Warning Raised");
            var warning = new Message
            {
                ProjectFile = Path.GetFileNameWithoutExtension(e.ProjectFile),
                Description = e.Message,
                Code = e.Code,
                Severity = Severity.Warning,
                File = Path.GetFileName(e.File),
                Line = e.LineNumber,
                Column = e.ColumnNumber
            };

            this.messages.Add(warning);
        }

        /// <summary>
        /// Shutdown() is guaranteed to be called by MSBuild at the end of the build, after all 
        /// events have been raised.
        /// </summary>
        public override void Shutdown()
        {
            Console.WriteLine($"Write to junit xml file: {this.logFile}");
            var options = new XmlWriterSettings
            {
                Indent = true
            };

            int nTotal = 0;
            int nWarnings = 0;
            int nErrors = 0;

            using (var xmlWriter = XmlWriter.Create(this.logFile, options))
            {
                nTotal = this.messages.Count(m => m.Severity == Severity.Info);

                var query = this.messages
                        .GroupBy(m => m.Severity)
                        .Select(g => new
                        {
                            Severity = g.Key,
                            Count = g.Select(m => new { m.ProjectFile, m.File }).Distinct().Count()
                        });

                foreach (var q in query)
                {
                    if (q.Severity == Severity.Warning)
                        nWarnings = q.Count;
                    else if (q.Severity == Severity.Error)
                        nErrors = q.Count;
                }

                xmlWriter.WriteStartElement("testsuites");
                xmlWriter.WriteAttributeString("errors", nErrors.ToString());
                xmlWriter.WriteAttributeString("failures", nWarnings.ToString());
                xmlWriter.WriteAttributeString("tests", nTotal.ToString());

                var projectNames = this.messages.Select(m => m.ProjectFile).Distinct();
                foreach (var projectName in projectNames)
                {
                    nTotal = this.messages
                        .Where(m => m.ProjectFile == projectName && m.Severity == Severity.Info)
                        .Count();

                    var query1 = this.messages
                        .Where(m => m.ProjectFile == projectName)
                        .GroupBy(m => m.Severity)
                        .Select(g => new
                        {
                            Severity = g.Key,
                            Count = g.Select(m => m.File).Distinct().Count()
                        });

                    foreach (var q in query1)
                    {
                        if (q.Severity == Severity.Warning)
                            nWarnings = q.Count;
                        else if (q.Severity == Severity.Error)
                            nErrors = q.Count;
                    }

                    xmlWriter.WriteStartElement("testsuite");
                    xmlWriter.WriteAttributeString("name", projectName);
                    xmlWriter.WriteAttributeString("errors", nErrors.ToString());
                    xmlWriter.WriteAttributeString("failures", nWarnings.ToString());
                    xmlWriter.WriteAttributeString("tests", nTotal.ToString());

                    var query2 = this.messages
                        .Where(m => m.ProjectFile == projectName)
                        .GroupBy(m => m.File)
                        .OrderBy(g => g.Key); 

                    foreach (var q in query2)
                    {
                        xmlWriter.WriteStartElement("testcase");
                        xmlWriter.WriteAttributeString("classname", q.Key);
                        xmlWriter.WriteAttributeString("name", q.Key);
                        var failMsgs = q.Where(m => m.Severity != Severity.Info).OrderBy(m => m.Severity);

                        foreach (var msg in failMsgs)
                        {
                            WriteFailTest(xmlWriter, msg);
                        }

                        xmlWriter.WriteEndElement();
                    }

                    xmlWriter.WriteEndElement();
                }

                xmlWriter.WriteEndElement();
            }
        }

        private void WriteFailTest(XmlWriter xmlWriter, Message m)
        {
            if (m.Severity == Severity.Error)
            {
                xmlWriter.WriteStartElement("error");
            }
            else if (m.Severity == Severity.Warning)
            {
                xmlWriter.WriteStartElement("failure");
            }

            xmlWriter.WriteAttributeString("type", m.Code);
            xmlWriter.WriteAttributeString("message", m.Description);
            xmlWriter.WriteString($"({m.Line}) {m.Code}: {m.Description}");
            xmlWriter.WriteEndElement();
        }

        // output log filename
        string logFile;

        // Collection of messages
        List<Message> messages = new List<Message>();
    }
}
