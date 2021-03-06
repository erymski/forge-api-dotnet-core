﻿/* 
 * Forge SDK
 *
 * The Forge Platform contains an expanding collection of web service components that can be used with Autodesk cloud-based products or your own technologies. Take advantage of Autodesk’s expertise in design and engineering.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Polly;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Autodesk.Forge.Core
{
    public class ForgeHandler : DelegatingHandler
    {
        private readonly Random rand = new Random();
        private readonly IAsyncPolicy<HttpResponseMessage> resiliencyPolicies;
        protected readonly IOptions<ForgeConfiguration> configuration;
        protected ITokenCache TokenCache { get; private set; }

        public ForgeHandler(IOptions<ForgeConfiguration> configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.TokenCache = new TokenCache();
            this.resiliencyPolicies = GetResiliencyPolicies();
        }

        
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == null)
            {
                throw new ArgumentNullException($"{nameof(HttpRequestMessage)}.{nameof(HttpRequestMessage.RequestUri)}");
            }

            var policies = this.resiliencyPolicies;
            if (request.Headers.Authorization == null &&
                request.Properties.ContainsKey(ForgeConfiguration.ScopeKey))
            {
                // no authorization header so we manage authorization
                await RefreshTokenAsync(request, false, cancellationToken);
                // add a retry policy so that we refresh invalid tokens
                policies = policies.WrapAsync(GetTokenRefreshPolicy());
            }
            return await policies.ExecuteAsync(async () => await base.SendAsync(request, cancellationToken));
        }
        protected virtual IAsyncPolicy<HttpResponseMessage> GetTokenRefreshPolicy()
        {
            // A policy that attempts to retry exactly once when 401 error is received after obtaining a new token
            return Policy
                .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.Unauthorized)
                .RetryAsync(
                    retryCount: 1,
                    onRetryAsync: async (outcome, retryNumber, context) => await RefreshTokenAsync(outcome.Result.RequestMessage, true, CancellationToken.None)
                );
        }
        
        protected virtual IAsyncPolicy<HttpResponseMessage> GetResiliencyPolicies()
        {
            // Retry when HttpRequestException is thrown (low level network error) or 
            // the server returns an error code that we think is transient
            var errors = Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(response =>
                {
                    int[] retriable = {
                        (int)HttpStatusCode.RequestTimeout, // 408
                        429, //too many requests
                        (int)HttpStatusCode.BadGateway, // 502
                        (int)HttpStatusCode.ServiceUnavailable, // 503
                        (int)HttpStatusCode.GatewayTimeout // 504
                        };
                    return retriable.Contains((int)response.StatusCode);
                });

            // retry 3 times with exponential backoff and jitter while respecting the RetryAfter header from server
            var retry = errors.WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryCount, response, context) =>
                {
                    // First see how long the server wants us to wait
                    var serverWait = response.Result?.Headers.RetryAfter?.Delta;
                    // Calculate how long we want to wait in milliseconds
                    var clientWait = (double)rand.Next(500, Math.Min(20000, (int)Math.Pow(2, retryCount) * 500));
                    var wait = clientWait;
                    if (serverWait.HasValue)
                    {
                        wait = serverWait.Value.TotalMilliseconds + clientWait;
                    }
                    return TimeSpan.FromMilliseconds(wait);
                },
                onRetryAsync: (response, sleepTime, retryCount, content) => Task.CompletedTask);

            // break circuit after 5 errors and keep it broken for 10 seconds
            var breaker = errors.CircuitBreakerAsync(5, TimeSpan.FromSeconds(10));

            // timeout after 10 seconds
            var timeout = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(10), Polly.Timeout.TimeoutStrategy.Pessimistic);

            // ordering is important here!
            return Policy.WrapAsync<HttpResponseMessage>(retry, breaker, timeout);
        }

        protected virtual async Task RefreshTokenAsync(HttpRequestMessage request, bool ignoreCache, CancellationToken cancellationToken)
        {
            if (request.Properties.TryGetValue(ForgeConfiguration.ScopeKey, out var obj))
            {
                var scope = (string)obj;
                if (ignoreCache || !TokenCache.TryGetValue(scope, out var token))
                {
                    TimeSpan expiry;
                    (token, expiry) = await this.Get2LeggedTokenAsync(scope, cancellationToken);
                    TokenCache.Add(scope, token, expiry);
                }
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
            }
        }
        protected virtual async Task<(string, TimeSpan)> Get2LeggedTokenAsync(string scope, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage())
            {
                var config = this.configuration.Value;
                if (string.IsNullOrEmpty(config.ClientId))
                {
                    throw new ArgumentNullException($"{nameof(ForgeConfiguration)}.{nameof(ForgeConfiguration.ClientId)}");
                }
                if (string.IsNullOrEmpty(config.ClientSecret))
                {
                    throw new ArgumentNullException($"{nameof(ForgeConfiguration)}.{nameof(ForgeConfiguration.ClientSecret)}");
                }
                var values = new List<KeyValuePair<string, string>>();
                values.Add(new KeyValuePair<string, string>("client_id", config.ClientId));
                values.Add(new KeyValuePair<string, string>("client_secret", config.ClientSecret));
                values.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
                values.Add(new KeyValuePair<string, string>("scope", scope));
                request.Content = new FormUrlEncodedContent(values);
                request.RequestUri = config.AuthenticationAddress;
                request.Method = HttpMethod.Post;

                var response = await this.resiliencyPolicies.ExecuteAsync(async () => await base.SendAsync(request, cancellationToken));

                response.EnsureSuccessStatusCode();
                var responseContent = await response.Content.ReadAsStringAsync();
                var resValues = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseContent);
                return (resValues["token_type"] + " " + resValues["access_token"], TimeSpan.FromSeconds(double.Parse(resValues["expires_in"])));
            }
        }
    }
}
