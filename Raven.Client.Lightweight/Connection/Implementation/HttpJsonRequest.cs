#if !NETFX_CORE && !SILVERLIGHT
//-----------------------------------------------------------------------
// <copyright file="HttpJsonRequest.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
#if SILVERLIGHT || NETFX_CORE
using Raven.Client.Silverlight.MissingFromSilverlight;
#else
using System.Collections.Specialized;
#endif
using System.Diagnostics;
using System.IO;
#if !SILVERLIGHT
using System.IO.Compression;
#endif
#if NETFX_CORE
using Raven.Client.WinRT.Connection;
#endif
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Util;
using Raven.Imports.Newtonsoft.Json;
using Raven.Imports.Newtonsoft.Json.Linq;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Connection;
using Raven.Client.Connection.Profiling;
using Raven.Client.Document;
using Raven.Json.Linq;

namespace Raven.Client.Connection
{
	/// <summary>
	/// A representation of an HTTP json request to the RavenDB server
	/// </summary>
	public class HttpJsonRequest
	{
		internal readonly string Url;
		internal readonly string Method;

		internal volatile HttpClient httpClient;
		internal volatile HttpWebRequest webRequest;

        private readonly NameValueCollection headers = new NameValueCollection();

		// temporary create a strong reference to the cached data for this request
		// avoid the potential for clearing the cache from a cached item
		internal CachedRequest CachedRequestDetails;
		private readonly HttpJsonRequestFactory factory;
		private readonly IHoldProfilingInformation owner;
		private readonly DocumentConvention conventions;
		private string postedData;
		private Stopwatch sp = Stopwatch.StartNew();
		internal bool ShouldCacheRequest;
		private Stream postedStream;
		private bool writeCalled;
		public static readonly string ClientVersion = typeof(HttpJsonRequest).Assembly.GetName().Version.ToString();
		private bool disabledAuthRetries;
		private string primaryUrl;

		private string operationUrl;

		public Action<string, string, string> HandleReplicationStatusChanges = delegate { };

		/// <summary>
		/// Gets or sets the response headers.
		/// </summary>
		/// <value>The response headers.</value>
		public NameValueCollection ResponseHeaders { get; set; }

		internal HttpJsonRequest(
			CreateHttpJsonRequestParams requestParams,
			HttpJsonRequestFactory factory)
		{
			Url = requestParams.Url;
			Method = requestParams.Method;

			this.factory = factory;
			owner = requestParams.Owner;
			conventions = requestParams.Convention;

			var handler = new WebRequestHandler
			{
				UseDefaultCredentials = true,
				Credentials = requestParams.Credentials,
			};
			httpClient = new HttpClient(handler);

			webRequest = (HttpWebRequest)WebRequest.Create(requestParams.Url);
			webRequest.UseDefaultCredentials = true;
			webRequest.Credentials = requestParams.Credentials;
			webRequest.Method = requestParams.Method;

			if (factory.DisableRequestCompression == false && requestParams.DisableRequestCompression == false)
			{
				if (requestParams.Method == "POST" || requestParams.Method == "PUT" ||
				    requestParams.Method == "PATCH" || requestParams.Method == "EVAL")
				{
                    webRequest.Headers["Content-Encoding"] = "gzip";
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Content-Encoding", "gzip");
				}

                webRequest.Headers["Accept-Encoding"] = "gzip";
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip");
			}

			webRequest.ContentType = "application/json; charset=utf-8";
			httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json") { CharSet = "utf-8" });
			headers.Add("Raven-Client-Version", ClientVersion);

			WriteMetadata(requestParams.Metadata);
			requestParams.UpdateHeaders(webRequest);
		}

		public void DisableAuthentication()
		{
			webRequest.Credentials = null;
			webRequest.UseDefaultCredentials = false;
			disabledAuthRetries = true;
		}

		public Task ExecuteRequestAsync()
		{
			return ReadResponseJsonAsync();
		}

		/// <summary>
		/// Begins the read response string.
		/// </summary>
		public async Task<RavenJToken> ReadResponseJsonAsync()
		{
			if (SkipServerCheck)
			{
				var result = factory.GetCachedResponse(this);
				factory.InvokeLogRequest(owner, () => new RequestResultArgs
				{
					DurationMilliseconds = CalculateDuration(),
					Method = webRequest.Method,
					HttpResult = (int)ResponseStatusCode,
					Status = RequestStatus.AggressivelyCached,
					Result = result.ToString(),
					Url = webRequest.RequestUri.PathAndQuery,
					PostedData = postedData
				});
				return result;
			}

			int retries = 0;
			while (true)
			{
				ErrorResponseException webException;
				try
				{
					if (writeCalled == false)
					{
						try
						{
						    var httpRequestMessage = new HttpRequestMessage(new HttpMethod(Method), Url);
						    CopyHeadersToHttpRequestMessage(httpRequestMessage);
						    HttpResponseMessage httpResponseMessage = await httpClient.SendAsync(httpRequestMessage);
						    Response = httpResponseMessage;
							SetResponseHeaders(Response);

							ResponseStatusCode = Response.StatusCode;
						}
						finally
						{
							sp.Stop();
						}
						var result = await CheckForErrorsAndReturnCachedResultIfAnyAsync();
					    if (result != null)
					        return result;
					}
					return await ReadJsonInternalAsync();
				}
				catch (ErrorResponseException e)
				{
					if (++retries >= 3 || disabledAuthRetries)
						throw;

					if (e.StatusCode != HttpStatusCode.Unauthorized &&
						e.StatusCode != HttpStatusCode.Forbidden &&
						e.StatusCode != HttpStatusCode.PreconditionFailed)
						throw;

					webException = e;
				}

				if (Response.StatusCode == HttpStatusCode.Forbidden)
				{
					await HandleForbiddenResponseAsync(Response);
					await new CompletedTask(webException).Task; // Throws, preserving original stack
				}

				if (await HandleUnauthorizedResponseAsync(Response) == false)
					await new CompletedTask(webException).Task; // Throws, preserving original stack
			}
		}

	    private void CopyHeadersToHttpRequestMessage(HttpRequestMessage httpRequestMessage)
	    {
	        for (int i = 0; i < headers.Count; i++)
	        {
	            var key = headers.GetKey(i);
	            var values = headers.GetValues(i);
	            Debug.Assert(values != null);
	            httpRequestMessage.Headers.Add(key, values);
	        }
	    }

	    private void SetResponseHeaders(HttpResponseMessage response)
		{
			ResponseHeaders = new NameValueCollection();
			foreach (var header in response.Headers)
			{
				foreach (var val in header.Value)
				{
					ResponseHeaders.Add(header.Key, val);
				}
			}
		}

		private async Task<RavenJToken> CheckForErrorsAndReturnCachedResultIfAnyAsync()
		{
			if (Response.IsSuccessStatusCode == false)
			{
				if (Response.StatusCode == HttpStatusCode.Unauthorized ||
					Response.StatusCode == HttpStatusCode.NotFound ||
					Response.StatusCode == HttpStatusCode.Conflict)
				{
					factory.InvokeLogRequest(owner, () => new RequestResultArgs
					{
						DurationMilliseconds = CalculateDuration(),
						Method = webRequest.Method,
						HttpResult = (int)Response.StatusCode,
						Status = RequestStatus.ErrorOnServer,
						Result = Response.StatusCode.ToString(),
						Url = webRequest.RequestUri.PathAndQuery,
						PostedData = postedData
					});

					throw new ErrorResponseException(Response);
				}

				if (Response.StatusCode == HttpStatusCode.NotModified
					&& CachedRequestDetails != null)
				{
					factory.UpdateCacheTime(this);
					var result = factory.GetCachedResponse(this, ResponseHeaders);

                    // here we explicitly need to get Response.Headers, and NOT ResponseHeaders because we are 
                    // getting the value _right now_ from the secondary, and don't care about the 304, the force check
                    // is still valid
					HandleReplicationStatusChanges(Response.Headers.GetFirstValue(Constants.RavenForcePrimaryServerCheck), primaryUrl, operationUrl);

					factory.InvokeLogRequest(owner, () => new RequestResultArgs
					{
						DurationMilliseconds = CalculateDuration(),
						Method = webRequest.Method,
						HttpResult = (int)Response.StatusCode,
						Status = RequestStatus.Cached,
						Result = result.ToString(),
						Url = webRequest.RequestUri.PathAndQuery,
						PostedData = postedData
					});

					return result;
				}


				using (var sr = new StreamReader(await Response.GetResponseStreamWithHttpDecompression()))
				{
					var readToEnd = sr.ReadToEnd();

					factory.InvokeLogRequest(owner, () => new RequestResultArgs
					{
						DurationMilliseconds = CalculateDuration(),
						Method = webRequest.Method,
						HttpResult = (int)Response.StatusCode,
						Status = RequestStatus.Cached,
						Result = readToEnd,
						Url = webRequest.RequestUri.PathAndQuery,
						PostedData = postedData
					});

					if (string.IsNullOrWhiteSpace(readToEnd))
						throw new ErrorResponseException(Response);

					RavenJObject ravenJObject;
					try
					{
						ravenJObject = RavenJObject.Parse(readToEnd);
					}
					catch (Exception e)
					{
                        throw new ErrorResponseException(Response, readToEnd, e);
					}
					if (ravenJObject.ContainsKey("IndexDefinitionProperty"))
					{
						throw new IndexCompilationException(ravenJObject.Value<string>("Message"))
						{
							IndexDefinitionProperty = ravenJObject.Value<string>("IndexDefinitionProperty"),
							ProblematicText = ravenJObject.Value<string>("ProblematicText")
						};
					}
					if (Response.StatusCode == HttpStatusCode.BadRequest && ravenJObject.ContainsKey("Message"))
					{
						throw new BadRequestException(ravenJObject.Value<string>("Message"), new ErrorResponseException(Response));
					}
					if (ravenJObject.ContainsKey("Error"))
					{
						var sb = new StringBuilder();
						foreach (var prop in ravenJObject)
						{
							if (prop.Key == "Error")
								continue;

							sb.Append(prop.Key).Append(": ").AppendLine(prop.Value.ToString(Formatting.Indented));
						}

						if (sb.Length > 0)
							sb.AppendLine();
						sb.Append(ravenJObject.Value<string>("Error"));

						throw new ErrorResponseException(Response, sb.ToString());
					}
					throw new ErrorResponseException(Response, readToEnd);
				}
			}
		    return null;
		}

		public async Task<byte[]> ReadResponseBytesAsync()
		{
			if (writeCalled == false)
				webRequest.ContentLength = 0;
			var httpRequestMessage = new HttpRequestMessage(new HttpMethod(Method), Url);
			CopyHeadersToHttpRequestMessage(httpRequestMessage);
			Response = await httpClient.SendAsync(httpRequestMessage);
			using (var stream = await Response.GetResponseStreamWithHttpDecompression())
			{
				SetResponseHeaders(Response);
				return await stream.ReadDataAsync();
			}
		}

		public void ExecuteRequest()
		{
			ReadResponseJsonAsync().Wait();
		}

		/// <summary>
		/// Reads the response string.
		/// </summary>
		/// <returns></returns>
		public RavenJToken ReadResponseJson()
		{
		    return ReadResponseJsonAsync().Result;
		}

	    private void CopyHeadersToWebRequest()
	    {
	        for (int i = 0; i < headers.Count; i++)
	        {
	            var key = headers.GetKey(i);
	            var values = headers.GetValues(i);
	            Debug.Assert(values != null);
	            foreach (var value in values)
	            {
	                webRequest.Headers.Set(key, value);
	            }
	        }
	    }

	    private async Task<bool> HandleUnauthorizedResponseAsync(HttpResponseMessage unauthorizedResponse)
		{
			if (conventions.HandleUnauthorizedResponseAsync == null)
				return false;

			var unauthorizedResponseAsync = conventions.HandleUnauthorizedResponseAsync(unauthorizedResponse);
			if (unauthorizedResponseAsync == null)
				return false;

			RecreateWebRequest(await unauthorizedResponseAsync);
			return true;
		}

		private async Task HandleForbiddenResponseAsync(HttpResponseMessage forbiddenResponse)
		{
			if (conventions.HandleForbiddenResponseAsync == null)
				return;

			var forbiddenResponseAsync = conventions.HandleForbiddenResponseAsync(forbiddenResponse);
			if (forbiddenResponseAsync == null)
				return;

			await forbiddenResponseAsync;
		}

		private void RecreateWebRequest(Action<HttpWebRequest> action)
		{
			// we now need to clone the request, since just calling GetRequest again wouldn't do anything

			var newWebRequest = (HttpWebRequest)WebRequest.Create(Url);
			newWebRequest.Method = webRequest.Method;
			HttpRequestHelper.CopyHeaders(webRequest, newWebRequest);
			newWebRequest.UseDefaultCredentials = webRequest.UseDefaultCredentials;
			newWebRequest.Credentials = webRequest.Credentials;
			action(newWebRequest);

			if (postedData != null)
			{
				HttpRequestHelper.WriteDataToRequest(newWebRequest, postedData, factory.DisableRequestCompression);
			}
			if (postedStream != null)
			{
				postedStream.Position = 0;
				using (var stream = newWebRequest.GetRequestStream())
				using (var commpressedData = new GZipStream(stream, CompressionMode.Compress))
				{
				    postedStream.CopyTo(factory.DisableRequestCompression == false ? commpressedData : stream);
				    commpressedData.Flush();
					stream.Flush();
				}
			}
			webRequest = newWebRequest;
		}

		private async Task<RavenJToken> ReadJsonInternalAsync()
		{
			HandleReplicationStatusChanges(ResponseHeaders.Get(Constants.RavenForcePrimaryServerCheck), primaryUrl, operationUrl);

			using (var responseStream = await Response.GetResponseStreamWithHttpDecompression())
			{
				var data = await RavenJToken.TryLoadAsync(responseStream);

				if (Method == "GET" && ShouldCacheRequest)
				{
					factory.CacheResponse(Url, data, ResponseHeaders);
				}

				factory.InvokeLogRequest(owner, () => new RequestResultArgs
				{
					DurationMilliseconds = CalculateDuration(),
					Method = webRequest.Method,
					HttpResult = (int) ResponseStatusCode,
					Status = RequestStatus.SentToServer,
					Result = (data ?? "").ToString(),
					Url = webRequest.RequestUri.PathAndQuery,
					PostedData = postedData
				});

				return data;
			}
		}

	    /// <summary>
	    /// Adds the operation headers.
	    /// </summary>
	    /// <param name="operationsHeaders">The operations headers.</param>
	    public void AddOperationHeaders(NameValueCollection operationsHeaders)
	    {
	        headers.Add(operationsHeaders);
	    }

	    /// <summary>
		/// Adds the operation header.
		/// </summary>
		public void AddOperationHeader(string key, string value)
	    {
	        headers[key] = value;
	    }

	    public HttpJsonRequest AddReplicationStatusHeaders(string thePrimaryUrl, string currentUrl,
            ReplicationInformer replicationInformer, FailoverBehavior failoverBehavior,
            Action<string, string, string> handleReplicationStatusChanges)
		{
			if (thePrimaryUrl.Equals(currentUrl, StringComparison.OrdinalIgnoreCase))
				return this;
			if (replicationInformer.GetFailureCount(thePrimaryUrl) <= 0)
				return this; // not because of failover, no need to do this.

			var lastPrimaryCheck = replicationInformer.GetFailureLastCheck(thePrimaryUrl);
			headers.Set(Constants.RavenClientPrimaryServerUrl, ToRemoteUrl(thePrimaryUrl));
			headers.Set(Constants.RavenClientPrimaryServerLastCheck, lastPrimaryCheck.ToString("s"));

			primaryUrl = thePrimaryUrl;
			operationUrl = currentUrl;

			HandleReplicationStatusChanges = handleReplicationStatusChanges;

			return this;
		}

		private static string ToRemoteUrl(string primaryUrl)
		{
			var uriBuilder = new UriBuilder(primaryUrl);
			if (uriBuilder.Host == "localhost" || uriBuilder.Host == "127.0.0.1")
				uriBuilder.Host = Environment.MachineName;
			return uriBuilder.Uri.ToString();
		}

		/// <summary>
		/// The request duration
		/// </summary>
		public double CalculateDuration()
		{
			return sp.ElapsedMilliseconds;
		}

		/// <summary>
		/// Gets or sets the response status code.
		/// </summary>
		/// <value>The response status code.</value>
		public HttpStatusCode ResponseStatusCode { get; set; }

		///<summary>
		/// Whatever we can skip the server check and directly return the cached result
		///</summary>
		public bool SkipServerCheck { get; set; }

        public TimeSpan Timeout
		{
			set { webRequest.Timeout = (int) value.TotalMilliseconds; }
		}

		public HttpResponseMessage Response { get; private set; }

		private void WriteMetadata(RavenJObject metadata)
		{
			if (metadata == null || metadata.Count == 0)
			{
				return;
			}

			foreach (var prop in metadata)
			{
				if (prop.Value == null)
					continue;

				if (prop.Value.Type == JTokenType.Object ||
					prop.Value.Type == JTokenType.Array)
					continue;

				var headerName = prop.Key;
				string value = prop.Value.Value<object>().ToString();
				if (headerName == "ETag")
				{
					headerName = "If-None-Match";
					// If-None-Match headers MUST be surrounded in quotes
					if (!value.StartsWith("\""))
					{
						value = "\"" + value;
					}
					if (!value.EndsWith("\""))
					{
						value = value + "\"";
					}
				}

				bool isRestricted;
				try
				{
					isRestricted = WebHeaderCollection.IsRestricted(headerName);
				}
				catch (Exception e)
				{
					throw new InvalidOperationException("Could not figure out how to treat header: " + headerName, e);
				}
				// Restricted headers require their own special treatment, otherwise an exception will
				// be thrown.
				// See http://msdn.microsoft.com/en-us/library/78h415ay.aspx
				if (isRestricted)
				{
					switch (headerName)
					{
						/*case "Date":
						case "Referer":
						case "Content-Length":
						case "Expect":
						case "Range":
						case "Transfer-Encoding":
						case "User-Agent":
						case "Proxy-Connection":
						case "Host": // Host property is not supported by 3.5
							break;*/
						case "Content-Type":
							webRequest.ContentType = value;
							break;
						case "If-Modified-Since":
							DateTime tmp;
							DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out tmp);
							webRequest.IfModifiedSince = tmp;
							break;
						case "Accept":
							webRequest.Accept = value;
							break;
						case "Connection":
							webRequest.Connection = value;
							break;
					}
				}
				else
				{
					headers[headerName] = value;
				}
			}
		}

		/// <summary>
		/// Writes the specified data.
		/// </summary>
		/// <param name="data">The data.</param>
		public void Write(string data)
		{
			writeCalled = true;
			postedData = data;
            CopyHeadersToWebRequest();
			HttpRequestHelper.WriteDataToRequest(webRequest, data, factory.DisableRequestCompression);
		}

		public async Task<IObservable<string>> ServerPullAsync()
		{
			int retries = 0;
			while (true)
			{
				ErrorResponseException webException;

				try
				{
					Response = await httpClient.SendAsync(new HttpRequestMessage(new HttpMethod(Method), Url), HttpCompletionOption.ResponseHeadersRead);
					await CheckForErrorsAndReturnCachedResultIfAnyAsync();

					var stream = await Response.GetResponseStreamWithHttpDecompression();
					var observableLineStream = new ObservableLineStream(stream, () => Response.Dispose());
					SetResponseHeaders(Response);
					observableLineStream.Start();
					return (IObservable<string>) observableLineStream;
				}
				catch (ErrorResponseException e)
				{
					if (++retries >= 3 || disabledAuthRetries)
						throw;

					if (e.StatusCode != HttpStatusCode.Unauthorized &&
					    e.StatusCode != HttpStatusCode.Forbidden &&
					    e.StatusCode != HttpStatusCode.PreconditionFailed)
						throw;

					webException = e;
				}

				if (webException.StatusCode == HttpStatusCode.Forbidden)
				{
					await HandleForbiddenResponseAsync(webException.Response);
					await new CompletedTask(webException).Task; // Throws, preserving original stack
				}

				if (await HandleUnauthorizedResponseAsync(webException.Response) == false)
					await new CompletedTask(webException).Task; // Throws, preserving original stack
			}
		}

		public async Task ExecuteWriteAsync(string data)
		{
			await WriteAsync(data);
			await ExecuteRequestAsync();
		}

		public async Task ExecuteWriteAsync(Stream data)
		{
			await WriteAsync(data);
			await ExecuteRequestAsync();
		}

		private async Task WriteAsync(Stream streamToWrite)
		{
			postedStream = streamToWrite;

				Response = await httpClient.SendAsync(new HttpRequestMessage(new HttpMethod(Method), Url)
				{
				Content = new CompressedStreamContent(postedStream)
				});

				if (Response.IsSuccessStatusCode == false)
					throw new ErrorResponseException(Response);

				SetResponseHeaders(Response);
			}

		public async Task WriteAsync(string data)
		{
			writeCalled = true;
			Response = await httpClient.SendAsync(new HttpRequestMessage(new HttpMethod(Method), Url)
			{
				Content = new CompressedStringContent(data, factory.DisableRequestCompression),
			});

			if (Response.IsSuccessStatusCode == false)
				throw new ErrorResponseException(Response);

			SetResponseHeaders(Response);
		}

		public Task<Stream> GetRawRequestStream()
		{
            CopyHeadersToWebRequest();
			webRequest.SendChunked = true;
			return Task.Factory.FromAsync<Stream>(webRequest.BeginGetRequestStream, webRequest.EndGetRequestStream, null);
		}

		public async Task<WebResponse> RawExecuteRequestAsync()
		{
			try
			{
                CopyHeadersToWebRequest();
				return await webRequest.GetResponseAsync();
			}
			catch (WebException we)
			{
				var httpWebResponse = we.Response as HttpWebResponse;
				if (httpWebResponse == null)
					throw;
				var sb = new StringBuilder()
					.Append(httpWebResponse.StatusCode)
					.Append(" ")
					.Append(httpWebResponse.StatusDescription)
					.AppendLine();

				using (var reader = new StreamReader(httpWebResponse.GetResponseStreamWithHttpDecompression()))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						sb.AppendLine(line);
					}
				}
				throw new InvalidOperationException(sb.ToString(), we);
			}
		}

		public void PrepareForLongRequest()
		{
			Timeout = TimeSpan.FromHours(6);
			webRequest.AllowWriteStreamBuffering = false;
		}

	    public void AddHeader(string key, string val)
	    {
	        headers.Set(key, val);
	    }
	}
}
#endif
