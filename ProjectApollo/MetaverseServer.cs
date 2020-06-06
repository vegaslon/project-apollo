﻿//   Copyright 2020 Vircadia
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Project_Apollo.Logging;
using Project_Apollo.Configuration;
using Project_Apollo.Registry;

namespace Project_Apollo
{
    /// <summary>
    /// 'Context' is a global container of useful variables.
    /// A CleanDesign would pass these around to the necessary code
    ///    but, for the moment, it is easier if these read-only
    ///    instance things are global.
    /// </summary>
    public class Context
    {
        public static AppParams Params;
        public static Logger Log;

        // If cancelled, everything shuts down
        public static CancellationTokenSource KeepRunning;

        // The HTTP listener that's waiting for requests
        public static HttpListener Listener;

        // The database we're talking to
        public static DbConnection Db;

        // All the request path handles are registered in the registry
        public static APIRegistry PathRegistry;
    }

    /// <summary>
    /// MetaverseServer entry.
    /// This sets up the global context, fetches parameters (from
    ///     command line and config file), connects to the database,
    ///     and starts the HttpListener. It then waits until something
    ///     happens from one of the REST requests that tells us to
    ///     stop
    /// </summary>
    class MetaverseServer
    {
        private static readonly string _logHeader = "[MetaverseServer]";

        static void Main(string[] args)
        {
            // Temp logger for startup errors
            Context.Log = new ConsoleLogger
            {
                LogLevel = Logger.LogLevels.Info
            };

            // Setup global Context
            Context.Params = new AppParams(args);
            Context.Log = new LogFileLogger(Context.Params.P<string>("Logger.LogDirectory"),
                                            Context.Params.P<bool>("ConsoleLog") );
            Context.Log.SetLogLevel(Context.Params.P<string>("LogLevel"));

            if (Context.Params.P<bool>("Verbose") || !Context.Params.P<bool>("Quiet"))
            {
                Console.WriteLine("WELCOME TO PROJECT APOLLO METAVERSE API SERVER");
            }
            // This log message has a UTC time header and a local time message
            Context.Log.Info("{0} Started at {1}", _logHeader, DateTime.Now.ToString());

            // Everything will keep running until this TokenSource is cancelled
            Context.KeepRunning = new CancellationTokenSource();

            MetaverseServer server = new MetaverseServer();
            server.Start();

            return;
        }

        private void Start()
        {
            // Collect all the HTTP request path handlers in the registry
            Context.PathRegistry = new APIRegistry();

            // If the default ICE server address has not been set in the configuration file,
            //      assume the ice server is on the same host as this application.
            string defaultIceServer = Context.Params.P<string>("DefaultIceServer");
            if (String.IsNullOrEmpty(defaultIceServer))
            {
                defaultIceServer = Tools.GetMyExternalIPAddress().Result;
                Context.Params.SetSiteParameter("DefaultIceServer", defaultIceServer);
                Context.Log.Info("{0} DefaultIceServer not set in config file. Defaulting to {1}",
                                _logHeader, defaultIceServer);
            }

            // Database
            try
            {
                Context.Db = ConnectToDatabase();
            }
            catch (Exception e)
            {
                Context.Log.Error("{0} Exception connecting to database: {1}", _logHeader, e.ToString());
                Context.KeepRunning.Cancel();
            }

            // HttpListener and start accepting requests
            try
            {
                if (!Context.KeepRunning.IsCancellationRequested)
                {
                    Context.Listener = StartHttpListener();
                }
            }
            catch (Exception e)
            {
                Context.Log.Error("{0} Exception starting HttpListener: {1}", _logHeader, e.ToString());
                Context.KeepRunning.Cancel();
            }

            // Wait until someone tells us to stop
            while (!Context.KeepRunning.IsCancellationRequested)
            {
                Thread.Sleep(100);
            }

            this.Stop();

            return;
        }

        private void Stop()
        {
            if (Context.KeepRunning != null)
            {
                Context.KeepRunning.Cancel();
            }
            if (Context.Db != null)
            {
                Context.Db.Close();
                Context.Db = null;
            }
            if (Context.Listener != null)
            {
                Context.Listener.Stop();
                Context.Listener = null;
            }
            if (Context.Log != null)
            {
                Context.Log.Flush();
            }
        }

        // Connect and initialize database
        private DbConnection ConnectToDatabase()
        {
            // No database yet
            return null;
        }

        /// <summary>
        /// Set up the HttpListener on the proper port.
        /// When a request is received, 'OnWeb' will be called to search
        /// for the APIPath for the request's path.
        /// </summary>
        /// <returns>the created HttpListener</returns>
        private HttpListener StartHttpListener()
        {
            HttpListener listener = null;

            // Let any exceptions be caught by the caller
            Context.Log.Debug("{0} Creating HttpListener", _logHeader);
            listener = new HttpListener();

            // NOTE: on Windows10, you must add url to acl:
            //          netsh http add urlacl url=http://+:9400/ user=everyone
            string prefix = String.Format("http://{0}:{1}/",
                            Context.Params.P<string>("Listener.Host"),
                            Context.Params.P<int>("Listener.Port"));
            Context.Log.Debug("{0} HttpListener listening on '{1}", _logHeader, prefix);
            listener.Prefixes.Add(prefix);

            listener.Start();

            // Start a task to read and process all HTTP requests
            Task.Run(() =>
                {
                    // Common token that is used to stop processing tasks that have not started
                    CancellationToken keepRunningToken = Context.KeepRunning.Token;

                    while (!Context.KeepRunning.IsCancellationRequested)
                    {
                        // Wait for a request
                        HttpListenerContext ctx = listener.GetContext();
                        // Queue the request processing on the system task queue
                        Task.Run(() => { ProcessHttpRequest(ctx); }, keepRunningToken);
                    }
                }, Context.KeepRunning.Token
            );

            return listener;
        }

        /// <summary>
        /// Process a received HTTP request.
        /// We are on our own thread.
        /// </summary>
        /// <param name="pCtx"></param>
        private void ProcessHttpRequest(HttpListenerContext pCtx)
        {
            // Context.Log.Debug("{0} HTTP received {1} {2}", _logHeader, pCtx.Request.HttpMethod, pCtx.Request.RawUrl);

            // Find the processor for this request and do the operation
            // If the processing created any error, it will return reply data with the error.
            RESTReplyData _reply = Context.PathRegistry.ProcessInbound(new RESTRequestData(pCtx));

            pCtx.Response.Headers.Add("Server", Context.Params.P<string>("Listener.Response.Header.Server"));
            
            pCtx.Response.StatusCode = _reply.Status;
            if (_reply.CustomStatus != null) pCtx.Response.StatusDescription = _reply.CustomStatus;

            if(_reply.CustomOutputHeaders != null)
            {
                foreach(KeyValuePair<string,string> kvp in _reply.CustomOutputHeaders)
                {
                    pCtx.Response.Headers[kvp.Key] = kvp.Value;
                }
            }

            if (_reply.Body != null)
            {
                pCtx.Response.AddHeader("Content-Type", _reply.MIMEType);

                // This presumes that all requests only return text
                byte[] buffer = Encoding.UTF8.GetBytes("\n"+_reply.Body);
                pCtx.Response.ContentLength64 = buffer.Length;
                using (Stream output = pCtx.Response.OutputStream)
                {
                    output.Write(buffer, 0, buffer.Length);
                }
                pCtx.Response.ContentType = _reply.MIMEType;
            }

            pCtx.Response.Close();
        }
    }
}
