﻿using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Microsoft.TeamFoundation.Git.Helpers.Authentication
{
    internal class AzureAuthority : IAzureAuthority, ILiveAuthority, IVsoAuthority
    {
        public const string AuthorityHostUrlBase = "https://login.microsoftonline.com";
        public const string DefaultAuthorityHostUrl = AuthorityHostUrlBase + "/common";

        public AzureAuthority(string authorityHostUrl = DefaultAuthorityHostUrl)
        {
            AuthorityHostUrl = authorityHostUrl;
            _adalTokenCache = new VsoAdalTokenCache();
        }

        private readonly VsoAdalTokenCache _adalTokenCache;

        public string AuthorityHostUrl { get; }

        public Tokens AcquireToken(Uri targetUri, string clientId, string resource, Uri redirectUri, string queryParameters = null)
        {
            Debug.Assert(targetUri != null && targetUri.IsAbsoluteUri, "The targetUri parameter is null or invalid");
            Debug.Assert(!String.IsNullOrWhiteSpace(clientId), "The clientId parameter is null or empty");
            Debug.Assert(!String.IsNullOrWhiteSpace(resource), "The resource parameter is null or empty");
            Debug.Assert(redirectUri != null, "The redirectUri parameter is null");
            Debug.Assert(redirectUri.IsAbsoluteUri, "The redirectUri parameter is not an absolute Uri");

            Tokens tokens = null;
            queryParameters = queryParameters ?? String.Empty;

            try
            {
                string authorityUrl = String.Format("{0}/{1}", AuthorityHostUrl, targetUri.DnsSafeHost);

                AuthenticationContext authCtx = new AuthenticationContext(authorityUrl, _adalTokenCache);
                AuthenticationResult authResult = authCtx.AcquireToken(resource, clientId, redirectUri, PromptBehavior.Always, UserIdentifier.AnyUser, queryParameters);
                tokens = new Tokens(authResult);

                Trace.TraceInformation("AzureAuthority::AcquireToken succeeded.");
            }
            catch (AdalException exception)
            {
                Trace.TraceError("AzureAuthority::AcquireToken failed.");
                Debug.Write(exception);
            }

            return tokens;
        }

        public async Task<Tokens> AcquireTokenAsync(Uri targetUri, string clientId, string resource, Credential credentials = null)
        {
            Debug.Assert(targetUri != null && targetUri.IsAbsoluteUri, "The targetUri parameter is null or invalid");
            Debug.Assert(!String.IsNullOrWhiteSpace(clientId), "The clientId parameter is null or empty");
            Debug.Assert(!String.IsNullOrWhiteSpace(resource), "The resource parameter is null or empty");

            Tokens tokens = null;

            try
            {
                string authorityUrl = String.Format("{0}/{1}", AuthorityHostUrlBase, targetUri.Host);

                UserCredential userCredential = credentials == null ? new UserCredential() : new UserCredential(credentials.Username, credentials.Password);
                AuthenticationContext authCtx = new AuthenticationContext(authorityUrl, _adalTokenCache);
                AuthenticationResult authResult = await authCtx.AcquireTokenAsync(resource, clientId, userCredential);
                tokens = new Tokens(authResult);

                Trace.TraceInformation("AzureAuthority::AcquireTokenAsync succeeded.");
            }
            catch (AdalException exception)
            {
                Trace.TraceError("AzureAuthority::AcquireTokenAsync failed.");
                Debug.WriteLine(exception);
            }

            return tokens;
        }

        public async Task<Tokens> AcquireTokenByRefreshTokenAsync(Uri targetUri, string clientId, string resource, Token refreshToken)
        {
            Debug.Assert(targetUri != null && targetUri.IsAbsoluteUri, "The targetUri parameter is null or invalid");
            Debug.Assert(!String.IsNullOrWhiteSpace(clientId), "The clientId parameter is null or empty");
            Debug.Assert(!String.IsNullOrWhiteSpace(resource), "The resource parameter is null or empty");
            Debug.Assert(refreshToken != null, "The refreshToken parameter is null");
            Debug.Assert(refreshToken.Type == TokenType.Refresh, "The value of refreshToken parameter is not a refresh token");
            Debug.Assert(!String.IsNullOrWhiteSpace(refreshToken.Value), "The value of refreshToken parameter is null or empty");

            Tokens tokens = null;

            try
            {
                string authorityUrl = String.Format("{0}/{1}", AuthorityHostUrlBase, targetUri.Host);

                AuthenticationContext authCtx = new AuthenticationContext(authorityUrl, _adalTokenCache);
                AuthenticationResult authResult = await authCtx.AcquireTokenByRefreshTokenAsync(refreshToken.Value, clientId, resource);
                tokens = new Tokens(authResult);

                Trace.TraceInformation("AzureAuthority::AcquireTokenByRefreshTokenAsync succeeded.");
            }
            catch (AdalException exception)
            {
                Trace.TraceError("AzureAuthority::AcquireTokenByRefreshTokenAsync failed.");
                Debug.WriteLine(exception);
            }

            return tokens;
        }

        public async Task<Token> GeneratePersonalAccessToken(Uri targetUri, Token accessToken, VsoTokenScope tokenScope, bool requireCompactToken)
        {
            const string SessionTokenUrl = "https://app.vssps.visualstudio.com/_apis/token/sessiontokens?api-version=1.0";
            const string CompactTokenUrl = SessionTokenUrl + "&tokentype=compact";
            const string TokenScopeJsonFormat = "{{ \"scope\" : \"{0}\" }}";
            const string HttpJsonContentType = "application/json";
            const string AuthHeaderBearer = "Bearer";

            Debug.Assert(targetUri != null, "The targetUri parameter is null");
            Debug.Assert(accessToken != null, "The accessToken parameter is null");
            Debug.Assert(accessToken.Type == TokenType.Access, "The value of the accessToken parameter is not an access token");
            Debug.Assert(tokenScope != null);

            Trace.TraceInformation("Generating Personal Access Token for {0}", targetUri);

            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    string jsonContent = String.Format(TokenScopeJsonFormat, tokenScope);
                    StringContent content = new StringContent(jsonContent, Encoding.UTF8, HttpJsonContentType);
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthHeaderBearer, accessToken.Value);

                    HttpResponseMessage response = await httpClient.PostAsync(requireCompactToken ? CompactTokenUrl : SessionTokenUrl,
                                                                              content);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string responseText = await response.Content.ReadAsStringAsync();

                        Match tokenMatch = null;
                        if ((tokenMatch = Regex.Match(responseText, @"\s*""token""\s*:\s*""([^\""]+)""\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase)).Success)
                        {
                            string tokenValue = tokenMatch.Groups[1].Value;
                            Token token = new Token(tokenValue, TokenType.VsoPat);

                            Trace.TraceInformation("AzureAuthority::GeneratePersonalAccessToken succeeded.");

                            return token;
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine("Received {0} from Visual Studio Online authority. Unable to generate personal access token.", response.ReasonPhrase);
                    }
                }
            }
            catch
            {
                Trace.TraceError("Personal access token generation failed unexpectedly.");
            }

            Trace.TraceError("AzureAuthority::GeneratePersonalAccessToken failed.");

            return null;
        }

        public async Task<bool> ValidateCredentials(Uri targetUri, Credential credentials)
        {
            const string VsoValidationUrl = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=1.0";

            Credential.Validate(credentials);

            try
            {
                string basicAuthHeader = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(String.Format("{0}:{1}", credentials.Username, credentials.Password)));
                HttpWebRequest request = WebRequest.CreateHttp(VsoValidationUrl);
                request.Headers.Add(HttpRequestHeader.Authorization, basicAuthHeader);
                HttpWebResponse response = await request.GetResponseAsync() as HttpWebResponse;
                Trace.TraceInformation("validation status code: {0}", response.StatusCode);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                Trace.TraceError("credential validation failed");
            }

            return false;
        }
    }
}
