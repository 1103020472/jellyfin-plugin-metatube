using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

public class ActorInfo : ActorSearchResult
{
    [JsonPropertyName("aliases")]
    public string[] Aliases { get; set; }

    [JsonPropertyName("birthday")]
    public DateTime Birthday { get; set; }

    [JsonPropertyName("blood_type")]
    public string BloodType { get; set; }

    [JsonPropertyName("cup_size")]
    public string CupSize { get; set; }
    
    // lj-新增字段，推特、ins、标签
    [JsonPropertyName("twitter")]
    public string Twitter { get; set; }
    
    [JsonPropertyName("instagram")]
    public string Instagram { get; set; }
    
    [JsonPropertyName("tags")]
    public string[] Tags { get; set; }

    [JsonPropertyName("debut_date")]
    public DateTime DebutDate { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("hobby")]
    public string Hobby { get; set; }

    [JsonPropertyName("skill")]
    public string Skill { get; set; }

    [JsonPropertyName("measurements")]
    public string Measurements { get; set; }

    [JsonPropertyName("nationality")]
    public string Nationality { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }
}