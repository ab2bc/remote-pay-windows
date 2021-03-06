/**
 * Autogenerated by Avro
 * 
 * DO NOT EDIT DIRECTLY
 */

// Copyright (C) 2016 Clover Network, Inc.
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

namespace com.clover.sdk.v3.order {


/// <summary>
/// Snapshot of a line item modifier at the time that the order was placed.
/// </summary>
public class Modification {

  public String id { get; set; }

  /// <summary>
  /// The line item with which the modification is associated
  /// </summary>
  public com.clover.sdk.v3.base_.Reference lineItemRef { get; set; }

  public String name { get; set; }

  public String alternateName { get; set; }

  public Int64 amount { get; set; }

  /// <summary>
  /// The modifier object.  Values from the Modifier are copied to the Modification at the time that the order is placed.  Modifier values may change after the order is placed.
  /// </summary>
  public com.clover.sdk.v3.inventory.Modifier modifier { get; set; }

}

}
