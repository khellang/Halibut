// Copyright 2012-2013 Octopus Deploy Pty. Ltd.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//    http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;

namespace Halibut.Protocol
{
    public static class DefaultJsonSerializer
    {
        public static Func<JsonSerializer> Factory = CreateDefault;

        static JsonSerializer CreateDefault()
        {
            var settings = new JsonSerializerSettings();
            settings.Formatting = Formatting.None;

            JsonSerializer serializer = JsonSerializer.Create(settings);
            serializer.TypeNameHandling = TypeNameHandling.All;
            serializer.TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple;
            return serializer;
        }
    }
}