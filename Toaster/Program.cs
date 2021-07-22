using System;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using AzureMapsToolkit;
using AzureMapsToolkit.Common;

namespace Toaster
{
    class Program
    {
        //Send telemntry if its connected, disconnected or in standby
        enum ConnectionStatus
        {
            Connected,
            Disconnected,
            Standby
        };

        // Azure Maps service globals.
        static AzureMapsServices azureMapsServices;

        // Telemetry globals.
        const int intervalInMilliseconds = 900000;        // Time interval required by wait function.

        // Refrigerated truck globals.
        static string identification = "breadToaster";
        static double lat = 47.6437253;              // Base position latitude.
        static double lon = -122.1224279;            // Base position longitude.
       // const string noEvent = "none";
        //static string eventText = noEvent;              // Event text sent to IoT Central.

        static Random rand;

        // IoT Central global variables.
        static DeviceClient s_deviceClient;
        static CancellationTokenSource cts;
        static string GlobalDeviceEndpoint = "global.azure-devices-provisioning.net";
        static TwinCollection reportedProperties = new TwinCollection();

        // User IDs.
        static string IDScope = "Your ID scope";
        static string DeviceID = "Your deviceID";
        static string PrimaryKey = "Your primary key";
        static string AzureMapsKey = "Your Azure Maps key";

        //consumption cycle over time to simulate consumption reading
        static int[] consumptionOverTime = new int[] {700, 1000, 1000, 1000, 1000, 1000, 0, 0};

        //Temperature over time depending on its consumption
        static int[] applianceTemperatureOverTime = new int[] {70, 150, 300, 325, 325, 325, 200, 100};

        //Simulate the voltage the appliance is reading
        static double[] voltageOverTime = new double[] {230, 225.7, 229.6, 228.3, 233.5, 231, 226.8, 229.4, 228.7, 225.9, 231, 232, 223.6, 232};

        //start voltage reading variable from 0 
        static double currentVoltage = 0;

        //cycle through voltage reading and report minimum and maximum voltage
        static double[] returnMinMax(double[] voltageTimeLapse)
        {
            double max = voltageTimeLapse[0];
            double min = voltageTimeLapse[0];

            for(int i = 0; i < voltageTimeLapse.Length - 1; i++)
            {
                if(voltageTimeLapse[i] > max)
                {
                    max = voltageTimeLapse[i];
                }

                if(voltageTimeLapse[i] < min && voltageTimeLapse[i] != 0)
                {
                    min = voltageTimeLapse[i];
                }
            }
            double[] minMax = new double[] {min, max};
            return minMax;
        }

        //power cut command
        static Task<MethodResponse> PowerOff(MethodRequest methodRequest, object userContext)
        {
            
            // Acknowledge the command.
            if (currentVoltage != 0)
            {
                currentVoltage = 0;
                // Acknowledge the direct method call with a 200 success message.
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            else
            {
                // Acknowledge the direct method call with a 400 error message.
                string result = "{\"result\":\"Invalid call\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }

        //turn power on command
        static Task<MethodResponse> PowerOn(MethodRequest methodRequest, object userContext)
        {
            
            // Acknowledge the command.
            if (currentVoltage == 0)
            {
                int index = rand.Next(voltageOverTime.Length);
                currentVoltage = voltageOverTime[index];
                // Acknowledge the direct method call with a 200 success message.
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            else
            {
                // Acknowledge the direct method call with a 400 error message.
                string result = "{\"result\":\"Invalid call\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }

        static void colorMessage(string text, ConsoleColor clr)
        {
            Console.ForegroundColor = clr;
            Console.WriteLine(text + "\n");
            Console.ResetColor();
        }
        static void greenMessage(string text)
        {
            colorMessage(text, ConsoleColor.Green);
        }

        static void redMessage(string text)
        {
            colorMessage(text, ConsoleColor.Red);
        }

        static async void SendToasterTelemetryAsync(Random rand, CancellationToken token)
        {
            double[] minmax = returnMinMax(voltageOverTime);

            int i = 0;
            int j = 0;
            int EnergyConsumption = 0;
            while (true)
            {
                //keep repeating cycles of temperature and consumption
                if(i > consumptionOverTime.Length - 1 )
                {
                    i = 0;
                }

                if (j > applianceTemperatureOverTime.Length - 1)
                {
                    j = 0;
                }

                //calculate energy consumption total
                EnergyConsumption = consumptionOverTime[i] + EnergyConsumption;

                // Create the telemetry JSON message.
                var telemetryDataPoint = new
                {
                    MinVoltage = minmax[0],
                    MaxVoltage = minmax[1],
                    EnergyReading = consumptionOverTime[i],
                    EnergyConsumption,
                    ApplianceTemperature = applianceTemperatureOverTime[j],
                    ConnectionStatus = ConnectionStatus.Connected.ToString(),
                    Location = new { lon, lat } 
                };

                i++;
                j++;

                var telemetryMessageString = JsonSerializer.Serialize(telemetryDataPoint);
                var telemetryMessage = new Message(Encoding.ASCII.GetBytes(telemetryMessageString));

                Console.WriteLine($"Telemetry data: {telemetryMessageString}");

                // Bail if requested.
                token.ThrowIfCancellationRequested();

                // Send the telemetry message.
                await s_deviceClient.SendEventAsync(telemetryMessage);
                greenMessage($"Telemetry sent {DateTime.Now.ToShortTimeString()}");

                await Task.Delay(intervalInMilliseconds);
            }
        }

        static async Task SendDevicePropertiesAsync()
        {
            reportedProperties["Appliance"] = identification;
            await s_deviceClient.UpdateReportedPropertiesAsync(reportedProperties);
            greenMessage($"Sent device properties: {reportedProperties["Appliance"]}");
        }

        static void Main(string[] args)
        {

            rand = new Random();
            colorMessage($"Starting {identification}", ConsoleColor.Yellow);

            // Connect to Azure Maps.
            azureMapsServices = new AzureMapsServices(AzureMapsKey);

            try
            {
                using (var security = new SecurityProviderSymmetricKey(DeviceID, PrimaryKey, null))
                {
                    DeviceRegistrationResult result = RegisterDeviceAsync(security).GetAwaiter().GetResult();
                    if (result.Status != ProvisioningRegistrationStatusType.Assigned)
                    {
                        Console.WriteLine("Failed to register device");
                        return;
                    }
                    IAuthenticationMethod auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, (security as SecurityProviderSymmetricKey).GetPrimaryKey());
                    s_deviceClient = DeviceClient.Create(result.AssignedHub, auth, TransportType.Mqtt);
                }
                greenMessage("Device successfully connected to Azure IoT Central");

                SendDevicePropertiesAsync().GetAwaiter().GetResult();

                cts = new CancellationTokenSource();

                // Create a handler for the direct method calls.
                s_deviceClient.SetMethodHandlerAsync("PowerOff", PowerOff, null).Wait();
                s_deviceClient.SetMethodHandlerAsync("PowerOn", PowerOn, null).Wait();

                SendToasterTelemetryAsync(rand, cts.Token);

                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine(ex.Message);
            }
        }


        public static async Task<DeviceRegistrationResult> RegisterDeviceAsync(SecurityProviderSymmetricKey security)
        {
            Console.WriteLine("Register device...");

            using (var transport = new ProvisioningTransportHandlerMqtt(TransportFallbackType.TcpOnly))
            {
                ProvisioningDeviceClient provClient =
                          ProvisioningDeviceClient.Create(GlobalDeviceEndpoint, IDScope, security, transport);

                Console.WriteLine($"RegistrationID = {security.GetRegistrationID()}");

                Console.Write("ProvisioningClient RegisterAsync...");
                DeviceRegistrationResult result = await provClient.RegisterAsync();

                Console.WriteLine($"{result.Status}");

                return result;
            }
        }
    }
}
