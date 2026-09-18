using System;
using System.IO;
using System.Text.Json;
using Toolbox.Tools.Store;

var m = ToolStore.BuiltInManifest();
m.GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

var opts = new JsonSerializerOptions
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

var json = JsonSerializer.Serialize(m, opts);
var outPath = args.Length > 0 ? args[0] : "tools.json";
File.WriteAllText(outPath, json, new System.Text.UTF8Encoding(false));
Console.WriteLine($"已写出 {outPath}（{m.Tools.Count} 个条目）");
