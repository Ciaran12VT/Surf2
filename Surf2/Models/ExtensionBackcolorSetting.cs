using System.Text.Json.Serialization;

namespace Surf2.Models;

public sealed class ExtensionBackcolorSetting
{
    public string Extension { get; set; } = string.Empty;

    public string Backcolor { get; set; } = "#FFFFFF";

    public string Language { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayLabel => $"{Extension}  {Backcolor}  {Language}";
}
