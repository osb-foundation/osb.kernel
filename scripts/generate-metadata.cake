// OSB 1.1 (Olorun): deterministic version-metadata generator.
//
// Reads the five official component version.json files and emits a single
// generated-metadata.json consumed by the shell's VER command. The output is
// a deterministic, line-oriented artifact (one record per component):
//
//     <component>:<version>:<generation>:<codename>
//
// The generator is strict: any missing file, malformed JSON, missing/invalid
// field, non-SemVer version, unknown generation or codename inconsistency is
// a hard error. On any error nothing is written and the process exits non-zero.
//
// Invocation (Cake 7 / Cake.Tool 7.0.0):
//
//     dotnet cake scripts/generate-metadata.cake --root=<repo-parent> --output=<file>
//
//   --root    Absolute path to the directory that contains the five component
//             repositories side by side (osb.kernel, osb.shell, osb.core,
//             osbkt, osb.oslang). Required. CWD-independent.
//   --output  Absolute path of the generated artifact to write. Required.
//
// The component order is fixed, so the output is byte-for-byte reproducible.

#addin nuget:?package=Newtonsoft.Json&version=13.0.3

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

var root = Argument<string>("root", null);
var output = Argument<string>("output", null);

if (string.IsNullOrWhiteSpace(root))
{
    Error("missing required --root (absolute path to the directory holding the component repos)");
    System.Environment.Exit(1); return;
}
if (string.IsNullOrWhiteSpace(output))
{
    Error("missing required --output (absolute path of the generated artifact)");
    System.Environment.Exit(1); return;
}

if (!System.IO.Directory.Exists(root))
{
    Error($"--root directory does not exist: {root}");
    System.Environment.Exit(1); return;
}

// Fixed component order -> deterministic output.
var components = new[] { "osb.kernel", "osb.shell", "osb.core", "osbkt", "osb.oslang" };

// Generation codename registry (single source of truth for gen->codename).
var registry = new Dictionary<int, string> { { 0, "Olorun" } };

var semver = new Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*))?(?:\+([0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*))?$");

var errors = new List<string>();
var lines = new List<string>();

foreach (var comp in components)
{
    var vjPath = System.IO.Path.Combine(root, comp, "version.json");
    if (!System.IO.File.Exists(vjPath))
    {
        errors.Add($"[{comp}] missing version.json at {vjPath}");
        continue;
    }

    JObject j;
    try
    {
        j = JObject.Parse(System.IO.File.ReadAllText(vjPath));
    }
    catch (Exception ex)
    {
        errors.Add($"[{comp}] invalid JSON in {vjPath}: {ex.Message}");
        continue;
    }

    var fileComponent = j["component"]?.Value<string>();
    var version = j["version"]?.Value<string>();
    var generation = j["generation"]?.Value<int?>();
    var codename = j["codename"]?.Type == JTokenType.Null ? null : j["codename"]?.Value<string>();

    if (string.IsNullOrWhiteSpace(fileComponent))
        errors.Add($"[{comp}] missing required field 'component'");
    else if (fileComponent != comp)
        errors.Add($"[{comp}] field 'component' is '{fileComponent}', expected '{comp}'");

    if (string.IsNullOrWhiteSpace(version))
        errors.Add($"[{comp}] missing required field 'version'");
    else if (!semver.IsMatch(version))
        errors.Add($"[{comp}] version '{version}' is not valid SemVer");

    if (!generation.HasValue)
        errors.Add($"[{comp}] missing required field 'generation'");
    else if (!registry.ContainsKey(generation.Value))
        errors.Add($"[{comp}] generation {generation.Value} has no registered codename");

    if (codename == null)
        errors.Add($"[{comp}] missing required field 'codename'");

    if (generation.HasValue && registry.ContainsKey(generation.Value))
    {
        var expected = registry[generation.Value];
        if (codename != null && codename != expected)
            errors.Add($"[{comp}] codename '{codename}' != registered codename '{expected}' for generation {generation.Value}");
    }

    if (errors.Count == 0 || !errors.Exists(e => e.StartsWith($"[{comp}]")))
        lines.Add($"{comp}:{version}:{generation}:{codename}");
}

if (errors.Count > 0)
{
    Error($"metadata generation failed with {errors.Count} error(s):");
    foreach (var e in errors) Error($"  - {e}");
    System.Environment.Exit(1); return;
}

var outDir = System.IO.Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(outDir) && !System.IO.Directory.Exists(outDir))
    System.IO.Directory.CreateDirectory(outDir);

System.IO.File.WriteAllText(output, string.Join("\n", lines) + "\n");
Information($"Generated metadata for {lines.Count} components at {output}");
return 0;