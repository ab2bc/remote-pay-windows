﻿// Copyright (C) 2016 Clover Network, Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
//
// You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using LibUsbDotNet.WinUsb;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Timers;

namespace com.clover.remotepay.transport
{
    public class USBCloverTransport : CloverTransport
    {
        private UsbDevice MyUsbDevice;
        private System.Timers.Timer _timer = new System.Timers.Timer();
        public static Dictionary<string, List<UsbDeviceFinder>> VendorToFinder = new Dictionary<string, List<UsbDeviceFinder>>();
        public static List<UsbDeviceFinder> MerchantUsbFinders = new List<UsbDeviceFinder>();
        public static List<UsbDeviceFinder> CustomerUsbFinders = new List<UsbDeviceFinder>();
        private static readonly uint REMOTE_STRING_MAGIC_START_TOKEN = 0xcc771122;
        private static readonly int REMOTE_STRING_HEADER_BYTE_COUNT = 4 + 4; // 2 ints
        // Defined by AOA
        private static readonly int MAX_PACKET_BYTES = 16384;
        // A short
        private static readonly short PACKET_HEADER_SIZE = 2;
        private static readonly int REMOTE_STRING_LENGTH_MAX = 4 * 1024 * 1024;

        private UsbEndpointReader reader;
        private UsbEndpointWriter writer;
        private static int EMPTY_STRING = 1;
        private bool shutdown = false;
        private readonly int ERROR_LIMIT = 3;
        private BackgroundWorker receiveMessagesThread;
        private BackgroundWorker sendMessagesThread;
        private DoWorkEventHandler receiveMessagesDoWorkHandler;
        private DoWorkEventHandler sendMessagesDoWorkHandler;

        private readonly object DeviceAccessorySyncLock = new object();
        private readonly object DeviceInitSyncLock = new object();

        BlockingQueue<string> messageQueue = new BlockingQueue<string>();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="merchantDevices"></param>
        /// <param name="customerDevices"></param>
        public USBCloverTransport(List<USBDevice> merchantDevices, List<USBDevice> customerDevices)
        {
            foreach (USBDevice device in merchantDevices)
            {
                AddMerchantDevice(device.VID, device.PID);
            }
            foreach (USBDevice device in customerDevices)
            {
                AddCustomerDevice(device.VID, device.PID);
            }
            init();
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="deviceId">The device id (like the one obtained from 'adb devices')</param>
        public USBCloverTransport(string deviceId)
        {
            loadDevicesFromConfig();
            init();
        }

        public USBCloverTransport(string deviceId, bool enableLogging, int pingSleepSeconds)
        {
            loadDevicesFromConfig();
            if (enableLogging)
            {
                EnableLogging();
            }
            if (pingSleepSeconds > 0)
            {
                EnablePinging(pingSleepSeconds);
            }
            init();
        }

        private void init()
        {
            //start listening for connection events
            listenForUSB();
            initializeBGWDoWorkHandlers();
            ConnectDevice();
            // Create a timer that will check the connection to 
            // ensure that it is still up and healthy.  There
            // are circumstances where Windows might put the 
            // USB port to sleep and not wake it up, so this code
            // proactively checks the connection and re-establishes
            // it if necessary.
            if (getPingSleepSeconds() > 0)
            {
                _timer.AutoReset = false;
                _timer.Interval = getPingSleepSeconds() * 1000;
                _timer.Elapsed += new ElapsedEventHandler(OnTimerEvent);
                _timer.Start();
            }
        }

        private void ConnectDevice()
        {
            // attempt to open the device as if it is already in customer mode
            DeviceSetToAccessoryMode();
            // if opening in cust mode fails, then try merchant mode, 
            // which will retrigger the accessory mode call
            if (MyUsbDevice == null)  
            {
                DeviceInitiallyConnected();
            }
        }

        private void AddMerchantDevice(int vid, int pid)
        {
            UsbDeviceFinder deviceFinder = new UsbDeviceFinder(vid, pid);
            MerchantUsbFinders.Add(deviceFinder);

            string vidString = String.Format("{0:x}", vid).ToUpper();
            List<UsbDeviceFinder> finders = null;
            if (VendorToFinder.TryGetValue(vidString, out finders))
            {
                finders.Add(deviceFinder);
            }
            else
            {
                finders = new List<UsbDeviceFinder>();
                finders.Add(deviceFinder);
                VendorToFinder.Add(vidString, finders);
            }
        }
        private void AddCustomerDevice(int vid, int pid)
        {
            UsbDeviceFinder deviceFinder = new UsbDeviceFinder(vid, pid);
            CustomerUsbFinders.Add(deviceFinder);

            string vidString = String.Format("{0:x}", vid).ToUpper();
            List<UsbDeviceFinder> finders = null;
            if (VendorToFinder.TryGetValue(vidString, out finders))
            {
                finders.Add(deviceFinder);
            }
            else
            {
                finders = new List<UsbDeviceFinder>();
                finders.Add(deviceFinder);
                VendorToFinder.Add(vidString, finders);
            }
        }
        private void loadDevicesFromConfig()
        {
            string merchantDevices = System.Configuration.ConfigurationSettings.AppSettings["merchant_devices"];

            if (merchantDevices != null)
            {
                MerchantUsbFinders.Clear();
                foreach (string mpid in merchantDevices.Split(','))
                {
                    string[] dev = mpid.Trim().Split(':');
                    if (dev.Length != 2)
                    {
                        throw new Exception("Invalid device specified. It should be VID1:PID1,VID2:PID2 e.g. 0x28F3:0x3003");
                    }
                    int vid = (int)new System.ComponentModel.Int32Converter().ConvertFromString(dev[0].Trim());
                    int pid = (int)new System.ComponentModel.Int32Converter().ConvertFromString(dev[1].Trim());
                    AddMerchantDevice(vid, pid);
                }
            }
            else
            {
                AddMerchantDevice(0x28F3, 0x3003);
                AddMerchantDevice(0x28F3, 0x3000);
                AddMerchantDevice(0x28F3, 0x2000);
            }

            string customerDevices = System.Configuration.ConfigurationSettings.AppSettings["customer_devices"];
            if (customerDevices != null)
            {
                CustomerUsbFinders.Clear();
                foreach (string mpid in customerDevices.Split(','))
                {
                    string[] dev = mpid.Trim().Split(':');
                    if (dev.Length != 2)
                    {
                        throw new Exception("Invalid device specified. It should be VID1:PID1,VID2:PID2");
                    }
                    int vid = (int)new System.ComponentModel.Int32Converter().ConvertFromString(dev[0].Trim());
                    int pid = (int)new System.ComponentModel.Int32Converter().ConvertFromString(dev[1].Trim());
                    AddCustomerDevice(vid, pid);
                }
            }
            else
            {
                AddCustomerDevice(0x28F3, 0x3002);
                AddCustomerDevice(0x28F3, 0x3004);
                AddCustomerDevice(0x18D1, 0x2D01);
            }
        }

        protected static int getMaxDataTransferSize()
        {
            return MAX_PACKET_BYTES - PACKET_HEADER_SIZE;
        }

        // return the GUID of the interface for MI_00
        public bool IsUsbDeviceConnected(string pid, string vid, string mi)
        {
            using (var searcher =
              new ManagementObjectSearcher(@"Select * From Win32_USBControllerDevice"))
            {
                using (var collection = searcher.Get())
                {
                    foreach (var device in collection)
                    {
                        var usbDevice = Convert.ToString(device);

                        if (usbDevice.Contains(pid) && usbDevice.Contains(vid))
                        {
                            if (mi == null)
                            {
                                return true;
                            }
                            else if (usbDevice.Contains(mi))
                            {
                                return true;
                            }
                        }

                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Called when the device is INITIALLY connected.  This sets the mini device to accessory mode,
        /// at which point the mini device disconnects.
        /// </summary>
        private Boolean DeviceInitiallyConnected()
        {
            lock(DeviceInitSyncLock)
            {
                TransportLog("Entering DeviceInitiallyConnected: " + Thread.CurrentThread.GetHashCode());
                Boolean initialized = false;

                if (MyUsbDevice == null || !MyUsbDevice.IsOpen)
                {

                    UsbDevice TempMyUsbDevice = null;
                    try
                    {
                        // Find and open the usb device.
                        foreach (UsbDeviceFinder merchUsbFinder in MerchantUsbFinders)
                        {

                            TempMyUsbDevice = UsbDevice.OpenUsbDevice(merchUsbFinder);

                            if (TempMyUsbDevice != null) // if it matches, go on...
                            {
                                break;
                            }
                        }

                        // If the device is open and ready
                        if (TempMyUsbDevice != null && TempMyUsbDevice.IsOpen)
                        {
                            initialized = MiniInitializer.initializeDeviceConnectionAccessoryMode(TempMyUsbDevice);
                        }
                        else
                        {
                            TransportLog("Error finding device in merchant mode");
                        }
                    }
                    catch (Exception ex)
                    {
                        TransportLog(ex.Message);
                    }
                    TransportLog("Exiting DeviceInitiallyConnected initialized=" + initialized);
                }
                else
                {
                    initialized = true;
                }

                if (initialized)
                {
                    onDeviceConnected();
                }
                return initialized;
            }
        }

        private void OnTimerEvent(object sender, ElapsedEventArgs e)
        {
            // The ConnectDevice() call will ensure the device reference is still valid
            // and the connection is healthy.  If not, it will attempt to re-establish
            // the device and connection. 
            ConnectDevice();
            _timer.Start();
        }


        /// <summary>
        /// Opens the device in accessory mode.  Sets up the read and write streams.
        /// </summary>
        /// 
        private Boolean DeviceSetToAccessoryMode()
        {
            lock(DeviceAccessorySyncLock)
            {
                Boolean initialized = false;
                if (MyUsbDevice == null || !MyUsbDevice.IsOpen)
                {
                    if (MyUsbDevice == null)
                    {
                        TransportLog("DeviceSetToAccessoryMode(): MyUsbDevice is null.  Attempting to recreate/reopen using WinUSB lib");
                    }
                    else
                    {
                        TransportLog("DeviceSetToAccessoryMode(): MyUsbDevice.IsOpen = false.  Attempting to reopen using WinUSB lib.");
                    }
                    try
                    {
                        for (int i = 0; i < UsbDevice.AllWinUsbDevices.Count && MyUsbDevice == null; i++)
                        {
                            WinUsbRegistry usbDevice = (WinUsbRegistry)UsbDevice.AllWinUsbDevices[i];
                            foreach (UsbDeviceFinder finder in CustomerUsbFinders)
                            {
                                if (usbDevice.Vid == finder.Vid && usbDevice.Pid == finder.Pid && usbDevice.InterfaceID == 0)
                                {
                                    if (usbDevice.Open(out MyUsbDevice))
                                    {
                                    TransportLog("WinUSB Device Found and Open.");
                                        break;
                                    }
                                    else
                                    {
                                        // found the device, but can't open it...Someone else may have this open.
                                        throw new Exception("Found the device, but can't open it. Some other process(service or application) may already have it open.");
                                    }

                                }
                            }
                        }
                        if (MyUsbDevice == null) // try libusb, but current WinUsb is the driver of choice
                        {
                            TransportLog("DeviceSetToAccessoryMode(): MyUsbDevice is null.  Attempting to recreate/reopen using LibUSB driver");
                            for (int i = 0; i < UsbDevice.AllLibUsbDevices.Count; i++)
                            {
                                LibUsbRegistry usbDevice = (LibUsbRegistry)UsbDevice.AllLibUsbDevices[i];
                                foreach (UsbDeviceFinder finder in CustomerUsbFinders)
                                {
                                    if (usbDevice.Vid == finder.Vid && usbDevice.Pid == finder.Pid)
                                    {
                                        if (usbDevice.Open(out MyUsbDevice))
                                        {
                                        TransportLog("LibUSB Device Found and Open.");
                                            break;
                                        }
                                        else
                                        {
                                            // found the device, but can't open it...Someone else may have this open.
                                            throw new Exception("LibUSB: Found the device, but can't open it. Some other process(service or application) may already have it open.");
                                        }
                                    }
                                }
                            }
                        }
                        if (MyUsbDevice == null)
                        {
                            throw new Exception("Obtaining handle to the usb device failed.");
                        }

                        // If this is a "whole" usb device (libusb-win32, linux libusb)
                        // it will have an IUsbDevice interface. If not (WinUSB) the 
                        // variable will be null indicating this is an interface of a 
                        // device.

                        IUsbDevice wholeUsbDevice = MyUsbDevice as IUsbDevice;
                        if (!ReferenceEquals(wholeUsbDevice, null))
                        {
                            // This is a "whole" USB device. Before it can be used, 
                            // the desired configuration and interface must be selected.

                            // Select config #1
                            wholeUsbDevice.SetConfiguration(1);

                            // Claim interface #0.
                            wholeUsbDevice.ClaimInterface(0);
                        }

                        // open read endpoint 1.
                        reader = MyUsbDevice.OpenEndpointReader(ReadEndpointID.Ep01);
                        if (null != reader)
                        {
                            // open write endpoint 1.
                            writer = MyUsbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
                            if (null != writer)
                            {
                                shutdown = false;
                                startListeningForMessages();
                                messageSendLoop();
                                initialized = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        TransportLog(Environment.NewLine);
                        TransportLog(ex.Message);
                    }
                    TransportLog("Exiting DeviceSetToAccessoryMode");
                    if (initialized)
                    {
                        TransportLog("DeviceSetToAccessoryMode(): MyUsbDevice is ready");
                        onDeviceReady();
                    }
                }
                else
                {
                    initialized = true;
                }
                return initialized;
            }
        }

        public override int sendMessage(string message)
        {
            if (isConnected())
            {
                TransportLog("In sendMessage() just before the Enqueue: messageQueue.Count = " + messageQueue.Count.ToString());
                TransportLog("In sendMessage() just before the Enqueue: message = " + message);
                messageQueue.Enqueue(message);
                TransportLog("In sendMessage() just after the Enqueue: messageQueue.Count = " + messageQueue.Count.ToString());
                return 1;
            }
            return 0;
        }


        private void messageSendLoop()
        {
            sendMessagesThread = new BackgroundWorker();
            // what to do in the background thread
            sendMessagesThread.DoWork += sendMessagesDoWorkHandler;
            sendMessagesThread.RunWorkerAsync();
        }

        private void initializeBGWDoWorkHandlers()
        {
            sendMessagesDoWorkHandler = new DoWorkEventHandler(
            delegate (object o, DoWorkEventArgs args)
            {
                TransportLog("Starting send message loop in BGW.DoWork().");
                TransportLog(Thread.CurrentThread.ManagedThreadId + " : Starting sendMessagesThread");

                do
                {
                    try
                    {
                        while (messageQueue.Count > 0)
                        {
                            TransportLog("In sendMessagesDoWorkHandler() just before the Dequeue: messageQueue.Count = " + messageQueue.Count.ToString());
                            String message = messageQueue.Dequeue();
                            TransportLog("In sendMessagesDoWorkHandler() just after the Dequeue: messageQueue.Count = " + messageQueue.Count.ToString());
                            if (message != null)
                            {
                                sendMessageSync(message);
                            } else
                            {
                                TransportLog("Dequeued a null message");  // this should never happen, but just in case, let's log it
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        TransportLog("Error occurred in sendMessageSync(): " + e.Message);
                        TransportLog(e.StackTrace);
                    }
                    lock (messageQueue)
                    {
#if DEBUG
                        GC.Collect(); // use to test for memory leaks
#endif
                        Monitor.Wait(messageQueue, 1000); // wake up every second and check if it is shutdown...
                    }
                } while (!shutdown);
                TransportLog(Thread.CurrentThread.ManagedThreadId + " : Terminating sendMessagesThread");
            });

            receiveMessagesDoWorkHandler = new DoWorkEventHandler(
            delegate (object o, DoWorkEventArgs args)
            {
                TransportLog(Thread.CurrentThread.ManagedThreadId + " : Starting receiveMessagesThread");
                try
                {
                    getMessages();
                }
                catch (Exception e)
                {
                    TransportLog("receiveMessagesThread Exception: " + e.Message);
                }
                TransportLog(Thread.CurrentThread.ManagedThreadId + " : Terminating receiveMessagesThread");
            });
        }

        public int sendMessageSync(string message)
        {
            BinaryWriter mOutPacketBuffer = new BinaryWriter(new MemoryStream(getMaxDataTransferSize()));

            TransportLog("Entering sendMessageSync() with message content = " + message);
            TransportLog(message);
            int errorcode = 0;
            if (!String.IsNullOrEmpty(message))
            {
                byte[] stringBytes = Encoding.UTF8.GetBytes(message);

                mOutPacketBuffer.Seek(0, SeekOrigin.Begin);
                mOutPacketBuffer.Write(IPAddress.HostToNetworkOrder((int)REMOTE_STRING_MAGIC_START_TOKEN));
                mOutPacketBuffer.Write(IPAddress.HostToNetworkOrder(stringBytes.Length));

                int stringByteLength = stringBytes.Length;
                if (stringByteLength <= 0 || stringByteLength > REMOTE_STRING_LENGTH_MAX)
                {
                    throw new Exception("String byte length " + stringByteLength + " bytes exceeds maximum " + REMOTE_STRING_LENGTH_MAX + " bytes");
                }

                TransportLog("Writing a " + stringByteLength + " byte message");
                int remainingBytes = stringByteLength;

                do
                {
                    // Figure out the size that we can send.
                    int sizeOfBuffer = ((MemoryStream)mOutPacketBuffer.BaseStream).Capacity;
                    int remainingSpace = (int)(sizeOfBuffer - mOutPacketBuffer.BaseStream.Position);
                    // It is the lesser of either the remaining size of the message,
                    // or the remaining size of the buffer.
                    int packetLength = Math.Min(remainingBytes, remainingSpace);
                    // Write the bytes into the buffer
                    mOutPacketBuffer.Write(stringBytes, stringByteLength - remainingBytes, packetLength);
                    // current position is set to the maximum that can be written.
                    // Current position is reset zero
                    writePacket(mOutPacketBuffer);
                    TransportLog("Just wrote to the output buffer for the next " + packetLength + " bytes of the message");
                    // We have either nothing left to write, or we wrote the packetLength,
                    // and have more to write.
                    remainingBytes = Math.Max(0, remainingBytes - packetLength);
                    if (remainingBytes == 0)
                    {
                        TransportLog("Finished writing to the output buffer for the message");
                        break;
                    }
                } while (true);
            }
            else
            {
                errorcode = EMPTY_STRING;
                TransportLog("Message string was empty");
            }
            TransportLog("Exiting sendMessageSync()");
            return errorcode;
        }

        /// <summary>
        /// Writes a packet to the connected USB device.
        /// </summary>
        /// <param name="outDataBuffer"></param>
        public void writePacket(BinaryWriter outDataBuffer)
        {
            if (!isConnected())
            {
                throw new IOException("USB accessory not connected");
            }

            try
            {
                //the last element that can be read or written.
                int outDataSize = (int)outDataBuffer.BaseStream.Position;
                if (outDataSize > getMaxDataTransferSize())
                {
                    throw new IOException("Out data too big, " + outDataSize + " bytes");
                }
                TransportLog("The outDataSize = " + outDataSize + " bytes.");
                BinaryWriter writePacketBuffer = new BinaryWriter(new MemoryStream(outDataSize + 2));
                // Write two bytes
                writePacketBuffer.Write(IPAddress.HostToNetworkOrder((short)outDataSize));
                writePacketBuffer.Write(((MemoryStream)outDataBuffer.BaseStream).ToArray());

                outDataBuffer.Seek(0, SeekOrigin.Begin);

                ErrorCode ecWrite = ErrorCode.None;
                int bytesWritten;

                if (null != writer && !writer.IsDisposed)
                {
                    ecWrite = writer.Write(((MemoryStream)writePacketBuffer.BaseStream).ToArray(), 2000, out bytesWritten);
                    if (ecWrite != ErrorCode.None) 
                    {
                        onDeviceError((int)ecWrite, "The Clover transport layer can see the USB device, but encountered an error when attempting to send it a message.  Try physically disconnecting/reconnecting the Clover device.");
	                    TransportLog("ErrorCode: " + ecWrite + "The Clover transport layer can see the USB device, but encountered an error when attempting to send it a message.  Try physically disconnecting/reconnecting the Clover device.");
	                } else
                    {
                        TransportLog("The UsbEndpointWriter just wrote " + bytesWritten + " bytes.");
                    }
                }
                else
                {
                    throw new Exception("Writer is null!");
                }
            }
            catch (Exception e)
            {
                throw new IOException("Error writing", e);
            }
        }

        public override void Dispose()
        {
            disconnect();
        }

        public void startListeningForMessages()
        {
            TransportLog("Starting a new receiveMessagesThread.");
            receiveMessagesThread = new BackgroundWorker();
            // what to do in the background thread
            receiveMessagesThread.DoWork += receiveMessagesDoWorkHandler;
            receiveMessagesThread.RunWorkerAsync();
        }

        /// <summary>
        /// This needs to be run in a thread.
        /// </summary>
        private void getMessages()
        {
            TransportLog("Thread Start: getMessages()");
            do
            {
                String message = receiveString();
                if (!shutdown)
                {
                    /*  This code is for debugging.  If needed, just uncomment the code
                    {
                        JObject obj = (JObject)JsonConvert.DeserializeObject(message);
                        JToken methodToken = obj.GetValue("method");
                        if(methodToken != null)
                        {
                            if ("UI_STATE".Equals(methodToken.ToString()))
                            {
                                string payloadStr = obj.GetValue("payload").ToString();
                                JObject payload = (JObject)JsonConvert.DeserializeObject(payloadStr);
                                TransportLog(methodToken.ToString() + " : " + payload.GetValue("uiDirection").ToString() + " : " + payload.GetValue("uiState").ToString() + " : " + payload.GetValue("uiText").ToString() + "   -> " + message);
                            }
                            else if ("TX_STATE".Equals(methodToken.ToString()))
                            {
                                string payloadStr = obj.GetValue("payload").ToString();
                                JObject payload = (JObject)JsonConvert.DeserializeObject(payloadStr);
                                TransportLog(methodToken.ToString() + " : " + payload.GetValue("txState").ToString() + "   -> " + message);
                            }
                            else
                            {
                                TransportLog(methodToken.ToString() + "   -> " + message);
                            }
                        }
                        else
                        {
                            TransportLog("Unknown: " + message);
                        }

                    }
                    catch(Exception e)
                    {
                        TransportLog("Got message: " + message);
                    }  */
                    // End of the debugging code block 
                    try
                    {
                        onMessage(message);
                    }
                    catch(Exception e)
                    {
                        TransportLog("Error parsing message: " + message);
                        TransportLog(e.Message);
                    }
                }
            } while (shutdown != true);
            TransportLog("Thread Exiting: getMessages()");
        }

        private string receiveString()
        {
            BinaryReader inputPacketBuffer = readPacket();

            uint startInt = (uint)IPAddress.NetworkToHostOrder(inputPacketBuffer.ReadInt32());
            if (startInt != REMOTE_STRING_MAGIC_START_TOKEN)
            {
                throw new IOException("Unexpected start token: " + String.Format("{0:x2}", startInt));
                // Integer.toHexString(startInt));
            }

            uint totalStringLength = (uint)IPAddress.NetworkToHostOrder(inputPacketBuffer.ReadInt32());
            if (totalStringLength <= 0 || totalStringLength > REMOTE_STRING_LENGTH_MAX)
            {
                throw new IOException("Illegal string length: " + totalStringLength + " bytes");
            }

            MemoryStream messageBuffer = new MemoryStream((int)totalStringLength);

            uint remainingBytes = totalStringLength;
            do
            {
                int packetLength = (int)(inputPacketBuffer.BaseStream.Length - inputPacketBuffer.BaseStream.Position);
                messageBuffer.Write(inputPacketBuffer.ReadBytes(packetLength), 0, packetLength);
                remainingBytes = (uint)Math.Max(0, remainingBytes - packetLength);
                if (remainingBytes == 0)
                {
                    break;
                }
                inputPacketBuffer = readPacket();
            } while (!shutdown);

            String returnString = null;
            if (!shutdown)
            {
                returnString = Encoding.UTF8.GetString(messageBuffer.ToArray());
            }
            TransportLog("Got Message: " + returnString);
            return returnString;
        }

        private BinaryReader readPacket()
        {
            if (!isConnected())
            {
                throw new IOException("USB accessory not connected");
            }
            int numBytesRead;
            byte[] readPacket = new byte[MAX_PACKET_BYTES];

            ErrorCode ecRead;
            BinaryReader output = null;
            do
            {
                ecRead = reader.Read(readPacket, 1000, out numBytesRead);
                //TransportLog("Read  :{0} ErrorCode:{1}", numBytesRead, ecRead);
                if (ecRead != ErrorCode.Success
                    &&
                    ecRead != ErrorCode.IoTimedOut
                    &&
                    ecRead != ErrorCode.IoCancelled)
                {
                    throw new IOException("Error reading USB message: " + ecRead.ToString());
                }
            }
            while (numBytesRead <= 0 && !shutdown);

            if (!shutdown)
            {

                if (numBytesRead < PACKET_HEADER_SIZE)
                {
                    throw new IOException("Read failed, " + numBytesRead + " bytes returned");
                }

                BinaryReader inPacketBuffer = new BinaryReader(new MemoryStream(readPacket));

                short inDataSize = IPAddress.NetworkToHostOrder(
                    inPacketBuffer.ReadInt16()
                    );
                if (inDataSize <= 0)
                {
                    throw new IOException("Invalid data size " + inDataSize + " bytes");
                }

                if (numBytesRead - PACKET_HEADER_SIZE < inDataSize)
                {
                    throw new IOException("Data size is " + inDataSize + " bytes but packet only contains " + numBytesRead + " bytes");
                }

                output = new BinaryReader(new MemoryStream(readPacket, PACKET_HEADER_SIZE, inDataSize));
            }
            return output;
        }

        public bool isConnected()
        {
            return (MyUsbDevice != null) && (MyUsbDevice.IsOpen);
        }

        private byte[] processOutputData(byte[] outputData)
        {
            System.Collections.ArrayList alist = new System.Collections.ArrayList();

            // Write two bytes for the size of the 'chunk'
            // I thought that the usb driver did this, but apparently not...
            short chunkLength = (short)(4 + 4 + outputData.Length);
            byte[] chunkArr = BitConverter.GetBytes(chunkLength);
            Array.Reverse(chunkArr); // Endian fun
            for (int i = 0; i < chunkArr.Length; i++)
            {
                alist.Add(chunkArr[i]);
            }

            // Write the four byte magic number
            byte[] mst = BitConverter.GetBytes(REMOTE_STRING_MAGIC_START_TOKEN);
            Array.Reverse(mst); // Endian fun
            for (int i = 0; i < mst.Length; i++)
            {
                alist.Add(mst[i]);
            }
            // Write the length of the string message
            byte[] outLen = BitConverter.GetBytes(outputData.Length);
            Array.Reverse(outLen);// Endian fun
            for (int i = 0; i < outLen.Length; i++)
            {
                alist.Add(outLen[i]);
            }
            // Write the message
            for (int i = 0; i < outputData.Length; i++)
            {
                alist.Add(outputData[i]);
            }
            byte[] returnVal = new byte[alist.Count];
            alist.CopyTo(returnVal);
            return returnVal;
        }

        ~USBCloverTransport()
        {
            disconnect();
        }

        public void disconnect()
        {
            lock(DeviceAccessorySyncLock)
            {
                onDeviceDisconnected();

                shutdown = true;
                lock (messageQueue)
                {
                    Monitor.PulseAll(messageQueue);
                }
                if (MyUsbDevice != null)
                {
                    UsbDevice TempUsbDevice = MyUsbDevice;
                    MyUsbDevice = null;
                    try
                    {
                        if (TempUsbDevice.IsOpen)
                        {
                            // If this is a "whole" usb device (libusb-win32, linux libusb-1.0)
                            // it exposes an IUsbDevice interface. If not (WinUSB) the 
                            // 'wholeUsbDevice' variable will be null indicating this is 
                            // an interface of a device; it does not require or support 
                            // configuration and interface selection.
                            IUsbDevice wholeUsbDevice = TempUsbDevice as IUsbDevice;
                            if (!ReferenceEquals(wholeUsbDevice, null))
                            {
                                // Release interface #0.
                                wholeUsbDevice.ReleaseInterface(0);
                            }

                        }
                        TempUsbDevice.Close();
                    }
                    finally
                    {
                        // Free usb resources
                        if (reader != null && !reader.IsDisposed)
                        {
                            reader.Dispose();
                        }
                        if (writer != null && !reader.IsDisposed)
                        {
                            writer.Dispose();
                        }
                        UsbDevice.Exit();
                    }
                }
            }
        }

        private void listenForUSB()
        {
            listenForUSBInserts();
            listenForUSBRemovals();
        }

        private void listenForUSBInserts()
        {
            //create a query to look for usb devices
            WqlEventQuery w = new WqlEventQuery();
            w.EventClassName = "__InstanceCreationEvent";
            w.Condition = "TargetInstance ISA 'Win32_USBControllerDevice'";
            w.WithinInterval = new TimeSpan(0, 0, 2);

            //use a "watcher", to run the query
            ManagementEventWatcher watch = new ManagementEventWatcher(w);
            watch.EventArrived += new EventArrivedEventHandler(DeviceInsertedEvent);
            watch.Start();
        }
        private void listenForUSBRemovals()
        {
            //create a query to look for usb devices
            WqlEventQuery w = new WqlEventQuery();
            w.EventClassName = "__InstanceDeletionEvent";
            w.Condition = "TargetInstance ISA 'Win32_USBControllerDevice'";
            w.WithinInterval = new TimeSpan(0, 0, 2);

            //use a "watcher", to run the query
            ManagementEventWatcher watch = new ManagementEventWatcher(w);
            watch.EventArrived += new
            EventArrivedEventHandler(DeviceRemovedEvent);
            watch.Start();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void DeviceEvent(object sender, EventArrivedEventArgs e, bool inserted)
        {
            TransportLog("DeviceEvent: " + Thread.CurrentThread.ManagedThreadId);
            ManagementBaseObject instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            instance.GetPropertyValue("Dependent");
            string value = instance.GetPropertyValue("Dependent").ToString();
            string upperValue = value.ToUpper();
            TransportLog("Device was " + (inserted ? "inserted" : "removed") + ", device id:" + value);

            bool found = false;
            foreach(UsbDeviceFinder customerFinder in CustomerUsbFinders)
            {
                int customerVid = customerFinder.Vid;
                int customerPid = customerFinder.Pid;

                if (upperValue.Contains("VID_" + String.Format("{0:x}", customerVid).ToUpper()))
                {
                    if(upperValue.Contains("PID_" + String.Format("{0:x}", customerPid).ToUpper()))
                    {
                        found = true;
                        if (inserted)
                        {
                            DeviceSetToAccessoryMode();
                        }
                        else
                        {
                            disconnect();
                        }
                    }
                }
            }
            if(!found)
            {
                foreach (UsbDeviceFinder merchantFinder in MerchantUsbFinders)
                {
                    int merchantVid = merchantFinder.Vid;
                    int merchantPid = merchantFinder.Pid;

                    if (upperValue.Contains("VID_" + String.Format("{0:x}", merchantVid).ToUpper()))
                    {
                        if (upperValue.Contains("PID_" + String.Format("{0:x}", merchantPid).ToUpper()))
                        {
                            found = true;
                            if (inserted)
                            {
                                //Console.WriteLine("Merchant device inserted.");
                                DeviceInitiallyConnected();
                            }
                            else
                            {
                                //don't call disconnect for merchant disconnect, only customer disconnect
                                // because we don't keep a reference to the merchant device as it is only
                                // used long enough to flip the device
                            }
                        }
                    }
                }
            }

        }

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
        {
            //TransportLog("Device Inserted");
            DeviceEvent(sender, e, true);
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e)
        {
            //TransportLog("Device Removed");
            DeviceEvent(sender, e, false);
        }

        protected override void onDeviceDisconnected()
        {
            TransportLog("USBTransport:onDeviceDisconnected - shutdown = true");
            shutdown = true;
            if (receiveMessagesThread != null)
            {
                TransportLog(receiveMessagesThread.ToString() + " : receiveMessagesThread removing DoWorkHandler");
                receiveMessagesThread.DoWork -= receiveMessagesDoWorkHandler;
            }
            if (sendMessagesThread != null)
            {
                TransportLog(sendMessagesThread.ToString() + " : sendMessagesThread removing DoWorkHandler");
                sendMessagesThread.DoWork -= sendMessagesDoWorkHandler;
            }
            base.onDeviceDisconnected();
        }
    }

    class BlockingQueue<T>
    {
        private readonly Queue<T> queue = new Queue<T>();

        public void Enqueue(T item)
        {
            lock(this)
            {
                queue.Enqueue(item);
                Monitor.PulseAll(this);
            }
        }

        /// <summary>
        /// Can return null if the thread is notified/pulsed or wait timeout exires
        /// </summary>
        /// <returns></returns>
        public T Dequeue()
        {
            lock(this)
            {
                return queue.Dequeue();
            }
        }

        public int Count
        {
            get
            {
                lock(this)
                {
                    return queue.Count;
                }
            }
        }

        ~BlockingQueue()
        {
        }
    }

    public class USBDevice
    {
        public int VID { get; set; }
        public int PID { get; set; }
    }
}
