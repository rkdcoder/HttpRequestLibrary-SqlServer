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
        private static readonly string[] ValidHttpMethods =
        {
            "GET", "POST", "PUT", "DELETE", "HEAD", "PATCH", "OPTIONS", "TRACE"
        };

        // Limites para rodar dentro do SQL Server (CLR)
        private const int MaxPayloadSize = 20 * 1024 * 1024;         // 20MB
        private const int MaxResponseSize = 20 * 1024 * 1024;        // 20MB
        private const int MaxErrorResponseSize = 512 * 1024;         // 512KB
        private const int DefaultTimeoutMilliseconds = 120_000;      // 120s

        private class HttpResponse
        {
            public int StatusCode { get; set; }
            public string Response { get; set; }
            public long Timing { get; set; }
        }

        [SqlFunction(
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
            var stopwatch = Stopwatch.StartNew();

            bool hasError = false;

            try
            {
                Uri uri = null;

                // 1. Configuração de Segurança / Conexões (nível AppDomain)
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                }
                catch { }

                // 2. Tenta injetar o TLS 1.3
                try
                {
                    const int Tls13Value = 12288;
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)Tls13Value;
                }
                catch
                {
                    // O ambiente (Windows/CLR) é antigo e não suporta TLS 1.3. 
                    // Vida que segue com o TLS mais seguro disponível.
                }

                ServicePointManager.Expect100Continue = false;

                if (ServicePointManager.DefaultConnectionLimit < 512)
                    ServicePointManager.DefaultConnectionLimit = 512;

                // 2. Validações de Entrada
                if (method.IsNull || string.IsNullOrWhiteSpace(method.Value))
                {
                    SetError(result, 400, "Error: METHOD_REQUIRED");
                    hasError = true;
                }
                else if (url.IsNull ||
                         string.IsNullOrWhiteSpace(url.Value) ||
                         !Uri.TryCreate(url.Value, UriKind.Absolute, out uri))
                {
                    SetError(result, 400, "Error: INVALID_URL");
                    hasError = true;
                }
                else if (!Array.Exists(
                             ValidHttpMethods,
                             m => m.Equals(method.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    SetError(result, 400, "Error: INVALID_HTTP_METHOD");
                    hasError = true;
                }

                // Timeout: NULL => default, negativo => erro
                int timeoutValue = DefaultTimeoutMilliseconds;
                if (!timeout.IsNull)
                {
                    if (timeout.Value < 0)
                    {
                        SetError(result, 400, "Error: INVALID_TIMEOUT");
                        hasError = true;
                    }
                    else
                    {
                        timeoutValue = timeout.Value;
                    }
                }

                // 3. Execução da Requisição
                if (!hasError)
                {
                    var request = (HttpWebRequest)WebRequest.Create(uri);
                    request.Method = method.Value.ToUpperInvariant();

                    request.Timeout = timeoutValue;
                    request.ReadWriteTimeout = timeoutValue;
                    request.UserAgent = "HttpRequestLibrary/1.0";

                    // 3.1 Headers
                    if (!headers.IsNull && !string.IsNullOrWhiteSpace(headers.Value))
                    {
                        try
                        {
                            ApplyHeadersFromJson(headers.Value, request);
                        }
                        catch (JsonException ex)
                        {
                            SetError(result, 400, $"Error: HEADERS_JSON_INVALID - {ex.Message}");
                            hasError = true;
                        }
                        catch (ArgumentException ex)
                        {
                            SetError(result, 400, $"Error: HEADERS_INVALID - {ex.Message}");
                            hasError = true;
                        }
                        catch (Exception ex)
                        {
                            SetError(result, 500, $"Error: HEADERS_UNEXPECTED - {ex.Message}");
                            hasError = true;
                        }
                    }

                    // 3.2 Payload
                    if (!hasError &&
                        !payload.IsNull &&
                        request.Method != "GET" &&
                        request.Method != "HEAD")
                    {
                        string payloadValue = payload.Value ?? string.Empty;
                        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadValue);

                        if (payloadBytes.Length > MaxPayloadSize)
                        {
                            SetError(
                                result,
                                413,
                                $"Error: PAYLOAD_TOO_LARGE (max {MaxPayloadSize} bytes)");
                            hasError = true;
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(request.ContentType))
                                request.ContentType = "application/json";

                            request.ContentLength = payloadBytes.Length;
                            using (var requestStream = request.GetRequestStream())
                            {
                                requestStream.Write(payloadBytes, 0, payloadBytes.Length);
                            }
                        }
                    }

                    // 3.3 Envio
                    if (!hasError)
                    {
                        // mede apenas a latência da chamada HTTP
                        stopwatch.Restart();

                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            result.StatusCode = (int)response.StatusCode;

                            using (var responseStream = response.GetResponseStream())
                            {
                                if (responseStream != null)
                                {
                                    Encoding encoding = GetEncoding(response.ContentType);
                                    result.Response = ReadStreamWithLimit(
                                        responseStream,
                                        MaxResponseSize,
                                        encoding);
                                }
                                else
                                {
                                    result.Response = string.Empty;
                                }
                            }
                        }

                        result.Timing = stopwatch.ElapsedMilliseconds;
                    }
                }
            }
            catch (WebException ex)
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                result.Timing = stopwatch.ElapsedMilliseconds;

                if (ex.Status == WebExceptionStatus.Timeout)
                {
                    result.StatusCode = 408;
                    result.Response = "Error: REQUEST_TIMEOUT";
                }
                else if (ex.Response is HttpWebResponse errorResponse)
                {
                    result.StatusCode = (int)errorResponse.StatusCode;

                    try
                    {
                        using (errorResponse)
                        using (var responseStream = errorResponse.GetResponseStream())
                        {
                            if (responseStream != null)
                            {
                                Encoding encoding = GetEncoding(errorResponse.ContentType);
                                result.Response = ReadStreamWithLimit(
                                    responseStream,
                                    MaxErrorResponseSize,
                                    encoding);
                            }
                            else
                            {
                                result.Response = $"Error: HTTP_{result.StatusCode}_NO_BODY";
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        result.Response =
                            $"Error: WEB_EXCEPTION_BODY_READ_FAILED - {ex.Message} -> {innerEx.Message}";
                    }
                }
                else
                {
                    result.StatusCode = 0;
                    result.Response = $"Error: CONNECTION_FAILURE ({ex.Status}) - {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                result.Timing = stopwatch.ElapsedMilliseconds;
                result.StatusCode = 0;
                result.Response = $"Error: UNEXPECTED_EXCEPTION - {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();

                if (result.Timing <= 0)
                    result.Timing = stopwatch.ElapsedMilliseconds;
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

        // --------- Helpers Privados ---------

        private static void SetError(HttpResponse result, int statusCode, string message)
        {
            result.StatusCode = statusCode;
            result.Response = message;
        }

        private static void ApplyHeadersFromJson(string json, HttpWebRequest request)
        {
            var headerList = JsonSerializer.Deserialize<JsonElement[]>(json);

            if (headerList == null)
                throw new ArgumentException("Headers cannot be null.");

            foreach (var header in headerList)
            {
                if (header.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Each header must be a JSON object.");

                var properties = header.EnumerateObject().ToList();
                if (properties.Count != 1)
                    throw new ArgumentException("Each header object must have exactly one key-value pair.");

                var property = properties[0];
                string key = property.Name;
                string value = property.Value.GetString();

                if (string.IsNullOrEmpty(key) || value == null)
                    continue;

                if (key.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                    value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                {
                    throw new ArgumentException("Invalid characters in header.");
                }

                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    request.ContentType = value;
                }
                else if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    request.UserAgent = value;
                }
                else
                {
                    request.Headers.Add(key, value);
                }
            }
        }

        private static string ReadStreamWithLimit(Stream stream, int maxSize, Encoding encoding)
        {
            if (stream == null)
                return string.Empty;

            if (encoding == null)
                encoding = Encoding.UTF8;

            byte[] buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                int bytesRead;
                int totalBytes = 0;

                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxSize)
                        throw new InvalidOperationException(
                            $"Response limit exceeded ({maxSize / 1024 / 1024} MB)");

                    ms.Write(buffer, 0, bytesRead);
                }

                return encoding.GetString(ms.ToArray());
            }
        }

        private static Encoding GetEncoding(string contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                try
                {
                    string[] parts = contentType.Split(';');
                    foreach (var part in parts)
                    {
                        string trimmed = part.Trim();
                        if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                        {
                            string charset = trimmed.Substring("charset=".Length)
                                                 .Trim('"', '\'');
                            if (!string.IsNullOrEmpty(charset))
                            {
                                return Encoding.GetEncoding(charset);
                            }
                        }
                    }
                }
                catch
                {
                    // Charset inválido: cai para UTF-8
                }
            }

            return Encoding.UTF8;
        }
    }
}
