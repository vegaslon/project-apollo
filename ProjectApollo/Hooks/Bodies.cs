//   Copyright 2020 Vircadia
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.

using System;
using System.Collections.Generic;
using System.Text;

using Newtonsoft.Json;

namespace Project_Apollo.Hooks
{
    public class Bodies
    {
    }

    /// <summary>
    /// All the Metaverse responses are a JSON string with two top
    /// level fields of "status" and "data".
    /// Wrap some of the manipulations of this structure.
    /// </summary>
    public class ResponseBody
    {
        public string Status;
        public object Data;

        public ResponseBody()
        {
            Status = "success";
        }
        public ResponseBody(object pDataContents) {
            Data = pDataContents;
        }

        public ResponseBody(string pStatus, object pDataContents) {
            Status = pStatus;
            Data = pDataContents;
        }

        public static implicit operator string(ResponseBody rb) => rb.ToString();
        public override string ToString()
        {
            Dictionary<string, object> respBody = new Dictionary<string, object>();
            respBody.Add("status", "success");
            respBody.Add("data", Data);
            return JsonConvert.SerializeObject(respBody);
        }
    }
}