﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SymbolStore.SymbolStores
{
    /// <summary>
    /// Basic http symbol store. The request can be authentication with a PAT for VSTS symbol stores.
    /// </summary>
    public class HttpSymbolStore : SymbolStore
    {
        private readonly HttpClient _client;
        private readonly HttpClient _authenticatedClient;
        private bool _clientFailure;

        /// <summary>
        /// For example, https://dotnet.myget.org/F/dev-feed/symbols.
        /// </summary>
        public Uri Uri { get; }

        /// <summary>
        /// Get or set the request timeout. Default 4 minutes.
        /// </summary>
        public TimeSpan Timeout
        {
            get 
            { 
                return _client.Timeout;
            }
            set 
            { 
                _client.Timeout = value; 
                if (_authenticatedClient != null)
                {
                    _authenticatedClient.Timeout = value;
                }
            }
        }

        /// <summary>
        /// The number of retries to do on a retryable status or socket error
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// Create an instance of a http symbol store
        /// </summary>
        /// <param name="backingStore">next symbol store or null</param>
        /// <param name="symbolServerUri">symbol server url</param>
        /// <param name="personalAccessToken">optional PAT or null if no authentication</param>
        public HttpSymbolStore(ITracer tracer, SymbolStore backingStore, Uri symbolServerUri, string personalAccessToken = null)
            : base(tracer, backingStore)
        {
            Uri = symbolServerUri ?? throw new ArgumentNullException(nameof(symbolServerUri));
            if (!symbolServerUri.IsAbsoluteUri || symbolServerUri.IsFile)
            {
                throw new ArgumentException(nameof(symbolServerUri));
            }

            // Normal unauthenticated client
            _client = new HttpClient {
                Timeout = TimeSpan.FromMinutes(4)
            };

            // If PAT, create authenticated client
            if (!string.IsNullOrEmpty(personalAccessToken))
            {
                var handler = new HttpClientHandler() {
                    AllowAutoRedirect = false
                };
                var client = new HttpClient(handler) {
                    Timeout = TimeSpan.FromMinutes(4)
                };
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(string.Format("{0}:{1}", "", personalAccessToken))));
                _authenticatedClient = client;
            }
        }

        /// <summary>
        /// Resets the sticky client failure flag. This client instance will now 
        /// attempt to download again instead of automatically failing.
        /// </summary>
        public void ResetClientFailure()
        {
            _clientFailure = false;
        }

        protected override async Task<SymbolStoreFile> GetFileInner(SymbolStoreKey key, CancellationToken token)
        {
            Uri uri = GetRequestUri(key.Index);

            bool needsChecksumMatch = key.PdbChecksums.Any();

            if (needsChecksumMatch)
            {
                string checksumHeader = string.Join(";", key.PdbChecksums);
                HttpClient client = _authenticatedClient ?? _client;
                Tracer.Information($"SymbolChecksum: {checksumHeader}");
                client.DefaultRequestHeaders.Add("SymbolChecksum", checksumHeader);
            }

            Stream stream = await GetFileStream(key.FullPathName, uri, token);
            if (stream != null)
            {
                if (needsChecksumMatch)
                {
                    ChecksumValidator.Validate(Tracer, stream, key.PdbChecksums);
                }
                return new SymbolStoreFile(stream, uri.ToString());
            }
            return null;
        }

        protected Uri GetRequestUri(string index)
        {
            // Escape everything except the forward slashes (/) in the index
            index = string.Join("/", index.Split('/').Select(part => Uri.EscapeDataString(part)));
            if (!Uri.TryCreate(Uri, index, out Uri requestUri))
            {
                throw new ArgumentException(nameof(index));
            }
            if (requestUri.IsFile)
            {
                throw new ArgumentException(nameof(index));
            }
            return requestUri;
        }

        protected async Task<Stream> GetFileStream(string path, Uri requestUri, CancellationToken token)
        {
            // Just return if previous failure
            if (_clientFailure)
            {
                return null;
            }
            string fileName = Path.GetFileName(path);
            HttpClient client = _authenticatedClient ?? _client;
            int retries = 0;
            while(true)
            {
                bool retryable;
                string message;
                try
                {
                    // Can not dispose the response (like via using) on success because then the content stream
                    // is disposed and it is returned by this function.
                    HttpResponseMessage response = await client.GetAsync(requestUri, token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return await response.Content.ReadAsStreamAsync();
                    }
                    if (response.StatusCode == HttpStatusCode.Found)
                    {
                        Uri location = response.Headers.Location;
                        response.Dispose();

                        response = await _client.GetAsync(location, token);
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            return await response.Content.ReadAsStreamAsync();
                        }
                    }
                    HttpStatusCode statusCode = response.StatusCode;
                    string reasonPhrase = response.ReasonPhrase;
                    response.Dispose();

                    // The GET failed 

                    if (statusCode == HttpStatusCode.NotFound)
                    {
                        Tracer.Error("Not Found: {0} - '{1}'", fileName, requestUri.AbsoluteUri);
                        break;
                    }

                    retryable = IsRetryableStatus(statusCode);

                    // Build the status code error message
                    message = string.Format("{0} {1}: {2} - '{3}'", (int)statusCode, reasonPhrase, fileName, requestUri.AbsoluteUri);
                }
                catch (HttpRequestException ex)
                {
                    SocketError socketError = SocketError.Success;
                    retryable = false;

                    Exception innerException = ex.InnerException;
                    while (innerException != null)
                    {
                        if (innerException is SocketException se)
                        {
                            socketError = se.SocketErrorCode;
                            retryable = IsRetryableSocketError(socketError);
                            break;
                        }

                        innerException = innerException.InnerException;
                    }

                    // Build the socket error message
                    message = string.Format($"HttpSymbolStore: {fileName} retryable {retryable} socketError {socketError} '{requestUri.AbsoluteUri}' {ex}");
                }

                // If the status code or socket error isn't some temporary or retryable condition, mark failure
                if (!retryable)
                {
                    MarkClientFailure();
                    Tracer.Error(message);
                    break;
                }
                else
                {
                    Tracer.Warning(message);
                }

                // Retry the operation?
                if (retries++ >= RetryCount)
                {
                    break;
                }

                Tracer.Information($"HttpSymbolStore: retry #{retries}");

                // Delay for a while before doing the retry
                await Task.Delay(TimeSpan.FromMilliseconds((Math.Pow(2, retries) * 100) + new Random().Next(200)));
            }
            return null;
        }

        public override void Dispose()
        {
            _client.Dispose();
            if (_authenticatedClient != null)
            {
                _authenticatedClient.Dispose();
            }
            base.Dispose();
        }

        private HashSet<HttpStatusCode> s_retryableStatusCodes = new HashSet<HttpStatusCode>
        {
            HttpStatusCode.RequestTimeout,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout,
        };

        /// <summary>
        /// Returns true if the http status code is temporary or retryable condition.
        /// </summary>
        protected bool IsRetryableStatus(HttpStatusCode status) => s_retryableStatusCodes.Contains(status);

        private HashSet<SocketError> s_retryableSocketErrors = new HashSet<SocketError>
        {
            SocketError.ConnectionReset,
            SocketError.ConnectionAborted,
            SocketError.Shutdown,
            SocketError.TimedOut,
            SocketError.TryAgain,
        };

        protected bool IsRetryableSocketError(SocketError se) => s_retryableSocketErrors.Contains(se);

        /// <summary>
        /// Marks this client as a failure where any subsequent calls to 
        /// GetFileStream() will return null.
        /// </summary>
        protected void MarkClientFailure()
        {
            _clientFailure = true;
        }

        public override bool Equals(object obj)
        {
            if (obj is HttpSymbolStore store)
            {
                return Uri.Equals(store.Uri);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Uri.GetHashCode();
        }

        public override string ToString()
        {
            return $"Server: {Uri}";
        }
    }
}
