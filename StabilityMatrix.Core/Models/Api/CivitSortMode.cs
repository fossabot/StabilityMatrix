﻿using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace StabilityMatrix.Core.Models.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CivitSortMode
{
    [EnumMember(Value = "Highest Rated")]
    HighestRated,
    [EnumMember(Value = "Most Downloaded")]
    MostDownloaded,
    [EnumMember(Value = "Newest")]
    Newest,
    [EnumMember(Value = "Installed")]
    Installed,
}
