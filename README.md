# OneDrive Data Robot Azure Function Sample Code

This project provides an example implementation for connecting Azure Functions to OneDrive to enable your solution to react to changes in files in OneDrive nearly instantly.

The project consists of two parts:

* An [Azure Function](https://azure.microsoft.com/services/functions/) definition that handles the processing of webhook notifications and the resulting work from those notifications.
* An ASP.NET MVC application that activates and deactivates the OneDrive Data Robot for a signed in user.

In this scenario, the benefit of using Azure Function is that the load is required by the data robot is dynamic and hard to predict.
Instead of scaling out an entire web application to handle the load, Azure Functions can scale dynamically based on the load required at any given time.
This provides a cost-savings measure for hosting the application while still ensuring high performance results.

## Getting Started

To get started with the sample, you need to complete the following steps:

1. Register a new application with Azure Active Directory, generate an app password, and provide a redirect URI for the application (use `https://localhost:44382` for running from Visual Studio).
2. Create a new Azure Storage instance for this project, and copy the connection string into `Web.config` in the ASP.NET project and `local.settings.json` in the Azure function project .
3. Run the sample project and sign-in with your Office 365 account and activate the data robot by clicking the **Pick Document Library** button.
4. Navigate to the document library.
5. Watch the data robot update the metadata on files automatically.

### Register a new application

To register a new application with Azure Active Directory, log into the [Azure Portal](https://portal.azure.com).

After logging into the Azure Portal, follow these steps to register the sample application:

1. Navigate to the **Azure Active Directory** module.
2. Select **App registrations** and click **New application registration**.
    1. Type the name of your file handler application.
    2. Ensure **Application Type** is set to **Web app / API**
    3. Enter a sign-on URL for your application, for this sample use `https://localhost:44382`.
    4. Click **Create** to create the app.
3. After the app has been created successfully, select the app from the list of applications. It should be at the bottom of the list.
4. Copy the **Application ID** for the app you registered and paste it into two places:
    * In the [`Web.config`](OneDriveDataRobot/Web.config) file on the line: `<add key="ida:ClientId" value="[ClientId]" />`
    * In the `AutoTranscribe.cs` file on the line: `private const string idaClientId = "[ClientId]";`
5. Configure the application settings for this sample:
    1. Select **Reply URLs** and ensure that `https://localhost:44382` is listed.
    2. Select **Required Permissions** and then **Add**.
    3. Select **Select an API** and then choose **Microsoft Graph** and click **Select**.
    4. Find the permission **Have full access to user files** and check the box next to it, then click **Select**, and then **Done**.
    5. Select **Keys** and generate a new application key by entering a description for the key, selecting a duration, and then click **Save**. Copy the value of the displayed key since it will only be displayed once. Paste it into two places:
       * In the `Web.config` file on the line: `<add key="ida:ClientSecret" value="[ClientSecret]" />`
       * In the `AutoTranscribe.cs` file on the line: `private const string idaClientSecret = "[ClientSecret]"`

### Generate a cognitive services speech API key

This sample uses the [Bing Speech API](https://azure.microsoft.com/en-us/services/cognitive-services/speech/) to transcribe the audio files into the metadata.
To use the Speech API, you need to have an API key for your application.
To obtain an API key:

1. Visit the [Try Cognitive Services](https://azure.microsoft.com/en-us/try/cognitive-services/?api=speech-api) page
2. Find the Bing Speech API and click **Get API Key**
3. Agree to the terms of use, and then copy the key and paste it into `AudioTranscribe.cs` on the line: `private const string speechAPIKey = "[SpeechAPIKey]";`.

### For local debugging, use ngrok.exe

To receive webhook notifications on your developer machine, which likely isn't accessible from the internet, you need a proxy that can route traffic
from the internet to your local computer.

You can download [NGrok](https://ngrok.com/) as one example of such a tool to enable running this sample from your local computer.

After downloading NGrok, launch it with the following command line:

```console
ngrok http -host-header=rewrite localhost:7071
```

This will establish a new HTTP and HTTPS tunnel to your local application.

```console
ngrok by @inconshreveable                                                                               (Ctrl+C to quit)

Session Status                online
Version                       2.2.8
Region                        United States (us)
Web Interface                 http://127.0.0.1:4040
Forwarding                    http://ac47ffcd.ngrok.io -> localhost:7071
Forwarding                    https://ac47ffcd.ngrok.io -> localhost:7071

Connections                   ttl     opn     rt1     rt5     p50     p90
                              0       0       0.00    0.00    0.00    0.00
```

Copy the forwarding URL value from the console (`https://ac47ffcd.ngrok.io` in the above example) and paste it into the web.config file on the line
that look like this: `<add key="ida:NotificationUrl" value="NGROK_OR_FUNCTION_ENDPOINT /api/AudioTranscribe?guid=" />`.

Replace NGROK_OR_FUNCTION_ENDPOINT with the value you paste, and remove any spaces between the pasted value and `/api/AudioTranscribe?guid=`.

For example, the final value should look like this:

```xml
<add key="ida:NotificationUrl" value="https://ac47ffcd.ngrok.io/api/AudioTranscribe?guid=" />
```

If you've followed along, you now have completed all the prerequisite steps required to run this sample.

### Run the project and sign-in

Now that everything is properly configured, open the web project in Visual Studio and press F5 launch the project in the debugger.

1. Sign in to the data robot project and authorize the application to have access to data in your organization.
2. Select a document library in a SharePoint site or Office 365 group. If you select a OneDrive, the sample will return an error message that indicates OneDrive isn't supported.
3. After you authorize the data robot, you should see a Subscription ID and Expiration date/time.
   These values are returned from the Microsoft Graph webhook notification subscription that powers the data robot.
   By default the expiration time is 3 days from when the robot is activated.

If no value is returned, check to ensure that the notification URL is correct in the `Web.config` file and in the `AudioTranscribe.cs` file.

### Navigate to the document library and try out the data robot

1. Record an audio file using your PC or Phone. The file needs to be in the .WAV format.
2. Name the file `Audio.en-us.wav` where Audio is the name of your file, and `en-us` is the locale code for the language you were speaking.
3. Drag the .WAV file into the document library.
4. Watch the webhook trigger your Azure Function in the simulator, the app transfer the file into cognitive services, and then push the transcription back into the document library.

## Related references

For more information about Microsoft Graph API, see [Microsoft Graph](https://graph.microsoft.com).

## License

See [License](LICENSE.txt) for the license agreement convering this sample code.
