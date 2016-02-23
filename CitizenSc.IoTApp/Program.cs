using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using System.IO;
using System.Diagnostics;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using Microsoft.SPOT.Net.NetworkInformation;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using Amqp;
using Amqp.Framing;
using Json.NETMF;

namespace CitizenSc.IoTApp
{
   public class Program
   {
      #region Fields
      static OutputPort _onboardLed = new OutputPort(Pins.ONBOARD_LED, false);
      static InterruptPort _onboardButton = new InterruptPort(Pins.ONBOARD_BTN, true, Port.ResistorMode.PullUp, Port.InterruptMode.InterruptEdgeHigh);

      //private const string HOST = "RBAIceHouseIV.azure-devices.net";
      //private const int PORT = 5671;
      //private const string DEVICE_ID = "RBAIceHouse";
      //private const string DEVICE_KEY = "3DYtpTGkB+jIt5Yz35E/eUf/TGl7+LjHSEl+4nafx4U=";
      private const string HOST = "SitecoreIoTHub.azure-devices.net";
      private const int PORT = 5671;
      private const string DEVICE_ID = "mySitecoreIoTDevice";
      private const string DEVICE_KEY = "M8J10aRSazr71ol1J+hhqBFTYnCiupozphUrOB5mBys=";
      private static Address address;
      private static Connection connection;
      private static Session session;
      private static Thread receiverThread;
      private static AutoResetEvent networkAvailableEvent = new AutoResetEvent(false);
      private static AutoResetEvent networkAddressChangedEvent = new AutoResetEvent(false);

      private static int _yardsPerMinute = 176; //176 yards per minute = 6 miles per hour
      private static bool _isDeviceOn = false;
      private static DateTime _lastStartTime = DateTime.MinValue;
      private static DateTime _lastStopTime = DateTime.MinValue;
      #endregion

      public static void Main()
      {
         try
         {
            Microsoft.SPOT.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
            Microsoft.SPOT.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChange_NetworkAddressChanged;

            networkAvailableEvent.WaitOne();
            Debug.Print("link is up!");
            networkAddressChangedEvent.WaitOne();
            Thread.Sleep(3000);

            DateTime dateTime = MFToolkit.Net.Ntp.NtpClient.GetNetworkTime();
            Utility.SetLocalTime(dateTime);

            address = new Address(HOST, PORT, null, null);
            connection = new Connection(address);
            string audience = Fx.Format("/devices/{0}/events", DEVICE_ID);
            string resourceUri = Fx.Format("{0}/devices/{1}", HOST, DEVICE_ID);
            string sasToken = GetSharedAccessSignature(null, DEVICE_KEY, resourceUri, new TimeSpan(1, 0, 0));
            bool cbs = PutCbsToken(connection, HOST, sasToken, audience);

            if (cbs)
            {
               session = new Session(connection);
               Blink(3);
               _onboardButton.OnInterrupt += new NativeEventHandler(button_OnInterrupt);

               while (true) { }
            }
         }
         catch (Exception ex) { Debug.Print(ex.Message); }
         finally
         {
            session.Close();
            connection.Close();
         }
      }

      static void button_OnInterrupt(uint data1, uint data2, DateTime time)
      {
         _isDeviceOn = !_isDeviceOn;
         _onboardLed.Write(_isDeviceOn);
         if (_isDeviceOn)
         {
            Debug.Print("Device started");
            _lastStartTime = DateTime.Now;
         }
         else
         {
            Debug.Print("Device stopped");
            _lastStopTime = DateTime.Now;

            var report = new RunReport();
            if (_lastStartTime < _lastStopTime)
               report.RunTime = _lastStopTime - _lastStartTime;
            report.Distance = report.RunTime.Seconds * _yardsPerMinute;

            Debug.Print("Device ran for " + report.RunTime.Seconds + " minutes");
            Debug.Print("Device traveled " + report.Distance + " yards");

            SendRunReport(report);
         }
      }

      static void NetworkChange_NetworkAddressChanged(object sender, EventArgs e)
      {
         Debug.Print("NetworkAddressChanged");
         networkAddressChangedEvent.Set();
      }

      static void NetworkChange_NetworkAvailabilityChanged(object sender, Microsoft.SPOT.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
      {
         Debug.Print("NetworkAvailabilityChanged " + e.IsAvailable);
         if (e.IsAvailable)
         {
            networkAvailableEvent.Set();
         }
      }


      static private bool PutCbsToken(Connection connection, string host, string shareAccessSignature, string audience)
      {
         bool result = true;
         Session session = new Session(connection);

         string cbsReplyToAddress = "cbs-reply-to";
         var cbsSender = new SenderLink(session, "cbs-sender", "$cbs");
         var cbsReceiver = new ReceiverLink(session, cbsReplyToAddress, "$cbs");

         // construct the put-token message
         var request = new Message(shareAccessSignature);
         request.Properties = new Properties();
         request.Properties.MessageId = Guid.NewGuid().ToString();
         request.Properties.ReplyTo = cbsReplyToAddress;
         request.ApplicationProperties = new ApplicationProperties();
         request.ApplicationProperties["operation"] = "put-token";
         request.ApplicationProperties["type"] = "azure-devices.net:sastoken";
         request.ApplicationProperties["name"] = audience;
         cbsSender.Send(request);

         // receive the response
         var response = cbsReceiver.Receive();
         if (response == null || response.Properties == null || response.ApplicationProperties == null)
         {
            result = false;
         }
         else
         {
            int statusCode = (int)response.ApplicationProperties["status-code"];
            string statusCodeDescription = (string)response.ApplicationProperties["status-description"];
            if (statusCode != (int)202 && statusCode != (int)200) // !Accepted && !OK
            {
               result = false;
            }
         }

         // the sender/receiver may be kept open for refreshing tokens
         cbsSender.Close();
         cbsReceiver.Close();
         session.Close();

         return result;
      }

      private static readonly long UtcReference = (new DateTime(1970, 1, 1, 0, 0, 0, 0)).Ticks;

      static string GetSharedAccessSignature(string keyName, string sharedAccessKey, string resource, TimeSpan tokenTimeToLive)
      {
#if NETMF
            // needed in .Net Micro Framework to use standard RFC4648 Base64 encoding alphabet
            System.Convert.UseRFC4648Encoding = true;
#endif
         string expiry = ((long)(DateTime.UtcNow - new DateTime(UtcReference, DateTimeKind.Utc) + tokenTimeToLive).TotalSeconds()).ToString();
         string encodedUri = HttpUtility.UrlEncode(resource);

         byte[] hmac = SHA.computeHMAC_SHA256(Convert.FromBase64String(sharedAccessKey), Encoding.UTF8.GetBytes(encodedUri + "\n" + expiry));
         string sig = Convert.ToBase64String(hmac);

         if (keyName != null)
         {
            return Fx.Format(
            "SharedAccessSignature sr={0}&sig={1}&se={2}&skn={3}",
            encodedUri,
            HttpUtility.UrlEncode(sig),
            HttpUtility.UrlEncode(expiry),
            HttpUtility.UrlEncode(keyName));
         }
         else
         {
            return Fx.Format(
                "SharedAccessSignature sr={0}&sig={1}&se={2}",
                encodedUri,
                HttpUtility.UrlEncode(sig),
                HttpUtility.UrlEncode(expiry));
         }
      }

      static void SendRunReport(RunReport report)
      {
         string entity = Fx.Format("/devices/{0}/messages/events", DEVICE_ID);
         SenderLink senderLink = new SenderLink(session, "sender-link", entity);

         var messageValueJson = JsonSerializer.SerializeObject(report);

         var messageValue = Encoding.UTF8.GetBytes(messageValueJson);
         Message message = new Message()
         {
            BodySection = new Data() { Binary = messageValue }
         };

         try
         {
            Debug.Print("Begin Message Send");
            senderLink.Send(message);
            Debug.Print("End Message Send");
            Blink(2);
         }
         catch (TimeoutException ex)
         {
            Debug.Print("Message Send Timed Out: " + ex.Message + "\n" + ex.StackTrace);
            Blink(6);
         }
         finally
         {
            senderLink.Close();
         }
      }


      static void ConfigureNetwork()
      {
         NetworkInterface networkInterface = NetworkInterface.GetAllNetworkInterfaces()[0];

         if (networkInterface.IsDhcpEnabled)
         {
            Debug.Print("Waiting for IP address ");
            while (NetworkInterface.GetAllNetworkInterfaces()[0].IPAddress == IPAddress.Any.ToString())
            {
               Thread.Sleep(1000);
            };
         }

         // Display network config for debugging
         Debug.Print("Network configuration");
         Debug.Print(" Network interface type: " + networkInterface.NetworkInterfaceType.ToString());
         Debug.Print(" DHCP enabled: " + networkInterface.IsDhcpEnabled.ToString());
         Debug.Print(" Dynamic DNS enabled: " + networkInterface.IsDynamicDnsEnabled.ToString());
         Debug.Print(" IP Address: " + networkInterface.IPAddress.ToString());
         Debug.Print(" Subnet Mask: " + networkInterface.SubnetMask.ToString());
         Debug.Print(" Gateway: " + networkInterface.GatewayAddress.ToString());

         foreach (string dnsAddress in networkInterface.DnsAddresses)
         {
            Debug.Print(" DNS Server: " + dnsAddress.ToString());
         }

         networkInterface = null;
      }

      static void Blink(int blinks) { Blink(blinks, _onboardLed); }

      static void Blink(int blinks, OutputPort led)
      {
         for (int i = 0; i < blinks; i++)
         {
            led.Write(true);
            Thread.Sleep(100);
            led.Write(false);
            Thread.Sleep(100);
         }
      }
   }
}
