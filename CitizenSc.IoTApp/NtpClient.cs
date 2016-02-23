using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using Microsoft.SPOT;
using System.Threading;

namespace MFToolkit.Net.Ntp
{
   /// <summary>
   /// Static class to receive the time from a NTP server.
   /// </summary>
   public class NtpClient
   {
      /// <summary>
      /// Gets the current DateTime from time-a.nist.gov.
      /// </summary>
      /// <returns>A DateTime containing the current time.</returns>
      public static DateTime GetNetworkTime()
      {
         return GetNetworkTime("utcnist2.colorado.edu");
      }

      /// <summary>
      /// Gets the current DateTime from <paramref name="ntpServer"/>.
      /// </summary>
      /// <param name="ntpServer">The hostname of the NTP server.</param>
      /// <returns>A DateTime containing the current time.</returns>
      public static DateTime GetNetworkTime(string ntpServer)
      {
         IPAddress[] address = Dns.GetHostEntry(ntpServer).AddressList;

         if (address == null || address.Length == 0)
            throw new ArgumentException("Could not resolve ip address from '" + ntpServer + "'.", "ntpServer");

         IPEndPoint ep = new IPEndPoint(address[0], 123);

         return GetNetworkTime(ep);
      }

      /// <summary>
      /// Gets the current DateTime form <paramref name="ep"/> IPEndPoint.
      /// </summary>
      /// <param name="ep">The IPEndPoint to connect to.</param>
      /// <returns>A DateTime containing the current time.</returns>
      public static DateTime GetNetworkTime(IPEndPoint ep)
      {
         byte[] ntpData = new byte[48]; // RFC 2030
         ntpData[0] = 0x1B;
         for (int i = 1; i < 48; i++)
            ntpData[i] = 0;

         Socket s = null;

         bool success = false;
         int attempts = 0;

         while (!success && attempts < 5)
         {
            attempts++;
            try
            {
               s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
               s.Connect(ep);
               s.SendTimeout = 5000;
               s.ReceiveTimeout = 5000;
               s.Send(ntpData);
               s.Receive(ntpData);
               success = true;
            }
            catch (Exception ex)
            {
               Debug.Print(ex.Message);
               Thread.Sleep(3000);
            }
            finally
            {
               if (s != null) s.Close();
               s = null;
            }
         }


         byte offsetTransmitTime = 40;
         ulong intpart = 0;
         ulong fractpart = 0;

         for (int i = 0; i <= 3; i++)
            intpart = 256 * intpart + ntpData[offsetTransmitTime + i];

         for (int i = 4; i <= 7; i++)
            fractpart = 256 * fractpart + ntpData[offsetTransmitTime + i];

         ulong milliseconds = (intpart * 1000 + (fractpart * 1000) / 0x100000000L);

         TimeSpan timeSpan = TimeSpan.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);

         DateTime dateTime = new DateTime(1900, 1, 1);
         dateTime += timeSpan;

         TimeSpan offsetAmount = TimeZone.CurrentTimeZone.GetUtcOffset(dateTime);
         DateTime networkDateTime = (dateTime + offsetAmount);

         return networkDateTime;
      }
   }
}
