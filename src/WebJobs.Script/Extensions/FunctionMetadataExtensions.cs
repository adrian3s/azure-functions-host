// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.WebJobs.Script.Abstractions.Description;
using Microsoft.Azure.WebJobs.Script.Description;

namespace Microsoft.Azure.WebJobs.Script.Extensions
{
    public static class FunctionMetadataExtensions
    {
        public static bool IsCodeless(this FunctionMetadata functionMetadata) =>
            string.Equals(functionMetadata.Language, DotNetScriptTypes.Codeless, StringComparison.OrdinalIgnoreCase);
    }
}
