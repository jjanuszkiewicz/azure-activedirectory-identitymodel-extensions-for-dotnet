﻿//------------------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Threading;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.IdentityModel.Validators
{
    /// <summary>
    /// Generic class that validates the issuer for either JsonWebTokens or JwtSecurityTokens issued from the Microsoft identity platform (AAD).
    /// </summary>
    public class AadIssuerValidator
    {
        internal AadIssuerValidator(
            HttpClient httpClient,
            string aadAuthority)
        {
            HttpClient = httpClient;
            IsV2Authority = aadAuthority.Contains("v2.0");
            if (IsV2Authority)
            {
                AadAuthorityV2 = aadAuthority.TrimEnd('/');
                AadAuthorityV1 = CreateV1Authority(AadAuthorityV2);
            }
            else
            {
                AadAuthorityV1 = aadAuthority.TrimEnd('/');
                AadAuthorityV2 = AadAuthorityV1 + "/v2.0";
            }
        }

        private HttpClient HttpClient { get; }

        internal string AadIssuerV1 { get; set; }
        internal string AadIssuerV2 { get; set; }
        internal string AadAuthorityV2 { get; set; }
        internal string AadAuthorityV1 { get; set; }
        internal bool IsV2Authority { get; set; }
        internal static readonly IDictionary<string, AadIssuerValidator> s_issuerValidators = new ConcurrentDictionary<string, AadIssuerValidator>();

        /// <summary>
        /// Validate the issuer for single and multi-tenant applications of various audiences (Work and School accounts, or Work and School accounts +
        /// Personal accounts) and the various clouds.
        /// </summary>
        /// <param name="issuer">Issuer to validate (will be tenanted).</param>
        /// <param name="securityToken">Received security token.</param>
        /// <param name="validationParameters">Token validation parameters.</param>
        /// <example><code>
        /// AadIssuerValidator aadIssuerValidator = AadIssuerValidator.GetAadIssuerValidator(authority, httpClient);
        /// TokenValidationParameters.IssuerValidator = aadIssuerValidator.Validate;
        /// </code></example>
        /// <remarks>The issuer is considered as valid if it has the same HTTP scheme and authority as the
        /// authority from the configuration file, has a tenant ID, and optionally v2.0 (if this web API
        /// accepts both V1 and V2 tokens).</remarks>
        /// <returns>The <c>issuer</c> if it's valid, or otherwise <c>SecurityTokenInvalidIssuerException</c> is thrown.</returns>
        /// <exception cref="ArgumentNullException"> if <paramref name="securityToken"/> is null.</exception>
        /// <exception cref="ArgumentNullException"> if <paramref name="validationParameters"/> is null.</exception>
        /// <exception cref="SecurityTokenInvalidIssuerException">if the issuer is invalid or if there is a network issue. </exception>
        public string Validate(
            string issuer,
            SecurityToken securityToken,
            TokenValidationParameters validationParameters)
        {
            _ = issuer ?? throw LogHelper.LogArgumentNullException(nameof(issuer));
            _ = securityToken ?? throw LogHelper.LogArgumentNullException(nameof(securityToken));
            _ = validationParameters ?? throw LogHelper.LogArgumentNullException(nameof(validationParameters));

            string tenantId = GetTenantIdFromToken(securityToken);

            if (string.IsNullOrWhiteSpace(tenantId))
                throw LogHelper.LogExceptionMessage(new SecurityTokenInvalidIssuerException(LogMessages.IDX40003));

            if (validationParameters.ValidIssuers != null)
            {
                foreach (var validIssuerTemplate in validationParameters.ValidIssuers)
                {
                    if (IsValidIssuer(validIssuerTemplate, tenantId, issuer))
                        return issuer;
                }
            }

            if (validationParameters.ValidIssuer != null)
            {
                if (IsValidIssuer(validationParameters.ValidIssuer, tenantId, issuer))
                    return issuer;
            }

            try
            {
                if (securityToken.Issuer.EndsWith("v2.0", StringComparison.OrdinalIgnoreCase))
                {
                    if (AadIssuerV2 == null)
                    {
                        if (IsV2Authority)
                        {
                            AadIssuerV2 = CreateConfigManager(AadAuthorityV2).GetConfigurationAsync().ConfigureAwait(false).GetAwaiter().GetResult().Issuer;
                        }
                        else
                        {
                            OpenIdConnectConfiguration openIdConnectConfig =
                            CreateConfigManager(AadAuthorityV2).GetConfigurationAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                            AadIssuerV2 = openIdConnectConfig.Issuer;
                        }
                    }

                    if (IsValidIssuer(AadIssuerV2, tenantId, issuer))
                        return issuer;
                }
                else
                {
                    if (AadIssuerV1 == null)
                    {
                        if (IsV2Authority)
                        {
                            OpenIdConnectConfiguration openIdConnectConfig =
                                CreateConfigManager(AadAuthorityV1).GetConfigurationAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                            AadIssuerV1 = openIdConnectConfig.Issuer;
                        }
                        else
                        {
                            AadIssuerV1 = CreateConfigManager(AadAuthorityV1).GetConfigurationAsync().ConfigureAwait(false).GetAwaiter().GetResult().Issuer;
                        }
                    }

                    if (IsValidIssuer(AadIssuerV1, tenantId, issuer))
                        return issuer;
                }
            }
            catch (Exception ex)
            {
                throw LogHelper.LogExceptionMessage(new SecurityTokenInvalidIssuerException(LogHelper.FormatInvariant(LogMessages.IDX40001, issuer), ex));
            }

            // If a valid issuer is not found, throw
            throw LogHelper.LogExceptionMessage(new SecurityTokenInvalidIssuerException(LogHelper.FormatInvariant(LogMessages.IDX40001, issuer)));
        }

        /// <summary>
        /// Gets an <see cref="AadIssuerValidator"/> for an Azure Active Directory (AAD) authority.
        /// </summary>
        /// <param name="aadAuthority">The authority to create the validator for, e.g. https://login.microsoftonline.com/. </param>
        /// <param name="httpClient">Optional HttpClient to use to retrieve the endpoint metadata (can be null).</param>
        /// <example><code>
        /// AadIssuerValidator aadIssuerValidator = AadIssuerValidator.GetAadIssuerValidator(authority, httpClient);
        /// TokenValidationParameters.IssuerValidator = aadIssuerValidator.Validate;
        /// </code></example>
        /// <returns>A <see cref="AadIssuerValidator"/> for the aadAuthority.</returns>
        /// <exception cref="ArgumentNullException">if <paramref name="aadAuthority"/> is null or empty.</exception>
        public static AadIssuerValidator GetAadIssuerValidator(string aadAuthority, HttpClient httpClient)
        {
            if(string.IsNullOrEmpty(aadAuthority))
                throw LogHelper.LogArgumentNullException(nameof(aadAuthority));

            if (s_issuerValidators.TryGetValue(aadAuthority, out AadIssuerValidator aadIssuerValidator))
                return aadIssuerValidator;

            s_issuerValidators[aadAuthority] = new AadIssuerValidator(
                httpClient,
                aadAuthority);

            return s_issuerValidators[aadAuthority];
        }

        /// <summary>
        /// Gets an <see cref="AadIssuerValidator"/> for an Azure Active Directory (AAD) authority.
        /// </summary>
        /// <param name="aadAuthority">The authority to create the validator for, e.g. https://login.microsoftonline.com/. </param>
        /// <example><code>
        /// AadIssuerValidator aadIssuerValidator = AadIssuerValidator.GetAadIssuerValidator(authority);
        /// TokenValidationParameters.IssuerValidator = aadIssuerValidator.Validate;
        /// </code></example>
        /// <returns>A <see cref="AadIssuerValidator"/> for the aadAuthority.</returns>
        /// <exception cref="ArgumentNullException">if <paramref name="aadAuthority"/> is null or empty.</exception>
        public static AadIssuerValidator GetAadIssuerValidator(string aadAuthority)
        {
            return GetAadIssuerValidator(aadAuthority, null);
        }

        private static string CreateV1Authority(string aadV2Authority)
        {
            if (aadV2Authority.Contains(AadIssuerValidatorConstants.Organizations))
                return aadV2Authority.Replace($"{AadIssuerValidatorConstants.Organizations}/v2.0", AadIssuerValidatorConstants.Common);

            return aadV2Authority.Replace("/v2.0", string.Empty);
        }

        private ConfigurationManager<OpenIdConnectConfiguration> CreateConfigManager(
            string aadAuthority)
        {
            if (HttpClient != null)
            {
                return
                 new ConfigurationManager<OpenIdConnectConfiguration>(
                     $"{aadAuthority}{AadIssuerValidatorConstants.OidcEndpoint}",
                     new OpenIdConnectConfigurationRetriever(),
                     HttpClient);
            }
            else
            {
                return
                new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"{aadAuthority}{AadIssuerValidatorConstants.OidcEndpoint}",
                    new OpenIdConnectConfigurationRetriever());
            }
        }

        private static bool IsValidIssuer(string validIssuerTemplate, string tenantId, string actualIssuer)
        {
            if (string.IsNullOrEmpty(validIssuerTemplate))
                return false;

            if (validIssuerTemplate.Contains("{tenantid}"))
            {
                try
                {
                    string issuerFromTemplate = validIssuerTemplate.Replace("{tenantid}", tenantId);

                    return issuerFromTemplate == actualIssuer;
                }
                catch
                {
                    // if something faults, ignore
                }

                return false;
            }
            else
            {
                return validIssuerTemplate == actualIssuer;
            }
        }

        /// <summary>Gets the tenant ID from a token.</summary>
        /// <param name="securityToken">A JWT token.</param>
        /// <returns>A string containing the tenant ID, if found or <see cref="string.Empty"/>.</returns>
        /// <remarks>Only <see cref="JwtSecurityToken"/> and <see cref="JsonWebToken"/> are acceptable types.</remarks>
        private static string GetTenantIdFromToken(SecurityToken securityToken)
        {
            if (securityToken is JwtSecurityToken jwtSecurityToken)
            {
                if (jwtSecurityToken.Payload.TryGetValue(AadIssuerValidatorConstants.Tid, out object tid))
                    return (string)tid;

                if (jwtSecurityToken.Payload.TryGetValue(AadIssuerValidatorConstants.TenantId, out object tenantId))
                    return (string)tenantId;

                // Since B2C doesn't have "tid" as default, get it from issuer
                return GetTenantIdFromIss(jwtSecurityToken.Issuer);
            }

            if (securityToken is JsonWebToken jsonWebToken)
            {
                if (jsonWebToken.TryGetPayloadValue(AadIssuerValidatorConstants.Tid, out string tid))
                    return tid;

                if (jsonWebToken.TryGetPayloadValue(AadIssuerValidatorConstants.Tid, out string tenantId))
                    return tenantId;

                // Since B2C doesn't have "tid" as default, get it from issuer
                return GetTenantIdFromIss(jsonWebToken.Issuer);
            }

            return string.Empty;
        }

        // The AAD "iss" claims contains the tenant ID in its value.
        // The URI can be
        // - {domain}/{tid}/v2.0
        // - {domain}/{tid}/v2.0/
        // - {domain}/{tfp}/{tid}/{userFlow}/v2.0/
        private static string GetTenantIdFromIss(string iss)
        {
            if (string.IsNullOrEmpty(iss))
                return string.Empty;

            var uri = new Uri(iss);

            if (uri.Segments.Length == 3)
                return uri.Segments[1].TrimEnd('/');

            if (uri.Segments.Length == 5 && uri.Segments[1].TrimEnd('/') == AadIssuerValidatorConstants.Tfp)
                throw LogHelper.LogExceptionMessage(new SecurityTokenInvalidIssuerException(LogMessages.IDX40002));

            return string.Empty;
        }
    }
}
