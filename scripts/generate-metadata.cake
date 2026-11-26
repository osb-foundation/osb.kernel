// OSB 1.1: deterministic metadata generator for version.json files.
// Reads official component metadata and emits generated-metadata.json consumed by the shell.
using System;
using System.IO;
using Newtonsoft.Json.Linq;

class Program {
    static void Main(string[] args) {
        string root = args[0];
        string out = args[1];
        if (!string.IsNullOrWhiteSpace(root)) root = Path.Combine(root, "..", "..");
        var entries = new List<string>();
        foreach (var comp in new[] { "osb.kernel", "osb.shell", "osb.core", "osbkt", "osb.oslang" }) {
            var vjPath = Path.Combine(root, comp, "version.json");
            if (!File.Exists(vjPath)) {
                entries.Add($"{comp},missing");
                continue;
            }
            try {
                JObject j = JObject.Parse(File.ReadAllText(vjPath));
                entries.Add($"{comp}:{j["version"]}:{j["generation"]}:{j["codename"]}");
            } catch (Exception ex) {
                entries.Add($"{comp} invalid JSON: {ex.Message}");
            }
        }
        File.WriteAllLines(out, entries);
    }
}
