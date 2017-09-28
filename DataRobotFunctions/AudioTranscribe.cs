/*
 * OneDrive Data Robot - Sample Code
 * Copyright (c) Microsoft Corporation
 * All rights reserved. 
 * 
 * MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of 
 * this software and associated documentation files (the ""Software""), to deal in 
 * the Software without restriction, including without limitation the rights to use, 
 * copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
 * Software, and to permit persons to whom the Software is furnished to do so, 
 * subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all 
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, 
 * INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A 
 * PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION 
 * OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System;
using Newtonsoft.Json;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Graph;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace DataRobotFunctions
{
    public static class AudioTranscribe
    {
        /// <summary>
        /// These properties should be loaded from a configuration file instead of being hard coded.
        /// </summary>
        private const string idaClientId = "[ClientId]";
        private const string idaClientSecret = "[ClientSecret]";
        private const string idaAuthorityUrl = "https://login.microsoftonline.com/common";
        private const string idaMicrosoftGraphUrl = "https://graph.microsoft.com";
        private const string speechAPIKey = "[SpeechAPIKey]";
        private const string acceptedAudioFileExtension = ".wav";

        /// <summary>
        /// This is the Azure Function entry point. We're using an HttpTrigger function, which 
        /// receives the Microsoft Graph webhook and handles responding according to the Microsoft Graph
        /// requirements.
        /// </summary>
        /// <param name="req">Incoming HTTP request</param>
        /// <param name="syncStateTable">Table that contains our user state information. This is connected to the TableConnection parameter, either in local.settings.json or in your Azure Function configuration.</param>
        /// <param name="tokenCacheTable">Table that contains the ADAL token cache persisted blobs. This is connected to the TableConnection parameter, either in local.settings.json or in your Azure Function configuration.</param>
        /// <param name="log">An Azure Function log we can write to for debugging purposes</param>
        /// <returns></returns>
        [FunctionName("AudioTranscribe")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)]HttpRequestMessage req,
            [Table(tableName: "syncState", Connection = "TableConnection")] CloudTable syncStateTable,
            [Table(tableName: "tokenCache", Connection = "TableConnection")]CloudTable tokenCacheTable, 
            TraceWriter log)
        {
            log.Info($"Webhook was triggered!");

            // Handle validation scenario for creating a new webhook subscription
            string validationToken;
            if (GetValidationToken(req, out validationToken))
            {
                return PlainTextResponse(validationToken);
            }

            // Process each notification
            var response = await ProcessWebhookNotificationsAsync(req, log, async hook =>
            {
                return await CheckForSubscriptionChangesAsync(hook.SubscriptionId, syncStateTable, tokenCacheTable, log);
            });
            return response;
        }

        /// <summary>
        /// Helper function that contains the work of parsing the incoming HTTP request JSON 
        /// body into webhook subscription notifications, and then calling the processSubscriptionNotification
        /// function for each received notification.
        /// </summary>
        /// <param name="req">Incoming HTTP Request.</param>
        /// <param name="log">Log used for writing out tracing information.</param>
        /// <param name="processSubscriptionNotification">Async function that is called per-notification in the request.</param>
        /// <returns></returns>
        private static async Task<HttpResponseMessage> ProcessWebhookNotificationsAsync(HttpRequestMessage req, TraceWriter log, Func<SubscriptionNotification, Task<bool>> processSubscriptionNotification)
        {
            // Read the body of the request and parse the notification
            string content = await req.Content.ReadAsStringAsync();
            log.Verbose($"Raw request content: {content}");

            // In a production application you should queue the work to be done in an Azure Queue and _not_ do the heavy lifting 
            // in the webhook request handler.

            var webhooks = JsonConvert.DeserializeObject<WebhookNotification>(content);
            if (webhooks?.Notifications != null)
            {
                // Since webhooks can be batched together, loop over all the notifications we receive and process them separately.
                foreach (var hook in webhooks.Notifications)
                {
                    log.Info($"Hook received for subscription: '{hook.SubscriptionId}' Resource: '{hook.Resource}', clientState: '{hook.ClientState}'");
                    try
                    {
                        if (!(await processSubscriptionNotification(hook)))
                        {
                            // If we didn't find the subscription, return HTTP Gone so the Graph service can 
                            // be intelligent about delivering notifications in the future.
                            return req.CreateResponse(HttpStatusCode.Gone);
                        }
                    } catch (Exception ex)
                    {
                        log.Error($"Error processing subscription notification. Subscription {hook.SubscriptionId} was skipped. {ex.Message}", ex);
                    }
                }

                // After we process all the messages, return an empty response.
                return req.CreateResponse(HttpStatusCode.NoContent);
            }
            else
            {
                log.Info($"Request was incorrect. Returning bad request.");
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }
        }

        /// <summary>
        /// Data structure for the request payload for an incoming webhook.
        /// </summary>
        private class WebhookNotification
        {
            [JsonProperty("value")]
            public SubscriptionNotification[] Notifications { get; set; }
        }

        /// <summary>
        /// Data structure for the notification payload inside the incoming webhook.
        /// </summary>
        private class SubscriptionNotification
        {
            [JsonProperty("clientState")]
            public string ClientState { get; set; }
            [JsonProperty("resource")]
            public string Resource { get; set; }
            [JsonProperty("subscriptionId")]
            public string SubscriptionId { get; set; }
        }

        /// <summary>
        /// Parse the request query string to see if there is a validationToken parameter, and return the value if found.
        /// </summary>
        /// <param name="req">Incoming HTTP request.</param>
        /// <param name="token">Out parameter containing the validationToken, if found.</param>
        /// <returns></returns>
        private static bool GetValidationToken(HttpRequestMessage req, out string token)
        {
            Dictionary<string, string> qs = req.GetQueryNameValuePairs()
                                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            return qs.TryGetValue("validationToken", out token);
        }

        /// <summary>
        /// Look up the subscription in our state table and see if we know anything about the subscription.
        /// If the subscription is found, then check for changed files and process them accordingly.
        /// </summary>
        /// <param name="subscriptionId">The subscription ID that generated the notification</param>
        /// <param name="syncStateTable">An Azure Table that contains our sync state information for each subscription / user.</param>
        /// <param name="tokenCacheTable">An Azure Table we use to cache ADAL tokens.</param>
        /// <param name="log">TraceWriter for debug output</param>
        /// <returns></returns>
        private static async Task<bool> CheckForSubscriptionChangesAsync(string subscriptionId, CloudTable syncStateTable, CloudTable tokenCacheTable, TraceWriter log)
        {
            // Retrieve our stored state from an Azure Table
            StoredSubscriptionState state = StoredSubscriptionState.Open(subscriptionId, syncStateTable);
            if (state == null)
            {
                log.Info($"Unknown subscription ID: '{subscriptionId}'.");
                return false;
            }

            log.Info($"Found subscription ID: '{subscriptionId}' with stored delta URL: '{state.LastDeltaToken}'.");
            
            // Create a new instance of the Graph SDK client for the user attached to this subscription
            GraphServiceClient client = new GraphServiceClient(new DelegateAuthenticationProvider(async (request) => {
                string accessToken = await RetrieveAccessTokenAsync(state.SignInUserId, tokenCacheTable, log);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            }));
            client.BaseUrl = "https://graph.microsoft.com/stagingv1.0";

            // Query for items that have changed since the last notification was received
            List<DriveItem> changedDriveItems = await FindChangedDriveItemsAsync(state, client, log);

            // Iterate over the changed items and perform our work
            foreach (var driveItem in changedDriveItems)
            {
                try
                {
                    log.Info($"Processing file: {driveItem.Name}");
                    await ProcessAudioFileAsync(client, driveItem, log);
                }
                catch (Exception ex)
                {
                    log.Info($"Error with file {driveItem.Name}: {ex.Message}");
                }
            }

            // Update our saved state for this subscription
            state.Insert(syncStateTable);
            return true;
        }

        /// <summary>
        /// Request the delta stream from OneDrive to find files that have changed between notifications for this account 
        /// </summary>
        /// <param name="state">Our internal state information for the subscription we're processing.</param>
        /// <param name="client">Graph client for the attached user.</param>
        /// <param name="log">Tracewriter for debug output</param>
        /// <returns></returns>
        private static async Task<List<DriveItem>> FindChangedDriveItemsAsync(StoredSubscriptionState state, GraphServiceClient client, TraceWriter log)
        {
            string DefaultLatestDeltaUrl = idaMicrosoftGraphUrl + "/v1.0/drives/" + state.DriveId + "/root/delta?token=latest";

            // We default to reading the "latest" state of the drive, so we don't have to process all the files in the drive
            // when a new subscription comes in.
            string deltaUrl = state?.LastDeltaToken ?? DefaultLatestDeltaUrl;
            List<DriveItem> changedDriveItems = new List<DriveItem>();

            // Create an SDK request using the URL, instead of building up the request using the SDK
            IDriveItemDeltaRequest request = new DriveItemDeltaRequest(deltaUrl, client, null);

            // We max out at 50 requests of delta responses, just for demo purposes.
            const int MaxLoopCount = 50;
            for (int loopCount = 0; loopCount < MaxLoopCount && request != null; loopCount++)
            {
                log.Info($"Making request for '{state.SubscriptionId}' to '{deltaUrl}' ");
                
                // Get the next page of delta results
                IDriveItemDeltaCollectionPage deltaResponse = await request.GetAsync();

                // Filter to the audio files we're interested in working with and add them to our list
                var changedFiles = (from f in deltaResponse
                                    where f.File != null && 
                                            f.Name != null && 
                                            (f.Name.EndsWith(acceptedAudioFileExtension) || f.Audio != null) && 
                                            f.Deleted == null
                                    select f);
                changedDriveItems.AddRange(changedFiles);

                // Figure out how to proceed, whether we have more pages of changes to retrieve or not.
                if (null != deltaResponse.NextPageRequest)
                {
                    request = deltaResponse.NextPageRequest;
                }
                else if (null != deltaResponse.AdditionalData["@odata.deltaLink"])
                {
                    string deltaLink = (string)deltaResponse.AdditionalData["@odata.deltaLink"];

                    log.Verbose($"All changes requested, nextDeltaUrl: {deltaLink}");
                    state.LastDeltaToken = deltaLink;

                    return changedDriveItems;
                }
                else
                {
                    // Shouldn't get here, but just in case, we don't want to get stuck in a loop forever.
                    request = null;
                }
            }

            // If we exit the For loop without returning, that means we read MaxLoopCount pages without finding a deltaToken
            log.Info($"Read through MaxLoopCount pages without finding an end. Too much data has changed and we're going to start over on the next notification.");
            state.LastDeltaToken = DefaultLatestDeltaUrl;

            return changedDriveItems;
        }

        /// <summary>
        /// Validate that it makes sense for the robot to run on this file
        /// </summary>
        /// <param name="driveItem">The item we're validating</param>
        /// <param name="log">TraceWriter for logging output</param>
        /// <param name="client">MSGraph service client</param>
        /// <returns></returns>
        private static async Task<bool> IsFileValidAsync(DriveItem driveItem, TraceWriter log, GraphServiceClient client)
        {
            // Validate that this file can be transcribed (we match a specific filename format)
            string[] split = driveItem.Name.Split(new[] { '.' });
            if (split.Length != 3)
            {
                // This isn't a valid file for our experiment
                log.Info($"Filename {driveItem.Name} didn't match the format we expected, so we're skipping it.");
                return false;
            }

            // Check to make sure we haven't already processed this item (and thus potentially get stuck in a loop)
            var listItem = await client.Drives[driveItem.ParentReference.DriveId].Items[driveItem.Id].ListItem.Request().Expand("fields").GetAsync();

            if (listItem.Fields.AdditionalData.TryGetValue("Language", out object existingLanguageFieldValue) &&
                !string.IsNullOrEmpty(existingLanguageFieldValue.ToString()))
            {
                log.Info($"We've already added metadata to this item, so let's ignore it.");
                return false;
            }

            if (driveItem.Size > (4 * 1024 * 1024))
            {
                log.Info($"File was larger than 4 MB so we skipped processing it.");
                return false;
            }

            return true;
        }

        private static bool TryParseLanguage(DriveItem driveItem, out string languageCode, out string languageDisplayName)
        {
            // Detect the source language for this file (we're just reading it from the filename, but you could use a cognitive service for this)
            string[] split = driveItem.Name.Split(new[] { '.' });
            if (split.Length != 3)
            {
                languageCode = null;
                languageDisplayName = null;
                return false;
            }

            languageCode = split[1];
            if (LanguageTable.TryGetValue(languageCode, out languageDisplayName))
            {
                return true;
            }
            return false;
        }

        // Use the cognative services APIs to transcribe and transcode the audio in the file, if it matches the required parameters
        private static async Task ProcessAudioFileAsync(GraphServiceClient client, DriveItem driveItem, TraceWriter log)
        {
            if (!(await IsFileValidAsync(driveItem, log, client)))
            {
                return;
            }

            if (!TryParseLanguage(driveItem, out string languageCode, out string languageDisplayName))
            {
                log.Info($"Language could not be detected for {driveItem.Name}.");
                return;
            }
            
            // Download the contents of the audio file
            log.Info("Downloading audio file contents...");
            byte[] audioBytes;

            using (var contentStream = await client.Drives[driveItem.ParentReference.DriveId].Items[driveItem.Id].Content.Request().GetAsync())
            {
                audioBytes = StreamToBytes(contentStream);
            }

            // Transcribe the file using cognitive services APIs
            log.Info($"Transcribing the file...");
            var bingAuthToken = await AzureAuthHelper.FetchAccessTokenAsync(speechAPIKey);
            var transcriptionValue = await RequestTranscriptionAsync(audioBytes, languageCode, bingAuthToken, log);

            if (null != transcriptionValue)
            {
                log.Info($"Patching metadata on file...");

                // Build up our changes to the custom columns for our update operation
                var patchedListItem = new ListItem
                {
                    Fields = new FieldValueSet
                    {
                        AdditionalData = new Dictionary<string, object>
                    {
                        {"Language",  languageDisplayName },
                        {"Transcription", transcriptionValue }
                    }
                    }
                };

                // Update the driveItem with the langauge information
                await client.Drives[driveItem.ParentReference.DriveId].Items[driveItem.Id].ListItem.Request().UpdateAsync(patchedListItem);
                log.Info($"Updated {driveItem.Name} with transcription.");
            }
        }

        // reference data (language codes supported by the cognitive service and their display names)
        private static Dictionary<string, string> LanguageTable = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
        {
            { "ko-KR", "Korean (Korea)" },
            { "en-US", "English (US)"},
            { "fr-FR", "French (France)" },
            { "de-DE", "German (Germany)"},
            { "es-ES", "Spanish (Spain)" },
            { "ja-JP", "Japaneese (Japan)" },
            { "it-IT", "Italian (Italy)" },
            { "pr-BR", "Portuguese (Brazil)" },
            { "ru-RU", "Russian (Russia)" },
            { "zh-CN", "Chinese (Mandarin, simplified)" }
        }; // source: https://docs.microsoft.com/en-us/azure/cognitive-services/speech/api-reference-rest/bingvoicerecognition

        /* 
        /// <summary>
        /// Get transcription of audio stream via Bing STT service, reference: https://docs.microsoft.com/en-us/azure/cognitive-services/speech/getstarted/getstartedrest */
        /// </summary>
        /// <param name="audioBytes">Bytes of the audio file in WAV format.</param>
        /// <param name="languageCode">The detected language of the audio.</param>
        /// <param name="authToken">Speech API bearer token.</param>
        /// <param name="log">TraceWriter for debug output.</param>
        /// <returns>The transcript of the audio file if the transcription was succeessful.</returns>
        private static async Task<string> RequestTranscriptionAsync(byte[] audioBytes, string languageCode, string authToken, TraceWriter log)
        {
            string conversation_url = $"https://speech.platform.bing.com/speech/recognition/conversation/cognitiveservices/v1?language={languageCode}";
            string dictation_url = $"https://speech.platform.bing.com/speech/recognition/dictation/cognitiveservices/v1?language={languageCode}";

            try
            {
                HttpResponseMessage response = await PostAudioRequestAsync(dictation_url, audioBytes, authToken);
                string responseJson = await response.Content.ReadAsStringAsync();
                JObject data = JObject.Parse(responseJson);
                return data["DisplayText"].ToString();
            }
            catch (Exception ex)
            {
                log.Error($"Unexpected response from transcription service: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create HTTP POST request to a url with a given post body and authorization token.
        /// </summary>
        private static async Task<HttpResponseMessage> PostAudioRequestAsync(string url, byte[] bodyContents, string authToken)
        {
            var payload = new ByteArrayContent(bodyContents);
            HttpResponseMessage response;

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + authToken);
                payload.Headers.TryAddWithoutValidation("content-type", "audio/wav");
                response = await client.PostAsync(url, payload);
            }

            return response;
        }

        /// <summary>
        /// Convert a stream into a byte array
        /// </summary>
        private static byte[] StreamToBytes(Stream stream)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Generate a plain text response, used to return the validationToken during the subscription validation flow
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static HttpResponseMessage PlainTextResponse(string text)
        {
            HttpResponseMessage response = new HttpResponseMessage()
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                        text,
                        System.Text.Encoding.UTF8,
                        "text/plain"
                    )
            };
            return response;
        }

        /// <summary>
        /// Retrieve an access token for a particular user from our token cache using ADAL.
        /// </summary>
        private static async Task<string> RetrieveAccessTokenAsync(string signInUserId, CloudTable tokenCacheTable, TraceWriter log)
        {
            log.Verbose($"Retriving new accessToken for signInUser: {signInUserId}");

            var tokenCache = new AzureTableTokenCache(signInUserId, tokenCacheTable);
            var authContext = new AuthenticationContext(idaAuthorityUrl, tokenCache);

            try
            {
                var userCredential = new UserIdentifier(signInUserId, UserIdentifierType.UniqueId);
                // Don't really store your clientId and clientSecret in your code. Read these from configuration.
                var clientCredential = new ClientCredential(idaClientId, idaClientSecret);
                var authResult = await authContext.AcquireTokenSilentAsync(idaMicrosoftGraphUrl, clientCredential, userCredential);
                return authResult.AccessToken;
            }
            catch (AdalSilentTokenAcquisitionException ex)
            {
                log.Info($"ADAL Error: Unable to retrieve access token: {ex.Message}");
                return null;
            }
        }

        /*** The code below is shared between the OneDriveDataRobot project and this project. This should be refactored into a shared module, but wasn't for readability ***/

        /// <summary>
        /// Persists information about a subscription, userId, and deltaToken state. This class is shared between the Azure Function and the bootstrap project
        /// </summary>
        public class StoredSubscriptionState : TableEntity
        {
            public StoredSubscriptionState()
            {
                this.PartitionKey = "AAA";
            }

            public string SignInUserId { get; set; }
            public string LastDeltaToken { get; set; }
            public string SubscriptionId { get; set; }
            public string DriveId { get; set; }


            public static StoredSubscriptionState CreateNew(string subscriptionId)
            {
                var newState = new StoredSubscriptionState();
                newState.RowKey = subscriptionId;
                newState.SubscriptionId = subscriptionId;
                return newState;
            }

            public void Insert(CloudTable table)
            {
                TableOperation insert = TableOperation.InsertOrReplace(this);
                table.Execute(insert);
            }

            public static StoredSubscriptionState Open(string subscriptionId, CloudTable table)
            {
                TableOperation retrieve = TableOperation.Retrieve<StoredSubscriptionState>("AAA", subscriptionId);
                TableResult results = table.Execute(retrieve);
                return (StoredSubscriptionState)results.Result;
            }
        }

        /// <summary>
        /// Keep track of file specific information for a short period of time, so we can avoid repeatedly acting on the same file
        /// </summary>
        public class FileHistory : TableEntity
        {
            public FileHistory()
            {
                this.PartitionKey = "BBB";
            }

            public string ExcelSessionId { get; set; }
            public DateTime LastAccessedDateTime { get; set; }

            public static FileHistory CreateNew(string userId, string fileId)
            {
                var newState = new FileHistory
                {
                    RowKey = $"{userId},{fileId}"
                };
                return newState;
            }

            public void Insert(CloudTable table)
            {
                TableOperation insert = TableOperation.InsertOrReplace(this);
                table.Execute(insert);
            }

            public static FileHistory Open(string userId, string fileId, CloudTable table)
            {
                TableOperation retrieve = TableOperation.Retrieve<FileHistory>("BBB", $"{userId},{fileId}");
                TableResult results = table.Execute(retrieve);
                return (FileHistory)results.Result;
            }
        }

        /// <summary>
        /// ADAL TokenCache implementation that stores the token cache in the provided Azure CloudTable instance.
        /// This class is shared between the Azure Function and the bootstrap project.
        /// </summary>
        public class AzureTableTokenCache : TokenCache
        {
            private readonly string signInUserId;
            private readonly CloudTable tokenCacheTable;

            private TokenCacheEntity cachedEntity;      // data entity stored in the Azure Table

            public AzureTableTokenCache(string userId, CloudTable cacheTable)
            {
                signInUserId = userId;
                tokenCacheTable = cacheTable;

                this.AfterAccess = AfterAccessNotification;

                cachedEntity = ReadFromTableStorage();
                if (null != cachedEntity)
                {
                    Deserialize(cachedEntity.CacheBits);
                }
            }

            private TokenCacheEntity ReadFromTableStorage()
            {
                TableOperation retrieve = TableOperation.Retrieve<TokenCacheEntity>(TokenCacheEntity.PartitionKeyValue, signInUserId);
                TableResult results = tokenCacheTable.Execute(retrieve);
                return (TokenCacheEntity)results.Result;
            }

            private void AfterAccessNotification(TokenCacheNotificationArgs args)
            {
                if (this.HasStateChanged)
                {
                    if (cachedEntity == null)
                    {
                        cachedEntity = new TokenCacheEntity();
                    }
                    cachedEntity.RowKey = signInUserId;
                    cachedEntity.CacheBits = Serialize();
                    cachedEntity.LastWrite = DateTime.Now;

                    TableOperation insert = TableOperation.InsertOrReplace(cachedEntity);
                    tokenCacheTable.Execute(insert);

                    this.HasStateChanged = false;
                }
            }

            /// <summary>
            /// Representation of the data stored in the Azure Table
            /// </summary>
            private class TokenCacheEntity : TableEntity
            {
                public const string PartitionKeyValue = "tokenCache";
                public TokenCacheEntity()
                {
                    this.PartitionKey = PartitionKeyValue;
                }

                public byte[] CacheBits { get; set; }
                public DateTime LastWrite { get; set; }
            }

        }



    }
}
