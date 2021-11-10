﻿using EasyAuthForK8s.Web.Models;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyAuthForK8s.Web.Helpers
{
    public class GraphHelperService
    {
        private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptions;
        private readonly HttpClient _httpClient;
        Lazy<Task<ConfigurationManager<AppManifest>>> _configurationManager;
        ILogger<GraphHelperService> _logger;

        public GraphHelperService(IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptions, HttpClient httpClient, ILogger<GraphHelperService> logger )
        {
            _httpClient = httpClient ?? throw new ArgumentNullException("httpClient");
            _openIdConnectOptions = openIdConnectOptions ?? throw new ArgumentNullException("openIdConnectOptions");
            _configurationManager = new(async () =>
            {
                var oidcConfiguration = await OidcOptions()
                    .ConfigurationManager?.GetConfigurationAsync(CancellationToken.None);
                
                return new ConfigurationManager<AppManifest>(oidcConfiguration.TokenEndpoint, 
                    new AppManifestRetriever(_httpClient, OidcOptions, logger));
            });
            _logger = logger ?? throw new ArgumentNullException("logger");
        }

        private OpenIdConnectOptions OidcOptions()
        {
            return _openIdConnectOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        }

        public virtual async Task<AppManifest> ManifestConfigurationAsync(CancellationToken cancel)
        {
            var configManager = await _configurationManager.Value;
            return await configManager.GetConfigurationAsync(cancel);
        }

        public virtual async Task<List<string>> ExecuteQueryAsync(string endpoint, string accessToken, string[] queries)
        {
            List<string> data = new List<string>();
            if (queries != null && queries.Length > 0)
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/$batch");
                request.Headers.Accept.TryParseAdd("application/json;odata.metadata=none");
                request.Headers.Add("ConsistencyLevel", "eventual");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                dynamic body = new ExpandoObject();
                body.requests = new List<dynamic>();
                for (int i = 0; i < queries.Length; i++)
                {
                    body.requests.Add(new { url = queries[i], method = "GET", id = i });
                }

                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                try
                {
                    using (HttpResponseMessage response = await _httpClient.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            JsonDocument document = await JsonDocument.ParseAsync(response.Content.ReadAsStream());

                            JsonElement responseCollection = document.RootElement.GetProperty("responses");

                            //we have to order the reponses back into the order they were sent
                            //since the order is non-determistic
                            foreach (JsonElement element in responseCollection.EnumerateArray()
                                .OrderBy(x => x.GetProperty("id").GetString())
                                .ToArray())
                            {
                                using MemoryStream stream = new MemoryStream();
                                using Utf8JsonWriter writer = new Utf8JsonWriter(stream);
                                {
                                    writer.WriteStartObject();
                                    JsonElement bodyElement = element.GetProperty("body");
                                    JsonElement statusElement = element.GetProperty("status");

                                    bool hasError = false;
                                    if (!IsSuccessStatus(statusElement.GetInt32()))
                                    {
                                        hasError = true;
                                        writer.WritePropertyName("error_status");
                                        writer.WriteNumberValue(statusElement.GetInt32());
                                    }

                                    if (bodyElement.ValueKind == JsonValueKind.Object || hasError)
                                    {
                                        //this gets a little weird when there is an error but the expected value 
                                        //is not an object.  This means a raw value should have been returned,
                                        //but since it wasn't the error will be encoded.
                                        if (hasError && bodyElement.ValueKind == JsonValueKind.String)
                                        {
                                            string s = bodyElement.GetRawText();
                                            bodyElement = JsonDocument
                                                .Parse(Encoding.UTF8.GetString(Convert.FromBase64String(bodyElement.GetString())))
                                                .RootElement;
                                        }

                                        if (bodyElement.TryGetProperty("error", out JsonElement errorElement))
                                        {
                                            writer.WritePropertyName("error_message");
                                            var error_message = errorElement.GetProperty("message").GetString();
                                            writer.WriteStringValue(error_message);
                                            _logger.LogWarning($"An item in a graph query batch had errors - {error_message}");
                                        }
                                        else
                                        {
                                            //graph responses tend to be quite verbose, so remove metadata
                                            foreach (JsonProperty property in bodyElement.EnumerateObject())
                                            {
                                                if (!property.Name.StartsWith("@odata"))
                                                {
                                                    property.WriteTo(writer);
                                                }
                                            }
                                        }
                                    }
                                    //here we're dealing with a raw value from odata $value
                                    else
                                    {
                                        writer.WritePropertyName("$value");
                                        string foo = bodyElement.GetRawText();
                                        bodyElement.WriteTo(writer);
                                    }

                                    writer.WriteEndObject();
                                    writer.Flush();
                                    stream.Position = 0;
                                    data.Add(await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync());
                                }
                            }
                        }
                        else
                        {
                            data.Add($"{{\"error_status\":{(int)(response.StatusCode)}," +
                                $"\"error_message\":\"Graph API failure: {JsonEncodedText.Encode(response.ReasonPhrase)}\"}}");

                            _logger.LogWarning($"An graph query resulted in an error code - HttpStatus:{(int)(response.StatusCode)}, " +
                                $"Reason:{response.ReasonPhrase}, Request:{request.RequestUri}, Body: {JsonSerializer.Serialize(body)}");
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred attempting to execute a graph query");
                    data.Add($"{{\"error_status\":500,\"error_message\":\"Graph API failure: {JsonEncodedText.Encode(ex.Message)}\"}}");
                }
            }
            return data;
        }

        private static bool IsSuccessStatus(int status)
        {
            return status >= 200 && status < 300;
        }

        internal class AppManifestRetriever : IConfigurationRetriever<AppManifest>
        {
            HttpClient _client;
            Func<OpenIdConnectOptions> _optionsResolver;
            ILogger _logger;
            public AppManifestRetriever(HttpClient client, Func<OpenIdConnectOptions> optionsResolver, ILogger logger)
            {
                _client = client;
                _optionsResolver = optionsResolver;
                _logger = logger;
            }
            public async Task<AppManifest> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
            {
                var options = _optionsResolver();
                string access_token = null;
                string id = null;
                AppManifest appManifest = null;

                _logger.LogInformation("Begin GetConfigurationAsync to aquire application manifest.");
                try
                {
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, address)
                    {
                        Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>()
                        {
                            new("client_id", options.ClientId),
                            new("client_secret", options.ClientSecret),
                            new("grant_type", "client_credentials"),
                            new("scope", "https://graph.microsoft.com/.default")
                        })
                    };
                    using (HttpResponseMessage response = await _client.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            JsonDocument document = await JsonDocument.ParseAsync(response.Content.ReadAsStream());
                            access_token = document.RootElement.GetProperty("access_token").GetString();

                            if(!string.IsNullOrEmpty(access_token))
                            {
                                id = new JwtSecurityToken(access_token).Claims.First(x => x.Type == "oid").Value;
                            }
                        }
                    }
                    if(access_token != null && id != null)
                    {
                        request = new HttpRequestMessage(HttpMethod.Get, string.Concat("https://graph.microsoft.com/beta/directoryObjects/", id));
                        request.Headers.Authorization = new("Bearer", access_token);
                        using (HttpResponseMessage response = await _client.SendAsync(request))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                appManifest = await JsonSerializer.DeserializeAsync<AppManifest>(response.Content.ReadAsStream());
                                _logger.LogInformation("Successful GetConfigurationAsync to aquire application manifest.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error retrieving application manifest configuration.");
                }
                return appManifest;
            }
        }
    }
}