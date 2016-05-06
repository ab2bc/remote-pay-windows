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

using System;
using System.Collections.Generic;
using System.Text;

namespace com.clover.remotepay.transport
{
    public class USBCloverDeviceConfiguration : CloverDeviceConfiguration
    {
        string deviceId;
        bool enableLogging = false;
        int pingSleepSeconds = 1;
        string remoteApplicationID;

        public USBCloverDeviceConfiguration(string deviceId)
        {
            this.deviceId = deviceId;
        }
        public USBCloverDeviceConfiguration(string deviceId, string remoteApplicationID, bool enableLogging, int pingSleepSeconds)
        {
            this.deviceId = deviceId;
            this.remoteApplicationID = remoteApplicationID;
            this.enableLogging = enableLogging;
            this.pingSleepSeconds = pingSleepSeconds;
        }
        public string getCloverDeviceTypeName()
        {
            return typeof(DefaultCloverDevice).AssemblyQualifiedName;
        }

        public CloverTransport getCloverTransport()
        {
            return new USBCloverTransport(this.deviceId, enableLogging, pingSleepSeconds);
        }

        public bool getEnableLogging()
        {
            return enableLogging;
        }

        public int getPingSleepSeconds()
        {
            return pingSleepSeconds;
        }

        public string getMessagePackageName()
        {
            return "com.clover.remote.protocol.usb";
        }

        public string getName()
        {
            return "Clover via USB";
        }

        public string getRemoteApplicationID()
        {
            return remoteApplicationID;
        }
    }
}