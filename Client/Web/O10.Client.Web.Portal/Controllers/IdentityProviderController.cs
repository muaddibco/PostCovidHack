﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using O10.Client.Common.Entities;
using O10.Client.Common.Interfaces;
using O10.Client.DataLayer.Enums;
using O10.Client.DataLayer.Model;
using O10.Client.DataLayer.Services;
using O10.Client.Web.Portal.Services;
using O10.Core.ExtensionMethods;
using System.Globalization;
using Flurl;
using Flurl.Http;
using O10.Client.Web.Common.Services;
using O10.Client.Web.Common.Dtos.Biometric;
using System.Threading.Tasks;
using O10.Client.DataLayer.AttributesScheme;
using O10.Core.Cryptography;
using O10.Crypto.ConfidentialAssets;
using O10.Client.DataLayer.Model.Scenarios;
using O10.Core.Logging;
using Newtonsoft.Json;
using O10.Core.Translators;
using O10.Client.Common.ExternalIdps;
using O10.Client.Common.ExternalIdps.BlinkId;
using O10.Client.Web.Portal.ExternalIdps.Validators;
using O10.Client.Web.Portal.Exceptions;
using System.Collections.ObjectModel;
using O10.Transactions.Core.DataModel.Transactional;
using O10.Client.Web.Portal.Dtos.IdentityProvider;
using Microsoft.AspNetCore.SignalR;
using O10.Client.Web.Common.Hubs;
using O10.Client.Common.Exceptions;
using O10.Client.Common.Integration;
using O10.Client.Web.Portal.Services.Idps;

namespace O10.Client.Web.Portal.Controllers
{
    //[Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class IdentityProviderController : ControllerBase
    {
        private readonly IExecutionContextManager _executionContextManager;
        private readonly IAssetsService _assetsService;
        private readonly IDataAccessService _dataAccessService;
        private readonly IIdentityAttributesService _identityAttributesService;
        private readonly IAccountsServiceEx _accountsService;
        private readonly ITranslatorsRepository _translatorsRepository;
        private readonly IExternalIdpDataValidatorsRepository _externalIdpDataValidatorsRepository;
        private readonly IIntegrationIdPRepository _integrationIdPRepository;
        private readonly IHubContext<IdentitiesHub> _idenitiesHubContext;
        private readonly ILogger _logger;

        public IdentityProviderController(
            IExecutionContextManager executionContextManager,
            IAssetsService assetsService,
            IDataAccessService dataAccessService,
            IIdentityAttributesService identityAttributesService,
            IAccountsServiceEx accountsService,
            ITranslatorsRepository translatorsRepository,
            IExternalIdpDataValidatorsRepository externalIdpDataValidatorsRepository,
            IIntegrationIdPRepository integrationIdPRepository,
            IHubContext<IdentitiesHub> idenitiesHubContext,
            ILoggerService loggerService)
        {
            _executionContextManager = executionContextManager;
            _assetsService = assetsService;
            _dataAccessService = dataAccessService;
            _identityAttributesService = identityAttributesService;
            _accountsService = accountsService;
            _translatorsRepository = translatorsRepository;
            _externalIdpDataValidatorsRepository = externalIdpDataValidatorsRepository;
            _integrationIdPRepository = integrationIdPRepository;
            _idenitiesHubContext = idenitiesHubContext;
            _logger = loggerService.GetLogger(nameof(IdentityProviderController));
        }

        [HttpGet("All")]
        public IActionResult GetAll(long scenarioId = 0)
        {
            ScenarioSession scenarioSession = scenarioId > 0 ? _dataAccessService.GetScenarioSessions(User.Identity.Name).FirstOrDefault(s => s.ScenarioId == scenarioId) : null;
            if (scenarioSession != null)
            {
                IEnumerable<ScenarioAccount> scenarioAccounts = _dataAccessService.GetScenarioAccounts(scenarioSession.ScenarioSessionId);
                var identityProviders = _accountsService.GetAll().Where(a => a.AccountType == AccountType.IdentityProvider && scenarioAccounts.Any(sa => sa.AccountId == a.AccountId)).Select(a => new IdentityProviderInfoDto
                {
                    Id = a.AccountId.ToString(CultureInfo.InvariantCulture),
                    Description = a.AccountInfo,
                    Target = a.PublicSpendKey.ToHexString()
                });

                return Ok(identityProviders);
            }
            else
            {
                var identityProviders = _accountsService.GetAll().Where(a => !a.IsPrivate && a.AccountType == AccountType.IdentityProvider).Select(a => new IdentityProviderInfoDto
                {
                    Id = a.AccountId.ToString(CultureInfo.InvariantCulture),
                    Description = a.AccountInfo,
                    Target = a.PublicSpendKey.ToHexString()
                });

                return Ok(identityProviders);
            }
        }

        [AllowAnonymous]
        [HttpGet("ById/{accountId}")]
        public IActionResult GetById(long accountId)
        {
            AccountDescriptor account = _accountsService.GetById(accountId);

            if (account == null)
            {
                return BadRequest();
            }

            var identityProvider = new IdentityProviderInfoDto
            {
                Id = account.AccountId.ToString(CultureInfo.InvariantCulture),
                Description = account.AccountInfo,
                Target = account.PublicSpendKey.ToHexString()
            };

            return Ok(identityProvider);
        }

        [HttpPost("Identity")]
        public async Task<IActionResult> CreateIdentity(long accountId, [FromBody] IdentityDto identity)
        {
            //StatePersistency statePersistency = _executionContextManager.ResolveStateExecutionServices(accountId);
            AccountDescriptor account = _accountsService.GetById(accountId);

            //byte[] assetId = await _assetsService.GenerateAssetId(identity.RootAttribute.SchemeName, identity.RootAttribute.Content, account.PublicSpendKey.ToHexString()).ConfigureAwait(false);
            //statePersistency.TransactionsService.IssueBlindedAsset(assetId, 0UL.ToByteArray(32), out byte[] originatingCommitment);
            //identity.RootAttribute.OriginatingCommitment = originatingCommitment.ToHexString();

            IEnumerable<(string attributeName, string content)> attrs = await GetAttribitesAndContent(identity, account).ConfigureAwait(false);

            Identity identityDb = _dataAccessService.CreateIdentity(account.AccountId, identity.Description, attrs.ToArray());
            identity.Id = identityDb.IdentityId.ToString(CultureInfo.InvariantCulture);

            return Ok(identity.Id);
        }

        private async Task<IEnumerable<(string attributeName, string content)>> GetAttribitesAndContent(IdentityDto identity, AccountDescriptor account)
        {
            IEnumerable<(string attributeName, string content)> attrs;

            IdentitiesScheme rootScheme = _dataAccessService.GetRootIdentityScheme(account.PublicSpendKey.ToHexString());
            if (rootScheme != null)
            {
                IdentityAttributeDto rootAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == rootScheme.AttributeName);
                byte[] rootAssetId = await _assetsService.GenerateAssetId(rootScheme.AttributeSchemeName, rootAttribute.Content, account.PublicSpendKey.ToHexString()).ConfigureAwait(false);

                var protectionIdentityAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD);
                if (protectionIdentityAttribute != null)
                {
                    protectionIdentityAttribute.Content = rootAssetId.ToHexString();
                }

                attrs = identity.Attributes.Select(a => (a.AttributeName, a.Content));

                if (protectionIdentityAttribute == null)
                {
                    attrs = attrs.Append((AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD, rootAssetId.ToHexString()));
                }
            }
            else
            {
                attrs = identity.Attributes.Select(a => (a.AttributeName, a.Content));
            }

            return attrs;
        }

        [HttpGet("GetIdentityById/{id}")]
        public IActionResult GetIdentityById(long id)
        {
            Identity identity = _dataAccessService.GetIdentity(id);

            if (identity != null)
            {
                return base.Ok(GetIdentityDto(identity));
            }

            return BadRequest();
        }

        private static IdentityDto GetIdentityDto(Identity identity) => new IdentityDto
        {
            Id = identity.IdentityId.ToString(CultureInfo.InvariantCulture),
            Description = identity.Description,
            Attributes = identity.Attributes.Select(
                    a => new IdentityAttributeDto
                    {
                        AttributeName = a.AttributeName,
                        Content = a.Content,
                        OriginatingCommitment = a.Commitment?.ToHexString()
                    }).ToArray(),
            //NumberOfTransfers = _dataAccessService.GetOutcomingTransactionsCountByOriginatingCommitment(identity.RootAttribute.Commitment)
        };

        [HttpGet("GetAllIdentities/{accountId}")]
        public IActionResult GetAllIdentities(long accountId)
        {
            IEnumerable<Identity> identities = _dataAccessService.GetIdentities(accountId);

            return Ok(identities?.Select(identity => GetIdentityDto(identity)));
        }

        [AllowAnonymous]
        [HttpGet("AttributesScheme")]
        public async Task<IActionResult> GetAttributesScheme(long accountId)
        {
            AccountDescriptor account = _accountsService.GetById(accountId);

            if(account == null)
            {
                throw new AccountNotFoundException(accountId);
            }

            if(account.AccountType != AccountType.IdentityProvider)
            {
                throw new UnexpectedAccountTypeException(accountId, account.AccountType);
            }

            string issuer = account.PublicSpendKey.ToHexString();
            IEnumerable<AttributeDefinition> attributeDefinitions = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true)
                            .Select(a => new AttributeDefinition
                            {
                                SchemeId = a.IdentitiesSchemeId,
                                AttributeName = a.AttributeName,
                                SchemeName = a.AttributeSchemeName,
                                Alias = a.Alias,
                                Description = a.Description,
                                IsActive = a.IsActive,
                                IsRoot = a.CanBeRoot
                            });

            IdentityAttributeSchemaDto rootAttributeScheme = null;
            var rootAttrDefinition = attributeDefinitions.FirstOrDefault(a => a.IsRoot);
            if(rootAttrDefinition != null)
            {
                rootAttributeScheme = new IdentityAttributeSchemaDto
                {
                    AttributeName = rootAttrDefinition.AttributeName,
                    Alias = rootAttrDefinition.Alias
                };
            }
            
            IdentityAttributesSchemaDto schemaDto = new IdentityAttributesSchemaDto
            {
                RootAttribute = rootAttributeScheme,
                AssociatedAttributes = attributeDefinitions
                    .Where(a => !a.IsRoot && a.SchemeName != AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD)
                    .Select(a => 
                        new IdentityAttributeSchemaDto { AttributeName = a.AttributeName, Alias = a.Alias }).ToList()
            };

            return Ok(schemaDto);
        }

        [AllowAnonymous]
        [HttpGet("IssuanceDetails")]
        public ActionResult<IssuerActionDetails> GetIssuanceDetails(string issuer)
        {
            AccountDescriptor account = _accountsService.GetByPublicKey(issuer.HexStringToByteArray());
            IssuerActionDetails registrationDetails = new IssuerActionDetails
            {
                Issuer = account.PublicSpendKey.ToHexString(),
                IssuerAlias = account.AccountInfo,
                ActionUri = $"{Request.Scheme}://{Request.Host.ToUriComponent()}/IdentityProvider/ProcessRootIdentityRequest?issuer={issuer}".EncodeToString64()
            };

            return registrationDetails;
        }

        //[AllowAnonymous]
        //[HttpPost("ProcessRootIdentityRequest")]
        //public async Task<ActionResult<IEnumerable<AttributeValue>>> ProcessRootIdentityRequest(string issuer, [FromBody] IdentityBaseData sessionData)
        //{
        //    try
        //    {
        //        _logger.LogIfDebug(() => $"{nameof(ProcessRootIdentityRequest)} of {issuer} with sessionData: {JsonConvert.SerializeObject(sessionData)}");
        //        string sessionDataJson = sessionData != null ? JsonConvert.SerializeObject(sessionData) : "NULL";
        //        _logger.Info($"{nameof(ProcessRootIdentityRequest)}: {nameof(issuer)} = {issuer}, {nameof(sessionData)} = {sessionDataJson}");

        //        AccountDescriptor account = _accountsService.GetByPublicKey(issuer.HexStringToByteArray());
        //        StatePersistency statePersistency = _executionContextManager.ResolveStateExecutionServices(account.AccountId);
        //        byte[] blindingPoint = sessionData.BlindingPoint.HexStringToByteArray();

        //        IdentitiesScheme rootScheme = _dataAccessService.GetRootIdentityScheme(issuer);
        //        if (rootScheme == null)
        //        {
        //            throw new NoRootAttributeSchemeDefinedException(issuer);
        //        }

        //        IEnumerable<IdentitiesScheme> identitiesSchemes = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true);
        //        Identity identity = _dataAccessService.GetIdentityByAttribute(account.AccountId, rootScheme.AttributeName, sessionData.Content);

        //        if (identity == null)
        //        {
        //            string message = $"Failed to find person with {rootScheme.AttributeName} {sessionData.Content} at account {account.AccountId}";
        //            _logger.Warning(message);
        //            return BadRequest(new { Message = message });
        //        }

        //        bool proceed = !identitiesSchemes.Any(s => s.AttributeSchemeName == AttributesSchemes.ATTR_SCHEME_NAME_PASSPORTPHOTO) || await VerifyFaceImage(sessionData.ImageContent, sessionData.Content, issuer).ConfigureAwait(false);

        //        if (proceed)
        //        {
        //            byte[] rootAssetId = await _assetsService.GenerateAssetId(rootScheme.AttributeSchemeName, sessionData.Content, issuer).ConfigureAwait(false);
        //            IdentityAttribute rootAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == rootScheme.AttributeName);
        //            if (!CreateRootAttributeIfNeeded(statePersistency, rootAttribute, rootAssetId))
        //            {
        //                var protectionAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD);
        //                bool res = VerifyProtectionAttribute(protectionAttribute,
        //                                   sessionData.SignatureE.HexStringToByteArray(),
        //                                   sessionData.SignatureS.HexStringToByteArray(),
        //                                   sessionData.SessionCommitment.HexStringToByteArray());

        //                if (!res)
        //                {
        //                    const string message = "Failed to verify Surjection Proofs";
        //                    _logger.Warning($"[{account.AccountId}]: " + message);
        //                    return BadRequest(message);
        //                }
        //            }
        //            else
        //            {
        //                await IssueAssociatedAttributes(
        //                    attributeIssuanceDetails
        //                        .ToDictionary(d => identity.Attributes.First(a => a.AttributeName == d.Definition.AttributeName).AttributeId, d => d),
        //                    identity.Attributes.Where(a => a.AttributeName != rootScheme.AttributeName)
        //                        .Select(a =>
        //                            (
        //                                a.AttributeId,
        //                                identitiesSchemes.FirstOrDefault(s => s.AttributeName == a.AttributeName).AttributeSchemeName,
        //                                a.Content,
        //                                blindingPoint,
        //                                blindingPoint))
        //                        .ToArray(),
        //                    statePersistency.TransactionsService,
        //                    issuer, rootAssetId).ConfigureAwait(false);
        //            }

        //            //byte[] faceImageAssetId = await _assetsService.GenerateAssetId(AttributesSchemes.ATTR_SCHEME_NAME_PASSPORTPHOTO, identityRequest.FaceImageContent, issuer).ConfigureAwait(false);

        //            var packet = TransferAssetToUtxo(
        //                statePersistency.TransactionsService,
        //                new ConfidentialAccount
        //                {
        //                    PublicSpendKey = sessionData.PublicSpendKey.HexStringToByteArray(),
        //                    PublicViewKey = sessionData.PublicViewKey.HexStringToByteArray()
        //                },
        //                rootAssetId);

        //            if (packet == null)
        //            {
        //                _logger.Error($"[{account.AccountId}]: failed to transfer Root Attribute");
        //                return BadRequest();
        //            }

        //            IEnumerable<AttributeValue> attributeValues = GetAttributeValues(issuer, identity);

        //            return Ok(attributeValues);
        //        }
        //        else
        //        {
        //            const string message = "Captured face does not match to registered one";
        //            _logger.Warning($"[{account.AccountId}]: " + message);
        //            return BadRequest(new { Message = message });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.Error($"[{issuer}]: Failed ProcessRootIdentityRequest\r\nSessionData={(sessionData != null ? JsonConvert.SerializeObject(sessionData) : null)}", ex);
        //        throw;
        //    }
        //}

        [AllowAnonymous]
        [HttpPost("TranslateToAttributes/{issuerName}")]
        public ActionResult<TranslationResponse> TranslateToAttributes(string issuerName, [FromBody] object json)
        {
            var provider = _dataAccessService.GetExternalIdentityProvider(issuerName);
            AccountDescriptor account = _accountsService.GetById(provider.AccountId);

            var validator = _externalIdpDataValidatorsRepository.GetInstance(issuerName);
            if (validator == null)
            {
                throw new Exception($"No validator found for {issuerName}");
            }

            TranslationResponse response = new TranslationResponse
            {
                Issuer = account.PublicSpendKey.ToHexString(),
                ActionUri = $"{Request.Scheme}://{Request.Host}".AppendPathSegments("IdentityProvider", "IssueExternalIdpAttributes", account.PublicSpendKey.ToHexString()).ToString()
            };

            string jsonString = json?.ToString();
            switch (issuerName)
            {
                case "BlinkID-DrivingLicense":
                case "BlinkID-Passport":
                    var request = JsonConvert.DeserializeObject<BlinkIdIdentityRequest>(jsonString);
                    validator.Validate(request);
                    var translator = _translatorsRepository.GetInstance<BlinkIdIdentityRequest, Dictionary<string, string>>();
                    var attributes = translator.Translate(request);
                    response.Attributes = attributes;
                    break;
                default:
                    return BadRequest("unknown issuer name");
            }

            return Ok(response);
        }

        [AllowAnonymous]
        [HttpPost("IssueIdpAttributes/{issuer}")]
        public async Task<ActionResult<IEnumerable<AttributeValue>>> IssueIdpAttributes(string issuer, [FromBody] IssueAttributesRequestDTO request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            AccountDescriptor account = _accountsService.GetByPublicKey(issuer.HexStringToByteArray());
            StatePersistency statePersistency = _executionContextManager.ResolveStateExecutionServices(account.AccountId);

            IEnumerable<AttributeDefinition> attributeDefinitions = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true)
                .Select(a => new AttributeDefinition
                {
                    SchemeId = a.IdentitiesSchemeId,
                    AttributeName = a.AttributeName,
                    SchemeName = a.AttributeSchemeName,
                    Alias = a.Alias,
                    Description = a.Description,
                    IsActive = a.IsActive,
                    IsRoot = a.CanBeRoot
                });

            if (!attributeDefinitions.Any(a => a.IsRoot))
            {
                throw new NoRootAttributeSchemeDefinedException(issuer);
            }

            var issuanceInputDetails = GetValidatedIssuanceDetails(request, attributeDefinitions);

            Identity identity = CreateIdentityInDb(account, issuanceInputDetails);

            IssuanceDetailsDto issuanceDetails;
            if (!string.IsNullOrEmpty(request.PublicSpendKey) && !string.IsNullOrEmpty(request.PublicViewKey))
            {
                ConfidentialAccount targetAccount = new ConfidentialAccount
                {
                    PublicSpendKey = request.PublicSpendKey.HexStringToByteArray(),
                    PublicViewKey = request.PublicViewKey.HexStringToByteArray()
                };

                issuanceDetails = await IssueIdpAttributesAsRoot(issuer, request.Protection, identity, issuanceInputDetails, account, targetAccount, statePersistency).ConfigureAwait(false);
            }
            else
            {
                issuanceDetails = await IssueIdpAttributesAsAssociated(issuer, identity, issuanceInputDetails, statePersistency).ConfigureAwait(false);
            }

            await _idenitiesHubContext.Clients.Group(account.AccountId.ToString()).SendAsync("RequestForIssuance", issuanceDetails);
            
            var attributeValues = FillAttributeValues(request.Attributes, attributeDefinitions);

            return Ok(attributeValues);

            #region Internal Functions

            IReadOnlyCollection<AttributeValue> FillAttributeValues(Dictionary<string, IssueAttributesRequestDTO.AttributeValue> attributes,
                            IEnumerable<AttributeDefinition> attributeDefinitions)
            {
                List<AttributeValue> attributeValues = new List<AttributeValue>();
                var protectionAttrDefinition = attributeDefinitions.FirstOrDefault(a => a.SchemeName == AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD);
                foreach (var attributeName in attributes.Keys.Where(a => protectionAttrDefinition?.AttributeName != a))
                {
                    string content = attributes[attributeName].Value;

                    AttributeValue attributeValue = new AttributeValue
                    {
                        Value = content,
                        Definition = attributeDefinitions.FirstOrDefault(d => d.AttributeName == attributeName)
                    };
                    attributeValues.Add(attributeValue);
                }

                return new ReadOnlyCollection<AttributeValue>(attributeValues);
            }

            async Task<IssuanceDetailsDto> IssueIdpAttributesAsRoot(
                string issuer,
                IssuanceProtection protection,
                Identity identity,
                IEnumerable<AttributeIssuanceDetails> attributeIssuanceDetails,
                AccountDescriptor account,
                ConfidentialAccount targetAccount,
                StatePersistency statePersistency)
            {
                IssuanceDetailsDto issuanceDetails = new IssuanceDetailsDto();

                IEnumerable<IdentitiesScheme> identitiesSchemes = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true);

                var rootAttributeDetails = attributeIssuanceDetails.First(a => a.Definition.IsRoot);

                byte[] rootAssetId = await _assetsService.GenerateAssetId(rootAttributeDetails.Definition.SchemeName, rootAttributeDetails.Value.Value, issuer).ConfigureAwait(false);
                IdentityAttribute rootAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == rootAttributeDetails.Definition.AttributeName);
                if (!CreateRootAttributeIfNeeded(statePersistency, rootAttribute, rootAssetId))
                {
                    var protectionAttribute = identity.Attributes.FirstOrDefault(a => a.AttributeName == AttributesSchemes.ATTR_SCHEME_NAME_PASSWORD);
                    bool res = VerifyProtectionAttribute(protectionAttribute,
                                       protection.SignatureE.HexStringToByteArray(),
                                       protection.SignatureS.HexStringToByteArray(),
                                       protection.SessionCommitment.HexStringToByteArray());

                    if (!res)
                    {
                        _logger.Warning($"[{account.AccountId}]: Failed to verify Surjection Proofs of the Protection Attribute");
                        throw new ProtectionAttributeVerificationFailedException();
                    }
                }
                else
                {
                    issuanceDetails.AssociatedAttributes
                        = await IssueAssociatedAttributes(
                            attributeIssuanceDetails.Where(a => !a.Definition.IsRoot)
                                .ToDictionary(d => identity.Attributes.First(a => a.AttributeName == d.Definition.AttributeName).AttributeId, d => d),
                            statePersistency.TransactionsService,
                            issuer, rootAssetId).ConfigureAwait(false);
                }

                var packet = TransferAssetToUtxo(statePersistency.TransactionsService, targetAccount, rootAssetId);

                if (packet == null)
                {
                    _logger.Error($"[{account.AccountId}]: failed to transfer Root Attribute");
                    throw new RootAttributeTransferFailedException();
                }

                _dataAccessService.AddOrUpdateIdentityTarget(identity.IdentityId, targetAccount.PublicSpendKey.ToHexString(), targetAccount.PublicViewKey.ToHexString());

                issuanceDetails.RootAttribute = new IssuanceDetailsDto.IssuanceDetailsRoot
                {
                    AttributeName = rootAttribute.AttributeName,
                    OriginatingCommitment = packet.SurjectionProof.AssetCommitments[0].ToHexString(),
                    AssetCommitment = packet.TransferredAsset.AssetCommitment.ToHexString(),
                    SurjectionProof = $"{packet.SurjectionProof.Rs.E.ToHexString()}{packet.SurjectionProof.Rs.S[0].ToHexString()}"
                };

                return issuanceDetails;
            }

            async Task<IssuanceDetailsDto> IssueIdpAttributesAsAssociated(
                                                string issuer,
                                                Identity identity,
                                                IEnumerable<AttributeIssuanceDetails> attributeIssuanceDetails,
                                                StatePersistency statePersistency)
            {
                IssuanceDetailsDto issuanceDetails = new IssuanceDetailsDto();

                IdentitiesScheme rootScheme = _dataAccessService.GetRootIdentityScheme(issuer);

                IEnumerable<IdentitiesScheme> identitiesSchemes = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true);
                var rootAttributeDetails = attributeIssuanceDetails.First(a => a.Definition.IsRoot);

                var packet = await IssueAssociatedAttribute(
                                        rootScheme.AttributeSchemeName,
                                        rootAttributeDetails.Value.Value,
                                        rootAttributeDetails.Value.BlindingPointValue,
                                        rootAttributeDetails.Value.BlindingPointRoot,
                                        issuer,
                                        statePersistency.TransactionsService).ConfigureAwait(false);
                _dataAccessService.UpdateIdentityAttributeCommitment(identity.Attributes.FirstOrDefault(a => a.AttributeName == rootScheme.AttributeName).AttributeId, packet.AssetCommitment);

                byte[] rootAssetId = _assetsService.GenerateAssetId(rootScheme.IdentitiesSchemeId, rootAttributeDetails.Value.Value);
                issuanceDetails.AssociatedAttributes = await IssueAssociatedAttributes(
                                    attributeIssuanceDetails
                                        .ToDictionary(d => identity.Attributes.First(a => a.AttributeName == d.Definition.AttributeName).AttributeId, d => d),
                                    statePersistency.TransactionsService,
                                    issuer, rootAssetId).ConfigureAwait(false);

                return issuanceDetails;
            }

            static IEnumerable<AttributeIssuanceDetails> GetValidatedIssuanceDetails(IssueAttributesRequestDTO request, IEnumerable<AttributeDefinition> attributeDefinitions)
            {
                IEnumerable<string> notSupportedAttributeNames = request.Attributes.Keys.Where(k => attributeDefinitions.All(a => a.AttributeName != k));

                if (notSupportedAttributeNames?.Any() ?? false)
                {
                    throw new Exception($"Following attribute names are not supported: {string.Join(',', notSupportedAttributeNames)}");
                }

                IEnumerable<AttributeIssuanceDetails> attributeIssuanceDetails = request.Attributes.Select(kv =>
                    new AttributeIssuanceDetails
                    {
                        Definition = attributeDefinitions.FirstOrDefault(a => a.AttributeName == kv.Key),
                        Value = kv.Value
                    });

                if (!attributeIssuanceDetails.Any(a => a.Definition.IsRoot))
                {
                    throw new MandatoryAttributeValueMissingException(attributeDefinitions.FirstOrDefault(a => a.IsRoot).AttributeName);
                }

                return attributeIssuanceDetails;
            }

            Identity CreateIdentityInDb(AccountDescriptor account, IEnumerable<AttributeIssuanceDetails> issuanceInputDetails)
            {
                var rootAttributeDetails = issuanceInputDetails.First(a => a.Definition.IsRoot);
                Identity identity = _dataAccessService.GetIdentityByAttribute(account.AccountId, rootAttributeDetails.Definition.AttributeName, rootAttributeDetails.Value.Value);
                if (identity == null)
                {
                    _dataAccessService.CreateIdentity(account.AccountId,
                                       rootAttributeDetails.Value.Value,
                                       issuanceInputDetails.Select(d => (d.Definition.AttributeName, d.Value.Value)).ToArray());
                    identity = _dataAccessService.GetIdentityByAttribute(account.AccountId, rootAttributeDetails.Definition.AttributeName, rootAttributeDetails.Value.Value);
                }

                return identity;
            }

            #endregion  Internal Functions
        }

        [HttpPost("Activate")]
        public async Task<IActionResult> Activate(long accountId)
        {
            string integrationKey = _dataAccessService.GetAccountKeyValue(accountId, _integrationIdPRepository.IntegrationKeyName);
            var integrationService = _integrationIdPRepository.GetInstance(integrationKey);
            return Ok(await integrationService.Register(accountId).ConfigureAwait(false));
        }

        #region Private Functions

        private IEnumerable<AttributeValue> GetAttributeValues(string issuer, Identity identity)
        {
            IEnumerable<AttributeDefinition> attributeDefinitions = _dataAccessService.GetAttributesSchemeByIssuer(issuer, true)
                .Select(a => new AttributeDefinition
                {
                    SchemeId = a.IdentitiesSchemeId,
                    AttributeName = a.AttributeName,
                    SchemeName = a.AttributeSchemeName,
                    Alias = a.Alias,
                    Description = a.Description,
                    IsActive = a.IsActive,
                    IsRoot = a.CanBeRoot
                });

            IEnumerable<AttributeValue> attributeValues
                = identity.Attributes.Select(a =>
                    new AttributeValue
                    {
                        Value = a.Content,
                        Definition = attributeDefinitions.FirstOrDefault(d => d.AttributeName == a.AttributeName)
                    });
            return attributeValues;
        }

        private static bool VerifyProtectionAttribute(IdentityAttribute protectionAttribute, byte[] signatureE, byte[] signatureS, byte[] sessionCommitment)
        {
            if (protectionAttribute != null)
            {
                byte[] protectionCommitment = protectionAttribute.Commitment;

                SurjectionProof surjectionProof = new SurjectionProof
                {
                    AssetCommitments = new byte[][] { protectionCommitment },
                    Rs = new BorromeanRingSignature
                    {
                        E = signatureE,
                        S = new byte[][] { signatureS }
                    }
                };

                bool res = ConfidentialAssetsHelper.VerifySurjectionProof(surjectionProof, sessionCommitment);
                return res;
            }

            return true;
        }

        private bool CreateRootAttributeIfNeeded(StatePersistency statePersistency, IdentityAttribute rootAttribute, byte[] rootAssetId)
        {
            bool rootAttributeIssued = false;

            if (rootAttribute != null && rootAttribute.Commitment == null)
            {
                statePersistency.TransactionsService.IssueBlindedAsset(rootAssetId, 0UL.ToByteArray(32), out byte[] originatingCommitment);
                _dataAccessService.UpdateIdentityAttributeCommitment(rootAttribute.AttributeId, originatingCommitment);

                rootAttributeIssued = true;
            }

            return rootAttributeIssued;
        }

        private async Task<IEnumerable<IssuanceDetailsDto.IssuanceDetailsAssociated>> IssueAssociatedAttributes(Dictionary<long, AttributeIssuanceDetails> attributes, IStateTransactionsService transactionsService, string issuer, byte[] rootAssetId = null)
        {
            List<IssuanceDetailsDto.IssuanceDetailsAssociated> issuanceDetails = new List<IssuanceDetailsDto.IssuanceDetailsAssociated>();

            if (attributes.Any(kv => kv.Value.Definition.IsRoot))
            {
                var rootKv = attributes.FirstOrDefault(kv => kv.Value.Definition.IsRoot);
                var packet = await IssueAssociatedAttribute(rootKv.Value.Definition.SchemeName, rootKv.Value.Value.Value, rootKv.Value.Value.BlindingPointValue, rootKv.Value.Value.BlindingPointRoot, issuer, transactionsService).ConfigureAwait(false);
                _dataAccessService.UpdateIdentityAttributeCommitment(rootKv.Key, packet.AssetCommitment);
                issuanceDetails.Add(new IssuanceDetailsDto.IssuanceDetailsAssociated
                {
                    AttributeName = rootKv.Value.Definition.AttributeName,
                    AssetCommitment = packet.AssetCommitment.ToHexString(),
                    BindingToRootCommitment = packet.RootAssetCommitment.ToHexString()
                });
                rootAssetId = _assetsService.GenerateAssetId(rootKv.Value.Definition.SchemeId, rootKv.Value.Value.Value);
            }

            if (rootAssetId == null)
            {
                throw new ArgumentException("Either rootAssetId must be provided outside or one of attributes must be root one");
            }

            foreach (var kv in attributes.Where(a => !a.Value.Definition.IsRoot))
            {
                byte[] rootCommitment = _assetsService.GetCommitmentBlindedByPoint(rootAssetId, kv.Value.Value.BlindingPointRoot);

                var packet = await IssueAssociatedAttribute(kv.Value.Definition.SchemeName, kv.Value.Value.Value, kv.Value.Value.BlindingPointValue, rootCommitment, issuer, transactionsService).ConfigureAwait(false);
                issuanceDetails.Add(new IssuanceDetailsDto.IssuanceDetailsAssociated
                {
                    AttributeName = kv.Value.Definition.AttributeName,
                    AssetCommitment = packet.AssetCommitment.ToHexString(),
                    BindingToRootCommitment = packet.RootAssetCommitment.ToHexString()
                });
                _dataAccessService.UpdateIdentityAttributeCommitment(kv.Key, packet.AssetCommitment);
            }

            return issuanceDetails;
        }

        private async Task<IssueAssociatedBlindedAsset> IssueAssociatedAttribute(string schemeName,
                                                      string content,
                                                      byte[] blindingPointValue,
                                                      byte[] blindingPointRoot,
                                                      string issuer,
                                                      IStateTransactionsService transactionsService)
        {
            byte[] assetId = await _assetsService.GenerateAssetId(schemeName, content, issuer).ConfigureAwait(false);
            byte[] groupId;

            if (AttributesSchemes.AttributeSchemes.FirstOrDefault(a => a.Name == schemeName)?.ValueType == AttributeValueType.Date)
            {
                groupId = await _identityAttributesService.GetGroupId(schemeName, DateTime.ParseExact(content, "yyyy-MM-dd", null), issuer).ConfigureAwait(false);
            }
            else
            {
                groupId = schemeName switch
                {
                    AttributesSchemes.ATTR_SCHEME_NAME_PLACEOFBIRTH => await _identityAttributesService.GetGroupId(schemeName, content, issuer).ConfigureAwait(false),
                    _ => await _identityAttributesService.GetGroupId(schemeName, issuer).ConfigureAwait(false),
                };
            }

            return transactionsService.IssueAssociatedAsset(assetId, groupId, blindingPointValue, blindingPointRoot, out byte[] originatingCommitment);
        }

        private async Task<bool> VerifyFaceImage(string imageContent, string idContent, string publicKey)
        {
            if (!string.IsNullOrEmpty(imageContent))
            {
                try
                {
                    var biometricResult
                        = await $"{Request.Scheme}://{Request.Host.ToUriComponent()}/biometric/"
                            .AppendPathSegment("VerifyPersonFace")
                            .PostJsonAsync(
                                new BiometricVerificationDataDto
                                {
                                    KeyImage = ConfidentialAssetsHelper.GetRandomSeed().ToHexString(),
                                    Issuer = publicKey,
                                    RegistrationKey = idContent,
                                    ImageString = imageContent
                                })
                            .ReceiveJson<BiometricSignedVerificationDto>().ConfigureAwait(false);


                    return biometricResult != null;
                }
                catch (FlurlHttpException)
                {
                    return false;
                }
            }

            return true;
        }

        private TransferAssetToUtxo TransferAssetToUtxo(IStateTransactionsService transactionsService, ConfidentialAccount account, byte[] rootAssetId)
        {
            return transactionsService.TransferAssetToUtxo(rootAssetId, account);
        }

        #endregion Private Functions
    }
}