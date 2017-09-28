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

namespace OneDriveDataRobot.Controllers
{
    using OneDriveDataRobot.Models;
    using OneDriveDataRobot.Utils;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Web.Http;
    using static OneDriveDataRobot.AuthHelper;
    using Microsoft.Graph;

    [Authorize]
    public class SetupController : ApiController
    {
        /// <summary>
        /// This method is called by the JavaScript on the home page to activate the data robot
        /// </summary>
        /// <param name="driveId"></param>
        /// <returns></returns>
        public async Task<IHttpActionResult> ActivateRobot(string driveId)
        {
            // Make sure we still have a user signed in before we do anything (we'll need this to get auth tokens for MS Graph)
            var signedInUserId = AuthHelper.GetUserId();
            if (string.IsNullOrEmpty(signedInUserId))
            {
                return Ok(new DataRobotSetup { Success = false, Error = "User needs to sign in." });
            }

            // Setup a Microsoft Graph client for calls to the graph
            var client = GetGraphClient();

            // Configure the document library with the custom columns, if they don't already exist.
            try
            {
                await ProvisionDocumentLibraryAsync(driveId, client);
            }
            catch (Exception ex)
            {
                return Ok(new DataRobotSetup { Success = false, Error = $"Unable to provision the selected document library: {ex.Message}" });
            }

            // Check to see if this user already has a subscription, so we avoid duplicate subscriptions (this sample only allows a user to hook up the data robot to a single document library)
            var storedState = StoredSubscriptionState.FindUser(signedInUserId, AzureTableContext.Default.SyncStateTable);
            var subscription = await CreateOrRefreshSubscriptionAsync(driveId, client, signedInUserId, storedState?.SubscriptionId);

            var results = new DataRobotSetup { SubscriptionId = subscription.Id };

            if (storedState == null)
            {
                storedState = StoredSubscriptionState.CreateNew(subscription.Id, driveId);
                storedState.SignInUserId = signedInUserId;
            }

            // Catch up our delta link so we only start working on files modified starting now
            var latestDeltaResponse = await client.Drives[driveId].Root.Delta("latest").Request().GetAsync();
            storedState.LastDeltaToken = latestDeltaResponse.AdditionalData["@odata.deltaLink"] as string;

            // Once we have a subscription, then we need to store that information into our Azure Table
            storedState.Insert(AzureTableContext.Default.SyncStateTable);

            results.Success = true;
            results.ExpirationDateTime = subscription.ExpirationDateTime;

            return Ok(results);
        }

        /// <summary>
        /// Ensure that all the required elements are provisioned into the SharePoint document library
        /// </summary>
        /// <param name="driveId"></param>
        /// <param name="client"></param>
        /// <returns></returns>
        private static async Task ProvisionDocumentLibraryAsync(string driveId, GraphServiceClient client)
        {
            // Check to make sure the drive we're targeting is supported
            var drive = await client.Drives[driveId].Request().GetAsync();
            if (drive.DriveType != "documentLibrary")
            {
                throw new InvalidOperationException("The selected drive type is not compatible with this sample. You must select a SharePoint document library.");
            }

            // Ensure sure that the custom columns we need for the data robot to function exist in the target document library
            ColumnDefinition[] expectedColumns = new ColumnDefinition[]
            {
                new ColumnDefinition { Name = "Language", Text = new TextColumn { AllowMultipleLines = false }},
                new ColumnDefinition { Name = "Transcription", Text = new TextColumn { AllowMultipleLines = true }}
            };

            var columns = await client.Drives[driveId].List.Columns.Request().Select("id,name,displayName,text").GetAsync();

            // Evaluate which columns are missing from the site
            List<ColumnDefinition> columnsToCreate = new List<ColumnDefinition>();
            foreach (var requiredColumn in expectedColumns)
            {
                if (!columns.Any(x => x.Name == requiredColumn.Name))
                {
                    columnsToCreate.Add(requiredColumn);
                }
            }

            // Add any required columns to the column definition for the document library
            foreach (var column in columnsToCreate)
            {
                Console.WriteLine($"Provisoning column {column.Name}...");
                await client.Drives[driveId].List.Columns.Request().AddAsync(column);
            }
        }

        /// <summary>
        /// Create a new instance of the GraphClient to use for the current user context
        /// </summary>
        /// <returns></returns>
        private GraphServiceClient GetGraphClient()
        {
            string graphBaseUrl = SettingsHelper.MicrosoftGraphBaseUrl;
            GraphServiceClient client = new GraphServiceClient(new DelegateAuthenticationProvider(async (req) =>
            {
                // Get a fresh auth token
                var authToken = await GetUserAccessTokenSilentAsync(graphBaseUrl);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken.AccessToken}");
            }));
            client.BaseUrl = "https://graph.microsoft.com/stagingv1.0";
            return client;
        }


        /// <summary>
        /// Tries to create a new webhook subscription via MS Graph and returns the details of the subscription if successful
        /// </summary>
        /// <param name="driveId"></param>
        /// <param name="client"></param>
        /// <param name="userId"></param>
        /// <param name="existingSubscriptionId"></param>
        /// <returns></returns>
        private static async Task<Subscription> CreateOrRefreshSubscriptionAsync(string driveId, GraphServiceClient client, string userId, string existingSubscriptionId = null)
        {
            Console.WriteLine("Creating webhook subscription...");
            var notificationSubscription = new Subscription()
            {
                ChangeType = "updated",
                NotificationUrl = SettingsHelper.NotificationUrl + Guid.NewGuid().ToString("b"),
                Resource = $"/drives/{driveId}/root",
                ExpirationDateTime = DateTime.UtcNow.AddDays(3),
                ClientState = $"odr_{userId}"
            };

            Subscription createdSubscription = null;
            if (!string.IsNullOrEmpty(existingSubscriptionId))
            {
                // See if our existing subscription can be extended to today + 3 days
                try
                {
                    createdSubscription = await client.Subscriptions[existingSubscriptionId].Request().UpdateAsync(notificationSubscription);
                }
                catch
                {
                    // If the subscription no longer exists, we expect this failure case.
                }
            }

            if (null == createdSubscription)
            {
                // No existing subscription or we failed to update the existing subscription, so create a new one
                try
                {
                    createdSubscription = await client.Subscriptions.Request().AddAsync(notificationSubscription);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Webhook validation failed. Did you remember to run ngrok.exe?");
                }
            }

            return createdSubscription;
        }

        /// <summary>
        /// Called by the homepage to remove the subscription and disable the data robot on the previously selected document library.
        /// This does not deprovision the changes to the document library, since that would involve potentially removing user data.
        /// </summary>
        /// <returns></returns>
        public async Task<IHttpActionResult> DisableRobot()
        {
            // Make sure we still have a user signed in before we do anything (we'll need this to get auth tokens for MS Graph)
            var signedInUserId = AuthHelper.GetUserId();
            if (string.IsNullOrEmpty(signedInUserId))
            {
                return Ok(new DataRobotSetup { Success = false, Error = "User needs to sign in." });
            }

            // Setup a Microsoft Graph client for calls to the graph
            var client = GetGraphClient();

            // See if the robot was previous activated for the signed in user.
            var robotSubscription = StoredSubscriptionState.FindUser(signedInUserId, AzureTableContext.Default.SyncStateTable);

            if (null == robotSubscription)
            {
                return Ok(new DataRobotSetup { Success = true, Error = "The robot wasn't activated for you anyway!" });
            }

            // Remove the webhook subscription
            try
            {
                await client.Subscriptions[robotSubscription.SubscriptionId].Request().DeleteAsync();
            }
            catch
            {
                // If the subscription doesn't exist or we get an error, we'll just consider it OK.
            }

            // Remove the robotSubscription information
            try
            {
                robotSubscription.Delete(AzureTableContext.Default.SyncStateTable);
            } catch (Exception ex)
            {
                return Ok(new DataRobotSetup { Success = false, Error = $"Unable to delete subscription information in our database. Please try again. ({ex.Message})" });
            }

            return Ok(new DataRobotSetup { Success = true, Error = "The robot was deactivated from your account." });
        }
    }
}