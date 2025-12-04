using System;
using System.Collections;
using System.Data.SqlTypes;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.SqlServer.Server;
using System.Linq;

namespace HttpRequestLibrary
{
    public class HttpRequest
    {
        private static readonly string[] ValidHttpMethods = { "GET", "POST", "PUT", "DELETE", "HEAD", "PATCH", "OPTIONS", "TRACE" };
        private const int MaxPayloadSize = 10 * 1024 * 1024; // 10MB

        private class HttpResponse
        {
            public int StatusCode { get; set; }
            public string Response { get; set; }
            public long Timing { get; set; }
        }

        [Microsoft.SqlServer.Server.SqlFunction(
            DataAccess = DataAccessKind.None,
            FillRowMethodName = "FillRow",
            TableDefinition = "statusCode INT, response NVARCHAR(MAX), timing BIGINT")]
        public static IEnumerable FrisiaHttpRequest(
            SqlString method,
            SqlString url,
            SqlString headers,
            SqlInt32 timeout,
            SqlString payload)
        {
            var result = new HttpResponse();

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                ServicePointManager.Expect100Continue = false;
                if (ServicePointManager.DefaultConnectionLimit < 512)
                {
                    ServicePointManager.DefaultConnectionLimit = 512;
                }
                // Validate inputs
                if (method.IsNull || string.IsNullOrWhiteSpace(method.Value))
                {
                    result.Response = "Error: Method is required";
                }
                else if (url.IsNull || string.IsNullOrWhiteSpace(url.Value) || !Uri.TryCreate(url.Value, UriKind.Absolute, out Uri uri))
                {
                    result.Response = "Error: Invalid URL";
                }
                else if (timeout.IsNull || timeout.Value < 0)
                {
                    result.Response = "Error: Invalid timeout";
                }
                else if (!Array.Exists(ValidHttpMethods, m => m.Equals(method.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Response = "Error: Invalid HTTP method";
                }
                else
                {
                    // Create HttpWebRequest
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                    request.Method = method.Value.ToUpper();
                    int timeoutValue = timeout.Value > 0 ? timeout.Value : 120000;
                    request.Timeout = timeoutValue;
                    request.ReadWriteTimeout = timeoutValue;
                    request.UserAgent = "HttpRequestLibrary/1.0";

                    // Parse and set headers
                    if (!headers.IsNull && !string.IsNullOrWhiteSpace(headers.Value))
                    {
                        try
                        {
                            var headerList = JsonSerializer.Deserialize<JsonElement[]>(headers.Value);
                            foreach (var header in headerList)
                            {
                                if (header.ValueKind != JsonValueKind.Object)
                                {
                                    throw new ArgumentException("Each header must be a JSON object");
                                }

                                var properties = header.EnumerateObject().ToList();
                                if (properties.Count != 1)
                                {
                                    throw new ArgumentException("Each header object must have exactly one key-value pair");
                                }

                                var property = properties[0];
                                string keyStr = property.Name;
                                string valueStr = property.Value.GetString();

                                if (!string.IsNullOrEmpty(keyStr) && valueStr != null)
                                {
                                    // Validate for invalid characters (CR/LF)
                                    if (keyStr.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                                        valueStr.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                                    {
                                        throw new ArgumentException("Header contains invalid characters (CR/LF)");
                                    }

                                    if (keyStr.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                                        request.ContentType = valueStr;
                                    else if (keyStr.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                                        request.UserAgent = valueStr;
                                    else
                                        request.Headers.Add(keyStr, valueStr);
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            result.Response = $"Error (JsonException): Invalid headers JSON - {ex.Message}";
                        }
                        catch (ArgumentException ex)
                        {
                            result.Response = $"Error (ArgumentException): {ex.Message}";
                        }
                        catch (Exception ex)
                        {
                            result.Response = $"Error (Exception): Unexpected error processing headers - {ex.Message}";
                        }
                    }

                    // Set payload for non-GET methods
                    if (!payload.IsNull && request.Method != "GET" && request.Method != "HEAD")
                    {
                        string payloadValue = payload.Value ?? "";
                        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadValue);
                        if (payloadBytes.Length > MaxPayloadSize)
                        {
                            result.Response = $"Error: Payload exceeds maximum size of {MaxPayloadSize} bytes";
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(request.ContentType))
                                request.ContentType = "application/json";
                            request.ContentLength = payloadBytes.Length;
                            using (var stream = request.GetRequestStream())
                                stream.Write(payloadBytes, 0, payloadBytes.Length);
                        }
                    }

                    // Measure timing
                    Stopwatch stopwatch = Stopwatch.StartNew();

                    // Send request and get response
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    {
                        Encoding encoding = GetEncoding(response.ContentType);
                        result.StatusCode = (int)response.StatusCode;
                        result.Response = ReadStreamWithLimit(stream, 10 * 1024 * 1024, encoding); // 10MB limit
                        result.Timing = stopwatch.ElapsedMilliseconds;
                    }

                    stopwatch.Stop();
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse errorResponse)
                {
                    result.StatusCode = (int)errorResponse.StatusCode;
                    try
                    {
                        Encoding encoding = GetEncoding(errorResponse.ContentType);
                        using (errorResponse)
                        using (Stream responseStream = errorResponse.GetResponseStream())
                        {
                            result.Response = ReadStreamWithLimit(responseStream, 1 * 1024 * 1024, encoding); // 1MB for errors
                        }
                    }
                    catch (Exception innerEx)
                    {
                        result.Response = $"Error (WebException): {ex.Message} - Inner: {innerEx.Message}";
                    }
                }
                else
                {
                    result.Response = $"Error (WebException): {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                result.Response = $"Error ({ex.GetType().Name}): {ex.Message}";
            }

            yield return result;
        }

        public static void FillRow(
            object obj,
            out SqlInt32 statusCode,
            out SqlString response,
            out SqlInt64 timing)
        {
            var result = (HttpResponse)obj;
            statusCode = result.StatusCode;
            response = result.Response ?? SqlString.Null;
            timing = result.Timing;
        }

        private static string ReadStreamWithLimit(Stream stream, int maxSize, Encoding encoding = null)
        {
            if (encoding == null)
                encoding = Encoding.UTF8;

            byte[] buffer = new byte[8192]; // 8KB buffer
            using (MemoryStream memoryStream = new MemoryStream(1024)) // Initial 1KB capacity
            {
                int bytesRead;
                int totalBytes = 0;

                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxSize)
                    {
                        throw new InvalidOperationException($"Response exceeded maximum size of {maxSize / 1024 / 1024} MB");
                    }
                    memoryStream.Write(buffer, 0, bytesRead);
                }
                return encoding.GetString(memoryStream.ToArray());
            }
        }

        private static Encoding GetEncoding(string contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                // Split on semicolon and find charset
                string[] parts = contentType.Split(';');
                foreach (var part in parts)
                {
                    string trimmedPart = part.Trim();
                    if (trimmedPart.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                    {
                        string charset = trimmedPart.Substring(8).Trim('"', '\'');
                        if (!string.IsNullOrEmpty(charset))
                        {
                            try
                            {
                                return Encoding.GetEncoding(charset);
                            }
                            catch
                            {
                                // Log invalid charset for debugging (not in SQL CLR)
                            }
                        }
                    }
                }
            }
            return Encoding.UTF8; // Default
        }
    }
}